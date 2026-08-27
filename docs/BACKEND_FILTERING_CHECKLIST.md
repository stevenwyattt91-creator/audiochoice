# Backend filtering: quality and accuracy checklist

Parked work, ordered for a solo developer who cannot hand-label explicit content.
Items 1–3 replace a conventional labeled eval set with automated measurement that
requires no content exposure. Do them first: they tell you whether items 4+ help
or hurt. Changing model routing before measurement exists means trading cost for
accuracy invisibly.

## Measurement (do first — substitutes for a hand-labeled eval set)

- [ ] **1. Negative-control suite on known-clean books.**
  Scan 5–10 books known to contain nothing objectionable (children's classics,
  technical nonfiction, Gutenberg public domain). Assert near-zero events.
  Directly measures the "filtering the slightest things" precision problem.
  Zero content exposure. If a Jane Austen novel returns 40 events, that's the
  precision bug found without reading anything.

- [ ] **2. Structural invariant checks after every scan.**
  All computable, no labels needed:
  - Skipped duration as % of runtime, per category
  - Scene count relative to book length
  - Scene duration distribution (flag >6 min as a merge failure)
  - Scene gaps clustering near the 45 s merge constant (means the constant is
    driving results, not the content)
  - Scenes starting/ending exactly on a Luna batch boundary (batch-edge artifact)
  - Confidence distribution (flag anchoring at the threshold)
  - **Terra/Sol agreement rate** — tells you whether Sol earns its cost

- [ ] **3. Profanity precision/recall against the deterministic word list.**
  The word list is ground truth, so Luna's accuracy on profanity is fully
  measurable automatically. Use as a general model-health proxy: if Luna over- or
  under-fires here it likely does elsewhere.

## Correctness

- [ ] **4. Redesign `ApplyCompleteSceneSafetyGuard` — confidence-gated, never total.**
  Current: >25 scenes OR >20% coverage drops *all* `sexual_complete_scene` events,
  so the books that most need filtering get none. Fix:
  - Drop the hard count limit (30 scenes in a romance novel is a fact, not an error)
  - Raise or remove the 20% ratio cap (some books run 30–40% legitimately)
  - Gate on confidence and Sol corroboration instead
  - When ratio is high and confidence low, publish the high-confidence subset and
    mark the rest unverified
  - Never drop everything; partial filtering beats none
  - Log ratio per book for drift monitoring

- [ ] **5. Word-level timestamps from transcription.**
  Request `word_timestamps` from Whisper. Currently segment-level (5–30 s), which
  is the root cause of profanity coarseness. Unlocks items 6 and the unused
  `CustomWords` contract field.

- [ ] **6. Mute vs skip as a per-category action.**
  `FilterRule.Action` already exists in the contract and is never read. Short
  1–2 word profanity mutes; sex scenes skip. iOS sets `isMuted = false` on all
  three branches; Android only ever calls `seekTo`. Wire `Action` through to both.

## Model routing and cost

- [ ] **7. Terra/Sol routing fix.**
  Current filter is `(Accepted || NeedsEscalation)`, so everything Terra approves
  still goes to Sol — double-paying on the easy path. Escalate only
  `NeedsEscalation` plus accepted-but-low-confidence.
  Also gate Terra entry: exclude lone `suggestive_dialogue` / `sexual_references`
  singletons; admit `nudity` / `implied` / `explicit` / `complete` plus dense
  clusters of mild labels (3+ within 60 s). A passing reference cannot be a
  sustained scene.

- [ ] **8. Reduce Luna overlap from 50% to 10–20%; batch on natural boundaries.**
  `batchSize = 100`, `overlap = 50`, `step = 50` means every segment is classified
  twice. Split at paragraph/chapter breaks instead of fixed segment counts —
  protects scene continuity better for roughly half the tokens.
  Also pull `profanity_*` out of Luna's allowed-label enum entirely.

- [ ] **9. Stop deleting detections in the pipeline — mark instead.**
  `ApplyNarrowViolencePolicy` permanently discards `violence_mild` /
  `violence_intense` / `violence_death`. Keep the narrow-violence product
  decision, but store with `filterable = false` rather than deleting, so a future
  tier change doesn't require rescanning the catalog.
  Separately verify `violence_graphic` itself isn't over-firing (negative controls
  + safe-description review) — deleting mild labels doesn't help if the
  over-firing happens inside the label you keep.

## Give users the dial

- [ ] **10. Two thresholds per category; ship confidence + severity; sensitivity dial.**
  A low *detect* threshold (high recall, everything found and shown as a
  toggleable control) plus a higher *skip* threshold (precision, only confident
  events auto-skip). Events in between are visible but don't fire by default.
  `confidence` and `severity` already exist end-to-end and no client reads either.
  Dial: aggressive / balanced / minimal → per-category confidence floors.
  Model severity explicitly 1–5 per label so the violence policy becomes a
  threshold, not a denylist.

- [ ] **11. "Play that anyway" in-session correction.**
  Rewinds into the skipped range, plays it, files a correction server-side. Fixes
  the moment (a false positive is currently a permanent hole with no recourse),
  and aggregate tap counts per event key become a precision signal from real
  usage with nobody reviewing content.

- [ ] **12. Snap skip boundaries to sentence ends; add a 150–250 ms fade.**
  Transcript has punctuation and timings. Current target is `windowEnd + 0.20 s`,
  which lands mid-word.
  Also audit compounding padding: Sol clamps to proposed ±30 s,
  `SceneEventPostProcessor` pads 8 s each side and merges across 45 s gaps.
  Stacked, that is the over-filtering risk. Measure actual skipped-duration ratio
  on the negative-control set and tighten from data.

## Taxonomy and contract

- [ ] **13. Taxonomy additions.**
  - Sexual violence as its own category. Currently lands under sexual OR violence
    depending on the model's label choice, making toggles unpredictable on the
    most sensitive content in the book.
  - Split `profanity_slur` into racial / religious / homophobic / ableist /
    misogynistic.
  - Explicit severity 1–5 per label.
  - Lower priority gaps: eating disorders, gore vs combat violence, non-violent
    child endangerment, threat/peril intensity, gambling, tobacco.

- [ ] **14. Close the auditor loop (when auditors go live).**
  `audit_decisions` are keyed by `scan_event_id` and nothing writes back to
  `scan_events`, so paid QA reaches no listener and a rescan orphans it (new
  GUIDs). Sol's escalation set is the natural auditor queue — ambiguous and
  expensive is exactly what deserves human review. The hand-labeled eval set
  becomes an auditor deliverable rather than founder work.

- [ ] **15. Single canonical taxonomy contract.**
  From the audit:
  - Six independent taxonomy implementations (backend C#, Android Kotlin, iOS
    Swift, mac-app Swift, contracts JSON at stale v1.0, website prose)
  - GUID `40000000-0000-0000-0000-000000000001` means three different things:
    legacy `self_harm`, current Substance category, and `'Drugs & Alcohol'` in the
    migration 011 seed
  - Violence exclusion expressed three incompatible ways
  - Audit allowlist GUIDs pasted 7× into raw SQL in `PostgresInternalAuditStore`
  - `stableKey` is not stable across rescans (hashes mapping + times +
    description), so every rescan silently invalidates users' `disabledEventKeys`
  - `CleanKeys` does not lowercase, while the scanner emits lowercase hex
  - `UserFacingEventPostProcessor` rewrites `groupID` for substances, so stored
    `group_id` doesn't always match the label→group mapping
  Generate every per-platform taxonomy from one registry in CI.
