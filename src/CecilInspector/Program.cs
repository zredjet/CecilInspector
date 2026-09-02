using System.Text;
using CecilInspector.Cli;
using CecilInspector.Core;
using CecilInspector.Output;

try
{
    // Reports contain arbitrary identifiers and Japanese diagnostics; a legacy console code page
    // would turn them into '?' and make the console differ from the UTF-8 --output file.
    Console.OutputEncoding = new UTF8Encoding(false);
}
catch (Exception ex) when (ex is IOException or System.Security.SecurityException)
{
    // Headless hosts may not allow changing the console encoding; keep the default.
}

var parseResult = CommandLine.Parse(args);
if (parseResult.ShowVersion)
{
    Console.WriteLine($"cecil-inspector {CommandLine.VersionText}");
    return 0;
}

if (!parseResult.IsSuccess)
{
    if (!string.IsNullOrWhiteSpace(parseResult.Error))
    {
        Console.Error.WriteLine($"エラー: {TextSanitizer.Escape(parseResult.Error)}");
        Console.Error.WriteLine();
        Console.Error.WriteLine(CommandLine.HelpText);
        return 1;
    }

    Console.WriteLine(CommandLine.HelpText);
    return 0;
}

try
{
    var options = parseResult.Options!;

    // Preflight: everything that can reject the invocation runs before the first side effect
    // (creating the report file) and before any long-running analysis, so a bad --output or
    // --reference-path fails immediately instead of after a full scan.
    var discovery = AssemblyFiles.DiscoverDetailed(options.InputPath, options.Recursive);
    CecilResolverFactory.ValidateReferencePaths(options.ReferencePaths);
    using var reportFile = OutputFile.OpenAtomic(options.OutputPath);
    var writer = reportFile is null ? Console.Out : new TeeTextWriter(Console.Out, reportFile.Writer);

    IReadOnlyList<ScanError> errors;
    IReadOnlyList<ScanError> warnings;
    int filesSucceeded;
    switch (options)
    {
        case SearchOptions searchOptions:
            {
                var result = new AssemblySearcher().Search(searchOptions, discovery);
                TextReport.WriteSearch(writer, result, searchOptions);
                (errors, warnings, filesSucceeded) = (result.Errors, result.Warnings, result.FilesSucceeded);
                break;
            }
        case DumpOptions dumpOptions:
            {
                var result = new MetadataDumper().Dump(dumpOptions, discovery, writer);
                (errors, warnings, filesSucceeded) = (result.Errors, result.Warnings, result.FilesSucceeded);
                break;
            }
        default:
            throw new InvalidOperationException("未対応のコマンドです。");
    }

    writer.Flush();
    reportFile?.Commit();
    WriteDiagnostics(warnings);
    WriteDiagnostics(errors);
    return ExitCode(filesSucceeded, errors.Count);
}
catch (SearchQueryException ex)
{
    Console.Error.WriteLine($"エラー: {TextSanitizer.Escape(ex.Message)}");
    return 1;
}
catch (Exception ex) when (ex is ArgumentException or IOException or UnauthorizedAccessException)
{
    Console.Error.WriteLine($"エラー: {TextSanitizer.Escape(ex.Message)}");
    return 2;
}

static int ExitCode(int filesSucceeded, int errorCount) => errorCount switch
{
    0 => 0,
    _ when filesSucceeded == 0 => 2,
    _ => 3,
};

static void WriteDiagnostics(IEnumerable<ScanError> errors)
{
    var debug = Environment.GetEnvironmentVariable("CECIL_INSPECTOR_DEBUG") == "1";
    foreach (var error in errors)
    {
        Console.Error.WriteLine($"警告: {TextSanitizer.Escape(error.FilePath)}: {TextSanitizer.Escape(error.Message)}");
        if (debug && error.Exception is not null)
        {
            Console.Error.WriteLine(TextSanitizer.Escape(error.Exception.ToString()).Replace("\\n", Environment.NewLine, StringComparison.Ordinal));
        }
    }
}
