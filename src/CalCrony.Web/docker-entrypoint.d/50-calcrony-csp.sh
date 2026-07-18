#!/bin/sh
# The SPA and API run on different origins, so the CSP's connect-src must allow the
# browser-visible API origin. Injected at container start (same pattern as
# 40-calcrony-appsettings.sh) so the CSP tracks API_BASE_URL without an image rebuild.
set -e

HEADERS_FILE=/etc/nginx/conf.d/calcrony-security-headers.inc

if [ -n "${API_BASE_URL:-}" ]; then
    # Reduce to an origin (scheme://host[:port]) — CSP source expressions are origins,
    # not URLs. POSIX parameter expansion only.
    case "$API_BASE_URL" in
        *://*)
            scheme=${API_BASE_URL%%://*}
            rest=${API_BASE_URL#*://}
            origin="${scheme}://${rest%%[/?#]*}"
            ;;
        *)
            origin=${API_BASE_URL%%[/?#]*}
            ;;
    esac
    # Escape sed-replacement metacharacters so an odd value can't mangle the file.
    escaped=$(printf '%s' "$origin" | sed 's/[&\\|]/\\&/g')
    # Replace the whole connect-src clause so re-running on restart is idempotent.
    sed -i "s|connect-src [^;]*|connect-src 'self' ${escaped}|" "$HEADERS_FILE"
    echo "CalCrony.Web: CSP connect-src set to 'self' $origin"
else
    echo "CalCrony.Web: API_BASE_URL not set; CSP connect-src stays same-origin"
fi
