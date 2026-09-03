using CecilInspector.Cli;
using CecilInspector.Output;
using Xunit;

namespace CecilInspector.Tests;

public sealed class AnsiTests
{
    [Fact]
    public void StrippingWriterRemovesSequencesEvenWhenSplitAcrossWrites()
    {
        using var inner = new StringWriter();
        using var writer = new AnsiStrippingTextWriter(inner);

        writer.Write("a\u001b[3");
        writer.Write("6mb\u001b[0m");
        writer.WriteLine("c\u001b[1;36md\u001b[0m");
        writer.Write('e');
        writer.Write("\u001b[90m".ToCharArray(), 0, 5);
        writer.WriteLine("f".AsSpan());
        writer.Flush();

        Assert.Equal($"abcd{Environment.NewLine}ef{Environment.NewLine}", inner.ToString());
    }

    [Fact]
    public void StrippingWriterPassesPlainTextThrough()
    {
        using var inner = new StringWriter();
        using var writer = new AnsiStrippingTextWriter(inner);

        writer.WriteLine("Query: Save");
        writer.WriteLine();
        writer.Write("done");

        Assert.Equal($"Query: Save{Environment.NewLine}{Environment.NewLine}done", inner.ToString());
    }

    [Theory]
    [InlineData(ColorMode.Always, true, "1", true)]
    [InlineData(ColorMode.Never, false, null, false)]
    [InlineData(ColorMode.Auto, false, null, true)]
    [InlineData(ColorMode.Auto, true, null, false)]
    [InlineData(ColorMode.Auto, false, "1", false)]
    [InlineData(ColorMode.Auto, false, "", true)]
    public void ShouldColorFollowsModeRedirectionAndNoColor(ColorMode mode, bool redirected, string? noColor, bool expected)
    {
        Assert.Equal(expected, AnsiConsole.ShouldColor(mode, redirected, name => name == "NO_COLOR" ? noColor : null));
    }

    [Fact]
    public void ShouldColorIsOffForDumbTerminals()
    {
        Assert.False(AnsiConsole.ShouldColor(ColorMode.Auto, false, name => name == "TERM" ? "dumb" : null));
        Assert.True(AnsiConsole.ShouldColor(ColorMode.Auto, false, name => name == "TERM" ? "xterm-256color" : null));
    }
}
