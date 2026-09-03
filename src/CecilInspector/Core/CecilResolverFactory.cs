using Mono.Cecil;
using System.Collections.Concurrent;

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
            probeFrameworkLocations: true, gacRoots, fallback: null, ownsFallback: false, TraceRequested());
        AddDirectories(resolver, frameworkDirectories);
        return resolver;
    }

    /// <summary>
    /// The resolver for one analyzed file. Probe order: the target's own folder, then
    /// --reference-path in the given order, then every folder of the input that contains
    /// assemblies, and finally <paramref name="frameworkResolver"/>. Cecil's implicit relative
    /// "." and "bin" entries are deliberately dropped so resolution never depends on the
    /// current directory.
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
            probeFrameworkLocations: false, [], frameworkResolver, ownsFallback, TraceRequested());
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

/// <summary>
/// Two-tier resolution with runtime binding semantics. A resolver over directories the user
/// controls (the target's folder, --reference-path, folders of the input) falls back to a
/// shared resolver over framework locations. Both accept a candidate with the same name,
/// culture and public key token whose version is the same or newer, which is how the runtime
/// binds: app-local assemblies may be newer than the reference (binding redirects, transitive
/// NuGet upgrades) but never older. Every rejected candidate is remembered with its reason,
/// so the warning for an unresolved dependency says which file was found and why it was not
/// taken. Each resolver caches what it resolved and owns those assemblies; identities that
/// fail are remembered so a missing dependency is probed once rather than once per
/// referencing instruction.
/// </summary>
internal sealed class IdentityAwareAssemblyResolver : DefaultAssemblyResolver
{
    private readonly bool _probeFrameworkLocations;
    private readonly IReadOnlyList<string> _gacRoots;
    private readonly IdentityAwareAssemblyResolver? _fallback;
    private readonly bool _ownsFallback;
    private readonly bool _trace;
    // Concurrent so the shared framework resolver answers repeated names (type forwarders
    // resolve System.Runtime to System.Private.CoreLib on every lookup) without taking the
    // lock; only a miss probes the disk under it.
    private readonly ConcurrentDictionary<string, AssemblyDefinition> _cache = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, AssemblyDefinition> _borrowed = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, string> _unresolvable = new(StringComparer.Ordinal);
    private readonly Dictionary<string, List<string>> _rejections = new(StringComparer.Ordinal);
    private readonly object _gate = new();
    private int _probeCount;

    internal IdentityAwareAssemblyResolver(
        bool probeFrameworkLocations,
        IReadOnlyList<string> gacRoots,
        IdentityAwareAssemblyResolver? fallback,
        bool ownsFallback,
        bool trace)
    {
        _probeFrameworkLocations = probeFrameworkLocations;
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

    /// <summary>
    /// Serialized: the framework resolver is shared by the files scanned in parallel, and the
    /// per-file resolver is only ever used by one thread, so the lock is uncontended there.
    /// </summary>
    public override AssemblyDefinition Resolve(AssemblyNameReference name, ReaderParameters parameters)
    {
        if (TryAnswer(name, out var answer))
        {
            return answer;
        }

        lock (_gate)
        {
            return TryAnswer(name, out answer) ? answer : ResolveCore(name, parameters);
        }
    }

    private bool TryAnswer(AssemblyNameReference name, out AssemblyDefinition answer)
    {
        if (_cache.TryGetValue(name.FullName, out answer!) || _borrowed.TryGetValue(name.FullName, out answer!))
        {
            return true;
        }

        if (_unresolvable.TryGetValue(name.FullName, out var knownDetail))
        {
            throw Unresolved(name, knownDetail);
        }

        return false;
    }

    private AssemblyDefinition ResolveCore(AssemblyNameReference name, ReaderParameters parameters)
    {

        var resolved = IsSafeFileName(name.Name) ? SearchOwnLocations(name, parameters) : null;
        if (resolved is not null)
        {
            _cache[name.FullName] = resolved;
            Trace(name, resolved);
            return resolved;
        }

        string? fallbackDetail = null;
        if (_fallback is not null)
        {
            try
            {
                // Resolved through (and owned by) the fallback, which caches and traces it. Kept
                // here as a borrowed entry so the shared fallback's lock is taken once per name
                // per file, not once per referencing member.
                var borrowed = _fallback.Resolve(name);
                _borrowed[name.FullName] = borrowed;
                return borrowed;
            }
            catch (AssemblyResolutionException ex)
            {
                fallbackDetail = (ex.InnerException as AssemblyResolutionDetail)?.Message;
            }
        }

        var detail = DescribeFailure(name, fallbackDetail);
        _unresolvable[name.FullName] = detail;
        _rejections.Remove(name.FullName);
        Trace(name, null);
        throw Unresolved(name, detail);
    }

    /// <summary>
    /// Cecil's exception type is sealed, so the explanation travels as the inner exception;
    /// <see cref="AssemblyResolutionDetail.Describe"/> turns the pair back into one message.
    /// </summary>
    private static AssemblyResolutionException Unresolved(AssemblyNameReference name, string detail) =>
        new(name, new AssemblyResolutionDetail(detail));

    /// <summary>
    /// What was found for the name and why it was not taken: the rejected candidates of this
    /// resolver, then the fallback's, or the plain fact that no file of that name exists.
    /// </summary>
    private string DescribeFailure(AssemblyNameReference name, string? fallbackDetail)
    {
        var parts = new List<string>();
        if (_rejections.TryGetValue(name.FullName, out var rejections))
        {
            parts.AddRange(rejections.Distinct(StringComparer.Ordinal));
        }
        else if (_probeFrameworkLocations)
        {
            parts.Add($"フレームワークの既知フォルダ ({GetSearchDirectories().Length} 箇所)、実行中ランタイム、GACにもありません");
        }
        else if (GetSearchDirectories() is { Length: > 0 } directories)
        {
            parts.Add($"{name.Name}.dll が検索フォルダにありません ({string.Join(", ", directories)})");
        }

        if (fallbackDetail is not null)
        {
            parts.Add(fallbackDetail);
        }

        return string.Join("; ", parts);
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
        if (resolved is null && _probeFrameworkLocations)
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
            var mismatch = DescribeMismatch(name, candidate.Name);
            if (mismatch is null)
            {
                var resolved = candidate;
                candidate = null;
                return resolved;
            }

            Reject(name, $"{candidatePath} は {mismatch}");
        }
        catch (Exception ex) when (ExceptionPolicy.IsRecoverableAssemblyError(ex))
        {
            // Like Cecil's base resolver, an invalid or unreadable candidate does not prevent
            // probing the remaining extensions and locations. Cecil reports a truncated or
            // fuzzed candidate through the same runtime exceptions as any broken image.
            Reject(name, $"{candidatePath} は読み込めません ({ex.Message})");
        }
        finally
        {
            candidate?.Dispose();
        }

        return null;
    }

    private void Reject(AssemblyNameReference name, string reason)
    {
        if (!_rejections.TryGetValue(name.FullName, out var reasons))
        {
            reasons = [];
            _rejections.Add(name.FullName, reasons);
        }

        reasons.Add(reason);
    }

    /// <summary>Null when the candidate binds to the request; otherwise why it does not.</summary>
    private static string? DescribeMismatch(AssemblyNameReference requested, AssemblyNameReference candidate)
    {
        if (!string.Equals(requested.Name, candidate.Name, StringComparison.OrdinalIgnoreCase))
        {
            return $"名前が異なります (候補 '{candidate.Name}')";
        }

        if (!string.Equals(NormalizeCulture(requested.Culture), NormalizeCulture(candidate.Culture), StringComparison.OrdinalIgnoreCase))
        {
            return $"Culture が異なります (要求 '{DisplayCulture(requested.Culture)}', 候補 '{DisplayCulture(candidate.Culture)}')";
        }

        if (!requested.PublicKeyToken.AsSpan().SequenceEqual(candidate.PublicKeyToken))
        {
            return $"PublicKeyToken が異なります (要求 {DisplayToken(requested.PublicKeyToken)}, 候補 {DisplayToken(candidate.PublicKeyToken)})";
        }

        if (!IsCompatibleVersion(requested, candidate))
        {
            return $"Version={candidate.Version} で要求 {requested.Version} より古いです";
        }

        return null;
    }

    /// <summary>
    /// An assembly name comes from the referencing file's metadata; one that is not a plain
    /// file name would probe outside the search directories.
    /// </summary>
    private static bool IsSafeFileName(string? name) =>
        !string.IsNullOrEmpty(name) &&
        name != "." && name != ".." &&
        string.Equals(Path.GetFileName(name), name, StringComparison.Ordinal) &&
        name.IndexOfAny(Path.GetInvalidFileNameChars()) < 0;

    /// <summary>
    /// Runtime binding semantics for the version: the same or newer. A retargetable or
    /// zero-version request accepts any version.
    /// </summary>
    private static bool IsCompatibleVersion(AssemblyNameReference requested, AssemblyNameReference candidate) =>
        requested.IsRetargetable ||
        requested.Version is null ||
        requested.Version == new Version(0, 0, 0, 0) ||
        candidate.Version >= requested.Version;

    private static string DisplayCulture(string? culture) =>
        string.IsNullOrEmpty(culture) ? "neutral" : culture;

    private static string DisplayToken(byte[]? token) =>
        token is null || token.Length == 0 ? "null" : Convert.ToHexStringLower(token);

    private static string NormalizeCulture(string? culture) =>
        string.IsNullOrEmpty(culture) || string.Equals(culture, "neutral", StringComparison.OrdinalIgnoreCase)
            ? string.Empty
            : culture;
}

/// <summary>
/// What the resolver found instead of the requested assembly, carried as the inner exception
/// of Cecil's sealed <see cref="AssemblyResolutionException"/>, so the unresolved-dependency
/// warning can say "found 1.0.0.0, needed 2.0.0.0" rather than just "failed to resolve".
/// </summary>
internal sealed class AssemblyResolutionDetail : Exception
{
    public AssemblyResolutionDetail(string detail)
        : base(detail)
    {
    }

    /// <summary>The user-facing reason for a failure, using the detail when the resolver recorded one.</summary>
    public static string Describe(Exception exception) =>
        exception is AssemblyResolutionException { InnerException: AssemblyResolutionDetail detail } resolution
            ? $"アセンブリ '{resolution.AssemblyReference.FullName}' を解決できません。{detail.Message}"
            : exception.Message;
}
