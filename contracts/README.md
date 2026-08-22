# AudioChoice shared mobile contracts

Stage 13 establishes a versioned wire contract shared by the private backend, iOS,
and Android.

- `content-taxonomy.v1.json` is the canonical stable ID registry.
- `fixtures/completed-scan-response.v1.json` is the canonical completed-result fixture.
- Backend responses include `taxonomyVersion`.
- Mobile clients must reject nonempty results with an unsupported taxonomy version or
  unknown category/group/event mapping.
- Dates use ISO-8601 and JSON properties use lower camel case.
- Transcripts and uploaded audio are never part of a mobile response.

Changing an existing ID is a breaking contract change. Additive taxonomy changes must
increment the taxonomy version and ship client support before the backend emits them.
