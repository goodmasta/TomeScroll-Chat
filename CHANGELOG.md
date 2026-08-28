# Changelog

Starts at 0.2.0.0 - earlier versions aren't retroactively documented here, but
nothing about them is lost: every past commit stays in git history, and every
past release (`v0.1.1.0` included) stays up on GitHub exactly as it was.

## [0.2.0.0] - 2026-08-22

### Cross-DC chat (new)

A relay-based chat channel for reaching players on a different data center,
where native `/tell` can't. Off by default (Settings → Cross-DC).

- **1:1 pairing** - create/redeem a one-time invite code shared out-of-band
  (voice, Discord, ...; never sent through this relay or native chat), then
  message that contact directly. Messages are end-to-end encrypted
  (X25519 key exchange + XChaCha20-Poly1305) - the relay server only ever
  sees ciphertext.
- **Groups** ("relay linkshells") - create/invite/join a multi-member
  encrypted chat, with owner/moderator roles, kick, ownership transfer, and
  automatic key rotation on kick/leave so a departed member loses access to
  future messages.
- Both 1:1 chats and groups show up as **real tabs**, auto-created the
  moment a pairing/membership completes, self-healing if closed - not
  confined to the Settings window.
- Contact/member names show the other side's actual character name
  (auto-announced, refreshable by hand) instead of a raw relay ID; cross-DC
  tabs get a `[CD]` sidebar badge, groups get `[GRP]`.
- **Unpair**/**Block**/**Unblock** for 1:1 contacts.
- Optional admin tooling (claim admin, view server logs, connected-client
  count) for whoever runs the relay.

### Emotes

- **Custom BTTV/7TV channels**: add additional channels (by numeric Twitch
  ID) beyond the always-loaded global emote sets, from Settings → Emotes.
- **Cross-DC group emote sync**: a per-group toggle shares your configured
  channel list with that group's members (and theirs with you) - additive
  only, never removes a channel you already have.
- The full emote list now refreshes automatically every time the plugin
  starts, instead of only after a 24-hour cache expired.

### Novice Chat

- The built-in Novice Chat tab is now only created for characters actually
  eligible for the Novice Network (matches the game's own check) - no more
  tab for a channel you can't use.
- Both new and already-existing Novice Chat tabs get a 🌱 sprout icon
  automatically.

### Fixes

- Native chat could become permanently un-recoverable after typing `/` once -
  toggling "Hide native chat" back off no longer brought it back.
- The dialogue/story translation window could undershoot the true bottom on
  a long phrase, leaving part of it out of view.
- A cross-world `/tell`'s sender name could lose its `@World` separator
  entirely.
- "Message to X could not be sent" (a failed outgoing tell) now surfaces as
  a notification, not just a chat line easy to miss.
- Gemini request latency is now logged, to help diagnose occasional slow
  translation calls.
