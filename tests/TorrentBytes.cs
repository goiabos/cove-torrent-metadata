using System.Text;

namespace Cove.TorrentMetadata.Tests;

/// <summary>
/// Bencode fixtures, built in code.
///
/// No test may depend on a real .torrent — none is committed to this repo and a contributor clones it
/// without a corpus — so anything needing real bytes writes them here. Shared rather than copied per
/// suite: two writers would be two definitions of the same format, and the one that drifts is never
/// the one being read when a test fails.
///
/// Only the members the extension actually reads are emitted. There is no <c>pieces</c>, no
/// <c>piece length</c> and no announce URL, because nothing downstream looks at them — piece hashes
/// are deliberately unused (<see cref="TorrentIndex"/>), so a fixture carrying them would suggest a
/// dependency that does not exist.
/// </summary>
internal static class TorrentBytes
{
    /// <summary>
    /// A comment URL carrying <paramref name="torrentId"/>, which is where <c>TorrentRelease.TorrentId</c>
    /// reads it from. The host is deliberately <c>tracker.invalid</c>: a fixture must not name a real one.
    /// </summary>
    public static string CommentFor(string torrentId) => $"https://tracker.invalid/torrents.php?id={torrentId}";

    /// <summary>A single-file torrent: <c>info.name</c> is the file and <c>info.length</c> its size.</summary>
    public static byte[] SingleFile(string name, long length, string? comment = null)
    {
        var buffer = new MemoryStream();
        buffer.WriteByte((byte)'d');
        WriteComment(buffer, comment);
        WriteString(buffer, "info");
        buffer.WriteByte((byte)'d');
        WriteString(buffer, "length");
        WriteInteger(buffer, length);
        WriteString(buffer, "name");
        WriteString(buffer, name);
        buffer.WriteByte((byte)'e');
        buffer.WriteByte((byte)'e');
        return buffer.ToArray();
    }

    /// <summary>A multi-file torrent: <c>info.name</c> is the payload folder and each entry has its own path.</summary>
    public static byte[] MultiFile(string name, params (string Path, long Length)[] files) =>
        MultiFile(name, null, files);

    /// <summary>The same, carrying a comment — the only place a tracker's torrent id comes from.</summary>
    public static byte[] MultiFile(string name, string? comment, (string Path, long Length)[] files)
    {
        var buffer = new MemoryStream();
        buffer.WriteByte((byte)'d');
        WriteComment(buffer, comment);
        WriteString(buffer, "info");
        buffer.WriteByte((byte)'d');
        WriteString(buffer, "files");
        buffer.WriteByte((byte)'l');
        foreach (var (path, length) in files)
        {
            buffer.WriteByte((byte)'d');
            WriteString(buffer, "length");
            WriteInteger(buffer, length);
            WriteString(buffer, "path");
            buffer.WriteByte((byte)'l');
            foreach (var segment in path.Split('/'))
                WriteString(buffer, segment);
            buffer.WriteByte((byte)'e');
            buffer.WriteByte((byte)'e');
        }

        buffer.WriteByte((byte)'e');
        WriteString(buffer, "name");
        WriteString(buffer, name);
        buffer.WriteByte((byte)'e');
        buffer.WriteByte((byte)'e');
        return buffer.ToArray();
    }

    /// <summary>
    /// Written before <c>info</c> because bencode dictionaries are key-sorted and "comment" precedes it.
    /// Our own reader does not care, but a fixture only that reader accepts is a fixture that cannot
    /// catch us drifting away from the format.
    /// </summary>
    private static void WriteComment(Stream to, string? comment)
    {
        if (comment is null)
            return;

        WriteString(to, "comment");
        WriteString(to, comment);
    }

    /// <summary>Bencode length-prefixes in bytes, not characters.</summary>
    private static void WriteString(Stream to, string value)
    {
        var bytes = Encoding.UTF8.GetBytes(value);
        to.Write(Encoding.ASCII.GetBytes($"{bytes.Length}:"));
        to.Write(bytes);
    }

    private static void WriteInteger(Stream to, long value) => to.Write(Encoding.ASCII.GetBytes($"i{value}e"));
}
