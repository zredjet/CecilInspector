using CecilInspector.Cli;
using CecilInspector.Core;
using System.Diagnostics;
using System.Globalization;
using System.Text.RegularExpressions;
using Xunit;

namespace CecilInspector.Tests;

public sealed class CliProcessTests
{
    private static readonly string ToolAssembly = typeof(AssemblySearcher).Assembly.Location;
    private static readonly string TestAssembly = typeof(CliProcessTests).Assembly.Location;

    [Fact]
    public async Task PartialScanUsesExitCodeThreeAndStderrDiagnostics()
    {
        using var temp = new TempDirectory();
        var directory = temp.Path;
        File.Copy(typeof(CliProcessTests).Assembly.Location, Path.Combine(directory, "good.dll"));
        File.WriteAllText(Path.Combine(directory, "bad.dll"), "not an assembly");
        var result = await RunAsync("search", directory, "NoUniqueMatchExpected", "--kind", "method", "--symbols", "off");

        Assert.Equal(3, result.ExitCode);
        Assert.Contains("警告:", result.StandardError, StringComparison.Ordinal);
        Assert.DoesNotContain("警告:", result.StandardOutput, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("search")]
    [InlineData("dump")]
    public async Task QuietReplacesDiagnosticsWithOneSummaryLineAndKeepsTheExitCode(string command)
    {
        using var temp = new TempDirectory();
        File.Copy(TestAssembly, temp.File("good.dll"));
        File.WriteAllText(temp.File("bad.dll"), "not an assembly");
        string[] positional = command == "search" ? ["search", temp.Path, "Estimate"] : ["dump", temp.Path];

        var result = await RunAsync([.. positional, "--symbols", "off", "--quiet"]);

        Assert.Equal(3, result.ExitCode);
        var line = Assert.Single(result.StandardError.Split('\n', StringSplitOptions.RemoveEmptyEntries));
        Assert.StartsWith("警告: 1 件の警告と 0 件の情報を --quiet で省略しました。", line, StringComparison.Ordinal);
        Assert.DoesNotContain("bad.dll", result.StandardError, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ColorAlwaysColorsTheConsoleButNeverTheReportFile()
    {
        using var temp = new TempDirectory();
        var output = temp.File("report.txt");

        var result = await RunAsync(
            "search", TestAssembly, "EstimateTarget", "--kind", "method", "--match", "exact", "--scope", "all",
            "--color", "always", "--output", output);

        Assert.Equal(0, result.ExitCode);
        Assert.Contains("\u001b[36m  assembly: ", result.StandardOutput, StringComparison.Ordinal);
        Assert.Contains("\u001b[33m  in: ", result.StandardOutput, StringComparison.Ordinal);
        Assert.Contains("\u001b[90m  il: IL_", result.StandardOutput, StringComparison.Ordinal);
        var file = File.ReadAllText(output);
        Assert.DoesNotContain('\u001b', file);
        Assert.Equal(file, result.StandardOutput.Replace("\u001b[0m", "", StringComparison.Ordinal)
            .Replace("\u001b[36m", "", StringComparison.Ordinal).Replace("\u001b[33m", "", StringComparison.Ordinal)
            .Replace("\u001b[90m", "", StringComparison.Ordinal).Replace("\u001b[94m", "", StringComparison.Ordinal)
            .Replace("\u001b[32m", "", StringComparison.Ordinal).Replace("\u001b[35m", "", StringComparison.Ordinal)
            .Replace("\u001b[1m", "", StringComparison.Ordinal));
    }

    [Fact]
    public async Task RedirectedOutputIsNotColoredByDefault()
    {
        var result = await RunAsync("search", TestAssembly, "EstimateTarget", "--kind", "method", "--match", "exact", "--symbols", "off");

        Assert.Equal(0, result.ExitCode);
        Assert.DoesNotContain('\u001b', result.StandardOutput);
    }

    [Fact]
    public async Task QuietPrintsNothingOnStderrWhenThereIsNothingToReport()
    {
        var result = await RunAsync("search", TestAssembly, "EstimateTarget", "--kind", "method", "--symbols", "off", "-q");

        Assert.Equal(0, result.ExitCode);
        Assert.Empty(result.StandardError);
        Assert.Contains("Matches: 1", result.StandardOutput, StringComparison.Ordinal);
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
        // A 64-character namespace makes the backtracking fallback exceed its 250 ms budget on
        // any machine; the test assembly's own namespaces are short enough to be speed-dependent.
        using var temp = new TempDirectory();
        var assembly = temp.File("LongNamespace.dll");
        GeneratedAssemblies.WriteTypeInNamespace(assembly, new string('a', 64));

        var result = await RunAsync(
            "search", assembly, "^(?=.)(.+)+Z$", "--kind", "namespace", "--match", "regex", "--symbols", "off");

        Assert.Equal(1, result.ExitCode);
        Assert.Empty(result.StandardOutput);
        Assert.Contains("タイムアウト", result.StandardError, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("search")]
    [InlineData("dump")]
    public async Task InvalidReferencePathIsRejectedBeforeOutputIsCreated(string command)
    {
        using var temp = new TempDirectory();
        var directory = temp.Path;
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

    [Fact]
    public async Task ExistingOutputIsRejectedBeforeSearching()
    {
        using var temp = new TempDirectory();
        var existing = temp.File("existing.txt");
        File.WriteAllText(existing, "keep");

        var result = await RunAsync(
            "search", TestAssembly, "Estimate", "--symbols", "off", "--output", existing);

        Assert.Equal(2, result.ExitCode);
        Assert.Empty(result.StandardOutput);
        Assert.Contains("出力先は既に存在します", result.StandardError, StringComparison.Ordinal);
        Assert.Equal("keep", File.ReadAllText(existing));
    }

    [Fact]
    public async Task CorruptPdbIsWarnedAboutButDoesNotFailTheScan()
    {
        using var temp = new TempDirectory();
        var directory = temp.Path;
        var assembly = Path.Combine(directory, "Target.dll");
        File.Copy(TestAssembly, assembly);
        File.WriteAllBytes(Path.Combine(directory, "Target.pdb"), "BSJB"u8.ToArray());

        var result = await RunAsync("search", assembly, "EstimateTarget", "--kind", "method", "--match", "exact");

        Assert.Equal(0, result.ExitCode);
        Assert.Contains("Matches: 1", result.StandardOutput, StringComparison.Ordinal);
        Assert.Contains("情報:", result.StandardError, StringComparison.Ordinal);
        Assert.DoesNotContain("警告:", result.StandardError, StringComparison.Ordinal);
        Assert.Contains("シンボルなしで解析", result.StandardError, StringComparison.Ordinal);
    }

    [Fact]
    public async Task VersionPrintsProductVersion()
    {
        var result = await RunAsync("--version");

        Assert.Equal(0, result.ExitCode);
        Assert.Equal($"cecil-inspector {CommandLine.VersionText}", result.StandardOutput.TrimEnd());
        Assert.Empty(result.StandardError);
    }

    [Fact]
    public async Task MsBuildFormatPrintsNavigableOrigins()
    {
        var result = await RunAsync(
            "search", TestAssembly, "EstimateTarget", "--kind", "method", "--match", "exact", "--scope", "all",
            "--format", "msbuild");

        Assert.Equal(0, result.ExitCode);
        Assert.Matches(
            new Regex(@"^.+\(\d+,\d+\): info CI0001: \[definition/method\] ", RegexOptions.Multiline),
            result.StandardOutput);
        Assert.Matches(
            new Regex(@"^.+\(\d+,\d+\): info CI0002: \[reference/method\] .* \(in .*\) @ IL_[0-9A-F]{4}\r?$", RegexOptions.Multiline),
            result.StandardOutput);
        Assert.DoesNotContain("  source:", result.StandardOutput, StringComparison.Ordinal);
    }

    [Fact]
    public async Task MissingInputIsAnInputError()
    {
        using var temp = new TempDirectory();

        var result = await RunAsync("search", temp.File("missing.dll"), "Save");

        Assert.Equal(2, result.ExitCode);
        Assert.Empty(result.StandardOutput);
        Assert.Contains("入力パスが見つかりません", result.StandardError, StringComparison.Ordinal);
    }

    [Fact]
    public async Task MaxResultsTruncatesReportAndKeepsTotal()
    {
        var result = await RunAsync(
            "search", TestAssembly, "Estimate", "--kind", "all", "--max-results", "1", "--symbols", "off");

        Assert.Equal(0, result.ExitCode);
        Assert.Contains("--max-resultsで変更できます", result.StandardOutput, StringComparison.Ordinal);
        Assert.Equal(1, result.StandardOutput.Split("[definition/").Length - 1);
        var total = Regex.Match(result.StandardOutput, @"^Matches: (\d+)\r?$", RegexOptions.Multiline);
        Assert.True(total.Success, result.StandardOutput);
        Assert.True(int.Parse(total.Groups[1].Value, CultureInfo.InvariantCulture) > 1, total.Value);
    }

    [Fact]
    public async Task OutputFileMatchesConsoleReport()
    {
        using var temp = new TempDirectory();
        var output = temp.File("report.txt");

        var result = await RunAsync(
            "search", TestAssembly, "EstimateTarget", "--kind", "method", "--match", "exact", "--symbols", "off",
            "--output", output);

        Assert.Equal(0, result.ExitCode);
        Assert.Equal(result.StandardOutput, File.ReadAllText(output));
        Assert.Contains("Matches: 1", result.StandardOutput, StringComparison.Ordinal);
    }

    [Fact]
    public async Task HelpExampleWithOptionsAfterSeparatorRuns()
    {
        var result = await RunAsync("search", TestAssembly, "--", "-Prefixed", "--match", "exact", "--symbols", "off");

        Assert.Equal(0, result.ExitCode);
        Assert.Contains("Query: -Prefixed", result.StandardOutput, StringComparison.Ordinal);
        Assert.Contains("Match: Exact", result.StandardOutput, StringComparison.Ordinal);
        Assert.Empty(result.StandardError);
    }

    [Fact]
    public async Task SeparatorIsNotWrittenAsAReportFile()
    {
        using var temp = new TempDirectory();

        var result = await RunAsync(new RunOptions(WorkingDirectory: temp.Path), "dump", TestAssembly, "--output", "--");

        Assert.Equal(1, result.ExitCode);
        Assert.Contains("--outputにはファイルパスが必要", result.StandardError, StringComparison.Ordinal);
        Assert.Empty(Directory.GetFileSystemEntries(temp.Path));
    }

    [Fact]
    public async Task EmptyOutputPathIsAnArgumentError()
    {
        var result = await RunAsync("search", TestAssembly, "Estimate", "--symbols", "off", "--output", "");

        Assert.Equal(1, result.ExitCode);
        Assert.Empty(result.StandardOutput);
        Assert.Contains("--outputにはファイルパスが必要", result.StandardError, StringComparison.Ordinal);
        Assert.DoesNotContain("Parameter", result.StandardError, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RepeatedKindsAreCombined()
    {
        var result = await RunAsync(
            "search", TestAssembly, "EstimateTarget", "--kind", "method", "--kind=type", "--match", "exact", "--symbols", "off");

        Assert.Equal(0, result.ExitCode);
        Assert.Contains("Kinds: Type, Method", result.StandardOutput, StringComparison.Ordinal);
        Assert.Contains("Matches: 1", result.StandardOutput, StringComparison.Ordinal);
    }

    [Fact]
    public async Task NoArgumentsIsAnArgumentError()
    {
        var result = await RunAsync();

        Assert.Equal(1, result.ExitCode);
        Assert.Empty(result.StandardOutput);
        Assert.Contains("エラー:", result.StandardError, StringComparison.Ordinal);
        Assert.Contains("使用方法", result.StandardError, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AllTargetsFailingUsesExitCodeTwo()
    {
        using var temp = new TempDirectory();
        File.WriteAllText(temp.File("bad.dll"), "not an assembly");

        var result = await RunAsync("search", temp.Path, "Anything", "--symbols", "off");

        Assert.Equal(2, result.ExitCode);
        Assert.Contains("Assemblies: 0/1 succeeded", result.StandardOutput, StringComparison.Ordinal);
        Assert.Contains("警告: ", result.StandardError, StringComparison.Ordinal);
        Assert.DoesNotContain("情報: ", result.StandardError, StringComparison.Ordinal);
    }

    [Fact]
    public async Task DebugEnvironmentPrintsTheExceptionBehindAWarning()
    {
        using var temp = new TempDirectory();
        File.WriteAllText(temp.File("bad.dll"), "not an assembly");
        var environment = new Dictionary<string, string> { ["CECIL_INSPECTOR_DEBUG"] = "1" };

        var plain = await RunAsync("dump", temp.Path, "--symbols", "off");
        var debug = await RunAsync(new RunOptions(Environment: environment), "dump", temp.Path, "--symbols", "off");

        Assert.Equal(2, plain.ExitCode);
        Assert.Equal(2, debug.ExitCode);
        Assert.DoesNotContain("Exception", plain.StandardError, StringComparison.Ordinal);
        Assert.Contains("Exception", debug.StandardError, StringComparison.Ordinal);
        Assert.Contains("\n    ", debug.StandardError.ReplaceLineEndings("\n"), StringComparison.Ordinal);
    }

    [Fact]
    public async Task InterruptedRunLeavesNoPartialReport()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "SIGTERM を送る手段が無く、Ctrl-C は同じコンソールの全プロセスへ届く。");

        using var temp = new TempDirectory();
        var output = temp.File("report.txt");
        var input = System.Runtime.InteropServices.RuntimeEnvironment.GetRuntimeDirectory();
        var cancellationToken = TestContext.Current.CancellationToken;
        using var process = Start("dump", input, "--include-il", "--symbols", "off", "--output", output);
        var standardOutput = process.StandardOutput.ReadToEndAsync(cancellationToken);
        var standardError = process.StandardError.ReadToEndAsync(cancellationToken);

        // Wait until the report is being written, then interrupt while the dump is still running.
        var started = DateTime.UtcNow;
        while (Directory.GetFiles(temp.Path, "*.partial").Length == 0)
        {
            if (process.HasExited)
            {
                // Awaiting stderr only here: doing so in the message would wait for the process to end.
                Assert.Fail($"ダンプが中断前に終了した (exit={process.ExitCode}): {await standardError}");
            }

            Assert.True(DateTime.UtcNow - started < ProcessTimeout, "一時ファイルが作られなかった。");
            await Task.Delay(20, cancellationToken);
        }

        using (var kill = Process.Start("kill", ["-TERM", process.Id.ToString(System.Globalization.CultureInfo.InvariantCulture)]))
        {
            Assert.NotNull(kill);
            await kill.WaitForExitAsync(cancellationToken);
        }

        await WaitForExitAsync(process, ["dump", input]);

        Assert.Equal(130, process.ExitCode);
        Assert.Contains("中断しました", await standardError, StringComparison.Ordinal);
        Assert.False(File.Exists(output));
        Assert.Empty(Directory.GetFiles(temp.Path, "*.partial"));
        _ = await standardOutput;
    }

    private static Task<ProcessResult> RunAsync(params string[] arguments) => RunAsync(new RunOptions(), arguments);

    private static async Task<ProcessResult> RunAsync(RunOptions options, params string[] arguments)
    {
        using var process = Start(options, arguments);
        var standardOutput = process.StandardOutput.ReadToEndAsync();
        var standardError = process.StandardError.ReadToEndAsync();
        await WaitForExitAsync(process, arguments);
        return new ProcessResult(process.ExitCode, await standardOutput, await standardError);
    }

    private static Process Start(params string[] arguments) => Start(new RunOptions(), arguments);

    private static Process Start(RunOptions options, params string[] arguments)
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

        startInfo.Environment["DOTNET_NOLOGO"] = "1";
        startInfo.Environment["DOTNET_CLI_TELEMETRY_OPTOUT"] = "1";
        startInfo.Environment.Remove("CECIL_INSPECTOR_DEBUG");
        foreach (var (name, value) in options.Environment ?? new Dictionary<string, string>())
        {
            startInfo.Environment[name] = value;
        }

        if (options.WorkingDirectory is not null)
        {
            startInfo.WorkingDirectory = options.WorkingDirectory;
        }

        return Process.Start(startInfo) ?? throw new InvalidOperationException("dotnetを起動できませんでした。");
    }

    private static async Task WaitForExitAsync(Process process, string[] arguments)
    {
        using var timeout = new CancellationTokenSource(ProcessTimeout);
        try
        {
            await process.WaitForExitAsync(timeout.Token);
        }
        catch (OperationCanceledException)
        {
            process.Kill(entireProcessTree: true);
            throw new TimeoutException(
                $"cecil-inspector が{ProcessTimeout.TotalSeconds:0}秒以内に終了しませんでした: {string.Join(' ', arguments)}");
        }
    }

    private static readonly TimeSpan ProcessTimeout = TimeSpan.FromSeconds(60);

    private sealed record RunOptions(string? WorkingDirectory = null, IReadOnlyDictionary<string, string>? Environment = null);

    private sealed record ProcessResult(int ExitCode, string StandardOutput, string StandardError);
}
