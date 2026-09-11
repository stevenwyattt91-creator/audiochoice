#!/usr/bin/env python3
"""Shadow-evaluates a candidate OpenAI-compatible model (e.g. the self-hosted Qwen3.6-27B
vLLM sidecar) against ground_truth_scenes.json, using the REAL Terra/Sol consensual-lane
verification prompt this app runs in production.

Why this exists
----------------
Phase 4/5 of the self-host feasibility checklist: before any production wiring is even
considered, this answers one question on real data -- does a candidate model classify this
app's exact sexual-content taxonomy (accept/reject, and the buildup-inclusive boundary rule)
the way the already-verified OpenAI pipeline does, on the exact real scenes this session
found and fixed.

The prompt text embedded below (CONSENSUAL_LANE_INSTRUCTIONS + CLOSING_INSTRUCTIONS) is
copied verbatim from OpenAIContentAnalysisProvider.cs's VerifySceneBatch/
SceneVerificationClosingInstructions as of PR #74 (5.4-sol-majority-vote). If those prompts
change, update this copy too -- there is no runtime coupling between this script and the C#
source, so the two can silently drift apart if the app's own prompt changes without this
being updated. Grep OpenAIContentAnalysisProvider.cs for "SceneVerificationClosingInstructions"
to check.

Usage
-----
Against the local vLLM eval sidecar (default):
    python3 scripts/eval/run_scene_verification_eval.py

Against OpenAI directly (for a side-by-side baseline -- costs real credits):
    python3 scripts/eval/run_scene_verification_eval.py \\
        --base-url https://api.openai.com/v1 --model gpt-5.6-terra \\
        --api-key "$OPENAI_API_KEY"

This only ever sends the four small excerpts in ground_truth_scenes.json (a few dozen
transcript segments total), never a full book -- costs on a real API are a handful of cents
at most, not a full scan's worth.
"""
import argparse
import json
import sys
import urllib.error
import urllib.request
from pathlib import Path

GROUND_TRUTH_PATH = Path(__file__).parent / "ground_truth_scenes.json"

# Copied verbatim from OpenAIContentAnalysisProvider.cs's VerifySceneBatch, consensual-lane
# branch only -- every case in ground_truth_scenes.json is a consensual-lane scenario, so the
# sexual_violence branch is intentionally not reproduced here.
CONSENSUAL_LANE_INSTRUCTIONS = """
Act as a strict final verifier for one audiobook sexual-scene skip range.
Each candidate was produced by a high-recall detector and may be a false positive.
Set directSexualActEvidence=true only when this candidate's own transcript directly supports
an ongoing sexual act. Set sustainedBeyondKissing=true when the passage goes beyond
attraction, dialogue, kissing, embracing, or nudity alone into an actual sexual act. It asks how
far the passage goes, NOT how long it lasts: a brief encounter still qualifies. A listener who
switched on Complete sex scenes is asking for sex scenes to be gone, and a short one rejected here
is exactly the scene that then plays. A discussion
of past sex is a reference, not an ongoing act. Flirting, suggestive language, attraction,
kissing alone, embraces, nudity alone, sexual jokes or references, profanity, medical
discussion, violence, combat, pain, breathing, groaning, or the word "thrust" in a non-sexual
context must be rejected. Do not infer an act from tone, romance, or physical closeness.
Do not require graphic anatomical vocabulary. In context, physical sexual escalation such as
intimate touching (for example a hand moving onto a thigh), opening or spreading legs, removing
clothing, intimate caressing, or explicit consent/positioning is direct evidence when it is part
of an ongoing sexual encounter. A combination of these cues must not be downgraded merely because
the narration is euphemistic or non-graphic. This lane is for consensual activity only: if the
passage instead shows the act was non-consensual, reject it here (accepted=false) rather than
reclassifying it -- a separate sexual-violence review handles that case.

accepted may be true only when BOTH evidence booleans are true and confidence is at least
0.85. Otherwise accepted must be false. Set needsEscalation=true only when the candidate is
still a plausible ongoing sexual scene but the evidence, confidence, or exact boundaries are
uncertain and require a stronger final review. Set needsEscalation=false for clear rejections,
isolated innuendo, references, attraction, kissing, or nudity alone. Confirmed accepted scenes
will also receive final review. Confidence must describe the evidence that a sexual act occurs,
not merely for one suggestive word, and not for how long it lasts.
"""

# Copied verbatim from SceneVerificationClosingInstructions.
CLOSING_INSTRUCTIONS = """
For an accepted candidate, refine startTime to the beginning of the continuous romantic or
physical escalation that leads directly into the act -- kissing, embracing, undressing, or
touching that builds without a break in the scene -- not only the single sentence containing
the act's own unmistakable moment. A listener who wants this scene skipped is asking to miss
the buildup that makes the scene recognizable as one, not to have the skip begin partway
through it once the act itself is unambiguous. Only start later, at the act itself, when the
scene truly opens there with no preceding kissing or touching that belongs to the same
continuous moment. Set endTime where that activity clearly finishes.

Example of a correct startTime -- a real passage from a production book, reported by a
listener because the skip began too late and audible content still played:
"He leaned forward and kissed me lightly. Not forever. And though I knew it was a lie, I put my
arms around his neck and kissed him. He pulled me onto his lap, holding me tightly against him
as his lips parted mine. I became aware of every pore in my body when his tongue entered my
mouth. [...] I pushed Tamlin onto the bed, straddling him."
The correct startTime is at "He leaned forward and kissed me lightly" -- the kissing is the
scene's own beginning, part of the same unbroken escalation, not separate lead-in to be left
playing. A startTime placed instead at "I pushed Tamlin onto the bed" (the act's own most
unambiguous sentence) is wrong: it skips too little, and is exactly the mistake this
instruction exists to prevent.

Example of a correct rejection -- ordinary romantic tension with no escalation to skip:
"He caught her eye across the room and she felt her pulse quicken. There was something about
the way he looked at her." This is attraction and narration, not activity; accepted must be
false here regardless of how romantically charged the prose is, because there is no kissing,
touching, or undressing to mark where a scene even begins.

Keep timestamps within the supplied excerpt. Use a neutral, non-graphic but useful description.
Do not include graphic details or quotations in safeDescription. Never name intimate
anatomy or describe touching mechanics, positions, squeezing, or similar physical details.
Also return quote: for an accepted candidate, the exact consecutive words, copied verbatim
from a single segment's text, that begin the activity at your refined startTime -- this is not
shown to a listener, it is how the server confirms your refined boundary against the
transcript's own word timing. For a rejected candidate return an empty string. Return one
decision for every candidateKey, including nonconsensualEvidence (false when not applicable).
"""

# Same required-field contract as SceneVerificationResponseSchema() in the C# source, relaxed
# to a plain JSON instruction here since the vLLM sidecar is not guaranteed to support
# OpenAI's strict json_schema response_format the way the production OpenAIResponsesModelClient
# does. The model is asked for the same shape; malformed responses are reported as failures
# rather than silently coerced, so a model that cannot follow the schema shows up as a real
# eval failure instead of being hidden by lenient parsing.
RESPONSE_FORMAT_INSTRUCTIONS = """
Respond with ONLY a single JSON object, no other text, matching exactly this shape:
{"candidates": [{"candidateKey": string, "accepted": boolean, "needsEscalation": boolean,
"directSexualActEvidence": boolean, "sustainedBeyondKissing": boolean,
"nonconsensualEvidence": boolean, "startTime": number, "endTime": number,
"confidence": number, "safeDescription": string, "quote": string}]}
Return exactly one entry in "candidates" for the one candidateKey given below.
"""


def build_prompt(case: dict) -> str:
    window = case["transcript_window"]
    candidate = {
        "candidateKey": case["id"],
        "proposedStartTime": window["start_time"],
        "proposedEndTime": window["end_time"],
        "segments": window["segments"],
    }
    return (
        CONSENSUAL_LANE_INSTRUCTIONS
        + CLOSING_INSTRUCTIONS
        + RESPONSE_FORMAT_INSTRUCTIONS
        + "\nCandidates:\n"
        + json.dumps([candidate])
    )


def call_model(base_url: str, model: str, api_key: str | None, prompt: str,
                max_tokens: int, timeout: int) -> str:
    url = f"{base_url.rstrip('/')}/chat/completions"
    body = {
        "model": model,
        "messages": [{"role": "user", "content": prompt}],
        "max_tokens": max_tokens,
        "temperature": 0,
    }
    headers = {"Content-Type": "application/json"}
    if api_key:
        headers["Authorization"] = f"Bearer {api_key}"
    request = urllib.request.Request(
        url, data=json.dumps(body).encode("utf-8"), headers=headers, method="POST"
    )
    with urllib.request.urlopen(request, timeout=timeout) as response:
        payload = json.load(response)
    return payload["choices"][0]["message"]["content"]


def extract_json_object(text: str) -> dict | None:
    """Qwen3.6 (and many reasoning models) may prepend a visible thinking block before the
    actual answer. Rather than trust the whole response as JSON, find the last top-level
    {...} object in the text -- the model's actual answer, not its reasoning preamble."""
    depth = 0
    start = None
    candidates = []
    for index, char in enumerate(text):
        if char == "{":
            if depth == 0:
                start = index
            depth += 1
        elif char == "}":
            if depth > 0:
                depth -= 1
                if depth == 0 and start is not None:
                    candidates.append(text[start:index + 1])
    for candidate in reversed(candidates):
        try:
            return json.loads(candidate)
        except json.JSONDecodeError:
            continue
    return None


def score_case(case: dict, parsed: dict | None) -> dict:
    expected = case["expected"]
    outcome = {"id": case["id"], "raw_parsed": parsed}

    if parsed is None:
        outcome["verdict"] = "FAIL (no parseable JSON response)"
        return outcome

    candidates = parsed.get("candidates") or []
    if not candidates:
        outcome["verdict"] = "FAIL (empty candidates array)"
        return outcome

    result = candidates[0]
    accepted = result.get("accepted")
    expected_accepted = expected["accepted"]

    if accepted != expected_accepted:
        outcome["verdict"] = (
            f"FAIL (accepted={accepted}, expected {expected_accepted})"
        )
        return outcome

    if not expected_accepted:
        outcome["verdict"] = "PASS (correctly rejected)"
        return outcome

    # Accepted correctly -- now check the boundary, if this case specifies one.
    start_range = expected.get("expected_start_time_range")
    if start_range:
        start_time = result.get("startTime")
        low, high = start_range
        if start_time is None or not (low <= start_time <= high):
            outcome["verdict"] = (
                f"FAIL (accepted correctly, but startTime={start_time} outside "
                f"expected [{low}, {high}])"
            )
            return outcome

    outcome["verdict"] = "PASS"
    return outcome


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__,
                                      formatter_class=argparse.RawDescriptionHelpFormatter)
    parser.add_argument("--base-url", default="http://127.0.0.1:8002/v1",
                         help="OpenAI-compatible base URL. Default: the local vLLM eval sidecar.")
    parser.add_argument("--model", default="qwen3.6-27b-eval")
    parser.add_argument("--api-key", default=None)
    parser.add_argument("--max-tokens", type=int, default=2048,
                         help="Higher than a typical answer needs -- Qwen3.6 may emit a "
                              "visible thinking block before its actual JSON answer.")
    parser.add_argument("--timeout", type=int, default=120)
    args = parser.parse_args()

    ground_truth = json.loads(GROUND_TRUTH_PATH.read_text(encoding="utf-8"))
    cases = ground_truth["cases"]

    print(f"Evaluating {len(cases)} ground-truth case(s) against {args.model} "
          f"at {args.base_url}\n")

    results = []
    for case in cases:
        prompt = build_prompt(case)
        try:
            raw_response = call_model(
                args.base_url, args.model, args.api_key, prompt, args.max_tokens, args.timeout
            )
        except (urllib.error.URLError, urllib.error.HTTPError) as error:
            print(f"[{case['id']}] REQUEST FAILED: {error}")
            results.append({"id": case["id"], "verdict": f"FAIL (request error: {error})"})
            continue

        parsed = extract_json_object(raw_response)
        outcome = score_case(case, parsed)
        results.append(outcome)

        print(f"[{case['id']}] {outcome['verdict']}")
        if parsed and parsed.get("candidates"):
            result = parsed["candidates"][0]
            print(f"    accepted={result.get('accepted')} "
                  f"startTime={result.get('startTime')} "
                  f"confidence={result.get('confidence')} "
                  f"safeDescription={result.get('safeDescription')!r}")
        elif parsed is None:
            print(f"    raw response (truncated): {raw_response[:300]!r}")
        print()

    passed = sum(1 for r in results if r["verdict"].startswith("PASS"))
    print("=" * 72)
    print(f"TOTAL: {passed}/{len(results)} passed")
    return 0 if passed == len(results) else 1


if __name__ == "__main__":
    sys.exit(main())
