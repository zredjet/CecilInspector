using CecilInspector.Cli;
using CecilInspector.Core;
using System.Globalization;
using System.Text;

namespace CecilInspector.Output;

public static class TextReport
{
    public static void WriteSearch(TextWriter writer, SearchResult result, SearchOptions options) =>
        WriteSearch(writer, result, options, ReportStyle.None);

    internal static void WriteSearch(
        TextWriter writer, SearchResult result, SearchOptions options, ReportStyle style,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        writer = new GuardedTextWriter(writer);
        var format = options.Format;
        if (format != ReportFormat.Text)
        {
            // Only the human-readable report is styled: problem matchers must see the bare
            // path(line,col): prefix, and a csv cell must not carry escape sequences.
            style = ReportStyle.None;
        }

        var row = new StringBuilder();
        switch (format)
        {
            case ReportFormat.Csv:
                // Only the table goes to stdout (and the --output file); the caller prints the
                // summary lines elsewhere (Program: stderr, after the report file is committed).
                writer.Write(Csv.ByteOrderMark);
                writer.WriteLine(Csv.Header);
                break;
            default:
                // In msbuild format the query is the one header value under the user's control,
                // and a query shaped like "x(1,1): error X: y" would be picked up by problem
                // matchers.
                WriteHeader(writer, result, options, style, includeQuery: format != ReportFormat.MsBuild);
                writer.WriteLine();
                break;
        }

        foreach (var hit in result.Hits)
        {
            // A large report can take longer to write than to compute; an interrupt here must
            // still discard the partial --output file and exit with 130.
            cancellationToken.ThrowIfCancellationRequested();
            switch (format)
            {
                case ReportFormat.Csv:
                    WriteCsvHit(writer, row, hit);
                    break;
                case ReportFormat.MsBuild:
                    WriteMsBuildHit(writer, hit);
                    break;
                default:
                    WriteTextHit(writer, hit, style);
                    break;
            }
        }

        if (format != ReportFormat.Csv)
        {
            WriteTruncationNote(writer, result, style);
        }
    }

    /// <summary>
    /// The header lines and truncation note of the text report, unstyled. The csv format keeps
    /// them off stdout; the caller prints them here (on stderr) once the report exists, so the
    /// total count and the "N件を省略" notice are still visible, --quiet or not.
    /// </summary>
    public static void WriteSummary(TextWriter writer, SearchResult result, SearchOptions options)
    {
        WriteHeader(writer, result, options, ReportStyle.None, includeQuery: true);
        WriteTruncationNote(writer, result, ReportStyle.None);
    }

    private static void WriteHeader(
        TextWriter writer, SearchResult result, SearchOptions options, ReportStyle style, bool includeQuery)
    {
        if (includeQuery)
        {
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
                $"{ScopeName(count.Scope)}/{KindName(count.Kind)}={count.Count}");
            writer.WriteLine(style.Apply(ReportPart.Header, $"Breakdown: {string.Join(", ", breakdown)}"));
        }
    }

    private static void WriteTruncationNote(TextWriter writer, SearchResult result, ReportStyle style)
    {
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

    /// <summary>
    /// One row per hit in the column order of <see cref="Csv.Header"/>. Unknown values are empty
    /// cells; the IL offset keeps the IL_XXXX spelling of the other formats.
    /// </summary>
    private static void WriteCsvHit(TextWriter writer, StringBuilder row, SearchHit hit)
    {
        Csv.WriteRow(
            writer,
            row,
            ScopeName(hit.Scope),
            KindName(hit.Kind),
            hit.Symbol,
            hit.Container,
            hit.AssemblyName,
            hit.AssemblyPath,
            hit.Location?.Document,
            hit.Location?.Line.ToString(CultureInfo.InvariantCulture),
            hit.Location is { HasColumn: true } location ? location.Column.ToString(CultureInfo.InvariantCulture) : null,
            hit.IlOffset is { } ilOffset ? $"IL_{ilOffset:X4}" : null);
    }

    private static string Label(SearchHit hit) => $"[{ScopeName(hit.Scope)}/{KindName(hit.Kind)}]";

    /// <summary>The one spelling shared by the text label, the Breakdown line and the csv cells.</summary>
    private static string ScopeName(HitScope scope) => scope.ToString().ToLowerInvariant();

    private static string KindName(HitKind kind) => kind.ToString().ToLowerInvariant();
}
