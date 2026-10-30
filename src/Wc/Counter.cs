using System.Globalization;
using System.Text;
using WinUtils.Common.IO;

namespace WinUtils.Wc;

internal sealed class Counts
{
    public long Lines, Words, Chars, Bytes, MaxLineLength;

    public void Add(Counts other)
    {
        Lines += other.Lines;
        Words += other.Words;
        Chars += other.Chars;
        Bytes += other.Bytes;
        MaxLineLength = Math.Max(MaxLineLength, other.MaxLineLength);
    }
}

/// <summary>
/// Counts one input. Pure-ASCII blocks are counted directly on the bytes; blocks with other
/// characters are decoded (UTF-8, UTF-16 or Latin-1, detected from the first block) so words,
/// characters and line widths are counted by character rather than byte.
/// </summary>
internal sealed class Counter(bool lines, bool words, bool chars, bool maxLength)
{
    private const int BlockSize = 256 * 1024;

    // ASCII whitespace, as isspace() in the C locale: space, \t, \n, \v, \f, \r.
    private static readonly bool[] AsciiSpace = Enumerable.Range(0, 128).Select(c => c is ' ' or >= '\t' and <= '\r').ToArray();

    private bool _inWord;
    private long _column;

    public Counts Count(Stream stream)
    {
        var counts = new Counts();
        _inWord = false;
        _column = 0;

        // Bytes alone come from the file size, without reading anything.
        if (!lines && !words && !chars && !maxLength && stream.CanSeek)
        {
            counts.Bytes = stream.Length - stream.Position;
            return counts;
        }

        var buffer = new byte[BlockSize];
        int n = stream.Read(buffer, 0, buffer.Length);
        if (n == 0) return counts;
        var (encoding, preamble) = LineReader.Detect(buffer.AsSpan(0, n), complete: n < buffer.Length);
        bool utf16 = encoding is UnicodeEncoding;
        bool bytesOnly = !words && !chars && !maxLength && !utf16;
        var decoder = encoding.GetDecoder();
        var decoded = new char[BlockSize + 2];
        bool pending = false; // the decoder may hold part of a character
        bool first = true;

        for (; n > 0; n = stream.Read(buffer, 0, buffer.Length))
        {
            counts.Bytes += n;
            var span = buffer.AsSpan(0, n);
            if (bytesOnly)
            {
                counts.Lines += span.Count((byte)'\n');
            }
            else if (!utf16 && !pending && Ascii.IsValid(span))
            {
                CountAscii(span, counts);
            }
            else
            {
                // A UTF-16 byte-order mark isn't text; a UTF-8 one counts as a character, as in GNU wc.
                if (first && utf16) span = span[preamble..];
                int length = decoder.GetChars(span, decoded, flush: false);
                CountChars(decoded.AsSpan(0, length), counts);
                pending = span.Length > 0 && span[^1] >= 0x80;
            }
            first = false;
        }
        if (pending)
        {
            int length = decoder.GetChars([], decoded, flush: true);
            CountChars(decoded.AsSpan(0, length), counts);
        }
        counts.MaxLineLength = Math.Max(counts.MaxLineLength, _column);
        return counts;
    }

    private void CountAscii(ReadOnlySpan<byte> span, Counts counts)
    {
        counts.Lines += span.Count((byte)'\n');
        if (chars) counts.Chars += span.Length;
        if (!words && !maxLength) return;
        foreach (byte b in span)
        {
            if (words)
            {
                bool space = AsciiSpace[b];
                if (!space && !_inWord) counts.Words++;
                _inWord = !space;
            }
            if (maxLength) Advance(b, b is >= 0x20 and < 0x7F ? 1 : 0, counts); // controls take no space
        }
    }

    private void CountChars(ReadOnlySpan<char> text, Counts counts)
    {
        for (int i = 0; i < text.Length; i++)
        {
            char c = text[i];
            int codePoint = c;
            if (char.IsHighSurrogate(c) && i + 1 < text.Length && char.IsLowSurrogate(text[i + 1]))
                codePoint = char.ConvertToUtf32(c, text[++i]);

            if (c == '\n') counts.Lines++;
            if (chars) counts.Chars++;
            if (words)
            {
                bool space = IsSpace(codePoint);
                if (!space && !_inWord) counts.Words++;
                _inWord = !space;
            }
            if (maxLength) Advance(codePoint, Width(codePoint), counts);
        }
    }

    /// <summary>Tracks the display column for -L: tabs go to the next multiple of 8, CR/LF/FF end a line.</summary>
    private void Advance(int c, int width, Counts counts)
    {
        switch (c)
        {
            case '\n' or '\r' or '\f':
                counts.MaxLineLength = Math.Max(counts.MaxLineLength, _column);
                _column = 0;
                break;
            case '\t':
                _column += 8 - _column % 8;
                break;
            default:
                _column += width;
                break;
        }
    }

    // Whitespace separates words. Like GNU wc, non-breaking spaces don't.
    private static bool IsSpace(int c) =>
        c < 128 ? AsciiSpace[c] : c is not (0xA0 or 0x2007 or 0x202F) && c <= 0xFFFF && char.IsWhiteSpace((char)c);

    /// <summary>Display width: 0 for controls and combining marks, 2 for East Asian wide characters.</summary>
    private static int Width(int c)
    {
        if (c < 0x20 || c is >= 0x7F and < 0xA0) return 0;
        if (c < 0x300) return 1;
        var category = CharUnicodeInfo.GetUnicodeCategory(c);
        if (category is UnicodeCategory.NonSpacingMark or UnicodeCategory.EnclosingMark or UnicodeCategory.Format) return 0;
        bool wide = c is >= 0x1100 and <= 0x115F or >= 0x2E80 and <= 0xA4CF or >= 0xAC00 and <= 0xD7A3
            or >= 0xF900 and <= 0xFAFF or >= 0xFE30 and <= 0xFE4F or >= 0xFF00 and <= 0xFF60 or >= 0xFFE0 and <= 0xFFE6
            or >= 0x1F300 and <= 0x1F64F or >= 0x1F900 and <= 0x1F9FF or >= 0x20000 and <= 0x3FFFD;
        return wide ? 2 : 1;
    }
}
