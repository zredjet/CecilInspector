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
    public void SubcommandHelpIsRecognized(string command)
    {
        var result = CommandLine.Parse([command, "--help"]);

        Assert.True(result.IsHelp);
        Assert.Null(result.Error);
        Assert.Null(result.Options);
        Assert.False(result.ShowVersion);
    }

    [Theory]
    [InlineData("search", "a.dll", "Save", "--help")]
    [InlineData("search", "--kind", "method", "-h", "a.dll")]
    [InlineData("dump", "a.dll", "--include-il", "--help")]
    public void HelpIsRecognizedAnywhereAmongOptions(params string[] args)
    {
        Assert.True(CommandLine.Parse(args).IsHelp);
    }

    [Fact]
    public void KnownOptionAfterSeparatorStillTakesItsInlineValue()
    {
        var result = CommandLine.Parse(["search", "a.dll", "--", "-Prefixed", "--kind=type"]);

        var options = Assert.IsType<SearchOptions>(result.Options);
        Assert.Equal("-Prefixed", options.Query);
        Assert.Equal(SearchKinds.Type, options.Kinds);
    }

    [Fact]
    public void HelpAfterSeparatorIsAQuery()
    {
        var result = CommandLine.Parse(["search", "a.dll", "--", "--help"]);

        var options = Assert.IsType<SearchOptions>(result.Options);
        Assert.Equal("--help", options.Query);
    }

    [Fact]
    public void NoArgumentsIsAnError()
    {
        var result = CommandLine.Parse([]);

        Assert.False(result.IsHelp);
        Assert.Contains("コマンド", result.Error, StringComparison.Ordinal);
    }

    [Fact]
    public void HelpExampleWithOptionsAfterSeparatorParses()
    {
        var result = CommandLine.Parse(["search", "app.dll", "--", "-Prefixed", "--match", "exact"]);

        var options = Assert.IsType<SearchOptions>(result.Options);
        Assert.Equal("-Prefixed", options.Query);
        Assert.Equal(MatchMode.Exact, options.MatchMode);
    }

    [Theory]
    [InlineData("--not-an-option")]
    [InlineData("-x")]
    [InlineData("--kind-of=")]
    public void UnknownDashTokenAfterSeparatorIsPositional(string query)
    {
        var result = CommandLine.Parse(["search", "app.dll", "--", query]);

        var options = Assert.IsType<SearchOptions>(result.Options);
        Assert.Equal(query, options.Query);
    }

    [Theory]
    [InlineData("dump", "app.dll", "--output", "--")]
    [InlineData("search", "app.dll", "--output", "--", "-Prefixed")]
    [InlineData("search", "app.dll", "x", "-o", "--")]
    public void SeparatorIsNotAnOptionValue(params string[] args)
    {
        var result = CommandLine.Parse(args);

        Assert.False(result.IsSuccess);
        Assert.Contains("--outputにはファイルパスが必要", result.Error, StringComparison.Ordinal);
    }

    [Fact]
    public void InlineValuesParse()
    {
        var result = CommandLine.Parse([
            "search", "a.dll", "Save", "--kind=method,type", "--scope=all", "--max-results=5", "--output=out.txt",
        ]);

        var options = Assert.IsType<SearchOptions>(result.Options);
        Assert.Equal(SearchKinds.Method | SearchKinds.Type, options.Kinds);
        Assert.Equal(SearchScope.All, options.Scope);
        Assert.Equal(5, options.MaxResults);
        Assert.Equal("out.txt", options.OutputPath);
    }

    [Fact]
    public void InlineValueMayContainEquals()
    {
        var result = CommandLine.Parse(["search", "a.dll", "Save", "--output=a=b.txt"]);

        var options = Assert.IsType<SearchOptions>(result.Options);
        Assert.Equal("a=b.txt", options.OutputPath);
    }

    [Theory]
    [InlineData("search", "a.dll", "Save", "--case-sensitive=true")]
    [InlineData("search", "a.dll", "Save", "--no-recursive=1")]
    [InlineData("dump", "a.dll", "--include-il=yes")]
    public void InlineValueOnFlagIsRejected(params string[] args)
    {
        var result = CommandLine.Parse(args);

        Assert.False(result.IsSuccess);
        Assert.Contains("値を取りません", result.Error, StringComparison.Ordinal);
    }

    [Fact]
    public void UnknownInlineOptionExplainsTheForm()
    {
        var result = CommandLine.Parse(["search", "a.dll", "Save", "--kinds-of=type"]);

        Assert.False(result.IsSuccess);
        Assert.Contains("不明なオプション '--kinds-of=type'", result.Error, StringComparison.Ordinal);
        Assert.Contains("--name=value", result.Error, StringComparison.Ordinal);
    }

    [Fact]
    public void RepeatedKindIsAUnion()
    {
        var result = CommandLine.Parse(["search", "a.dll", "Save", "--kind", "method", "--kinds", "type"]);

        var options = Assert.IsType<SearchOptions>(result.Options);
        Assert.Equal(SearchKinds.Method | SearchKinds.Type, options.Kinds);
    }

    [Theory]
    [InlineData("search", "a.dll", "Save", "--scope", "all", "--scope", "definitions")]
    [InlineData("search", "a.dll", "Save", "--output", "a.txt", "--output", "b.txt")]
    [InlineData("search", "a.dll", "Save", "-o", "a.txt", "--output", "b.txt")]
    [InlineData("search", "a.dll", "Save", "--max-results", "1", "--max-results=2")]
    [InlineData("dump", "a.dll", "--symbols", "off", "--symbols", "auto")]
    public void RepeatedValueOptionIsRejected(params string[] args)
    {
        var result = CommandLine.Parse(args);

        Assert.False(result.IsSuccess);
        Assert.Contains("が重複しています", result.Error, StringComparison.Ordinal);
    }

    [Fact]
    public void RepeatedFlagsAreHarmless()
    {
        var result = CommandLine.Parse(["search", "a.dll", "Save", "--case-sensitive", "--case-sensitive"]);

        var options = Assert.IsType<SearchOptions>(result.Options);
        Assert.False(options.IgnoreCase);
    }

    [Theory]
    [InlineData("0")]
    [InlineData("-1")]
    [InlineData("abc")]
    [InlineData("+5")]
    [InlineData("1,000")]
    [InlineData(" 5")]
    [InlineData("")]
    public void InvalidMaxResultsIsRejected(string value)
    {
        var result = CommandLine.Parse(["search", "a.dll", "Save", "--max-results", value]);

        Assert.False(result.IsSuccess);
        Assert.Contains("--max-resultsには1以上の整数", result.Error, StringComparison.Ordinal);
    }

    [Fact]
    public void OverflowingMaxResultsIsReportedAsTooLarge()
    {
        var result = CommandLine.Parse(["search", "a.dll", "Save", "--max-results", "99999999999"]);

        Assert.False(result.IsSuccess);
        Assert.Contains("大きすぎます", result.Error, StringComparison.Ordinal);
        Assert.Contains("2147483647", result.Error, StringComparison.Ordinal);
    }

    [Fact]
    public void MaxResultsParses()
    {
        var options = Assert.IsType<SearchOptions>(
            CommandLine.Parse(["search", "a.dll", "Save", "--max-results", "7"]).Options);

        Assert.Equal(7, options.MaxResults);
    }

    [Theory]
    [InlineData("search", "a.dll", "Save", "--output", "")]
    [InlineData("search", "a.dll", "Save", "-o", "  ")]
    [InlineData("dump", "a.dll", "--output=")]
    [InlineData("dump", "a.dll", "--output")]
    public void EmptyOutputPathIsRejected(params string[] args)
    {
        var result = CommandLine.Parse(args);

        Assert.False(result.IsSuccess);
        Assert.Contains("--outputにはファイルパスが必要", result.Error, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("search", "a.dll", "Save", "--reference-path", "")]
    [InlineData("search", "a.dll", "Save", "--reference-path")]
    [InlineData("dump", "a.dll", "--reference-path=")]
    public void EmptyReferencePathIsRejected(params string[] args)
    {
        var result = CommandLine.Parse(args);

        Assert.False(result.IsSuccess);
        Assert.Contains("--reference-pathにはフォルダパスが必要", result.Error, StringComparison.Ordinal);
    }

    [Fact]
    public void MsBuildFormatRejectsSymbolsOff()
    {
        var result = CommandLine.Parse(["search", "a.dll", "Save", "--format", "msbuild", "--symbols", "off"]);

        Assert.False(result.IsSuccess);
        Assert.Contains("併用できません", result.Error, StringComparison.Ordinal);
    }

    [Fact]
    public void EmptyQueryIsRejected()
    {
        var result = CommandLine.Parse(["search", "a.dll", ""]);

        Assert.False(result.IsSuccess);
        Assert.Contains("検索文言を空", result.Error, StringComparison.Ordinal);
    }

    [Fact]
    public void DumpWithoutInputIsRejected()
    {
        var result = CommandLine.Parse(["dump"]);

        Assert.False(result.IsSuccess);
        Assert.Contains("dumpには入力パスが必要", result.Error, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("search", "a.dll", "Save", "--quiet")]
    [InlineData("search", "-q", "a.dll", "Save")]
    [InlineData("dump", "a.dll", "--quiet")]
    [InlineData("dump", "a.dll", "-q", "-q")]
    public void QuietParsesForBothCommands(params string[] args)
    {
        var result = CommandLine.Parse(args);

        Assert.NotNull(result.Options);
        Assert.True(result.Options.Quiet);
    }

    [Fact]
    public void QuietDefaultsToFalseAndTakesNoValue()
    {
        Assert.False(CommandLine.Parse(["search", "a.dll", "Save"]).Options!.Quiet);
        Assert.False(CommandLine.Parse(["dump", "a.dll"]).Options!.Quiet);

        var result = CommandLine.Parse(["search", "a.dll", "Save", "--quiet=true"]);
        Assert.False(result.IsSuccess);
        Assert.Contains("値を取りません", result.Error, StringComparison.Ordinal);
    }

    [Fact]
    public void ShortOutputAliasAndSearchFlagsParse()
    {
        var result = CommandLine.Parse([
            "search", "a.dll", "Save", "-o", "x.txt", "--symbols", "required", "--no-recursive", "--kind", "all",
        ]);

        var options = Assert.IsType<SearchOptions>(result.Options);
        Assert.Equal("x.txt", options.OutputPath);
        Assert.Equal(SymbolMode.Required, options.SymbolMode);
        Assert.False(options.Recursive);
        Assert.Equal(SearchKinds.All, options.Kinds);
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
    [InlineData("--format", "1")]
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

    [Theory]
    [InlineData("msbuild")]
    [InlineData("MSBUILD")]
    public void ParsesReportFormat(string value)
    {
        var result = CommandLine.Parse(["search", "a.dll", "Save", "--format", value]);

        var options = Assert.IsType<SearchOptions>(result.Options);
        Assert.Equal(ReportFormat.MsBuild, options.Format);
    }

    [Fact]
    public void ReportFormatDefaultsToText()
    {
        var options = Assert.IsType<SearchOptions>(CommandLine.Parse(["search", "a.dll", "Save"]).Options);

        Assert.Equal(ReportFormat.Text, options.Format);
    }

    [Fact]
    public void RejectsUnknownReportFormat()
    {
        var result = CommandLine.Parse(["search", "a.dll", "Save", "--format", "xml"]);

        Assert.False(result.IsSuccess);
        Assert.Contains("--format", result.Error, StringComparison.Ordinal);
    }

    [Fact]
    public void DumpDoesNotAcceptReportFormat()
    {
        var result = CommandLine.Parse(["dump", "a.dll", "--format", "msbuild"]);

        Assert.False(result.IsSuccess);
        Assert.Contains("不明なオプション", result.Error, StringComparison.Ordinal);
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
