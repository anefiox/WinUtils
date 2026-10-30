namespace WinUtils.Tr;

internal static class HelpText
{
    public const string Version = """
        tr (WinUtils) 0.1.0
        A Windows-native reimplementation of GNU tr, written in C#.

        """;

    public const string Text = """
        Usage: tr [OPTION]... SET1 [SET2]
        Translate, squeeze, and/or delete characters from standard input,
        writing to standard output.

          -c, -C, --complement    use the complement of SET1
          -d, --delete            delete characters in SET1, do not translate
          -s, --squeeze-repeats   replace each sequence of a repeated character
                                    that is listed in the last specified SET,
                                    with a single occurrence of that character
          -t, --truncate-set1     first truncate SET1 to length of SET2
              --help              display this help and exit
              --version           output version information and exit

        SETs are specified as strings of characters. Most represent themselves.
        Interpreted sequences are:

          \NNN            character with octal value NNN (1 to 3 octal digits)
          \\              backslash
          \a \b \f \n \r \t \v   control characters
          CHAR1-CHAR2     all characters from CHAR1 to CHAR2 in ascending order
          [CHAR*]         in SET2, copies of CHAR until length of SET1
          [CHAR*REPEAT]   REPEAT copies of CHAR, REPEAT octal if starting with 0
          [:alnum:] [:alpha:] [:blank:] [:cntrl:] [:digit:] [:graph:] [:lower:]
          [:print:] [:punct:] [:space:] [:upper:] [:xdigit:]   character classes
          [=CHAR=]        all characters which are equivalent to CHAR

        tr works on characters, not bytes: é is one character, and the classes include
        non-ASCII letters, so tr '[:lower:]' '[:upper:]' turns é into É. Carriage
        returns are ordinary characters, so tr -d '\r' converts CRLF line endings to LF.

        """;
}
