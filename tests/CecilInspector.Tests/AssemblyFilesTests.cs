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

        var error = Assert.Throws<ArgumentException>(() => AssemblyFiles.DiscoverDetailed(temp.Path, true, TestContext.Current.CancellationToken));

        Assert.Contains("対象アセンブリ", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void MissingInputPathIsReportedAsInputError()
    {
        using var temp = new TempDirectory();

        var error = Assert.Throws<ArgumentException>(() =>
            AssemblyFiles.DiscoverDetailed(temp.File("missing.dll"), true, TestContext.Current.CancellationToken));

        Assert.Contains("入力パスが見つかりません", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void SingleFileInputReportsItsDirectoryAsSearchDirectory()
    {
        using var temp = new TempDirectory();
        var assembly = temp.File("single.dll");
        File.Copy(ThisAssembly, assembly);

        var result = AssemblyFiles.DiscoverDetailed(assembly, true, TestContext.Current.CancellationToken);

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

        var result = AssemblyFiles.DiscoverDetailed(temp.Path, true, TestContext.Current.CancellationToken);

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

        var result = AssemblyFiles.DiscoverDetailed(temp.Path, false, TestContext.Current.CancellationToken);

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
        SymbolicLinks.SkipUnlessSupported(temp);
        Directory.CreateSymbolicLink(Path.Combine(root, "linked"), target);

        var result = AssemblyFiles.DiscoverDetailed(root, true, TestContext.Current.CancellationToken);

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

        var result = AssemblyFiles.DiscoverDetailed(temp.Path, true, TestContext.Current.CancellationToken);
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
        SymbolicLinks.SkipUnlessSupported(temp);
        File.CreateSymbolicLink(Path.Combine(root, "linked.dll"), target);

        var result = AssemblyFiles.DiscoverDetailed(root, true, TestContext.Current.CancellationToken);

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
        SymbolicLinks.SkipUnlessSupported(temp);
        Directory.CreateSymbolicLink(linkedRoot, target);

        var error = Assert.Throws<ArgumentException>(() => AssemblyFiles.DiscoverDetailed(linkedRoot, true, TestContext.Current.CancellationToken));

        Assert.Contains("シンボリックリンク", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void DiscoveryObservesCancellation()
    {
        using var temp = new TempDirectory();
        File.Copy(ThisAssembly, temp.File("root.dll"));
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        Assert.ThrowsAny<OperationCanceledException>(() => AssemblyFiles.DiscoverDetailed(temp.Path, true, cancellation.Token));
    }

    [Fact]
    public void SymbolicLinkToANonAssemblyFileIsIgnoredSilently()
    {
        // Only links the scan would otherwise follow are gaps in the result; a linked README
        // or native library must not turn a clean run into a partial one.
        using var temp = new TempDirectory();
        var root = temp.CreateSubdirectory("root");
        File.Copy(ThisAssembly, Path.Combine(root, "root.dll"));
        var target = temp.File("target.txt");
        File.WriteAllText(target, "text");
        SymbolicLinks.SkipUnlessSupported(temp);
        File.CreateSymbolicLink(Path.Combine(root, "linked.txt"), target);

        var result = AssemblyFiles.DiscoverDetailed(root, true, TestContext.Current.CancellationToken);

        Assert.Empty(result.Errors);
        Assert.Single(result.Files);
    }

    [Fact]
    public void DirectorySymbolicLinkIsIgnoredWhenNotRecursive()
    {
        using var temp = new TempDirectory();
        var root = temp.CreateSubdirectory("root");
        File.Copy(ThisAssembly, Path.Combine(root, "root.dll"));
        var target = temp.CreateSubdirectory("target");
        File.Copy(ThisAssembly, Path.Combine(target, "target.dll"));
        SymbolicLinks.SkipUnlessSupported(temp);
        Directory.CreateSymbolicLink(Path.Combine(root, "linked"), target);

        var result = AssemblyFiles.DiscoverDetailed(root, false, TestContext.Current.CancellationToken);

        Assert.Empty(result.Errors);
        Assert.Single(result.Files);
    }
}
