using CecilInspector.Cli;
using CecilInspector.Core;
using CecilInspector.Output;

var parseResult = CommandLine.Parse(args);
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
    int filesSucceeded;
    switch (options)
    {
        case SearchOptions searchOptions:
            {
                var result = new AssemblySearcher().Search(searchOptions, discovery);
                TextReport.WriteSearch(writer, result, searchOptions);
                errors = result.Errors;
                filesSucceeded = result.FilesSucceeded;
                break;
            }
        case DumpOptions dumpOptions:
            {
                var result = new MetadataDumper().Dump(dumpOptions, discovery, writer);
                errors = result.Errors;
                filesSucceeded = result.FilesSucceeded;
                break;
            }
        default:
            throw new InvalidOperationException("未対応のコマンドです。");
    }

    writer.Flush();
    reportFile?.Commit();
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
    foreach (var error in errors)
    {
        Console.Error.WriteLine($"警告: {TextSanitizer.Escape(error.FilePath)}: {TextSanitizer.Escape(error.Message)}");
    }
}
