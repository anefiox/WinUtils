using WinUtils.Common.CommandLine;
using WinUtils.Common.IO;

namespace WinUtils.Tr;

public static class TrCommand
{
    private const string TryHelp = "Try 'tr --help' for more information.\n";
    private const int Bmp = 0x10000;

    private static readonly OptionParser Parser = new(
    [
        new("c", 'c', "complement"),
        new("c", 'C'),
        new("d", 'd', "delete"),
        new("s", 's', "squeeze-repeats"),
        new("t", 't', "truncate-set1"),
        new("help", null, "help"),
        new("version", null, "version"),
    ]);

    /// <summary>Runs tr and returns its exit status: 0, or 1 on error.</summary>
    public static int Run(IReadOnlyList<string> args, Stream stdin, OutputSink stdout, OutputSink stderr)
    {
        void Error(string message)
        {
            stdout.Flush();
            stderr.Write($"tr: {message}\n");
            stderr.Flush();
        }

        bool complement = false, delete = false, squeeze = false, truncate = false;
        char[]? map = null;
        bool[]? deleteSet = null, squeezeSet = null;
        try
        {
            var parsed = Parser.Parse(args);
            foreach (var (id, _, _) in parsed.Options)
            {
                switch (id)
                {
                    case "c": complement = true; break;
                    case "d": delete = true; break;
                    case "s": squeeze = true; break;
                    case "t": truncate = true; break;
                    case "help":
                        stdout.Write(HelpText.Text);
                        return 0;
                    case "version":
                        stdout.Write(HelpText.Version);
                        return 0;
                }
            }

            var sets = parsed.Operands;
            bool translate = !delete && sets.Count == 2;
            int needed = delete && squeeze ? 2 : delete ? 1 : squeeze ? 1 : 2;
            if (sets.Count == 0) throw new OptionException("missing operand");
            if (sets.Count < needed)
            {
                throw new OptionException($"missing operand after '{sets[^1]}'\n" + (delete
                    ? "Two strings must be given when both deleting and squeezing repeats."
                    : "Two strings must be given when translating."));
            }
            int allowed = delete && !squeeze ? 1 : 2;
            if (sets.Count > allowed)
            {
                throw new OptionException($"extra operand '{sets[allowed]}'" + (delete && !squeeze
                    ? "\nOnly one string may be given when deleting without squeezing repeats."
                    : ""));
            }

            var set1 = CharSet.Parse(sets[0], Error);
            var set2 = sets.Count > 1 ? CharSet.Parse(sets[1], Error) : null;
            // [c*n] is allowed in SET1; only the open-ended [c*] fill isn't.
            if (set1.Any(item => item is RepeatItem { Count: 0 })) throw new TrException("the [c*] repeat construct may not appear in string1");

            bool[] members1 = Membership(set1, complement);
            if (delete) deleteSet = members1;
            if (translate) map = BuildMap(set1, set2!, complement, truncate);
            if (squeeze) squeezeSet = set2 is not null ? Membership(set2, false) : members1;
        }
        catch (OptionException e)
        {
            Error(e.Message);
            stderr.Write(TryHelp);
            return 1;
        }
        catch (TrException e)
        {
            Error(e.Message);
            return 1;
        }

        // CRs are ordinary characters here, so tr -d '\r' turns CRLF into LF.
        var reader = new LineReader(stdin, '\n', stripCarriageReturn: false);
        stdout.ContentEncoding = reader.IsUtf16 ? OutputSink.Utf8 : reader.Encoding;
        var buffer = new char[4096];
        int last = -1; // last character written, for squeezing
        while (reader.ReadLine(out string line, out string lineEnd))
        {
            int needed = line.Length + lineEnd.Length;
            if (buffer.Length < needed) buffer = new char[needed * 2];
            int n = 0;
            foreach (string part in (ReadOnlySpan<string>)[line, lineEnd])
            {
                foreach (char ch in part)
                {
                    char c = ch;
                    if (deleteSet is not null && deleteSet[c]) continue;
                    if (map is not null) c = map[c];
                    if (squeezeSet is not null && squeezeSet[c] && c == last) continue;
                    buffer[n++] = c;
                    last = c;
                }
            }
            stdout.WriteContent(buffer.AsSpan(0, n));
            if (stdout.IsConsole) stdout.Flush();
        }
        stdout.Flush();
        return 0;
    }

    /// <summary>A lookup table of which characters belong to the set (or to its complement).</summary>
    private static bool[] Membership(List<SetItem> items, bool complement)
    {
        var inSet = new bool[Bmp];
        foreach (var item in items)
        {
            switch (item)
            {
                case CharItem { C: var c }: inSet[c] = true; break;
                case RangeItem { From: var from, To: var to }:
                    for (int c = from; c <= to; c++) inSet[c] = true;
                    break;
                case ClassItem { Name: var name }:
                    foreach (char c in CharSet.ClassMembers(name)) inSet[c] = true;
                    break;
                case RepeatItem { C: var c }: inSet[c] = true; break;
            }
        }
        if (complement)
            for (int c = 0; c < Bmp; c++) inSet[c] = !inSet[c];
        return inSet;
    }

    /// <summary>
    /// Pairs SET1 with SET2 position by position. SET2 is padded with its last character to
    /// SET1's length (or SET1 is cut to SET2's with -t). [:lower:] opposite [:upper:] (or the
    /// reverse) maps by Unicode case conversion, since the classes differ in size.
    /// </summary>
    private static char[] BuildMap(List<SetItem> set1, List<SetItem> set2, bool complement, bool truncate)
    {
        var map = new char[Bmp];
        for (int c = 0; c < Bmp; c++) map[c] = (char)c;

        foreach (var (item, i) in set2.Select((item, i) => (item, i)))
        {
            if (item is not ClassItem { Name: var name }) continue;
            if (name is not ("upper" or "lower"))
                throw new TrException("when translating, the only character classes that may appear in\nstring2 are 'upper' and 'lower'");
            if (complement || i >= set1.Count || set1[i] is not ClassItem { Name: "upper" or "lower" })
                throw new TrException("misaligned [:upper:] and/or [:lower:] construct");
        }

        // Expand both sets into entries; a case-class pair stays a single marker entry.
        var from = new List<(char C, string? Case)>();
        if (complement)
        {
            var inSet1 = Membership(set1, complement: false);
            for (int c = 0; c < Bmp; c++)
                if (!inSet1[c] && c is < 0xD800 or > 0xDFFF) from.Add(((char)c, null));
        }
        else
        {
            for (int i = 0; i < set1.Count; i++)
            {
                bool paired = i < set2.Count && set2[i] is ClassItem && set1[i] is ClassItem { Name: "upper" or "lower" };
                Expand(set1[i], from, paired);
            }
        }
        var to = new List<(char C, string? Case)>();
        int fillAt = -1;
        for (int i = 0; i < set2.Count; i++)
        {
            if (set2[i] is RepeatItem { Count: 0 })
            {
                if (fillAt >= 0) throw new TrException("only one [c*] repeat construct may appear in string2");
                fillAt = to.Count;
                continue;
            }
            bool paired = set2[i] is ClassItem;
            Expand(set2[i], to, paired);
        }
        if (fillAt >= 0 && set2.First(x => x is RepeatItem { Count: 0 }) is RepeatItem fill)
        {
            int count = Math.Max(0, from.Count - to.Count);
            to.InsertRange(fillAt, Enumerable.Repeat((fill.C, (string?)null), count));
        }

        if (to.Count == 0)
        {
            if (!truncate && from.Count > 0) throw new TrException("when not truncating set1, string2 must be non-empty");
            return map;
        }
        int length = truncate ? Math.Min(from.Count, to.Count) : from.Count;
        for (int i = 0; i < length; i++)
        {
            var source = from[i];
            var target = i < to.Count ? to[i] : to[^1];
            if (source.Case is not null && target.Case is not null)
            {
                bool toUpper = target.Case == "upper";
                foreach (char c in CharSet.ClassMembers(source.Case))
                    map[c] = toUpper ? char.ToUpperInvariant(c) : char.ToLowerInvariant(c);
                continue;
            }
            if (source.Case is not null) throw new TrException("misaligned [:upper:] and/or [:lower:] construct");
            map[source.C] = target.C;
        }
        return map;
    }

    private static void Expand(SetItem item, List<(char, string?)> into, bool caseMarker)
    {
        switch (item)
        {
            case CharItem { C: var c }:
                into.Add((c, null));
                break;
            case RangeItem { From: var from, To: var to }:
                for (int c = from; c <= to; c++) into.Add(((char)c, null));
                break;
            case ClassItem { Name: var name } when caseMarker:
                into.Add(('\0', name));
                break;
            case ClassItem { Name: var name }:
                foreach (char c in CharSet.ClassMembers(name)) into.Add((c, null));
                break;
            case RepeatItem { C: var c, Count: var n }:
                for (int i = 0; i < n; i++) into.Add((c, null));
                break;
        }
    }
}
