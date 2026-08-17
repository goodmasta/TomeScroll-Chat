using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Dalamud.Plugin.Services;
using Lumina.Text.Payloads;
using Lumina.Text.ReadOnly;

namespace CustomChat.Services;

/// <summary>One row of the game's own auto-translate dictionary - see <see cref="AutoTranslatePhraseService"/>.
/// <paramref name="RowId"/> (the <c>Lumina.Excel.Sheets.Completion</c> row's own identity for a
/// self-contained phrase, or the *referenced sheet's* row id for an expanded one - see
/// <see cref="AutoTranslatePhraseService"/>'s own doc comment) is what actually has to be encoded -
/// see <see cref="Services.ChatSendService"/>'s own doc comment for how that was found.</summary>
public sealed record AutoTranslatePhrase(uint Group, uint RowId, string GroupTitle, string Text);

/// <summary>
/// Source data for <see cref="Windows.AutoTranslatePicker"/> - the game's own auto-translate dictionary,
/// read from <c>Lumina.Excel.Sheets.Completion</c>, following ChatTwo's own working implementation
/// (<c>Infiziert90/ChatTwo</c>, <c>Util/AutoTranslate.cs</c>, downloaded and read in full at the user's
/// suggestion - a first pass via WebFetch's lossy summary had this filter backwards once already, so
/// this was built from the literal source, not a paraphrase of it).
///
/// <c>AllEntries()</c> there processes each <c>Completion</c> row's <c>LookupTable</c> three ways,
/// **all three implemented here**, matching the same encoding rule either way (only the row id half of
/// the pair differs):
/// <list type="bullet">
/// <item><description><b>Empty</b> (<c>""</c>) - self-contained phrase; <see cref="AutoTranslatePhrase.RowId"/>
/// is this <c>Completion</c> row's own <c>RowId</c>, text is its own <c>Text</c>.</description></item>
/// <item><description><b><c>"@"</c></b> - category/navigation placeholder (e.g. <c>Text == "[Battle]"</c>,
/// matching the native Tab menu's own "square brackets = leads to a sub-list" convention) - skipped
/// entirely, not a real phrase.</description></item>
/// <item><description><b>Anything else</b> - a small expression naming another sheet, optionally with a
/// row/column selector: <c>SheetName</c>, <c>SheetName[N]</c> (single row), <c>SheetName[N-M]</c>
/// (inclusive row range), <c>SheetName[col-N]</c> (column N instead of the default 0), any of those
/// comma-separated, plus a <c>noun</c> marker this plugin ignores (it only affects grammar-aware noun
/// forms, not which text/row gets used). No selector at all means every row, column 0. Each resolved
/// row expands into its own <see cref="AutoTranslatePhrase"/>, keyed by this <c>Completion</c> row's own
/// <see cref="AutoTranslatePhrase.Group"/> but the *referenced sheet's* row id (not the `Completion`
/// row's <c>RowId</c>) - e.g. this is where minion/mount names (reported missing, "fatcat" - a real
/// minion name - wasn't showing up before this) come from, `LookupTable` pointing at whichever sheet
/// holds them.</description></item>
/// </list>
///
/// <para>Expanding every such reference (some point at genuinely large sheets) is measurably slow -
/// ChatTwo itself calls this out explicitly (its own <c>PreloadCache()</c> doc comment: "the first
/// message will take a long time to send" if this isn't warmed up ahead of time) - so
/// <see cref="Preload"/> kicks the whole load off on a background thread from <c>Plugin.cs</c>'s
/// constructor rather than letting it happen lazily on the main thread the first time Tab is pressed,
/// which would otherwise hitch the UI right when the player's mid-typing.</para>
/// </summary>
public sealed class AutoTranslatePhraseService
{
    private readonly IDataManager dataManager;
    private readonly IPluginLog log;
    private readonly Lazy<IReadOnlyList<AutoTranslatePhrase>> phrases;

    public AutoTranslatePhraseService(IDataManager dataManager, IPluginLog log)
    {
        this.dataManager = dataManager;
        this.log = log;
        phrases = new Lazy<IReadOnlyList<AutoTranslatePhrase>>(Load);
    }

    public IReadOnlyList<AutoTranslatePhrase> Phrases => phrases.Value;

    /// <summary>Warms up <see cref="Phrases"/> on a background thread - see this class's own doc
    /// comment for why that matters. Safe to call more than once; <see cref="Lazy{T}"/> only actually
    /// runs <see cref="Load"/> the first time.</summary>
    public void Preload() => Task.Run(() => phrases.Value);

    private IReadOnlyList<AutoTranslatePhrase> Load()
    {
        try
        {
            var sheet = dataManager.GetExcelSheet<Lumina.Excel.Sheets.Completion>();
            if (sheet == null)
                return Array.Empty<AutoTranslatePhrase>();

            var result = new List<AutoTranslatePhrase>();
            foreach (var row in sheet)
            {
                var lookup = FlattenLookupTable(row.LookupTable);
                var groupTitle = row.GroupTitle.ToString();

                if (lookup.Length == 0)
                {
                    var text = row.Text.ToString();
                    if (!string.IsNullOrWhiteSpace(text))
                        result.Add(new AutoTranslatePhrase(row.Group, row.RowId, groupTitle, text));
                }
                else if (lookup != "@")
                {
                    ExpandLookup(row.Group, groupTitle, lookup, result);
                }
            }

            return result
                .OrderBy(p => p.GroupTitle, StringComparer.OrdinalIgnoreCase)
                .ThenBy(p => p.Text, StringComparer.OrdinalIgnoreCase)
                .ToArray();
        }
        catch (Exception ex)
        {
            log.Warning(ex, "CustomChat: failed to load the auto-translate dictionary sheet");
            return Array.Empty<AutoTranslatePhrase>();
        }
    }

    /// <summary><b>Fixed 2026-08-17</b>: a plain <c>row.LookupTable.ToString()</c> silently drops any
    /// embedded numeric macro payload - some of the newer/larger lookup expressions (e.g. "TextCommand"'s
    /// column selectors) encode their numbers as a <c>MacroCode.Num</c> expression payload rather than
    /// literal text, which <c>ToString()</c> just skips instead of rendering, so <c>"col-0,col-1"</c>
    /// came out as <c>"col-,col-"</c> - malformed enough that <see cref="ParseLookup"/> couldn't
    /// recover a real column index, and the fallback default (column 0) didn't exist on that sheet,
    /// throwing <c>ArgumentOutOfRangeException</c> in <see cref="ExpandLookup"/> (caught there, but the
    /// whole category silently failed to expand). Fixed by walking payloads exactly like ChatTwo's own
    /// <c>AllEntries()</c> does (read directly, not paraphrased, per this class's own doc comment) -
    /// text payloads contribute their literal text, <c>Num</c> macro payloads contribute their decoded
    /// integer value as digits, anything else contributes a harmless placeholder that just won't match
    /// any real selector syntax.</summary>
    private static string FlattenLookupTable(ReadOnlySeString lookupTable)
    {
        var sb = new StringBuilder();
        foreach (var payload in lookupTable)
        {
            if (payload.Type == ReadOnlySePayloadType.Text)
            {
                sb.Append(Encoding.UTF8.GetString(payload.Body.Span));
            }
            else if (payload.MacroCode == MacroCode.Num && payload.TryGetExpression(out var num) && num.TryGetInt(out var value))
            {
                sb.Append(value.ToString(CultureInfo.InvariantCulture));
            }
            else
            {
                sb.Append(",,,unexpected macro code,,,");
            }
        }

        return sb.ToString();
    }

    /// <summary>Resolves one <c>LookupTable</c> expression (see this class's own doc comment for the
    /// grammar) against the sheet it names, appending one <see cref="AutoTranslatePhrase"/> per
    /// resolved (row, column) cell.</summary>
    private void ExpandLookup(uint group, string groupTitle, string lookup, List<AutoTranslatePhrase> result)
    {
        try
        {
            var (sheetName, rowRanges, columns) = ParseLookup(lookup.Replace(" ", string.Empty));
            var referenced = dataManager.GetExcelSheet<Lumina.Excel.RawRow>(null, sheetName);
            if (referenced == null || referenced.Count == 0)
                return;

            if (columns.Count == 0)
                columns.Add(0);

            if (rowRanges.Count == 0)
            {
                // No explicit row selector - every row. Uses the highest actual RowId (not Count-1),
                // since sheets aren't always densely packed from 0 - same reasoning ChatTwo's own
                // AllEntries() comment gives for the same choice.
                var maxRowId = referenced.GetRowAt(referenced.Count - 1).RowId;
                rowRanges.Add((0, (int)maxRowId));
            }

            foreach (var (start, end) in rowRanges)
            {
                for (var i = start; i <= end; i++)
                {
                    if (!referenced.TryGetRow((uint)i, out var referencedRow))
                        continue;

                    foreach (var column in columns)
                    {
                        var text = referencedRow.ReadStringColumn(column).ToString();
                        if (!string.IsNullOrWhiteSpace(text))
                            result.Add(new AutoTranslatePhrase(group, (uint)i, groupTitle, text));
                    }
                }
            }
        }
        catch (Exception ex)
        {
            log.Warning(ex, "CustomChat: failed to expand auto-translate lookup '{Lookup}'", lookup);
        }
    }

    /// <summary>Parses <c>SheetName</c> or <c>SheetName[selector1,selector2,...]</c> - see this class's
    /// own doc comment for the grammar. Deliberately hand-rolled rather than a parser-combinator library
    /// (ChatTwo uses Pidgin) - the grammar is small enough not to need one.</summary>
    private static (string SheetName, List<(int Start, int End)> RowRanges, List<int> Columns) ParseLookup(string lookup)
    {
        var bracket = lookup.IndexOf('[');
        if (bracket < 0)
            return (lookup, new List<(int, int)>(), new List<int>());

        var sheetName = lookup[..bracket];
        var selectorText = lookup[(bracket + 1)..].TrimEnd(']');
        var rowRanges = new List<(int, int)>();
        var columns = new List<int>();

        foreach (var part in selectorText.Split(',', StringSplitOptions.RemoveEmptyEntries))
        {
            if (part.StartsWith("col-", StringComparison.Ordinal))
            {
                if (int.TryParse(part.AsSpan(4), out var column))
                    columns.Add(column);
            }
            else if (part == "noun")
            {
                // Grammar-aware noun form marker - doesn't affect which row/column gets used, so
                // nothing to do with it here.
            }
            else
            {
                var dash = part.IndexOf('-');
                if (dash > 0 && int.TryParse(part[..dash], out var start) && int.TryParse(part[(dash + 1)..], out var end))
                    rowRanges.Add((start, end));
                else if (int.TryParse(part, out var single))
                    rowRanges.Add((single, single));
            }
        }

        return (sheetName, rowRanges, columns);
    }
}
