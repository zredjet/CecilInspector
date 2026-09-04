using Mono.Cecil;
using Xunit;

namespace CecilInspector.Tests;

/// <summary>
/// A unique directory under the system temp folder that is removed on dispose, so a test that
/// fails during setup cannot leak it. Declare it with <c>using var</c> before creating any file.
/// </summary>
internal sealed class TempDirectory : IDisposable
{
    private static readonly string TestAssembly = typeof(TempDirectory).Assembly.Location;

    public TempDirectory()
    {
        Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"cecil-inspector-{Guid.NewGuid():N}");
        Directory.CreateDirectory(Path);
    }

    public string Path { get; }

    /// <summary>Returns a path inside the directory without creating anything.</summary>
    public string File(string name) => System.IO.Path.Combine(Path, name);

    /// <summary>Copies the test assembly itself into the directory under the given name.</summary>
    public string CopyAssembly(string name)
    {
        var path = File(name);
        System.IO.File.Copy(TestAssembly, path);
        return path;
    }

    /// <summary>Writes a file that is not a PE image at all, for the broken-input scenarios.</summary>
    public string WriteBrokenAssembly(string name)
    {
        var path = File(name);
        System.IO.File.WriteAllText(path, "not an assembly");
        return path;
    }

    /// <summary>Creates and returns a subdirectory.</summary>
    public string CreateSubdirectory(string name)
    {
        var path = File(name);
        Directory.CreateDirectory(path);
        return path;
    }

    public void Dispose()
    {
        for (var attempt = 0; ; attempt++)
        {
            try
            {
                if (Directory.Exists(Path))
                {
                    Directory.Delete(Path, true);
                }

                return;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                if (attempt >= 5)
                {
                    // A leaked directory is worth a note, not a failure that would replace the
                    // assertion message of the test that is already unwinding.
                    Console.Error.WriteLine($"TempDirectory: {Path} を削除できません: {ex.Message}");
                    return;
                }

                // Mono.Cecil does not dispose the PDB stream when a symbol read fails, so on
                // Windows the file stays locked until the finalizer runs. Finalize and retry.
                GC.Collect();
                GC.WaitForPendingFinalizers();
                Thread.Sleep(50 * (attempt + 1));
            }
        }
    }
}

internal static class SymbolicLinks
{
    /// <summary>
    /// Skips the current test when the account cannot create symbolic links (Windows without
    /// Developer Mode or SeCreateSymbolicLinkPrivilege), instead of failing it.
    /// </summary>
    public static void SkipUnlessSupported(TempDirectory temp)
    {
        var probe = temp.File($"symlink-probe-{Guid.NewGuid():N}");
        try
        {
            File.CreateSymbolicLink(probe, temp.Path);
            File.Delete(probe);
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException)
        {
            Assert.Skip($"シンボリックリンクを作成できない: {ex.Message}");
        }
    }
}

internal static class GeneratedAssemblies
{
    /// <summary>Writes an assembly containing one empty public class in the given namespace.</summary>
    public static void WriteTypeInNamespace(string path, string @namespace, string typeName = "Fixture")
    {
        using var assembly = AssemblyDefinition.CreateAssembly(
            new AssemblyNameDefinition(System.IO.Path.GetFileNameWithoutExtension(path), new Version(1, 0)),
            System.IO.Path.GetFileNameWithoutExtension(path),
            ModuleKind.Dll);
        var module = assembly.MainModule;
        module.Types.Add(new TypeDefinition(
            @namespace, typeName, TypeAttributes.Public | TypeAttributes.Class, module.TypeSystem.Object));
        assembly.Write(path);
    }
}
