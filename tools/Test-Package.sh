#!/bin/sh
# Unpacks a release download the way a person does and starts EasyVocal from it (CI runs this on the archives that
# tools/Publish-Release.ps1 builds, on a real macOS / Linux runner):
#   Linux: tar xzf, the program and install.sh executable, every native library's dependencies there, ./install.sh
#          into a pretend home, then the installed program runs --smoketest under Xvfb.
#   macOS: unzip with ditto (what a double-click does), the program executable, the README's one-time ad-hoc
#          codesign, then Contents/MacOS/EasyVocal runs --smoketest.
# usage: tools/Test-Package.sh <EasyVocal-linux-x64.tar.gz | EasyVocal-macos-*.zip> <empty work folder>
set -u
archive=$1
work=$2
fail() { echo "FAIL: $*"; exit 1; }
rm -rf "$work"
mkdir -p "$work/unpacked" "$work/home" "$work/data" || fail "can't make $work"
archive=$(cd "$(dirname "$archive")" && pwd)/$(basename "$archive")

case "$archive" in
*.tar.gz)
    tar xzf "$archive" -C "$work/unpacked" || fail "tar xzf"
    folder="$work/unpacked/EasyVocal"
    ls -l "$folder"
    [ -x "$folder/EasyVocal" ] || fail "EasyVocal/EasyVocal is not executable after tar xzf"
    [ -x "$folder/install.sh" ] || fail "EasyVocal/install.sh is not executable after tar xzf"
    head -c 4 "$folder/EasyVocal" | od -c | head -1
    missing=0
    for lib in "$folder/EasyVocal" "$folder"/*.so; do
        [ -e "$lib" ] || continue
        if ldd "$lib" 2>&1 | grep -q "not found"; then
            echo "missing libraries for $(basename "$lib"):"
            ldd "$lib" | grep "not found"
            missing=1
        fi
    done
    [ "$missing" = 0 ] || fail "native libraries miss system libraries"
    (cd "$folder" && HOME="$work/home" XDG_DATA_HOME="" ./install.sh) || fail "./install.sh"
    installed="$work/home/.local/share/EasyVocal"
    [ -x "$installed/EasyVocal" ] || fail "install.sh: no executable $installed/EasyVocal"
    entry="$work/home/.local/share/applications/easyvocal.desktop"
    [ -f "$entry" ] || fail "install.sh: no menu entry"
    grep -qx "Exec=\"$installed/EasyVocal\"" "$entry" || { cat "$entry"; fail "install.sh: the menu entry's Exec line"; }
    [ -f "$work/home/.local/share/icons/hicolor/256x256/apps/easyvocal.png" ] || fail "install.sh: no icon"
    program="$installed/EasyVocal"
    runner="xvfb-run -a"
    ;;
*.zip)
    ditto -x -k "$archive" "$work/unpacked" || fail "ditto -x -k"
    app="$work/unpacked/EasyVocal.app"
    ls -l "$app/Contents/MacOS"
    [ -x "$app/Contents/MacOS/EasyVocal" ] || fail "EasyVocal.app/Contents/MacOS/EasyVocal is not executable after unzipping"
    [ -f "$app/Contents/Info.plist" ] || fail "no Info.plist"
    plutil -lint "$app/Contents/Info.plist" || fail "Info.plist"
    file "$app/Contents/MacOS/EasyVocal"
    for lib in "$app/Contents/MacOS"/*.dylib; do
        [ -e "$lib" ] || continue
        file "$lib"
    done
    # The README's one-time step (the .app is unsigned; Apple silicon refuses unsigned code).
    codesign --force --deep --sign - "$app" || fail "codesign --force --deep --sign -"
    codesign --verify --deep "$app" || fail "codesign --verify"
    program="$app/Contents/MacOS/EasyVocal"
    runner=""
    ;;
*) fail "unknown archive $archive" ;;
esac

echo "== $program --data $work/data --smoketest"
# perl's alarm: macOS has no timeout(1). A smoke test takes well under a minute.
HOME="$work/home" perl -e 'alarm shift; exec @ARGV or die "exec: $!"' 300 $runner "$program" --data "$work/data" --smoketest
code=$?
echo "exit code $code"
if [ -f "$work/data/easyvocal.log" ]; then
    echo "== easyvocal.log"
    cat "$work/data/easyvocal.log"
fi
ls "$work/data"
[ "$code" = 0 ] || fail "the smoke test of the unpacked download exited with $code"
ls "$work/data"/smoke_*.png >/dev/null 2>&1 || fail "the smoke test saved no pictures"
echo "PASS: $(basename "$archive") unpacks, installs and starts"

# The end-to-end test of the unpacked program: pretend devices, the real windows, a quit and a relaunch.
mkdir -p "$work/e2e" || fail "can't make $work/e2e"
echo "== $program --data $work/e2e --e2e"
HOME="$work/home" perl -e 'alarm shift; exec @ARGV or die "exec: $!"' 400 $runner "$program" --data "$work/e2e" --e2e
code=$?
echo "exit code $code"
[ -f "$work/e2e/e2e.log" ] && cat "$work/e2e/e2e.log"
[ "$code" = 0 ] || { [ -f "$work/e2e/easyvocal.log" ] && tail -n 80 "$work/e2e/easyvocal.log"; fail "the e2e test of the unpacked download exited with $code"; }
echo "PASS: $(basename "$archive") passes the e2e test"
