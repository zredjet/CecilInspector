using CecilInspector.Cli;
using Xunit;

namespace CecilInspector.Tests;

public sealed class CommandLineTests
{
    [Fact]
    public void ParsesSearchOptions()
    {
        var result = CommandLine.Parse([
            "search", "a.dll", "Save", "--kind", "method,property",
            "--scope", "all", "--match", "exact", "--case-sensitive",
        ]);

        var options = Assert.IsType<SearchOptions>(result.Options);
        Assert.Equal(SearchKinds.Method | SearchKinds.Property, options.Kinds);
        Assert.Equal(SearchScope.All, options.Scope);
        Assert.Equal(MatchMode.Exact, options.MatchMode);
        Assert.False(options.IgnoreCase);
    }

    [Theory]
    [InlineData("search")]
    [InlineData("dump")]
    public void SubcommandHelpIsSuccessful(string command)
    {
        var result = CommandLine.Parse([command, "--help"]);

        Assert.Null(result.Error);
        Assert.False(result.IsSuccess);
    }

    [Fact]
    public void UnknownCommandWithHelpIsRejected()
    {
        var result = CommandLine.Parse(["typo", "--help"]);

        Assert.False(result.IsSuccess);
        Assert.Contains("不明なコマンド", result.Error, StringComparison.Ordinal);
    }

    [Fact]
    public void InvalidRegexIsRejectedDuringParsing()
    {
        var result = CommandLine.Parse(["search", "a.dll", "[", "--match", "regex"]);

        Assert.False(result.IsSuccess);
        Assert.Contains("正規表現が不正", result.Error, StringComparison.Ordinal);
    }

    [Fact]
    public void ParsesMultipleReferencePaths()
    {
        var result = CommandLine.Parse([
            "search", "a.dll", "Save",
            "--reference-path", "lib1",
            "--reference-path", "lib2",
        ]);

        var options = Assert.IsType<SearchOptions>(result.Options);
        Assert.Equal(["lib1", "lib2"], options.ReferencePaths);
    }

    [Fact]
    public void RejectsUnknownOption()
    {
        var result = CommandLine.Parse(["search", "a.dll", "Save", "--wat"]);

        Assert.False(result.IsSuccess);
        Assert.Contains("不明なオプション", result.Error, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("--scope", "99")]
    [InlineData("--match", "99")]
    [InlineData("--symbols", "99")]
    [InlineData("--kind", "64")]
    public void RejectsNumericEnumValues(string option, string value)
    {
        var result = CommandLine.Parse(["search", "a.dll", "Save", option, value]);

        Assert.False(result.IsSuccess);
    }
}
