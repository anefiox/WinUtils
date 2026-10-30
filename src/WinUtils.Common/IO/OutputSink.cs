using System.Text;
using WinUtils.Common.Interop;

namespace WinUtils.Common.IO;

/// <summary>Thrown when the reader of our output has gone away (e.g. <c>grep ... | head</c>).</summary>
public sealed class OutputClosedException(Exception inner) : Exception("Output closed", inner);

/// <summary>
/// Buffered standard output/error. A console gets UTF-16 text through WriteConsoleW, so any
/// character displays regardless of the console code page. Redirected output is bytes: UTF-8 for
/// our own text, and <see cref="ContentEncoding"/> for text echoed from input files so their
/// bytes pass through unchanged.
/// </summary>
public abstract class OutputSink : IDisposable
{
    public static readonly Encoding Utf8 = new UTF8Encoding(false);

    /// <summary>Encoding used by <see cref="WriteContent"/> when writing bytes.</summary>
    public Encoding ContentEncoding { get; set; } = Utf8;

    /// <summary>True when writing to an interactive console window.</summary>
    public abstract bool IsConsole { get; }

    public abstract void Write(ReadOnlySpan<char> text);

    public void Write(char c) => Write(new ReadOnlySpan<char>(in c));

    /// <summary>Writes text that came from an input file.</summary>
    public abstract void WriteContent(ReadOnlySpan<char> text);

    public abstract void Flush();

    /// <summary>Turns on ANSI escape-sequence processing; returns false if colors can't be shown.</summary>
    public virtual bool TryEnableVirtualTerminal() => false;

    public void Dispose()
    {
        try { Flush(); }
        catch (OutputClosedException) { }
        GC.SuppressFinalize(this);
    }

    public static OutputSink ForStream(Stream stream) => new StreamSink(stream);

    public static OutputSink OpenStdout() => Open(Kernel32.STD_OUTPUT_HANDLE, Console.OpenStandardOutput);

    public static OutputSink OpenStderr() => Open(Kernel32.STD_ERROR_HANDLE, Console.OpenStandardError);

    private static OutputSink Open(int stdHandle, Func<Stream> openStream)
    {
        if (OperatingSystem.IsWindows())
        {
            nint handle = Kernel32.GetStdHandle(stdHandle);
            if (Kernel32.GetConsoleMode(handle, out _)) return new ConsoleSink(handle);
        }
        return new StreamSink(openStream());
    }

    private sealed class StreamSink(Stream stream) : OutputSink
    {
        private readonly byte[] _buffer = new byte[64 * 1024];
        private int _length;
        private bool _closed;

        public override bool IsConsole => false;

        public override void Write(ReadOnlySpan<char> text) => Encode(text, Utf8);

        public override void WriteContent(ReadOnlySpan<char> text) => Encode(text, ContentEncoding);

        private void Encode(ReadOnlySpan<char> text, Encoding encoding)
        {
            int max = encoding.GetMaxByteCount(text.Length);
            if (max > _buffer.Length - _length)
            {
                Flush();
                if (max > _buffer.Length)
                {
                    WriteBytes(encoding.GetBytes(text.ToArray()));
                    return;
                }
            }
            _length += encoding.GetBytes(text, _buffer.AsSpan(_length));
        }

        public override void Flush()
        {
            if (_length == 0) return;
            WriteBytes(_buffer.AsSpan(0, _length));
            _length = 0;
            if (!_closed) Guard(stream.Flush);
        }

        private void WriteBytes(ReadOnlySpan<byte> bytes)
        {
            if (_closed) return;
            try
            {
                stream.Write(bytes);
            }
            catch (IOException e)
            {
                _closed = true;
                throw new OutputClosedException(e);
            }
        }

        private void Guard(Action action)
        {
            try
            {
                action();
            }
            catch (IOException e)
            {
                _closed = true;
                throw new OutputClosedException(e);
            }
        }
    }

    private sealed class ConsoleSink(nint handle) : OutputSink
    {
        private char[] _buffer = new char[16 * 1024];
        private int _length;

        public override bool IsConsole => true;

        public override void Write(ReadOnlySpan<char> text)
        {
            if (text.Length > _buffer.Length - _length)
            {
                Flush();
                if (text.Length > _buffer.Length) Array.Resize(ref _buffer, text.Length);
            }
            text.CopyTo(_buffer.AsSpan(_length));
            _length += text.Length;
        }

        public override void WriteContent(ReadOnlySpan<char> text) => Write(text);

        public override unsafe void Flush()
        {
            if (_length == 0 || !OperatingSystem.IsWindows()) return;
            fixed (char* p = _buffer)
            {
                int offset = 0;
                while (offset < _length)
                {
                    // Large single writes can fail on older consoles, so write in chunks.
                    int chunk = Math.Min(_length - offset, 8192);
                    if (!Kernel32.WriteConsoleW(handle, p + offset, chunk, out int written, 0) || written == 0)
                    {
                        _length = 0;
                        throw new OutputClosedException(new IOException("WriteConsoleW failed"));
                    }
                    offset += written;
                }
            }
            _length = 0;
        }

        public override bool TryEnableVirtualTerminal()
        {
            if (!OperatingSystem.IsWindows() || !Kernel32.GetConsoleMode(handle, out uint mode)) return false;
            return (mode & Kernel32.ENABLE_VIRTUAL_TERMINAL_PROCESSING) != 0
                || Kernel32.SetConsoleMode(handle, mode | Kernel32.ENABLE_VIRTUAL_TERMINAL_PROCESSING);
        }
    }
}
