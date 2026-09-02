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
    /// folder of the input that contains assemblies, then the framework locations found by
    /// <see cref="FrameworkProbe"/> (installed .NET runtimes and reference packs, .NET Framework
    /// directories), and finally the GAC on Windows. Cecil's implicit relative "." and "bin"
    /// entries are deliberately dropped so resolution never depends on the current directory.
    /// </summary>
    public static IdentityAwareAssemblyResolver Create(
        string targetFile,
        IReadOnlyList<string> referenceDirectories,
        IReadOnlyList<string> discoveredDirectories) =>
        Create(targetFile, referenceDirectories, discoveredDirectories, FrameworkProbe.Directories, FrameworkProbe.GacRoots);

    public static IdentityAwareAssemblyResolver Create(
        string targetFile,
        IReadOnlyList<string> referenceDirectories,
        IReadOnlyList<string> discoveredDirectories,
        IReadOnlyList<string> frameworkDirectories,
        IReadOnlyList<string> gacRoots)
    {
        var resolver = new IdentityAwareAssemblyResolver(frameworkDirectories, gacRoots);
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

        foreach (var directory in frameworkDirectories)
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
/// Two-tier identity policy. Directories the user controls (the target's folder, --reference-path,
/// folders of the input) must match the full AssemblyIdentity, so a stray wrong-version copy is
/// never mistaken for the dependency. Framework locations (installed runtimes and reference
/// packs, .NET Framework folders, the GAC, and the running process's trusted platform
/// assemblies) accept the same or a newer version of the same strong name, which is how the
/// runtime itself binds framework references. Identities that fail are remembered so a missing
/// dependency is probed once per resolver rather than once per referencing instruction.
/// </summary>
internal sealed class IdentityAwareAssemblyResolver : DefaultAssemblyResolver
{
    private static readonly bool TraceEnabled = Environment.GetEnvironmentVariable("CECIL_INSPECTOR_DEBUG") == "1";

    private readonly HashSet<string> _frameworkDirectories;
    private readonly IReadOnlyList<string> _gacRoots;
    private readonly HashSet<string> _unresolvable = new(StringComparer.Ordinal);

    public IdentityAwareAssemblyResolver()
        : this([], [])
    {
    }

    public IdentityAwareAssemblyResolver(IReadOnlyList<string> frameworkDirectories, IReadOnlyList<string> gacRoots)
    {
        _frameworkDirectories = new HashSet<string>(
            frameworkDirectories.Select(Path.TrimEndingDirectorySeparator),
            StringComparer.OrdinalIgnoreCase);
        _gacRoots = gacRoots;
    }

    /// <summary>Number of file probes performed; exposed for tests.</summary>
    internal int ProbeCount { get; private set; }

    public override AssemblyDefinition Resolve(AssemblyNameReference name, ReaderParameters parameters)
    {
        if (_unresolvable.Contains(name.FullName))
        {
            throw new AssemblyResolutionException(name);
        }

        var resolved = SearchDirectory(name, GetSearchDirectories(), parameters) ??
                       SearchTrustedPlatformAssemblies(name, parameters) ??
                       SearchGac(name, parameters);
        Trace(name, resolved);
        if (resolved is not null)
        {
            return resolved;
        }

        _unresolvable.Add(name.FullName);
        throw new AssemblyResolutionException(name);
    }

    /// <summary>
    /// The assemblies of the running runtime, which Cecil would otherwise use by simple name
    /// only. Inside a single-file bundle these paths do not exist on disk and are skipped.
    /// </summary>
    private AssemblyDefinition? SearchTrustedPlatformAssemblies(AssemblyNameReference name, ReaderParameters parameters)
    {
        if (AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES") is not string list)
        {
            return null;
        }

        foreach (var path in list.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            if (!string.Equals(Path.GetFileNameWithoutExtension(path), name.Name, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            ProbeCount++;
            var candidate = TryReadMatching(name, path, parameters, IsCompatibleFrameworkIdentity);
            if (candidate is not null)
            {
                return candidate;
            }
        }

        return null;
    }

    private AssemblyDefinition? SearchGac(AssemblyNameReference name, ReaderParameters parameters)
    {
        if (_gacRoots.Count == 0)
        {
            return null;
        }

        foreach (var candidatePath in FrameworkProbe.GacCandidatePaths(name, _gacRoots))
        {
            ProbeCount++;
            var candidate = TryReadMatching(name, candidatePath, parameters, IsCompatibleFrameworkIdentity);
            if (candidate is not null)
            {
                return candidate;
            }
        }

        return null;
    }

    private static void Trace(AssemblyNameReference name, AssemblyDefinition? resolved)
    {
        if (TraceEnabled)
        {
            Console.Error.WriteLine(resolved is null
                ? $"解決失敗: {name.FullName}"
                : $"解決: {name.FullName} -> {resolved.MainModule.FileName} ({resolved.Name.FullName})");
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
            Func<AssemblyNameReference, AssemblyNameReference, bool> policy =
                _frameworkDirectories.Contains(Path.TrimEndingDirectorySeparator(directory))
                    ? IsCompatibleFrameworkIdentity
                    : HasSameIdentity;
            foreach (var extension in extensions)
            {
                var candidatePath = Path.Combine(directory, name.Name + extension);
                ProbeCount++;
                var candidate = TryReadMatching(name, candidatePath, parameters, policy);
                if (candidate is not null)
                {
                    return candidate;
                }
            }
        }

        return null;
    }

    private AssemblyDefinition? TryReadMatching(
        AssemblyNameReference name,
        string candidatePath,
        ReaderParameters parameters,
        Func<AssemblyNameReference, AssemblyNameReference, bool> accepts)
    {
        if (!File.Exists(candidatePath))
        {
            return null;
        }

        AssemblyDefinition? candidate = null;
        try
        {
            parameters.AssemblyResolver ??= this;
            candidate = AssemblyDefinition.ReadAssembly(candidatePath, parameters);
            if (accepts(name, candidate.Name))
            {
                var resolved = candidate;
                candidate = null;
                return resolved;
            }
        }
        catch (Exception ex) when (ex is BadImageFormatException or IOException or UnauthorizedAccessException)
        {
            // Match Cecil's base resolver: an invalid or unreadable candidate does not prevent
            // probing the remaining extensions and search directories.
        }
        finally
        {
            candidate?.Dispose();
        }

        return null;
    }

    private static bool HasSameIdentity(AssemblyNameReference requested, AssemblyNameReference candidate) =>
        HasSameStrongNameParts(requested, candidate) && requested.Version == candidate.Version;

    /// <summary>
    /// Framework binding semantics: same name, culture and public key token, and a version that is
    /// the same or newer. A retargetable or zero-version request accepts any version.
    /// </summary>
    private static bool IsCompatibleFrameworkIdentity(AssemblyNameReference requested, AssemblyNameReference candidate) =>
        HasSameStrongNameParts(requested, candidate) &&
        (requested.IsRetargetable ||
         requested.Version is null ||
         requested.Version == new Version(0, 0, 0, 0) ||
         candidate.Version >= requested.Version);

    private static bool HasSameStrongNameParts(AssemblyNameReference requested, AssemblyNameReference candidate) =>
        string.Equals(requested.Name, candidate.Name, StringComparison.OrdinalIgnoreCase) &&
        string.Equals(NormalizeCulture(requested.Culture), NormalizeCulture(candidate.Culture),
            StringComparison.OrdinalIgnoreCase) &&
        requested.PublicKeyToken.AsSpan().SequenceEqual(candidate.PublicKeyToken);

    private static string NormalizeCulture(string? culture) =>
        string.IsNullOrEmpty(culture) || string.Equals(culture, "neutral", StringComparison.OrdinalIgnoreCase)
            ? string.Empty
            : culture;
}
