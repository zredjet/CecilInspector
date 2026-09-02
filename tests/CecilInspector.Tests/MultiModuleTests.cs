using System.Diagnostics;
using System.Runtime.InteropServices;
using CecilInspector.Cli;
using CecilInspector.Core;
using Xunit;

namespace CecilInspector.Tests;

/// <summary>
/// Multi-module assemblies (a manifest DLL plus secondary .netmodule files). Cecil's netstandard
/// build cannot write them, so the fixture is compiled with the SDK's csc.dll; the tests skip when
/// no SDK is installed next to the running runtime.
/// </summary>
public sealed class MultiModuleTests
{
    [Fact]
    public void SingleFileInputSearchesSecondaryNetmodules()
    {
        using var temp = new TempDirectory();
        var fixture = MultiModuleFixture.Build(temp.Path);
        File.WriteAllText(temp.File("Unrelated.dll"), "not an assembly");

        var result = new AssemblySearcher().Search(Options(fixture.Manifest, "NetModuleType", SearchKinds.Type));

        var hit = Assert.Single(result.Hits);
        Assert.Equal(fixture.NetModule, hit.AssemblyPath);
        Assert.Equal(1, result.FilesSucceeded);
        Assert.Empty(result.Errors);
    }

    [Fact]
    public void SecondaryNetmoduleReferencesAreSearchedFromTheManifest()
    {
        using var temp = new TempDirectory();
        var fixture = MultiModuleFixture.Build(temp.Path);

        var result = new AssemblySearcher().Search(
            Options(fixture.Manifest, "NetModuleMethod", SearchKinds.Method) with { Scope = SearchScope.All });

        Assert.Contains(result.Hits, hit => hit.Scope == HitScope.Definition && hit.AssemblyPath == fixture.NetModule);
        Assert.Contains(result.Hits, hit => hit.Scope == HitScope.Reference && hit.AssemblyPath == fixture.Manifest);
    }

    [Fact]
    public void FolderInputScansNetmodulesAsFilesExactlyOnce()
    {
        using var temp = new TempDirectory();
        var fixture = MultiModuleFixture.Build(temp.Path);

        var result = new AssemblySearcher().Search(Options(temp.Path, "NetModuleType", SearchKinds.Type));

        var hit = Assert.Single(result.Hits);
        Assert.Equal(fixture.NetModule, hit.AssemblyPath);
        Assert.Equal(2, result.FilesSucceeded);
    }

    [Fact]
    public void NetmoduleFilesAreReleasedAfterTheSearch()
    {
        using var temp = new TempDirectory();
        var fixture = MultiModuleFixture.Build(temp.Path);

        _ = new AssemblySearcher().Search(Options(fixture.Manifest, "NetModuleType", SearchKinds.Type));

        using var exclusive = new FileStream(fixture.NetModule, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        Assert.True(exclusive.Length > 0);
    }

    [Fact]
    public void DumpOfSingleFileIncludesSecondaryNetmodules()
    {
        using var temp = new TempDirectory();
        var fixture = MultiModuleFixture.Build(temp.Path);
        var discovery = AssemblyFiles.DiscoverDetailed(fixture.Manifest, true);
        using var writer = new StringWriter();

        var result = new MetadataDumper().Dump(
            new DumpOptions(fixture.Manifest, true, false, SymbolMode.Off, null, []),
            discovery,
            writer,
            TestContext.Current.CancellationToken);

        var output = writer.ToString();
        Assert.Equal(1, result.FilesSucceeded);
        Assert.Empty(result.Errors);
        Assert.Contains("Module: Second.netmodule", output, StringComparison.Ordinal);
        Assert.Contains("Type: Fixtures.NetModuleType", output, StringComparison.Ordinal);
        Assert.Contains($"File: {fixture.NetModule}", output, StringComparison.Ordinal);
    }

    [Fact]
    public void MissingNetmoduleIsReportedWithoutLosingManifestHits()
    {
        using var temp = new TempDirectory();
        var fixture = MultiModuleFixture.Build(temp.Path);
        File.Delete(fixture.NetModule);

        var result = new AssemblySearcher().Search(Options(fixture.Manifest, "ManifestType", SearchKinds.Type));

        Assert.Single(result.Hits);
        Assert.Equal(1, result.FilesSucceeded);
        var error = Assert.Single(result.Errors);
        Assert.Contains("secondary netmodule", error.Message, StringComparison.Ordinal);
    }

    private static SearchOptions Options(string input, string query, SearchKinds kinds) =>
        new(input, query, kinds, SearchScope.Definitions, MatchMode.Exact, true, true, SymbolMode.Off, 100, null, []);
}

internal static class MultiModuleFixture
{
    public static (string Manifest, string NetModule) Build(string directory)
    {
        var csc = FindCsc();
        Assert.SkipWhen(csc is null, "実行中のランタイムの隣に .NET SDK (csc.dll) が見つからない。");

        var runtimeDirectory = RuntimeEnvironment.GetRuntimeDirectory();
        var references = new[] { "System.Runtime.dll", "System.Private.CoreLib.dll" }
            .Select(name => "-r:" + Path.Combine(runtimeDirectory, name))
            .ToArray();
        var netModule = Path.Combine(directory, "Second.netmodule");
        var manifest = Path.Combine(directory, "Multi.dll");
        var secondSource = Path.Combine(directory, "Second.cs");
        var mainSource = Path.Combine(directory, "Main.cs");
        File.WriteAllText(
            secondSource,
            "namespace Fixtures { public class NetModuleType { public static void NetModuleMethod() { } } }");
        File.WriteAllText(
            mainSource,
            "namespace Fixtures { public class ManifestType { public static void Call() { NetModuleType.NetModuleMethod(); } } }");

        Compile(csc!, ["-target:module", "-out:" + netModule, .. references, secondSource]);
        Compile(csc!, ["-target:library", "-addmodule:" + netModule, "-out:" + manifest, .. references, mainSource]);
        File.Delete(secondSource);
        File.Delete(mainSource);
        return (manifest, netModule);
    }

    private static string? FindCsc()
    {
        // <root>/shared/Microsoft.NETCore.App/<version>/ -> <root>/sdk/<version>/Roslyn/bincore/csc.dll
        var root = Path.GetFullPath(Path.Combine(RuntimeEnvironment.GetRuntimeDirectory(), "..", "..", ".."));
        var sdks = Path.Combine(root, "sdk");
        if (!Directory.Exists(sdks))
        {
            return null;
        }

        return Directory.GetDirectories(sdks)
            .Select(sdk => Path.Combine(sdk, "Roslyn", "bincore", "csc.dll"))
            .Where(File.Exists)
            .OrderByDescending(path => Version.TryParse(Path.GetFileName(Path.GetDirectoryName(Path.GetDirectoryName(Path.GetDirectoryName(path)!))!), out var version) ? version : new Version(0, 0))
            .FirstOrDefault();
    }

    private static void Compile(string csc, string[] arguments)
    {
        var startInfo = new ProcessStartInfo("dotnet")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        startInfo.ArgumentList.Add(csc);
        startInfo.ArgumentList.Add("-nologo");
        startInfo.ArgumentList.Add("-noconfig");
        startInfo.ArgumentList.Add("-nostdlib");
        startInfo.ArgumentList.Add("-debug-");
        startInfo.ArgumentList.Add("-optimize-");
        foreach (var argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        startInfo.Environment["DOTNET_NOLOGO"] = "1";
        using var process = Process.Start(startInfo) ?? throw new InvalidOperationException("csc を起動できませんでした。");
        var output = process.StandardOutput.ReadToEnd() + process.StandardError.ReadToEnd();
        process.WaitForExit();
        if (process.ExitCode != 0)
        {
            throw new InvalidOperationException($"csc が失敗しました ({process.ExitCode}): {output}");
        }
    }
}
