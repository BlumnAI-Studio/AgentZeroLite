#!/bin/sh
# Wrap AgentZeroLite.app (from build-app.sh) into a macOS Installer package that installs it
# into /Applications — the Installer.app wizard a person double-clicks.
#
# Usage: build-pkg.sh <AgentZeroLite.app> <out.pkg> [version]
#   e.g. build-pkg.sh publish-avalonia/AgentZeroLite.app publish-avalonia/AgentZeroLite-0.25.8-osx-arm64.pkg
#
# Unsigned unless PKG_SIGN_ID names an installer identity ("Developer ID Installer: …" for direct
# download, "3rd Party Mac Developer Installer: …" for the Mac App Store). Unsigned, Gatekeeper
# refuses a double-click on another Mac: right-click → Open, or System Settings → Privacy & Security.
# Signing the app inside is build-app.sh's job (and the signing notes in
# harness/knowledge/_shared/code-signing.md §7); this script never touches the bundle.
set -eu

app="${1:?AgentZeroLite.app path}"
out="${2:?output .pkg path}"
version="${3:-}"

if [ ! -d "$app/Contents/MacOS" ]; then
  echo "[build-pkg] not an app bundle: $app" >&2
  exit 1
fi
if [ -z "$version" ]; then
  version="$(/usr/libexec/PlistBuddy -c 'Print :CFBundleShortVersionString' "$app/Contents/Info.plist")"
fi
identifier="$(/usr/libexec/PlistBuddy -c 'Print :CFBundleIdentifier' "$app/Contents/Info.plist")"

work="$(mktemp -d)"
trap 'rm -rf "$work"' EXIT
mkdir -p "$work/root" "$work/resources"
cp -R "$app" "$work/root/"

# Not relocatable: without this, Installer "upgrades" any other copy of the bundle id it finds
# (a build folder, a mounted image) instead of writing /Applications/AgentZeroLite.app.
pkgbuild --analyze --root "$work/root" "$work/component.plist" >/dev/null
/usr/libexec/PlistBuddy -c 'Set :0:BundleIsRelocatable false' "$work/component.plist"

pkgbuild --root "$work/root" --component-plist "$work/component.plist" \
  --install-location /Applications --identifier "$identifier" --version "$version" \
  "$work/AgentZeroLite-component.pkg" >/dev/null

cat > "$work/resources/welcome.html" <<EOF
<html><body style="font-family:-apple-system,Helvetica;font-size:13px">
<h2>AgentZero Lite $version</h2>
<p>Installs <b>AgentZero Lite.app</b> into <b>/Applications</b> (Apple Silicon, macOS 12 or later).</p>
<p>Settings and data live under <code>~/Library/Application Support/AgentZeroLite</code> and are kept
when you reinstall or upgrade.</p>
</body></html>
EOF

cat > "$work/distribution.xml" <<EOF
<?xml version="1.0" encoding="utf-8"?>
<installer-gui-script minSpecVersion="2">
  <title>AgentZero Lite $version</title>
  <welcome file="welcome.html" mime-type="text/html"/>
  <options customize="never" require-scripts="false" hostArchitectures="arm64"/>
  <domains enable_localSystem="true"/>
  <volume-check><allowed-os-versions><os-version min="12.0"/></allowed-os-versions></volume-check>
  <choices-outline><line choice="default"><line choice="$identifier"/></line></choices-outline>
  <choice id="default"/>
  <choice id="$identifier" visible="false"><pkg-ref id="$identifier"/></choice>
  <pkg-ref id="$identifier" version="$version" onConclusion="none">AgentZeroLite-component.pkg</pkg-ref>
</installer-gui-script>
EOF

mkdir -p "$(dirname "$out")"
if [ -n "${PKG_SIGN_ID:-}" ]; then
  productbuild --distribution "$work/distribution.xml" --resources "$work/resources" \
    --package-path "$work" --sign "$PKG_SIGN_ID" "$out"
else
  productbuild --distribution "$work/distribution.xml" --resources "$work/resources" \
    --package-path "$work" "$out"
fi
echo "[build-pkg] $out ($identifier $version${PKG_SIGN_ID:+, signed})"
