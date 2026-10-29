#!/usr/bin/env bash
# Builds downloadable, self-contained bundles of Shaft Additionality.
# The result needs no .NET installation on the target machine:
# one exe (runtime + OpenAL audio embedded) plus the dist\ asset folder.
#
# Usage:  tools/make_bundle.sh [win-x64|linux-x64|all]     (default: win-x64)
# Output: ShaftAdditionality-<rid>.zip in the repository root.
set -euo pipefail
cd "$(dirname "$0")/.."
REPO=$(pwd)
PUB=${PUB_DIR:-/tmp/publish}
RID="${1:-win-x64}"
export DOTNET_SYSTEM_GLOBALIZATION_INVARIANT=1 TZ=UTC DOTNET_CLI_TELEMETRY_OPTOUT=1
NOWARN='NU1902%3BNU1903%3BNETSDK1188'   # NuGet advisories; semicolons must be %3B on the cmdline

bundle() {
  local rid="$1"
  rm -rf "$PUB/$rid" "$PUB/stage-$rid" "$REPO/ShaftAdditionality-$rid.zip"
  dotnet publish src/Shaft.Game/Shaft.Game.csproj -c Release -r "$rid" --self-contained true \
    -p:PublishSingleFile=true -p:EnableCompressionInSingleFile=true \
    -p:IncludeNativeLibrariesForSelfExtract=false -p:NoWarn="$NOWARN" -o "$PUB/$rid"
  # NOTE: IncludeNativeLibrariesForSelfExtract must stay false. Silk.NET resolves GLFW and
  # OpenAL by probing next to the executable; buried inside the single-file bundle they are
  # invisible and Window.Create dies with "GlfwPlatform - not applicable" (the crash every
  # Windows download hit until this was fixed). Natives ship loose beside the exe.

  # Debug symbols are useless to players.
  rm -f "$PUB/$rid"/*.pdb

  # Sanity: every dist asset must ship loose next to the exe (the game reads them by path).
  local want got
  want=$(cd dist && find . -type f | sort | md5sum)
  got=$(cd "$PUB/$rid/dist" && find . -type f | sort | md5sum)
  [ "$want" = "$got" ] || { echo "ERROR: dist assets missing from $rid publish" >&2; exit 1; }

  local stage="$PUB/stage-$rid/ShaftAdditionality-$rid"
  mkdir -p "$stage"
  cp -r "$PUB/$rid"/. "$stage"/
  cp tools/README-win-x64.txt "$stage/README.txt"
  (cd "$PUB/stage-$rid" && zip -qr9 "$REPO/ShaftAdditionality-$rid.zip" "ShaftAdditionality-$rid")

  # Smoke-test the Linux bundle when built on a Linux x64 machine.
  if [ "$rid" = "linux-x64" ] && [ "$(uname -s)" = "Linux" ] && [ "$(uname -m)" = "x86_64" ]; then
    "$stage/ShaftAdditionality" --selftest | tail -1
  fi
  ls -la "$REPO/ShaftAdditionality-$rid.zip"
}

case "$RID" in
  all) bundle win-x64; bundle linux-x64 ;;
  *)   bundle "$RID" ;;
esac
