using CecilInspector.Core;
using Xunit;

namespace CecilInspector.Tests;

public sealed class AssemblyFilesTests
{
    [Fact]
    public void EmptyDirectoryIsReportedAsInputError()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"cecil-inspector-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        try
        {
            var error = Assert.Throws<ArgumentException>(() => AssemblyFiles.Discover(directory, true));
            Assert.Contains("対象アセンブリ", error.Message, StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(directory);
        }
    }

    [Fact]
    public void SkippedSymbolicLinkIsReported()
    {
        var baseDirectory = Path.Combine(Path.GetTempPath(), $"cecil-inspector-{Guid.NewGuid():N}");
        var root = Path.Combine(baseDirectory, "root");
        var target = Path.Combine(baseDirectory, "target");
        Directory.CreateDirectory(root);
        Directory.CreateDirectory(target);
        File.Copy(typeof(AssemblyFilesTests).Assembly.Location, Path.Combine(target, "target.dll"));
        Directory.CreateSymbolicLink(Path.Combine(root, "linked"), target);
        try
        {
            var result = AssemblyFiles.DiscoverDetailed(root, true);

            Assert.Empty(result.Files);
            Assert.Single(result.Errors);
            Assert.Contains("シンボリックリンク", result.Errors[0].Message, StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(baseDirectory, true);
        }
    }

    [Fact]
    public void DiscoveryFilesAreSnapshottedDuringProtectedTraversal()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"cecil-inspector-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        var assembly = Path.Combine(directory, "snapshot.dll");
        File.Copy(typeof(AssemblyFilesTests).Assembly.Location, assembly);

        var result = AssemblyFiles.DiscoverDetailed(directory, true);
        Directory.Delete(directory, true);

        Assert.Equal(1, result.FileCount);
        Assert.Equal(assembly, Assert.Single(result.Files));
    }

    [Fact]
    public void FileSymbolicLinkIsSkippedWithWarning()
    {
        var baseDirectory = Path.Combine(Path.GetTempPath(), $"cecil-inspector-{Guid.NewGuid():N}");
        var root = Path.Combine(baseDirectory, "root");
        var outside = Path.Combine(baseDirectory, "outside");
        Directory.CreateDirectory(root);
        Directory.CreateDirectory(outside);
        var target = Path.Combine(outside, "target.dll");
        File.Copy(typeof(AssemblyFilesTests).Assembly.Location, target);
        File.CreateSymbolicLink(Path.Combine(root, "linked.dll"), target);
        try
        {
            var result = AssemblyFiles.DiscoverDetailed(root, true);

            Assert.Empty(result.Files);
            var error = Assert.Single(result.Errors);
            Assert.Contains("シンボリックリンク", error.Message, StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(baseDirectory, true);
        }
    }

    [Fact]
    public void RootDirectorySymbolicLinkIsRejected()
    {
        var baseDirectory = Path.Combine(Path.GetTempPath(), $"cecil-inspector-{Guid.NewGuid():N}");
        var target = Path.Combine(baseDirectory, "target");
        var linkedRoot = Path.Combine(baseDirectory, "linked-root");
        Directory.CreateDirectory(target);
        Directory.CreateSymbolicLink(linkedRoot, target);
        try
        {
            var error = Assert.Throws<ArgumentException>(() => AssemblyFiles.DiscoverDetailed(linkedRoot, true));

            Assert.Contains("シンボリックリンク", error.Message, StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(baseDirectory, true);
        }
    }
}
