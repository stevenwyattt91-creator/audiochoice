#!/usr/bin/env bash
# Inspects and curates the Explore catalogue.
#
# Explore is built from whatever listeners have scanned, so it accumulates entries that
# should not be on a store front: a file that arrived with no tags, or a second copy of a
# recording that is already listed. Most of those are withheld automatically, and this is
# for the rest -- seeing what is actually in there, and taking an entry down or putting it
# back by hand.
#
# Hiding sets a flag; it deletes nothing. The scan, the transcript and the filter events all
# survive, so an entry can be restored, and a listener who owns that file keeps the results
# it already produced.
#
# Usage:
#   scripts/explore-catalog.sh list                 every entry, with why any are withheld
#   scripts/explore-catalog.sh live                 only what listeners currently see
#   scripts/explore-catalog.sh hide <catalog-id>    take an entry off the catalogue
#   scripts/explore-catalog.sh restore <catalog-id> put a hidden entry back
#
# Reads the API token and host from Azure, like the other operational scripts here. Set
# AUDIOCHOICE_API_HOST and AUDIOCHOICE_API_TOKEN to skip that and talk to somewhere else.
set -euo pipefail

command="${1:-list}"
catalog_id="${2:-}"
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

# One place so the token cannot end up in an error message or a shell history entry.
call() {
  curl --fail --silent --show-error \
    --header "Authorization: Bearer $api_token" "$@"
}

# Renders the catalogue as a table. Python rather than jq because jq is not always installed,
# and the interesting part is the withheld reason, which needs wrapping to be readable.
render() {
  python3 -c '
import json, sys
entries = json.load(sys.stdin)
if not entries:
    print("The catalogue is empty.")
    sys.exit()
show_status = isinstance(entries[0], dict) and "book" in entries[0]
print(f"{'"'"'catalog id'"'"':<26} {'"'"'runtime'"'"':>8}  {'"'"'ctl'"'"':>4}  title")
print("-" * 96)
shown = withheld = 0
for entry in entries:
    book = entry["book"] if show_status else entry
    reason = entry.get("withheldReason") if show_status else None
    hours = (book.get("duration") or 0) / 3600
    runtime = f"{hours:.1f}h" if hours else "-"
    marker = "" if reason is None else "  [withheld]"
    print(f"{book['"'"'catalogID'"'"']:<26} {runtime:>8}  {book['"'"'eventCount'"'"']:>4}  "
          f"{book['"'"'title'"'"']}{marker}")
    byline = book.get("author") or "no author"
    identifier = book.get("productIdentifier")
    print(f"{'"'"''"'"':<26} {'"'"''"'"':>8}  {'"'"''"'"':>4}  {byline}"
          + (f"  ({identifier})" if identifier else ""))
    if reason:
        withheld += 1
        print(f"{'"'"''"'"':<26} {'"'"''"'"':>8}  {'"'"''"'"':>4}  why: {reason}")
    else:
        shown += 1
print("-" * 96)
if show_status:
    print(f"{shown} listed, {withheld} withheld, {len(entries)} scanned in total.")
else:
    print(f"{len(entries)} entr{'"'"'y'"'"' if len(entries) == 1 else '"'"'ies'"'"'} visible to listeners.")
'
}

case "$command" in
  list)
    call "https://$api_host/v1/admin/explore/all" | render
    ;;
  live)
    call "https://$api_host/v1/admin/explore" | render
    ;;
  hide)
    if [ -z "$catalog_id" ]; then
      echo "Needs a catalog id. Run 'list' to see them." >&2
      exit 1
    fi
    call --request DELETE "https://$api_host/v1/admin/explore/$catalog_id"
    echo "Hid $catalog_id. Nothing was deleted; 'restore' puts it back."
    ;;
  restore)
    if [ -z "$catalog_id" ]; then
      echo "Needs a catalog id. Run 'list' to see them." >&2
      exit 1
    fi
    call --request POST "https://$api_host/v1/admin/explore/$catalog_id/restore"
    echo "Restored $catalog_id."
    ;;
  *)
    echo "Unknown command '$command'. Expected list, live, hide or restore." >&2
    exit 1
    ;;
esac
