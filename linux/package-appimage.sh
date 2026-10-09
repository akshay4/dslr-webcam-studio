#!/usr/bin/env bash
# Builds DSLR_Webcam_Studio-<version>-x86_64.AppImage (bundles GTK and libusb so it runs on most distros).
# Run on an older distro (e.g. Ubuntu 22.04) for the widest glibc compatibility.
set -euo pipefail
cd "$(dirname "$0")"
VERSION=$(cat ../VERSION)

make clean && make
./dslr-webcam-studio --selftest

rm -rf AppDir && make install DESTDIR="$PWD/AppDir"

TOOLS=${TOOLS:-$PWD/.tools}
mkdir -p "$TOOLS"
fetch() { [ -x "$TOOLS/$1" ] || { curl -fsSL -o "$TOOLS/$1" "$2"; chmod +x "$TOOLS/$1"; }; }
fetch linuxdeploy-x86_64.AppImage https://github.com/linuxdeploy/linuxdeploy/releases/download/continuous/linuxdeploy-x86_64.AppImage
fetch linuxdeploy-plugin-gtk.sh https://raw.githubusercontent.com/linuxdeploy/linuxdeploy-plugin-gtk/master/linuxdeploy-plugin-gtk.sh

export PATH="$TOOLS:$PATH"
export APPIMAGE_EXTRACT_AND_RUN=1 DEPLOY_GTK_VERSION=3 LINUXDEPLOY_OUTPUT_VERSION="$VERSION"
"$TOOLS/linuxdeploy-x86_64.AppImage" --appdir AppDir --plugin gtk --output appimage \
    --desktop-file AppDir/usr/share/applications/dslr-webcam-studio.desktop \
    --icon-file AppDir/usr/share/icons/hicolor/256x256/apps/dslr-webcam-studio.png

mv DSLR_Webcam_Studio-*x86_64.AppImage "DSLR_Webcam_Studio-$VERSION-x86_64.AppImage" 2>/dev/null || true
ls -la ./*.AppImage
