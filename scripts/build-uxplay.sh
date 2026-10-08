#!/usr/bin/env bash
# Baut den AirPlay-Empfaenger UxPlay fuer Bromirror (Windows, MSYS2 UCRT64).
# Ausfuehren aus dem Repo-Root:  bash scripts/build-uxplay.sh
set -euo pipefail

UXPLAY_REPO="https://github.com/FDH2/UxPlay.git"
UXPLAY_COMMIT="3dbf7ceee65932154e85a2f83963d53520a799fa"   # getesteter Stand (UxPlay 1.74)
ROOT="$(cd "$(dirname "$0")/.." && pwd)"
export PATH="/c/msys64/ucrt64/bin:/c/msys64/usr/bin:$PATH"

echo "[1/4] Abhaengigkeiten (MSYS2 UCRT64) ..."
pacman -S --needed --noconfirm \
  mingw-w64-ucrt-x86_64-cmake mingw-w64-ucrt-x86_64-gcc mingw-w64-ucrt-x86_64-ninja \
  mingw-w64-ucrt-x86_64-pkgconf mingw-w64-ucrt-x86_64-libplist mingw-w64-ucrt-x86_64-openssl \
  mingw-w64-ucrt-x86_64-gstreamer mingw-w64-ucrt-x86_64-gst-plugins-base \
  mingw-w64-ucrt-x86_64-gst-plugins-good mingw-w64-ucrt-x86_64-gst-plugins-bad \
  mingw-w64-ucrt-x86_64-gst-libav

echo "[2/4] UxPlay holen ..."
if [ ! -d "$ROOT/UxPlay/.git" ]; then
  git clone "$UXPLAY_REPO" "$ROOT/UxPlay"
fi
git -C "$ROOT/UxPlay" fetch --quiet origin "$UXPLAY_COMMIT" || true
git -C "$ROOT/UxPlay" checkout --quiet --force "$UXPLAY_COMMIT"

echo "[3/4] Bromirror-Patch anwenden ..."
git -C "$ROOT/UxPlay" apply "$ROOT/patches/uxplay-unbuffered-stdout.patch"

echo "[4/4] Bauen ..."
cmake -S "$ROOT/UxPlay" -B "$ROOT/UxPlay/build" -G Ninja -DCMAKE_BUILD_TYPE=Release
ninja -C "$ROOT/UxPlay/build"

echo "Fertig: $ROOT/UxPlay/build/uxplay.exe"
