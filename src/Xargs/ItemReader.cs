using System.Text;
using WinUtils.Common.IO;

namespace WinUtils.Xargs;

/// <summary>An input error, e.g. an unmatched quote. Exit status 1.</summary>
public sealed class XargsException(string message) : Exception(message);

/// <summary>An argument read from the input, and whether it ends a (logical) input line.</summary>
internal readonly record struct Item(string Text, bool EndsLine);

/// <summary>
/// Splits the input into arguments. By default items are separated by blanks and newlines, with
/// '...', "..." and backslash quoting; with -0 or -d, by a delimiter alone. With -I, each line
/// is one item. CRLF line endings count as plain newlines, so input piped from Windows programs
/// doesn't carry a stray '\r' into the last argument of each line.
/// </summary>
internal sealed class ItemReader(Stream input, char? delimiter, bool wholeLines, string? eof)
{
    public IEnumerable<Item> Read()
    {
        if (delimiter is char d)
        {
            var reader = new LineReader(input, d, stripCarriageReturn: d == '\n');
            while (reader.ReadLine(out string text, out _)) yield return new Item(text, true);
            yield break;
        }

        var lines = new LineReader(input);
        while (lines.ReadLine(out string line, out _))
        {
            if (wholeLines)
            {
                // -I: blanks don't separate items, but leading blanks are dropped and quotes still work.
                string trimmed = line.TrimStart(' ', '\t');
                if (trimmed.Length == 0) continue;
                string item = Unquote(trimmed, splitOnBlanks: false)[0];
                if (item == eof) yield break;
                yield return new Item(item, true);
                continue;
            }

            var items = Unquote(line, splitOnBlanks: true);
            // A trailing blank continues the logical line onto the next one (it matters for -L).
            bool endsLine = line.Length == 0 || line[^1] is not (' ' or '\t');
            for (int i = 0; i < items.Count; i++)
            {
                if (items[i] == eof) yield break;
                yield return new Item(items[i], endsLine && i == items.Count - 1);
            }
        }
    }

    private static List<string> Unquote(string line, bool splitOnBlanks)
    {
        var items = new List<string>();
        var current = new StringBuilder();
        bool inItem = false;
        for (int i = 0; i < line.Length; i++)
        {
            char c = line[i];
            if (splitOnBlanks && c is ' ' or '\t')
            {
                if (inItem) items.Add(current.ToString());
                current.Clear();
                inItem = false;
                continue;
            }
            inItem = true;
            if (c is '\'' or '"')
            {
                int close = line.IndexOf(c, i + 1);
                if (close < 0)
                {
                    throw new XargsException(
                        $"unmatched {(c == '"' ? "double" : "single")} quote; by default quotes are special to xargs unless you use the -0 option");
                }
                current.Append(line, i + 1, close - i - 1);
                i = close;
            }
            else if (c == '\\' && i + 1 < line.Length && line[i + 1] is ' ' or '\t' or '\'' or '"')
            {
                // Unlike GNU, a backslash escapes only a blank or a quote; anywhere else it's
                // literal, so Windows paths (C:\dir\file, \\server\share) come through intact.
                current.Append(line[++i]);
            }
            else
            {
                current.Append(c);
            }
        }
        if (inItem || !splitOnBlanks) items.Add(current.ToString());
        return items;
    }
}
