#!/bin/sh
# Writes the SPA's runtime config from the container environment so per-deployment values
# can change without rebuilding the image:
#   API_BASE_URL   — the browser-visible URL of CalCrony.Api (never the compose-internal
#                    service name; the browser makes the calls, not this container).
#   DISCORD_APP_ID — the Discord application id the invite links advertise. Set this in a
#                    TEST environment so its web app invites the test bot, not production's
#                    (empty = the production id baked into the app).
#   DONATE_URL     — the operator's tip-jar URL for the footer donate link. Empty = no link
#                    renders (test stacks and self-hosted deployments stay clean by default).
set -e

if [ -n "${API_BASE_URL:-}" ] || [ -n "${DISCORD_APP_ID:-}" ] || [ -n "${DONATE_URL:-}" ]; then
    # Escape backslashes and double quotes — the common .env mishaps. Exotic control
    # characters in a value could still break the JSON; keep values simple.
    escape() { printf '%s' "$1" | sed -e 's/\\/\\\\/g' -e 's/"/\\"/g'; }
    printf '{\n  "Api": {\n    "BaseUrl": "%s"\n  },\n  "Discord": {\n    "AppId": "%s"\n  },\n  "Donations": {\n    "BuyMeACoffeeUrl": "%s"\n  }\n}\n' \
        "$(escape "${API_BASE_URL:-}")" "$(escape "${DISCORD_APP_ID:-}")" "$(escape "${DONATE_URL:-}")" \
        > /usr/share/nginx/html/appsettings.json
    echo "CalCrony.Web: Api:BaseUrl set to '${API_BASE_URL:-}'; Discord:AppId set to '${DISCORD_APP_ID:-<production default>}'; Donations:BuyMeACoffeeUrl set to '${DONATE_URL:-<none>}'"
else
    echo "CalCrony.Web: API_BASE_URL/DISCORD_APP_ID/DONATE_URL not set; using the appsettings.json baked into the image"
fi
