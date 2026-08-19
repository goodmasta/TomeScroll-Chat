# TomeScroll Chat

> **⚠️ Beta - v0.1.2.0.** Under active development, not yet feature-frozen or thoroughly battle-tested. Expect rough edges, and back up `pluginConfigs/TomeScrollChat*` before major updates.

> **🤖 This entire plugin - every line of code, every feature, this README - was built through an AI coding agent (Claude), directed by a human via natural-language requests rather than hand-written by a developer.** No prior C#/Dalamud codebase was used as a starting point.

A fully custom, tab-based replacement for FFXIV's in-game chat window, built as a Dalamud plugin. The game's own chat log is hidden and everything - reading, writing, whispers, history - happens through this plugin's own ImGui interface instead.

Not affiliated with Square Enix, Twitch, BetterTTV, 7TV or Google.

## Quick install

1. In-game: `/xlsettings` → **Experimental** → **Custom Plugin Repositories** → add:
   `https://raw.githubusercontent.com/goodmasta/TomeScroll-Chat/main/pluginmaster.json`
2. `/xlplugins` → find **TomeScroll Chat** → Install.
3. Use `/tomescrollc` to bring up the chat window, `/tomescrollc config` for settings.

Details and building from source: see [Installing](#installing) below.

## Requirements

- FFXIV with XIVLauncher/Dalamud installed.
- Optional: a free [Google Gemini](https://ai.google.dev/) API key to unlock the AI features (translation and the rest of the chat window work without one).

## Installing

### As a custom repository (recommended for regular use)

1. In-game: `/xlsettings` → **Experimental** → **Custom Plugin Repositories**.
2. Add: `https://raw.githubusercontent.com/goodmasta/TomeScroll-Chat/main/pluginmaster.json`
3. Save, then find "TomeScroll Chat" in `/xlplugins` and install it like any other plugin. Updates are picked up automatically like any other plugin.

`.github/workflows/release.yml` rebuilds and republishes automatically on every push to `main`: it bumps `pluginmaster.json`'s version/timestamp to match the `<Version>` in the `.csproj`, moves a rolling `latest` tag, and updates the `latest` GitHub Release with the freshly packaged zip. Bumping the version only requires editing `<Version>` in `TomeScrollChat/TomeScrollChat.csproj` and pushing.

### As a dev plugin (for local development)

1. **Build it:**
   ```
   git clone https://github.com/goodmasta/TomeScroll-Chat.git
   cd TomeScroll-Chat/TomeScrollChat
   dotnet build -c Debug
   ```
   Requires the [.NET 10 SDK](https://dotnet.microsoft.com/download). Produces `TomeScrollChat/bin/x64/Debug/TomeScrollChat.dll` (use `-c Release` for `bin/x64/Release/TomeScrollChat.dll` instead, if you'd rather run a release build).
2. In-game: `/xlsettings` → **Experimental** → **Dev Plugin Locations** → add the path to the `TomeScrollChat.dll` built above.
3. `/xlplugins` → **Dev Tools** → **Installed Dev Plugins** → enable TomeScroll Chat. Dalamud loads it immediately - no restart needed.
4. Rebuilding after pulling changes is enough to update it - with "Automatic Reloading" left on for the dev plugin entry (the default), Dalamud picks up the new build the next time it changes.

## Commands

| Command | Effect |
|---|---|
| `/tomescrollc` | Bring the main chat window to front. |
| `/tomescrollc config` | Open the settings window. |

The main chat window itself has no close button and can't be hidden by Dalamud's own "Toggle UI" hotkey - it's meant to always be visible, like the game's own chat log.

## Chat window

- **Sidebar** lists every tab that isn't popped out into its own window. Each row shows the tab's icon (if set), a friend marker (if the tab is a friend's whisper and that's enabled), the tab name, and an unread count that pulses in a configurable colour while there's something new to read.
- **Discord-style unread tracking**: opening a tab scrolls to a "New messages" divider marking where you left off, rather than jumping straight to the bottom.
- **Select text mode**: an I-beam toggle button swaps the rich message view for a read-only, plain-text transcript that supports native click-drag selection and Ctrl+C.
- **Search in this tab**: right-click a tab in the sidebar → **Search...** filters the message list live as you type.
- **Message input** starts one line tall and grows up to 3 - **Enter sends, Shift+Enter inserts a line break** - then shrinks back once sent. Has, attached flush to its right edge: a "jump to bottom" button, a "select text" toggle, and an emote-picker button.
- Typing `/` directly is redirected into the plugin's input instead of leaking into the game's own (hidden) chat box - including `/tell`/`/t `, which opens the matching whisper tab.
- **Fades when unfocused**: the main window and any popped-out tab windows fade their background to a configurable opacity while inactive (on by default).

## Tabs

- Fully custom: each tab is bound to an arbitrary set of chat channels (Say, Yell, Party, FC, Novice, PvP Team, Linkshells, system messages, combat log, etc.).
- Optional extra filter on top of channel membership: keyword-contains or regex.
- Any tab can be **popped out** into its own floating window and reattached later.
- Any tab can have a custom name, a custom icon, per-channel message colours, its own unread/blink colours, and an outgoing channel command (e.g. `/p`, `/fc`, `/n`).
- Five built-in tabs are created on first run: **Party**, **General** (Say/Yell/Shout), **Free Company**, **Novice Chat**, and **Log**.

## Whispers

- A tab is created automatically per conversation partner the first time you send or receive a tell.
- The game's native "Send Tell" (right-click menu, friends list, target, "R" shortcut) is detected and redirected straight into the matching whisper tab.
- "Close All PM" closes every whisper tab/window at once (history is kept).

## Messages

- Hovering anywhere over a message highlights the whole block.
- Right-click a message for: **Copy message**, **Translate**/**Retranslate**/**Hide translation**, **Copy nickname**, **Send Tell**, **Send Party Invite**, **Send Friend Request**, **Copy link(s)**, and (if a Gemini key is set) **Generate AI Reply**.
- Links are coloured and clickable, opening in your default browser.
- BTTV/7TV emote codes and standard emoji (typed as `:code:`) render as inline images.
- Your own name shows as **"You"** everywhere, including whispers.
- Per-player nickname colours are stable across sessions.
- A message that name-drops your own character gets a persistent highlight tint.
- **Screenshot mode** redacts player/sender names.

## Setting up Gemini

Optional - the chat window, tabs, whispers, and Google Translate-based translation all work without it. A Gemini key just unlocks the AI-backed features below.

1. Get a free API key from [Google AI Studio](https://aistudio.google.com/apikey) (a Google account is all that's needed).
2. In-game, open `/tomescrollc config` → **AI** tab.
3. Paste the key into **API key**. A sensible default model is pre-selected in the **Model** dropdown - only change it if you want something faster/cheaper or more capable.
4. "Status: configured." confirms it took.

Once set, it powers:

- The **"Gemini"** option for the translation engine (General → Translation) - richer/more accurate than the default Google Translate engine.
- **Generate AI Reply** / **Rephrase** / **Fix errors** (see AI features below) - hidden from menus entirely until a key is set.

The key is stored only in this plugin's own local config file, never sent anywhere except Google's Gemini API.

## AI features (requires a Gemini API key, Settings → AI)

All AI actions only ever fill the compose box - none of them send anything automatically.

- **Generate AI Reply** - right-click any message → generates a suggested reply from its content and drops it into your input, ready to edit or send. Optionally remembers a bounded history of past reply generations (off by default) to keep tone consistent across a conversation.
- **Rephrase** - right-click your own compose box → reword whatever you've currently typed.
- **Fix errors** - right-click the compose box → correct spelling/grammar only, without rewording.
- All three menu entries are hidden entirely (not just disabled) until a Gemini API key is configured.

## Auto-reply

- A title-bar button opens auto-reply configuration: a **fixed, player-written** message (default: "Busy IRL, will reply as soon as I can.") sent automatically - never AI-generated.
- Can trigger on incoming whispers and/or on your character's name being mentioned in Say/Yell/Shout/Party/FC/Alliance/Linkshell (reusing the same mention-detection as the in-chat name highlight).
- Per-sender cooldown plus a global minimum gap between sends to avoid spam/loops. "Reset to default message" button included.

## Friends

- Toggle a small emote-image marker before a friend's name in chat and their whisper tab.
- Optional online/offline toast notifications, checked on a 10-second interval without disrupting the native friend list while it's open.

## Translation

- Right-click a message → **Translate** for an inline translation under the original text.
- Select text in your own compose box → **Translate to** → replaces the selection (or whole input) with the translation, ready to send.
- Uses Google Translate's free, unofficial endpoint - no key needed, but not an officially supported integration.

## Emotes

- **BTTV** and **7TV** global emote sets, plus a curated standard-emoji pool rendered as real images via a public emoji CDN.
- All sources share one searchable image-grid picker, used for chat input, tab icons, and the friend marker.
- Emotes must be wrapped in colons (`:cat:`) to render, avoiding accidental matches on plain words.

## History

- Stored in a local SQLite database, capped at a configurable size (64 MiB - 4 GiB, default 1 GiB) - oldest messages are deleted first once the cap is hit.
- "Clear history..." wipes everything, with a confirmation prompt. Live on-disk size shown in Settings.
- Right-click a tab → **Export to file...** dumps its entire stored history to a plain-text file.

## Settings window (`/tomescrollc config`)

- **General** - native chat hiding, whisper window behaviour, screenshot mode, translation target language, unread colours, friend marker, font size, history size cap.
- **Tabs** - create/rename/delete tabs, channels, filters, colours, icons, pop out/reattach.
- **Emotes** - BTTV/7TV toggles, emote size, cache refresh interval, loaded emote list.
- **AI** - Gemini API key, AI reply prompt and memory toggle/limit.
- **Reset settings to defaults** - available in General, with tabs and all preferences restored to their shipped defaults.

## Not yet implemented

- Per-Twitch-channel BTTV/7TV emotes (global sets only).
- Animated emote playback (static frame only).
