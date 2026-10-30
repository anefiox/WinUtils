namespace WinUtils.Xargs;

internal static class HelpText
{
    public const string Version = """
        xargs (WinUtils) 0.1.0
        A Windows-native reimplementation of GNU xargs, written in C#.

        """;

    public const string Text = """
        Usage: xargs [OPTION]... COMMAND [INITIAL-ARGS]...
        Run COMMAND with arguments INITIAL-ARGS and more arguments read from input.

          -0, --null                   items are separated by a null, not whitespace;
                                         disables quote and backslash processing and
                                         logical EOF processing
          -a, --arg-file=FILE          read arguments from FILE, not standard input
          -d, --delimiter=CHARACTER    items in input stream are separated by CHARACTER,
                                         not by whitespace; disables quote and backslash
                                         processing and logical EOF processing
          -E END                       set logical EOF string; if END occurs as a line
                                         of input, the rest of the input is ignored
          -e, --eof[=END]              equivalent to -E END if END is specified
          -I R                         same as --replace=R
          -i, --replace[=R]            replace R in INITIAL-ARGS with names read
                                         from standard input, split at newlines;
                                         if R is unspecified, assume {}
          -L, --max-lines=MAX-LINES    use at most MAX-LINES non-blank input lines per
                                         command line
          -l[MAX-LINES]                similar to -L but defaults to at most one
                                         non-blank input line if MAX-LINES is not specified
          -n, --max-args=MAX-ARGS      use at most MAX-ARGS arguments per command line
          -P, --max-procs=MAX-PROCS    run at most MAX-PROCS processes at a time
          -p, --interactive            prompt before running commands
              --process-slot-var=VAR   set environment variable VAR in child processes
          -r, --no-run-if-empty        if there are no arguments, then do not run COMMAND;
                                         if this option is not given, COMMAND will be
                                         run at least once
          -s, --max-chars=MAX-CHARS    limit length of command line to MAX-CHARS
              --show-limits            show limits on command-line length
          -t, --verbose                print commands before executing them
          -x, --exit                   exit if the size (see -s) is exceeded
              --help                   display this help and exit
              --version                output version information and exit

        The default COMMAND is echo, which (like GNU's /bin/echo) prints its arguments
        separated by spaces. Programs (.exe) run directly; .bat/.cmd files and cmd
        built-ins such as del, copy and type run through cmd /c, whose command lines
        are limited to 8191 characters, so batches are sized to fit.
        CRLF line endings in the input are treated as newlines.

        Exit status: 0 on success; 123 if any command exited with status 1-125;
        124 if a command exited with status 255; 125 if a command crashed;
        126 if a command cannot be run; 127 if a command is not found; 1 otherwise.

        """;
}
