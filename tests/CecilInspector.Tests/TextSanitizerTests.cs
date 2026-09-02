using CecilInspector.Output;
using Xunit;

namespace CecilInspector.Tests;

public sealed class TextSanitizerTests
{
    [Theory]
    [InlineData(null, "")]
    [InlineData("", "")]
    [InlineData("plain.Name`1<T>", "plain.Name`1<T>")]
    [InlineData("日本語 and emoji \U0001F600", "日本語 and emoji \U0001F600")]
    public void LeavesPrintableTextUntouched(string? input, string expected)
    {
        Assert.Equal(expected, TextSanitizer.Escape(input));
    }

    [Fact]
    public void EscapesControlCharacters()
    {
        Assert.Equal("a\\rb\\nc\\td\\ee\\u0000", TextSanitizer.Escape("a\rb\nc\td\u001be\0"));
    }

    [Fact]
    public void EscapesFormatAndSeparatorCharacters()
    {
        // Bidi override, zero-width joiner, line/paragraph separators and a supplementary tag
        // character can all disguise what a symbol looks like in a terminal.
        Assert.Equal(
            "\\u202Eevil\\u200D\\u2028\\u2029\\U000E0001",
            TextSanitizer.Escape("\u202Eevil\u200D\u2028\u2029\U000E0001"));
    }

    [Fact]
    public void EscapesLoneSurrogates()
    {
        Assert.Equal("x\\uD83Dy", TextSanitizer.Escape("x\uD83Dy"));
        Assert.Equal("x\\uDE00", TextSanitizer.Escape("x\uDE00"));
        Assert.Equal("end\\uD83D", TextSanitizer.Escape("end\uD83D"));
    }
}
