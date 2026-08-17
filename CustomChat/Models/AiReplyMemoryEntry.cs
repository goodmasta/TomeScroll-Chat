using System;

namespace CustomChat.Models;

/// <summary>One past (message replied to, generated reply) pair remembered by
/// <see cref="Services.AiReplyService"/> - fed back as extra context on every future generation while
/// <see cref="Configuration.AiReplyMemoryEnabled"/> is on, so replies stay consistent with earlier ones
/// instead of each generation starting from a blank slate.</summary>
public sealed record AiReplyMemoryEntry(string SenderName, string OriginalMessage, string GeneratedReply, DateTime CreatedAt);
