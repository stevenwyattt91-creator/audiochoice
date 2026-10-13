#!/usr/bin/env python3
"""Transcribes one audio chunk with faster-whisper and prints word-timed segments as JSON.

Called once per chunk by LocalWhisperTranscriptionProvider (see that class's own remarks for
why this is a short-lived subprocess per chunk rather than a long-running server): the chunk's
file path is the only argument, and the result is a single JSON object on stdout, matching the
shape AudioChoice's TranscriptSegment/TranscriptWord records already expect so no translation
layer is needed on the C# side.

    python transcribe.py <audio_file_path> [--model large-v3-turbo] [--device cpu]

Output shape:
    {"segments": [{"startTime": 0.0, "endTime": 4.2, "text": "...",
                    "words": [{"text": "...", "startTime": 0.0, "endTime": 0.3}, ...]}]}

Model weights are downloaded once (by setup.ps1 running this script's --warm-up mode) and
cached under the faster-whisper/huggingface cache directory on this machine, the same way the
Lambda GPU worker's own model cache persists across container replacement -- the download is a
one-time setup cost, not something every scan repeats.
"""
import argparse
import json
import sys


def transcribe(audio_path: str, model_name: str, device: str, compute_type: str) -> dict:
    # Imported here, not at module scope, so --help and argument errors do not pay the cost of
    # loading faster-whisper (and, transitively, torch) just to print a usage message.
    from faster_whisper import WhisperModel

    model = WhisperModel(model_name, device=device, compute_type=compute_type)
    segments_iter, _info = model.transcribe(
        audio_path,
        word_timestamps=True,
        # Matches this pipeline's own expectation that a segment is a sentence-ish unit, not
        # one word or one whole chapter -- the backend's batching/scene-boundary logic
        # (ComputeAnalysisBatchRanges, NearestNaturalBreak) already assumes segments roughly
        # this size.
        vad_filter=True,
    )

    segments = []
    for segment in segments_iter:
        words = [
            {"text": word.word.strip(), "startTime": word.start, "endTime": word.end}
            for word in (segment.words or [])
        ]
        segments.append({
            "startTime": segment.start,
            "endTime": segment.end,
            "text": segment.text.strip(),
            "words": words,
        })
    return {"segments": segments}


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("audio_path", nargs="?", help="Path to the audio chunk to transcribe.")
    parser.add_argument(
        "--model", default="large-v3-turbo",
        help="faster-whisper model name or local path (default: large-v3-turbo).")
    parser.add_argument(
        "--device", default="cpu",
        help="cpu or cuda. The OptiPlex has no CUDA-capable GPU, so this stays cpu there.")
    parser.add_argument(
        "--compute-type", default="int8",
        help="int8 is the practical choice for CPU inference: full float precision on a CPU "
             "with no GPU offload is needlessly slow for a workload that is not latency "
             "sensitive here (see LocalWhisperTranscriptionProvider's own remarks).")
    parser.add_argument(
        "--warm-up", action="store_true",
        help="Download and load the model, then exit without transcribing anything. Used by "
             "setup.ps1 so the (large, one-time) model download happens during setup rather "
             "than silently in the middle of someone's first real scan.")
    args = parser.parse_args()

    if args.warm_up:
        from faster_whisper import WhisperModel
        print(f"Downloading/loading {args.model}...", file=sys.stderr)
        WhisperModel(args.model, device=args.device, compute_type=args.compute_type)
        print("Model ready.", file=sys.stderr)
        return 0

    if not args.audio_path:
        parser.error("audio_path is required unless --warm-up is given.")

    result = transcribe(args.audio_path, args.model, args.device, args.compute_type)
    print(json.dumps(result))
    return 0


if __name__ == "__main__":
    sys.exit(main())
