# Azure to AWS cutover runbook

Everything in this repository is ready. What remains needs credentials or a Mac, so it needs you.

Read phase 0 first. One item in it is time-critical and cannot be recovered if it lapses.

---

## Phase 0. Protect the data, before anything else

Azure disables resources on non-payment and deletes them after a grace period. Three things exist
only inside that subscription right now:

| What | Where | If it is lost |
|---|---|---|
| Accounts, entitlements, libraries, playback positions, scan results, Android bookmarks | PostgreSQL `audiochoice-stg-db-*` | Everyone's account and progress, permanently |
| `edition-signatures.json` | the `/data` file share | Edition matching degrades until every client happens to re-report. No table holds this and nothing regenerates it |
| Read-along transcripts, auditor review media | blob containers `private-transcripts`, `audit-review-media` | Read-along stops working for scanned books; paid audit work is unrecoverable |

**Do this first, today.** If the subscription is already disabled you may have to settle the bill
purely to regain read access long enough to export. That is far cheaper than the alternative.

```bash
# The database. Everything else can be rebuilt with effort; this cannot.
PGPASSWORD='...' pg_dump \
  --host=audiochoice-stg-db-XXXX.postgres.database.azure.com \
  --username=audiochoice_admin --dbname=audiochoice \
  --format=custom --no-owner --no-privileges \
  --file=audiochoice-$(date -u +%Y%m%dT%H%M%SZ).dump
```

Keep that file somewhere that is not Azure and not this laptop only.

---

## Phase 1. AWS account preparation

You need these before any stack deploys.

**1a. An OIDC provider and a deploy role.** So the pipeline holds no long-lived key.

```bash
aws iam create-open-id-connect-provider \
  --url https://token.actions.githubusercontent.com \
  --client-id-list sts.amazonaws.com \
  --thumbprint-list 6938fd4d98bab03faadb97b34396831e3780aea1
```

Then a role trusting only this repository. Scope the `sub` condition to the repo, not to `*`:

```json
{
  "Version": "2012-10-17",
  "Statement": [{
    "Effect": "Allow",
    "Principal": { "Federated": "arn:aws:iam::ACCOUNT:oidc-provider/token.actions.githubusercontent.com" },
    "Action": "sts:AssumeRoleWithWebIdentity",
    "Condition": {
      "StringEquals": { "token.actions.githubusercontent.com:aud": "sts.amazonaws.com" },
      "StringLike": { "token.actions.githubusercontent.com:sub": "repo:stevenwyattt91-creator/audiochoice:*" }
    }
  }]
}
```

The role needs CloudFormation, ECS, ECR, IAM (it creates named roles), EC2, ELB, RDS, EFS, S3, SSM
and Logs permissions. `PowerUserAccess` plus `IAMFullAccess` will do it to start; tighten later.

**1b. An ACM certificate for `api.audiochoiceapp.com`**, in the same region as the stacks
(`us-east-1` unless you change it).

```bash
aws acm request-certificate \
  --domain-name api.audiochoiceapp.com \
  --validation-method DNS \
  --region us-east-1
aws acm describe-certificate --certificate-arn <arn> --region us-east-1 \
  --query 'Certificate.DomainValidationOptions[0].ResourceRecord'
```

Add the returned CNAME in whichever DNS hosts `audiochoiceapp.com` — Cloudflare, most likely, given
the website. Wait for `Status: ISSUED`.

This is a parameter rather than something the stack creates, deliberately: a certificate created
inside a stack holds the whole deployment in `CREATE_IN_PROGRESS` until its validation record
appears, and this domain's DNS is not in Route 53.

**1c. Repository secrets.**

| Secret | Notes |
|---|---|
| `AWS_DEPLOY_ROLE_ARN` | From 1a |
| `AWS_CERTIFICATE_ARN` | From 1b |
| `AWS_DATABASE_PASSWORD` | New RDS master password, 16+ characters |
| `APPLE_CLIENT_SECRET` | Required. The workflow refuses to deploy without it, because Apple sign-in is a required alternative wherever Google sign-in is offered |
| `RESEND_API_KEY` | Optional. Without it password reset cannot send |
| `APNS_AUTH_KEY`, `APNS_KEY_ID` | Optional, both needed together or push stays off |
| `GOOGLE_PURCHASES_SERVICE_ACCOUNT` | Optional |

---

## Phase 2. Deploy the foundation

```
Actions -> "Deploy AudioChoice backend (AWS)" -> Run workflow
          -> Deploy foundation stack: enabled
```

Creates the VPC, ECR repository, S3 bucket, EFS filesystem and RDS instance. Ten to fifteen minutes,
mostly RDS.

Note the outputs — you need three of them for phase 3:

```bash
aws cloudformation describe-stacks --stack-name audiochoice-foundation \
  --query 'Stacks[0].Outputs' --output table
```

The same run then builds the image, writes the secrets and deploys the API. It will fail the final
verification step, because DNS does not point anywhere yet. That is expected.

---

## Phase 3. Move the data

Requires `pg_dump`/`pg_restore` 16.x, `psql`, `rclone`, `azcopy` and the AWS CLI.

```bash
export AZURE_PG_HOST=audiochoice-stg-db-XXXX.postgres.database.azure.com
export AZURE_PG_PASSWORD='...'
export AZURE_STORAGE_ACCOUNT=audiochoicestgXXXXXXXX
export AZURE_STORAGE_KEY='...'

export RDS_HOST=<DatabaseAddress output>
export RDS_PASSWORD='<AWS_DATABASE_PASSWORD>'
export S3_BUCKET=<BucketName output>
export AWS_REGION=us-east-1

# azcopy cannot use an account key for Azure Files, only a SAS.
export AZCOPY_SAS=$(az storage share generate-sas \
  --account-name "$AZURE_STORAGE_ACCOUNT" --account-key "$AZURE_STORAGE_KEY" \
  --name audiochoice-staging-data --permissions rl \
  --expiry "$(date -u -d '+2 hours' +%Y-%m-%dT%H:%MZ)" --output tsv)

./deploy/aws/migrate-data.sh
./deploy/aws/verify-migration.sh
```

**Do not continue unless `verify-migration.sh` exits clean.** It checks the thing that would
otherwise break silently: `user_library_books.id` values are stored on every iPhone as
`accountLibraryID` and are the only link between a device's library and its account rows. If those
differ, every install keeps working and quietly stops syncing.

Then restart the API once so it picks up the restored database and seeds `edition-signatures.json`
onto the EFS volume from the bucket:

```bash
aws ecs update-service --cluster audiochoice-cluster \
  --service audiochoice-api --force-new-deployment
```

---

## Phase 4. Point DNS at the load balancer

```bash
aws cloudformation describe-stacks --stack-name audiochoice-api \
  --query "Stacks[0].Outputs[?OutputKey=='LoadBalancerHostname'].OutputValue" --output text
```

Create `api.audiochoiceapp.com` as a CNAME to that value. If it is in Cloudflare, set it to
**DNS only** (grey cloud) rather than proxied — the audio upload path moves multi-gigabyte files and
Cloudflare's proxy has a request size limit that would reject them.

Confirm end to end:

```bash
curl -i https://api.audiochoiceapp.com/v1/faq       # expect 200
curl -i https://api.audiochoiceapp.com/health        # expect 200
```

Then re-run the workflow so the verification step passes and you have a green deploy on record.

---

## Phase 5. Ship the clients

Every installed build still points at the dead Azure hostname and cannot be rescued by DNS. This is
the step that actually restores service for existing users.

**iOS 1.0.6, build 37.** Already prepared on this branch: the Sign in with Apple fix, the offline
access fix, and the new base URL. On a Mac:

```bash
git checkout aws/migration
cd ios-app
# Confirm in the simulator BEFORE archiving:
#  - Sign in with Apple opens the sheet
#  - tapping empty space still dismisses the keyboard
#  - the app opens straight to the library when already signed in
```

Then Product > Archive, Distribute App > App Store Connect, attach build 37 to the 1.0.6 version,
and submit. "What's New" can be short: fixes Sign in with Apple, and keeps your library available
when the connection drops.

**Android beta.** `local.properties` no longer needs `audiochoice.apiBaseUrl` — production is the
default now. Build the beta variant with the real beta keystore, not debug signing: a signature
change forces testers to uninstall, which discards their local library mappings and reading
positions, and those are not recoverable from the server.

**Portals and website.** Redeploy each so the new default compiles in, or set
`NEXT_PUBLIC_AUDIOCHOICE_API_URL` in their hosting configuration.

---

## Phase 6. Decommission

Only after the AWS deployment has served real traffic for a few days.

1. Delete `.github/workflows/deploy-backend.yml` and `deploy/azure/`.
2. Delete the `Azure.Identity` and `Azure.Storage.Blobs` packages and the `Blob*` storage classes.
   Until then they are the rollback: setting `S3Enabled=false` and `BlobEnabled=true` returns the
   API to Azure storage with no code change.
3. Keep the database dump from phase 0 regardless. It is the only rollback that does not depend on
   the Azure subscription still existing.
4. Cancel Azure.

---

## What this migration does not touch

Worth knowing so you do not go looking for problems that are not there.

**iOS bookmarks, favourites and collections were never on the server.** They live in `UserDefaults`
on each device. They are unaffected, and they were never backed up either — that is pre-existing and
separate from this move.

**Android bookmarks do sync**, to the `bookmarks` table, and are covered by the row-count check.

**Parental PINs are in the iOS Keychain** and are not server-side.

**The GPU scanning host stays on Lambda Labs.** Nothing here changes it. It reaches the API over the
public internet and will pick up the new hostname from its own configuration.

## Known gaps carried into the new deployment

**Uploads over 5 GiB will fail.** S3 caps a single PUT at 5 GiB, `MaximumUploadBytes` allows 20 GiB,
and the shipped clients cannot do multipart. The Azure path chunked and this one does not. It fails
as a clean rejection rather than corruption, and only for very large audiobooks, but it is a real
regression that needs multipart support and a client update to close.

**Presigned upload URLs can expire early.** They are signed with the task role's temporary
credentials and die with them, regardless of the 120-minute window the response advertises. The
client's remedy is unchanged: request another authorization.

**Deploys drop traffic briefly.** The service stops the old task before starting the new one, on
purpose — the startup path applies 29 SQL migrations and two tasks racing that is worse than a few
seconds of downtime. The pinned single Azure revision behaved the same way.
