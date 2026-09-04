using CecilInspector.Cli;
using CecilInspector.Core;
using CecilInspector.Output;
using Mono.Cecil;
using Xunit;

namespace CecilInspector.Tests;

public sealed class MetadataDumperTests
{
    [Fact]
    public void StreamsMetadataToProvidedWriter()
    {
        var assembly = typeof(MetadataDumperTests).Assembly.Location;
        var options = new DumpOptions(assembly, Recursive: true, IncludeIl: false, SymbolMode.Required, null, []);
        using var writer = new StringWriter();

        var result = new MetadataDumper().Dump(
            options,
            [assembly],
            1,
            [Path.GetDirectoryName(assembly)!],
            [],
            writer);

        Assert.Equal(1, result.FilesSucceeded);
        Assert.Contains("Assembly: CecilInspector.Tests", writer.ToString(), StringComparison.Ordinal);
        Assert.Contains("Summary: discovered=1", writer.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void WriterFailureIsNotReportedAsAssemblyFailure()
    {
        var assembly = typeof(MetadataDumperTests).Assembly.Location;
        var options = new DumpOptions(assembly, Recursive: true, IncludeIl: false, SymbolMode.Off, null, []);

        Assert.Throws<ReportWriteException>(() =>
            new MetadataDumper().Dump(
                options,
                [assembly],
                1,
                [Path.GetDirectoryName(assembly)!],
                [],
                new FailingWriter()));
    }

    [Fact]
    public void EscapesControlAndUnicodeFormatCharactersFromMetadata()
    {
        using var temp = new TempDirectory();
        var directory = temp.Path;
        var path = Path.Combine(directory, "ControlChars.dll");
        using (var assembly = AssemblyDefinition.CreateAssembly(
                   new AssemblyNameDefinition("ControlChars", new Version(1, 0)),
                   "ControlChars",
                   ModuleKind.Dll))
        {
            assembly.MainModule.Types.Add(new TypeDefinition(
                "Fixtures",
                "Bad\n\u001b\u202E\u2028\u2029\U000E0001Name",
                TypeAttributes.Public));
            assembly.Write(path);
        }

        var options = new DumpOptions(path, Recursive: true, IncludeIl: false, SymbolMode.Off, null, []);
        using var writer = new StringWriter();
        new MetadataDumper().Dump(options, [path], 1, [directory], [], writer);

        var output = writer.ToString();
        Assert.DoesNotContain('\u001b', output);
        Assert.DoesNotContain('\u202E', output);
        Assert.DoesNotContain('\u2028', output);
        Assert.DoesNotContain('\u2029', output);
        Assert.DoesNotContain("\U000E0001", output, StringComparison.Ordinal);
        Assert.Contains("Bad\\n\\e\\u202E\\u2028\\u2029\\U000E0001Name", output, StringComparison.Ordinal);
    }

    [Fact]
    public void DumpUsesCanonicalGenericFormatting()
    {
        var assembly = typeof(MetadataDumperTests).Assembly.Location;
        var options = new DumpOptions(assembly, Recursive: true, IncludeIl: false, SymbolMode.Off, null, []);
        using var writer = new StringWriter();

        new MetadataDumper().Dump(options, [assembly], 1, [Path.GetDirectoryName(assembly)!], [], writer);

        var output = writer.ToString();
        Assert.Contains(
            "GenericSignatureFixture::TwoArgs() : System.Func`2<System.Int32, System.String>",
            output,
            StringComparison.Ordinal);
        Assert.Contains(
            "GenericContainer`1::Store(!0) : System.Void",
            output,
            StringComparison.Ordinal);
        Assert.DoesNotContain("`2<System.Int32,System.String>", output, StringComparison.Ordinal);
    }

    [Fact]
    public void IncludeIlEmitsInstructionsWithSourceLocations()
    {
        var assembly = typeof(MetadataDumperTests).Assembly.Location;
        var options = new DumpOptions(assembly, Recursive: true, IncludeIl: true, SymbolMode.Required, null, []);
        using var writer = new StringWriter();

        var result = new MetadataDumper().Dump(options, [assembly], 1, [Path.GetDirectoryName(assembly)!], [], writer);

        var output = writer.ToString();
        Assert.Equal(1, result.FilesSucceeded);
        Assert.Contains("Has symbols: True", output, StringComparison.Ordinal);
        Assert.Contains("SearchFixture::CallTarget() : System.Int32 [", output, StringComparison.Ordinal);
        Assert.Contains("IL_0000: ", output, StringComparison.Ordinal);
        Assert.Contains(" call System.Int32 CecilInspector.Tests.SearchFixture::EstimateTarget(System.Int32) // ",
            output, StringComparison.Ordinal);
        Assert.Contains("AssemblySearcherTests.cs:", output, StringComparison.Ordinal);
    }

    private sealed class FailingWriter : StringWriter
    {
        public override void WriteLine(string? value) => throw new IOException("disk full");
    }

    [Fact]
    public void NoSymbolsMeansNoStateMachineResolution()
    {
        // Without a PDB the async fallback (custom attribute decode plus a Resolve() of the
        // state machine type per method) has nothing to find and must not run at all.
        var assembly = typeof(MetadataDumperTests).Assembly.Location;
        using var resolver = CecilResolverFactory.Create(assembly, [], [Path.GetDirectoryName(assembly)!]);
        using var module = CecilModuleReader.Read(assembly, SymbolMode.Off, resolver, out _);
        var asyncMethod = module.GetType("CecilInspector.Tests.SearchFixture").Methods.Single(method => method.Name == "AsyncTarget");

        var location = DebugLocations.First(asyncMethod);

        Assert.Null(location);
        Assert.Equal(0, resolver.ProbeCount);
    }

    [Fact]
    public void AnsiStyleColorsAssemblyFileTypeAndMethodLines()
    {
        var assembly = typeof(MetadataDumperTests).Assembly.Location;
        var options = new DumpOptions(assembly, false, false, SymbolMode.Off, null, []);
        var esc = ((char)27).ToString();
        using var writer = new StringWriter();

        new MetadataDumper(ReportStyle.Ansi).Dump(options, [assembly], 1, [Path.GetDirectoryName(assembly)!], [], writer);

        var text = writer.ToString();
        Assert.Contains(esc + "[1mAssembly: CecilInspector.Tests", text, StringComparison.Ordinal);
        Assert.Contains(esc + "[36mFile: ", text, StringComparison.Ordinal);
        Assert.Contains(esc + "[1;36mType: CecilInspector.Tests.SearchFixture" + esc + "[0m", text, StringComparison.Ordinal);
        Assert.Contains(esc + "[33mMethod: ", text, StringComparison.Ordinal);
    }
}
