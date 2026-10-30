# WinUtils

Native Windows ports of the best Unix command-line tools, written in C#. Each tool is a
single self-contained `.exe` (Native AOT, no .NET install needed) that behaves like its GNU
counterpart but understands Windows: CRLF line endings, UTF-16 files, `\` paths, and shells
that don't expand wildcards.

| Tool | Status |
|------|--------|
| `grep` | ✅ 0.1 |

## Build

```powershell
dotnet test              # run the tests
.\build.ps1              # publish every tool (needs the MSVC build tools for AOT)
.\build.ps1 -Tool grep   # publish just one
```

Every tool ships on its own: `publish\<tool>\<tool>.exe` is a standalone exe with no
dependencies, and `publish\<tool>-<version>-win-x64.zip` is its release download. Install
one tool or all of them by putting the exes somewhere on your `PATH`.

## Layout

```
src/WinUtils.Common   shared by every tool
  CommandLine/          GNU getopt_long-style option parser
  IO/                   LineReader (encoding detection), OutputSink (Unicode console output),
                        Wildcards (expands *.txt, since cmd/PowerShell don't), FileIdentity
  Text/                 Glob (fnmatch)
src/Grep              grep
tests/Grep.Tests      xUnit tests
```

To add a tool: create `src/<Tool>` with `<AssemblyName>tool</AssemblyName>` and
`<PublishAot>true</PublishAot>`, reference `WinUtils.Common`, and add it to `WinUtils.slnx`.

## grep

Supports essentially all of GNU grep 3.x's options: `-E -F -G -P -e -f -i -v -w -x -z -c -l -L
-o -q -s -m -n -b -H -h -T -Z -A -B -C -NUM -r -R -a -I -U --include --exclude --exclude-dir
--exclude-from --color --label --line-buffered --binary-files --directories --group-separator`.
Run `grep --help` for the full list.

**Windows behaviour**

- **Line endings:** CRLF counts as the line terminator, so `foo$` matches `foo\r\n`. Lines are
  written back with their original endings. Use `-U` to treat `\r` as part of the line.
- **Encodings:** UTF-8 and UTF-16 files with a BOM are decoded, so files written by Windows
  PowerShell's `>` can be searched. UTF-16 matches are printed as UTF-8. Files that aren't valid
  UTF-8 pass through byte-for-byte.
- **Console output:** characters display correctly whatever the console code page is, because
  output goes through `WriteConsoleW`. Colors use ANSI sequences, enabled automatically.
- **Wildcards:** `grep foo *.log` expands `*.log` itself.
- **Paths:** recursion prints paths with `\`. Symlinks and junctions are followed only with `-R`.
- **Output file as input:** `grep -r x . > out.txt` skips `out.txt` instead of reading its own
  output (in shells that redirect directly, such as cmd.exe).

**Differences from GNU grep**

- `-P` uses .NET regular expressions, which are close to PCRE.
- In BRE/ERE, `\d` and `\D` match digits, as an extension. GNU treats `\d` as a literal `d`.
- Alternation is leftmost-first (.NET) instead of POSIX leftmost-longest. This only changes
  which text `-o` and `--color` show, never which lines match.
- Invalid UTF-8 doesn't make a file "binary". Only NUL bytes do.
