namespace Cove.TorrentMetadata;

/// <summary>
/// Minimal bencode reader, sufficient for reading .torrent files.
///
/// Bencode length-prefixes every byte string ("9:noa.amane" = a 10-byte string). Those prefixes are
/// part of the encoding, not part of the value — a hex view of a torrent's tag list makes them look
/// like trailing tracker IDs, which they are not. Reading through this type keeps callers from ever
/// seeing them.
///
/// Values are surfaced as <see cref="BencodeValue"/> rather than deserialized into a fixed shape: the
/// tracker-injected <c>metadata</c> dictionary is not part of BEP-3, so the reader stays structural and
/// the interpretation of tracker-injected keys lives in <see cref="ITorrentDialect"/>.
/// </summary>
public static class BencodeReader
{
    /// <summary>Maximum bytes a single string may declare, guarding against corrupt length prefixes.</summary>
    private const int MaxStringLength = 64 * 1024 * 1024;

    /// <summary>Bounds recursion so a hostile file cannot drive the parser into a stack overflow.</summary>
    private const int MaxDepth = 32;

    /// <summary>
    /// Parses the first bencode value in <paramref name="data"/>.
    ///
    /// <para>
    /// Bytes after that value are <b>deliberately ignored</b>, not overlooked. Torrents pick up
    /// trailing bytes in the wild — a padded write, a concatenated download, an editor appending a
    /// newline — and in every one of those cases the root dictionary is complete and correct.
    /// Rejecting the file would discard a matchable release to enforce a rule that buys nothing:
    /// the cursor never advances past the root value, so trailing bytes are never decoded, never
    /// allocated for and never surfaced to a caller. The size guards that matter all sit inside the
    /// value, where a hostile length prefix actually costs something.
    /// </para>
    ///
    /// <para>
    /// The consequence to know is that this is a reader, not a validator: parsing clean does not
    /// mean the input was exactly one bencode value. Nothing here needs it to be. Pinned by
    /// <c>Ignores_trailing_data_after_the_root_value</c> so a later reader does not mistake the
    /// leniency for an oversight and "fix" it.
    /// </para>
    /// </summary>
    public static BencodeValue Parse(ReadOnlySpan<byte> data)
    {
        var offset = 0;
        var budget = new ElementBudget();
        var value = ParseValue(data, ref offset, 0, budget);
        return value;
    }

    public static bool TryParse(ReadOnlySpan<byte> data, out BencodeValue value)
    {
        try
        {
            value = Parse(data);
            return true;
        }
        catch (FormatException)
        {
            value = BencodeValue.Empty;
            return false;
        }
    }

    private static BencodeValue ParseValue(ReadOnlySpan<byte> data, ref int offset, int depth, ElementBudget budget)
    {
        if (depth > MaxDepth)
            throw new FormatException($"Bencode nesting deeper than {MaxDepth} levels.");
        if (offset >= data.Length)
            throw new FormatException("Unexpected end of bencode data.");

        budget.Charge();

        return data[offset] switch
        {
            (byte)'d' => ParseDictionary(data, ref offset, depth, budget),
            (byte)'l' => ParseList(data, ref offset, depth, budget),
            (byte)'i' => ParseInteger(data, ref offset),
            _ => BencodeValue.ForBytes(ParseByteString(data, ref offset)),
        };
    }

    private static BencodeValue ParseDictionary(ReadOnlySpan<byte> data, ref int offset, int depth, ElementBudget budget)
    {
        offset++; // 'd'
        var entries = new Dictionary<string, BencodeValue>(StringComparer.Ordinal);
        while (true)
        {
            if (offset >= data.Length)
                throw new FormatException("Unterminated bencode dictionary.");
            if (data[offset] == (byte)'e')
            {
                offset++;
                return BencodeValue.ForDictionary(entries);
            }

            // Charged separately from the value: a key is a byte string exactly like any other and
            // allocates the same way, but it never passes through ParseValue to be charged there.
            var key = ParseByteString(data, ref offset);
            budget.Charge();
            var value = ParseValue(data, ref offset, depth + 1, budget);
            // Duplicate keys are malformed; last write wins rather than throwing, since a torrent with a
            // repeated key is still usable and rejecting it would lose an otherwise valid file.
            entries[DecodeUtf8(key)] = value;
        }
    }

    private static BencodeValue ParseList(ReadOnlySpan<byte> data, ref int offset, int depth, ElementBudget budget)
    {
        offset++; // 'l'
        var items = new List<BencodeValue>();
        while (true)
        {
            if (offset >= data.Length)
                throw new FormatException("Unterminated bencode list.");
            if (data[offset] == (byte)'e')
            {
                offset++;
                return BencodeValue.ForList(items);
            }

            items.Add(ParseValue(data, ref offset, depth + 1, budget));
        }
    }

    private static BencodeValue ParseInteger(ReadOnlySpan<byte> data, ref int offset)
    {
        offset++; // 'i'
        var end = data[offset..].IndexOf((byte)'e');
        if (end < 0)
            throw new FormatException("Unterminated bencode integer.");

        if (!TryParseAsciiInt64(data.Slice(offset, end), out var result))
            throw new FormatException("Malformed bencode integer.");

        offset += end + 1;
        return BencodeValue.ForInteger(result);
    }

    /// <summary>
    /// Reads a length-prefixed byte string.
    ///
    /// The separator search scans the whole remaining buffer before giving up, so a file with no
    /// ':' left in it costs a full-buffer scan per attempt rather than a bounded look-ahead. That is
    /// acceptable only because the input is already bounded: uploads are capped at
    /// <c>MaxTorrentBytes</c> (8 MB) and a watched-folder file is read whole before parsing, so
    /// the worst case is megabytes of <c>IndexOf</c>, which is vectorised. Noted rather than fixed
    /// because the sibling guards are all bounded and the asymmetry would otherwise read as an
    /// omission. If the reader is ever pointed at an unbounded stream, this becomes the first
    /// thing to bound.
    /// </summary>
    private static byte[] ParseByteString(ReadOnlySpan<byte> data, ref int offset)
    {
        var separator = data[offset..].IndexOf((byte)':');
        if (separator < 0)
            throw new FormatException("Malformed bencode string: missing ':' length separator.");

        if (!TryParseAsciiInt64(data.Slice(offset, separator), out var declared)
            || declared < 0
            || declared > MaxStringLength)
        {
            throw new FormatException("Malformed bencode string length.");
        }

        var length = (int)declared;

        var start = offset + separator + 1;
        if (start + length > data.Length)
            throw new FormatException("Bencode string length exceeds available data.");

        offset = start + length;
        return data.Slice(start, length).ToArray();
    }

    /// <summary>
    /// Parses a bencode ASCII integer. Hand-rolled rather than delegating to <c>long.TryParse</c> because
    /// bencode's grammar is stricter than the framework's: no sign but a leading '-', no whitespace, no
    /// digit separators, no culture. Rejecting those here keeps malformed input from being silently
    /// coerced into a plausible-looking length prefix.
    /// </summary>
    private static bool TryParseAsciiInt64(ReadOnlySpan<byte> digits, out long value)
    {
        value = 0;
        if (digits.Length == 0)
            return false;

        var negative = digits[0] == (byte)'-';
        if (negative)
            digits = digits[1..];
        if (digits.Length == 0)
            return false;

        foreach (var b in digits)
        {
            if (b < (byte)'0' || b > (byte)'9')
                return false;

            // Overflow here means a corrupt or hostile length prefix, not a real value.
            try
            {
                checked { value = (value * 10) + (b - (byte)'0'); }
            }
            catch (OverflowException)
            {
                return false;
            }
        }

        if (negative)
            value = -value;
        return true;
    }

    /// <summary>
    /// Torrent strings are conventionally UTF-8 but not guaranteed to be. Latin-1 is the fallback rather
    /// than replacement characters so a mis-encoded filename still round-trips to something matchable.
    /// </summary>
    internal static string DecodeUtf8(byte[] bytes)
    {
        try
        {
            return new System.Text.UTF8Encoding(false, throwOnInvalidBytes: true).GetString(bytes);
        }
        catch (ArgumentException)
        {
            return System.Text.Encoding.Latin1.GetString(bytes);
        }
    }
}

/// <summary>
/// Counts elements across one <see cref="BencodeReader.Parse"/> call, so a single small file cannot
/// force allocation of an unbounded object graph regardless of how it distributes that width across
/// depth. A plain mutable counter rather than a <c>ref int</c> threaded through every parse
/// method: the recursive shape already passes several parameters by value, and a reference type here
/// reads no differently at each call site while keeping the threading itself boring.
/// </summary>
internal sealed class ElementBudget
{
    /// <summary>
    /// Bounds how many bencode elements (strings, integers, lists and dictionaries — every dictionary
    /// key counts too, since it allocates the same way a value does) one parse may produce, regardless
    /// of depth.
    ///
    /// <see cref="BencodeReader"/>'s own <c>MaxDepth</c> bounds how *deep* a file can nest; nothing
    /// bounded how *wide* it could get at a shallow depth, and a list is structurally an amplifier — a
    /// couple of bytes of source per element becomes a full <see cref="BencodeValue"/> plus a list or
    /// dictionary slot. Measured at depth 2 that is roughly a 35x blow-up from source bytes to the
    /// graph built from them, so "small file, huge object graph" costs nothing to construct without a
    /// budget.
    ///
    /// The number is sized against the corpus this extension was built against, not against a
    /// plausible one. Its largest pack (1,913 video files) is dominated by its <c>files</c> list: each
    /// entry is a dictionary of a length and a path, and a path is itself a list of segments — one
    /// dictionary, one length key, one length value, one path key, one path-list, and (typically) two
    /// path segments is 7 elements per file, so ~13,400 for that list alone. Its heaviest tag list,
    /// separately, is 1,122 tags — another ~1,100 elements if it sat on the very same file, which
    /// the corpus never actually shows. Summed, the worst *real* torrent measured is under 15,000
    /// elements. 100,000 gives that room to grow — new metadata fields, deeper path nesting — without
    /// moving this number again, while still refusing a hostile file cheaply: reaching the cap with the
    /// minimal encoding (a flat list of empty strings, "0:" per element) takes on the order of 200 KB
    /// of source, so the budget trips long before a folder-path file even approaches
    /// <c>MaxTorrentBytes</c>.
    /// </summary>
    private const int MaxElements = 100_000;

    private int _count;

    public void Charge()
    {
        if (++_count > MaxElements)
            throw new FormatException($"Bencode value exceeds {MaxElements} elements.");
    }
}

/// <summary>A decoded bencode value. Exactly one of the accessors is meaningful, per <see cref="Kind"/>.</summary>
public readonly struct BencodeValue
{
    public enum ValueKind { None, Bytes, Integer, List, Dictionary }

    public ValueKind Kind { get; private init; }
    private byte[]? Bytes { get; init; }
    private long Integer { get; init; }
    private List<BencodeValue>? Items { get; init; }
    private Dictionary<string, BencodeValue>? Entries { get; init; }

    public static BencodeValue Empty => new() { Kind = ValueKind.None };

    internal static BencodeValue ForBytes(byte[] value) => new() { Kind = ValueKind.Bytes, Bytes = value };
    internal static BencodeValue ForInteger(long value) => new() { Kind = ValueKind.Integer, Integer = value };
    internal static BencodeValue ForList(List<BencodeValue> value) => new() { Kind = ValueKind.List, Items = value };
    internal static BencodeValue ForDictionary(Dictionary<string, BencodeValue> value) => new() { Kind = ValueKind.Dictionary, Entries = value };

    public IReadOnlyList<BencodeValue> AsList() => Items ?? [];

    public BencodeValue this[string key] =>
        Entries is not null && Entries.TryGetValue(key, out var value) ? value : Empty;

    public bool Has(string key) => Entries?.ContainsKey(key) == true;

    public string? AsString() => Kind == ValueKind.Bytes && Bytes is not null
        ? BencodeReader.DecodeUtf8(Bytes)
        : null;

    public long? AsInteger() => Kind == ValueKind.Integer ? Integer : null;

    /// <summary>Strings in a bencode list, skipping any non-string entries.</summary>
    public IEnumerable<string> AsStringList()
    {
        foreach (var item in AsList())
        {
            if (item.AsString() is { } text)
                yield return text;
        }
    }
}
