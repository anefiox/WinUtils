using System.Text;
using WinUtils.Common.IO;

namespace WinUtils.Sed;

/// <summary>
/// An output stream (stdout, an in-place temp file, or a w file). Like GNU sed, a final line
/// that had no newline in the input gets none in the output; if more output follows, the
/// newline is supplied first.
/// </summary>
internal sealed class SedWriter(OutputSink sink)
{
    private bool _missingNewline;

    public OutputSink Sink => sink;

    public void Line(string text, string terminator, string newline)
    {
        if (_missingNewline)
        {
            sink.WriteContent(newline);
            _missingNewline = false;
        }
        sink.WriteContent(text);
        if (terminator.Length == 0) _missingNewline = true;
        else sink.WriteContent(terminator);
    }
}

/// <summary>The file behind an R command, read one line per use.</summary>
internal sealed class LineSource(string path)
{
    private LineReader? _reader;
    private bool _done;

    public string? Next()
    {
        if (_done) return null;
        try
        {
            _reader ??= new LineReader(File.OpenRead(path));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            _done = true; // like GNU sed, an unreadable R file is silently ignored
            return null;
        }
        if (_reader.ReadLine(out string text, out _)) return text;
        _done = true;
        return null;
    }
}

/// <summary>
/// The input lines of one run: all files as one stream, or a single file with -s and -i.
/// It reads one line ahead so that '$' (last line) can be answered.
/// </summary>
internal sealed class Input : IDisposable
{
    private readonly Queue<string> _files;
    private readonly Stream _stdin;
    private readonly char _terminator;
    private readonly bool _stripCarriageReturn;
    private readonly Action<string, Exception> _onError;
    private Stream? _stream;
    private LineReader? _reader;
    private string _readerName = "-";
    private (string Text, string Terminator, string File, LineReader Reader)? _next;

    public Input(IEnumerable<string> files, Stream stdin, char terminator, bool stripCarriageReturn, Action<string, Exception> onError)
    {
        _files = new Queue<string>(files);
        _stdin = stdin;
        _terminator = terminator;
        _stripCarriageReturn = stripCarriageReturn;
        _onError = onError;
        ReadAhead();
    }

    public long LineNumber { get; private set; }

    /// <summary>The current line's file name, "-" for standard input.</summary>
    public string FileName { get; private set; } = "-";

    /// <summary>The current line's encoding.</summary>
    public Encoding Encoding { get; private set; } = OutputSink.Utf8;

    public bool IsUtf16 { get; private set; }

    /// <summary>The first file's byte-order mark, to write back after an in-place edit.</summary>
    public byte[] Preamble { get; private set; } = [];

    /// <summary>True when the current line is the last one.</summary>
    public bool IsLast => _next is null;

    public bool Next(out string text, out string terminator)
    {
        if (_next is not { } next)
        {
            text = terminator = "";
            return false;
        }
        (text, terminator) = (next.Text, next.Terminator);
        FileName = next.File;
        Encoding = next.Reader.Encoding;
        IsUtf16 = next.Reader.IsUtf16;
        LineNumber++;
        ReadAhead();
        return true;
    }

    private void ReadAhead()
    {
        _next = null;
        while (true)
        {
            if (_reader is null && !OpenNext()) return;
            if (_reader!.ReadLine(out string text, out string terminator))
            {
                _next = (text, terminator, _readerName, _reader);
                return;
            }
            CloseCurrent();
        }
    }

    private bool OpenNext()
    {
        while (_files.TryDequeue(out string? name))
        {
            try
            {
                if (name == "-")
                {
                    _stream = null;
                    _reader = new LineReader(_stdin, _terminator, _stripCarriageReturn);
                }
                else
                {
                    if (Directory.Exists(name)) throw new IOException("read error: Is a directory");
                    _stream = new FileStream(name, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                    _reader = new LineReader(_stream, _terminator, _stripCarriageReturn);
                }
                _readerName = name;
                if (LineNumber == 0 && Preamble.Length == 0 && _reader.PreambleLength > 0)
                    Preamble = _reader.PreambleLength == 3 ? [0xEF, 0xBB, 0xBF] : _reader.Encoding.GetPreamble();
                if (LineNumber == 0) (Encoding, IsUtf16) = (_reader.Encoding, _reader.IsUtf16);
                return true;
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                _stream?.Dispose();
                _stream = null;
                _onError(name, e);
            }
        }
        return false;
    }

    private void CloseCurrent()
    {
        _stream?.Dispose();
        _stream = null;
        _reader = null;
    }

    public void Dispose() => CloseCurrent();
}
