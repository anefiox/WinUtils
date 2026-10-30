namespace WinUtils.Common.CommandLine;

public enum ArgumentKind { None, Required, Optional }

/// <summary>One spelling of an option. Several specs may share an <see cref="Id"/> to act as aliases.</summary>
public sealed record OptionSpec(string Id, char? Short = null, string? Long = null, ArgumentKind Argument = ArgumentKind.None);

public readonly record struct ParsedOption(string Id, string? Value, string Spelling);

public sealed class ParseResult
{
    /// <summary>Options in command-line order; order matters for options that override each other.</summary>
    public List<ParsedOption> Options { get; } = [];
    public List<string> Operands { get; } = [];
}

public sealed class OptionException(string message) : Exception(message);

/// <summary>
/// GNU getopt_long-style parser: bundled short options (-abc), attached or separate arguments
/// (-eFOO, -e FOO, --regexp=FOO, --regexp FOO), unambiguous long-option prefixes, the "--"
/// terminator, and argument permutation (options may follow operands) unless POSIXLY_CORRECT is set.
/// </summary>
public sealed class OptionParser
{
    /// <summary>Id reported for GNU-style numeric options such as grep's -5.</summary>
    public const string NumericId = "#";

    private readonly Dictionary<char, OptionSpec> _short = [];
    private readonly List<OptionSpec> _long = [];
    private readonly bool _numeric;

    public OptionParser(IEnumerable<OptionSpec> specs, bool allowNumericOptions = false)
    {
        foreach (var spec in specs)
        {
            if (spec.Short is char c) _short[c] = spec;
            if (spec.Long is not null) _long.Add(spec);
        }
        _numeric = allowNumericOptions;
    }

    public ParseResult Parse(IReadOnlyList<string> args)
    {
        var result = new ParseResult();
        bool permute = Environment.GetEnvironmentVariable("POSIXLY_CORRECT") is null;
        bool onlyOperands = false;
        for (int i = 0; i < args.Count; i++)
        {
            string arg = args[i];
            if (onlyOperands || arg.Length < 2 || arg[0] != '-')
            {
                result.Operands.Add(arg);
                if (!permute) onlyOperands = true;
                continue;
            }
            if (arg == "--")
            {
                onlyOperands = true;
                continue;
            }
            i = arg[1] == '-' ? ParseLong(args, i, result) : ParseShort(args, i, result);
        }
        return result;
    }

    private int ParseLong(IReadOnlyList<string> args, int i, ParseResult result)
    {
        string arg = args[i];
        int eq = arg.IndexOf('=');
        string name = eq < 0 ? arg[2..] : arg[2..eq];
        string? value = eq < 0 ? null : arg[(eq + 1)..];
        var spec = FindLong(name);
        string spelling = "--" + spec.Long;
        if (spec.Argument == ArgumentKind.None && value is not null)
            throw new OptionException($"option '{spelling}' doesn't allow an argument");
        if (spec.Argument == ArgumentKind.Required && value is null)
        {
            if (i + 1 >= args.Count) throw new OptionException($"option '{spelling}' requires an argument");
            value = args[++i];
        }
        result.Options.Add(new ParsedOption(spec.Id, value, spelling));
        return i;
    }

    private OptionSpec FindLong(string name)
    {
        OptionSpec? match = null;
        bool ambiguous = false;
        foreach (var spec in _long)
        {
            if (spec.Long == name) return spec;
            if (!spec.Long!.StartsWith(name, StringComparison.Ordinal)) continue;
            if (match is null) match = spec;
            else if (match.Id != spec.Id || match.Argument != spec.Argument) ambiguous = true;
        }
        if (ambiguous) throw new OptionException($"option '--{name}' is ambiguous");
        return match ?? throw new OptionException($"unrecognized option '--{name}'");
    }

    private int ParseShort(IReadOnlyList<string> args, int i, ParseResult result)
    {
        string arg = args[i];
        for (int j = 1; j < arg.Length; j++)
        {
            char c = arg[j];
            if (_numeric && char.IsAsciiDigit(c))
            {
                int end = j;
                while (end < arg.Length && char.IsAsciiDigit(arg[end])) end++;
                result.Options.Add(new ParsedOption(NumericId, arg[j..end], "-" + arg[j..end]));
                j = end - 1;
                continue;
            }
            if (!_short.TryGetValue(c, out var spec))
                throw new OptionException($"invalid option -- '{c}'");
            string spelling = "-" + c;
            if (spec.Argument == ArgumentKind.None)
            {
                result.Options.Add(new ParsedOption(spec.Id, null, spelling));
                continue;
            }
            // An option that takes an argument consumes the rest of this word, or the next word.
            string? value = j + 1 < arg.Length ? arg[(j + 1)..] : null;
            if (value is null && spec.Argument == ArgumentKind.Required)
            {
                if (i + 1 >= args.Count) throw new OptionException($"option requires an argument -- '{c}'");
                value = args[++i];
            }
            result.Options.Add(new ParsedOption(spec.Id, value, spelling));
            break;
        }
        return i;
    }
}
