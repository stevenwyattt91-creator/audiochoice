# AudioChoice Local Scanner

Scans an audiobook for sexual content on this computer instead of in the cloud, and produces
a single `.audiochoicepack` file you move to a phone (AirDrop, Google Drive, a cable, whatever
is easiest) and import normally. No AWS account, no monthly server bill, no sign-in.

Transcription (turning the audio into words) runs entirely on this computer and costs
nothing. The three-stage content analysis (the actual judgment calls about what the book
contains) calls out to a cloud API, which costs a small amount per book -- real numbers
below.

## One-time setup

1. Open PowerShell in this folder.
2. Run:
   ```
   .\setup.ps1
   ```
   This installs Python, a private virtual environment just for this app, faster-whisper,
   ffmpeg, and the .NET 8 runtime -- whichever of those aren't already on this computer. It
   also downloads the whisper model (a few GB, one time only).
3. Copy `appsettings.local.json.example` to `appsettings.local.json` and fill in an API key
   for at least one of Gemini or DeepInfra (see "Getting API keys" below). You do not need
   both -- just whichever provider the model names in that file actually point at.

## Running a scan

```
.\AudioChoiceLocalScanner.exe "C:\path\to\your\audiobook.m4b"
```

This can take a while for a long book -- transcription on this computer's CPU is slower than
a rented GPU, and the content analysis itself runs multiple passes. Progress prints as it
goes. When it finishes, you'll have a `scans\<book title>.audiochoicepack` file. Move that to
your phone and open it with AudioChoice.

## Getting API keys

- **Gemini**: console.cloud.google.com (or ai.google.dev) -> create an API key. Used for the
  default `AnalysisModel`/`SceneVerificationModel` tiers in the example config.
- **DeepInfra**: deepinfra.com -> sign up -> API keys. Used for the default
  `SceneEscalationModel` tier, which runs the same Qwen model family this project's cloud
  backend already validated for scene verification and escalation, just hosted rather than
  self-run.

## Roughly what this costs per book

Based on a real completed scan of a 44.9-hour audiobook (Stephen King's *IT*) run through
this project's own pipeline: roughly 28 million input tokens and 136 thousand output tokens
across the three stages combined. At current (October 2026) rates:

| Tier assignment | Approximate cost for a book that size |
|---|---|
| All three tiers on Gemini 2.5 Flash-Lite | ~$3 |
| All three tiers on Gemini 2.5 Flash | ~$9 |
| Tiered (cheap first pass, stronger verification/escalation) | roughly $1-7, depending on how dense the book's content is |

A typical, shorter book with less flagged content costs meaningfully less than this. These
are real measured numbers from this project's own usage, not vendor marketing figures -- but
pricing changes over time, so check the current rate card for whichever provider you use if
cost matters to you.

## Why transcription is free but analysis isn't

Transcription (faster-whisper) runs on this computer's CPU with no ongoing cost once the
one-time setup is done. The content analysis stages need a capable language model to catch
subtle things like innuendo and judge genuinely ambiguous scenes, and no model that strong
runs well on a computer without a dedicated GPU. Calling a hosted API for just that part,
rather than renting a GPU or running everything locally, is what keeps this both affordable
and accurate -- see this project's own planning notes for the fuller reasoning.

## Troubleshooting

- **"faster_whisper module not found"**: run `setup.ps1` again, or check that
  `whisper-venv\Scripts\python.exe` exists next to this executable.
- **ffmpeg/ffprobe errors**: confirm `ffmpeg -version` and `ffprobe -version` work from an
  ordinary PowerShell window. `setup.ps1` installs ffmpeg via winget if it's missing, but a
  new PowerShell window is sometimes needed afterward for PATH changes to take effect.
- **A model/API error naming "GeminiApiKey" or "DeepInfraApiKey" is required**: edit
  `appsettings.local.json` and fill in the key for whichever provider the model names in that
  same file point at.
