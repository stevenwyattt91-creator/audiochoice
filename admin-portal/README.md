# AudioChoice Admin Portal

Separate internal workspace for AudioChoice administrators. It uses the existing
AudioChoice account service and accepts only accounts granted the `admin` internal
role by the backend.

## Included operations

- Browse and search scanned audiobook editions.
- Open or download generated filter results and generated transcripts.
- Create, approve, or reject auditor tasks.
- See approved compensation, group unpaid work by auditor, and record payments.
- Prepare for the subscription-verified affiliate ledger.

The portal intentionally has no endpoint for source audiobook uploads or original
customer audiobook files.

## Local preview

```sh
npm install
npm run dev
```

The API base defaults to the staging API. Set `NEXT_PUBLIC_AUDIOCHOICE_API_URL`
only when a different environment is intentionally needed.
