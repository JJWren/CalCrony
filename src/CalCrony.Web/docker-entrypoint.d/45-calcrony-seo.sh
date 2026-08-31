#!/bin/sh
# Rewrites the crawler files for the deployment's own origin so a self-hosted or test
# instance never advertises the hosted instance's URLs to search engines:
#   WEB_PUBLIC_ORIGIN — the browser-visible origin of THIS deployment
#                       (e.g. https://cal.example.org). Empty = keep the baked default
#                       (https://calcrony.app), which is correct for the hosted instance.
#   ROBOTS_MODE       — "disallow" replaces robots.txt with a disallow-all file (no sitemap
#                       pointer) for deployments that must never be indexed, e.g. test stacks.
set -e

html_root=/usr/share/nginx/html

if [ -n "${WEB_PUBLIC_ORIGIN:-}" ]; then
    origin="${WEB_PUBLIC_ORIGIN%/}"
    # Plain textual substitution; an origin containing sed-special characters (| & \) is
    # not a valid https origin anyway, so no escaping pass is needed.
    sed -i "s|https://calcrony.app|${origin}|g" "$html_root/sitemap.xml" "$html_root/robots.txt"
    echo "CalCrony.Web: sitemap/robots origin set to '${origin}'"
else
    echo "CalCrony.Web: WEB_PUBLIC_ORIGIN not set; sitemap/robots keep the baked origin"
fi

if [ "${ROBOTS_MODE:-}" = "disallow" ]; then
    printf 'User-agent: *\nDisallow: /\n' > "$html_root/robots.txt"
    echo "CalCrony.Web: robots.txt set to disallow-all (ROBOTS_MODE=disallow)"
fi
