namespace CecilInspector.Core;

using CecilInspector.Output;
using Mono.Cecil.Cil;

internal static class ExceptionPolicy
{
    public static bool IsFatal(Exception exception) => exception is
        OutOfMemoryException or StackOverflowException or AccessViolationException or AppDomainUnloadedException;

    public static bool IsRecoverableAssemblyError(Exception exception)
    {
        if (exception is SearchQueryException or ReportWriteException)
        {
            return false;
        }

        return exception is
            BadImageFormatException or IOException or UnauthorizedAccessException or
            SymbolsNotFoundException or SymbolsNotMatchingException or InvalidOperationException or
            NotSupportedException or ArgumentException;
    }
}

public sealed class SearchQueryException : ArgumentException
{
    public SearchQueryException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
