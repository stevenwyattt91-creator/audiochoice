# Admin-only Lambda backend rescan

Use this procedure to run an updated filter pipeline against an audiobook whose
transcript is already complete. It does not upload or transcribe the audio again,
and it is intentionally unavailable to app users.

## One-command rescan

On the Lambda host, update and recreate the scanner first:

```bash
cd /lambda/nfs/audiochoice-worker-state/audiochoice-source-updated
git pull --ff-only origin main
sudo docker compose -f deploy/lambda/docker-compose.yml build scanner
sudo docker compose -f deploy/lambda/docker-compose.yml up -d --force-recreate scanner
until curl -s http://127.0.0.1:8080/health >/dev/null; do sleep 2; done
```

Then provide enough of the audiobook title to identify it:

```bash
python3 scripts/reanalyze-lambda-transcript.py "King Sparrow"
```

The helper reads the private admin token from `deploy/lambda/lambda-worker.env`,
selects the newest complete matching transcript, submits it to the
`ios-beta-lambda` lane, and prints progress until completion. It requires only
Python 3; `jq` is not required.

## Initial private-token setup

The scanner's untracked `deploy/lambda/lambda-worker.env` must contain:

```text
AudioChoice__ApiToken=<a private random value>
```

Generate it on the Lambda host without printing it or committing it:

```bash
scan_admin_token="$(openssl rand -hex 32)"
printf '\nAudioChoice__ApiToken=%s\n' "$scan_admin_token" >> deploy/lambda/lambda-worker.env
unset scan_admin_token
sudo docker compose -f deploy/lambda/docker-compose.yml up -d --force-recreate scanner
```

Never put this token in GitHub, screenshots, logs, or chat.

## Raw progress and troubleshooting

The helper reports overall percentage and stage. For detailed provider messages:

```bash
sudo docker compose -f deploy/lambda/docker-compose.yml logs -f --since=5m scanner whisper
```

Common issues:

- `401 Unauthorized`: `AudioChoice__ApiToken` is missing from the scanner environment,
  or the container was not recreated after it was added.
- `Connection refused`: the scanner is still starting. Wait for `/health` before retrying.
- Reimport immediately restores the old result: completed results are cached by the exact
  audiobook fingerprint. Use this admin reanalysis procedure instead.
- A reanalysis shows zero transcription chunks: expected. It is reusing the saved transcript.

The underlying private endpoints are `GET /v1/admin/transcripts`,
`POST /v1/admin/scans/reanalysis`, and `GET /v1/admin/scans/jobs/{scanID}`.
