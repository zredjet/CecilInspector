using System.Diagnostics;
using CecilInspector.Core;
using Xunit;

namespace CecilInspector.Tests;

public sealed class CliProcessTests
{
    private static readonly string ToolAssembly = typeof(AssemblySearcher).Assembly.Location;
    private static readonly string TestAssembly = typeof(CliProcessTests).Assembly.Location;

    [Fact]
    public async Task PartialScanUsesExitCodeThreeAndStderrDiagnostics()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"cecil-inspector-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        File.Copy(typeof(CliProcessTests).Assembly.Location, Path.Combine(directory, "good.dll"));
        File.WriteAllText(Path.Combine(directory, "bad.dll"), "not an assembly");
        try
        {
            var result = await RunAsync("search", directory, "NoUniqueMatchExpected", "--kind", "method", "--symbols", "off");

            Assert.Equal(3, result.ExitCode);
            Assert.Contains("警告:", result.StandardError, StringComparison.Ordinal);
            Assert.DoesNotContain("警告:", result.StandardOutput, StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(directory, true);
        }
    }

    [Theory]
    [InlineData("search")]
    [InlineData("dump")]
    public async Task SubcommandHelpExitsSuccessfully(string command)
    {
        var result = await RunAsync(command, "--help");

        Assert.Equal(0, result.ExitCode);
        Assert.Contains("使用方法", result.StandardOutput, StringComparison.Ordinal);
        Assert.Empty(result.StandardError);
    }

    [Fact]
    public async Task UnknownCommandWithHelpIsAnArgumentError()
    {
        var result = await RunAsync("typo", "--help");

        Assert.Equal(1, result.ExitCode);
        Assert.Empty(result.StandardOutput);
        Assert.Contains("不明なコマンド", result.StandardError, StringComparison.Ordinal);
        Assert.Contains("使用方法", result.StandardError, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ParseErrorWritesHelpOnlyToStandardError()
    {
        var result = await RunAsync("search", "a.dll", "Save", "--wat");

        Assert.Equal(1, result.ExitCode);
        Assert.Empty(result.StandardOutput);
        Assert.Contains("不明なオプション", result.StandardError, StringComparison.Ordinal);
        Assert.Contains("使用方法", result.StandardError, StringComparison.Ordinal);
    }

    [Fact]
    public async Task InvalidRegexIsAnArgumentError()
    {
        var result = await RunAsync("search", ToolAssembly, "[", "--match", "regex", "--symbols", "off");

        Assert.Equal(1, result.ExitCode);
        Assert.Empty(result.StandardOutput);
        Assert.Contains("正規表現が不正", result.StandardError, StringComparison.Ordinal);
    }

    [Fact]
    public async Task MissingOptionValueDoesNotConsumeFollowingOption()
    {
        var result = await RunAsync("search", "a.dll", "Save", "--output", "--symbols", "off");

        Assert.Equal(1, result.ExitCode);
        Assert.Empty(result.StandardOutput);
        Assert.Contains("--outputにはファイルパスが必要", result.StandardError, StringComparison.Ordinal);
        Assert.DoesNotContain("不明なオプション 'off'", result.StandardError, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RegexTimeoutIsAnArgumentError()
    {
        var result = await RunAsync(
            "search", TestAssembly, "^(?=.)(.+)+Z$", "--kind", "namespace", "--match", "regex", "--symbols", "off");

        Assert.Equal(1, result.ExitCode);
        Assert.Empty(result.StandardOutput);
        Assert.Contains("タイムアウト", result.StandardError, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("search")]
    [InlineData("dump")]
    public async Task InvalidReferencePathIsRejectedBeforeOutputIsCreated(string command)
    {
        var directory = Path.Combine(Path.GetTempPath(), $"cecil-inspector-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        try
        {
            var output = Path.Combine(directory, "out", "report.txt");
            var missing = Path.Combine(directory, "missing");
            string[] positional = command == "search" ? ["search", TestAssembly, "Estimate"] : ["dump", TestAssembly];

            var result = await RunAsync([
                .. positional, "--symbols", "off", "--output", output, "--reference-path", missing]);

            Assert.Equal(2, result.ExitCode);
            Assert.Empty(result.StandardOutput);
            Assert.Contains("依存アセンブリの検索フォルダ", result.StandardError, StringComparison.Ordinal);
            Assert.False(Directory.Exists(Path.Combine(directory, "out")));
            Assert.Empty(Directory.GetFiles(directory, "*.partial", SearchOption.AllDirectories));
        }
        finally
        {
            Directory.Delete(directory, true);
        }
    }

    [Fact]
    public async Task ExistingOutputIsRejectedBeforeSearching()
    {
        var existing = Path.GetTempFileName();
        try
        {
            var result = await RunAsync(
                "search", TestAssembly, "Estimate", "--symbols", "off", "--output", existing);

            Assert.Equal(2, result.ExitCode);
            Assert.Empty(result.StandardOutput);
            Assert.Contains("出力先は既に存在します", result.StandardError, StringComparison.Ordinal);
        }
        finally
        {
            File.Delete(existing);
        }
    }

    [Fact]
    public async Task CorruptPdbIsWarnedAboutButDoesNotFailTheScan()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"cecil-inspector-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        try
        {
            var assembly = Path.Combine(directory, "Target.dll");
            File.Copy(TestAssembly, assembly);
            File.WriteAllBytes(Path.Combine(directory, "Target.pdb"), "BSJB"u8.ToArray());

            var result = await RunAsync("search", assembly, "EstimateTarget", "--kind", "method", "--match", "exact");

            Assert.Equal(0, result.ExitCode);
            Assert.Contains("Matches: 1", result.StandardOutput, StringComparison.Ordinal);
            Assert.Contains("警告:", result.StandardError, StringComparison.Ordinal);
            Assert.Contains("シンボルなしで解析", result.StandardError, StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(directory, true);
        }
    }

    private static async Task<ProcessResult> RunAsync(params string[] arguments)
    {
        var startInfo = new ProcessStartInfo("dotnet")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        startInfo.ArgumentList.Add(ToolAssembly);
        foreach (var argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        using var process = Process.Start(startInfo) ?? throw new InvalidOperationException("dotnetを起動できませんでした。");
        var standardOutput = process.StandardOutput.ReadToEndAsync();
        var standardError = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();
        return new ProcessResult(process.ExitCode, await standardOutput, await standardError);
    }

    private sealed record ProcessResult(int ExitCode, string StandardOutput, string StandardError);
}
