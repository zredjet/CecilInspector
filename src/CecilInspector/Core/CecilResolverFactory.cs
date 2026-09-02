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
    /// The resolver for framework locations (installed runtimes and reference packs, .NET
    /// Framework directories, the running process's trusted platform assemblies, the GAC).
    /// Its results do not depend on which file is being analyzed, so one instance serves a
    /// whole run and each framework assembly is opened once instead of once per input file.
    /// </summary>
    public static IdentityAwareAssemblyResolver CreateFrameworkResolver() =>
        CreateFrameworkResolver(FrameworkProbe.Directories, FrameworkProbe.GacRoots);

    public static IdentityAwareAssemblyResolver CreateFrameworkResolver(
        IReadOnlyList<string> frameworkDirectories,
        IReadOnlyList<string> gacRoots)
    {
        var resolver = new IdentityAwareAssemblyResolver(
            IdentityPolicy.Framework, gacRoots, fallback: null, ownsFallback: false, TraceRequested());
        AddDirectories(resolver, frameworkDirectories);
        return resolver;
    }

    /// <summary>
    /// The resolver for one analyzed file. Probe order: the target's own folder, then
    /// --reference-path in the given order, then every folder of the input that contains
    /// assemblies (all requiring the exact identity), and finally
    /// <paramref name="frameworkResolver"/>. Cecil's implicit relative "." and "bin" entries
    /// are deliberately dropped so resolution never depends on the current directory.
    /// </summary>
    public static IdentityAwareAssemblyResolver Create(
        string targetFile,
        IReadOnlyList<string> referenceDirectories,
        IReadOnlyList<string> discoveredDirectories,
        IdentityAwareAssemblyResolver frameworkResolver) =>
        Create(targetFile, referenceDirectories, discoveredDirectories, frameworkResolver, ownsFallback: false);

    /// <summary>Convenience for tests and single-file callers: owns a framework resolver built from the default probe.</summary>
    public static IdentityAwareAssemblyResolver Create(
        string targetFile,
        IReadOnlyList<string> referenceDirectories,
        IReadOnlyList<string> discoveredDirectories) =>
        Create(targetFile, referenceDirectories, discoveredDirectories, CreateFrameworkResolver(), ownsFallback: true);

    /// <summary>Convenience for tests: owns a framework resolver over the given locations.</summary>
    public static IdentityAwareAssemblyResolver Create(
        string targetFile,
        IReadOnlyList<string> referenceDirectories,
        IReadOnlyList<string> discoveredDirectories,
        IReadOnlyList<string> frameworkDirectories,
        IReadOnlyList<string> gacRoots) =>
        Create(
            targetFile,
            referenceDirectories,
            discoveredDirectories,
            CreateFrameworkResolver(frameworkDirectories, gacRoots),
            ownsFallback: true);

    private static IdentityAwareAssemblyResolver Create(
        string targetFile,
        IReadOnlyList<string> referenceDirectories,
        IReadOnlyList<string> discoveredDirectories,
        IdentityAwareAssemblyResolver frameworkResolver,
        bool ownsFallback)
    {
        var resolver = new IdentityAwareAssemblyResolver(
            IdentityPolicy.Exact, [], frameworkResolver, ownsFallback, TraceRequested());
        AddDirectories(
            resolver,
            [Path.GetDirectoryName(Path.GetFullPath(targetFile))!, .. referenceDirectories, .. discoveredDirectories]);
        return resolver;
    }

    private static void AddDirectories(IdentityAwareAssemblyResolver resolver, IEnumerable<string> directories)
    {
        foreach (var directory in resolver.GetSearchDirectories())
        {
            resolver.RemoveSearchDirectory(directory);
        }

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var directory in directories)
        {
            if (seen.Add(directory))
            {
                resolver.AddSearchDirectory(directory);
            }
        }
    }

    private static bool TraceRequested() => Environment.GetEnvironmentVariable("CECIL_INSPECTOR_DEBUG") == "1";
}

/// <summary>Which candidates a resolver accepts for a requested assembly identity.</summary>
internal enum IdentityPolicy
{
    /// <summary>The full AssemblyIdentity must match (name, version, culture, public key token).</summary>
    Exact,

    /// <summary>Framework binding: same strong-name parts and the same or a newer version.</summary>
    Framework,
}

/// <summary>
/// Two-tier identity resolution. A resolver over directories the user controls (the target's
/// folder, --reference-path, folders of the input) requires the full AssemblyIdentity, so a
/// stray wrong-version copy is never mistaken for the dependency, and falls back to a shared
/// resolver over framework locations, which accepts the same or a newer version of the same
/// strong name the way the runtime itself binds framework references. Each resolver caches
/// what it resolved and owns those assemblies; identities that fail are remembered so a
/// missing dependency is probed once rather than once per referencing instruction.
/// </summary>
internal sealed class IdentityAwareAssemblyResolver : DefaultAssemblyResolver
{
    private readonly IdentityPolicy _policy;
    private readonly IReadOnlyList<string> _gacRoots;
    private readonly IdentityAwareAssemblyResolver? _fallback;
    private readonly bool _ownsFallback;
    private readonly bool _trace;
    private readonly Dictionary<string, AssemblyDefinition> _cache = new(StringComparer.Ordinal);
    private readonly HashSet<string> _unresolvable = new(StringComparer.Ordinal);
    private int _probeCount;

    internal IdentityAwareAssemblyResolver(
        IdentityPolicy policy,
        IReadOnlyList<string> gacRoots,
        IdentityAwareAssemblyResolver? fallback,
        bool ownsFallback,
        bool trace)
    {
        _policy = policy;
        _gacRoots = gacRoots;
        _fallback = fallback;
        _ownsFallback = ownsFallback;
        _trace = trace;
    }

    /// <summary>Number of file probes performed, including the fallback's; exposed for tests.</summary>
    internal int ProbeCount => _probeCount + (_fallback?.ProbeCount ?? 0);

    /// <summary>This resolver's directories followed by the fallback's, in probe order.</summary>
    internal IReadOnlyList<string> AllSearchDirectories =>
        [.. GetSearchDirectories(), .. _fallback?.AllSearchDirectories ?? []];

    public override AssemblyDefinition Resolve(AssemblyNameReference name) =>
        Resolve(name, new ReaderParameters { AssemblyResolver = this });

    public override AssemblyDefinition Resolve(AssemblyNameReference name, ReaderParameters parameters)
    {
        if (_cache.TryGetValue(name.FullName, out var cached))
        {
            return cached;
        }

        if (_unresolvable.Contains(name.FullName))
        {
            throw new AssemblyResolutionException(name);
        }

        var resolved = IsSafeFileName(name.Name) ? SearchOwnLocations(name, parameters) : null;
        if (resolved is not null)
        {
            _cache.Add(name.FullName, resolved);
            Trace(name, resolved);
            return resolved;
        }

        if (_fallback is not null)
        {
            try
            {
                // Resolved through (and owned by) the fallback, which caches and traces it.
                return _fallback.Resolve(name);
            }
            catch (AssemblyResolutionException)
            {
                // Fall through and remember the failure here too.
            }
        }

        _unresolvable.Add(name.FullName);
        Trace(name, null);
        throw new AssemblyResolutionException(name);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            foreach (var assembly in _cache.Values)
            {
                assembly.Dispose();
            }

            _cache.Clear();
            if (_ownsFallback)
            {
                _fallback?.Dispose();
            }
        }

        base.Dispose(disposing);
    }

    private AssemblyDefinition? SearchOwnLocations(AssemblyNameReference name, ReaderParameters parameters)
    {
        var resolved = SearchDirectories(name, parameters);
        if (resolved is null && _policy == IdentityPolicy.Framework)
        {
            resolved = SearchTrustedPlatformAssemblies(name, parameters) ?? SearchGac(name, parameters);
        }

        return resolved;
    }

    private AssemblyDefinition? SearchDirectories(AssemblyNameReference name, ReaderParameters parameters)
    {
        var extensions = name.IsWindowsRuntime ? new[] { ".winmd", ".dll" } : new[] { ".dll", ".exe" };
        foreach (var directory in GetSearchDirectories())
        {
            foreach (var extension in extensions)
            {
                var candidate = TryReadMatching(name, Path.Combine(directory, name.Name + extension), parameters);
                if (candidate is not null)
                {
                    return candidate;
                }
            }
        }

        return null;
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

            var candidate = TryReadMatching(name, path, parameters);
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
            var candidate = TryReadMatching(name, candidatePath, parameters);
            if (candidate is not null)
            {
                return candidate;
            }
        }

        return null;
    }

    private void Trace(AssemblyNameReference name, AssemblyDefinition? resolved)
    {
        if (_trace)
        {
            Console.Error.WriteLine(resolved is null
                ? $"解決失敗: {name.FullName}"
                : $"解決: {name.FullName} -> {resolved.MainModule.FileName} ({resolved.Name.FullName})");
        }
    }

    private AssemblyDefinition? TryReadMatching(AssemblyNameReference name, string candidatePath, ReaderParameters parameters)
    {
        _probeCount++;
        if (!File.Exists(candidatePath))
        {
            return null;
        }

        AssemblyDefinition? candidate = null;
        try
        {
            parameters.AssemblyResolver ??= this;
            candidate = AssemblyDefinition.ReadAssembly(candidatePath, parameters);
            if (Accepts(name, candidate.Name))
            {
                var resolved = candidate;
                candidate = null;
                return resolved;
            }
        }
        catch (Exception ex) when (ExceptionPolicy.IsRecoverableAssemblyError(ex))
        {
            // Like Cecil's base resolver, an invalid or unreadable candidate does not prevent
            // probing the remaining extensions and locations. Cecil reports a truncated or
            // fuzzed candidate through the same runtime exceptions as any broken image.
        }
        finally
        {
            candidate?.Dispose();
        }

        return null;
    }

    private bool Accepts(AssemblyNameReference requested, AssemblyNameReference candidate) => _policy switch
    {
        IdentityPolicy.Exact => HasSameIdentity(requested, candidate),
        _ => IsCompatibleFrameworkIdentity(requested, candidate),
    };

    /// <summary>
    /// An assembly name comes from the referencing file's metadata; one that is not a plain
    /// file name would probe outside the search directories.
    /// </summary>
    private static bool IsSafeFileName(string? name) =>
        !string.IsNullOrEmpty(name) &&
        name != "." && name != ".." &&
        string.Equals(Path.GetFileName(name), name, StringComparison.Ordinal) &&
        name.IndexOfAny(Path.GetInvalidFileNameChars()) < 0;

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
