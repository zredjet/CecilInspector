using Mono.Cecil;

namespace CecilInspector.Core;

internal static class CecilResolverFactory
{
    public static IReadOnlyList<string> ValidateReferencePaths(IEnumerable<string> referencePaths)
    {
        var directories = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var referencePath in referencePaths)
        {
            var fullPath = Path.GetFullPath(referencePath);
            if (!Directory.Exists(fullPath))
            {
                throw new ArgumentException($"依存アセンブリの検索フォルダが見つかりません: {referencePath}");
            }

            if (seen.Add(fullPath))
            {
                directories.Add(fullPath);
            }
        }

        return directories;
    }

    public static DefaultAssemblyResolver Create(
        string targetFile,
        IReadOnlyList<string> referenceDirectories,
        IReadOnlyList<string> discoveredDirectories)
    {
        var resolver = new IdentityAwareAssemblyResolver();
        var defaultDirectories = resolver.GetSearchDirectories().ToArray();
        foreach (var directory in defaultDirectories)
        {
            resolver.RemoveSearchDirectory(directory);
        }

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        Add(Path.GetDirectoryName(Path.GetFullPath(targetFile))!);
        foreach (var directory in referenceDirectories)
        {
            Add(directory);
        }

        foreach (var directory in discoveredDirectories)
        {
            Add(directory);
        }

        foreach (var directory in defaultDirectories)
        {
            Add(directory);
        }

        return resolver;

        void Add(string directory)
        {
            if (seen.Add(directory))
            {
                resolver.AddSearchDirectory(directory);
            }
        }
    }

    private sealed class IdentityAwareAssemblyResolver : DefaultAssemblyResolver
    {
        protected override AssemblyDefinition? SearchDirectory(
            AssemblyNameReference name,
            IEnumerable<string> directories,
            ReaderParameters parameters)
        {
            var extensions = name.IsWindowsRuntime ? new[] { ".winmd", ".dll" } : new[] { ".dll", ".exe" };
            foreach (var directory in directories)
            {
                foreach (var extension in extensions)
                {
                    var candidatePath = Path.Combine(directory, name.Name + extension);
                    if (!File.Exists(candidatePath))
                    {
                        continue;
                    }

                    AssemblyDefinition? candidate = null;
                    try
                    {
                        parameters.AssemblyResolver ??= this;
                        candidate = AssemblyDefinition.ReadAssembly(candidatePath, parameters);
                        if (HasSameIdentity(name, candidate.Name))
                        {
                            var resolved = candidate;
                            candidate = null;
                            return resolved;
                        }
                    }
                    catch (BadImageFormatException)
                    {
                        // Match Cecil's base resolver: an invalid candidate does not prevent
                        // probing the remaining extensions and search directories.
                    }
                    finally
                    {
                        candidate?.Dispose();
                    }
                }
            }

            return null;
        }

        private static bool HasSameIdentity(AssemblyNameReference requested, AssemblyNameReference candidate) =>
            string.Equals(requested.Name, candidate.Name, StringComparison.OrdinalIgnoreCase) &&
            requested.Version == candidate.Version &&
            string.Equals(NormalizeCulture(requested.Culture), NormalizeCulture(candidate.Culture),
                StringComparison.OrdinalIgnoreCase) &&
            requested.PublicKeyToken.AsSpan().SequenceEqual(candidate.PublicKeyToken);

        private static string NormalizeCulture(string? culture) =>
            string.IsNullOrEmpty(culture) || string.Equals(culture, "neutral", StringComparison.OrdinalIgnoreCase)
                ? string.Empty
                : culture;
    }
}
