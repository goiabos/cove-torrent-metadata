namespace Cove.TorrentMetadata;

/// <summary>How a tag name that does not yet exist in the library should be spelled when created.</summary>
public enum TagNameStyle
{
    /// <summary>"Big Red Barn" — matches how most curated libraries name tags. The default.</summary>
    TitleCase,

    /// <summary>"big red barn" — the normalised form, dots replaced by spaces.</summary>
    Spaced,

    /// <summary>"big.red.barn" — the tracker's own spelling, kept verbatim.</summary>
    Dotted,
}

/// <summary>
/// Applies the configured spelling to tags the torrent would create.
///
/// This only ever affects *new* tags. A tag that resolves to an existing row keeps the library's own
/// spelling, because the library is the authority on how its tags are named — the setting decides what
/// happens when there is nothing to defer to.
///
/// Titlecasing is deliberately naive: it uppercases the first letter of each word and leaves the rest
/// alone. That keeps "69", "1on1" and "x265" intact, where a culture-aware ToTitleCase would not, and
/// it never has to guess about acronyms.
/// </summary>
public static class TagNameStyler
{
    public const string SettingKey = "tagNameStyle";

    public static TagNameStyle Parse(string? value) => value?.Trim().ToLowerInvariant() switch
    {
        "spaced" => TagNameStyle.Spaced,
        "dotted" => TagNameStyle.Dotted,
        _ => TagNameStyle.TitleCase,
    };

    public static string Serialize(TagNameStyle style) => style switch
    {
        TagNameStyle.Spaced => "spaced",
        TagNameStyle.Dotted => "dotted",
        _ => "titlecase",
    };

    /// <summary>
    /// Spells a proposed tag. <paramref name="normalized"/> is the dot-to-space form; <paramref name="source"/>
    /// is the original tag-list entry, needed for <see cref="TagNameStyle.Dotted"/>.
    /// </summary>
    public static string Apply(TagNameStyle style, string normalized, string? source) => style switch
    {
        TagNameStyle.Dotted => string.IsNullOrWhiteSpace(source) ? normalized : source,
        TagNameStyle.Spaced => normalized,
        _ => ToTitleCase(normalized),
    };

    private static string ToTitleCase(string value)
    {
        if (string.IsNullOrEmpty(value))
            return value;

        var characters = value.ToCharArray();
        var atWordStart = true;
        for (var i = 0; i < characters.Length; i++)
        {
            if (characters[i] == ' ')
            {
                atWordStart = true;
                continue;
            }

            if (atWordStart)
                characters[i] = char.ToUpperInvariant(characters[i]);
            atWordStart = false;
        }

        return new string(characters);
    }
}
