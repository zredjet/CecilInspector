using CecilInspector.Output;
using Xunit;

namespace CecilInspector.Tests;

public sealed class OutputFileTests
{
    [Fact]
    public void RefusesToOverwriteAnyExistingFile()
    {
        var existing = Path.GetTempFileName();
        try
        {
            var error = Assert.Throws<ArgumentException>(() => OutputFile.WriteIfRequested(existing, "x"));
            Assert.Contains("上書きしません", error.Message, StringComparison.Ordinal);
        }
        finally
        {
            File.Delete(existing);
        }
    }

    [Fact]
    public void RefusesAssemblyExtensionEvenWhenFileDoesNotExist()
    {
        var output = Path.Combine(Path.GetTempPath(), $"cecil-inspector-{Guid.NewGuid():N}.dll");

        var error = Assert.Throws<ArgumentException>(() => OutputFile.WriteIfRequested(output, "x"));

        Assert.Contains("アセンブリ拡張子", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void WritesNewReportFile()
    {
        var output = Path.Combine(Path.GetTempPath(), $"cecil-inspector-{Guid.NewGuid():N}.txt");
        try
        {
            OutputFile.WriteIfRequested(output, "report");
            Assert.Equal("report", File.ReadAllText(output));
        }
        finally
        {
            if (File.Exists(output))
            {
                File.Delete(output);
            }
        }
    }

    [Fact]
    public void DoesNotPublishUncommittedReport()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"cecil-inspector-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        var output = Path.Combine(directory, "report.txt");
        try
        {
            using (var report = OutputFile.OpenAtomic(output))
            {
                Assert.NotNull(report);
                report.Writer.Write("partial");
            }

            Assert.False(File.Exists(output));
            Assert.Empty(Directory.GetFiles(directory));
        }
        finally
        {
            Directory.Delete(directory, true);
        }
    }

    [Fact]
    public void RefusesToCommitWhenPartialPathIsReplacedBySymbolicLink()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"cecil-inspector-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        var output = Path.Combine(directory, "report.txt");
        var attacker = Path.Combine(directory, "attacker.txt");
        File.WriteAllText(attacker, "attacker-controlled");
        try
        {
            using (var report = OutputFile.OpenAtomic(output))
            {
                Assert.NotNull(report);
                report.Writer.Write("real report");
                report.Writer.Flush();

                var partial = Assert.Single(Directory.GetFiles(directory, "*.partial"));
                File.Move(partial, partial + ".real");
                File.CreateSymbolicLink(partial, attacker);

                var error = Assert.Throws<IOException>(report.Commit);
                Assert.Contains("差し替え", error.Message, StringComparison.Ordinal);
            }

            Assert.False(File.Exists(output));
        }
        finally
        {
            Directory.Delete(directory, true);
        }
    }
}
