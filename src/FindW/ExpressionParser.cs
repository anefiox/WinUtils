using System.Globalization;
using System.Text.RegularExpressions;
using WinUtils.Common.Text;

namespace WinUtils.FindW;

public sealed class FindException(string message) : Exception(message);

/// <summary>
/// Parses a find expression. Precedence from loosest to tightest: "," then -o then -a (also
/// implied between adjacent primaries) then ! then parentheses.
/// </summary>
internal sealed class ExpressionParser(FindCommand find, IReadOnlyList<string> tokens)
{
    private int _i;

    /// <summary>True if the expression has an action; otherwise -print is implied.</summary>
    public bool HasAction { get; private set; }

    public Expr? Parse()
    {
        if (tokens.Count == 0) return null;
        var expr = ParseComma();
        if (_i < tokens.Count)
        {
            throw tokens[_i] == ")"
                ? new FindException("invalid expression; you have too many ')'")
                : new FindException($"unexpected '{tokens[_i]}'");
        }
        return expr;
    }

    private bool Peek(params string[] options) => _i < tokens.Count && options.Contains(tokens[_i]);

    private Expr ParseComma()
    {
        var left = ParseOr();
        while (Peek(","))
        {
            _i++;
            left = new CommaExpr(left, ParseOr());
        }
        return left;
    }

    private Expr ParseOr()
    {
        var left = ParseAnd();
        while (Peek("-o", "-or"))
        {
            _i++;
            left = new OrExpr(left, ParseAnd());
        }
        return left;
    }

    private Expr ParseAnd()
    {
        var left = ParseNot();
        while (_i < tokens.Count && !Peek("-o", "-or", ",", ")"))
        {
            if (Peek("-a", "-and")) _i++;
            left = new AndExpr(left, ParseNot());
        }
        return left;
    }

    private Expr ParseNot()
    {
        if (!Peek("!", "-not")) return ParsePrimary();
        _i++;
        return new NotExpr(ParseNot());
    }

    private string Argument(string option)
    {
        if (_i >= tokens.Count) throw new FindException($"missing argument to '{option}'");
        return tokens[_i++];
    }

    private Expr ParsePrimary()
    {
        if (_i >= tokens.Count) throw new FindException($"expected an expression after '{tokens[_i - 1]}'");
        string t = tokens[_i++];
        switch (t)
        {
            case "(":
                var inner = ParseComma();
                if (!Peek(")"))
                    throw new FindException("invalid expression; I was expecting to find a ')' somewhere but did not see one.");
                _i++;
                return inner;

            // Tests on names. Windows file names are case-insensitive, so -name is too.
            case "-name" or "-iname":
            {
                var glob = new Glob(Argument(t));
                return new Primary(e => glob.IsMatch(e.Name));
            }
            case "-path" or "-ipath" or "-wholename" or "-iwholename":
            {
                var glob = new Glob(ForwardSlashes(Argument(t)));
                return new Primary(e => glob.IsMatch(ForwardSlashes(e.Path)));
            }
            case "-regex" or "-iregex":
            {
                var regex = BuildRegex(Argument(t), ignoreCase: t == "-iregex");
                return new Primary(e => regex.IsMatch(e.Path));
            }
            case "-regextype":
                find.RegexType = Argument(t) switch
                {
                    "posix-extended" or "egrep" or "posix-egrep" or "findutils-default" => RegexType.Extended,
                    "posix-basic" or "grep" or "posix-minimal-basic" => RegexType.Basic,
                    "dotnet" or "pcre" => RegexType.DotNet,
                    var other => throw new FindException($"Unknown regular expression type '{other}'; valid types are 'posix-extended', 'posix-basic', 'dotnet'."),
                };
                return Primary.True;

            // Tests on type, size and times.
            case "-type":
            {
                string types = Argument(t);
                var set = new HashSet<char>();
                foreach (string part in types.Split(','))
                {
                    if (part.Length != 1 || !"fdlbcps".Contains(part[0]))
                        throw new FindException($"Unknown argument to -type: {part}");
                    set.Add(part[0]);
                }
                return new Primary(e => set.Contains(e.Type));
            }
            case "-size":
                return ParseSize(Argument(t));
            case "-empty":
                return new Primary(IsEmpty);
            case "-hidden":
                return new Primary(e => e.Info.Attributes.HasFlag(FileAttributes.Hidden));
            case "-mtime" or "-atime" or "-ctime":
            {
                var (sign, n) = ParseNumber(Argument(t), t);
                return new Primary(e => Compare(Age(e, t[1]).Days, sign, n));
            }
            case "-mmin" or "-amin" or "-cmin":
            {
                var (sign, n) = ParseNumber(Argument(t), t);
                return new Primary(e => Compare((long)Math.Floor(Age(e, t[1]).TotalMinutes), sign, n));
            }
            case "-newer" or "-anewer" or "-cnewer":
            {
                string reference = Argument(t);
                var info = Directory.Exists(reference) ? new DirectoryInfo(reference) : (FileSystemInfo)new FileInfo(reference);
                if (!info.Exists) throw new FindException($"'{reference}': No such file or directory");
                var since = info.LastWriteTimeUtc;
                char kind = t == "-newer" ? 'm' : t[1];
                return new Primary(e => Time(e.Stat, kind) > since);
            }
            case "-newermt":
            {
                string text = Argument(t);
                if (!DateTime.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.AssumeLocal, out var when))
                    throw new FindException($"I cannot figure out how to interpret '{text}' as a date or time");
                var since = when.ToUniversalTime();
                return new Primary(e => e.Stat.LastWriteTimeUtc > since);
            }
            case "-true": return Primary.True;
            case "-false": return Primary.False;

            // Global options: they apply to the whole walk wherever they appear.
            case "-maxdepth" or "-mindepth":
            {
                string text = Argument(t);
                if (!int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out int depth))
                    throw new FindException($"Expected a positive decimal integer argument to {t}, but got '{text}'");
                if (t == "-maxdepth") find.MaxDepth = depth;
                else find.MinDepth = depth;
                return Primary.True;
            }
            case "-depth" or "-d":
                find.DepthFirst = true;
                return Primary.True;
            case "-follow":
                find.Follow = LinkMode.All;
                return Primary.True;
            case "-xdev" or "-mount" or "-noleaf" or "-ignore_readdir_race" or "-noignore_readdir_race" or "-nowarn" or "-warn":
                return Primary.True;

            // Actions.
            case "-prune":
                return new Primary(_ =>
                {
                    find.PruneCurrent = true;
                    return true;
                });
            case "-quit":
                return new Primary(_ =>
                {
                    find.Quit = true;
                    return true;
                });
            case "-print" or "-print0":
            {
                HasAction = true;
                char terminator = t == "-print" ? '\n' : '\0';
                return new Primary(e =>
                {
                    find.Print(e.Path, terminator);
                    return true;
                });
            }
            case "-printf":
            {
                HasAction = true;
                var format = new PrintfFormat(Argument(t));
                return new Primary(e =>
                {
                    find.Out.Write(format.Format(e));
                    return true;
                });
            }
            case "-delete":
                HasAction = true;
                find.DepthFirst = true; // a directory's contents must go before the directory
                return new Primary(find.Delete);
            case "-exec" or "-execdir":
                HasAction = true;
                return ParseExec(t);

            default:
                throw t.StartsWith('-')
                    ? new FindException($"unknown predicate '{t}'")
                    : new FindException($"paths must precede expression: '{t}'");
        }
    }

    private Primary ParseExec(string option)
    {
        var command = new List<string>();
        while (true)
        {
            if (_i >= tokens.Count || (command.Count == 0 && tokens[_i] is ";" or "\\;"))
                throw new FindException($"missing argument to '{option}'");
            string token = tokens[_i++];
            if (token is ";" or "\\;") break;
            if (token == "+" && command.Count > 0 && command[^1] == "{}")
            {
                command.RemoveAt(command.Count - 1);
                var batch = new ExecAction(find, command, batch: true, inDirectory: option == "-execdir");
                find.Batches.Add(batch);
                return new Primary(batch.Eval);
            }
            command.Add(token);
        }
        return new Primary(new ExecAction(find, command, batch: false, inDirectory: option == "-execdir").Eval);
    }

    private Regex BuildRegex(string pattern, bool ignoreCase)
    {
        var options = RegexOptions.CultureInvariant | RegexOptions.Singleline;
        if (ignoreCase) options |= RegexOptions.IgnoreCase;
        try
        {
            string body = find.RegexType switch
            {
                RegexType.DotNet => pattern,
                var type => PosixRegex.Translate(pattern, type == RegexType.Extended, ignoreCase, out _),
            };
            // Like GNU find, the regex has to match the whole path.
            return new Regex($@"\A(?:{body})\z", options);
        }
        catch (Exception e) when (e is PatternException or ArgumentException)
        {
            throw new FindException($"invalid regular expression '{pattern}': {e.Message}");
        }
    }

    /// <summary>-size [+-]N[cwbkMG]: sizes are rounded up to whole units, as in GNU find.</summary>
    private static Primary ParseSize(string text)
    {
        long unit = 512;
        string digits = text;
        if (digits.Length > 0 && char.IsAsciiLetter(digits[^1]))
        {
            unit = digits[^1] switch
            {
                'c' => 1,
                'w' => 2,
                'b' => 512,
                'k' => 1024,
                'M' => 1024 * 1024,
                'G' => 1024L * 1024 * 1024,
                _ => throw new FindException($"invalid -size type '{digits[^1]}'"),
            };
            digits = digits[..^1];
        }
        var (sign, n) = ParseNumber(digits, "-size");
        return new Primary(e => e.Type != 'd' && Compare((e.Size + unit - 1) / unit, sign, n));
    }

    private static (int Sign, long N) ParseNumber(string text, string option)
    {
        int sign = text.StartsWith('+') ? 1 : text.StartsWith('-') ? -1 : 0;
        string digits = sign == 0 ? text : text[1..];
        if (!long.TryParse(digits, NumberStyles.None, CultureInfo.InvariantCulture, out long n))
            throw new FindException($"invalid argument '{text}' to '{option}'");
        return (sign, n);
    }

    private static bool Compare(long value, int sign, long n) => sign switch
    {
        > 0 => value > n,
        < 0 => value < n,
        _ => value == n,
    };

    private TimeSpan Age(FileEntry e, char kind) => find.StartTime - Time(e.Stat, kind);

    /// <summary>m = modified, a = accessed, c = created (Windows has no inode change time).</summary>
    internal static DateTime Time(FileSystemInfo info, char kind) => kind switch
    {
        'a' => info.LastAccessTimeUtc,
        'c' => info.CreationTimeUtc,
        _ => info.LastWriteTimeUtc,
    };

    private static bool IsEmpty(FileEntry e)
    {
        if (e.Type == 'f') return e.Size == 0;
        if (e.Type != 'd') return false;
        try
        {
            return !Directory.EnumerateFileSystemEntries(e.Stat.FullName).Any();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    private static string ForwardSlashes(string path) => path.Replace('\\', '/');
}
