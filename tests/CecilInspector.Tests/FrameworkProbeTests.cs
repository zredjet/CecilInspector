using CecilInspector.Core;
using Mono.Cecil;
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
        temp.CreateSubdirectory(Path.Combine("packs", "Microsoft.NETCore.App.Ref", "9.0.0"));

        var directories = FrameworkProbe.DotnetRootDirectories(temp.Path).ToArray();

        Assert.Equal([preview, shared10, shared8, ref10, ref8], directories);
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

        using var resolved = resolver.Resolve(new AssemblyNameReference("Model", new Version(2, 0, 0, 0)));

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

        Assert.Equal([temp.Path, framework], resolver.GetSearchDirectories());
        using var resolved = resolver.Resolve(new AssemblyNameReference("Model", new Version(5, 0, 0, 0)));
        Assert.Equal(Path.Combine(framework, "Model.dll"), resolved.MainModule.FileName);
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

    [Fact]
    public void FrameworkDirectoriesAcceptNewerVersionsButInputDirectoriesDoNot()
    {
        using var temp = new TempDirectory();
        var framework = temp.CreateSubdirectory("framework");
        WriteAssembly(Path.Combine(framework, "Model.dll"), new Version(5, 0, 0, 0));
        var older = new AssemblyNameReference("Model", new Version(4, 0, 0, 0));
        var newer = new AssemblyNameReference("Model", new Version(6, 0, 0, 0));

        using (var asFramework = CecilResolverFactory.Create(temp.File("Target.dll"), [], [temp.Path], [framework], []))
        {
            using var resolved = asFramework.Resolve(older);
            Assert.Equal(new Version(5, 0, 0, 0), resolved.Name.Version);
            Assert.Throws<AssemblyResolutionException>(() => asFramework.Resolve(newer));
        }

        using (var asInput = CecilResolverFactory.Create(temp.File("Target.dll"), [framework], [temp.Path], [], []))
        {
            Assert.Throws<AssemblyResolutionException>(() => asInput.Resolve(older));
            using var exact = asInput.Resolve(new AssemblyNameReference("Model", new Version(5, 0, 0, 0)));
            Assert.Equal(new Version(5, 0, 0, 0), exact.Name.Version);
        }
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

        using var resolved = resolver.Resolve(request);

        Assert.Equal("System.Runtime", resolved.Name.Name);
        Assert.True(resolved.Name.Version >= request.Version);
    }

    private static void WriteAssembly(string path, Version version)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        using var assembly = AssemblyDefinition.CreateAssembly(
            new AssemblyNameDefinition("Model", version), "Model", ModuleKind.Dll);
        assembly.Write(path);
    }
}
