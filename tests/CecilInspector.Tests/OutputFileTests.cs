using CecilInspector.Output;
using Xunit;

namespace CecilInspector.Tests;

public sealed class OutputFileTests
{
    [Fact]
    public void NullOutputPathMeansNoReportFile()
    {
        Assert.Null(OutputFile.OpenAtomic(null));
    }

    [Fact]
    public void RefusesToOverwriteAnyExistingFile()
    {
        using var temp = new TempDirectory();
        var existing = temp.File("existing.txt");
        File.WriteAllText(existing, "keep");

        var error = Assert.Throws<ArgumentException>(() => OutputFile.OpenAtomic(existing));

        Assert.Contains("上書きしません", error.Message, StringComparison.Ordinal);
        Assert.Equal("keep", File.ReadAllText(existing));
    }

    [Fact]
    public void RefusesExistingDirectoryAsOutput()
    {
        using var temp = new TempDirectory();

        var error = Assert.Throws<ArgumentException>(() => OutputFile.OpenAtomic(temp.Path));

        Assert.Contains("上書きしません", error.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("report.dll")]
    [InlineData("report.EXE")]
    [InlineData("report.netmodule")]
    public void RefusesAssemblyExtensionEvenWhenFileDoesNotExist(string name)
    {
        using var temp = new TempDirectory();

        var error = Assert.Throws<ArgumentException>(() => OutputFile.OpenAtomic(temp.File(name)));

        Assert.Contains("アセンブリ拡張子", error.Message, StringComparison.Ordinal);
        Assert.Empty(Directory.GetFiles(temp.Path));
    }

    [Fact]
    public void CommitPublishesUtf8ReportWithoutBomAndRemovesPartialFile()
    {
        using var temp = new TempDirectory();
        var output = temp.File("nested/report.txt");

        using (var report = OutputFile.OpenAtomic(output))
        {
            Assert.NotNull(report);
            report.Writer.Write("レポート");
            report.Commit();
        }

        Assert.Equal("レポート"u8.ToArray(), File.ReadAllBytes(output));
        Assert.Equal([output], Directory.GetFiles(Path.GetDirectoryName(output)!));
    }

    [Fact]
    public void DoesNotPublishUncommittedReport()
    {
        using var temp = new TempDirectory();
        var output = temp.File("report.txt");

        using (var report = OutputFile.OpenAtomic(output))
        {
            Assert.NotNull(report);
            report.Writer.Write("partial");
        }

        Assert.False(File.Exists(output));
        Assert.Empty(Directory.GetFiles(temp.Path));
    }

    [Fact]
    public void RefusesToCommitWhenPartialPathIsReplacedBySymbolicLink()
    {
        using var temp = new TempDirectory();
        var output = temp.File("report.txt");
        var attacker = temp.File("attacker.txt");
        File.WriteAllText(attacker, "attacker-controlled");

        using (var report = OutputFile.OpenAtomic(output))
        {
            Assert.NotNull(report);
            report.Writer.Write("real report");
            report.Writer.Flush();

            var partial = Assert.Single(Directory.GetFiles(temp.Path, "*.partial"));
            File.Move(partial, partial + ".real");
            File.CreateSymbolicLink(partial, attacker);

            var error = Assert.Throws<IOException>(report.Commit);
            Assert.Contains("差し替え", error.Message, StringComparison.Ordinal);
        }

        Assert.False(File.Exists(output));
    }
}
