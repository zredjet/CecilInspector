using Mono.Cecil;

namespace CecilInspector.Core;

/// <summary>
/// Runs one analysis over the secondary netmodules of a multi-module assembly. Cecil opens each
/// netmodule lazily as its own <see cref="ModuleDefinition"/> with its own image stream, and
/// disposing the manifest module does not close them, so every module is disposed here as soon
/// as its callback returns. A netmodule that cannot be loaded or analyzed is reported for that
/// module only; whatever the caller collected from the manifest module is unaffected.
/// </summary>
internal static class SecondaryModules
{
    public static void ForEach(
        ModuleDefinition manifestModule,
        string file,
        Action<ModuleDefinition, string> analyze,
        Action<ScanError> report,
        CancellationToken cancellationToken)
    {
        if (manifestModule.Assembly is null)
        {
            return;
        }

        ModuleDefinition[] modules;
        try
        {
            modules = manifestModule.Assembly.Modules
                .Where(candidate => !ReferenceEquals(candidate, manifestModule))
                .ToArray();
        }
        catch (Exception ex) when (ExceptionPolicy.IsRecoverableAssemblyError(ex))
        {
            report(new ScanError(file, $"secondary netmoduleを読み込めません: {ExceptionPolicy.UserMessage(ex)}", ex));
            return;
        }

        foreach (var module in modules)
        {
            var moduleFile = module.FileName ?? file;
            try
            {
                cancellationToken.ThrowIfCancellationRequested();
                analyze(module, moduleFile);
            }
            catch (Exception ex) when (ExceptionPolicy.IsRecoverableAssemblyError(ex))
            {
                report(new ScanError(moduleFile, ExceptionPolicy.UserMessage(ex), ex));
            }
            finally
            {
                module.Dispose();
            }
        }
    }
}
