namespace WinUtils.Grep;

/// <summary>SGR color codes, configurable through GREP_COLORS (e.g. "ms=01;31:fn=35:ln=32").</summary>
internal sealed class ColorScheme
{
    public const string End = "\e[m\e[K";

    public string Match { get; private set; } = "01;31";
    public string FileName { get; private set; } = "35";
    public string LineNumber { get; private set; } = "32";
    public string ByteOffset { get; private set; } = "32";
    public string Separator { get; private set; } = "36";

    public static string Start(string sgr) => $"\e[{sgr}m\e[K";

    public static ColorScheme FromEnvironment()
    {
        var scheme = new ColorScheme();
        string? spec = Environment.GetEnvironmentVariable("GREP_COLORS");
        if (string.IsNullOrEmpty(spec)) return scheme;
        foreach (string item in spec.Split(':'))
        {
            int eq = item.IndexOf('=');
            if (eq < 0) continue; // boolean capabilities like "ne" and "rv" aren't supported
            string key = item[..eq], value = item[(eq + 1)..];
            switch (key)
            {
                case "mt" or "ms": scheme.Match = value; break;
                case "fn": scheme.FileName = value; break;
                case "ln": scheme.LineNumber = value; break;
                case "bn": scheme.ByteOffset = value; break;
                case "se": scheme.Separator = value; break;
            }
        }
        return scheme;
    }
}
