using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using Dalamud.Game.Chat;
using Dalamud.Game.Text;
using Dalamud.Game.Text.SeStringHandling;
using Dalamud.Game.Text.SeStringHandling.Payloads;
using Dalamud.Plugin.Services;
using TomeScrollChat.Models;

namespace TomeScrollChat.Services;

/// <summary>
/// Hooks incoming/outgoing chat, classifies each message into every matching regular tab (by
/// channel + optional keyword/regex filter) or into the relevant whisper tab, and forwards it to
/// both the UI (<see cref="MessageRouted"/>) and <see cref="ChatHistoryService"/> for persistence.
/// Subscribes to the raw <c>ChatMessage</c> event (fires unconditionally for every message) rather
/// than <c>ChatMessageUnhandled</c> - the latter only fires once "handled" status has been decided,
/// which turned out not to be a reliable signal to depend on for "did this message happen at all".
///
/// <para>We don't call <see cref="IHandleableChatMessage.PreventOriginal"/> for anything except one
/// narrow, opt-in case: an incoming whisper while <see cref="Configuration.WhisperSoundEnabled"/> is
/// on (see <see cref="HandleTell"/>), to suppress the game's own native "incoming tell" chime so it
/// doesn't double up with this plugin's own whisper notification sound
/// (<see cref="WhisperNotificationService"/>) - inspired by <c>NightmareXIV/XIVInstantMessenger</c>'s
/// <c>MessageProcessor.cs</c> calling the same API for a related purpose. <b>Confirmed live (2026-08-19)
/// that this does silence the native sound</b>, not just the visual log entry Dalamud's own docs
/// literally describe ("prevents [the message] from being processed by the game any further"). Comes
/// with the trade-off that description implies: the whisper never reaches the native (hidden) chat log
/// or any other plugin's own <c>ChatMessage</c>/<c>ChatMessageUnhandled</c> handler either, same as any
/// other <c>PreventOriginal()</c> call would - acceptable here since nothing else in this plugin reads
/// tells back from the native log (everything already comes from the Dalamud-level <c>message</c> object
/// itself), but worth remembering if a report ever comes in about a *different* plugin no longer seeing
/// whispers. Every other message/chat type is completely untouched.</para>
/// </summary>
public sealed class ChatCaptureService : IDisposable
{
    /// <summary>Chat types the game routes command-syntax/target errors through - checked against
    /// both since it's not confirmed which one specifically carries "invalid command" style messages,
    /// and treating either as a candidate costs nothing (see <see cref="LooksLikeInvalidCommandError"/>).</summary>
    private static readonly XivChatType[] CommandErrorChatTypes = { XivChatType.ErrorMessage, XivChatType.SystemError };

    private readonly IChatGui chatGui;
    private readonly IPluginLog log;
    private readonly Configuration configuration;
    private readonly TabManager tabManager;
    private readonly ChatHistoryService historyService;
    private readonly NotificationService notificationService;

    /// <summary>Whisper partner ("Name@World") the next outgoing tell should be attributed to if the
    /// game's own sender payload for that event doesn't resolve one. Set by <see cref="ChatSendService"/>
    /// immediately before sending a "/tell" command.</summary>
    public string? PendingOutgoingTellTarget { get; set; }

    public event Action<ChatTabConfig, ChatMessageRecord>? MessageRouted;

    /// <summary>Fires exactly once per real chat event, before any tab-routing happens - unlike
    /// <see cref="MessageRouted"/>, which fires once *per matching tab* (so the same incoming "Say"
    /// message could invoke it twice if two tabs both show that channel). <see cref="Services.AutoReplyService"/>
    /// needs an exactly-once signal to avoid double-triggering, which is the whole reason this exists
    /// separately rather than just reusing <see cref="MessageRouted"/>. The trailing <c>bool</c> is
    /// <see cref="Models.ChatMessageRecord.IsFromLocalPlayer"/> - see that property's own doc comment
    /// for why this is the preferred "is this the local player's own message" signal over string-matching
    /// sender name/key.</summary>
    public event Action<XivChatType, string, string, string, bool>? RawMessageReceived;

    public ChatCaptureService(IChatGui chatGui, IPluginLog log, Configuration configuration, TabManager tabManager, ChatHistoryService historyService, NotificationService notificationService)
    {
        this.chatGui = chatGui;
        this.log = log;
        this.configuration = configuration;
        this.tabManager = tabManager;
        this.historyService = historyService;
        this.notificationService = notificationService;
        chatGui.ChatMessage += OnChatMessage;
    }

    private void OnChatMessage(IHandleableChatMessage message)
    {
        try
        {
            Handle(message);
        }
        catch (Exception ex)
        {
            log.Error(ex, "TomeScrollChat: failed to process an incoming chat message");
        }
    }

    private void Handle(IHandleableChatMessage message)
    {
        var chatType = message.LogKind;
        var senderKey = ExtractSenderKey(message.Sender);
        var senderText = BuildSenderText(message.Sender, senderKey);
        var isFromLocalPlayer = message.SourceKind == XivChatRelationKind.LocalPlayer;
        var (body, payloadLinks) = BuildBodyAndPayloadLinks(message.Message);
        // IChatMessage.Timestamp reads back 0 for the raw ChatMessage event in the Dalamud version
        // this was tested against (every message showed the same UTC-epoch-in-local-time clock),
        // so this uses wall-clock time at the moment the message is actually handled instead - for
        // a live chat capture that's effectively the same instant anyway.
        var timestamp = DateTime.UtcNow;

        RawMessageReceived?.Invoke(chatType, senderText, senderKey, body, isFromLocalPlayer);

        if (configuration.NotifyOnInvalidCommand && Array.IndexOf(CommandErrorChatTypes, chatType) >= 0 && LooksLikeChatSystemError(body))
            notificationService.Show(body, NotificationSeverity.Error);

        if (chatType is XivChatType.TellIncoming or XivChatType.TellOutgoing)
        {
            HandleTell(message, chatType, senderText, senderKey, isFromLocalPlayer, body, payloadLinks, timestamp);
            return;
        }

        foreach (var tab in tabManager.Tabs)
        {
            if (tab.IsPmTab || !tab.Channels.Contains(chatType) || !MatchesFilter(tab, body))
                continue;

            var record = new ChatMessageRecord
            {
                TimestampUtc = timestamp,
                ChatType = chatType,
                SenderName = senderText,
                SenderKey = senderKey,
                IsFromLocalPlayer = isFromLocalPlayer,
                Body = body,
                RoutingKey = tab.Id.ToString(),
                PayloadLinks = payloadLinks,
            };
            if (!tab.DisableLogging)
                historyService.Enqueue(record);
            MessageRouted?.Invoke(tab, record);
        }
    }

    /// <summary><see cref="IHandleableChatMessage.PreventOriginal"/> for an incoming whisper while
    /// <see cref="Configuration.WhisperSoundEnabled"/> is on - confirmed live to silence the game's own
    /// native tell chime (see this class's own doc comment for the trade-off that comes with). Deliberately
    /// does NOT gate on <see cref="Configuration.NotifyOnWhisper"/> (the popup toggle) - this is
    /// specifically about the *sound*, so it tracks <see cref="Configuration.WhisperSoundEnabled"/>
    /// alone. Never called for <see cref="XivChatType.TellOutgoing"/> - only ever affects messages
    /// received, never sent.</summary>
    private void HandleTell(IHandleableChatMessage message, XivChatType chatType, string senderText, string senderKey, bool isFromLocalPlayer, string body, IReadOnlyList<ChatPayloadLink> payloadLinks, DateTime timestamp)
    {
        if (chatType == XivChatType.TellIncoming && configuration.WhisperSoundEnabled)
            message.PreventOriginal();

        var partnerKey = !string.IsNullOrEmpty(senderKey)
            ? senderKey
            : chatType == XivChatType.TellOutgoing && !string.IsNullOrEmpty(PendingOutgoingTellTarget)
                ? PendingOutgoingTellTarget!
                : senderText;

        var displayName = partnerKey.Split('@')[0];
        var tab = tabManager.GetOrCreatePmTab(partnerKey, displayName);

        var record = new ChatMessageRecord
        {
            TimestampUtc = timestamp,
            ChatType = chatType,
            SenderName = senderText,
            SenderKey = senderKey,
            IsFromLocalPlayer = isFromLocalPlayer,
            Body = body,
            RoutingKey = partnerKey,
            PayloadLinks = payloadLinks,
        };
        if (!tab.DisableLogging)
            historyService.Enqueue(record);
        MessageRouted?.Invoke(tab, record);
    }

    /// <summary>Guillemets this plugin wraps auto-translate dictionary phrases in for display - a
    /// stand-in for the game's own native bracket-icon glyphs (see
    /// <see cref="StripNativeAutoTranslateBrackets"/>), chosen because they're ordinary Unicode
    /// punctuation likely to actually have a glyph in Dalamud's UI font, unlike the game-specific
    /// Private Use Area codepoints the native ones use.</summary>
    private const string AutoTranslateOpen = "《";
    private const string AutoTranslateClose = "》";

    /// <summary>The game's own native auto-translate bracket-icon glyphs - Private Use Area codepoints
    /// (not literal guillemet characters) that <see cref="AutoTranslatePayload.Text"/> comes wrapped
    /// in already, mapped to bracket shapes in FFXIV's own bundled font. Confirmed live (2026-08-17)
    /// via exact Unicode codepoint logging, after a report that clicking a rendered auto-translate
    /// phrase never matched its expected plain text - the codepoints turned out to be U+E040 (leading)
    /// and U+E041 (trailing), not this plugin's own guillemets as first assumed. Stripped from
    /// <see cref="AutoTranslatePayload.Text"/> before use in <see cref="BuildBodyAndPayloadLinks"/>,
    /// since Dalamud's ImGui font almost certainly doesn't have these same PUA glyphs mapped (they're
    /// specific to the game's own font atlas) - left in place, they'd likely render as missing-glyph
    /// placeholders sandwiched inside <see cref="AutoTranslateOpen"/>/<see cref="AutoTranslateClose"/>.</summary>
    private static string StripNativeAutoTranslateBrackets(string text) =>
        text.Trim('\uE040', '\uE041').Trim();

    /// <summary>Strips a leading Private Use Area icon glyph (U+E000-U+F8FF) from a chat message's
    /// sender name - confirmed live (2026-08-19) via exact codepoint logging that Party chat prefixes
    /// every sender's name with one of these (U+E0E1 observed - almost certainly a party-slot/leader
    /// icon in the game's own font atlas, the same private-icon-font mechanism as
    /// <see cref="StripNativeAutoTranslateBrackets"/>'s brackets), which Dalamud's ImGui font doesn't
    /// have glyphs for either (renders as a stray missing-glyph box in the chat window otherwise).
    /// <para>This was also the root cause of a live-reported "You" label bug specific to Party chat:
    /// the local player's own Party messages carry this same prefix on <c>Sender.TextValue</c>
    /// ("\uE0E1Shiun Yuzuka"), which never matched the clean <c>PlayerState.CharacterName</c>
    /// ("Shiun Yuzuka") the own-message fallback check compares against - stripping it here, at the one
    /// place <see cref="Handle"/> reads the sender text, fixes both the stray glyph and that comparison
    /// at once. Real player names can never start with a PUA codepoint, so this is safe unconditionally
    /// for every channel, not just Party - a no-op wherever the game doesn't prefix anything.</para></summary>
    /// <summary>A cross-world sender's name (a tell from another world, or apparently anywhere else the
    /// game marks a name cross-world - e.g. Party/FC members) inserts an icon-only payload between the
    /// name and world text runs to visually separate them in the native UI - unlike
    /// <see cref="StripLeadingIconGlyphs"/>'s PUA character (present in <c>TextValue</c> but unmapped in
    /// Dalamud's font, so it renders as a stray glyph), this icon payload contributes *no text at all* to
    /// <c>TextValue</c>, so a plain read silently mashes the two runs together with nothing between them
    /// at all ("Ren YanagiZodiark" - confirmed live 2026-08-22 via exact codepoint logging: literally zero
    /// characters between "Yanagi" and "Zodiark", not even the icon glyph itself). When any such icon
    /// payload is present, <paramref name="senderKey"/> - already built from the sender's
    /// <see cref="PlayerPayload"/>'s own structured Name/World fields, not text concatenation, so it isn't
    /// affected by this at all - is authoritative and used instead, giving a legible "Name@World" instead
    /// of the broken concatenation.</summary>
    private static string BuildSenderText(SeString sender, string senderKey)
    {
        if (!string.IsNullOrEmpty(senderKey) && sender.Payloads.Any(p => p is IconPayload))
            return senderKey;

        return StripLeadingIconGlyphs(sender.TextValue);
    }

    private static string StripLeadingIconGlyphs(string senderText)
    {
        var start = 0;
        while (start < senderText.Length && senderText[start] is >= '\uE000' and <= '\uF8FF')
            start++;

        return start > 0 ? senderText[start..] : senderText;
    }

    /// <summary>Builds the flattened message body *and* finds every map/flag, item, Party Finder, and
    /// auto-translate-dictionary span in it in one pass - replaces a plain <c>SeString.TextValue</c>
    /// read (used until 2026-08-17) specifically because <see cref="AutoTranslatePayload"/> needs its
    /// resolved text *inserted*, not just located: unlike a map/item link (whose marker payload is
    /// always followed by a game-generated <see cref="TextPayload"/> holding the visible name/
    /// coordinates, which already lands in <c>TextValue</c> on its own), an auto-translate phrase has
    /// no such companion payload - <c>TextValue</c> contributes nothing for it at all, so a message
    /// using the game's auto-translate dictionary reads with a silent gap where the phrase should be
    /// (reported: "[05:22] Joon Veyris: night night hmm, fatcat" - the two auto-translate phrases the
    /// player actually picked from the dictionary never showed up, unlike the "hmm, fatcat" they typed
    /// themselves). Every other payload contributes exactly what a plain <c>TextValue</c> read already
    /// did (only <see cref="TextPayload.Text"/> - confirmed by this project's own earlier map/item link
    /// investigation, see <see cref="ChatPayloadLink"/>'s doc comment), so this is behaviour-preserving
    /// for every message that has no auto-translate phrase in it.
    /// <para>The display text for a map/item/Party Finder link isn't always a single
    /// <see cref="TextPayload"/> immediately after the marker - confirmed via a real received map link
    /// (2026-08-13) to actually look like <c>MapLinkPayload, UIForegroundPayload, UIGlowPayload,
    /// TextPayload, UIGlowPayload, UIForegroundPayload, TextPayload, RawPayload</c>: an icon-glyph
    /// <see cref="TextPayload"/> inside the glow/colour span, then a second <see cref="TextPayload"/>
    /// *outside* it (the actual readable "Place Name (X, Y)" text) before the closing
    /// <see cref="RawPayload"/>. Handled by accumulating every consecutive <see cref="TextPayload"/>
    /// into one combined span, only ending it at the first payload that isn't text or the two "safe"
    /// formatting types (<see cref="UIForegroundPayload"/>/<see cref="UIGlowPayload"/>) that appear
    /// between them here.</para></summary>
    private static (string Body, List<ChatPayloadLink> Links) BuildBodyAndPayloadLinks(SeString message)
    {
        var body = new StringBuilder();
        var links = new List<ChatPayloadLink>();
        ChatPayloadLinkType? pendingType = null;
        MapLinkPayload? pendingMapLink = null;
        ItemPayload? pendingItemLink = null;
        PartyFinderPayload? pendingPartyFinderLink = null;
        QuestPayload? pendingQuestLink = null;
        var pendingStart = 0;
        var pendingLength = 0;

        void CommitPending()
        {
            if (pendingType != null && pendingLength > 0)
            {
                links.Add(new ChatPayloadLink
                {
                    Type = pendingType.Value,
                    Start = pendingStart,
                    Length = pendingLength,
                    MapLink = pendingMapLink,
                    Item = pendingItemLink,
                    PartyFinder = pendingPartyFinderLink,
                    Quest = pendingQuestLink,
                });
            }

            pendingType = null;
            pendingMapLink = null;
            pendingItemLink = null;
            pendingPartyFinderLink = null;
            pendingQuestLink = null;
            pendingLength = 0;
        }

        foreach (var payload in message.Payloads)
        {
            if (payload is TextPayload textPayload)
            {
                var text = textPayload.Text ?? string.Empty;
                if (pendingType != null && text.Length > 0)
                {
                    if (pendingLength == 0)
                        pendingStart = body.Length;
                    pendingLength += text.Length;
                }

                body.Append(text);
            }
            else if (payload is MapLinkPayload mapLink)
            {
                CommitPending();
                pendingType = ChatPayloadLinkType.MapLink;
                pendingMapLink = mapLink;
            }
            else if (payload is ItemPayload itemLink)
            {
                CommitPending();
                pendingType = ChatPayloadLinkType.Item;
                pendingItemLink = itemLink;
            }
            // Fixed 2026-08-17: reported live as "Could not retrieve party recruitment information"
            // (a genuine native error) when clicking a link built from a message like "Of the 38
            // parties currently recruiting, all match your search conditions." - confirmed via the
            // metadata tool that PartyFinderPayload carries its own LinkType
            // (PartyFinderLinkType: NotSpecified/LimitedToHomeWorld/PartyFinderNotification), and this
            // exact kind of summary/count message is PartyFinderNotification, not a link to any single
            // real listing - AgentLookingForGroup.OpenListing(ListingId) was always going to fail for
            // it since there's no real listing behind that id. A genuine "click to view this specific
            // recruitment" link (native "Relay", or someone else's shared listing) doesn't carry this
            // type, so those are unaffected - this payload type is skipped entirely (falls through as
            // plain text, same as any other unhandled payload) rather than treated as clickable.
            else if (payload is PartyFinderPayload pfLink && pfLink.LinkType != PartyFinderPayload.PartyFinderLinkType.PartyFinderNotification)
            {
                CommitPending();
                pendingType = ChatPayloadLinkType.PartyFinder;
                pendingPartyFinderLink = pfLink;
            }
            else if (payload is QuestPayload questLink)
            {
                CommitPending();
                pendingType = ChatPayloadLinkType.Quest;
                pendingQuestLink = questLink;
            }
            else if (payload is AutoTranslatePayload autoTranslate)
            {
                CommitPending();

                var phrase = autoTranslate.Text != null ? StripNativeAutoTranslateBrackets(autoTranslate.Text) : null;
                if (!string.IsNullOrEmpty(phrase))
                {
                    var wrapped = AutoTranslateOpen + phrase + AutoTranslateClose;
                    links.Add(new ChatPayloadLink
                    {
                        Type = ChatPayloadLinkType.AutoTranslate,
                        Start = body.Length,
                        Length = wrapped.Length,
                    });
                    body.Append(wrapped);
                }
            }
            else if (payload is not (UIForegroundPayload or UIGlowPayload))
            {
                // Anything else (a RawPayload closing marker, another unrelated payload, ...) ends the
                // current link's span - only text and these two "safe" formatting toggles extend it.
                CommitPending();
            }
        }

        CommitPending();

        return (body.ToString(), links);
    }

    /// <summary>Best-effort text match against a curated set of FFXIV's own system messages about a
    /// *typed chat action* failing - chat type alone (<see cref="CommandErrorChatTypes"/>) isn't
    /// specific enough on its own, since the same error channels also carry unrelated in-game errors
    /// (failed actions, out-of-range targets, etc.) that would otherwise make
    /// <see cref="Configuration.NotifyOnInvalidCommand"/> noisy well beyond just chat problems.
    /// <b>2026-08-17</b>: extended past just "invalid slash command" (<c>The command "/xyz" does not
    /// exist.</c>) to also catch the chat-spam-guard message (<c>Your message was not heard. You must
    /// wait before using /tell, /say, /yell, or /shout again.</c>) - reported live as easy to miss the
    /// same way the original invalid-command case was. <b>2026-08-22</b>: extended again to catch a
    /// failed outgoing tell (<c>Message to Name could not be sent.</c> - target offline/not found/
    /// cross-world routing failure/etc.), same "easy to miss" report. Deliberately a short, exact-phrase
    /// allowlist rather than a broader heuristic - each addition should be a real message seen live, not
    /// a guess, to avoid false-positiving on unrelated errors sharing the same chat channels. English-
    /// client text only - not verified against other game languages.</summary>
    private static readonly string[] ChatSystemErrorMarkers =
    {
        "does not exist", // invalid slash command, e.g. "/xyz"
        "was not heard", // /tell, /say, /yell, /shout spam guard
        "could not be sent", // outgoing /tell failed, e.g. "Message to Name could not be sent."
    };

    private static bool LooksLikeChatSystemError(string body) =>
        ChatSystemErrorMarkers.Any(marker => body.Contains(marker, StringComparison.OrdinalIgnoreCase));

    private static bool MatchesFilter(ChatTabConfig tab, string body) => tab.FilterMode switch
    {
        ChatTabFilterMode.None => true,
        ChatTabFilterMode.Keyword => !string.IsNullOrEmpty(tab.FilterPattern) &&
            body.Contains(tab.FilterPattern, StringComparison.OrdinalIgnoreCase),
        ChatTabFilterMode.Regex => TryRegexMatch(tab.FilterPattern, body),
        _ => true,
    };

    private static bool TryRegexMatch(string pattern, string body)
    {
        if (string.IsNullOrEmpty(pattern))
            return true;

        try
        {
            return Regex.IsMatch(body, pattern);
        }
        catch (ArgumentException)
        {
            // Invalid regex (e.g. still being typed in settings) - don't hide every message over a typo.
            return true;
        }
    }

    private static string ExtractSenderKey(SeString sender)
    {
        foreach (var payload in sender.Payloads)
        {
            if (payload is PlayerPayload player)
            {
                var world = TryGetWorldName(player);
                return world != null ? $"{player.PlayerName}@{world}" : player.PlayerName;
            }
        }

        return string.Empty;
    }

    private static string? TryGetWorldName(PlayerPayload player)
    {
        try
        {
            var world = player.World;
            return world.IsValid ? world.Value.Name.ToString() : null;
        }
        catch
        {
            return null;
        }
    }

    public void Dispose()
    {
        chatGui.ChatMessage -= OnChatMessage;
    }
}
