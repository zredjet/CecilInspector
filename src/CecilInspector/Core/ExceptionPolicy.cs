namespace CecilInspector.Core;

using CecilInspector.Output;
using Mono.Cecil;
using Mono.Cecil.Cil;
using System.Diagnostics;

internal static class ExceptionPolicy
{
    private static readonly System.Reflection.Assembly CecilAssembly = typeof(ModuleDefinition).Assembly;
    private static readonly string CecilAssemblyName = CecilAssembly.GetName().Name ?? "Mono.Cecil";

    public static bool IsFatal(Exception exception) => exception is
        OutOfMemoryException or AccessViolationException or AppDomainUnloadedException;

    /// <summary>
    /// The exception to surface for a failed parallel scan. Parallel.For reports every failing
    /// file in one AggregateException, and the files fail for the same reason (a query timeout,
    /// a report write failure), so one representative is what the caller and the exit code
    /// need. A fatal failure wins, then any failure that is not a cancellation, so a worker's
    /// exception is never hidden behind the cancellations it caused on the other workers.
    /// </summary>
    public static Exception Unwrap(AggregateException exception)
    {
        var inner = exception.Flatten().InnerExceptions;
        return inner.FirstOrDefault(IsFatal)
            ?? inner.FirstOrDefault(candidate => candidate is not OperationCanceledException)
            ?? (inner.Count > 0 ? inner[0] : exception);
    }

    /// <summary>
    /// The message to show for a failure. Cecil raises BadImageFormatException with an empty
    /// message for a truncated image and keeps the reason in the inner exception, so the first
    /// non-empty message along the chain is used, and the type name when there is none.
    /// </summary>
    public static string UserMessage(Exception exception)
    {
        for (var current = exception; current is not null; current = current.InnerException)
        {
            if (!string.IsNullOrWhiteSpace(current.Message))
            {
                return current.Message;
            }
        }

        return exception.GetType().Name;
    }

    /// <summary>
    /// True for failures that should be recorded as a warning for the current file and let the
    /// scan continue. Query and report-writing failures are never per-file problems.
    /// </summary>
    public static bool IsRecoverableAssemblyError(Exception exception)
    {
        if (exception is SearchQueryException or ReportWriteException or OperationCanceledException)
        {
            return false;
        }

        if (exception is
            BadImageFormatException or IOException or UnauthorizedAccessException or
            SymbolsNotFoundException or SymbolsNotMatchingException or InvalidOperationException or
            NotSupportedException or ArgumentException)
        {
            return true;
        }

        // Cecil reports truncated or fuzzed images through plain runtime exceptions from its
        // metadata readers. Treat those as a broken input only when they passed through Cecil,
        // so a genuine bug in this tool still surfaces as a crash.
        return exception is
                   NullReferenceException or IndexOutOfRangeException or
                   KeyNotFoundException or OverflowException &&
               PassedThroughCecil(exception);
    }

    /// <summary>
    /// The throwing method (Source / TargetSite) is checked first: it is cheap and survives the
    /// ReadyToRun and single-file builds, where inlining can thin out the captured frames that
    /// the full stack walk relies on.
    /// </summary>
    private static bool PassedThroughCecil(Exception exception) =>
        string.Equals(exception.Source, CecilAssemblyName, StringComparison.Ordinal) ||
        exception.TargetSite?.DeclaringType?.Assembly == CecilAssembly ||
        new StackTrace(exception).GetFrames().Any(frame =>
            frame.GetMethod()?.DeclaringType?.Assembly == CecilAssembly);
}

/// <summary>
/// CECIL_INSPECTOR_DEBUG=1 prints the exception behind every diagnostic and traces assembly
/// resolution; it also overrides --quiet, because the --quiet summary points at it for details.
/// Read on every use so in-process tests observe the current environment.
/// </summary>
internal static class DebugSwitch
{
    public static bool IsEnabled => Environment.GetEnvironmentVariable("CECIL_INSPECTOR_DEBUG") == "1";
}

public sealed class SearchQueryException : ArgumentException
{
    public SearchQueryException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
