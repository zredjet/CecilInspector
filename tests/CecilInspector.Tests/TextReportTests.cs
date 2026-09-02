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
