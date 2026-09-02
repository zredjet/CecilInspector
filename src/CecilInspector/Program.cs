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
    switch (parseResult.Options)
    {
        case SearchOptions options:
            {
                var result = new AssemblySearcher().Search(options);
                using var reportFile = OutputFile.OpenAtomic(options.OutputPath);
                var writer = reportFile is null ? Console.Out : new TeeTextWriter(Console.Out, reportFile.Writer);
                TextReport.WriteSearch(writer, result, options);
                writer.Flush();
                reportFile?.Commit();
                WriteDiagnostics(result.Errors);
                return ExitCode(result.FilesSucceeded, result.Errors.Count);
            }
        case DumpOptions options:
            {
                var discovery = AssemblyFiles.DiscoverDetailed(options.InputPath, options.Recursive);
                using var reportFile = OutputFile.OpenAtomic(options.OutputPath);
                var writer = reportFile is null ? Console.Out : new TeeTextWriter(Console.Out, reportFile.Writer);
                var result = new MetadataDumper().Dump(
                    options,
                    discovery.Files,
                    discovery.FileCount,
                    discovery.SearchDirectories,
                    discovery.Errors,
                    writer);
                writer.Flush();
                reportFile?.Commit();
                WriteDiagnostics(result.Errors);
                return ExitCode(result.FilesSucceeded, result.Errors.Count);
            }
        default:
            throw new InvalidOperationException("未対応のコマンドです。");
    }
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
