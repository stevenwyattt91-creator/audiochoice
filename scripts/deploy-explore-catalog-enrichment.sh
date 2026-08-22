#!/bin/zsh
set -euo pipefail

resource_group='audiochoice-staging'
image_tag='staging-042'
mode="${1:-deploy}"
if [[ "$mode" != 'deploy' && "$mode" != '--covers-only' ]]; then
  echo 'Usage: ./scripts/deploy-explore-catalog-enrichment.sh [--covers-only]' >&2
  exit 2
fi

if [[ "$mode" == 'deploy' ]]; then
  registry_name=$(az acr list --resource-group "$resource_group" --query '[0].name' --output tsv)
  google_client_id=$(az containerapp show \
    --name audiochoice-stg-api \
    --resource-group "$resource_group" \
    --query "properties.template.containers[0].env[?name=='AudioChoice__Authentication__GoogleClientID'].value | [0]" \
    --output tsv)

  az acr build --registry "$registry_name" --image "audiochoice-api:$image_tag" \
    --file backend/Dockerfile .
  az deployment group create --name audiochoice-staging-explore-enrichment \
    --resource-group "$resource_group" --template-file deploy/azure/api.bicep \
    --parameters imageTag="$image_tag" googleClientID="$google_client_id" --output none
else
  echo 'Using the current healthy backend; repairing Explore artwork only.'
fi

vault_name=$(az keyvault list --resource-group "$resource_group" --query '[0].name' --output tsv)
api_token=$(az keyvault secret show --vault-name "$vault_name" --name staging-api-token --query value --output tsv)
api_host=$(az containerapp show --name audiochoice-stg-api --resource-group "$resource_group" \
  --query properties.configuration.ingress.fqdn --output tsv)
curl --fail --silent --show-error "https://$api_host/health"
echo

catalog=$(curl --fail --silent --show-error \
  --header "Authorization: Bearer $api_token" "https://$api_host/v1/admin/explore")
real_books=$(print -r -- "$catalog" | jq '[.[] | select(
  ((.title | ascii_downcase) | test("test|two[[:space:]]*twisted|twotwisted") | not)
)]')

if [[ $(print -r -- "$real_books" | jq 'length') -ne 4 ]]; then
  echo 'Safety stop: expected exactly four real scanned editions in Explore.' >&2
  print -r -- "$catalog" | jq '[.[] | {catalogID, title, author, editionType, eventCount}]' >&2
  exit 1
fi

echo 'Verified Explore editions:'
print -r -- "$real_books" | jq -r '.[] | "- \(.title) | \(.editionType // "Unknown edition") | \(.eventCount) events"'

# A fresh installation has no locally extracted artwork. Store one normalized
# publisher cover per exact edition so Explore looks the same for every account.
artwork_directory=$(mktemp -d "${TMPDIR:-/tmp}/audiochoice-explore-artwork.XXXXXX")
cleanup_artwork() {
  rm -f -- "$artwork_directory"/* 2>/dev/null || true
  rmdir -- "$artwork_directory" 2>/dev/null || true
}
trap cleanup_artwork EXIT

seed_artwork() {
  local title_pattern="$1"
  local isbn="$2"
  local apple_query="$3"
  local apple_match="$4"
  local catalog_id cover_url source_file normalized_file content_type byte_count
  local apple_result apple_cover google_result google_cover candidate

  catalog_id=$(print -r -- "$real_books" | jq -r --arg pattern "$title_pattern" \
    '[.[] | select(.title | test($pattern; "i"))][0].catalogID // empty')
  if [[ -z "$catalog_id" ]]; then
    echo "Safety stop: no Explore edition matched $title_pattern." >&2
    exit 1
  fi

  # Apple Books exposes publisher artwork through its public Search API. Search
  # by exact edition and request the same CDN artwork at catalog size. Open
  # Library remains a fallback for editions it happens to index.
  apple_result=$(curl --fail --silent --get \
    --data-urlencode "term=$apple_query" \
    --data 'media=audiobook' \
    --data 'entity=audiobook' \
    --data 'country=US' \
    --data 'limit=10' \
    'https://itunes.apple.com/search') || apple_result='{"results":[]}'
  apple_cover=$(print -r -- "$apple_result" | jq -r --arg pattern "$apple_match" \
    '[.results[] | select(
      ((.collectionName // .trackName // "") | test($pattern; "i"))
    ) | .artworkUrl100 // empty][0] // empty')

  google_result=$(curl --fail --silent --get \
    --data-urlencode "q=isbn:$isbn" \
    --data 'maxResults=5' \
    'https://www.googleapis.com/books/v1/volumes') || google_result='{"items":[]}'
  google_cover=$(print -r -- "$google_result" | jq -r \
    '[.items[]?.volumeInfo.imageLinks? |
      .extraLarge // .large // .medium // .small // .thumbnail // empty][0] // empty')
  google_cover="${google_cover/http:/https:}"

  source_file="$artwork_directory/$catalog_id-source"
  normalized_file="$artwork_directory/$catalog_id.jpg"

  cover_url=''
  for candidate in \
    "${apple_cover/100x100bb/1200x1200bb}" \
    "${apple_cover/100x100bb/600x600bb}" \
    "$apple_cover" \
    "$google_cover" \
    "https://covers.openlibrary.org/b/isbn/$isbn-L.jpg?default=false"; do
    [[ -n "$candidate" ]] || continue
    rm -f -- "$source_file"
    if curl --fail --location --silent \
      --retry 2 --retry-all-errors --connect-timeout 15 --max-time 60 \
      --user-agent 'AudioChoice catalog artwork importer/1.0' \
      --output "$source_file" "$candidate" \
      && file --brief --mime-type "$source_file" | grep -q '^image/'; then
      cover_url="$candidate"
      break
    fi
  done
  if [[ -z "$cover_url" ]]; then
    echo "Safety stop: no artwork provider returned an image for $catalog_id." >&2
    exit 1
  fi

  sips -Z 1400 -s format jpeg -s formatOptions 85 \
    "$source_file" --out "$normalized_file" >/dev/null
  content_type=$(file --brief --mime-type "$normalized_file")
  byte_count=$(stat -f '%z' "$normalized_file")
  if [[ "$content_type" != 'image/jpeg' || "$byte_count" -le 0 || "$byte_count" -gt 2000000 ]]; then
    echo "Safety stop: invalid normalized cover for $catalog_id ($content_type, $byte_count bytes)." >&2
    exit 1
  fi

  upload_response="$artwork_directory/$catalog_id-upload-response"
  upload_status=$(curl --silent --request PUT \
    --header "Authorization: Bearer $api_token" \
    --header 'Content-Type: image/jpeg' \
    --data-binary "@$normalized_file" \
    --output "$upload_response" \
    --write-out '%{http_code}' \
    "https://$api_host/v1/explore/$catalog_id/cover")
  if [[ "$upload_status" -lt 200 || "$upload_status" -ge 300 ]]; then
    echo "Safety stop: cover upload failed for $catalog_id (HTTP $upload_status)." >&2
    [[ ! -s "$upload_response" ]] || cat "$upload_response" >&2
    exit 1
  fi
  echo "Stored Explore cover: $catalog_id"
}

seed_artwork 'A Court of Thorns and Roses.*Part 1 of 2' \
  '9781685082758' \
  'A Court of Thorns and Roses Part 1 of 2 Dramatized Adaptation GraphicAudio' \
  '^A Court of Thorns and Roses.*(Part[[:space:]]*)?1 of 2'
seed_artwork 'A Court of Thorns and Roses.*Part 2 of 2' \
  '9781685082772' \
  'A Court of Thorns and Roses Part 2 of 2 Dramatized Adaptation GraphicAudio' \
  '^A Court of Thorns and Roses.*(Part[[:space:]]*)?2 of 2'
seed_artwork 'Iron Flame' \
  '9798890552198' \
  'Iron Flame Part 2 of 2 Dramatized Adaptation GraphicAudio Rebecca Yarros' \
  '^Iron Flame.*(Part[[:space:]]*)?2 of 2'
seed_artwork 'Fourth Wing' \
  '9798890551030' \
  'Fourth Wing Part 1 of 2 Dramatized Adaptation GraphicAudio Rebecca Yarros' \
  '^Fourth Wing.*(Part[[:space:]]*)?1 of 2'

catalog=$(curl --fail --silent --show-error \
  --header "Authorization: Bearer $api_token" "https://$api_host/v1/admin/explore")
missing_covers=$(print -r -- "$catalog" | jq '[.[] | select(
  ((.title | ascii_downcase) | test("test|two[[:space:]]*twisted|twotwisted") | not)
  and .coverImageURL == null
)] | length')
if [[ "$missing_covers" -ne 0 ]]; then
  echo 'Safety stop: one or more Explore editions still has no stored cover.' >&2
  exit 1
fi

echo 'Verified stored cover artwork and direct links for all known Explore editions.'
echo 'Explore catalog enrichment deployment completed.'
