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
    [InlineData("--scope", "0")]
    [InlineData("--match", "99")]
    [InlineData("--match", "2")]
    [InlineData("--symbols", "99")]
    [InlineData("--symbols", "1")]
    [InlineData("--kind", "64")]
    [InlineData("--kind", "1,2")]
    public void RejectsNumericEnumValues(string option, string value)
    {
        var result = CommandLine.Parse(["search", "a.dll", "Save", option, value]);

        Assert.False(result.IsSuccess);
        Assert.Contains(option, result.Error, StringComparison.Ordinal);
    }

    [Fact]
    public void DashPrefixedQueryIsRejectedWithoutSeparator()
    {
        var result = CommandLine.Parse(["search", "a.dll", "--case-sensitive"]);

        Assert.False(result.IsSuccess);
        Assert.Contains("入力パスと検索文言が必要", result.Error, StringComparison.Ordinal);
    }

    [Fact]
    public void UnknownOptionHintsAtSeparator()
    {
        var result = CommandLine.Parse(["search", "a.dll", "-Prefixed"]);

        Assert.False(result.IsSuccess);
        Assert.Contains("不明なオプション '-Prefixed'", result.Error, StringComparison.Ordinal);
        Assert.Contains("'--'", result.Error, StringComparison.Ordinal);
    }

    [Fact]
    public void DoubleDashAllowsDashPrefixedQuery()
    {
        var result = CommandLine.Parse(["search", "a.dll", "--match", "exact", "--", "-Prefixed"]);

        var options = Assert.IsType<SearchOptions>(result.Options);
        Assert.Equal("-Prefixed", options.Query);
        Assert.Equal(MatchMode.Exact, options.MatchMode);
    }

    [Fact]
    public void OptionsMayPrecedePositionals()
    {
        var result = CommandLine.Parse(["search", "--kind", "method", "./bin", "Save"]);

        var options = Assert.IsType<SearchOptions>(result.Options);
        Assert.Equal("./bin", options.InputPath);
        Assert.Equal("Save", options.Query);
        Assert.Equal(SearchKinds.Method, options.Kinds);
    }

    [Theory]
    [InlineData("search", "a.dll", "Save", "extra")]
    [InlineData("dump", "a.dll", "extra")]
    public void ExtraPositionalIsRejected(params string[] args)
    {
        var result = CommandLine.Parse(args);

        Assert.False(result.IsSuccess);
        Assert.Contains("余分な引数 'extra'", result.Error, StringComparison.Ordinal);
    }

    [Fact]
    public void OutputValueMayBeNamedHelp()
    {
        var result = CommandLine.Parse(["search", "a.dll", "Save", "--output", "help"]);

        var options = Assert.IsType<SearchOptions>(result.Options);
        Assert.Equal("help", options.OutputPath);
    }

    [Theory]
    [InlineData("HELP")]
    [InlineData("--HELP")]
    [InlineData("-H")]
    public void HelpIsCaseInsensitive(string token)
    {
        Assert.Null(CommandLine.Parse([token]).Error);
        Assert.Null(CommandLine.Parse(["search", token]).Error);
    }

    [Theory]
    [InlineData("--version")]
    [InlineData("-V")]
    [InlineData("version")]
    public void VersionIsRecognized(string token)
    {
        var result = CommandLine.Parse([token]);

        Assert.True(result.ShowVersion);
        Assert.False(result.IsSuccess);
        Assert.Null(result.Error);
        Assert.Matches(@"^\d+\.\d+\.\d+", CommandLine.VersionText);
        Assert.DoesNotContain('+', CommandLine.VersionText);
    }

    [Fact]
    public void VersionWithExtraArgumentsIsAnUnknownCommand()
    {
        var result = CommandLine.Parse(["--version", "extra"]);

        Assert.False(result.ShowVersion);
        Assert.Contains("不明なコマンド", result.Error, StringComparison.Ordinal);
    }

    [Fact]
    public void ParsesDumpOptions()
    {
        var result = CommandLine.Parse(["dump", "a.dll", "--include-il", "--no-recursive", "--symbols", "required"]);

        var options = Assert.IsType<DumpOptions>(result.Options);
        Assert.True(options.IncludeIl);
        Assert.False(options.Recursive);
        Assert.Equal(SymbolMode.Required, options.SymbolMode);
    }
}
