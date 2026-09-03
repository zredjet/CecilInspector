using CecilInspector.Cli;
using CecilInspector.Core;
using System.Globalization;

namespace CecilInspector.Output;

public static class TextReport
{
    public static void WriteSearch(TextWriter writer, SearchResult result, SearchOptions options) =>
        WriteSearch(writer, result, options, ReportStyle.None);

    internal static void WriteSearch(TextWriter writer, SearchResult result, SearchOptions options, ReportStyle style)
    {
        writer = new GuardedTextWriter(writer);
        var msBuild = options.Format == ReportFormat.MsBuild;
        if (msBuild)
        {
            // Machine-readable: problem matchers must see the bare path(line,col): prefix.
            style = ReportStyle.None;
        }
        else
        {
            // In msbuild format the query is the one header value under the user's control, and a
            // query shaped like "x(1,1): error X: y" would be picked up by problem matchers.
            writer.WriteLine(
                style.Apply(ReportPart.Header, "Query: ") +
                style.Apply(ReportPart.Symbol, TextSanitizer.Escape(options.Query)));
        }

        writer.WriteLine(style.Apply(
            ReportPart.Header,
            $"Kinds: {options.Kinds} / Scope: {options.Scope} / Match: {options.MatchMode}" +
            (options.IgnoreCase ? " (ignore case)" : " (case sensitive)")));
        var symbols = AssemblySearcher.EffectiveSymbolMode(options) == SymbolMode.Off
            ? "symbols not read"
            : $"{result.FilesWithSymbols} with symbols";
        writer.WriteLine(style.Apply(
            ReportPart.Header,
            $"Assemblies: {result.FilesSucceeded}/{result.FilesDiscovered} succeeded, {symbols}, {result.Errors.Count} errors"));
        writer.WriteLine(
            "Matches: " + style.Apply(ReportPart.Symbol, result.TotalMatches.ToString(CultureInfo.InvariantCulture)));
        if (result.TotalMatches > 0)
        {
            var breakdown = result.Counts.Select(count =>
                $"{count.Scope.ToString().ToLowerInvariant()}/" +
                $"{count.Kind.ToString().ToLowerInvariant()}={count.Count}");
            writer.WriteLine(style.Apply(ReportPart.Header, $"Breakdown: {string.Join(", ", breakdown)}"));
        }

        writer.WriteLine();

        foreach (var hit in result.Hits)
        {
            if (msBuild)
            {
                WriteMsBuildHit(writer, hit);
            }
            else
            {
                WriteTextHit(writer, hit, style);
            }
        }

        if (result.TotalMatches > result.Hits.Count)
        {
            writer.WriteLine(style.Apply(
                ReportPart.Note,
                $"... {result.TotalMatches - result.Hits.Count}件を省略しました。--max-resultsで変更できます。"));
        }
    }

    /// <summary>
    /// Every line of a hit gets its own color so the eye can separate the symbol from where it
    /// lives (assembly), who references it (in), where in source (source) and where in IL (il).
    /// </summary>
    private static void WriteTextHit(TextWriter writer, SearchHit hit, ReportStyle style)
    {
        var labelPart = hit.Scope == HitScope.Definition ? ReportPart.DefinitionLabel : ReportPart.ReferenceLabel;
        writer.WriteLine(
            $"{style.Apply(labelPart, Label(hit))} {style.Apply(ReportPart.Symbol, TextSanitizer.Escape(hit.Symbol))}");
        writer.WriteLine(style.Apply(ReportPart.Assembly, $"  assembly: {TextSanitizer.Escape(hit.AssemblyPath)}"));
        if (hit.Container is not null)
        {
            writer.WriteLine(style.Apply(ReportPart.Container, $"  in: {TextSanitizer.Escape(hit.Container)}"));
        }

        if (hit.Location is not null)
        {
            writer.WriteLine(style.Apply(ReportPart.Source, $"  source: {TextSanitizer.Escape(hit.Location.ToString())}"));
        }

        if (hit.IlOffset is not null)
        {
            writer.WriteLine(style.Apply(ReportPart.Il, $"  il: IL_{hit.IlOffset.Value:X4}"));
        }
    }

    /// <summary>
    /// One line per hit. With a source location the line starts with the MSBuild canonical
    /// origin so Visual Studio's Output window and VS Code's $msCompile matcher can open it; the
    /// severity is "info" so an MSBuild &lt;Exec&gt; never turns hits into real warnings. Hits
    /// without a location deliberately omit the origin prefix (there is nothing to open).
    /// </summary>
    private static void WriteMsBuildHit(TextWriter writer, SearchHit hit)
    {
        var message = $"{Label(hit)} {TextSanitizer.Escape(hit.Symbol)}";
        if (hit.Container is not null)
        {
            message += $" (in {TextSanitizer.Escape(hit.Container)})";
        }

        if (hit.IlOffset is not null)
        {
            message += $" @ IL_{hit.IlOffset.Value:X4}";
        }

        if (hit.Location is null)
        {
            writer.WriteLine($"{message}  assembly: {TextSanitizer.Escape(hit.AssemblyPath)}");
            return;
        }

        var code = hit.Scope == HitScope.Definition ? "CI0001" : "CI0002";
        writer.WriteLine($"{TextSanitizer.Escape(hit.Location.ToMsBuildString())}: info {code}: {message}");
    }

    private static string Label(SearchHit hit) =>
        $"[{hit.Scope.ToString().ToLowerInvariant()}/{hit.Kind.ToString().ToLowerInvariant()}]";
}
