# CalCrony runs a first-party hosted instance while staying self-hostable

CalCrony will be operated as a public, first-party hosted instance (listed on top.gg, invitable by
any server) by the project maintainer, while the self-hosting path remains fully supported. The two
deployment modes serve different audiences: the hosted instance is the zero-friction default for
server admins who just want the bot, and self-hosting is the privacy path for communities that want
their event and attendee data on their own infrastructure. Nothing in the codebase may assume it is
running as the hosted instance — every hosted-instance concern (canonical domain, policy pages,
listing integrations) must stay configurable so a self-hoster gets a complete product without them.

## Considered Options

- **First-party hosted + self-hostable (chosen).** The maintainer takes on operator obligations:
  24/7 uptime, a canonical domain, a privacy policy and terms of service, Discord bot verification
  once the ~75-server threshold approaches, and support for other people's communities. In exchange
  CalCrony becomes adoptable by non-technical server admins, which self-hosting alone never allows.
- **Self-hosted only.** Rejected: caps adoption at people willing to run infrastructure, and makes a
  top.gg presence pointless since there is no invite link to offer.
- **Hosted only.** Rejected: gives up the privacy story that differentiates CalCrony from sesh —
  self-hosters keep their data entirely off third-party infrastructure.

## Consequences

- Features previously judged N/A for a self-hosted project (top.gg votepoints, vote webhooks) are
  back in scope for the hosted instance, but must be optional/config-gated so self-hosted
  deployments are unaffected.
- The hosted instance stores other communities' event and attendee data, so the project needs a
  real privacy policy and ToS — both as public web pages (required by top.gg listing and Discord
  verification) and as honest descriptions of what the software stores.
- Origin/domain configuration (see the sitemap/robots origin templating) must keep treating the
  canonical domain as deployment config, never a hardcoded value.
- Operational features matter more than they did: admin action logs, data export, and reliable
  notification delivery move up the priority list because a hosted operator answers for them.
