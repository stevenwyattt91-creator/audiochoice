#!/bin/zsh
set -euo pipefail

API_HOST=$(az containerapp show \
  --name audiochoice-stg-api \
  --resource-group audiochoice-staging \
  --query properties.configuration.ingress.fqdn \
  --output tsv)
API_BASE="https://$API_HOST"
EMAIL="auth-test-$(date +%s)@audiochoiceapp.com"
PASSWORD=$(openssl rand -base64 24 | tr -d '/+=' | head -c 24)

call() { curl --silent --show-error --retry 5 --retry-delay 3 "$@"; }
auth_body() { jq -n --arg email "$EMAIL" --arg password "$PASSWORD" '{email:$email,password:$password}'; }

print "Checkpoint 1/8: registering account"
REGISTER=$(jq -n --arg email "$EMAIL" --arg password "$PASSWORD" \
  '{email:$email,password:$password,displayName:"Authentication Test"}' |
  call --fail -H 'Content-Type: application/json' -d @- "$API_BASE/v1/auth/register")
TOKEN_ONE=$(jq -er '.accessToken' <<< "$REGISTER")

print "Checkpoint 2/8: listing linked identities"
IDENTITIES=$(call --fail -H "Authorization: Bearer $TOKEN_ONE" "$API_BASE/v1/auth/identities")
jq -e '.providers == ["password"]' <<< "$IDENTITIES" >/dev/null

print "Checkpoint 3/8: creating a second session"
LOGIN_TWO=$(auth_body | call --fail -H 'Content-Type: application/json' -d @- "$API_BASE/v1/auth/login")
TOKEN_TWO=$(jq -er '.accessToken' <<< "$LOGIN_TWO")

print "Checkpoint 4/8: logging out the first session"
call --fail -X POST -H "Authorization: Bearer $TOKEN_ONE" "$API_BASE/v1/auth/logout" >/dev/null
STATUS_ONE=$(call -o /dev/null -w '%{http_code}' -H "Authorization: Bearer $TOKEN_ONE" "$API_BASE/v1/library")
[[ "$STATUS_ONE" == "401" ]] || { print -u2 "Current-device logout did not revoke its session."; exit 1; }

print "Checkpoint 5/8: verifying the second session"
call --fail -H "Authorization: Bearer $TOKEN_TWO" "$API_BASE/v1/library" >/dev/null
print "Checkpoint 6/8: creating a third session"
LOGIN_THREE=$(auth_body | call --fail -H 'Content-Type: application/json' -d @- "$API_BASE/v1/auth/login")
TOKEN_THREE=$(jq -er '.accessToken' <<< "$LOGIN_THREE")

print "Checkpoint 7/8: logging out all sessions"
call --fail -X POST -H "Authorization: Bearer $TOKEN_TWO" "$API_BASE/v1/auth/logout-all" >/dev/null
STATUS_TWO=$(call -o /dev/null -w '%{http_code}' -H "Authorization: Bearer $TOKEN_TWO" "$API_BASE/v1/library")
STATUS_THREE=$(call -o /dev/null -w '%{http_code}' -H "Authorization: Bearer $TOKEN_THREE" "$API_BASE/v1/library")
[[ "$STATUS_TWO" == "401" && "$STATUS_THREE" == "401" ]] || {
  print -u2 "Logout-all did not revoke every active session."
  exit 1
}

print "Checkpoint 8/8: confirming all sessions were revoked"

unset TOKEN_ONE TOKEN_TWO TOKEN_THREE PASSWORD
print "Live authentication session-security test passed."
