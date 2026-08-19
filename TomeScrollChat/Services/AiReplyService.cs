using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Dalamud.Plugin.Services;
using Newtonsoft.Json;
using TomeScrollChat.Models;

namespace TomeScrollChat.Services;

/// <summary>
/// Generates a suggested reply to a chat message via Gemini (<see cref="GeminiService"/>) - the
/// message right-click menu's "Generate AI Reply" (see <see cref="Windows.ChatMessageRenderer"/>),
/// inserted into the compose box for the player to review/edit, never sent automatically.
///
/// <para>Remembers a bounded history of past (sender, original message, generated reply) triples on
/// disk (see <see cref="Configuration.AiReplyMemoryEnabled"/>/<see cref="Configuration.AiReplyMemoryLimit"/>),
/// fed back as extra context on every new generation so replies stay consistent in tone/style rather
/// than each one starting cold - added per explicit user request ("нейронка должна запоминать историю
/// моих прошлых запросов на генерацию ответа и учитывать её в генерации новых ответов"). Deliberately
/// one global history shared across every conversation, not per-partner - simpler mental model ("the
/// AI remembers how you've been replying"), revisit if that turns out to leak an odd tone/reference
/// from one conversation into an unrelated one.</para>
/// </summary>
public sealed class AiReplyService
{
    public const string DefaultPrompt =
        "You are helping a Final Fantasy XIV player write a short, natural reply to an in-game chat message. " +
        "Match the tone and language of the original message. Keep it casual and concise, like real chat - " +
        "one or two sentences at most. Output ONLY the reply text itself - no quotation marks, no speaker " +
        "labels, no explanation.";

    private readonly GeminiService geminiService;
    private readonly Configuration configuration;
    private readonly IPluginLog log;
    private readonly NotificationService notificationService;
    private readonly string memoryPath;
    private readonly object memoryLock = new();
    private readonly List<AiReplyMemoryEntry> memory = new();

    public AiReplyService(string configDirectory, GeminiService geminiService, Configuration configuration, IPluginLog log, NotificationService notificationService)
    {
        this.geminiService = geminiService;
        this.configuration = configuration;
        this.log = log;
        this.notificationService = notificationService;
        memoryPath = Path.Combine(configDirectory, "ai_reply_memory.json");
        LoadMemory();
    }

    public bool IsConfigured => geminiService.IsConfigured;

    /// <summary>Snapshot of the remembered history - Settings' "AI Reply" section lists it so the
    /// player can see what's actually being fed back as context (and clear it via <see cref="ClearMemory"/>).</summary>
    public IReadOnlyList<AiReplyMemoryEntry> Memory
    {
        get
        {
            lock (memoryLock)
                return memory.ToArray();
        }
    }

    private const string RephrasePromptPrefix =
        "Rephrase the following message the player is about to send, in the same language, keeping " +
        "the same meaning and overall tone - just worded differently. Output ONLY the rephrased text " +
        "itself - no quotation marks, no explanation.";

    /// <summary>Rephrases whatever the player has already typed into the compose box (right-click ->
    /// "Rephrase" on the input box itself) - a one-off, stateless call, deliberately not fed through
    /// <see cref="BuildPrompt"/>/the remembered exchange history the way <see cref="GenerateReplyAsync"/>
    /// is: this is reshaping the player's own draft, not generating a reply to someone else, so the
    /// "reply" memory concept doesn't apply here. Same null-on-failure/self-explaining-toast contract
    /// as <see cref="GenerateReplyAsync"/>.</summary>
    public async Task<string?> RephraseAsync(string text)
    {
        if (!geminiService.IsConfigured)
        {
            notificationService.Show("Rephrasing needs a Gemini API key first (Settings > General).", NotificationSeverity.Warning);
            return null;
        }

        if (string.IsNullOrWhiteSpace(text))
            return null;

        // No generic "failed" toast here on a null result - GeminiService itself already shows a
        // specific one (bad key, rate limit, blocked, etc.) for every real failure past this point.
        var result = await geminiService.GenerateTextAsync($"{RephrasePromptPrefix}\n\n\"{text}\"").ConfigureAwait(false);
        return string.IsNullOrWhiteSpace(result) ? null : result.Trim().Trim('"');
    }

    private const string CorrectPromptPrefix =
        "Fix any spelling, grammar, and punctuation errors in the following message the player is " +
        "about to send, in the same language. Do not change its meaning, tone, wording, or slang - " +
        "only fix genuine errors. If there are no errors, output the message unchanged. Output ONLY " +
        "the corrected text itself - no quotation marks, no explanation.";

    /// <summary>Fixes spelling/grammar/punctuation in whatever the player has already typed (right-
    /// click -> "Fix errors" on the input box itself) - same shape as <see cref="RephraseAsync"/>
    /// (stateless, no reply-memory involvement), just a different instruction: correct mistakes only,
    /// don't otherwise reword like <see cref="RephraseAsync"/> does.</summary>
    public async Task<string?> CorrectAsync(string text)
    {
        if (!geminiService.IsConfigured)
        {
            notificationService.Show("Auto-correct needs a Gemini API key first (Settings > General).", NotificationSeverity.Warning);
            return null;
        }

        if (string.IsNullOrWhiteSpace(text))
            return null;

        // Same reasoning as RephraseAsync - GeminiService already toasts on any real failure.
        var result = await geminiService.GenerateTextAsync($"{CorrectPromptPrefix}\n\n\"{text}\"").ConfigureAwait(false);
        return string.IsNullOrWhiteSpace(result) ? null : result.Trim().Trim('"');
    }

    /// <summary>Generates a reply to <paramref name="originalMessage"/> (from <paramref name="senderName"/>)
    /// via Gemini, remembers the exchange (if <see cref="Configuration.AiReplyMemoryEnabled"/>), and
    /// returns the reply text - or null on any failure (not configured, network error, empty response),
    /// in which case a <see cref="NotificationService"/> toast already explains why.</summary>
    public async Task<string?> GenerateReplyAsync(string senderName, string originalMessage)
    {
        if (!geminiService.IsConfigured)
        {
            notificationService.Show("AI reply needs a Gemini API key first (Settings > General).", NotificationSeverity.Warning);
            return null;
        }

        if (string.IsNullOrWhiteSpace(originalMessage))
            return null;

        // Same reasoning as RephraseAsync/CorrectAsync - GeminiService already toasts on any real failure.
        var reply = await geminiService.GenerateTextAsync(BuildPrompt(senderName, originalMessage)).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(reply))
            return null;

        reply = reply.Trim().Trim('"');

        if (configuration.AiReplyMemoryEnabled)
        {
            lock (memoryLock)
            {
                memory.Add(new AiReplyMemoryEntry(senderName, originalMessage, reply, DateTime.UtcNow));
                var limit = Math.Max(1, configuration.AiReplyMemoryLimit);
                while (memory.Count > limit)
                    memory.RemoveAt(0);
                SaveMemory();
            }
        }

        return reply;
    }

    private string BuildPrompt(string senderName, string originalMessage)
    {
        var sb = new StringBuilder();
        sb.AppendLine(string.IsNullOrWhiteSpace(configuration.AiReplyPrompt) ? DefaultPrompt : configuration.AiReplyPrompt);

        if (configuration.AiReplyMemoryEnabled)
        {
            List<AiReplyMemoryEntry> snapshot;
            lock (memoryLock)
                snapshot = memory.ToList();

            if (snapshot.Count > 0)
            {
                sb.AppendLine();
                sb.AppendLine("For consistency, here are some of your own previous replies in this chat:");
                foreach (var entry in snapshot)
                    sb.AppendLine($"- {entry.SenderName} said \"{entry.OriginalMessage}\" -> you replied \"{entry.GeneratedReply}\"");
            }
        }

        sb.AppendLine();
        sb.AppendLine($"Now {senderName} just said: \"{originalMessage}\"");
        sb.AppendLine("Write your reply:");

        return sb.ToString();
    }

    /// <summary>Settings' "Clear AI reply memory" button.</summary>
    public void ClearMemory()
    {
        lock (memoryLock)
        {
            memory.Clear();
            SaveMemory();
        }
    }

    private void LoadMemory()
    {
        try
        {
            if (!File.Exists(memoryPath))
                return;

            var loaded = JsonConvert.DeserializeObject<List<AiReplyMemoryEntry>>(File.ReadAllText(memoryPath));
            if (loaded != null)
                memory.AddRange(loaded);
        }
        catch (Exception ex)
        {
            log.Warning(ex, "TomeScrollChat: failed to load AI reply memory");
        }
    }

    /// <summary>Called with <see cref="memoryLock"/> already held.</summary>
    private void SaveMemory()
    {
        try
        {
            File.WriteAllText(memoryPath, JsonConvert.SerializeObject(memory));
        }
        catch (Exception ex)
        {
            log.Warning(ex, "TomeScrollChat: failed to save AI reply memory");
        }
    }
}
