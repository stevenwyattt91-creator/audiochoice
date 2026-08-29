#!/usr/bin/env bash
# Corrects one Explore catalogue entry: its metadata, its synopsis, or both.
#
# Descriptions normally come from the file's own `ldes`/`©des` tags, reported by whichever
# client imported it. Plenty of audiobook files carry no description at all, and titles
# arrive wrong often enough that a curated catalogue needs a way to state what a recording
# actually is. This is that way.
#
# Only the fields you pass change. Anything omitted keeps its current value, so setting a
# synopsis will not disturb a title and correcting a title will not discard a synopsis.
#
# Usage:
#   scripts/correct-explore-entry.sh <catalog-id> [options]
#
# Options:
#   --title <text>          work title, without part or edition wording
#   --author <text>
#   --series <text>
#   --series-number <n>
#   --edition-type <text>   e.g. "Dramatized Adaptation"
#   --part <n>              which part of the release this file is
#   --total-parts <n>
#   --duration <seconds>    only when the stored runtime is wrong
#   --synopsis <text>       the "About this audiobook" text
#   --synopsis-file <path>  read the synopsis from a file, for longer text
#
# Run scripts/explore-catalog.sh list first, for catalog IDs and what is missing.
#
# Example, an entry that is really the dramatised second part:
#   scripts/correct-explore-entry.sh 999888777666555444333222 \
#     --title "Fourth Wing" --author "Rebecca Yarros" \
#     --series "The Empyrean" --series-number 1 \
#     --edition-type "Dramatized Adaptation" --part 2 --total-parts 2
set -euo pipefail

catalog_id="${1:-}"
if [ -z "$catalog_id" ] || [[ "$catalog_id" == --* ]]; then
  echo "Needs a catalog id first. Run scripts/explore-catalog.sh list to find one." >&2
  exit 1
fi
shift

# Empty means "leave alone", which is why these are not given defaults.
declare -A field=()
while [ $# -gt 0 ]; do
  case "$1" in
    --title)          field[workTitle]="$2"; shift 2 ;;
    --author)         field[author]="$2"; shift 2 ;;
    --series)         field[seriesTitle]="$2"; shift 2 ;;
    --series-number)  field[seriesNumber]="$2"; shift 2 ;;
    --edition-type)   field[editionType]="$2"; shift 2 ;;
    --part)           field[partNumber]="$2"; shift 2 ;;
    --total-parts)    field[totalParts]="$2"; shift 2 ;;
    --duration)       field[duration]="$2"; shift 2 ;;
    --synopsis)       field[description]="$2"; shift 2 ;;
    --synopsis-file)  field[description]="$(cat "$2")"; shift 2 ;;
    *) echo "Unknown option '$1'." >&2; exit 1 ;;
  esac
done
if [ ${#field[@]} -eq 0 ]; then
  echo "Nothing to change. Pass at least one option; --help is in the file header." >&2
  exit 1
fi

resource_group="${AUDIOCHOICE_RESOURCE_GROUP:-audiochoice-staging}"
api_host="${AUDIOCHOICE_API_HOST:-}"
api_token="${AUDIOCHOICE_API_TOKEN:-}"
if [ -z "$api_host" ] || [ -z "$api_token" ]; then
  if ! command -v az >/dev/null 2>&1; then
    echo "Needs the Azure CLI, or AUDIOCHOICE_API_HOST and AUDIOCHOICE_API_TOKEN." >&2
    exit 1
  fi
  if [ -z "$api_host" ]; then
    api_host=$(az containerapp show --name audiochoice-stg-api \
      --resource-group "$resource_group" \
      --query properties.configuration.ingress.fqdn --output tsv)
  fi
  if [ -z "$api_token" ]; then
    vault_name=$(az keyvault list --resource-group "$resource_group" \
      --query '[0].name' --output tsv)
    api_token=$(az keyvault secret show --vault-name "$vault_name" \
      --name staging-api-token --query value --output tsv)
  fi
fi

call() {
  curl --fail --silent --show-error \
    --header "Authorization: Bearer $api_token" "$@"
}

# Overrides reach Python as a JSON object, so no value has to survive shell quoting twice.
# A synopsis is a paragraph of prose with apostrophes and quotes in it.
overrides=$(
  for key in "${!field[@]}"; do
    printf '%s\n%s\n' "$key" "${field[$key]}"
  done | python3 -c '
import json, sys
lines = sys.stdin.read().split("\n")
print(json.dumps(dict(zip(lines[0::2], lines[1::2]))))
'
)

# The metadata endpoint matches on the exact fingerprint rather than the catalog ID, so the
# fingerprint has to be looked up. Anything but a single match is refused: a prefix hitting
# two editions would otherwise correct whichever happened to come back first.
editions=$(call "https://$api_host/v1/admin/editions")

payload=$(printf '%s' "$editions" | python3 -c '
import json, sys
prefix = sys.argv[1].lower()
overrides = json.loads(sys.argv[2])

matches = [e["fingerprint"] for e in json.load(sys.stdin)
           if e["fingerprint"]["sha256"].lower().startswith(prefix)]
if len(matches) != 1:
    sys.exit(f"Expected exactly one edition starting with {prefix!r}, found {len(matches)}.")
fingerprint = matches[0]

# Current values come from the edition itself rather than from its catalogue entry. The
# entry carries a displayed title with the part and edition wording already appended, and
# no part numbers at all, so building on it would nest the wording and blank the parts.
# The endpoint overwrites every metadata column it is sent, so each one has to be refilled.
def value(key, cast=None):
    if key in overrides:
        raw = overrides[key]
        return cast(raw) if cast and raw != "" else (raw or None)
    return fingerprint.get(key)

print(json.dumps({
    "fingerprint": fingerprint,
    "workTitle": value("workTitle") or "",
    "author": value("author"),
    "seriesTitle": value("seriesTitle"),
    "seriesNumber": value("seriesNumber", int),
    "editionType": value("editionType"),
    "partNumber": value("partNumber", int),
    "totalParts": value("totalParts", int),
    # Coalesced server-side, so null keeps whatever is stored rather than clearing it.
    "duration": float(overrides["duration"]) if overrides.get("duration") else None,
    "description": overrides.get("description") or None,
}))
' "$catalog_id" "$overrides")

call --request PUT \
  --header 'Content-Type: application/json' \
  --data "$payload" \
  "https://$api_host/v1/admin/editions/metadata"

echo "Corrected $catalog_id. It now reads:"
call "https://$api_host/v1/admin/explore/all" | python3 -c '
import json, sys
prefix = sys.argv[1].lower()
for entry in json.load(sys.stdin):
    book = entry["book"]
    if book["catalogID"].lower() != prefix:
        continue
    synopsis = book.get("description")
    if synopsis and len(synopsis) > 70:
        synopsis = synopsis[:70] + "..."
    print("  title:    " + book["title"])
    print("  author:   " + (book.get("author") or "-"))
    print("  edition:  " + (book.get("editionType") or "-"))
    print("  synopsis: " + (synopsis or "none"))
    if entry.get("withheldReason"):
        print("  withheld: " + entry["withheldReason"])
' "$catalog_id"
