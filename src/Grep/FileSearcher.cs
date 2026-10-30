using System.Text;
using System.Text.RegularExpressions;
using WinUtils.Common.IO;

namespace WinUtils.Grep;

/// <summary>Searches one input and prints its selected lines with any requested context.</summary>
internal sealed class FileSearcher
{
    private readonly GrepOptions _o;
    private readonly Matcher _matcher;
    private readonly LinePrinter _printer;
    private readonly string? _name;        // printed before each line, or null without -H
    private readonly string _displayName;  // used in messages
    private readonly bool _printLines;
    private readonly bool _context;
    private readonly bool _lineBuffered;
    private readonly bool _checkNul;
    private readonly Queue<(long Number, long Offset, string Text, string Terminator)> _before = new();
    private Encoding _encoding = OutputSink.Utf8;
    private long _lastPrinted;             // line number of the last line printed, 0 if none
    private string _fallbackTerminator = "\n";

    private long _selected, _lineNumber, _offset;
    private int _afterLeft;
    private bool _maxReached, _binary;

    public FileSearcher(GrepOptions options, Matcher matcher, LinePrinter printer, string displayName, bool showName)
    {
        _o = options;
        _matcher = matcher;
        _printer = printer;
        _displayName = displayName;
        _name = showName ? displayName : null;
        _printLines = !options.Quiet && options.List == ListFiles.None && !options.Count;
        _context = (options.Before > 0 || options.After > 0) && !options.OnlyMatching;
        _lineBuffered = options.LineBuffered || printer.Output.IsConsole;
        _checkNul = !options.NullData && options.Binary != BinaryFiles.Text;
        if (options.NullData) _fallbackTerminator = "\0";
    }

    /// <summary>Searches <paramref name="stream"/> and returns the number of selected lines.</summary>
    public long Search(Stream stream)
    {
        var reader = new LineReader(stream, _o.NullData ? '\0' : '\n', !_o.KeepCarriageReturn);
        _encoding = reader.Encoding;
        // UTF-16 input is re-encoded as UTF-8; anything else passes through byte for byte.
        _printer.Output.ContentEncoding = reader.IsUtf16 ? OutputSink.Utf8 : reader.Encoding;
        _offset = reader.PreambleLength;

        _binary = _checkNul && reader.HeadContainsNul;
        if (_binary && _o.Binary == BinaryFiles.WithoutMatch) return 0;

        // Most searches select few lines, so jump between candidates with one regex call over the
        // whole buffer. Non-matching lines (-v) and context lines need every line visited.
        if (_matcher.BufferRegex is { } regex && !_o.InvertMatch && !_context)
            ScanBuffer(reader, regex);
        else
            while (reader.ReadLine(out string text, out string terminator) && ProcessLine(text, terminator)) { }
        return _selected;
    }

    private void ScanBuffer(LineReader reader, Regex regex)
    {
        while (true)
        {
            var buffered = reader.Buffered;
            // Scan only complete lines; the partial last line waits for more input.
            int complete = reader.AtEnd ? buffered.Length : buffered.LastIndexOf('\n') + 1;
            if (complete == 0)
            {
                if (reader.AtEnd) return;
                reader.ReadMore();
                continue;
            }
            if (!ScanLines(buffered[..complete], regex)) return;
            reader.Consume(complete);
        }
    }

    /// <summary>Processes the lines in <paramref name="lines"/>, which starts at a line start. Returns false to stop.</summary>
    private bool ScanLines(ReadOnlySpan<char> lines, Regex regex)
    {
        int pos = 0;
        while (pos < lines.Length)
        {
            var rest = lines[pos..];
            int candidate = -1;
            foreach (var m in regex.EnumerateMatches(rest))
            {
                candidate = m.Index;
                break;
            }
            // An empty match after the final newline isn't in any line.
            if (candidate < 0 || (candidate == rest.Length && rest[^1] == '\n')) return Skip(rest);

            int lineStart = rest[..candidate].LastIndexOf('\n') + 1;
            if (!Skip(rest[..lineStart])) return false;

            var line = rest[lineStart..];
            int newline = line.IndexOf('\n');
            var text = newline < 0 ? line : line[..newline];
            string terminator = newline < 0 ? "" : "\n";
            if (newline >= 0 && !_o.KeepCarriageReturn && text.EndsWith('\r'))
            {
                text = text[..^1];
                terminator = "\r\n";
            }
            // The buffer regex only finds candidates (a match may span lines), so confirm it.
            if (!ProcessLine(new string(text), terminator)) return false;
            pos += lineStart + (newline < 0 ? line.Length : newline + 1);
        }
        return true;
    }

    /// <summary>Accounts for lines with no match without looking at them one by one.</summary>
    private bool Skip(ReadOnlySpan<char> lines)
    {
        _lineNumber += lines.Count('\n');
        if (_o.ByteOffset) _offset += _encoding.GetByteCount(lines);
        if (_checkNul && !_binary && lines.Contains('\0'))
        {
            _binary = true;
            if (_o.Binary == BinaryFiles.WithoutMatch) return false;
        }
        return true;
    }

    /// <summary>Handles one line. Returns false when the rest of the input should be skipped.</summary>
    private bool ProcessLine(string text, string terminator)
    {
        _lineNumber++;
        long lineOffset = _offset;
        if (_o.ByteOffset) _offset += _encoding.GetByteCount(text) + _encoding.GetByteCount(terminator);
        if (terminator.Length > 0) _fallbackTerminator = terminator;

        if (_checkNul && !_binary && text.Contains('\0'))
        {
            _binary = true;
            if (_o.Binary == BinaryFiles.WithoutMatch) return false;
        }

        if (_maxReached)
        {
            // After -m's last line, only the trailing context remains to be printed.
            if (_afterLeft-- == 0 || _binary) return false;
            PrintLine(_lineNumber, lineOffset, text, terminator, selected: false);
            return true;
        }

        if (_matcher.IsMatch(text) != _o.InvertMatch)
        {
            _selected++;
            if (_o.Quiet || _o.List != ListFiles.None) return false;
            bool atMax = _o.MaxCount > 0 && _selected >= _o.MaxCount;
            if (!_printLines) return !atMax;
            if (_binary)
            {
                _printer.Output.Write($"grep: {_displayName}: binary file matches\n");
                return false;
            }
            while (_before.TryDequeue(out var b)) PrintLine(b.Number, b.Offset, b.Text, b.Terminator, selected: false);
            if (_o.OnlyMatching) PrintMatches(_lineNumber, lineOffset, text, terminator);
            else PrintLine(_lineNumber, lineOffset, text, terminator, selected: true);
            _afterLeft = _o.After;
            if (atMax)
            {
                _maxReached = true;
                if (_afterLeft == 0) return false;
            }
        }
        else if (_printLines && _context && !_binary)
        {
            if (_afterLeft > 0)
            {
                _afterLeft--;
                PrintLine(_lineNumber, lineOffset, text, terminator, selected: false);
            }
            else if (_o.Before > 0)
            {
                _before.Enqueue((_lineNumber, lineOffset, text, terminator));
                if (_before.Count > _o.Before) _before.Dequeue();
            }
        }

        // Like stdio, line-buffer a console so piped-in input shows results as they arrive.
        if (_lineBuffered) _printer.Output.Flush();
        return true;
    }

    private void PrintLine(long number, long offset, string text, string terminator, bool selected)
    {
        if (_context && ((_lastPrinted == 0 && _printer.AnyLinePrinted) || (_lastPrinted > 0 && number > _lastPrinted + 1)))
            _printer.GroupSeparator();
        _printer.Prefix(_name, number, offset, selected ? ':' : '-');
        _printer.Line(text, _matcher, highlight: selected && !_o.InvertMatch);
        _printer.Output.Write(terminator.Length > 0 ? terminator : _fallbackTerminator);
        _printer.AnyLinePrinted = true;
        _lastPrinted = number;
    }

    /// <summary>-o: prints each non-empty match on its own line. Inverted matches have nothing to show.</summary>
    private void PrintMatches(long number, long offset, string text, string terminator)
    {
        if (_o.InvertMatch) return;
        string newline = terminator.Length > 0 ? terminator : _fallbackTerminator;
        int pos = 0;
        while (pos <= text.Length && _matcher.TryMatch(text, pos, out int index, out int length))
        {
            if (length == 0)
            {
                pos = index + 1;
                continue;
            }
            long matchOffset = _o.ByteOffset ? offset + _encoding.GetByteCount(text.AsSpan(0, index)) : offset;
            _printer.Prefix(_name, number, matchOffset, ':');
            _printer.Match(text.AsSpan(index, length));
            _printer.Output.Write(newline);
            pos = index + length;
        }
        _printer.AnyLinePrinted = true;
        _lastPrinted = number;
    }
}
