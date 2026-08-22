# Isolated iOS-beta Lambda scan worker

This worker claims only jobs marked `ios-beta-lambda`. Android and existing Azure jobs stay in the `azure-openai` lane and are never claimed by this host.

The stack contains two private services on the Lambda host:

- `whisper`: one GPU-resident `faster-whisper large-v3` model, reused across chunks.
- `scanner`: the existing AudioChoice scanner pipeline, using private Azure Blob/PostgreSQL data and OpenAI only for content analysis/scene verification.

Create `lambda-worker.env` only on the Lambda host by copying the example. It needs the existing scan-job PostgreSQL connection, the existing Azure storage account, the existing OpenAI key, and a dedicated Azure identity with **Blob Data Contributor** on the AudioChoice storage account. Do not put those values in source control or chat.

From the repository root on the Lambda host, start with:

```bash
docker compose -f deploy/lambda/docker-compose.yml up -d --build
curl http://127.0.0.1:8001/health
```

The transcription endpoint is host-local only and is not exposed to the internet.
