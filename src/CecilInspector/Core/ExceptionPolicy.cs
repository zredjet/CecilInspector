namespace CecilInspector.Core;

using System.Diagnostics;
using CecilInspector.Output;
using Mono.Cecil;
using Mono.Cecil.Cil;

internal static class ExceptionPolicy
{
    private static readonly System.Reflection.Assembly CecilAssembly = typeof(ModuleDefinition).Assembly;

    public static bool IsFatal(Exception exception) => exception is
        OutOfMemoryException or StackOverflowException or AccessViolationException or AppDomainUnloadedException;

    /// <summary>
    /// True for failures that should be recorded as a warning for the current file and let the
    /// scan continue. Query and report-writing failures are never per-file problems.
    /// </summary>
    public static bool IsRecoverableAssemblyError(Exception exception)
    {
        if (exception is SearchQueryException or ReportWriteException)
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

    private static bool PassedThroughCecil(Exception exception) =>
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
