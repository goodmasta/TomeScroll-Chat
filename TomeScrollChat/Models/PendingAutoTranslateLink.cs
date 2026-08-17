namespace TomeScrollChat.Models;

/// <summary>An auto-translate dictionary phrase queued to be substituted for a "&lt;atlink&gt;"
/// placeholder in the compose box - see <see cref="Windows.AutoTranslatePicker"/> for how these get
/// picked and <see cref="Services.ChatSendService"/> for how they're actually sent (a raw
/// <c>MacroCode.Fixed</c> macro payload, *not* <see cref="Dalamud.Game.Text.SeStringHandling.Payloads.AutoTranslatePayload"/> -
/// see <c>ChatSendService</c>'s own doc comment for why, found by studying ChatTwo's working
/// implementation). <see cref="RowId"/> is the <c>Lumina.Excel.Sheets.Completion</c> row's own
/// identity - the first version of this feature used that sheet's separate <c>Key</c> column instead,
/// which turned out to be wrong (see <see cref="Services.AutoTranslatePhraseService"/>'s doc
/// comment).</summary>
public sealed record PendingAutoTranslateLink(uint Group, uint RowId, string DisplayText);
