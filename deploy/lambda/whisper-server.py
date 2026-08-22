"""Private GPU transcription service for the isolated AudioChoice iOS-beta worker."""
import os
import tempfile
import asyncio
import time
import gc
from pathlib import Path

from fastapi import FastAPI, File, Form, HTTPException, UploadFile
from faster_whisper import WhisperModel

MODEL_NAME = os.getenv("WHISPER_MODEL", "large-v3-turbo")
FALLBACK_MODEL_NAME = os.getenv("WHISPER_FALLBACK_MODEL", "large-v3")
COMPUTE_TYPE = os.getenv("WHISPER_COMPUTE_TYPE", "float16")
DEVICE = os.getenv("WHISPER_DEVICE", "cuda")
WORKER_COUNT = max(1, int(os.getenv("WHISPER_WORKERS", "4")))
CONCURRENCY_PER_WORKER = max(1, int(os.getenv("WHISPER_CONCURRENCY_PER_WORKER", "3")))
# WHISPER_WORKERS is the number of Uvicorn processes. Each process owns one
# model instance and has its own bounded two-request queue.
transcription_slots = asyncio.Semaphore(CONCURRENCY_PER_WORKER)
queue_depth = 0

app = FastAPI(title="AudioChoice private transcription service", docs_url=None, redoc_url=None)
active_model_name = MODEL_NAME
model = WhisperModel(MODEL_NAME, device=DEVICE, compute_type=COMPUTE_TYPE)
model_lock = asyncio.Lock()

def select_model(name):
    global model, active_model_name
    if active_model_name == name:
        return model
    del model
    gc.collect()
    try:
        import torch
        torch.cuda.empty_cache()
    except Exception:
        pass
    model = WhisperModel(name, device=DEVICE, compute_type=COMPUTE_TYPE)
    active_model_name = name
    return model


@app.get("/health")
def health():
    return {"ready": True, "model": MODEL_NAME, "fallbackModel": FALLBACK_MODEL_NAME,
            "device": DEVICE, "workers": WORKER_COUNT,
            "concurrencyPerWorker": CONCURRENCY_PER_WORKER, "queueDepth": queue_depth}


@app.post("/transcribe")
async def transcribe(
    file: UploadFile = File(...),
    language: str = Form("en"),
    word_timestamps: bool = Form(True),
    use_fallback: bool = Form(False),
):
    suffix = Path(file.filename or "audio.wav").suffix or ".wav"
    with tempfile.NamedTemporaryFile(suffix=suffix, delete=False) as temp_file:
        path = temp_file.name
        while chunk := await file.read(1024 * 1024):
            temp_file.write(chunk)

    global queue_depth
    queue_depth += 1
    queued_at = time.monotonic()
    await transcription_slots.acquire()
    queue_depth -= 1
    process_id = os.getpid()
    worker_id = process_id
    try:
        selected_name = FALLBACK_MODEL_NAME if use_fallback else MODEL_NAME
        async with model_lock:
            selected_model = select_model(selected_name)
        started = time.monotonic()
        # faster-whisper is blocking; move it off the event loop so this
        # process can service its second bounded slot concurrently.
        segments, _ = await asyncio.to_thread(
            selected_model.transcribe,
            path, language=language or None, word_timestamps=word_timestamps,
            vad_filter=True, beam_size=5)
        elapsed = time.monotonic() - started
        print({"event": "transcription_complete", "workerId": worker_id,
               "queueDepth": queue_depth, "elapsedSeconds": elapsed,
               "queueWaitSeconds": started - queued_at, "model": selected_name}, flush=True)
        return {"segments": [
            {"start": segment.start, "end": segment.end, "text": segment.text}
            for segment in segments
        ], "model": selected_name, "elapsedSeconds": elapsed}
    except Exception as error:
        raise HTTPException(status_code=500, detail="Transcription failed") from error
    finally:
        transcription_slots.release()
        try:
            os.unlink(path)
        except FileNotFoundError:
            pass
