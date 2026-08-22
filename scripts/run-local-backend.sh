#!/bin/zsh
set -euo pipefail

script_dir="${0:A:h}"
project_dir="${script_dir:h}"

export DOTNET_ROOT="/opt/homebrew/opt/dotnet@8/libexec"
export PATH="/opt/homebrew/opt/dotnet@8/bin:/opt/homebrew/bin:$PATH"
export DOTNET_CLI_HOME="$project_dir/work/dotnet8-home"
export NUGET_PACKAGES="$project_dir/work/nuget8-packages"
export DOTNET_SKIP_FIRST_TIME_EXPERIENCE=1

if [[ ! -x "$DOTNET_ROOT/dotnet" ]]; then
  print "AudioChoice needs .NET 8. Run: brew install dotnet@8"
  exit 1
fi

read -rs "openai_key?Paste your OpenAI API key (it will stay hidden): "
print
if [[ -z "$openai_key" ]]; then
  print "No API key was entered. Nothing was started."
  exit 1
fi

api_token="$(openssl rand -hex 24)"
machine_name="$(scutil --get LocalHostName 2>/dev/null || hostname -s)"

export AudioChoice__ApiToken="$api_token"
export AudioChoice__MaximumUploadBytes="52428800"
export AudioChoice__OpenAI__ApiKey="$openai_key"
export AudioChoice__OpenAI__WorkerEnabled="true"
export AudioChoice__OpenAI__MaximumChunksPerJob="3"
export ASPNETCORE_URLS="http://0.0.0.0:5080"

unset openai_key

print
print "AudioChoice is starting. Keep this Terminal window open."
print "iPhone server address: http://${machine_name}.local:5080"
print "iPhone access token:  $api_token"
print
print "Safety mode: uploads are capped at 50 MB and scans at 3 chunks."
print

cd "$project_dir"
exec "$DOTNET_ROOT/dotnet" run \
  --project backend/AudioChoice.Api/AudioChoice.Api.csproj \
  --no-restore
