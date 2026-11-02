#!/usr/bin/env bash
# Builds a release package for one platform:  build/package.sh <linux-x64|win-x64> [outdir]
#   - publishes the GUI (MELEAutoModInstaller) and CLI (mele) as self-contained single files
#   - bundles 7-Zip's console build in ./7zip (downloaded from the official release, SHA-256 pinned)
#   - writes mele-auto-mod-installer-<version>-<rid>.tar.gz (linux) or .zip (windows)
# Set MELE_SKIP_7ZIP=1 to skip the 7-Zip download (local smoke builds only: such a package relies on 7-Zip being on PATH).
set -euo pipefail

rid="${1:?usage: build/package.sh <linux-x64|win-x64> [outdir]}"
out="${2:-dist}"
root="$(cd "$(dirname "$0")/.." && pwd)"
cd "$root"
version="$(sed -n 's|.*<Version>\(.*\)</Version>.*|\1|p' Directory.Build.props | head -1)"
name="mele-auto-mod-installer-$version-$rid"
pkg="$out/$name"

# Official 7-Zip release (https://github.com/ip7z/7zip). Update the version and BOTH hashes together.
SEVENZ_VERSION="26.05"; SEVENZ_TAG="2605"
case "$rid" in
  linux-x64) sevenz_file="7z${SEVENZ_TAG}-linux-x64.tar.xz"; sevenz_sha="22cf82fcc6eb7474f1d5bb4f02c0f8ebd573a718089d6833dc30f8350a3b232e" ;;
  win-x64)   sevenz_file="7z${SEVENZ_TAG}-x64.exe";          sevenz_sha="fd99e86f57d8f429ef5ed045bf98315af962e2309b0f1ba87a147a44c71647d0" ;;
  *) echo "unsupported runtime '$rid' (use linux-x64 or win-x64)" >&2; exit 2 ;;
esac

rm -rf "$pkg" "$out/$name".tar.gz "$out/$name".zip
mkdir -p "$pkg/7zip"

echo "==> publishing ($rid)"
dotnet publish src/Mele.App -c Release -r "$rid" -o "$pkg" --nologo -v quiet
dotnet publish src/Mele.Cli -c Release -r "$rid" -o "$pkg" --nologo -v quiet

if [ "${MELE_SKIP_7ZIP:-}" != "1" ]; then
  echo "==> bundling 7-Zip $SEVENZ_VERSION"
  tmp="$(mktemp -d)"; trap 'rm -rf "$tmp"' EXIT
  curl -fsSL -o "$tmp/$sevenz_file" "https://github.com/ip7z/7zip/releases/download/$SEVENZ_VERSION/$sevenz_file"
  echo "$sevenz_sha  $tmp/$sevenz_file" | sha256sum -c - >/dev/null || { echo "7-Zip download does not match the pinned SHA-256; refusing to package it" >&2; exit 1; }
  mkdir -p "$tmp/x"
  case "$rid" in
    linux-x64) tar -xJf "$tmp/$sevenz_file" -C "$tmp/x" ;;
    win-x64)   7z x -y -o"$tmp/x" "$tmp/$sevenz_file" >/dev/null ;;   # the installer is a 7z-readable archive
  esac
  for f in $( [ "$rid" = linux-x64 ] && echo "7zz" || echo "7z.exe 7z.dll" ) License.txt; do
    found="$(find "$tmp/x" -name "$f" -print -quit)"
    [ -n "$found" ] || { echo "'$f' not found inside $sevenz_file" >&2; exit 1; }
    cp "$found" "$pkg/7zip/$f"
  done
  [ "$rid" = linux-x64 ] && chmod +x "$pkg/7zip/7zz"
fi

find "$pkg" -name "*.pdb" -delete   # native dependencies ship 100+ MB of debug symbols that users never need
cp README.md "$pkg/"
cp packaging/THIRD-PARTY.txt "$pkg/"

echo "==> archiving"
if [ "$rid" = linux-x64 ]; then tar -C "$out" -czf "$out/$name.tar.gz" "$name"; echo "$out/$name.tar.gz"
else
  if command -v zip >/dev/null; then (cd "$out" && zip -qr "$name.zip" "$name")
  elif command -v 7z >/dev/null; then (cd "$out" && 7z a -tzip -bso0 -bsp0 "$name.zip" "$name")
  else (cd "$out" && python3 -m zipfile -c "$name.zip" "$name"); fi
  echo "$out/$name.zip"
fi
