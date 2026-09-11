#!/usr/bin/env python3
"""Shadow-evaluates a candidate OpenAI-compatible model (e.g. the self-hosted Qwen3.6-27B
vLLM sidecar) against ground_truth_scenes.json, using the REAL production prompts this app
runs for each category a case belongs to.

Why this exists
----------------
Phase 4/5 of the self-host feasibility checklist: before any production wiring is even
considered, this answers one question on real data -- does a candidate model classify this
app's exact content taxonomy the way the already-verified OpenAI pipeline does, on real
scenes.

Three prompt paths, dispatched by each case's "category" field:
  - No category (or "sexual"): the Terra/Sol consensual-lane sexual-scene verification
    prompt, copied verbatim from OpenAIContentAnalysisProvider.cs's VerifySceneBatch /
    SceneVerificationClosingInstructions (as of PR #74, 5.4-sol-majority-vote).
  - category starts with "violence_": the dedicated graphic-violence/torture verification
    prompt, copied verbatim from VerifyViolenceBatch. This is the one other category in the
    app that gets a real second-opinion verification pass, distinct from Luna's first pass.
  - category starts with "self_harm": Luna's own first-pass classifier prompt (the general
    "Act as an audiobook content-preference classifier..." instructions), because self-harm
    has NO dedicated verification pass in the app at all -- Luna's first-pass judgment is
    the only judgment this category ever gets. This also means self-harm has no written
    rubric distinguishing e.g. a passing reference from an active attempt, unlike violence
    and sexual content -- see the self_harm cases' own notes for why that gap matters.

If any of these prompts change in the C# source, update the copy here too -- there is no
runtime coupling between this script and the app, so the two can silently drift apart.

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

# Copied verbatim from OpenAIContentAnalysisProvider.cs's VerifyViolenceBatch. This is the
# app's other real second-opinion verification pass (violence_graphic/violence_torture),
# separate from Luna's first-pass classification.
VIOLENCE_VERIFICATION_INSTRUCTIONS = """
Decide, for each candidate, whether the narration dwells on the physical detail of a body being
damaged. That is the only question.

Confirm it when the passage describes flesh being cut, torn or opened; blood flowing or pooling;
bones breaking; organs, entrails or brain matter; a limb or head severed; or a wound described
closely enough that a listener pictures the injury itself.

Do not confirm an act of violence stated without that detail. A punch, a slap, a shove, a
slammed door, a stabbing or shooting reported without describing the wound, a battle or duel, a
threat, someone being hurt or killed, a body discovered, bruises, scars, blood mentioned in
passing, medical treatment, pain, an injury's aftermath, grief, or fantasy peril are all not
confirmed. Captivity and beating are not confirmed either, however unpleasant: a character tied
to a chair and punched does not qualify.

Most fight scenes are not confirmed. If you are weighing whether the description is detailed
enough, it is not. Answer for every candidateKey.
"""

VIOLENCE_RESPONSE_FORMAT_INSTRUCTIONS = """
Respond with ONLY a single JSON object, no other text, matching exactly this shape:
{"candidates": [{"candidateKey": string, "dwellsOnPhysicalDamage": boolean,
"confidence": number}]}
Return exactly one entry in "candidates" for the one candidateKey given below.
"""

# Copied verbatim from OpenAIContentAnalysisProvider.cs's Luna first-pass classifier
# (BuildAnalysisPrompt's closing instructions) -- self-harm has no dedicated verification
# pass anywhere in the app, so this first-pass prompt is the ONLY judgment this category
# ever receives in production. Deliberately narrowed here to a single self-harm-labeled
# candidate rather than a full transcript batch, matching how a sexual/violence candidate is
# already isolated for review in this harness -- self-harm just never gets that isolation in
# the real pipeline, which is itself part of what these cases are testing.
SELF_HARM_CLASSIFIER_INSTRUCTIONS = """
Act as an audiobook content-preference classifier. Identify only events that are explicitly
supported by the supplied transcript.

Decide whether this passage should be labeled with one of: self_harm_reference (a passing
mention of self-harm or suicide, not depicted), self_harm_suicidal_thoughts (a character
expressing thoughts of suicide), self_harm_suicide_attempt (an active attempt at suicide or
serious self-harm depicted or clearly imminent), self_harm_depiction (self-harm actively
depicted, not suicide). If none of these apply -- including if the passage merely uses a
word like "suicide" or "kill myself" in a non-literal, genre-trope, or unrelated context --
return accepted=false and label null.

No word is content by itself. A word is evidence only in the sense the passage actually uses
it -- judge the passage by what is actually happening in it, never by the presence of a word.
"""

SELF_HARM_RESPONSE_FORMAT_INSTRUCTIONS = """
Respond with ONLY a single JSON object, no other text, matching exactly this shape:
{"candidates": [{"candidateKey": string, "accepted": boolean, "label": string or null,
"confidence": number, "safeDescription": string}]}
Return exactly one entry in "candidates" for the one candidateKey given below.
"""


def build_prompt(case: dict) -> str:
    window = case["transcript_window"]
    category = case.get("category", "sexual")
    candidate_base = {
        "candidateKey": case["id"],
        "startTime": window["start_time"],
        "endTime": window["end_time"],
        "segments": window["segments"],
    }

    if category.startswith("violence_"):
        return (
            VIOLENCE_VERIFICATION_INSTRUCTIONS
            + VIOLENCE_RESPONSE_FORMAT_INSTRUCTIONS
            + "\nCandidates:\n"
            + json.dumps([candidate_base])
        )

    if category.startswith("self_harm"):
        return (
            SELF_HARM_CLASSIFIER_INSTRUCTIONS
            + SELF_HARM_RESPONSE_FORMAT_INSTRUCTIONS
            + "\nCandidates:\n"
            + json.dumps([candidate_base])
        )

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
    category = case.get("category", "sexual")
    outcome = {"id": case["id"], "raw_parsed": parsed}

    # A case whose expected["accepted"] is explicitly None is a genuinely ambiguous
    # taxonomy-boundary case with no single settled correct answer (see its "notes" field
    # for why). Report the model's answer for a human to judge, but do not score it
    # pass/fail -- doing so would fabricate a false signal about model accuracy on a
    # question this eval set itself does not claim to have a ground truth for.
    if expected.get("accepted") is None:
        if parsed is None:
            outcome["verdict"] = "AMBIGUOUS (not scored -- no parseable JSON response)"
            return outcome
        candidates = parsed.get("candidates") or []
        result = candidates[0] if candidates else {}
        accepted_field = "dwellsOnPhysicalDamage" if category.startswith("violence_") \
            else "accepted"
        outcome["verdict"] = (
            f"AMBIGUOUS (not scored -- model answered "
            f"{accepted_field}={result.get(accepted_field)}, "
            f"see case notes for why this has no single correct answer)"
        )
        return outcome

    if parsed is None:
        outcome["verdict"] = "FAIL (no parseable JSON response)"
        return outcome

    candidates = parsed.get("candidates") or []
    if not candidates:
        outcome["verdict"] = "FAIL (empty candidates array)"
        return outcome

    result = candidates[0]
    # The violence-verification prompt's own response shape uses dwellsOnPhysicalDamage
    # rather than accepted, matching VerifyViolenceBatch's real schema exactly.
    accepted = result.get("dwellsOnPhysicalDamage") if category.startswith("violence_") \
        else result.get("accepted")
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
            category = case.get("category", "sexual")
            if category.startswith("violence_"):
                print(f"    dwellsOnPhysicalDamage={result.get('dwellsOnPhysicalDamage')} "
                      f"confidence={result.get('confidence')}")
            elif category.startswith("self_harm"):
                print(f"    accepted={result.get('accepted')} "
                      f"label={result.get('label')} "
                      f"confidence={result.get('confidence')} "
                      f"safeDescription={result.get('safeDescription')!r}")
            else:
                print(f"    accepted={result.get('accepted')} "
                      f"startTime={result.get('startTime')} "
                      f"confidence={result.get('confidence')} "
                      f"safeDescription={result.get('safeDescription')!r}")
        elif parsed is None:
            print(f"    raw response (truncated): {raw_response[:300]!r}")
        print()

    scored = [r for r in results if not r["verdict"].startswith("AMBIGUOUS")]
    ambiguous_count = len(results) - len(scored)
    passed = sum(1 for r in scored if r["verdict"].startswith("PASS"))
    print("=" * 72)
    if ambiguous_count:
        print(f"TOTAL: {passed}/{len(scored)} passed ({ambiguous_count} case(s) excluded as "
              f"genuinely ambiguous, not scored)")
    else:
        print(f"TOTAL: {passed}/{len(scored)} passed")
    return 0 if passed == len(scored) else 1


if __name__ == "__main__":
    sys.exit(main())
