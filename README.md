# Custom Chat

A fully custom, tab-based replacement for FFXIV's in-game chat window, built as a Dalamud plugin. The game's own chat log is hidden and everything - reading, writing, whispers, history - happens through this plugin's own ImGui interface instead.

Not affiliated with Square Enix, Twitch, BetterTTV or 7TV.

## Requirements

- FFXIV with XIVLauncher/Dalamud installed.
- Currently installed as a **dev plugin** (not yet published to a plugin repo) - point Dalamud's dev plugin location at this project's build output.

## Commands

| Command | Effect |
|---|---|
| `/customchat` | Bring the main chat window to front. |
| `/customchat config` | Open the settings window. |

The main chat window itself has no close button and can't be hidden by Dalamud's own "Toggle UI" hotkey - it's meant to always be visible, like the game's own chat log.

## Chat window

- **Sidebar** lists every tab that isn't popped out into its own window. Each row shows the tab's icon (if set), a friend marker (if the tab is a friend's whisper and that's enabled), the tab name, and an unread count that pulses in a configurable colour while there's something new to read.
- **Discord-style unread tracking**: opening a tab scrolls to a "New messages" divider marking where you left off, rather than jumping straight to the bottom. The divider stays put as you read - it doesn't recompute until you switch away and back. A "jump to bottom" button appears next to the message input whenever there's something unread.
- **Select text mode**: an I-beam toggle button next to the emote-picker button swaps the rich message view for a read-only, plain-text transcript of the same tab, which supports native click-drag selection and Ctrl+C (the rich view's colours/links/emote images aren't selectable directly, since it's built from individual widgets, not one block of text).
- **Search in this tab**: right-click a tab in the sidebar → **Search...** (or the magnifying-glass button in a popped-out tab's window) opens a search bar that filters the message list down to whatever matches (body or sender name, case-insensitive) as you type. Escape or the "x" button closes it.
- **Message input** has a "select text" toggle and an emote-picker button (searchable image grid: standard emoji, then BTTV/7TV) attached to its right edge. Pressing Enter with no ImGui text field focused anywhere opens/focuses the current tab's input, mimicking the game's own "press Enter to chat" keybind.
- Typing `/` directly (without pressing Enter first) is also redirected into the plugin's input instead of leaking into the game's own (hidden) chat box - including `/tell`/`/t `, which instead opens the matching whisper tab. A bare `/` you didn't mean as a command (e.g. the game's own `//` escape for a literal leading slash) is left alone.

## Tabs

- Fully custom: each tab is bound to an arbitrary set of chat channels (Say, Yell, Party, FC, Novice, PvP Team, Linkshells, system messages, combat log, etc. - configured in **Settings → Tabs**).
- Optional extra filter on top of channel membership: keyword-contains or regex.
- Any tab can be **popped out** into its own floating window and reattached later.
- Any tab can have:
  - A custom **name**.
  - A custom **icon** - a picked emote/emoji image shown before its name in the sidebar (real Unicode emoji typed into the name field wouldn't render; Dalamud's UI font has no colour-emoji glyphs).
  - Per-channel **message colours** (e.g. a different colour for FC chat vs. Party chat within the same tab).
  - Its own **blink/unread-count colours**, independent of the global defaults in Settings → General.
  - An **outgoing channel command** (e.g. `/p`, `/fc`, `/n`) used when you type in that tab without an explicit slash command of your own.
- Five built-in tabs are created on first run: **Party**, **General** (Say/Yell/Shout), **Free Company**, **Novice Chat**, and **Log** (system/informational messages - loot, crafting, gathering, echo, errors, etc; the raw combat log is excluded by default).

## Whispers

- A tab is created automatically per conversation partner the first time you send or receive a tell.
- Opens as a tab in the main window by default, or as its own floating window (**Settings → General**).
- The game's native "Send Tell" (right-click menu, friends list, target, the "R" shortcut) is detected and redirected straight into the matching whisper tab - no need to use the plugin's own UI to start one.
- "Close All PM" in the sidebar closes every whisper tab/window at once (history is kept; a new message reopens the tab).

## Messages

- Hovering anywhere over a message highlights the whole block, not just individual words.
- Right-clicking anywhere in a message opens one menu:
  - A header naming who it's from (**You** for your own messages, including outgoing tells).
  - **Copy message** - the whole line as `[HH:mm] Sender: text`.
  - **Translate** / **Retranslate** / **Hide translation** - see [Translation](#translation) below.
  - **Copy nickname**.
  - **Send Tell** (not shown on your own messages).
  - **Send Party Invite** (not shown on your own messages) - works by name+world, same as the `/invite` command, no need to target/see the player.
  - **Send Friend Request** (not shown on your own messages) - only works if the player is currently rendered nearby (same limitation the game's own right-click "Request as Friend" has); a toast tells you either way.
  - **Copy link**, or a submenu listing each one if the message has more than one.
- Links (`https://...`, `www...`, and bare domains like `discord.gg/xxxx`) are coloured and clickable, opening in your default browser.
- BTTV/7TV emote codes and the standard emoji pool are rendered as inline images, not text.
- Your own name shows as **"You"** in every channel, including whispers.
- Per-player nickname colours are stable across sessions (derived from their name, not random).
- A message that name-drops your own character (full name, or just the first/last half of it) gets a persistent highlight tint, since FFXIV chat has no @mention system to rely on for this.
- **Screenshot mode** (Settings → General) redacts player/sender names for screenshots.

## Exporting a tab

- Right-click a tab in the sidebar → **Export to file...**, or use the same button in **Settings → Tabs**'s tab editor (works even for a popped-out tab, which isn't in the sidebar).
- Exports that tab's *entire* stored history (not just what's currently loaded on screen) to a plain-text file under the plugin's config folder, one `[yyyy-MM-dd HH:mm] Sender: text` line per message, and opens Explorer with the file selected.

## Translation

- Right-click a message → **Translate** to see it translated inline, under the original text. **Retranslate** re-fetches (e.g. after changing the target language) without needing to hide and re-translate first.
- Source language is always detected automatically; the target language is set once in **Settings → General → "Translate messages to"** (~20 common languages).
- Uses Google Translate's free, unofficial endpoint - no API key or billing setup needed, but also not an officially supported integration, so it could occasionally be rate-limited or stop working without notice.

## Emotes

- **BTTV** and **7TV** global emote sets (toggle each independently in **Settings → Emotes**).
- A curated pool of standard Windows-style emoji (faces, hands, hearts, food, animals, meme favourites), rendered as real images via a public emoji CDN rather than text glyphs, since Dalamud's UI font has no colour-emoji coverage.
- All three sources share one searchable image-grid picker (standard emoji sorted first), used both for the chat input and for picking a tab icon / friend marker.
- Adjustable emote display size and a configurable cache refresh interval, plus a "Refresh emotes now" button that force-refetches everything immediately.

## Friends

- **Settings → General**: toggle showing a small emote-image marker before a friend's name in chat (and next to their whisper tab in the sidebar) - pick which emote via the same picker used everywhere else.

## History

- Stored in a local SQLite database, capped at a configurable size (**Settings → General**, 64 MiB - 4 GiB, default 1 GiB) - oldest messages are deleted first once the cap is hit.
- "Clear history..." in Settings wipes everything, with a confirmation prompt.

## Settings window (`/customchat config`)

- **General** - native chat hiding, whisper window behaviour, screenshot mode, link clicking, translation target language, unread notification colours, friend marker, font size, history size cap, clear history.
- **Tabs** - create/rename/delete tabs, assign channels and filters, per-channel colours, per-tab notification colours, tab icon, pop out/reattach, outgoing channel command.
- **Emotes** - BTTV/7TV toggles, emote size, cache refresh interval, and the full list of currently loaded emotes.

## Not yet implemented

- Per-Twitch-channel BTTV/7TV emotes (global sets only).
- Animated emote playback (static frame only).
- A plugin icon / listing on a public plugin repo - currently dev-plugin only.
