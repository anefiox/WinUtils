using System.Globalization;
using System.Text;

namespace WinUtils.FindW;

/// <summary>The -printf format language: %p %f %h %P %H %s %k %d %y %l %t/%a/%c and %T?/%A?/%C?.</summary>
internal sealed class PrintfFormat(string format)
{
    public string Format(FileEntry e)
    {
        var sb = new StringBuilder();
        for (int i = 0; i < format.Length; i++)
        {
            char c = format[i];
            if (c == '\\' && i + 1 < format.Length)
            {
                char d = format[++i];
                sb.Append(d switch
                {
                    'n' => "\n",
                    't' => "\t",
                    'r' => "\r",
                    '0' => "\0",
                    'a' => "\a",
                    '\\' => "\\",
                    _ => "\\" + d,
                });
            }
            else if (c == '%' && i + 1 < format.Length)
            {
                char d = format[++i];
                switch (d)
                {
                    case '%': sb.Append('%'); break;
                    case 'p': sb.Append(e.Path); break;
                    case 'f': sb.Append(e.Name); break;
                    case 'h': sb.Append(Path.GetDirectoryName(Path.TrimEndingDirectorySeparator(e.Path)) is { Length: > 0 } dir ? dir : "."); break;
                    case 'P': sb.Append(e.Path[e.StartPoint.Length..].TrimStart('\\', '/')); break;
                    case 'H': sb.Append(e.StartPoint); break;
                    case 's': sb.Append(e.Size); break;
                    case 'k': sb.Append((e.Size + 1023) / 1024); break;
                    case 'd': sb.Append(e.Depth); break;
                    case 'y': sb.Append(e.Type); break;
                    case 'l': sb.Append(e.IsLink ? e.Info.LinkTarget : ""); break;
                    case 't' or 'a' or 'c':
                        sb.Append(Local(e, d == 't' ? 'm' : d).ToString("ddd MMM dd HH:mm:ss yyyy", CultureInfo.InvariantCulture));
                        break;
                    case 'T' or 'A' or 'C' when i + 1 < format.Length:
                        sb.Append(TimeField(Local(e, d == 'T' ? 'm' : char.ToLowerInvariant(d)), format[++i]));
                        break;
                    default:
                        sb.Append('%').Append(d);
                        break;
                }
            }
            else
            {
                sb.Append(c);
            }
        }
        return sb.ToString();
    }

    private static DateTime Local(FileEntry e, char kind) => ExpressionParser.Time(e.Stat, kind).ToLocalTime();

    private static string TimeField(DateTime t, char k) => k switch
    {
        '@' => ((t.ToUniversalTime() - DateTime.UnixEpoch).Ticks / 1e7).ToString("0.0######", CultureInfo.InvariantCulture),
        '+' => t.ToString("yyyy-MM-dd+HH:mm:ss", CultureInfo.InvariantCulture),
        'F' => t.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
        'T' => t.ToString("HH:mm:ss", CultureInfo.InvariantCulture),
        'D' => t.ToString("MM/dd/yy", CultureInfo.InvariantCulture),
        'Y' => t.ToString("yyyy", CultureInfo.InvariantCulture),
        'y' => t.ToString("yy", CultureInfo.InvariantCulture),
        'm' => t.ToString("MM", CultureInfo.InvariantCulture),
        'd' => t.ToString("dd", CultureInfo.InvariantCulture),
        'H' => t.ToString("HH", CultureInfo.InvariantCulture),
        'M' => t.ToString("mm", CultureInfo.InvariantCulture),
        'S' => t.ToString("ss", CultureInfo.InvariantCulture),
        'b' => t.ToString("MMM", CultureInfo.InvariantCulture),
        'a' => t.ToString("ddd", CultureInfo.InvariantCulture),
        'j' => t.DayOfYear.ToString("000", CultureInfo.InvariantCulture),
        _ => "%" + k,
    };
}
