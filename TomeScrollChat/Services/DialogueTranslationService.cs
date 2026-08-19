using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Dalamud.Game.Gui.Toast;
using Dalamud.Game.Text.SeStringHandling;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.UI;
using TomeScrollChat.Models;

namespace TomeScrollChat.Services;

/// <summary>
/// Feeds <see cref="Windows.DialogueTranslationWindow"/> - watches three sources for new lines while
/// <see cref="Configuration.EnableDialogueTranslationWindow"/> is on, translates each via
/// <see cref="TranslationService.TranslateDialogueAsync"/> (a one-off dispatcher, sibling to the one the
/// input-box "Translate" action uses - bypasses the chat-message queue/backoff machinery entirely,
/// deliberately: that queue exists to survive *bursts* like reopening a tab with hundreds of backlog
/// messages, but dialogue/cutscene lines only ever advance one at a time at human reading pace, so
/// there's nothing to throttle here; unlike the input-box path, this one also frames the request as
/// FFXIV story text and remembers it for context when routed through Gemini - see that method's own doc
/// comment), and appends the result to <see cref="Entries"/>:
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
/// This service never hides itself during cutscenes - unlike <c>MainWindow</c>/<c>DetachedTabWindow</c>,
/// which check <c>Plugin.Condition.Any(ConditionFlag.WatchingCutscene, ...)</c> in their own
/// <c>DrawConditions()</c> to *optionally* hide (see <see cref="Configuration.HideChatDuringCutscenes"/>),
/// <see cref="Windows.DialogueTranslationWindow"/> always stays up through one - see that class's own
/// doc comment for the two-part fix this needed (a <c>DrawConditions()</c> bypass *and*
/// <c>Plugin.PluginInterface.UiBuilder.DisableCutsceneUiHide</c>, since Dalamud force-hides plugin
/// windows during cutscenes at its own level otherwise). <see cref="IsNativeDialogueOpen"/> is this
/// service's other contribution to that window's show/hide decision - not cutscene-related, but lets
/// it hide the instant an NPC conversation actually ends instead of only after an idle timeout.
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

    public IReadOnlyList<DialogueTranslationEntry> Entries
    {
        get
        {
            lock (entriesLock)
                return entries.ToArray();
        }
    }

    public DateTime LastEntryAt { get; private set; } = DateTime.MinValue;

    /// <summary>Whether the native NPC dialogue box (<c>AddonTalk</c>) or cutscene subtitle
    /// (<c>AddonTalkSubtitle</c>) is currently visible - used by <see cref="Windows.DialogueTranslationWindow"/>
    /// to hide itself the instant a conversation actually ends, rather than only after
    /// <see cref="Configuration.DialogueTranslationAutoHideSeconds"/> of idle time (per explicit user
    /// request, 2026-08-19). Only reflects these two addons, not quest toasts - those have no window
    /// of their own to signal against, so that case still relies on the idle timeout alone.</summary>
    public bool IsNativeDialogueOpen { get; private set; }

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
        {
            IsNativeDialogueOpen = false;
            return;
        }

        var talkOpen = CheckTalk();
        var subtitleOpen = CheckSubtitle();
        IsNativeDialogueOpen = talkOpen || subtitleOpen;
    }

    /// <summary>Returns whether the addon is currently visible, regardless of whether a new line was
    /// found this tick - <see cref="IsNativeDialogueOpen"/> needs "is it open right now," which is a
    /// different question from "did the text change."</summary>
    private unsafe bool CheckTalk()
    {
        var addon = gameGui.GetAddonByName<AddonTalk>("Talk");
        var visible = addon != null && addon->IsVisible;

        if (!visible)
        {
            lastTalkText = string.Empty; // next time the box opens (even with the same line) should re-translate
            return false;
        }

        var textNode = addon->GetTextNodeById(3);
        if (textNode == null)
            return true;

        var text = StripQuotes(textNode->NodeText.ToString());
        if (string.IsNullOrWhiteSpace(text) || text == lastTalkText)
            return true;

        lastTalkText = text;

        string? speaker = null;
        var speakerNode = addon->GetTextNodeById(2);
        if (speakerNode != null)
        {
            var speakerText = StripQuotes(speakerNode->NodeText.ToString());
            if (!string.IsNullOrWhiteSpace(speakerText))
                speaker = speakerText;
        }

        QueueTranslation(DialogueTranslationKind.NpcDialogue, speaker, text);
        return true;
    }

    /// <summary><c>AtkTextNode.NodeText</c> on <c>AddonTalk</c> comes back wrapped in a literal
    /// leading/trailing <c>"</c> character (confirmed live, 2026-08-17 - e.g. the raw text is
    /// <c>"Oswold"</c>, quote marks included) - stripped for both the speaker and body, since neither
    /// should actually show quoted in the translation window.</summary>
    private static string StripQuotes(string text) =>
        text.Length >= 2 && text[0] == '"' && text[^1] == '"' ? text[1..^1] : text;

    private unsafe bool CheckSubtitle()
    {
        var addon = gameGui.GetAddonByName<AddonTalkSubtitle>("TalkSubtitle");
        var visible = addon != null && addon->IsVisible;

        if (!visible)
        {
            lastSubtitleText = string.Empty;
            return false;
        }

        var text = addon->SubtitleText.ToString();
        if (string.IsNullOrWhiteSpace(text) || text == lastSubtitleText)
            return true;

        lastSubtitleText = text;
        QueueTranslation(DialogueTranslationKind.CutsceneSubtitle, null, text);
        return true;
    }

    private void OnQuestToast(ref SeString message, ref QuestToastOptions options, ref bool isHandled)
    {
        if (!configuration.EnableDialogueTranslationWindow)
            return;

        var text = message.TextValue;
        if (!string.IsNullOrWhiteSpace(text))
            QueueTranslation(DialogueTranslationKind.QuestNotice, null, text);
    }

    /// <summary>Retried up to this many times (per explicit user request - "если перевод сюжета не
    /// прошел по какой-либо причине") before a single dialogue line is given up on entirely - a null/
    /// empty result and a thrown exception are both treated as failures worth retrying, same as each
    /// other. Kept small: a dialogue line advances at human reading pace (see this class's own doc
    /// comment), so there's no bursty backlog to protect against the way the main chat translation
    /// queue's own backoff exists for - just enough attempts to ride out a transient network hiccup or
    /// a single flaky response, not to keep hammering a genuinely broken engine (which
    /// <see cref="TranslationService.TrackEngineHealth"/>'s own auto-switch-on-failure, fed by every one
    /// of these attempts too, already handles across calls).</summary>
    private const int MaxDialogueTranslationAttempts = 3;

    /// <summary>Multiplied by the attempt number for a short linear backoff between retries (2s, then
    /// 4s) - long enough to give a transient failure (rate limit, brief network blip) room to clear,
    /// short enough that a fully-given-up line still shows up within a few seconds of the original one,
    /// not distractingly late.</summary>
    private static readonly TimeSpan DialogueRetryBaseDelay = TimeSpan.FromSeconds(2);

    /// <summary>Fires the translation (retrying on failure, see <see cref="MaxDialogueTranslationAttempts"/>)
    /// and appends the result once it lands - not awaited by the caller (both call sites are synchronous
    /// framework-tick/event handlers), and each call is fully independent (no shared in-flight state to
    /// guard), so overlapping requests from back-to-back lines just complete and append in whatever
    /// order they finish, same as normal chat auto-translate.</summary>
    private async void QueueTranslation(DialogueTranslationKind kind, string? speaker, string originalText)
    {
        for (var attempt = 1; attempt <= MaxDialogueTranslationAttempts; attempt++)
        {
            try
            {
                var translated = await translationService.TranslateDialogueAsync(speaker, originalText, configuration.TranslateTargetLanguage).ConfigureAwait(false);
                if (!string.IsNullOrWhiteSpace(translated))
                {
                    var entry = new DialogueTranslationEntry(kind, speaker, originalText, translated, DateTime.UtcNow);
                    lock (entriesLock)
                    {
                        entries.Add(entry);
                        if (entries.Count > MaxEntries)
                            entries.RemoveAt(0);
                    }

                    LastEntryAt = entry.ReceivedAt;
                    return;
                }

                log.Warning("TomeScrollChat: dialogue translation ({Kind}) returned empty/null via {Engine} (attempt {Attempt}/{Max})",
                    kind, TranslationService.EngineLabel(translationService.ActiveEngine), attempt, MaxDialogueTranslationAttempts);
            }
            catch (Exception ex)
            {
                log.Warning(ex, "TomeScrollChat: dialogue translation failed (attempt {Attempt}/{Max})", attempt, MaxDialogueTranslationAttempts);
            }

            if (attempt < MaxDialogueTranslationAttempts)
                await Task.Delay(DialogueRetryBaseDelay * attempt).ConfigureAwait(false);
        }

        log.Warning("TomeScrollChat: dialogue translation ({Kind}) gave up after {Max} attempts", kind, MaxDialogueTranslationAttempts);
    }

    public void Dispose()
    {
        framework.Update -= OnFrameworkUpdate;
        toastGui.QuestToast -= OnQuestToast;
    }
}
