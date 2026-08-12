namespace CustomChat.Utility;

/// <summary>Curated list of target languages for the "Translate" context-menu item (see
/// <see cref="Services.TranslationService"/>) - ISO 639-1 codes Google Translate's endpoint accepts.
/// Not exhaustive Google Translate coverage, just the languages likely relevant to FFXIV's playerbase.</summary>
public static class TranslationLanguageCatalog
{
    public static readonly (string Code, string Name)[] Entries =
    {
        ("en", "English"),
        ("ru", "Russian"),
        ("de", "German"),
        ("fr", "French"),
        ("ja", "Japanese"),
        ("zh-CN", "Chinese (Simplified)"),
        ("zh-TW", "Chinese (Traditional)"),
        ("ko", "Korean"),
        ("es", "Spanish"),
        ("pt", "Portuguese"),
        ("it", "Italian"),
        ("pl", "Polish"),
        ("uk", "Ukrainian"),
        ("nl", "Dutch"),
        ("tr", "Turkish"),
        ("ar", "Arabic"),
        ("sv", "Swedish"),
        ("fi", "Finnish"),
        ("cs", "Czech"),
        ("th", "Thai"),
    };
}
