using System.Buffers;
using System.Text;

namespace WinUtils.Common.IO;

/// <summary>
/// Reads lines from a byte stream. The encoding is detected from the first block: a UTF-8 or
/// UTF-16 byte-order mark wins; otherwise UTF-8 if the bytes are valid UTF-8, else Latin-1,
/// which maps every byte to one char and so round-trips arbitrary bytes unchanged.
/// </summary>
public sealed class LineReader
{
    private const int ChunkSize = 64 * 1024;
    private static readonly Encoding Utf8 = new UTF8Encoding(false);

    private readonly Stream _stream;
    private readonly char _terminator;
    private readonly bool _stripCarriageReturn;
    private readonly Decoder _decoder;
    private readonly byte[] _bytes = new byte[ChunkSize];
    private char[] _chars = new char[ChunkSize * 2];
    private int _pos, _end, _searchFrom;
    private bool _eof;

    /// <param name="terminator">Line terminator: '\n' normally, '\0' for NUL-separated records.</param>
    /// <param name="stripCarriageReturn">Treat CRLF as one terminator rather than leaving '\r' in the line.</param>
    public LineReader(Stream stream, char terminator = '\n', bool stripCarriageReturn = true)
    {
        _stream = stream;
        _terminator = terminator;
        _stripCarriageReturn = stripCarriageReturn && terminator == '\n';

        // One Read rather than filling the buffer, so piped input is processed as it arrives.
        int n = stream.Read(_bytes, 0, _bytes.Length);
        // A block ending mid-character can't be judged, so read on until that character is complete.
        while (n > 0 && n < _bytes.Length && EndsInPartialUtf8(_bytes.AsSpan(0, n)))
        {
            int more = stream.Read(_bytes, n, _bytes.Length - n);
            if (more == 0) break;
            n += more;
        }
        var head = _bytes.AsSpan(0, n);
        (Encoding, PreambleLength) = Detect(head, complete: n < _bytes.Length);
        HeadContainsNul = !IsUtf16 && head.Contains((byte)0);
        _decoder = Encoding.GetDecoder();
        Decode(PreambleLength, n - PreambleLength, flush: n == 0);
        _eof = n == 0;
    }

    public Encoding Encoding { get; }

    /// <summary>Length in bytes of the byte-order mark, if any.</summary>
    public int PreambleLength { get; }

    /// <summary>True for UTF-16 input, which callers usually re-encode as UTF-8 rather than pass through.</summary>
    public bool IsUtf16 => Encoding is UnicodeEncoding;

    /// <summary>True when the first block contains a NUL byte, the usual sign of a binary file.</summary>
    public bool HeadContainsNul { get; }

    /// <summary>Reads the next line. <paramref name="terminator"/> is "" for a final line with no terminator.</summary>
    public bool ReadLine(out string text, out string terminator)
    {
        while (true)
        {
            int from = Math.Max(_pos, _searchFrom);
            int idx = _chars.AsSpan(from, _end - from).IndexOf(_terminator);
            if (idx >= 0)
            {
                int lineEnd = from + idx;
                int textEnd = lineEnd;
                if (_stripCarriageReturn && textEnd > _pos && _chars[textEnd - 1] == '\r')
                {
                    textEnd--;
                    terminator = "\r\n";
                }
                else
                {
                    terminator = _terminator == '\n' ? "\n" : "\0";
                }
                text = new string(_chars, _pos, textEnd - _pos);
                _pos = lineEnd + 1;
                return true;
            }
            if (_eof)
            {
                terminator = "";
                if (_pos == _end)
                {
                    text = "";
                    return false;
                }
                text = new string(_chars, _pos, _end - _pos);
                _pos = _end;
                return true;
            }
            _searchFrom = _end;
            Fill();
        }
    }

    /// <param name="complete">False if <paramref name="head"/> may end partway through a character.</param>
    public static (Encoding Encoding, int PreambleLength) Detect(ReadOnlySpan<byte> head, bool complete = true)
    {
        if (head.StartsWith((ReadOnlySpan<byte>)[0xEF, 0xBB, 0xBF])) return (Utf8, 3);
        if (head.StartsWith((ReadOnlySpan<byte>)[0xFF, 0xFE])) return (Encoding.Unicode, 2);
        if (head.StartsWith((ReadOnlySpan<byte>)[0xFE, 0xFF])) return (Encoding.BigEndianUnicode, 2);
        return (IsUtf8(head, complete) ? Utf8 : Encoding.Latin1, 0);
    }

    /// <summary>Text decoded but not yet consumed. It always begins at the start of a line.</summary>
    public ReadOnlySpan<char> Buffered => _chars.AsSpan(_pos, _end - _pos);

    /// <summary>True once the whole input is in the buffer.</summary>
    public bool AtEnd => _eof;

    /// <summary>Appends the next block of input to <see cref="Buffered"/>.</summary>
    public void ReadMore()
    {
        if (_eof) return;
        _searchFrom = _end;
        Fill();
    }

    /// <summary>Marks <paramref name="count"/> chars of <see cref="Buffered"/> as consumed; must end at a line start.</summary>
    public void Consume(int count) => _pos += count;

    /// <summary>True if the data ends with the first bytes of a multi-byte UTF-8 sequence.</summary>
    private static bool EndsInPartialUtf8(ReadOnlySpan<byte> data)
    {
        for (int back = 1; back <= Math.Min(3, data.Length); back++)
        {
            byte b = data[^back];
            if (b < 0x80) return false;   // ASCII: nothing pending
            if (b < 0xC0) continue;        // continuation byte: keep looking for the lead byte
            int length = b >= 0xF0 ? 4 : b >= 0xE0 ? 3 : 2;
            return back < length;
        }
        return false;
    }

    private static bool IsUtf8(ReadOnlySpan<byte> data, bool complete)
    {
        if (System.Text.Unicode.Utf8.IsValid(data)) return true;
        if (complete) return false;
        // The block may end in the middle of a character; that alone doesn't make it invalid.
        while (!data.IsEmpty)
        {
            var status = Rune.DecodeFromUtf8(data, out _, out int consumed);
            if (status == OperationStatus.InvalidData) return false;
            if (status == OperationStatus.NeedMoreData) return true;
            data = data[consumed..];
        }
        return true;
    }

    private void Fill()
    {
        if (_pos > 0)
        {
            Array.Copy(_chars, _pos, _chars, 0, _end - _pos);
            _end -= _pos;
            _searchFrom -= _pos;
            _pos = 0;
        }
        int n = _stream.Read(_bytes, 0, _bytes.Length);
        Decode(0, n, flush: n == 0);
        if (n == 0) _eof = true;
    }

    private void Decode(int offset, int count, bool flush)
    {
        int needed = Encoding.GetMaxCharCount(count);
        if (_chars.Length - _end < needed)
            Array.Resize(ref _chars, Math.Max(_chars.Length * 2, _end + needed));
        _end += _decoder.GetChars(_bytes, offset, count, _chars, _end, flush);
    }
}
