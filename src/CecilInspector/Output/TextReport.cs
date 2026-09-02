using CecilInspector.Cli;
using CecilInspector.Core;

namespace CecilInspector.Output;

public static class TextReport
{
    public static string FormatSearch(SearchResult result, SearchOptions options)
    {
        using var text = new StringWriter();
        WriteSearch(text, result, options);
        return text.ToString();
    }

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
            writer.Write($"[{hit.Scope.ToString().ToLowerInvariant()}/");
            writer.Write($"{hit.Kind.ToString().ToLowerInvariant()}] ");
            writer.WriteLine(TextSanitizer.Escape(hit.Symbol));
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

        if (result.TotalMatches > result.Hits.Count)
        {
            writer.WriteLine($"... {result.TotalMatches - result.Hits.Count}件を省略しました。--max-resultsで変更できます。");
        }
    }
}
