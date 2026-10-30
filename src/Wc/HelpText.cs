namespace WinUtils.Wc;

internal static class HelpText
{
    public const string Version = """
        wc (WinUtils) 0.1.0
        A Windows-native reimplementation of GNU wc, written in C#.

        """;

    public const string Text = """
        Usage: wc [OPTION]... [FILE]...
          or:  wc [OPTION]... --files0-from=F
        Print newline, word, and byte counts for each FILE, and a total line if
        more than one FILE is specified. A word is a non-zero-length sequence of
        characters delimited by white space.

        With no FILE, or when FILE is -, read standard input.

        The options below may be used to select which counts are printed, always in
        the following order: newline, word, character, byte, maximum line length.
          -c, --bytes            print the byte counts
          -m, --chars            print the character counts
          -l, --lines            print the newline counts
              --files0-from=F    read input from the files specified by
                                   NUL-terminated names in file F;
                                   If F is - then read names from standard input
          -L, --max-line-length  print the maximum display width
          -w, --words            print the word counts
              --total=WHEN       when to print a line with total counts;
                                   WHEN can be: auto, always, only, never
              --help             display this help and exit
              --version          output version information and exit

        Text may be UTF-8, UTF-16 (with a byte-order mark), or a single-byte
        encoding; characters, words and widths are counted by character. CRLF line
        endings count as one line. Wildcards (* and ?) in FILE are expanded.

        """;
}
