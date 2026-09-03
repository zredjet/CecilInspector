using CecilInspector.Cli;
using CecilInspector.Core;
using CecilInspector.Output;
using Xunit;

namespace CecilInspector.Tests;

public sealed class TextReportTests
{
    [Fact]
    public void WritesHeaderHitsAndTruncationFooter()
    {
        var options = new SearchOptions(
            "in.dll", "Save", SearchKinds.Method, SearchScope.All, MatchMode.Exact,
            false, true, SymbolMode.Off, 1, null, []);
        var hit = new SearchHit(
            "in.dll", "In", HitScope.Reference, HitKind.Method, "T::Save() : System.Void",
            "T::Caller() : System.Void", new SourceLocation("a.cs", 12, 5), 0x1A);
        var result = new SearchResult(
            [hit], 5, [new HitCount(HitScope.Reference, HitKind.Method, 5)], [], 1, 1, 1, []);
        using var writer = new StringWriter();

        TextReport.WriteSearch(writer, result, options);

        var text = writer.ToString();
        Assert.Contains("Query: Save", text, StringComparison.Ordinal);
        Assert.Contains("(case sensitive)", text, StringComparison.Ordinal);
        Assert.Contains("Matches: 5", text, StringComparison.Ordinal);
        Assert.Contains("Breakdown: reference/method=5", text, StringComparison.Ordinal);
        Assert.Contains("[reference/method] T::Save() : System.Void", text, StringComparison.Ordinal);
        Assert.Contains("  in: T::Caller() : System.Void", text, StringComparison.Ordinal);
        Assert.Contains("  source: a.cs:12:5", text, StringComparison.Ordinal);
        Assert.Contains("  il: IL_001A", text, StringComparison.Ordinal);
        Assert.Contains("... 4件を省略しました。--max-resultsで変更できます。", text, StringComparison.Ordinal);
    }

    [Fact]
    public void MsBuildFormatWritesOneNavigableLinePerHit()
    {
        var options = new SearchOptions(
            "in.dll", "Save", SearchKinds.All, SearchScope.All, MatchMode.Exact,
            true, true, SymbolMode.Auto, 1, null, [], ReportFormat.MsBuild);
        SearchHit[] hits =
        [
            new("in.dll", "In", HitScope.Reference, HitKind.Method, "T::Save() : System.Void",
                "T::Caller() : System.Void", new SourceLocation("a.cs", 12, 5), 0x1A),
            new("in.dll", "In", HitScope.Definition, HitKind.Method, "T::Save() : System.Void",
                null, new SourceLocation("a.cs", 30, 0), null),
            new("in.dll", "In", HitScope.Definition, HitKind.Type, "T", null, null, null),
        ];
        var result = new SearchResult(hits, 4, [new HitCount(HitScope.Reference, HitKind.Method, 4)], [], 1, 1, 1, []);
        using var writer = new StringWriter();

        TextReport.WriteSearch(writer, result, options);

        var lines = writer.ToString().Split(Environment.NewLine);
        Assert.DoesNotContain(lines, line => line.StartsWith("Query:", StringComparison.Ordinal));
        Assert.Contains("Matches: 4", lines);
        Assert.Contains(
            "a.cs(12,5): info CI0002: [reference/method] T::Save() : System.Void (in T::Caller() : System.Void) @ IL_001A",
            lines);
        Assert.Contains("a.cs(30): info CI0001: [definition/method] T::Save() : System.Void", lines);
        Assert.Contains("[definition/type] T  assembly: in.dll", lines);
        Assert.DoesNotContain(lines, line => line.Contains("in.dll(", StringComparison.Ordinal));
        Assert.Contains("... 1件を省略しました。--max-resultsで変更できます。", lines);
    }

    [Fact]
    public void MsBuildFormatCannotBeInjectedThroughSymbols()
    {
        var options = new SearchOptions(
            "in.dll", "x", SearchKinds.All, SearchScope.Definitions, MatchMode.Contains,
            true, true, SymbolMode.Off, 10, null, [], ReportFormat.MsBuild);
        var hit = new SearchHit(
            "in.dll", "In", HitScope.Definition, HitKind.Method, "Bad\nevil.cs(1,1): error X: Name",
            null, new SourceLocation("a.cs", 1, 1), null);
        var result = new SearchResult([hit], 1, [new HitCount(HitScope.Definition, HitKind.Method, 1)], [], 1, 1, 1, []);
        using var writer = new StringWriter();

        TextReport.WriteSearch(writer, result, options);

        var lines = writer.ToString().Split(Environment.NewLine);
        Assert.Single(lines, line => line.StartsWith("a.cs(", StringComparison.Ordinal));
        Assert.DoesNotContain(lines, line => line.StartsWith("evil.cs(", StringComparison.Ordinal));
        Assert.Contains(lines, line => line.Contains("Bad\\nevil.cs(1,1)", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData(SearchKinds.Type, SearchScope.Definitions, SymbolMode.Auto, "symbols not read")]
    [InlineData(SearchKinds.Method, SearchScope.Definitions, SymbolMode.Off, "symbols not read")]
    [InlineData(SearchKinds.Method, SearchScope.Definitions, SymbolMode.Auto, "1 with symbols")]
    [InlineData(SearchKinds.Type, SearchScope.References, SymbolMode.Auto, "1 with symbols")]
    public void SummarySaysWhenSymbolsWereNotRead(SearchKinds kinds, SearchScope scope, SymbolMode symbols, string expected)
    {
        var options = new SearchOptions("in.dll", "x", kinds, scope, MatchMode.Contains, true, true, symbols, 10, null, []);
        var result = new SearchResult([], 0, [], [], 1, 1, 1, []);
        using var writer = new StringWriter();

        TextReport.WriteSearch(writer, result, options);

        Assert.Contains($"Assemblies: 1/1 succeeded, {expected}, 0 errors", writer.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void AnsiStyleColorsEachHitLineDifferentlyAndResets()
    {
        var options = new SearchOptions(
            "in.dll", "Save", SearchKinds.Method, SearchScope.All, MatchMode.Exact,
            false, true, SymbolMode.Off, 10, null, []);
        var hit = new SearchHit(
            "in.dll", "In", HitScope.Reference, HitKind.Method, "T::Save() : System.Void",
            "T::Caller() : System.Void", new SourceLocation("a.cs", 12, 5), 0x1A);
        var result = new SearchResult([hit], 1, [new HitCount(HitScope.Reference, HitKind.Method, 1)], [], 1, 1, 0, []);
        using var writer = new StringWriter();

        TextReport.WriteSearch(writer, result, options, ReportStyle.Ansi);

        var lines = writer.ToString().Split(Environment.NewLine);
        Assert.Contains("\u001b[35m[reference/method]\u001b[0m \u001b[1mT::Save() : System.Void\u001b[0m", lines);
        Assert.Contains("\u001b[36m  assembly: in.dll\u001b[0m", lines);
        Assert.Contains("\u001b[33m  in: T::Caller() : System.Void\u001b[0m", lines);
        Assert.Contains("\u001b[94m  source: a.cs:12:5\u001b[0m", lines);
        Assert.Contains("\u001b[90m  il: IL_001A\u001b[0m", lines);
    }

    [Fact]
    public void MsBuildFormatIsNeverColored()
    {
        var options = new SearchOptions(
            "in.dll", "Save", SearchKinds.Method, SearchScope.All, MatchMode.Exact,
            true, true, SymbolMode.Auto, 10, null, [], ReportFormat.MsBuild);
        var hit = new SearchHit(
            "in.dll", "In", HitScope.Definition, HitKind.Method, "T::Save() : System.Void",
            null, new SourceLocation("a.cs", 30, 0), null);
        var result = new SearchResult([hit], 1, [new HitCount(HitScope.Definition, HitKind.Method, 1)], [], 1, 1, 1, []);
        using var writer = new StringWriter();

        TextReport.WriteSearch(writer, result, options, ReportStyle.Ansi);

        Assert.DoesNotContain('\u001b', writer.ToString());
    }

    [Fact]
    public void NoneStyleLeavesTextUntouched()
    {
        Assert.Equal("plain", ReportStyle.None.Apply(ReportPart.Assembly, "plain"));
        Assert.False(ReportStyle.None.IsEnabled);
        Assert.Equal("", ReportStyle.Ansi.Apply(ReportPart.Assembly, ""));
    }

    [Fact]
    public void EscapesSymbolsAndOmitsFooterWhenNothingWasDropped()
    {
        var options = new SearchOptions(
            "in.dll", "x", SearchKinds.All, SearchScope.Definitions, MatchMode.Contains,
            true, true, SymbolMode.Off, 10, null, []);
        var hit = new SearchHit("in.dll", "In", HitScope.Definition, HitKind.Type, "Bad\u202EName", null, null, null);
        var result = new SearchResult([hit], 1, [new HitCount(HitScope.Definition, HitKind.Type, 1)], [], 1, 1, 0, []);
        using var writer = new StringWriter();

        TextReport.WriteSearch(writer, result, options);

        var text = writer.ToString();
        Assert.Contains("[definition/type] Bad\\u202EName", text, StringComparison.Ordinal);
        Assert.DoesNotContain("省略", text, StringComparison.Ordinal);
    }
}
