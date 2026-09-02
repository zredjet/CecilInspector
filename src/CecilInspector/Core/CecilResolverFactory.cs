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

    /// <summary>
    /// Probe order: the target's own folder, then --reference-path in the given order, then every
    /// folder of the input that contains assemblies. Cecil's implicit relative "." and "bin"
    /// entries are deliberately dropped so resolution never depends on the current directory.
    /// (Cecil still falls back to the running runtime's framework folder on its own.)
    /// </summary>
    public static IdentityAwareAssemblyResolver Create(
        string targetFile,
        IReadOnlyList<string> referenceDirectories,
        IReadOnlyList<string> discoveredDirectories)
    {
        var resolver = new IdentityAwareAssemblyResolver();
        foreach (var directory in resolver.GetSearchDirectories())
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

        return resolver;

        void Add(string directory)
        {
            if (seen.Add(directory))
            {
                resolver.AddSearchDirectory(directory);
            }
        }
    }
}

/// <summary>
/// Cecil's default resolver accepts the first file whose simple name matches. This resolver
/// only accepts candidates whose full AssemblyIdentity (name, version, culture, public key token)
/// matches the request, and remembers identities that could not be resolved so a missing
/// dependency is probed once per resolver instead of once per referencing instruction.
/// </summary>
internal sealed class IdentityAwareAssemblyResolver : DefaultAssemblyResolver
{
    private readonly HashSet<string> _unresolvable = new(StringComparer.Ordinal);

    /// <summary>Number of directory probes performed; exposed for tests.</summary>
    internal int ProbeCount { get; private set; }

    public override AssemblyDefinition Resolve(AssemblyNameReference name, ReaderParameters parameters)
    {
        if (_unresolvable.Contains(name.FullName))
        {
            throw new AssemblyResolutionException(name);
        }

        try
        {
            return base.Resolve(name, parameters);
        }
        catch (AssemblyResolutionException)
        {
            _unresolvable.Add(name.FullName);
            throw;
        }
    }

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
                ProbeCount++;
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
