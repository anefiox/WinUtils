using System.Text;

namespace Grep.Tests;

/// <summary>Cases aimed at the whole-buffer scan, which skips non-matching lines without visiting them.</summary>
[Collection("grep")]
public sealed class BufferScanTests : IDisposable
{
    private readonly GrepRunner _g = new();

    public void Dispose() => _g.Dispose();

    [Fact]
    public void BufferMatchSpanningLinesIsNotALineMatch()
    {
        // In the buffer, a[^x]*b matches "a\nb" across lines 1-2; only line 3 really matches.
        Assert.Equal("3:ab\n", _g.Pipe("a\nb\nab\n", "-nE", "a[^x]*b").Stdout);
    }

    [Fact]
    public void AnchorsWorkInTheMiddleOfTheBuffer()
    {
        Assert.Equal("2:a\n", _g.Pipe("xa\na\nax\n", "-n", "^a$").Stdout);
        Assert.Equal("2:a\r\n", _g.Pipe("xa\r\na\r\nax\r\n", "-n", "^a$").Stdout);
        Assert.Equal("2:a\r\n", _g.Pipe("xa\r\na\r\nax\r\n", "-nx", "a").Stdout);
        Assert.Equal("2:a\n", _g.Pipe("xa\na\n", "-nE", @"\`a\'").Stdout);
    }

    [Fact]
    public void LineNumbersAndOffsetsCountSkippedLines()
    {
        Assert.Equal("4:12:needle\r\n", _g.Pipe("aa\r\nbbb\r\nc\r\nneedle\r\n", "-nb", "needle").Stdout);
    }

    [Fact]
    public void OffsetsCountMultiByteCharacters()
    {
        Assert.Equal("7:x\n", _g.Pipe("héllo\nx\n", "-b", "x").Stdout);
    }

    [Fact]
    public void ManyMatchesAcrossChunkBoundaries()
    {
        // ~1.5 MB, far bigger than one 64 KB read, with a match every 1000 lines.
        var sb = new StringBuilder();
        for (int i = 1; i <= 200_000; i++) sb.Append(i % 1000 == 0 ? $"needle {i}\r\n" : $"filler line {i}\r\n");
        _g.File("big.txt", sb.ToString());

        Assert.Equal("200\n", _g.Run("-c", "needle", "big.txt").Stdout);
        var lines = _g.Run("-n", "needle", "big.txt").Stdout.Split("\r\n", StringSplitOptions.RemoveEmptyEntries);
        Assert.Equal(200, lines.Length);
        Assert.Equal("200000:needle 200000", lines[^1]);
        Assert.Equal("1000:needle 1000\r\n2000:needle 2000\r\n", _g.Run("-n", "-m2", "needle", "big.txt").Stdout);
    }

    [Fact]
    public void FinalLineWithoutNewline()
    {
        Assert.Equal("2:end\n", _g.Pipe("start\nend", "-n", "end$").Stdout);
        Assert.Equal("1:a\n2:b\n", _g.Pipe("a\nb", "-n", "").Stdout);
    }

    [Fact]
    public void EmptyMatchesSelectEveryLine()
    {
        Assert.Equal("3\n", _g.Pipe("a\n\nb\n", "-c", "x*").Stdout);
    }

    [Fact]
    public void NulInASkippedLineMakesTheFileBinary()
    {
        // The NUL is beyond the first block, so only the skipped-region check can see it.
        byte[] data = [.. Encoding.ASCII.GetBytes(new string('x', 70_000) + "\n"), 0, .. "\nhit\n"u8];
        _g.Bytes("late.bin", data);
        Assert.Equal("grep: late.bin: binary file matches\n", _g.Run("hit", "late.bin").Stdout);
        Assert.Equal(1, _g.Run("-I", "hit", "late.bin").ExitCode);
    }

    [Fact]
    public void WordMatchAtLineEdges()
    {
        Assert.Equal("cat\ndog cat\n", _g.Pipe("cat\ncats\ndog cat\n", "-w", "cat").Stdout);
    }
}
