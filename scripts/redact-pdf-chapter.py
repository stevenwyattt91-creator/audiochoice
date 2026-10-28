#!/usr/bin/env python3
"""
Redacts sexual content from a chapter of a PDF using the real 3-category AudioChoice
prompt (sexual_nudity / sexual_explicit_vulgar_scene / sexual_violence), run per-page
against the self-hosted vLLM endpoint. Built for manual chapter-level review -- "run this
chapter through our filter and black out whatever would be skipped" -- not for production
scanning (the real pipeline scans transcribed audio, not PDF text; see
backend/AudioChoice.Api/Processing/OpenAIContentAnalysisProvider.cs for that).

USAGE (run on the Lambda host, where the vLLM endpoint lives):
    python3 redact-pdf-chapter.py <source.pdf> <start_page_0indexed> <end_page_0indexed> <out.pdf>

Requires: pip install pymupdf requests (on the host, or a local venv).

History: an earlier version of this tool only redacted a single literal "quote" sentence
per flagged event and was deleted after delivering a chapter. A later user review found
real explicit material still fully readable. Rebuilding and testing against the exact same
chapter found three real, distinct bugs this version fixes:

  1. The model's own `quote` field is documented (in the real production prompt) as just
     one anchor sentence inside a scene, not the scene's own boundary -- the old tool
     redacted only that one sentence and left the rest of the flagged scene (setup,
     continuation, aftermath) fully visible. Fixed by asking for startQuote/endQuote (the
     first and last few words of the FULL continuous span) and redacting every word
     between them, not just the literal quote substring.
  2. A scene whose explicit span crosses a page boundary either had no endQuote findable
     on the page it started on, or no startQuote findable on the page it continued onto.
     Fixed with explicit sentinel values (###CONTINUES_TO_NEXT_PAGE###,
     ###CONTINUES_FROM_PREVIOUS_PAGE###) the model is instructed to use, carrying the
     previous page's own tail text forward as context for the next page's call.
  3. A single model call missed an unmistakably explicit passage on roughly 1 in 5 runs in
     direct repeated testing (sampling variance), while catching it cleanly the other 4
     times -- the same real variance already documented in the production pipeline's own
     Terra/Sol single-call verdicts. Fixed by calling the model multiple times per page
     (PER_PAGE_MODEL_CALLS) and unioning every attempt's redaction onto the same output
     page, since this tool's own failure mode that matters is a false negative, not cost.

Also fixed, during this same rebuild, a real prompt-wording gap (not a tool bug): a
sentence combining a narrow anatomical word with graphic physiological detail right next
to it (e.g. "her sex swollen and gleaming, wet with his seed") was previously only flagged
for the single anatomical word, leaving the graphic detail beside it fully readable and
trivially inferable. The sexual_nudity section of the prompt now explicitly tells the model
to escalate the whole clause to sexual_explicit_vulgar_scene when that happens.

Rule v2: a further user review of the resulting redaction found real pre-act positioning
(gripping hips, spreading legs to begin the act) and explicit desire reactions to named
anatomy still fully visible -- both outside the original mechanical-only bar by design.
At the owner's explicit request, sexual_explicit_vulgar_scene's own definition broadened:
the ceiling for "still just tension" is now kissing, pulling a partner close, embracing,
and straddling with nothing further; anything past that (undressing with intent, further
positioning toward the act, vulgar/explicit dialogue, talk about sexual activity including
masturbation, or the act itself in any form) is in scope, while quiet non-descriptive
aftermath stays allowed. This mirrors the same change made to the real production prompt
in OpenAIContentAnalysisProvider.cs (BaseAnalysisPromptVersion 7.2).
"""
import json
import re
import sys
import fitz
import requests

VLLM_URL = "http://127.0.0.1:8002/v1/chat/completions"
MODEL = "qwen3.6-27b"

# See module docstring, bug 3. Each page is called this many times; every attempt's
# resulting redaction is unioned onto the output page.
PER_PAGE_MODEL_CALLS = 3

PROMPT_TEMPLATE = """Act as an audiobook content-preference classifier. Identify only events that are explicitly
supported by the supplied page text. This deployment filters sexual content only -- profanity,
violence, substance use, blasphemy, and self-harm are not in scope and must never be reported,
no matter how prominent they are in a passage.

This deployment looks for exactly three things, permanently narrowed down from a much larger
set of sexual-content rungs at the owner's explicit request. Ordinary romantic or sexual
tension, flirtation, innuendo, crude jokes, suggestive dialogue, nudity mentioned only in
passing with no anatomical word, kissing, pulling a partner close, and straddling are all
explicitly ALLOWED and must never be reported under any label -- that is the ceiling for
"building tension without acting on it." The moment a passage moves past that ceiling into
undressing with sexual intent, further physical positioning toward the act (beyond straddling),
vulgar or descriptive sexual dialogue, talking about sexual activity explicitly (including
masturbation), or the act itself happening in any form, it is in scope and must be reported.
Only the three things below qualify at all.

sexual_nudity -- ONLY a specific anatomical word or its direct synonym naming intimate anatomy:
genitalia (penis, vagina, cock, dick as anatomy, pussy, genitals, sex as anatomy), breasts,
nipples, areolas, or buttocks (butt, ass as a body part). Report the narrowest possible range --
the exact word or short phrase that uses the anatomical term, never the sentence or paragraph
around it. A general reference to someone being naked, nude, unclothed, undressed, bare, or
removing clothing does NOT qualify on its own -- this label is only for the literal anatomical
word itself, not for nakedness in general. A word in this list used in a non-body sense (an
insult, a euphemism with no anatomical meaning, an unrelated word that happens to share a
spelling) does not qualify either -- judge the word's actual use in context, not just its
presence. No word is sexual content by itself: a hand on a chest during first aid, a breast
wound in battle, a medical examination, a badge pinned at her breast, or armour such as a
breastplate is anatomy mentioned in a non-sexual, non-exposed context and does not qualify. The
word qualifies when the passage itself describes that anatomy as bare, uncovered, exposed, or
otherwise names it directly in a body-revealing or sexual context. IMPORTANT: if the sentence
containing the anatomical word ALSO describes that anatomy with graphic physiological detail --
swollen, gleaming, wet, dripping, aching, throbbing, or similar, especially when tied to arousal
or the aftermath of a sexual act (e.g. "wet with his seed") -- report the WHOLE sentence or
clause as sexual_explicit_vulgar_scene instead of (or in addition to) a narrow sexual_nudity hit
on the single word. A real miss this prompt exists to fix: a passage reading "her sex swollen
and gleaming, wet with his seed" was previously only flagged for the word "sex" in isolation,
leaving "swollen and gleaming, wet with his seed" fully visible immediately next to it -- which
defeats the purpose, since a reader can trivially infer the redacted word from the graphic detail
left standing right beside it. When in doubt about whether graphic detail accompanies an
anatomical word, prefer reporting the fuller clause under sexual_explicit_vulgar_scene.

sexual_explicit_vulgar_scene -- covers sexual activity in all its stages: leading into it,
during it, and explicit talk about it -- not only graphic mechanical description. The ceiling
for "still just tension, not yet acting on it": ordinary romantic or sexual tension, kissing,
pulling a partner close, embracing, and straddling with nothing further are all ALLOWED and
must never be flagged. Report a passage once it moves past that ceiling into any of:
  - Positioning or action that begins or advances toward a sexual act: undressing (self or a
    partner) with sexual intent, climbing further into position beyond a straddle, moving or
    spreading legs to begin the act, or any other physical setup clearly aimed at starting
    intercourse, oral sex, or similar -- this qualifies even described through sensation rather
    than anatomical mechanics (e.g. "he gripped her hips and positioned himself against her" is
    in scope even with no explicit anatomical word).
  - Vulgar or descriptive sexual dialogue: dirty talk, a character describing what they want
    done, what is being done, or what is about to happen, in explicit or crude terms.
  - Explicit talk about sexual activity generally, including masturbation ("touching
    yourself/myself" and similar), even when no act is currently happening in the scene.
  - The act itself, actually happening, in any form -- graphic and mechanical description
    still qualifies, and so does the act depicted through sensation, reaction, or implication
    once it has clearly started (not merely anticipated).
  - A character's body described nakedly/sexually during or immediately around the act (what
    they look like exposed, what was just done to them) -- beyond the narrow single anatomical
    word sexual_nudity already covers on its own.
Do NOT report: desire stated in prose with no positioning or act (wanting someone, wanting to
be touched, anticipating); or quiet aftermath -- two characters lying together, tangled in
sheets, breathing hard, calm or affectionate talk -- AS LONG AS the aftermath does not describe
either character's body, recap or describe what they just did sexually, or continue using
explicit sexual dialogue. The moment aftermath does any of those three things, it is back in
scope. Report the FULL CONTINUOUS SPAN, from the moment it crosses this bar to the moment it
returns to the allowed side (tension-only or clean aftermath), not just one representative
sentence. When genuinely unsure whether a passage has crossed this bar or is still on the
allowed side (tension, clean aftermath), resolve it as allowed and do not report it.

sexual_violence -- ONLY an ongoing scene depicting rape or sexual assault as a sexual act, as it
happens -- it must itself be sexual in nature, not merely violent. Judge consent from what the
passage itself establishes -- refusal, resistance, incapacitation, coercion, or an imbalance of
power exploited to force the act -- not from a character's profession, relationship, or the
setting alone. A passage ambiguous about consent is not sexual_violence.

For every event, return:
- label: one of sexual_nudity, sexual_explicit_vulgar_scene, sexual_violence
- startQuote: the exact first 4-8 consecutive words (copied verbatim from the page text) where
  this event's material begins. If this event is a continuation of an explicit scene that was
  ALREADY in progress at the very top of this page (see the note below about the previous
  page), set startQuote to exactly "###CONTINUES_FROM_PREVIOUS_PAGE###" instead of quoting text.
- endQuote: the exact last 4-8 consecutive words (copied verbatim from the page text) where this
  event's material ends. For a short nudity event, startQuote and endQuote may be the same short
  phrase. For sexual_explicit_vulgar_scene, endQuote must be the END of the full continuous
  explicit span (see above), not just the end of one sentence within it. If the explicit material
  is still ongoing at the very end of this page (does not resolve before the page's last
  sentence), set endQuote to exactly "###CONTINUES_TO_NEXT_PAGE###" instead of quoting text.
- confidence: 0 to 1
- safeDescription: a neutral, non-graphic summary of at most 80 characters, never naming the
  specific anatomical word or act

Omit events below 0.6 confidence. Do not invent text not present on the page.
"""

CONTEXT_NOTE_TEMPLATE = """
Note: the previous page ended {ending_state}. The last few sentences of the previous page were:
"{tail_text}"
If this page's own opening text is a direct, uninterrupted continuation of that same explicit
material (the scene has not yet resolved), report it as its own event with startQuote set to
"###CONTINUES_FROM_PREVIOUS_PAGE###" exactly as instructed above.
"""

PAGE_TEXT_HEADER = """
Page text:
"""

SCHEMA = {
    "type": "object",
    "additionalProperties": False,
    "required": ["events"],
    "properties": {
        "events": {
            "type": "array",
            "items": {
                "type": "object",
                "additionalProperties": False,
                "required": ["label", "startQuote", "endQuote", "confidence", "safeDescription"],
                "properties": {
                    "label": {"type": "string", "enum": ["sexual_nudity", "sexual_explicit_vulgar_scene", "sexual_violence"]},
                    "startQuote": {"type": "string"},
                    "endQuote": {"type": "string"},
                    "confidence": {"type": "number"},
                    "safeDescription": {"type": "string"},
                },
            },
        }
    },
}


SENTINEL_CONTINUES_FROM_PREVIOUS = "###CONTINUES_FROM_PREVIOUS_PAGE###"
SENTINEL_CONTINUES_TO_NEXT = "###CONTINUES_TO_NEXT_PAGE###"


def call_model(page_text, continuation_tail=None):
    prompt = PROMPT_TEMPLATE
    if continuation_tail:
        prompt += CONTEXT_NOTE_TEMPLATE.format(
            ending_state="mid-way through explicit sexual content",
            tail_text=continuation_tail,
        )
    prompt += PAGE_TEXT_HEADER + page_text
    payload = {
        "model": MODEL,
        "messages": [{"role": "user", "content": prompt}],
        "max_tokens": 3000,
        "chat_template_kwargs": {"enable_thinking": False},
        "response_format": {
            "type": "json_schema",
            "json_schema": {"name": "audiochoice_page_events", "schema": SCHEMA, "strict": True},
        },
    }
    resp = requests.post(VLLM_URL, json=payload, timeout=180)
    resp.raise_for_status()
    content = resp.json()["choices"][0]["message"]["content"]
    return json.loads(content)


def normalize(s):
    return re.sub(r"[^a-z0-9]+", " ", s.lower()).strip()


def build_subtoken_index(words):
    """Expands each PDF word into its normalized sub-tokens (a contraction like "wasn't"
    normalizes to two sub-tokens, "wasn" and "t") and records which original word index
    each sub-token came from. A real bug this fixes: normalizing the quote phrase as one
    whole string splits contractions into extra tokens, while normalizing the page's words
    one at a time (an earlier version of this function) kept each contraction as a single
    un-split token -- the two token counts disagreed and a quote spanning a contraction
    could never match at all, silently dropping that event's redaction."""
    subtokens = []
    owner = []
    for word_index, word in enumerate(words):
        for token in normalize(word).split():
            subtokens.append(token)
            owner.append(word_index)
    return subtokens, owner


def find_word_span(words, quote, start_from=0):
    """Find the index range [i, j) in `words` (list of word strings, inclusive-exclusive)
    matching `quote` (a short phrase), restricted to words at or after word index
    start_from. Word-boundary matching only -- never a substring match within a longer
    word (a real bug in this tool's first version: a short quote like "ass" matched inside
    "Cassian")."""
    quote_tokens = normalize(quote).split()
    if not quote_tokens:
        return None
    subtokens, owner = build_subtoken_index(words)
    n = len(quote_tokens)
    # Only consider sub-token start positions whose owning word is >= start_from.
    for i in range(len(subtokens) - n + 1):
        if owner[i] < start_from:
            continue
        if subtokens[i:i + n] == quote_tokens:
            return (owner[i], owner[i + n - 1] + 1)
    return None


def redact_page(page, events):
    word_tuples = page.get_text("words")  # x0,y0,x1,y1,word,block,line,word_no
    words = [w[4] for w in word_tuples]
    rects_to_draw = []
    unmatched = []
    continues_to_next_page = False

    for ev in events:
        starts_from_top = ev["startQuote"].strip() == SENTINEL_CONTINUES_FROM_PREVIOUS
        ends_into_next = ev["endQuote"].strip() == SENTINEL_CONTINUES_TO_NEXT

        if starts_from_top:
            span_start = 0
        else:
            start_match = find_word_span(words, ev["startQuote"], start_from=0)
            if start_match is None:
                unmatched.append(("startQuote", ev))
                continue
            span_start = start_match[0]

        if ends_into_next:
            span_end = len(words)
            continues_to_next_page = True
        else:
            end_match = find_word_span(words, ev["endQuote"], start_from=span_start)
            if end_match is None:
                end_match = find_word_span(words, ev["endQuote"], start_from=0)
            if end_match is None:
                # The model said the span ends on this page but the literal phrase
                # could not be located -- redact through the rest of the page rather
                # than silently dropping the whole event (fail safe, not fail silent).
                span_end = len(words)
                unmatched.append(("endQuote-fallback-to-page-end", ev))
            else:
                span_end = max(end_match[1], span_start)

        for idx in range(span_start, span_end):
            x0, y0, x1, y1, *_ = word_tuples[idx]
            rects_to_draw.append(fitz.Rect(x0, y0, x1, y1))

    for r in rects_to_draw:
        page.draw_rect(r, color=(0, 0, 0), fill=(0, 0, 0))
    return rects_to_draw, unmatched, continues_to_next_page


def main():
    if len(sys.argv) != 5:
        print(
            "Usage: redact-pdf-chapter.py <source.pdf> <start_page_0indexed> "
            "<end_page_0indexed> <out.pdf>",
            file=sys.stderr,
        )
        sys.exit(1)

    src_path = sys.argv[1]
    start_page = int(sys.argv[2])  # 0-indexed, inclusive
    end_page = int(sys.argv[3])    # 0-indexed, inclusive
    out_path = sys.argv[4]

    doc = fitz.open(src_path)
    out_doc = fitz.open()

    pending_continuation_tail = None
    for page_index in range(start_page, end_page + 1):
        src_page = doc[page_index]
        page_text = src_page.get_text()
        print(f"--- page {page_index} ---", file=sys.stderr)
        out_doc.insert_pdf(doc, from_page=page_index, to_page=page_index)
        out_page = out_doc[-1]

        all_events = []
        any_continues_to_next_page = False
        for attempt in range(PER_PAGE_MODEL_CALLS):
            result = call_model(page_text, continuation_tail=pending_continuation_tail)
            events = result.get("events", [])
            print(f"  attempt {attempt + 1}: {len(events)} event(s): " + json.dumps(events), file=sys.stderr)
            all_events.extend(events)
            # Each attempt is redacted independently against the SAME output page, so
            # overlapping word-rects from different attempts simply overlap visually --
            # a true union of coverage -- rather than needing the events themselves
            # deduplicated first, which would require comparing differently-phrased
            # quotes for the same real span across attempts.
            rects, unmatched, continues_to_next_page = redact_page(out_page, events)
            if continues_to_next_page:
                any_continues_to_next_page = True
            print(f"    drew {len(rects)} word-rects this attempt; unmatched={unmatched}", file=sys.stderr)
        continues_to_next_page = any_continues_to_next_page

        if continues_to_next_page:
            # Feed the next page's call the tail of this page's own text so it can
            # recognize the opening of the next page as a continuation rather than
            # judging it in isolation.
            tail_words = page_text.strip().split()
            pending_continuation_tail = " ".join(tail_words[-40:])
        else:
            pending_continuation_tail = None

    out_doc.save(out_path)
    print(f"Saved {out_path}", file=sys.stderr)


if __name__ == "__main__":
    main()
