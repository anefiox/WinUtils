using System.Buffers;
using System.Text;

namespace WinUtils.Common.IO;

/// <summary>
/// Copies raw input bytes to output. Bytes pass through unchanged, except that UTF-16 input,
/// and anything going to a console, is decoded so it displays as text.
/// </summary>
public sealed class ByteTextWriter(OutputSink sink)
{
    private Decoder? _decoder;

    /// <summary>True when bytes are decoded, so a byte-order mark must not be copied.</summary>
    public bool Decoding => _decoder is not null;

    /// <summary>Call before each input with the encoding detected for it.</summary>
    public void Start(ByteLines lines)
    {
        _decoder = lines.IsUtf16 || sink.IsConsole ? lines.Encoding.GetDecoder() : null;
    }

    public void Write(ReadOnlySpan<byte> bytes)
    {
        if (_decoder is null)
        {
            sink.WriteBytes(bytes);
            return;
        }
        char[] chars = ArrayPool<char>.Shared.Rent(bytes.Length + 2);
        int n = _decoder.GetChars(bytes, chars, flush: false);
        sink.Write(chars.AsSpan(0, n));
        ArrayPool<char>.Shared.Return(chars);
    }

    /// <summary>Copies stream bytes from <paramref name="from"/> to <paramref name="to"/> (exclusive).</summary>
    public void CopyRange(Stream stream, long from, long to)
    {
        var buffer = ArrayPool<byte>.Shared.Rent(64 * 1024);
        stream.Position = from;
        while (from < to)
        {
            int n = stream.Read(buffer, 0, (int)Math.Min(buffer.Length, to - from));
            if (n == 0) break;
            Write(buffer.AsSpan(0, n));
            from += n;
        }
        ArrayPool<byte>.Shared.Return(buffer);
    }
}
