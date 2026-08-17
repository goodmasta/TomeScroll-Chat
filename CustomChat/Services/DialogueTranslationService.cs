using System;
using System.Collections.Generic;
using Dalamud.Game.Gui.Toast;
using Dalamud.Game.Text.SeStringHandling;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.UI;
using CustomChat.Models;

namespace CustomChat.Services;

/// <summary>
/// Feeds <see cref="Windows.DialogueTranslationWindow"/> - watches three sources for new lines while
/// <see cref="Configuration.EnableDialogueTranslationWindow"/> is on, translates each via
/// <see cref="TranslationService.TranslateRawAsync"/> (the same one-off dispatcher the input-box
/// "Translate" action uses - bypasses the chat-message queue/backoff machinery entirely, deliberately:
/// that queue exists to survive *bursts* like reopening a tab with hundreds of backlog messages, but
/// dialogue/cutscene lines only ever advance one at a time at human reading pace, so there's nothing to
/// throttle here), and appends the result to <see cref="Entries"/>:
///
/// <list type="bullet">
/// <item><description><b>NPC dialogue / in-cutscene dialogue</b> - <c>AddonTalk</c> (the game's
/// standard name+text dialogue box, used both out of and during cutscenes), polled every
/// <see cref="IFramework.Update"/> tick like every other native-addon watcher in this project
/// (<see cref="NativeItemLinkWatcher"/>, <see cref="NativePartyFinderLinkWatcher"/>, etc.) rather than
/// via <c>IAddonLifecycle</c> events, since this project has no confirmed-working precedent for which
/// lifecycle event actually fires per new line on this specific addon, while polling is a proven
/// pattern here already. Speaker = <c>GetTextNodeById(2)</c>, body = <c>GetTextNodeById(3)</c> -
/// confirmed live (2026-08-17) via a diagnostic sweep across a wide id range after the initial
/// TextAdvance-style guess (speaker=3/body=4) turned out to both be permanently-empty nodes on this
/// game version. <c>NodeText</c> comes back wrapped in literal double-quote characters (e.g.
/// <c>"Oswold"</c>, quotes included in the raw string) - stripped via <see cref="StripQuotes"/> before
/// use.</description></item>
/// <item><description><b>Pure cutscene subtitles</b> (narration/ambient lines with no on-screen name
/// plate) - <c>AddonTalkSubtitle.SubtitleText</c>, a single cleanly-named field, no node-id guessing
/// needed. Addon name assumed to be <c>"TalkSubtitle"</c> (struct name minus the <c>Addon</c> prefix,
/// the same convention <c>AddonChatLog</c> -&gt; <c>"ChatLog"</c> already follows elsewhere in this
/// project) - not independently confirmed either.</description></item>
/// <item><description><b>Quest-related notices</b> - <see cref="IToastGui.QuestToast"/>, an actual
/// Dalamud event (not a native struct read), so nothing to verify here: fires with the toast's
/// already-resolved <see cref="SeString"/> text.</description></item>
/// </list>
///
/// No cutscene-specific hiding logic lives here or in <see cref="Windows.DialogueTranslationWindow"/> -
/// unlike <c>MainWindow</c>/<c>DetachedTabWindow</c>, which check
/// <c>Plugin.Condition.Any(ConditionFlag.WatchingCutscene, ...)</c> in their own <c>DrawConditions()</c>
/// to *optionally* hide during cutscenes (see <see cref="Configuration.HideChatDuringCutscenes"/>).
/// Simply never adding that check here is what keeps this window visible in cutscenes, per the
/// explicit ask - confirmed by the fact that turning that other setting off already proves Dalamud
/// itself doesn't force-hide plugin windows during cutscenes, so there was nothing else to disable.
/// </summary>
public sealed class DialogueTranslationService : IDisposable
{
    private const int MaxEntries = 60;

    private readonly IFramework framework;
    private readonly IGameGui gameGui;
    private readonly IToastGui toastGui;
    private readonly IPluginLog log;
    private readonly Configuration configuration;
    private readonly TranslationService translationService;
    private readonly List<DialogueTranslationEntry> entries = new();
    private readonly object entriesLock = new();

    private string lastTalkText = string.Empty;
    private string lastSubtitleText = string.Empty;

    // Gated on actual visibility transitions, never unconditional - CheckTalk/CheckSubtitle run on
    // every framework tick while the feature's enabled, so logging every tick would flood /xllog.
    private bool talkWasVisible;
    private bool subtitleWasVisible;

    public IReadOnlyList<DialogueTranslationEntry> Entries
    {
        get
        {
            lock (entriesLock)
                return entries.ToArray();
        }
    }

    public DateTime LastEntryAt { get; private set; } = DateTime.MinValue;

    public unsafe DialogueTranslationService(IFramework framework, IGameGui gameGui, IToastGui toastGui, IPluginLog log, Configuration configuration, TranslationService translationService)
    {
        this.framework = framework;
        this.gameGui = gameGui;
        this.toastGui = toastGui;
        this.log = log;
        this.configuration = configuration;
        this.translationService = translationService;

        framework.Update += OnFrameworkUpdate;
        toastGui.QuestToast += OnQuestToast;
    }

    private unsafe void OnFrameworkUpdate(IFramework _)
    {
        if (!configuration.EnableDialogueTranslationWindow)
            return;

        CheckTalk();
        CheckSubtitle();
    }

    private unsafe void CheckTalk()
    {
        var addon = gameGui.GetAddonByName<AddonTalk>("Talk");
        var visible = addon != null && addon->IsVisible;

        if (visible && !talkWasVisible)
            log.Info("CustomChat: Talk addon became visible");
        else if (!visible && talkWasVisible)
            log.Info("CustomChat: Talk addon no longer visible");
        talkWasVisible = visible;

        if (!visible)
        {
            lastTalkText = string.Empty; // next time the box opens (even with the same line) should re-translate
            return;
        }

        var textNode = addon->GetTextNodeById(3);
        if (textNode == null)
            return;

        var text = StripQuotes(textNode->NodeText.ToString());
        if (string.IsNullOrWhiteSpace(text) || text == lastTalkText)
            return;

        lastTalkText = text;

        string? speaker = null;
        var speakerNode = addon->GetTextNodeById(2);
        if (speakerNode != null)
        {
            var speakerText = StripQuotes(speakerNode->NodeText.ToString());
            if (!string.IsNullOrWhiteSpace(speakerText))
                speaker = speakerText;
        }

        log.Info("CustomChat: Talk dialogue detected - speaker='{Speaker}' text='{Text}'", speaker ?? "(none)", Truncate(text, 80));
        QueueTranslation(DialogueTranslationKind.NpcDialogue, speaker, text);
    }

    /// <summary><c>AtkTextNode.NodeText</c> on <c>AddonTalk</c> comes back wrapped in a literal
    /// leading/trailing <c>"</c> character (confirmed live, 2026-08-17 - e.g. the raw text is
    /// <c>"Oswold"</c>, quote marks included) - stripped for both the speaker and body, since neither
    /// should actually show quoted in the translation window.</summary>
    private static string StripQuotes(string text) =>
        text.Length >= 2 && text[0] == '"' && text[^1] == '"' ? text[1..^1] : text;

    private unsafe void CheckSubtitle()
    {
        var addon = gameGui.GetAddonByName<AddonTalkSubtitle>("TalkSubtitle");
        var visible = addon != null && addon->IsVisible;

        if (visible && !subtitleWasVisible)
            log.Info("CustomChat: TalkSubtitle addon became visible");
        else if (!visible && subtitleWasVisible)
            log.Info("CustomChat: TalkSubtitle addon no longer visible");
        subtitleWasVisible = visible;

        if (!visible)
        {
            lastSubtitleText = string.Empty;
            return;
        }

        var text = addon->SubtitleText.ToString();
        if (string.IsNullOrWhiteSpace(text) || text == lastSubtitleText)
            return;

        lastSubtitleText = text;
        log.Info("CustomChat: TalkSubtitle text detected - '{Text}'", Truncate(text, 80));
        QueueTranslation(DialogueTranslationKind.CutsceneSubtitle, null, text);
    }

    private void OnQuestToast(ref SeString message, ref QuestToastOptions options, ref bool isHandled)
    {
        if (!configuration.EnableDialogueTranslationWindow)
            return;

        var text = message.TextValue;
        log.Info("CustomChat: quest toast detected - '{Text}'", Truncate(text, 80));
        if (!string.IsNullOrWhiteSpace(text))
            QueueTranslation(DialogueTranslationKind.QuestNotice, null, text);
    }

    private static string Truncate(string text, int maxLength) =>
        text.Length <= maxLength ? text : text[..maxLength] + "...";

    /// <summary>Fires the translation and appends the result once it lands - not awaited by the
    /// caller (both call sites are synchronous framework-tick/event handlers), and each call is fully
    /// independent (no shared in-flight state to guard), so overlapping requests from back-to-back
    /// lines just complete and append in whatever order they finish, same as normal chat auto-translate.</summary>
    private async void QueueTranslation(DialogueTranslationKind kind, string? speaker, string originalText)
    {
        try
        {
            var translated = await translationService.TranslateRawAsync(originalText, configuration.TranslateTargetLanguage).ConfigureAwait(false);
            if (string.IsNullOrWhiteSpace(translated))
            {
                log.Warning("CustomChat: dialogue translation ({Kind}) returned empty/null via {Engine}", kind, TranslationService.EngineLabel(translationService.ActiveEngine));
                return;
            }

            log.Info("CustomChat: dialogue translation ({Kind}) succeeded - '{Text}'", kind, Truncate(translated, 80));
            var entry = new DialogueTranslationEntry(kind, speaker, originalText, translated, DateTime.UtcNow);
            lock (entriesLock)
            {
                entries.Add(entry);
                if (entries.Count > MaxEntries)
                    entries.RemoveAt(0);
            }

            LastEntryAt = entry.ReceivedAt;
        }
        catch (Exception ex)
        {
            log.Warning(ex, "CustomChat: dialogue translation failed");
        }
    }

    public void Dispose()
    {
        framework.Update -= OnFrameworkUpdate;
        toastGui.QuestToast -= OnQuestToast;
    }
}
