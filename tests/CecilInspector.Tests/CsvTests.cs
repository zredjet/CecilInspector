using CecilInspector.Output;
using System.Text;
using Xunit;

namespace CecilInspector.Tests;

public sealed class CsvTests
{
    [Theory]
    [InlineData(null, "")]
    [InlineData("", "")]
    [InlineData("plain", "plain")]
    [InlineData("T::Save() : System.Void", "T::Save() : System.Void")]
    [InlineData("日本語 path\\to\\file.cs", "日本語 path\\to\\file.cs")]
    public void FieldLeavesPlainTextUnquoted(string? input, string expected)
    {
        Assert.Equal(expected, Csv.Field(input));
    }

    [Theory]
    [InlineData("a,b", "\"a,b\"")]
    [InlineData("say \"hi\"", "\"say \"\"hi\"\"\"")]
    [InlineData("System.Func`2<System.Int32, System.String>", "\"System.Func`2<System.Int32, System.String>\"")]
    public void FieldQuotesCommasAndDoublesQuotes(string input, string expected)
    {
        Assert.Equal(expected, Csv.Field(input));
    }

    [Fact]
    public void FieldEscapesControlCharactersBeforeQuoting()
    {
        // A line break becomes the two characters \n, so the field needs no quotes and the
        // record stays on one physical line.
        Assert.Equal("a\\nb", Csv.Field("a\nb"));
        Assert.Equal("\"a,\\r\\nb\"", Csv.Field("a,\r\nb"));
        // A BOM inside a symbol must not look like the report's own BOM.
        Assert.Equal("\\uFEFFx", Csv.Field("\uFEFFx"));
    }

    [Fact]
    public void WriteRowJoinsFieldsWithCommasAndEndsTheLine()
    {
        using var writer = new StringWriter();

        Csv.WriteRow(writer, new StringBuilder(), "a", null, "b,c");

        Assert.Equal("a,,\"b,c\"" + Environment.NewLine, writer.ToString());
    }
}
