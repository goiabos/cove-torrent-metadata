using System.Text;
using Cove.TorrentMetadata;

namespace Cove.TorrentMetadata.Tests;

/// <summary>
/// The bencode reader's rejection surface.
///
/// <see cref="BencodeReader"/> is the only code in the extension that reads bytes nobody vouched
/// for: files land in the watched folder from wherever the user put them, and a torrent is a
/// self-describing format whose length prefixes are attacker-controlled. Every guard below exists
/// because a corrupt or hostile prefix would otherwise become an allocation, an overflow or a
/// recursion the process does not survive — so each one is asserted individually rather than
/// through a single "malformed input is rejected" case, which cannot tell one guard from another.
///
/// Some of these are not guards and are marked as such: the Latin-1 fallback, the tolerance of
/// trailing data and the last-write-wins handling of a repeated key are documented behaviour, and a
/// test that pins them is what stops a later reader mistaking one for an oversight and "fixing" it.
///
/// Every fixture here is written in code. No torrent is committed to this repo, and the
/// byte-level cases are short literals rather than excerpts of anything real.
/// </summary>
public class BencodeReaderTests
{
    // ---------------------------------------------------------------------
    // Structural guards
    // ---------------------------------------------------------------------

    [Fact]
    public void Rejects_nesting_past_the_recursion_cap_instead_of_overflowing_the_stack()
    {
        // The cap is 32 levels. A hostile file can nest arbitrarily deeply for a handful of bytes per
        // level, so the recursion has to be bounded by the parser rather than by the stack: without
        // the cap this input is a crash, not a rejection, and a crash takes the whole host down.
        var deep = new string('l', 64) + new string('e', 64);

        Assert.False(BencodeReader.TryParse(Encoding.ASCII.GetBytes(deep), out _));
    }

    [Fact]
    public void Accepts_nesting_up_to_the_recursion_cap()
    {
        // The other side of the cap: it must not be so tight that a legitimately nested torrent —
        // info.files[].path is three levels before anything tracker-injected — trips it.
        var nested = new string('l', 16) + new string('e', 16);

        Assert.True(BencodeReader.TryParse(Encoding.ASCII.GetBytes(nested), out _));
    }

    // ---------------------------------------------------------------------
    // Element budget
    //
    // MaxDepth bounds how deep a file can nest; nothing bounded how wide it could get at a shallow
    // depth, and a list is a structural amplifier — a couple of source bytes per element becomes a
    // full BencodeValue plus a list or dictionary slot. The budget is 100,000 elements (every string,
    // integer, list and dictionary, dictionary keys included), sized against the corpus this extension
    // was built against: its largest pack, 1,913 video files, costs ~13,400 elements in its `files`
    // list alone (one dictionary, a length key and value, a path key and list, and typically two path
    // segments per file), and its heaviest tag list, separately, is 1,122 tags. The real worst case
    // measured is under 15,000; 100,000 leaves that comfortable room to grow without moving the number
    // again, while a hostile file still needs on the order of 200 KB of the minimal encoding to reach
    // it.
    // ---------------------------------------------------------------------

    [Fact]
    public void Accepts_a_list_at_exactly_the_element_budget()
    {
        // The outer list is itself one element, so 99,999 empty-string entries plus the list that
        // holds them lands exactly on the 100,000-element budget. The boundary is asserted on both
        // sides so an off-by-one either direction is caught rather than merely "large input works".
        var atBudget = "l" + string.Concat(Enumerable.Repeat("0:", 99_999)) + "e";

        Assert.True(BencodeReader.TryParse(Encoding.ASCII.GetBytes(atBudget), out _));
    }

    [Fact]
    public void Rejects_a_list_one_element_past_the_budget()
    {
        var overBudget = "l" + string.Concat(Enumerable.Repeat("0:", 100_000)) + "e";

        Assert.False(BencodeReader.TryParse(Encoding.ASCII.GetBytes(overBudget), out _));
    }

    [Fact]
    public void Counts_a_dictionary_key_against_the_budget_and_not_only_its_value()
    {
        // A key never passes through the same code path a value does — it is read directly inside
        // ParseDictionary rather than through ParseValue — so it needs its own charge or it would be
        // free to repeat without limit. Each pair here costs two elements (key, value) plus one for
        // the root dictionary, so 60,000 pairs alone clears the 100,000 budget on keys and values
        // together, without any list or nesting involved at all.
        var builder = new StringBuilder("d");
        for (var i = 0; i < 60_000; i++)
        {
            var key = i.ToString(System.Globalization.CultureInfo.InvariantCulture);
            builder.Append(key.Length).Append(':').Append(key).Append("i0e");
        }

        builder.Append('e');

        Assert.False(BencodeReader.TryParse(Encoding.ASCII.GetBytes(builder.ToString()), out _));
    }

    [Fact]
    public void Parses_the_largest_real_pack_shape_comfortably_inside_the_budget()
    {
        // A regression guard in the other direction: the largest pack this extension was measured
        // against, 1,913 video files, must keep parsing. If the budget is ever tightened without
        // re-checking this number, this is the test that catches it rather than a user's real library.
        var files = new (string Path, long Length)[1_913];
        for (var i = 0; i < files.Length; i++)
            files[i] = ($"Pack/scene-{i:D4}.mp4", 1_000_000L + i);

        var torrent = TorrentBytes.MultiFile("Pack", files);

        Assert.True(BencodeReader.TryParse(torrent, out _));
    }

    [Fact]
    public void Rejects_a_dictionary_that_ends_after_a_complete_pair_with_no_terminator()
    {
        // Distinct from "d3:key", which runs out of input while looking for the *value*. Here the
        // pair is complete and the input simply stops, which is the loop-top guard rather than the
        // end-of-data guard in ParseValue.
        Assert.False(BencodeReader.TryParse("d3:key3:val"u8, out _));
    }

    [Fact]
    public void Rejects_a_list_that_ends_with_no_terminator()
    {
        Assert.False(BencodeReader.TryParse("li1ei2e"u8, out _));
    }

    // ---------------------------------------------------------------------
    // Integers
    // ---------------------------------------------------------------------

    [Theory]
    [InlineData("i4x2e")]                    // a non-digit in the middle
    [InlineData("ie")]                       // no digits at all
    [InlineData("i-e")]                      // a bare sign with no digits after it
    [InlineData("i 42e")]                    // leading whitespace, which long.TryParse would accept
    [InlineData("i+42e")]                    // an explicit plus, which bencode has no grammar for
    [InlineData("i4.2e")]                    // a decimal point
    [InlineData("i99999999999999999999e")]   // wider than Int64, caught by the checked multiply
    public void Rejects_a_malformed_integer(string malformed)
    {
        Assert.False(BencodeReader.TryParse(Encoding.ASCII.GetBytes(malformed), out _));
    }

    [Fact]
    public void Reads_a_negative_integer()
    {
        // Negative values are legal bencode and the sign handling is a separate branch from the digit
        // loop, so the round-trip is asserted rather than inferred from "it parsed".
        var value = BencodeReader.Parse("d6:offseti-42ee"u8);

        Assert.Equal(-42L, value["offset"].AsInteger());
    }

    [Fact]
    public void Reads_the_widest_integers_the_checked_multiply_allows()
    {
        // The checked multiply that rejects an over-wide prefix must not reject the widest legal one.
        // long.MinValue itself is not among them: the digits are accumulated positively and negated
        // afterwards, and its magnitude is one past long.MaxValue. Nothing in a torrent is that
        // number, and widening the accumulator to reach it would give up the overflow guard.
        var value = BencodeReader.Parse(
            Encoding.ASCII.GetBytes($"d3:maxi{long.MaxValue}e3:mini{long.MinValue + 1}ee"));

        Assert.Equal(long.MaxValue, value["max"].AsInteger());
        Assert.Equal(long.MinValue + 1, value["min"].AsInteger());
    }

    [Fact]
    public void Rejects_long_MinValue_which_the_positive_accumulator_cannot_reach()
    {
        // The documented edge of the previous test, asserted so the asymmetry is a decision on record
        // rather than a surprise to whoever next reads the digit loop.
        Assert.False(BencodeReader.TryParse(Encoding.ASCII.GetBytes($"i{long.MinValue}e"), out _));
    }

    // ---------------------------------------------------------------------
    // String length prefixes
    //
    // These are the dangerous ones: the prefix decides how many bytes get allocated and how far the
    // cursor jumps, so every rejection here is a rejection of something a caller would otherwise
    // have to survive.
    // ---------------------------------------------------------------------

    [Theory]
    [InlineData("-1:x")]                     // a negative declared length
    [InlineData("67108865:x")]               // one byte past the 64 MB cap
    [InlineData(":abc")]                     // an empty digit run before the separator
    [InlineData("-:abc")]                    // a bare sign and no digits
    [InlineData("1a:x")]                     // a non-digit byte inside the prefix
    [InlineData("99999999999999999999:x")]   // a prefix wider than Int64
    [InlineData("abc")]                      // no ':' separator anywhere
    public void Rejects_a_malformed_string_length_prefix(string malformed)
    {
        Assert.False(BencodeReader.TryParse(Encoding.ASCII.GetBytes(malformed), out _));
    }

    [Fact]
    public void Rejects_an_oversized_length_before_allocating_for_it()
    {
        // The cap is checked against the *declared* length, so a four-gigabyte claim in a twelve-byte
        // file costs nothing. If the order were reversed the guard would be the attack.
        Assert.False(BencodeReader.TryParse("4294967296:x"u8, out _));
    }

    [Fact]
    public void Accepts_a_zero_length_string()
    {
        // Zero is not negative, and empty path segments are real — a siterip's path list opens with
        // one. The negative-length guard must not take zero with it.
        var value = BencodeReader.Parse("d4:name0:e"u8);

        Assert.Equal(string.Empty, value["name"].AsString());
    }

    // ---------------------------------------------------------------------
    // Documented behaviour, not guards
    // ---------------------------------------------------------------------

    [Fact]
    public void Decodes_a_non_UTF8_string_as_Latin1_rather_than_failing_or_replacing_it()
    {
        // 0xE9 is a lone continuation-less high byte: valid Latin-1, invalid UTF-8. Matching is by
        // size first and basename second, so a mis-encoded filename still has to round-trip to
        // *something* stable — replacement characters would collapse distinct names onto each other
        // and throwing would lose the file's size along with its name.
        var name = new byte[] { 0xE9, (byte)'t', (byte)'e', 0xE9 };
        var torrent = Concat("d4:name"u8.ToArray(), Encoding.ASCII.GetBytes($"{name.Length}:"), name, "e"u8.ToArray());

        Assert.True(BencodeReader.TryParse(torrent, out var value));
        Assert.Equal("éteé", value["name"].AsString());
    }

    [Fact]
    public void Decodes_a_non_UTF8_dictionary_key_as_Latin1_too()
    {
        // Keys go through the same decode as values, and a dialect looks its keys up by string. A key
        // that threw here would take the whole file with it.
        var key = new byte[] { 0xFF, 0xFE };
        var torrent = Concat(
            "d"u8.ToArray(),
            Encoding.ASCII.GetBytes($"{key.Length}:"),
            key,
            "i1ee"u8.ToArray());

        Assert.True(BencodeReader.TryParse(torrent, out var value));
        Assert.Equal(1L, value["ÿþ"].AsInteger());
    }

    [Fact]
    public void Ignores_trailing_data_after_the_root_value()
    {
        // Deliberate leniency, pinned here so it is not read as an oversight. Torrents pick up
        // trailing bytes in the wild — a padded write, a concatenated download, an editor appending a
        // newline — and the root dictionary is complete and correct in every one of those cases.
        // Rejecting them would discard a perfectly matchable file to enforce a rule that buys nothing:
        // the cursor never advances past the root value, so trailing bytes are never decoded.
        Assert.True(BencodeReader.TryParse("d4:name5:videoetrailing garbage"u8, out var value));
        Assert.Equal("video", value["name"].AsString());

        // Including the newline case specifically, which is the one that actually turns up.
        Assert.True(BencodeReader.TryParse("d4:name5:videoe\n"u8, out var withNewline));
        Assert.Equal("video", withNewline["name"].AsString());
    }

    [Fact]
    public void Keeps_the_last_value_when_a_key_is_repeated()
    {
        // Also deliberate: a repeated key is malformed bencode but the file is still usable, and
        // rejecting it would lose it for nothing.
        var value = BencodeReader.Parse("d4:name5:first4:name6:seconde"u8);

        Assert.Equal("second", value["name"].AsString());
    }

    // ---------------------------------------------------------------------
    // The structure BencodeTorrent reads on top of it
    // ---------------------------------------------------------------------

    [Fact]
    public void Rejects_a_torrent_whose_info_dictionary_has_no_name()
    {
        // `info.name` is the payload name and the single-file torrent's filename both. Without it
        // there is nothing to match on, so the file is not a torrent for this extension's purposes.
        Assert.False(BencodeTorrent.TryParse("d4:infod6:lengthi4242eee"u8, out _));
    }

    [Fact]
    public void Rejects_a_torrent_whose_root_is_not_a_dictionary()
    {
        Assert.False(BencodeTorrent.TryParse("li1ei2ee"u8, out _));
    }

    [Fact]
    public void Skips_a_files_entry_with_no_length()
    {
        // Length is the match key. An entry without one cannot be matched by size, and admitting it
        // with a fabricated zero would make it collide with every other zero-length entry.
        Assert.True(BencodeTorrent.TryParse(
            "d4:infod5:filesld4:pathl9:scene.mp4eed6:lengthi4242e4:pathl9:other.mp4eee4:name4:packee"u8,
            out var torrent));

        var video = Assert.Single(torrent.Videos);
        Assert.Equal("other.mp4", video.Path);
        Assert.Equal(4242L, video.Length);
    }

    [Fact]
    public void Skips_a_files_entry_whose_path_list_is_empty()
    {
        // An empty path list joins to the empty string, which has no extension and no basename. It
        // would be indistinguishable from every other pathless entry in the index.
        Assert.True(BencodeTorrent.TryParse(
            "d4:infod5:filesld6:lengthi1e4:pathleed6:lengthi4242e4:pathl9:other.mp4eee4:name4:packee"u8,
            out var torrent));

        var video = Assert.Single(torrent.Videos);
        Assert.Equal("other.mp4", video.Path);
    }

    [Fact]
    public void Skips_a_single_file_torrent_whose_payload_is_not_a_video()
    {
        Assert.True(BencodeTorrent.TryParse("d4:infod6:lengthi4242e4:name9:notes.txtee"u8, out var torrent));

        Assert.Empty(torrent.Videos);
        Assert.Equal("notes.txt", torrent.Name);
    }

    private static byte[] Concat(params byte[][] parts)
    {
        var buffer = new List<byte>();
        foreach (var part in parts)
            buffer.AddRange(part);
        return buffer.ToArray();
    }
}
