using System.Runtime.InteropServices;
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

if (parseResult.IsHelp)
{
    Console.WriteLine(CommandLine.HelpText);
    return 0;
}

if (!parseResult.IsSuccess)
{
    Console.Error.WriteLine($"エラー: {TextSanitizer.Escape(parseResult.Error)}");
    Console.Error.WriteLine();
    Console.Error.WriteLine(CommandLine.HelpText);
    return 1;
}

using var cancellation = new CancellationTokenSource();
using var interrupts = new InterruptHandler(cancellation);

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
                var result = new AssemblySearcher().Search(searchOptions, discovery, cancellation.Token);
                TextReport.WriteSearch(writer, result, searchOptions);
                (errors, warnings, filesSucceeded) = (result.Errors, result.Warnings, result.FilesSucceeded);
                break;
            }
        case DumpOptions dumpOptions:
            {
                var result = new MetadataDumper().Dump(dumpOptions, discovery, writer, cancellation.Token);
                (errors, warnings, filesSucceeded) = (result.Errors, result.Warnings, result.FilesSucceeded);
                break;
            }
        default:
            throw new System.Diagnostics.UnreachableException($"未対応のコマンドです: {options.GetType().Name}");
    }

    writer.Flush();
    reportFile?.Commit();
    WriteDiagnostics(warnings, "情報");
    WriteDiagnostics(errors, "警告");
    return ExitCode(filesSucceeded, errors.Count);
}
catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
{
    // The report file, if any, was disposed on the way out and its partial file deleted.
    Console.Error.WriteLine("中断しました。");
    return 130;
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

/// <summary>
/// Prints one line per diagnostic. Entries that made the result incomplete (and set the exit
/// code) are "警告", notices that did not are "情報", so automation can tell them apart on stderr.
/// </summary>
static void WriteDiagnostics(IEnumerable<ScanError> diagnostics, string prefix)
{
    var debug = Environment.GetEnvironmentVariable("CECIL_INSPECTOR_DEBUG") == "1";
    foreach (var diagnostic in diagnostics)
    {
        Console.Error.WriteLine(
            $"{prefix}: {TextSanitizer.Escape(diagnostic.FilePath)}: {TextSanitizer.Escape(diagnostic.Message)}");
        if (debug && diagnostic.Exception is not null)
        {
            // Escape line by line: escaping the whole trace and then restoring "\n" would also turn
            // a literal backslash-n inside a Windows path (C:\nuget\...) into a line break.
            foreach (var line in diagnostic.Exception.ToString().Split(["\r\n", "\n"], StringSplitOptions.None))
            {
                Console.Error.WriteLine($"    {TextSanitizer.Escape(line)}");
            }
        }
    }
}

/// <summary>
/// Turns Ctrl-C, SIGTERM, SIGHUP and SIGQUIT into cooperative cancellation so the analysis
/// unwinds through its using blocks and the partial report file is deleted instead of being
/// orphaned. The first signal cancels; a second one falls back to the default handling
/// (immediate termination) in case the cancellation is not observed quickly enough.
/// </summary>
internal sealed class InterruptHandler : IDisposable
{
    private readonly List<PosixSignalRegistration> _registrations = [];

    public InterruptHandler(CancellationTokenSource cancellation)
    {
        foreach (var signal in new[] { PosixSignal.SIGINT, PosixSignal.SIGTERM, PosixSignal.SIGHUP, PosixSignal.SIGQUIT })
        {
            try
            {
                _registrations.Add(PosixSignalRegistration.Create(signal, context =>
                {
                    context.Cancel = !cancellation.IsCancellationRequested;
                    try
                    {
                        cancellation.Cancel();
                    }
                    catch (ObjectDisposedException)
                    {
                        // The run already finished; let the process exit normally.
                    }
                }));
            }
            catch (PlatformNotSupportedException)
            {
                // Not every host delivers every signal; the ones it does are enough.
            }
        }
    }

    public void Dispose()
    {
        foreach (var registration in _registrations)
        {
            registration.Dispose();
        }
    }
}
