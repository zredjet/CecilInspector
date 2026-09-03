using CecilInspector.Core;
using Mono.Cecil;
using System.Runtime.InteropServices;
using Xunit;

namespace CecilInspector.Tests;

public sealed class FrameworkProbeTests
{
    [Fact]
    public void DotnetRootYieldsSharedRuntimesAndReferencePacksNewestFirst()
    {
        using var temp = new TempDirectory();
        var shared8 = temp.CreateSubdirectory(Path.Combine("shared", "Microsoft.NETCore.App", "8.0.11"));
        var shared10 = temp.CreateSubdirectory(Path.Combine("shared", "Microsoft.NETCore.App", "10.0.2"));
        var preview = temp.CreateSubdirectory(Path.Combine("shared", "Microsoft.NETCore.App", "10.0.3-preview.1"));
        var ref8 = temp.CreateSubdirectory(Path.Combine("packs", "Microsoft.NETCore.App.Ref", "8.0.11", "ref", "net8.0"));
        var ref10 = temp.CreateSubdirectory(Path.Combine("packs", "Microsoft.NETCore.App.Ref", "10.0.2", "ref", "net10.0"));
        var ref10Legacy = temp.CreateSubdirectory(Path.Combine("packs", "Microsoft.NETCore.App.Ref", "10.0.2", "ref", "net9.0"));
        temp.CreateSubdirectory(Path.Combine("packs", "Microsoft.NETCore.App.Ref", "9.0.0"));

        var directories = FrameworkProbe.DotnetRootDirectories(temp.Path).ToArray();

        // Target framework folders sort by version ("net10.0" before "net9.0"), not by name.
        Assert.Equal([preview, shared10, shared8, ref10, ref10Legacy, ref8], directories);
    }

    [Fact]
    public void WindowsFrameworkDirectoriesCoverRuntimeAndReferenceAssemblies()
    {
        using var temp = new TempDirectory();
        var windir = temp.CreateSubdirectory("Windows");
        var programFiles = temp.CreateSubdirectory("ProgramFilesX86");
        var runtime64 = Directory.CreateDirectory(Path.Combine(windir, "Microsoft.NET", "Framework64", "v4.0.30319")).FullName;
        var runtime32 = Directory.CreateDirectory(Path.Combine(windir, "Microsoft.NET", "Framework", "v4.0.30319")).FullName;
        Directory.CreateDirectory(Path.Combine(windir, "Microsoft.NET", "Framework64", "v2.0.50727"));
        var reference = Path.Combine(programFiles, "Reference Assemblies", "Microsoft", "Framework", ".NETFramework", "v4.8");
        var facades = Directory.CreateDirectory(Path.Combine(reference, "Facades")).FullName;

        var directories = FrameworkProbe.WindowsFrameworkDirectories(windir, programFiles).ToArray();

        Assert.Equal([runtime64, runtime32, reference, facades], directories);
    }

    [Fact]
    public void GacCandidatePathsFollowNet4AndLegacyLayouts()
    {
        var name = new AssemblyNameReference("System.Data", new Version(4, 0, 0, 0))
        {
            PublicKeyToken = [0xb7, 0x7a, 0x5c, 0x56, 0x19, 0x34, 0xe0, 0x89],
        };

        var paths = FrameworkProbe.GacCandidatePaths(name, [Path.Combine("R", "assembly")]).ToArray();

        Assert.Equal(8, paths.Length);
        Assert.Equal(
            Path.Combine("R", "assembly", "GAC_MSIL", "System.Data", "v4.0_4.0.0.0__b77a5c561934e089", "System.Data.dll"),
            paths[0]);
        Assert.Equal(
            Path.Combine("R", "assembly", "GAC_MSIL", "System.Data", "4.0.0.0__b77a5c561934e089", "System.Data.dll"),
            paths[1]);
        Assert.Contains(paths, path => path.Contains(Path.Combine("GAC_64", "System.Data"), StringComparison.Ordinal));
    }

    [Fact]
    public void ResolverFallsBackToGacLayoutWithMatchingIdentity()
    {
        using var temp = new TempDirectory();
        var gacRoot = temp.CreateSubdirectory("assembly");
        var wrongVersion = Path.Combine(gacRoot, "GAC_MSIL", "Model", "v4.0_1.0.0.0__", "Model.dll");
        var rightVersion = Path.Combine(gacRoot, "GAC_MSIL", "Model", "v4.0_2.0.0.0__", "Model.dll");
        WriteAssembly(wrongVersion, new Version(1, 0, 0, 0));
        WriteAssembly(rightVersion, new Version(2, 0, 0, 0));
        using var resolver = CecilResolverFactory.Create(temp.File("Target.dll"), [], [temp.Path], [], [gacRoot]);

        var resolved = resolver.Resolve(new AssemblyNameReference("Model", new Version(2, 0, 0, 0)));

        Assert.Equal(new Version(2, 0, 0, 0), resolved.Name.Version);
        Assert.Equal(rightVersion, resolved.MainModule.FileName);
        Assert.Throws<AssemblyResolutionException>(() =>
            resolver.Resolve(new AssemblyNameReference("Model", new Version(3, 0, 0, 0))));
    }

    [Fact]
    public void FrameworkDirectoriesAreProbedAfterInputDirectories()
    {
        using var temp = new TempDirectory();
        var framework = temp.CreateSubdirectory("framework");
        WriteAssembly(Path.Combine(framework, "Model.dll"), new Version(5, 0, 0, 0));
        using var resolver = CecilResolverFactory.Create(temp.File("Target.dll"), [], [temp.Path], [framework], []);

        Assert.Equal([temp.Path], resolver.GetSearchDirectories());
        Assert.Equal([temp.Path, framework], resolver.AllSearchDirectories);
        var resolved = resolver.Resolve(new AssemblyNameReference("Model", new Version(5, 0, 0, 0)));
        Assert.Equal(Path.Combine(framework, "Model.dll"), resolved.MainModule.FileName);
    }

    [Fact]
    public void SharedFrameworkResolverOpensEachAssemblyOnceAcrossFiles()
    {
        using var temp = new TempDirectory();
        var framework = temp.CreateSubdirectory("framework");
        WriteAssembly(Path.Combine(framework, "Model.dll"), new Version(5, 0, 0, 0));
        var name = new AssemblyNameReference("Model", new Version(5, 0, 0, 0));
        using var shared = CecilResolverFactory.CreateFrameworkResolver([framework], []);

        AssemblyDefinition first;
        using (var firstFile = CecilResolverFactory.Create(temp.File("First.dll"), [], [temp.Path], shared))
        {
            first = firstFile.Resolve(name);
        }

        var probesAfterFirst = shared.ProbeCount;
        using var secondFile = CecilResolverFactory.Create(temp.File("Second.dll"), [], [temp.Path], shared);
        var second = secondFile.Resolve(name);

        // The per-file resolver that resolved it first has been disposed; the shared resolver
        // owns the assembly, so it is the same instance and still readable.
        Assert.Same(first, second);
        Assert.Equal(probesAfterFirst, shared.ProbeCount);
        Assert.NotEmpty(second.MainModule.Types);
    }

    [Fact]
    public void AssemblyNamesThatAreNotFileNamesAreNotProbed()
    {
        using var temp = new TempDirectory();
        using var resolver = CecilResolverFactory.Create(temp.File("Target.dll"), [], [temp.Path], [], []);

        Assert.Throws<AssemblyResolutionException>(() =>
            resolver.Resolve(new AssemblyNameReference(Path.Combine("..", "Model"), new Version(1, 0, 0, 0))));
        Assert.Equal(0, resolver.ProbeCount);
    }

    [Fact]
    public void DefaultProbeFindsTheRunningRuntimeOrAnInstalledDotnet()
    {
        // The test host is framework-dependent, so at least the running runtime directory
        // must be present; on machines with a dotnet install the shared runtimes follow.
        Assert.NotEmpty(FrameworkProbe.Directories);
        Assert.All(FrameworkProbe.Directories, directory => Assert.True(Directory.Exists(directory), directory));
        Assert.Contains(FrameworkProbe.Directories, directory =>
            File.Exists(Path.Combine(directory, "System.Runtime.dll")));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void SameOrNewerVersionsBindLikeTheRuntimeButOlderOnesDoNot(bool asFrameworkLocation)
    {
        using var temp = new TempDirectory();
        var folder = temp.CreateSubdirectory("folder");
        WriteAssembly(Path.Combine(folder, "Model.dll"), new Version(5, 0, 0, 0));
        var older = new AssemblyNameReference("Model", new Version(4, 0, 0, 0));
        var same = new AssemblyNameReference("Model", new Version(5, 0, 0, 0));
        var newer = new AssemblyNameReference("Model", new Version(6, 0, 0, 0));
        using var resolver = asFrameworkLocation
            ? CecilResolverFactory.Create(temp.File("Target.dll"), [], [temp.Path], [folder], [])
            : CecilResolverFactory.Create(temp.File("Target.dll"), [folder], [temp.Path], [], []);

        Assert.Equal(new Version(5, 0, 0, 0), resolver.Resolve(older).Name.Version);
        Assert.Equal(new Version(5, 0, 0, 0), resolver.Resolve(same).Name.Version);
        var failure = Assert.Throws<AssemblyResolutionException>(() => resolver.Resolve(newer));
        Assert.Contains("Version=5.0.0.0 で要求 6.0.0.0 より古い", AssemblyResolutionDetail.Describe(failure), StringComparison.Ordinal);
        Assert.Contains(Path.Combine(folder, "Model.dll"), AssemblyResolutionDetail.Describe(failure), StringComparison.Ordinal);
    }

    [Fact]
    public void FailureExplainsAMissingFileOrAMismatchedToken()
    {
        using var temp = new TempDirectory();
        WriteAssembly(temp.File("Model.dll"), new Version(1, 0, 0, 0));
        using var resolver = CecilResolverFactory.Create(temp.File("Target.dll"), [], [temp.Path], [], []);

        var missing = Assert.Throws<AssemblyResolutionException>(() =>
            resolver.Resolve(new AssemblyNameReference("Nowhere", new Version(1, 0, 0, 0))));
        Assert.Contains("Nowhere.dll が検索フォルダにありません", AssemblyResolutionDetail.Describe(missing), StringComparison.Ordinal);
        Assert.Contains(temp.Path, AssemblyResolutionDetail.Describe(missing), StringComparison.Ordinal);

        var signed = new AssemblyNameReference("Model", new Version(1, 0, 0, 0))
        {
            PublicKeyToken = [0xb0, 0x3f, 0x5f, 0x7f, 0x11, 0xd5, 0x0a, 0x3a],
        };
        var token = Assert.Throws<AssemblyResolutionException>(() => resolver.Resolve(signed));
        Assert.Contains("PublicKeyToken が異なります (要求 b03f5f7f11d50a3a, 候補 null)", AssemblyResolutionDetail.Describe(token), StringComparison.Ordinal);
    }

    [Fact]
    public void RunningRuntimeAssembliesResolveThroughTrustedPlatformAssemblies()
    {
        using var temp = new TempDirectory();
        using var resolver = CecilResolverFactory.Create(temp.File("Target.dll"), [], [temp.Path], [], []);
        var request = new AssemblyNameReference("System.Runtime", new Version(1, 0, 0, 0))
        {
            PublicKeyToken = [0xb0, 0x3f, 0x5f, 0x7f, 0x11, 0xd5, 0x0a, 0x3a],
        };

        var resolved = resolver.Resolve(request);

        Assert.Equal("System.Runtime", resolved.Name.Name);
        Assert.True(resolved.Name.Version >= request.Version);
    }

    [Fact]
    public void DotnetRootEnvironmentVariableIsProbedAfterTheRunningRuntime()
    {
        using var temp = new TempDirectory();
        var runtime = temp.CreateSubdirectory("runtime");
        var root = temp.CreateSubdirectory("root");
        var shared = Directory.CreateDirectory(Path.Combine(root, "shared", "Microsoft.NETCore.App", "9.0.0")).FullName;

        var directories = FrameworkProbe.ComputeDirectories(Env(("DOTNET_ROOT", root)), OSPlatform.Linux, runtime);

        Assert.Equal([runtime, shared], directories);
    }

    [Fact]
    public void ArchitectureSpecificDotnetRootIsProbedToo()
    {
        using var temp = new TempDirectory();
        var root = temp.CreateSubdirectory("arm64");
        var shared = Directory.CreateDirectory(Path.Combine(root, "shared", "Microsoft.NETCore.App", "8.0.0")).FullName;

        var directories = FrameworkProbe.ComputeDirectories(Env(("DOTNET_ROOT_ARM64", root)), OSPlatform.OSX, null);

        Assert.Contains(shared, directories);
    }

    [Fact]
    public void DotnetOnPathIsResolvedThroughItsSymbolicLink()
    {
        using var temp = new TempDirectory();
        SymbolicLinks.SkipUnlessSupported(temp);
        var real = temp.CreateSubdirectory("real");
        var bin = temp.CreateSubdirectory("bin");
        File.WriteAllText(Path.Combine(real, "dotnet"), "#!/bin/sh");
        File.CreateSymbolicLink(Path.Combine(bin, "dotnet"), Path.Combine(real, "dotnet"));
        var path = string.Join(Path.PathSeparator, temp.CreateSubdirectory("empty"), bin);

        var root = FrameworkProbe.DotnetRootFromPath(Env(("PATH", path)), OSPlatform.Linux);

        Assert.Equal(real, root);
    }

    [Fact]
    public void DotnetShimOnPathYieldsItsOwnDirectory()
    {
        using var temp = new TempDirectory();
        var bin = temp.CreateSubdirectory("bin");
        File.WriteAllText(Path.Combine(bin, "dotnet.exe"), "shim");

        Assert.Equal(bin, FrameworkProbe.DotnetRootFromPath(Env(("PATH", bin)), OSPlatform.Windows));
        Assert.Null(FrameworkProbe.DotnetRootFromPath(Env(("PATH", bin)), OSPlatform.Linux));
        Assert.Null(FrameworkProbe.DotnetRootFromPath(Env(), OSPlatform.Linux));
    }

    [Fact]
    public void GacRootsAndFrameworkDirectoriesComeFromWindirOnWindowsOnly()
    {
        using var temp = new TempDirectory();
        var windir = temp.CreateSubdirectory("Windows");
        var gac = Directory.CreateDirectory(Path.Combine(windir, "Microsoft.NET", "assembly")).FullName;
        var legacyGac = Directory.CreateDirectory(Path.Combine(windir, "assembly")).FullName;
        var runtime = Directory.CreateDirectory(Path.Combine(windir, "Microsoft.NET", "Framework64", "v4.0.30319")).FullName;
        var env = Env(("WINDIR", windir));

        Assert.Equal([gac, legacyGac], FrameworkProbe.ComputeGacRoots(env, OSPlatform.Windows));
        Assert.Empty(FrameworkProbe.ComputeGacRoots(env, OSPlatform.Linux));
        Assert.Contains(runtime, FrameworkProbe.ComputeDirectories(env, OSPlatform.Windows, null));
        Assert.DoesNotContain(runtime, FrameworkProbe.ComputeDirectories(env, OSPlatform.Linux, null));
    }

    private static Func<string, string?> Env(params (string Name, string Value)[] variables) =>
        name => variables.FirstOrDefault(variable => variable.Name == name).Value;

    private static void WriteAssembly(string path, Version version)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        using var assembly = AssemblyDefinition.CreateAssembly(
            new AssemblyNameDefinition("Model", version), "Model", ModuleKind.Dll);
        assembly.Write(path);
    }
}
