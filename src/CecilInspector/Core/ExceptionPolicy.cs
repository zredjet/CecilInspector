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

public sealed class SearchQueryException : ArgumentException
{
    public SearchQueryException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
