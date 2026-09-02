using System.Runtime.InteropServices;
using Mono.Cecil;

namespace CecilInspector.Core;

/// <summary>
/// Locates framework assemblies that are not next to the analyzed file: the running runtime,
/// every installed .NET shared runtime and reference pack, and on Windows the .NET Framework
/// directories and the GAC. Needed most by the self-contained single-file build, whose own
/// runtime lives inside the bundle where Mono.Cecil cannot see it.
/// </summary>
internal static class FrameworkProbe
{
    private static readonly Lazy<IReadOnlyList<string>> DefaultDirectories = new(ComputeDefaultDirectories);
    private static readonly Lazy<IReadOnlyList<string>> DefaultGacRoots = new(ComputeDefaultGacRoots);

    public static IReadOnlyList<string> Directories => DefaultDirectories.Value;

    public static IReadOnlyList<string> GacRoots => DefaultGacRoots.Value;

    /// <summary>shared/Microsoft.NETCore.App/&lt;version&gt; and packs/Microsoft.NETCore.App.Ref/&lt;version&gt;/ref/&lt;tfm&gt; under a dotnet root, newest first.</summary>
    public static IEnumerable<string> DotnetRootDirectories(string dotnetRoot)
    {
        foreach (var runtime in VersionDirectories(Path.Combine(dotnetRoot, "shared", "Microsoft.NETCore.App")))
        {
            yield return runtime;
        }

        foreach (var pack in VersionDirectories(Path.Combine(dotnetRoot, "packs", "Microsoft.NETCore.App.Ref")))
        {
            var reference = Path.Combine(pack, "ref");
            if (!Directory.Exists(reference))
            {
                continue;
            }

            // Target framework folders sort by version, not by name: "net10.0" must precede "net9.0".
            foreach (var tfm in SafeDirectories(reference)
                         .OrderByDescending(path => ParseVersion(Path.GetFileName(path).TrimStart("net".ToCharArray())))
                         .ThenByDescending(path => path, StringComparer.OrdinalIgnoreCase))
            {
                yield return tfm;
            }
        }
    }

    /// <summary>.NET Framework runtime and reference-assembly directories under the given Windows roots.</summary>
    public static IEnumerable<string> WindowsFrameworkDirectories(string? windowsDirectory, string? programFilesX86)
    {
        if (!string.IsNullOrEmpty(windowsDirectory))
        {
            foreach (var flavor in new[] { "Framework64", "Framework" })
            {
                var root = Path.Combine(windowsDirectory, "Microsoft.NET", flavor);
                foreach (var version in VersionDirectories(root, "v4"))
                {
                    yield return version;
                }
            }
        }

        if (!string.IsNullOrEmpty(programFilesX86))
        {
            var root = Path.Combine(programFilesX86, "Reference Assemblies", "Microsoft", "Framework", ".NETFramework");
            foreach (var version in VersionDirectories(root, "v4"))
            {
                yield return version;
                var facades = Path.Combine(version, "Facades");
                if (Directory.Exists(facades))
                {
                    yield return facades;
                }
            }
        }
    }

    /// <summary>
    /// Candidate file paths for an assembly in the .NET Framework 4 GAC
    /// (&lt;root&gt;\GAC_MSIL\Name\v4.0_1.0.0.0__token\Name.dll) and the legacy 2.0 GAC layout.
    /// </summary>
    public static IEnumerable<string> GacCandidatePaths(AssemblyNameReference name, IEnumerable<string> gacRoots)
    {
        var token = Convert.ToHexStringLower(name.PublicKeyToken ?? []);
        var version = name.Version?.ToString() ?? "0.0.0.0";
        var culture = string.IsNullOrEmpty(name.Culture) ? string.Empty : name.Culture;
        foreach (var root in gacRoots)
        {
            foreach (var architecture in new[] { "GAC_MSIL", "GAC_64", "GAC_32", "GAC" })
            {
                var directory = Path.Combine(root, architecture, name.Name);
                yield return Path.Combine(directory, $"v4.0_{version}_{culture}_{token}", name.Name + ".dll");
                yield return Path.Combine(directory, $"{version}_{culture}_{token}", name.Name + ".dll");
            }
        }
    }

    private static List<string> ComputeDefaultDirectories()
    {
        var directories = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        void Add(string? directory)
        {
            if (!string.IsNullOrEmpty(directory) && Directory.Exists(directory) && seen.Add(directory))
            {
                directories.Add(Path.TrimEndingDirectorySeparator(directory));
            }
        }

        // The running runtime (framework-dependent builds). Inside a single-file bundle this
        // does not exist on disk and is skipped.
        Add(RuntimeEnvironment.GetRuntimeDirectory());

        foreach (var root in DotnetRoots())
        {
            foreach (var directory in DotnetRootDirectories(root))
            {
                Add(directory);
            }
        }

        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            foreach (var directory in WindowsFrameworkDirectories(
                         Environment.GetEnvironmentVariable("WINDIR"),
                         Environment.GetEnvironmentVariable("ProgramFiles(x86)")))
            {
                Add(directory);
            }
        }

        return directories;
    }

    private static string[] ComputeDefaultGacRoots()
    {
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            return [];
        }

        var windir = Environment.GetEnvironmentVariable("WINDIR");
        if (string.IsNullOrEmpty(windir))
        {
            return [];
        }

        return new[] { Path.Combine(windir, "Microsoft.NET", "assembly"), Path.Combine(windir, "assembly") }
            .Where(Directory.Exists)
            .ToArray();
    }

    private static IEnumerable<string> DotnetRoots()
    {
        var candidates = new List<string?>
        {
            Environment.GetEnvironmentVariable("DOTNET_ROOT"),
            Environment.GetEnvironmentVariable("DOTNET_ROOT_X64"),
            Environment.GetEnvironmentVariable("DOTNET_ROOT_ARM64"),
        };

        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            candidates.Add(Combine(Environment.GetEnvironmentVariable("ProgramFiles"), "dotnet"));
            candidates.Add(Combine(Environment.GetEnvironmentVariable("ProgramFiles(x86)"), "dotnet"));
        }
        else if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
        {
            candidates.Add("/usr/local/share/dotnet");
            candidates.Add("/opt/homebrew/opt/dotnet/libexec");
        }
        else
        {
            candidates.Add("/usr/share/dotnet");
            candidates.Add("/usr/lib/dotnet");
            candidates.Add("/usr/lib64/dotnet");
        }

        candidates.Add(DotnetRootFromPath());

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var candidate in candidates)
        {
            if (!string.IsNullOrEmpty(candidate) && Directory.Exists(candidate) && seen.Add(Path.GetFullPath(candidate)))
            {
                yield return candidate;
            }
        }
    }

    private static string? DotnetRootFromPath()
    {
        var executable = RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ? "dotnet.exe" : "dotnet";
        var path = Environment.GetEnvironmentVariable("PATH");
        if (string.IsNullOrEmpty(path))
        {
            return null;
        }

        foreach (var directory in path.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            try
            {
                var candidate = Path.Combine(directory, executable);
                if (!File.Exists(candidate))
                {
                    continue;
                }

                var resolved = new FileInfo(candidate).ResolveLinkTarget(returnFinalTarget: true)?.FullName ?? candidate;
                return Path.GetDirectoryName(resolved);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
            {
                // An unreadable PATH entry is not worth failing resolution setup for.
            }
        }

        return null;
    }

    private static IEnumerable<string> VersionDirectories(string root, string? prefix = null) =>
        SafeDirectories(root)
            .Where(path => prefix is null || Path.GetFileName(path).StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(path => ParseVersion(Path.GetFileName(path)))
            .ThenByDescending(path => path, StringComparer.OrdinalIgnoreCase);

    private static string[] SafeDirectories(string root)
    {
        try
        {
            return Directory.Exists(root) ? Directory.GetDirectories(root) : [];
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return [];
        }
    }

    private static Version ParseVersion(string name)
    {
        var text = name.StartsWith('v') ? name[1..] : name;
        var dash = text.IndexOf('-', StringComparison.Ordinal);
        if (dash >= 0)
        {
            text = text[..dash];
        }

        return Version.TryParse(text, out var version) ? version : new Version(0, 0);
    }

    private static string? Combine(string? root, string child) =>
        string.IsNullOrEmpty(root) ? null : Path.Combine(root, child);
}
