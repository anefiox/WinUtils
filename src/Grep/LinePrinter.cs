using System.Globalization;
using WinUtils.Common.IO;

namespace WinUtils.Grep;

/// <summary>Formats grep's output: prefixes, separators, colors, and highlighted matches.</summary>
internal sealed class LinePrinter(OutputSink output, GrepOptions options, ColorScheme? colors)
{
    /// <summary>Set once any line is printed; a later context group then needs a separator first.</summary>
    public bool AnyLinePrinted { get; set; }

    public OutputSink Output => output;

    public void GroupSeparator()
    {
        if (options.GroupSeparator is null) return;
        Colored(colors?.Separator, options.GroupSeparator);
        output.Write('\n');
    }

    /// <summary>Writes "name:12:345:" (whichever parts are enabled), using '-' after context lines.</summary>
    public void Prefix(string? name, long lineNumber, long byteOffset, char separator)
    {
        if (name is not null)
        {
            Colored(colors?.FileName, name);
            if (options.NullAfterName) output.Write('\0');
            else Colored(colors?.Separator, [separator]);
        }
        if (options.LineNumber)
        {
            Colored(colors?.LineNumber, lineNumber.ToString(CultureInfo.InvariantCulture));
            Colored(colors?.Separator, [separator]);
        }
        if (options.ByteOffset)
        {
            Colored(colors?.ByteOffset, byteOffset.ToString(CultureInfo.InvariantCulture));
            Colored(colors?.Separator, [separator]);
        }
        if (options.InitialTab && (name is not null || options.LineNumber || options.ByteOffset))
            output.Write('\t');
    }

    /// <summary>Writes a file name on its own, as for -l, -L and -c.</summary>
    public void FileName(string name) => Colored(colors?.FileName, name);

    public void Match(ReadOnlySpan<char> text)
    {
        if (colors is null)
        {
            output.WriteContent(text);
            return;
        }
        output.Write(ColorScheme.Start(colors.Match));
        output.WriteContent(text);
        output.Write(ColorScheme.End);
    }

    /// <summary>Writes a line, highlighting every match when colors are on.</summary>
    public void Line(string text, Matcher matcher, bool highlight)
    {
        if (colors is null || !highlight)
        {
            output.WriteContent(text);
            return;
        }
        int written = 0, pos = 0;
        while (pos <= text.Length && matcher.TryMatch(text, pos, out int index, out int length))
        {
            if (length == 0)
            {
                pos = index + 1;
                continue;
            }
            output.WriteContent(text.AsSpan(written, index - written));
            Match(text.AsSpan(index, length));
            written = pos = index + length;
        }
        output.WriteContent(text.AsSpan(written));
    }

    private void Colored(string? sgr, ReadOnlySpan<char> text)
    {
        if (string.IsNullOrEmpty(sgr))
        {
            output.Write(text);
            return;
        }
        output.Write(ColorScheme.Start(sgr));
        output.Write(text);
        output.Write(ColorScheme.End);
    }
}
