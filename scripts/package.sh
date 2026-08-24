#!/usr/bin/env bash
# Build the release ZIP.
#
#   scripts/package.sh            # build, verify, package into artifacts/
#   scripts/package.sh --verify   # build and verify only, no ZIP
#
# Environment:
#   CoveSrc                      # explicit path to <cove>/src/, skipping the sibling probe
#   ALLOW_COVE_VERSION_DRIFT=1   # downgrade the minCoveVersion check below to a warning
#
# The layout follows Cove's packaging docs: {extensionId}.zip with extension.json at the archive
# ROOT (the "portable layout"), which is the only one the registry accepts. Everything the host
# already provides is stripped by Cove.Sdk.targets; the checks below fail the build if any of it
# comes back, because a duplicate Cove.Core/Cove.Plugins splits type identity across load contexts
# and fails only at runtime.

set -euo pipefail

cd "$(dirname "${BASH_SOURCE[0]}")/.."
ROOT=$(pwd)
# Sources live under src/, tests beside them at the root — the layout the extension template
# uses. Everything below is relative to $ROOT, so only these two lines know where.
PROJECT="$ROOT/src/Cove.TorrentMetadata"
OUT="$ROOT/artifacts/extension"
VERIFY_ONLY=${1:-}

MANIFEST="$PROJECT/extension.json"
ID=$(python3 -c "import json,sys;print(json.load(open(sys.argv[1]))['id'])" "$MANIFEST")
VERSION=$(python3 -c "import json,sys;print(json.load(open(sys.argv[1]))['version'])" "$MANIFEST")
MIN_COVE=$(python3 -c "import json,sys;print(json.load(open(sys.argv[1])).get('minCoveVersion') or '')" "$MANIFEST")

echo "==> $ID $VERSION (minCoveVersion: ${MIN_COVE:-UNSET})"
[ -z "$MIN_COVE" ] && { echo "FAIL: extension.json has no minCoveVersion — it is the compatibility contract"; exit 1; }

# ---------------------------------------------------------------------------
# The artifact must be built against the Cove it claims to support.
#
# minCoveVersion is a promise the host checks at load time, but nothing used to keep it honest at
# build time: the release workflow cloned whatever `main` was that morning, so two runs of the same
# extension tag could compile against different host sources, and neither had to match the number
# in extension.json. Resolve the checkout the way Directory.Build.props does, then recompute its
# version by Cove's own rule (its Directory.Build.targets: an exact stable tag, else
# "<next patch>-dev.<commits since the latest stable tag>").
# ---------------------------------------------------------------------------
# The candidate order is Directory.Build.props's, and has to stay it: `cove-131` is the pinned floor
# in its own worktree, `cove` is whatever the day's reading of upstream/main left behind. Probing
# `cove` first would package against an untested host that the version check below would then have
# to catch — it does, but a gate should not be the first thing to notice.
COVE_SRC=${CoveSrc:-}
if [ -z "$COVE_SRC" ]; then
  for candidate in "$ROOT/../cove-131/src" "$ROOT/../cove/src" "$ROOT/../../src"; do
    [ -f "$candidate/Cove.Sdk/Cove.Sdk.csproj" ] && { COVE_SRC=$candidate; break; }
  done
fi
[ -n "$COVE_SRC" ] || {
  echo "FAIL: no Cove checkout found. Put one in a sibling directory named 'cove-131' (the pinned"
  echo "      floor) or 'cove', or pass CoveSrc=<path-to>/cove/src/ (see Directory.Build.props)."
  exit 1
}

# Runs in a subshell so the cd does not leak.
cove_version() (
  cd "$1"
  exact=$(git describe --tags --exact-match --match "v[0-9]*.[0-9]*.[0-9]*" 2>/dev/null || true)
  case "$exact" in
    ""|*-dev.*) ;;                       # untagged, or a -dev tag Cove itself rejects
    v*) echo "${exact#v}"; return 0 ;;
  esac
  base=$(git describe --tags --abbrev=0 --match "v[0-9]*.[0-9]*.[0-9]*" --exclude "*-*" 2>/dev/null) || return 1
  IFS=. read -r major minor patch <<<"${base#v}"
  echo "$major.$minor.$((patch + 1))-dev.$(git rev-list --count "$base..HEAD")"
)

COVE_VERSION=$(cove_version "$COVE_SRC") || {
  echo "FAIL: cannot compute the Cove version at $COVE_SRC — it needs full history and tags,"
  echo "      and a shallow 'git clone --depth 1' has neither."
  exit 1
}
echo "==> Cove $COVE_VERSION ($COVE_SRC)"

if [ "$COVE_VERSION" != "$MIN_COVE" ]; then
  msg="built against Cove $COVE_VERSION, but extension.json declares minCoveVersion $MIN_COVE"
  if [ "${ALLOW_COVE_VERSION_DRIFT:-}" = "1" ]; then
    echo "  WARN: $msg (ALLOW_COVE_VERSION_DRIFT=1)"
  else
    echo "  FAIL: $msg"
    echo "        Check out that Cove revision, or bump minCoveVersion — and the pin in"
    echo "        .github/actions/cove-checkout/action.yml — once tested against this one."
    exit 1
  fi
fi

# ---------------------------------------------------------------------------
# Build. The UI bundle is not optional in a release: a stale dist-ui would ship
# silently, so build it explicitly rather than relying on the csproj's opportunistic target.
# ---------------------------------------------------------------------------
if [ "${SKIP_BUILD:-}" = "1" ]; then
  echo "==> SKIP_BUILD=1, verifying whatever is already in artifacts/extension"
else

echo "==> frontend"
[ -d "$PROJECT/ui/node_modules" ] || (cd "$PROJECT/ui" && npm ci)
(cd "$PROJECT/ui" && npm run build)

echo "==> publish"
rm -rf "$ROOT/artifacts"
# grep's exit status (1 = no error/warning lines matched) is expected and must not trip
# pipefail; but the publish's own exit status must still be caught, or a compile failure
# is swallowed here and only resurfaces later as a misleading "extension.json not at
# archive root". set +e/-e brackets just this pipeline so PIPESTATUS[0] survives intact.
set +e
dotnet publish "$PROJECT/Cove.TorrentMetadata.csproj" -c Release -o "$OUT" --nologo | grep -E "error|warning CS"
publish_status=${PIPESTATUS[0]}
set -e
[ "$publish_status" = 0 ] || { echo "FAIL: dotnet publish exited $publish_status"; exit 1; }

fi

# ---------------------------------------------------------------------------
# Verify
# ---------------------------------------------------------------------------
fail=0
note() { echo "  FAIL: $1"; fail=1; }

echo "==> checking the package contents"

[ -f "$OUT/extension.json" ] || note "extension.json is not at the archive root"
[ -f "$OUT/$(python3 -c "import json,sys;print(json.load(open(sys.argv[1]))['entryDll'])" "$MANIFEST")" ] \
  || note "entryDll named in extension.json is missing from the output"
[ -f "$OUT/Cove.TorrentMetadata.deps.json" ] || note ".deps.json must ship next to the entry DLL"
[ -f "$OUT/ui/main.js" ] || note "the UI bundle is missing"

# Host-provided assemblies. Shipping any of these is the type-identity trap the SDK exists to
# prevent; the SDK's targets strip them, so a hit here means the targets stopped being applied.
while read -r f; do
  case "$(basename "$f")" in
    Cove.Core.dll|Cove.Plugins.dll|Cove.Sdk.dll|Cove.Data.dll|Microsoft.EntityFrameworkCore*.dll|Npgsql*.dll|Pgvector*.dll|MediatR*.dll)
      note "host-provided assembly in the package: $(basename "$f")" ;;
  esac
done < <(find "$OUT" -name '*.dll')

# Symbols never ship: the PDB records the source root, and the DLL's debug directory records an
# absolute path to the PDB. Both leak the build machine's home directory.
#
# Process substitution, not `find ... | while ...`: the latter runs the loop body in a
# subshell, so `note`'s `fail=1` never reaches the outer shell and this check could never
# actually fail the build no matter what it found.
while IFS= read -r -d '' f; do note "symbols in the package: $(basename "$f")"; done \
  < <(find "$OUT" -name '*.pdb' -print0)

# Identity. The publication handle is allowed — it is part of the extension id in extension.json and
# of the cover client's name, which is a deliberate, documented decision. What must never ship is the
# author's real identity or a build-machine path.
#
# The patterns are derived at run time — the home prefix, the OS user, and the identity git would
# stamp on a commit — rather than hard-coded, so this script carries no name that must not travel:
# a literal name list here would itself be the leak it exists to catch.
command -v strings >/dev/null 2>&1 || { echo "FAIL: 'strings' (binutils) is required for the identity-leak scan"; exit 1; }
idwords="$(id -un)"
for word in $(git config user.name 2>/dev/null) $(git config user.email 2>/dev/null | cut -d@ -f1 | tr '.+_-' '    '); do
  case "$word" in *[!a-zA-Z0-9]*) : ;; ??*) idwords="$idwords|$word" ;; esac
done
while IFS= read -r -d '' f; do
  if strings -a "$f" | grep -qiE "/home/|$idwords"; then
    note "identity leak in $(basename "$f"): $(strings -a "$f" | grep -oiE "/home/[^ \"]*|$idwords" | sort -u | head -3 | tr '\n' ' ')"
  fi
done < <(find "$OUT" -type f -print0)

[ "$fail" = 0 ] || { echo "==> NOT PACKAGED"; exit 1; }
echo "==> all checks passed"

[ "$VERIFY_ONLY" = "--verify" ] && { echo "==> --verify, stopping before the ZIP"; exit 0; }

# ---------------------------------------------------------------------------
# Package — zip from INSIDE the publish directory so extension.json is at the root
# ---------------------------------------------------------------------------
ZIP="$ROOT/artifacts/$ID-$VERSION.zip"
(cd "$OUT" && zip -qr "$ZIP" .)

echo "==> $ZIP"
python3 -m zipfile --list "$ZIP"
