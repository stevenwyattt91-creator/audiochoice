# AudioChoice private backend

Stage 11 provides the first private ASP.NET Core API matching the Apple client contracts.

## Endpoints

- `GET /health`
- `POST /v1/scans/requests`
- `POST /v1/uploads/authorizations`
- `PUT /v1/uploads/{uploadID}`
- `POST /v1/scans/jobs`
- `GET /v1/scans/jobs/{scanID}`

API endpoints require `Authorization: Bearer <token>`. Configure the token with the
`AudioChoice__ApiToken` environment variable. Authorized upload URLs use a separate,
short-lived `X-AudioChoice-Upload-Token` header.

`AudioChoice__MaximumUploadBytes` controls the maximum authorized audiobook size
and defaults to 20 GiB. Uploads are streamed to disk and verified against both the
authorized byte count and SHA-256 fingerprint before they can create scan jobs.

Uploaded audio is private and stored under `App_Data/uploads`. The API never returns
audio or transcript content. Stage 11 intentionally leaves new scan jobs queued; a
later private worker will transcribe and classify them.

`AudioChoice__DataPath` can move the complete private data root outside the
application folder. It defaults to `App_Data` for the existing Mac workflow and is
set to `/data` by the container image so staging persistence can be mounted without
changing any API contract.

## Stage 12 worker foundation

The processing layer now defines:

- a deduplicating scan-job queue;
- queued, processing, completed, and failed lifecycle transitions;
- provider boundaries for audio chunking, transcription, and content analysis;
- private transcript storage under `App_Data/transcripts`;
- a coordinator pipeline that offsets chunk timestamps and stores transcripts before
  classification;
- a background worker that never returns transcripts through the public API.

The worker is disabled by default. Until it is explicitly enabled, submitted jobs
remain safely queued and cannot consume API spend.

### FFmpeg chunking

`FfmpegAudioChunker` creates mono 16 kHz WAV chunks sequentially, with a configurable
overlap that preserves context at chunk boundaries. It probes the source duration,
records each chunk's absolute start/end time, uses argument-safe process invocation,
supports cancellation, and deletes temporary audio immediately after consumption.

FFmpeg and FFprobe must be installed on the worker host. Their executable paths and
chunk settings live under `AudioChoice:Ffmpeg` in `appsettings.json`. They are not
installed on the current development Mac, so the real binary integration has not yet
been executed here.

### Paid processing activation

Stage 12 includes retrying OpenAI transcription and structured content-analysis
providers. The default transcription model is `whisper-1` because this implementation
requires segment timestamps; the default analysis model is the cost-oriented
`gpt-5.6-luna`. Model names remain configurable for evaluation and future upgrades.

To activate processing, install FFmpeg and .NET 8+, then set secrets through the
environment rather than `appsettings.json`:

```sh
export AudioChoice__OpenAI__ApiKey="..."
export AudioChoice__OpenAI__WorkerEnabled="true"
```

The worker stores transcripts privately, publishes only scan events, caches completed
results by fingerprint, and deletes uploaded audio after successful processing.

## Stage 20 authentication

`/v1/auth/register`, `/v1/auth/login`, `/v1/auth/external`, and `/v1/auth/logout`
provide persistent accounts and opaque 30-day sessions. Email passwords use PBKDF2-
SHA256 with unique 256-bit salts and are never logged or stored directly. Apple
authorization codes are exchanged with Apple before account creation; Google ID
tokens are checked against the configured server client ID. Provider configuration
lives under `AudioChoice:Authentication` and must be supplied through environment
variables or a secret manager, never committed to `appsettings.json`.

## Cross-platform account and library persistence

The authenticated library API now exposes:

- `GET /v1/library`
- `PUT /v1/library`
- `PUT /v1/library/{bookID}/progress`
- `PUT /v1/library/{bookID}/favorite`
- `GET /v1/library/{bookID}/bookmarks`
- `POST /v1/library/{bookID}/bookmarks`
- `DELETE /v1/library/bookmarks/{bookmarkID}`

The cloud library stores audiobook identity, metadata, listening progress, favorites,
and bookmarks. It deliberately contains no audio path or claim that audio is
available: each Android, web, or future iOS client must locate and fingerprint its
own local audio file before playback.

`Services/UserLibraryStore.cs` supplies a file-backed local-development adapter.
The normalized production schema begins at `Database/Migrations/001_initial.sql`
and supports multiple linked identities per user, sessions, library state, scan
results, temporary uploads, durable scan jobs, private transcript references, and
filter profiles. The staging service must remain on the local adapter until the
PostgreSQL runtime adapter and migration runner are added and verified.

## Run with .NET 8+

```sh
dotnet run --project AudioChoice.Api
```

## Container image

From the repository root:

```sh
docker build --file backend/Dockerfile --tag audiochoice-api:staging .
```

The image includes FFmpeg, listens on port 8080, stores private state under `/data`,
runs as the non-root `app` user, and keeps the paid OpenAI worker disabled unless it
is explicitly enabled through protected runtime configuration.

For guarded iPhone testing on a development Mac, run
`scripts/run-local-backend.sh` from the repository root. It prompts privately for
the OpenAI key, generates a temporary app access token, limits uploads to 50 MB,
and limits each job to three audio chunks.

## Contract checks

```sh
dotnet run --project AudioChoice.Api.ContractTests
```
