using CecilInspector.Core;
using Xunit;

namespace CecilInspector.Tests;

public sealed class AssemblyFilesTests
{
    private static readonly string ThisAssembly = typeof(AssemblyFilesTests).Assembly.Location;

    [Fact]
    public void EmptyDirectoryIsReportedAsInputError()
    {
        using var temp = new TempDirectory();

        var error = Assert.Throws<ArgumentException>(() => AssemblyFiles.DiscoverDetailed(temp.Path, true));

        Assert.Contains("対象アセンブリ", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void MissingInputPathIsReportedAsInputError()
    {
        using var temp = new TempDirectory();

        var error = Assert.Throws<ArgumentException>(() =>
            AssemblyFiles.DiscoverDetailed(temp.File("missing.dll"), true));

        Assert.Contains("入力パスが見つかりません", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void SingleFileInputReportsItsDirectoryAsSearchDirectory()
    {
        using var temp = new TempDirectory();
        var assembly = temp.File("single.dll");
        File.Copy(ThisAssembly, assembly);

        var result = AssemblyFiles.DiscoverDetailed(assembly, true);

        Assert.Equal([assembly], result.Files);
        Assert.Equal(1, result.FileCount);
        Assert.Equal([temp.Path], result.SearchDirectories);
        Assert.Empty(result.Errors);
    }

    [Fact]
    public void DiscoversAssemblyExtensionsInSortedOrderAndRecordsSearchDirectories()
    {
        using var temp = new TempDirectory();
        var nested = temp.CreateSubdirectory("nested");
        File.WriteAllText(temp.File("b.exe"), string.Empty);
        File.WriteAllText(temp.File("a.netmodule"), string.Empty);
        File.WriteAllText(temp.File("ignored.txt"), string.Empty);
        File.WriteAllText(Path.Combine(nested, "c.dll"), string.Empty);
        temp.CreateSubdirectory("empty");

        var result = AssemblyFiles.DiscoverDetailed(temp.Path, true);

        Assert.Equal(
            [temp.File("a.netmodule"), temp.File("b.exe"), Path.Combine(nested, "c.dll")],
            result.Files);
        Assert.Equal(3, result.FileCount);
        Assert.Equal([temp.Path, nested], result.SearchDirectories);
    }

    [Fact]
    public void NonRecursiveDiscoveryIgnoresSubdirectories()
    {
        using var temp = new TempDirectory();
        var nested = temp.CreateSubdirectory("nested");
        File.WriteAllText(temp.File("root.dll"), string.Empty);
        File.WriteAllText(Path.Combine(nested, "nested.dll"), string.Empty);

        var result = AssemblyFiles.DiscoverDetailed(temp.Path, false);

        Assert.Equal([temp.File("root.dll")], result.Files);
        Assert.Equal([temp.Path], result.SearchDirectories);
    }

    [Fact]
    public void SkippedSymbolicLinkIsReported()
    {
        using var temp = new TempDirectory();
        var root = temp.CreateSubdirectory("root");
        var target = temp.CreateSubdirectory("target");
        File.Copy(ThisAssembly, Path.Combine(target, "target.dll"));
        Directory.CreateSymbolicLink(Path.Combine(root, "linked"), target);

        var result = AssemblyFiles.DiscoverDetailed(root, true);

        Assert.Empty(result.Files);
        Assert.Single(result.Errors);
        Assert.Contains("シンボリックリンク", result.Errors[0].Message, StringComparison.Ordinal);
    }

    [Fact]
    public void DiscoveryFilesAreSnapshottedDuringProtectedTraversal()
    {
        using var temp = new TempDirectory();
        var assembly = temp.File("snapshot.dll");
        File.Copy(ThisAssembly, assembly);

        var result = AssemblyFiles.DiscoverDetailed(temp.Path, true);
        Directory.Delete(temp.Path, true);

        Assert.Equal(1, result.FileCount);
        Assert.Equal(assembly, Assert.Single(result.Files));
    }

    [Fact]
    public void FileSymbolicLinkIsSkippedWithWarning()
    {
        using var temp = new TempDirectory();
        var root = temp.CreateSubdirectory("root");
        var outside = temp.CreateSubdirectory("outside");
        var target = Path.Combine(outside, "target.dll");
        File.Copy(ThisAssembly, target);
        File.CreateSymbolicLink(Path.Combine(root, "linked.dll"), target);

        var result = AssemblyFiles.DiscoverDetailed(root, true);

        Assert.Empty(result.Files);
        var error = Assert.Single(result.Errors);
        Assert.Contains("シンボリックリンク", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void RootDirectorySymbolicLinkIsRejected()
    {
        using var temp = new TempDirectory();
        var target = temp.CreateSubdirectory("target");
        var linkedRoot = temp.File("linked-root");
        Directory.CreateSymbolicLink(linkedRoot, target);

        var error = Assert.Throws<ArgumentException>(() => AssemblyFiles.DiscoverDetailed(linkedRoot, true));

        Assert.Contains("シンボリックリンク", error.Message, StringComparison.Ordinal);
    }
}
