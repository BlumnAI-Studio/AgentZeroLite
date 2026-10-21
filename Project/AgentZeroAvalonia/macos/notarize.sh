#!/bin/sh
# Notarize and staple a Developer ID signed .pkg (from build-pkg.sh with PKG_SIGN_ID) or .app
# (from build-app.sh with SIGN_ID). A .pkg is submitted as is and covers the app inside it;
# a .app is zipped for the submission and the ticket is stapled to the bundle.
#
# Usage: notarize.sh <AgentZeroLite.pkg | AgentZeroLite.app>
#
# Credentials, one of:
#   NOTARY_PROFILE  a notarytool keychain profile, stored once with
#                   xcrun notarytool store-credentials <name> --apple-id <id> --team-id <TEAMID>
#   NOTARY_KEY + NOTARY_KEY_ID + NOTARY_ISSUER
#                   an App Store Connect API key (.p8 path, key id, issuer id) — what CI uses
set -eu

target="${1:?.pkg or .app path}"

if [ -n "${NOTARY_PROFILE:-}" ]; then
  set -- --keychain-profile "$NOTARY_PROFILE"
elif [ -n "${NOTARY_KEY:-}" ]; then
  set -- --key "$NOTARY_KEY" --key-id "${NOTARY_KEY_ID:?}" --issuer "${NOTARY_ISSUER:?}"
else
  echo "[notarize] set NOTARY_PROFILE, or NOTARY_KEY + NOTARY_KEY_ID + NOTARY_ISSUER" >&2
  exit 1
fi

case "$target" in
  *.pkg)
    pkgutil --check-signature "$target"
    xcrun notarytool submit "$target" "$@" --wait
    ;;
  *.app)
    zip="$(mktemp -d)/$(basename "$target" .app).zip"
    ditto -c -k --keepParent "$target" "$zip"
    xcrun notarytool submit "$zip" "$@" --wait
    rm -f "$zip"
    ;;
  *)
    echo "[notarize] expected a .pkg or .app: $target" >&2
    exit 1
    ;;
esac

# `submit --wait` exits 0 on "Invalid" too; staple fails when there is no ticket, which is the check.
xcrun stapler staple "$target"
xcrun stapler validate "$target"
case "$target" in
  *.pkg) spctl --assess --type install -vv "$target" ;;
  *)     spctl --assess --type execute -vv "$target" ;;
esac
echo "[notarize] notarized and stapled: $target"
