namespace WinUtils.Sort;

internal static class HelpText
{
    public const string Version = """
        sort (WinUtils) 0.1.0
        A Windows-native reimplementation of GNU sort, written in C#.

        """;

    public const string Text = """
        Usage: sort [OPTION]... [FILE]...
          or:  sort [OPTION]... --files0-from=F
        Write sorted concatenation of all FILE(s) to standard output.

        With no FILE, or when FILE is -, read standard input.

        Ordering options:
          -b, --ignore-leading-blanks  ignore leading blanks
          -d, --dictionary-order      consider only blanks and alphanumeric characters
          -f, --ignore-case           fold lower case to upper case characters
          -g, --general-numeric-sort  compare according to general numerical value
          -i, --ignore-nonprinting    consider only printable characters
          -M, --month-sort            compare (unknown) < 'JAN' < ... < 'DEC'
          -h, --human-numeric-sort    compare human readable numbers (e.g., 2K 1G)
          -n, --numeric-sort          compare according to string numerical value
          -R, --random-sort           shuffle, but group identical keys
          -r, --reverse               reverse the result of comparisons
              --sort=WORD             sort according to WORD:
                                        general-numeric -g, human-numeric -h, month -M,
                                        numeric -n, random -R, version -V
          -V, --version-sort          natural sort of (version) numbers within text

        Other options:
          -c, --check, --check=diagnose-first  check for sorted input; do not sort
          -C, --check=quiet, --check=silent  like -c, but do not report first bad line
              --files0-from=F         read input from the files specified by
                                        NUL-terminated names in file F
          -k, --key=KEYDEF            sort via a key; KEYDEF gives location and type
          -m, --merge                 merge already sorted files; do not sort
          -o, --output=FILE           write result to FILE instead of standard output
          -s, --stable                stabilize sort by disabling last-resort comparison
          -S, --buffer-size=SIZE      use SIZE for main memory buffer (default 1/32 of
                                        available memory); larger input is sorted on disk
          -t, --field-separator=SEP   use SEP instead of non-blank to blank transition
          -T, --temporary-directory=DIR  use DIR for temporaries, not %TEMP%
          -u, --unique                output only the first of an equal run
          -z, --zero-terminated       line delimiter is NUL, not newline
              --help                  display this help and exit
              --version               output version information and exit

        KEYDEF is F[.C][OPTS][,F[.C][OPTS]] for start and stop position, where F is a
        field number and C a character position in the field; both are origin 1, and
        the stop position defaults to the line's end. OPTS is one or more single-letter
        ordering options [bdfghiMnRrV], which override global ordering options for
        that key. If no key is given, use the entire line as the key.

        Text is compared by Unicode code point (like GNU sort in the C.UTF-8 locale),
        so the result doesn't depend on Windows' language settings. Output keeps the
        input's line endings (CRLF stays CRLF). Wildcards (* and ?) are expanded.

        """;
}
