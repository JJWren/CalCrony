# Security headers for every response. The .inc extension keeps this file out of
# nginx's conf.d/*.conf auto-include glob; nginx.conf pulls it in explicitly — at
# server level AND inside every location with its own add_header (nginx's add_header
# in a nested location silently discards all inherited headers).
#
# CSP notes:
# - 'wasm-unsafe-eval' is required by the .NET (Blazor) WASM runtime.
# - style-src 'unsafe-inline' covers Blazor's and Bootstrap's inline style attributes.
# - img-src allows https: — Discord CDN avatars/guild icons and event image URLs are
#   arbitrary external hosts. Images-only loosening; scripts/styles/connects stay strict.
# - connect-src 'self' is rewritten at container start by 50-calcrony-csp.sh to also
#   allow the cross-origin API (API_BASE_URL).
add_header Content-Security-Policy "default-src 'self'; script-src 'self' 'wasm-unsafe-eval'; style-src 'self' 'unsafe-inline'; img-src 'self' data: https:; font-src 'self'; connect-src 'self'; base-uri 'self'; form-action 'self'; frame-ancestors 'none'; object-src 'none'" always;
add_header X-Content-Type-Options "nosniff" always;
add_header Referrer-Policy "strict-origin-when-cross-origin" always;
add_header Permissions-Policy "camera=(), microphone=(), geolocation=()" always;
add_header X-Frame-Options "DENY" always;
