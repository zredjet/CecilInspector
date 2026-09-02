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
        var options = new DumpOptions(assembly, true, false, SymbolMode.Required, null, []);
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
        var options = new DumpOptions(assembly, true, false, SymbolMode.Off, null, []);

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
        var directory = Path.Combine(Path.GetTempPath(), $"cecil-inspector-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
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

        try
        {
            var options = new DumpOptions(path, true, false, SymbolMode.Off, null, []);
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
        finally
        {
            Directory.Delete(directory, true);
        }
    }

    [Fact]
    public void DumpUsesCanonicalGenericFormatting()
    {
        var assembly = typeof(MetadataDumperTests).Assembly.Location;
        var options = new DumpOptions(assembly, true, false, SymbolMode.Off, null, []);
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

    private sealed class FailingWriter : StringWriter
    {
        public override void WriteLine(string? value) => throw new IOException("disk full");
    }
}
