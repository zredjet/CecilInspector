using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace CecilInspector.Output;

/// <summary>What a piece of report text is, so a style can color it consistently.</summary>
internal enum ReportPart
{
    Header,
    DefinitionLabel,
    ReferenceLabel,
    Symbol,
    Assembly,
    Container,
    Source,
    Il,
    Note,
    DumpType,
    DumpMember,
}

/// <summary>
/// Colors report parts with ANSI SGR sequences, or leaves text untouched. Only the text report
/// is styled: msbuild format is machine-readable and the --output file never receives escape
/// sequences (see <see cref="AnsiStrippingTextWriter"/>). User-controlled text has already been
/// through <see cref="TextSanitizer"/>, so every ESC in a styled stream is one of ours.
/// </summary>
internal sealed class ReportStyle
{
    public static readonly ReportStyle None = new(false);
    public static readonly ReportStyle Ansi = new(true);

    private const string Escape = "\u001b[";
    private const string Reset = "\u001b[0m";

    private readonly bool _enabled;

    private ReportStyle(bool enabled)
    {
        _enabled = enabled;
    }

    public bool IsEnabled => _enabled;

    public string Apply(ReportPart part, string text)
    {
        if (!_enabled || text.Length == 0)
        {
            return text;
        }

        var code = part switch
        {
            ReportPart.Header => "90",
            ReportPart.DefinitionLabel => "32",
            ReportPart.ReferenceLabel => "35",
            ReportPart.Symbol => "1",
            ReportPart.Assembly => "36",
            ReportPart.Container => "33",
            ReportPart.Source => "94",
            ReportPart.Il => "90",
            ReportPart.Note => "33",
            ReportPart.DumpType => "1;36",
            ReportPart.DumpMember => "33",
            _ => null,
        };
        return code is null ? text : $"{Escape}{code}m{text}{Reset}";
    }
}

/// <summary>
/// Decides whether the console gets colors and, on Windows, switches the console to virtual
/// terminal processing so the sequences are interpreted instead of printed.
/// </summary>
internal static partial class AnsiConsole
{
    /// <param name="mode">The --color value.</param>
    /// <param name="outputRedirected">True when stdout is not a terminal.</param>
    /// <param name="getEnvironmentVariable">Source of NO_COLOR and TERM.</param>
    public static bool ShouldColor(Cli.ColorMode mode, bool outputRedirected, Func<string, string?> getEnvironmentVariable) =>
        mode switch
        {
            Cli.ColorMode.Always => true,
            Cli.ColorMode.Never => false,
            _ => !outputRedirected &&
                 string.IsNullOrEmpty(getEnvironmentVariable("NO_COLOR")) &&
                 !string.Equals(getEnvironmentVariable("TERM"), "dumb", StringComparison.Ordinal),
        };

    /// <summary>Enables VT processing on Windows; a no-op elsewhere. False when the console refuses.</summary>
    public static bool TryEnableVirtualTerminal()
    {
        if (!OperatingSystem.IsWindows())
        {
            return true;
        }

        return TryEnableWindowsVirtualTerminal();
    }

    [SupportedOSPlatform("windows")]
    private static bool TryEnableWindowsVirtualTerminal()
    {
        const int StdOutputHandle = -11;
        const uint EnableVirtualTerminalProcessing = 0x0004;
        var handle = GetStdHandle(StdOutputHandle);
        if (handle == nint.Zero || handle == -1 || !GetConsoleMode(handle, out var mode))
        {
            return false;
        }

        return (mode & EnableVirtualTerminalProcessing) != 0 || SetConsoleMode(handle, mode | EnableVirtualTerminalProcessing);
    }

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static partial nint GetStdHandle(int handle);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetConsoleMode(nint handle, out uint mode);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SetConsoleMode(nint handle, uint mode);
}

/// <summary>
/// Removes ANSI SGR sequences (ESC [ ... m) before forwarding to the report file, so --output
/// stays plain text while the console is colored. A sequence may be split across writes.
/// </summary>
internal sealed class AnsiStrippingTextWriter(TextWriter inner) : TextWriter
{
    private const char EscapeChar = '\u001b';

    private bool _inSequence;

    public override System.Text.Encoding Encoding => inner.Encoding;

    public override void Write(char value) => Write(new ReadOnlySpan<char>(in value));

    public override void Write(char[] buffer, int index, int count) => Write(buffer.AsSpan(index, count));

    public override void Write(string? value) => Write(value.AsSpan());

    public override void Write(ReadOnlySpan<char> buffer)
    {
        var start = 0;
        for (var index = 0; index < buffer.Length; index++)
        {
            if (_inSequence)
            {
                if (buffer[index] == 'm')
                {
                    _inSequence = false;
                }

                start = index + 1;
            }
            else if (buffer[index] == EscapeChar)
            {
                inner.Write(buffer[start..index]);
                _inSequence = true;
                start = index + 1;
            }
        }

        if (start < buffer.Length)
        {
            inner.Write(buffer[start..]);
        }
    }

    public override void WriteLine() => inner.WriteLine();

    public override void WriteLine(string? value)
    {
        Write(value.AsSpan());
        inner.WriteLine();
    }

    public override void WriteLine(ReadOnlySpan<char> buffer)
    {
        Write(buffer);
        inner.WriteLine();
    }

    public override void Flush() => inner.Flush();

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            inner.Flush();
        }

        base.Dispose(disposing);
    }
}
