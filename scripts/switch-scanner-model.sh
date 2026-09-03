#!/usr/bin/env bash
# Switches the GPU scanner between OpenAI and Bedrock Nova, and back.
#
# Run this on the Lambda GPU host, from the repository root. It edits lambda-worker.env, which
# lives only on that host and is not in source control, then rebuilds just the scanner
# container. Whisper is deliberately left running so the GPU model is not reloaded.
#
#   scripts/switch-scanner-model.sh nova     # classify on Bedrock Nova
#   scripts/switch-scanner-model.sh openai   # go back
#   scripts/switch-scanner-model.sh show     # report what is configured now
#
# Going back is a first-class option, not an afterthought. If Nova turns out to miss content
# the previous models caught, reverting has to be one command at any hour, without reading
# documentation or remembering which variables were involved.
set -euo pipefail

root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
env_file="$root/deploy/lambda/lambda-worker.env"
compose="$root/deploy/lambda/docker-compose.yml"
mode="${1:-show}"

if [ ! -f "$compose" ]; then
  echo "Run this from the AudioChoice repository on the Lambda GPU host." >&2
  exit 1
fi
if [ ! -f "$env_file" ]; then
  echo "lambda-worker.env is missing at $env_file" >&2
  echo "That file holds this host's database, storage and model settings. Do not create it" >&2
  echo "from scratch here; this script only adds the model settings to an existing one." >&2
  exit 1
fi

# Sets a key to a value, replacing any existing line for it. Kept idempotent so running this
# twice does not leave two conflicting definitions of the same variable, which would resolve
# differently depending on order.
set_value() {
  local key="$1" value="$2"
  if grep -q "^${key}=" "$env_file"; then
    local temporary
    temporary="$(mktemp)"
    grep -v "^${key}=" "$env_file" > "$temporary"
    printf '%s=%s\n' "$key" "$value" >> "$temporary"
    mv "$temporary" "$env_file"
  else
    printf '%s=%s\n' "$key" "$value" >> "$env_file"
  fi
}

current() {
  echo "Configured now:"
  for key in AudioChoice__OpenAI__AnalysisProvider AudioChoice__OpenAI__BedrockRegion \
             AudioChoice__OpenAI__AnalysisModel AudioChoice__OpenAI__SceneVerificationModel \
             AudioChoice__OpenAI__SceneEscalationModel AudioChoice__OpenAI__ScannerVersion; do
    printf '  %-46s %s\n' "${key##*__}" "$(grep "^${key}=" "$env_file" 2>/dev/null | tail -1 | cut -d= -f2- || true)"
  done
  if grep -q '^AWS_ACCESS_KEY_ID=' "$env_file" 2>/dev/null; then
    echo "  AWS credentials                                present"
  else
    echo "  AWS credentials                                MISSING"
  fi
}

case "$mode" in
  show)
    current
    exit 0
    ;;

  nova)
    # Refuse rather than start a scanner that will fail on its first model call. Without
    # credentials every scan would claim a job, transcribe it on the GPU, and then fail at
    # classification -- burning the expensive half of the work to discover a missing key.
    if ! grep -q '^AWS_ACCESS_KEY_ID=' "$env_file" || \
       ! grep -q '^AWS_SECRET_ACCESS_KEY=' "$env_file"; then
      echo "AWS credentials are not in lambda-worker.env." >&2
      echo "Add these two lines to $env_file, then run this again:" >&2
      echo "  AWS_ACCESS_KEY_ID=..." >&2
      echo "  AWS_SECRET_ACCESS_KEY=..." >&2
      echo "The IAM user needs bedrock:InvokeModel on the Nova models." >&2
      exit 1
    fi
    region="${AUDIOCHOICE_BEDROCK_REGION:-us-east-1}"
    set_value AudioChoice__OpenAI__AnalysisProvider bedrock
    set_value AudioChoice__OpenAI__BedrockRegion "$region"
    # Chosen by testing each available model on the question it would actually be asked,
    # rather than by assuming the price list is a capability ranking. It is not: Nova Pro,
    # the obvious choice for the judgement tiers, called a plainly sustained encounter "not
    # sustained" at 0.5 confidence and returned the transcript back as its own summary. That
    # is what rejected 70 of 70 candidate scenes and left a book with none. Nova 2 Lite, a
    # newer generation and a cheaper tier, answered the same question correctly at 0.95.
    #
    # Nova Premier is not an option: AWS refuses it as a legacy model to any account that was
    # not already using it. Nova Micro is incoherent here -- it called the scene sustained and
    # then neither accepted it nor escalated it, which silently drops it.
    set_value AudioChoice__OpenAI__AnalysisModel "amazon.nova-lite-v1:0"
    set_value AudioChoice__OpenAI__SceneVerificationModel "us.amazon.nova-2-lite-v1:0"
    set_value AudioChoice__OpenAI__SceneEscalationModel "us.amazon.nova-2-lite-v1:0"
    # A distinct scanner version so Nova's results are written as new rows beside the OpenAI
    # ones instead of over them. Without this the only record of what the previous models
    # found is destroyed by the first reanalysis, and there is nothing left to compare against.
    set_value AudioChoice__OpenAI__ScannerVersion "4.0-nova"
    echo "Switched to Bedrock Nova in $region."
    ;;

  openai)
    set_value AudioChoice__OpenAI__AnalysisProvider openai
    set_value AudioChoice__OpenAI__AnalysisModel "gpt-5.6-luna"
    set_value AudioChoice__OpenAI__SceneVerificationModel "gpt-5.6-terra"
    set_value AudioChoice__OpenAI__SceneEscalationModel "gpt-5.6-sol"
    set_value AudioChoice__OpenAI__ScannerVersion "3.5"
    echo "Switched back to OpenAI."
    ;;

  *)
    echo "Usage: scripts/switch-scanner-model.sh [nova|openai|show]" >&2
    exit 1
    ;;
esac

echo
current
echo
echo "Rebuilding the scanner. Whisper is left alone so the GPU model stays loaded."
docker compose -f "$compose" up -d --build scanner

echo
echo "Waiting for the scanner to report which models it is using."
sleep 12
# The startup line names the transport and all three tiers, so this confirms the switch took
# effect in the running container rather than only in a file.
docker compose -f "$compose" logs --tail 200 scanner 2>/dev/null \
  | grep -E "Analysis provider|Transcription provider" | tail -4 \
  || echo "No provider line yet. Check: docker compose -f $compose logs -f scanner"
