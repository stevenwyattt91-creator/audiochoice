#!/usr/bin/env bash
# Compile the Azure templates and check the shapes a deployment will reject.
#
# Bicep compiling proves very little about a Container App. A secret and an environment variable are
# both just objects, so putting one where the other belongs compiles cleanly and then fails at
# deployment with "Env variable '<name>' must have either value or a secretRef" -- after the release is
# already running. That happened: a secrets-shaped block carrying keyVaultUrl was inserted into the env
# array, bicep accepted it, and the deployment failed.
#
# So this checks the distinction the platform enforces:
#   secrets  entries resolve a Key Vault URL and name the identity that may read it
#   env      entries carry a literal value, or name a secret already declared above
#
# Usage: scripts/verify-azure-templates.sh
set -euo pipefail

repository_root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
cd "$repository_root"

if ! command -v az >/dev/null 2>&1; then
  echo "The Azure CLI is required to compile these templates." >&2
  exit 1
fi

failures=0
workspace="$(mktemp -d)"
trap 'rm -rf "$workspace"' EXIT

for template in deploy/azure/*.bicep; do
  name="$(basename "$template")"
  compiled="$workspace/${name%.bicep}.json"

  if ! az bicep build --file "$template" --stdout >"$compiled" 2>"$workspace/err"; then
    echo "  FAIL $name does not compile"
    sed 's/^/       /' "$workspace/err" >&2
    failures=$((failures + 1))
    continue
  fi

  python3 - "$compiled" "$name" <<'PY'
import json, sys

compiled, name = sys.argv[1], sys.argv[2]
template = json.load(open(compiled))
problems = []

def walk(node):
    """Yields every Container App resource, including those nested in modules."""
    if isinstance(node, dict):
        if node.get("type") == "Microsoft.App/containerApps":
            yield node
        for value in node.values():
            yield from walk(value)
    elif isinstance(node, list):
        for value in node:
            yield from walk(value)

apps = list(walk(template))
for app in apps:
    properties = app.get("properties", {})
    secrets = json.dumps(properties.get("configuration", {}).get("secrets", []))
    for container in properties.get("template", {}).get("containers", []):
        env = json.dumps(container.get("env", []))
        # A vault URL in the env array is the exact mistake that fails a deployment.
        if "keyVaultUrl" in env:
            problems.append("an environment variable carries keyVaultUrl; only secrets may")
        if "'identity'" in env or '"identity"' in env:
            problems.append("an environment variable carries identity; only secrets may")
        # And every secret an env var names must actually be declared.
        for marker in ["'secretRef', '", '"secretRef": "']:
            rest = env
            while marker in rest:
                rest = rest.split(marker, 1)[1]
                referenced, rest = rest.split("'", 1) if marker.endswith("'") else rest.split('"', 1)
                if referenced and referenced not in secrets:
                    problems.append(f"env names secret '{referenced}', which is never declared")

if not apps:
    print(f"  ok   {name} compiles (no container app to check)")
elif problems:
    for problem in dict.fromkeys(problems):
        print(f"  FAIL {name}: {problem}")
    sys.exit(1)
else:
    print(f"  ok   {name} compiles, and its secrets and env vars are shaped correctly")
PY
  # shellcheck disable=SC2181
  [ $? -eq 0 ] || failures=$((failures + 1))
done

if [ "$failures" -ne 0 ]; then
  echo "$failures template check(s) failed." >&2
  exit 1
fi
echo "All Azure template checks passed."
