using System.Text;

namespace WinUtils.Common.IO;

/// <summary>
/// Finds line delimiters in raw bytes, for tools that copy input byte for byte (head, tail).
/// In UTF-16 a delimiter is two bytes at an even offset, so a 0x0A inside another character
/// isn't mistaken for a newline.
/// </summary>
public sealed class ByteLines
{
    private readonly byte[] _pattern;

    private ByteLines(Encoding encoding, int preambleLength, char delimiter)
    {
        Encoding = encoding;
        PreambleLength = preambleLength;
        IsUtf16 = encoding is UnicodeEncoding;
        byte d = (byte)delimiter;
        _pattern = !IsUtf16 ? [d]
            : encoding.Equals(Encoding.BigEndianUnicode) ? [0, d]
            : [d, 0];
    }

    public Encoding Encoding { get; }
    public int PreambleLength { get; }
    public bool IsUtf16 { get; }

    /// <summary>Size of a delimiter in bytes: 1, or 2 for UTF-16.</summary>
    public int Unit => _pattern.Length;

    /// <summary>Detects the encoding from the first block of the input.</summary>
    public static ByteLines Detect(ReadOnlySpan<byte> head, char delimiter = '\n')
    {
        var (encoding, preamble) = LineReader.Detect(head, complete: false);
        return new ByteLines(encoding, preamble, delimiter);
    }

    /// <summary>First delimiter in <paramref name="span"/>, which starts at <paramref name="offset"/> in the file.</summary>
    public int IndexOf(ReadOnlySpan<byte> span, long offset)
    {
        if (Unit == 1) return span.IndexOf(_pattern[0]);
        for (int from = 0; ;)
        {
            int idx = span[from..].IndexOf(_pattern);
            if (idx < 0) return -1;
            idx += from;
            if ((offset + idx) % 2 == 0) return idx;
            from = idx + 1;
        }
    }

    /// <summary>Last delimiter in <paramref name="span"/>, which starts at <paramref name="offset"/> in the file.</summary>
    public int LastIndexOf(ReadOnlySpan<byte> span, long offset)
    {
        if (Unit == 1) return span.LastIndexOf(_pattern[0]);
        for (int end = span.Length; ;)
        {
            int idx = span[..end].LastIndexOf(_pattern);
            if (idx < 0) return -1;
            if ((offset + idx) % 2 == 0) return idx;
            end = idx + 1;
        }
    }

    public long Count(ReadOnlySpan<byte> span, long offset)
    {
        if (Unit == 1) return span.Count(_pattern[0]);
        long count = 0;
        for (int pos = 0, idx; (idx = IndexOf(span[pos..], offset + pos)) >= 0; pos += idx + Unit) count++;
        return count;
    }

    /// <summary>Returns the offset just past the first <paramref name="count"/> delimiters at or after <paramref name="dataStart"/>.</summary>
    public long SkipLines(Stream stream, long count, long dataStart)
    {
        if (count <= 0) return dataStart;
        var buffer = new byte[64 * 1024];
        long offset = dataStart;
        stream.Position = offset;
        while (true)
        {
            int n = stream.Read(buffer, 0, buffer.Length);
            if (n == 0) return stream.Length;
            n -= n % Unit; // keep reads aligned to whole UTF-16 code units
            if (n == 0) return stream.Length;
            for (int pos = 0, idx; (idx = IndexOf(buffer.AsSpan(pos, n - pos), offset + pos)) >= 0;)
            {
                pos += idx + Unit;
                if (--count == 0) return offset + pos;
            }
            offset += n;
            stream.Position = offset;
        }
    }

    public bool EndsWithDelimiter(ReadOnlySpan<byte> span, long offset) =>
        span.EndsWith(_pattern) && (offset + span.Length - Unit) % Unit == 0;

    /// <summary>
    /// Returns where the last <paramref name="count"/> lines of a seekable stream begin. A final
    /// line without a delimiter still counts as a line. Reads backwards from the end, so the cost
    /// depends on the size of those lines, not the file.
    /// </summary>
    public long FindStartOfLastLines(Stream stream, long count, long dataStart)
    {
        long end = stream.Length;
        if (count <= 0) return end;
        var buffer = new byte[64 * 1024];

        // A delimiter at the very end terminates the last line rather than starting another.
        long searchEnd = end;
        if (end - dataStart >= Unit)
        {
            stream.Position = end - Unit;
            stream.ReadExactly(buffer, 0, Unit);
            if (EndsWithDelimiter(buffer.AsSpan(0, Unit), end - Unit)) searchEnd = end - Unit;
        }

        long seen = 0;
        for (long pos = searchEnd; pos > dataStart;)
        {
            long start = Math.Max(dataStart, pos - buffer.Length);
            start -= start % Unit;
            if (start < dataStart) start = dataStart;
            int length = (int)(pos - start);
            stream.Position = start;
            stream.ReadExactly(buffer, 0, length);
            for (int idx = length; (idx = LastIndexOf(buffer.AsSpan(0, idx), start)) >= 0;)
            {
                if (++seen == count) return start + idx + Unit;
            }
            pos = start;
        }
        return dataStart;
    }
}
