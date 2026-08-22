# AudioChoice Azure staging foundation

This folder defines the first private Azure staging resources. It deliberately does
not deploy a production database, paid scan worker, or OpenAI secret yet.

`bootstrap.bicep` creates:

- a Container Apps environment;
- a Basic Azure Container Registry;
- a private Standard LRS storage account with a capped 20 GiB staging file share;
- a Key Vault with purge protection;
- a Log Analytics workspace with 30-day retention.

The API container defaults to `AudioChoice__OpenAI__WorkerEnabled=false`. Paid
processing must be enabled explicitly only after the health-only deployment is
working and cost controls have been reviewed.

The staging file share supports one API/worker replica during the first controlled
tests. It is not the production persistence design. PostgreSQL, direct Blob uploads,
and a durable queue will replace file-backed accounts, catalog state, and job state
before public beta.

The production database schema is defined in
`backend/Database/Migrations/001_initial.sql`. PostgreSQL is not provisioned by the
current foundation template yet, so applying the schema or switching persistence
requires a separate reviewed deployment. This prevents an accidental bill or a
partial migration of the working staging API.

`database.bicep` is the separately reviewed database deployment. It selects the
smallest intended staging configuration in West US 3: PostgreSQL 16, Burstable `Standard_B1ms`,
32 GiB storage, seven-day local backups, and no high-availability replica. The
administrator password is a secure deployment parameter and must never be written
to a parameters file or shell history. The template currently permits connections
from Azure-hosted services so the existing Container Apps environment can connect;
private networking is required before public beta.

The staging API reads both `postgres-connection-string` and `staging-api-token`
directly from Key Vault through its managed identity. Neither secret is copied into
Bicep parameters, source control, or mobile configuration. The first database-enabled revision runs
the ordered migrations at startup; migration names are recorded so subsequent
restarts do not reapply them. Paid OpenAI processing remains forced off.

`api.bicep` deploys the private staging API from an image already built in the
private registry. It uses a managed identity for registry pulls, mounts the staging
file share at `/data`, caps uploads at 250 MiB, scales from zero to at most one
replica, and forces paid OpenAI processing off.

Transactional email also defaults to disabled. The API contains email-verification
and password-reset flows, but Resend must not be enabled until its API key is stored
as an Azure secret and the `app.audiochoiceapp.com` verification/reset pages exist.
Account action tokens are stored only as SHA-256 hashes and expire after use or at
their configured deadline.

## Local container build

Run from the repository root:

```sh
docker build --file backend/Dockerfile --tag audiochoice-api:staging .
```

No API keys or credentials belong in the image, source tree, parameters file, or
mobile application.

`worker.bicep` deploys paid processing separately from the public API. Its first-test
defaults permit one five-minute chunk, disable paid-request retries, and keep exactly
one private worker replica alive. Deploy it only after `openai-api-key` exists in Key
Vault. After a controlled test, set the worker's minimum replicas to zero until the
next test; the public API always keeps `WorkerEnabled=false`.
