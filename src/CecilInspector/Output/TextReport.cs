using CecilInspector.Cli;
using CecilInspector.Core;

namespace CecilInspector.Output;

public static class TextReport
{
    public static void WriteSearch(TextWriter writer, SearchResult result, SearchOptions options)
    {
        writer = new GuardedTextWriter(writer);
        writer.WriteLine($"Query: {TextSanitizer.Escape(options.Query)}");
        writer.WriteLine($"Kinds: {options.Kinds} / Scope: {options.Scope} / Match: {options.MatchMode}" +
                         (options.IgnoreCase ? " (ignore case)" : " (case sensitive)"));
        writer.WriteLine($"Assemblies: {result.FilesSucceeded}/{result.FilesDiscovered} succeeded, " +
                         $"{result.FilesWithSymbols} with symbols, {result.Errors.Count} errors");
        writer.WriteLine($"Matches: {result.TotalMatches}");
        if (result.TotalMatches > 0)
        {
            var breakdown = result.Counts.Select(count =>
                $"{count.Scope.ToString().ToLowerInvariant()}/" +
                $"{count.Kind.ToString().ToLowerInvariant()}={count.Count}");
            writer.WriteLine($"Breakdown: {string.Join(", ", breakdown)}");
        }

        writer.WriteLine();

        foreach (var hit in result.Hits)
        {
            if (options.Format == ReportFormat.MsBuild)
            {
                WriteMsBuildHit(writer, hit);
            }
            else
            {
                WriteTextHit(writer, hit);
            }
        }

        if (result.TotalMatches > result.Hits.Count)
        {
            writer.WriteLine($"... {result.TotalMatches - result.Hits.Count}件を省略しました。--max-resultsで変更できます。");
        }
    }

    private static void WriteTextHit(TextWriter writer, SearchHit hit)
    {
        writer.WriteLine($"{Label(hit)} {TextSanitizer.Escape(hit.Symbol)}");
        writer.WriteLine($"  assembly: {TextSanitizer.Escape(hit.AssemblyPath)}");
        if (hit.Container is not null)
        {
            writer.WriteLine($"  in: {TextSanitizer.Escape(hit.Container)}");
        }

        if (hit.Location is not null)
        {
            writer.WriteLine($"  source: {TextSanitizer.Escape(hit.Location.ToString())}");
        }

        if (hit.IlOffset is not null)
        {
            writer.WriteLine($"  il: IL_{hit.IlOffset.Value:X4}");
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
