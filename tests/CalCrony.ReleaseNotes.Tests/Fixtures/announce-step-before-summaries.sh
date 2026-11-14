if [ -z "$WEBHOOK_URL" ]; then
  echo "DISCORD_RELEASE_WEBHOOK secret not set; skipping announcement."
  exit 0
fi
# Role id of @Release Ping in the community server; ids are public, the webhook URL
# is the secret. allowed_mentions pins the ping to exactly this role.
role_id="1543943300132769802"
jq -n \
  --arg content "<@&${role_id}> **CalCrony ${RELEASE_TAG}** just shipped — release notes: ${RELEASE_URL}" \
  --arg role "$role_id" \
  '{content: $content, allowed_mentions: {roles: [$role]}}' \
| curl -sf -X POST -H "Content-Type: application/json" -d @- "$WEBHOOK_URL"
