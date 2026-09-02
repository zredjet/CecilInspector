using Mono.Cecil;

namespace CecilInspector.Core;

internal static class CecilExtensions
{
    /// <summary>
    /// Drops the decoded body (instructions, variables, exception handlers) and the debug
    /// information Cecil caches on a method definition for the module's lifetime. Both are
    /// re-read from the image on the next access, so this is safe once a pass is done with the
    /// method; on top of the in-memory image copy, a scan of a large assembly would otherwise
    /// hold the entire IL graph at once.
    /// </summary>
    public static void ReleaseBody(this MethodDefinition method) => method.Body = null;
}
