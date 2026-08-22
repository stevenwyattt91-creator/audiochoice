#!/bin/zsh
set -euo pipefail

for command_name in az curl jq shasum openssl; do
  command -v "$command_name" >/dev/null 2>&1 || {
    print -u2 "Required tool is missing: $command_name"
    exit 1
  }
done

API_HOST=$(az containerapp show \
  --name audiochoice-stg-api \
  --resource-group audiochoice-staging \
  --query properties.configuration.ingress.fqdn \
  --output tsv)
API_BASE="https://$API_HOST"
TEST_EMAIL="account-data-test-$(date +%s)@audiochoiceapp.com"
TEST_PASSWORD=$(openssl rand -base64 24 | tr -d '/+=' | head -c 24)

request() {
  curl --fail --silent --show-error --retry 5 --retry-delay 3 "$@"
}

AUTH=$(jq -n --arg email "$TEST_EMAIL" --arg password "$TEST_PASSWORD" \
  '{email:$email,password:$password,displayName:"AudioChoice Account Data Test"}' |
  request -H 'Content-Type: application/json' -d @- "$API_BASE/v1/auth/register")
TOKEN=$(jq -er '.accessToken' <<< "$AUTH")

HASH=$(print -n "audiochoice-$TEST_EMAIL" | shasum -a 256 | awk '{print toupper($1)}')
FINGERPRINT=$(jq -n --arg hash "$HASH" \
  '{version:1,sha256:$hash,fileSize:4321,duration:3600,fileType:"m4b",workTitle:"Synced Data Test",author:"AudioChoice",seriesTitle:null,seriesNumber:null,editionType:"test",partNumber:null,totalParts:null}')
BOOK=$(jq -n --argjson fingerprint "$FINGERPRINT" \
  '{fingerprint:$fingerprint,title:"Synced Data Test",author:"AudioChoice",narrator:"Test Narrator",coverImageURL:null}' |
  request -X PUT -H "Authorization: Bearer $TOKEN" -H 'Content-Type: application/json' -d @- "$API_BASE/v1/library")
BOOK_ID=$(jq -er '.id' <<< "$BOOK")

request -X PUT -H "Authorization: Bearer $TOKEN" -H 'Content-Type: application/json' \
  -d '{"positionSeconds":125.5,"isFinished":false}' \
  "$API_BASE/v1/library/$BOOK_ID/progress" >/dev/null

BOOKMARK=$(jq -n '{positionSeconds:125.5,title:"Test bookmark",note:"Synced bookmark note"}' |
  request -H "Authorization: Bearer $TOKEN" -H 'Content-Type: application/json' \
  -d @- "$API_BASE/v1/library/$BOOK_ID/bookmarks")

PROFILE=$(jq -n \
  '{name:"Clean",isActive:true,rules:[{key:"profanity",enabled:true,action:"mute",severity:"moderate"},{key:"graphic_violence",enabled:true,action:"skip",severity:"high"}],customWords:["example"]}' |
  request -H "Authorization: Bearer $TOKEN" -H 'Content-Type: application/json' \
  -d @- "$API_BASE/v1/filter-profiles")

NOTE=$(jq -n --arg book "$BOOK_ID" \
  '{libraryBookID:$book,positionSeconds:125.5,text:"A standalone synced note"}' |
  request -H "Authorization: Bearer $TOKEN" -H 'Content-Type: application/json' \
  -d @- "$API_BASE/v1/notes")

COLLECTION=$(jq -n --arg book "$BOOK_ID" \
  '{name:"Test Collection",libraryBookIDs:[$book]}' |
  request -H "Authorization: Bearer $TOKEN" -H 'Content-Type: application/json' \
  -d @- "$API_BASE/v1/collections")

request -X POST -H "Authorization: Bearer $TOKEN" "$API_BASE/v1/auth/logout" >/dev/null
LOGIN=$(jq -n --arg email "$TEST_EMAIL" --arg password "$TEST_PASSWORD" \
  '{email:$email,password:$password}' |
  request -H 'Content-Type: application/json' -d @- "$API_BASE/v1/auth/login")
TOKEN=$(jq -er '.accessToken' <<< "$LOGIN")

LIBRARY=$(request -H "Authorization: Bearer $TOKEN" "$API_BASE/v1/library")
BOOKMARKS=$(request -H "Authorization: Bearer $TOKEN" "$API_BASE/v1/library/$BOOK_ID/bookmarks")
PROFILES=$(request -H "Authorization: Bearer $TOKEN" "$API_BASE/v1/filter-profiles")
NOTES=$(request -H "Authorization: Bearer $TOKEN" "$API_BASE/v1/notes?libraryBookID=$BOOK_ID")
COLLECTIONS=$(request -H "Authorization: Bearer $TOKEN" "$API_BASE/v1/collections")

jq -n \
  --argjson library "$LIBRARY" \
  --argjson bookmarks "$BOOKMARKS" \
  --argjson profiles "$PROFILES" \
  --argjson notes "$NOTES" \
  --argjson collections "$COLLECTIONS" \
  '{bookCount:($library|length),playbackPosition:$library[0].playbackPositionSeconds,bookmarkCount:($bookmarks|length),profileCount:($profiles|length),activeProfile:$profiles[0].name,noteCount:($notes|length),collectionCount:($collections|length),collectionBookCount:($collections[0].libraryBookIDs|length)}' |
  tee /dev/stderr |
  jq -e '.bookCount == 1 and .playbackPosition == 125.5 and .bookmarkCount == 1 and .profileCount == 1 and .activeProfile == "Clean" and .noteCount == 1 and .collectionCount == 1 and .collectionBookCount == 1' >/dev/null

unset TOKEN TEST_PASSWORD
print "Live account-data persistence test passed."
