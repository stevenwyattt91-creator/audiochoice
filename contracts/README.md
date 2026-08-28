# AudioChoice shared mobile contracts

Stage 13 establishes a versioned wire contract shared by the private backend, iOS,
and Android.

- `content-taxonomy.v2.json` is the canonical stable ID registry, and the only place the
  taxonomy is declared. The backend asserts it matches `Processing/ContentTaxonomy.cs`, and
  both mobile clients assert their own group tables match it, so adding a group in one place
  fails a check instead of silently producing a filter with no switch.
- `content-taxonomy.v1.json` is superseded. It describes the five prototype labels and is
  kept only so a scan stamped with taxonomy version 1.0 still resolves. It was described here
  as canonical while carrying five of the twenty-eight labels the backend actually used.
- `fixtures/completed-scan-response.v1.json` is the canonical completed-result fixture.
- Backend responses include `taxonomyVersion`.
- Mobile clients must reject nonempty results with an unsupported taxonomy version or
  unknown category/group/event mapping.
- Dates use ISO-8601 and JSON properties use lower camel case.
- Transcripts and uploaded audio are never part of a mobile response.

Changing an existing ID is a breaking contract change. Additive taxonomy changes must
increment the taxonomy version and ship client support before the backend emits them.
