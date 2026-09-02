using System.Text;

namespace CecilInspector.Output;

/// <summary>
/// Writes everything to two writers (console and report file). The span/array overloads are
/// forwarded directly so the base class does not decompose them into per-character calls.
/// </summary>
internal sealed class TeeTextWriter(TextWriter first, TextWriter second) : TextWriter
{
    public override Encoding Encoding => first.Encoding;

    public override void Write(char value)
    {
        first.Write(value);
        second.Write(value);
    }

    public override void Write(char[] buffer, int index, int count)
    {
        first.Write(buffer, index, count);
        second.Write(buffer, index, count);
    }

    public override void Write(ReadOnlySpan<char> buffer)
    {
        first.Write(buffer);
        second.Write(buffer);
    }

    public override void Write(string? value)
    {
        first.Write(value);
        second.Write(value);
    }

    public override void WriteLine()
    {
        first.WriteLine();
        second.WriteLine();
    }

    public override void WriteLine(string? value)
    {
        first.WriteLine(value);
        second.WriteLine(value);
    }

    public override void WriteLine(ReadOnlySpan<char> buffer)
    {
        first.WriteLine(buffer);
        second.WriteLine(buffer);
    }

    public override void Flush()
    {
        first.Flush();
        second.Flush();
    }
}

/// <summary>
/// Converts I/O failures of the underlying writer into <see cref="ReportWriteException"/> so the
/// analysis loop can tell "the report cannot be written" apart from "this assembly is broken".
/// </summary>
internal sealed class GuardedTextWriter(TextWriter inner) : TextWriter
{
    public override Encoding Encoding => inner.Encoding;

    public override void Write(char value) => Guard(() => inner.Write(value));

    public override void Write(char[] buffer, int index, int count) => Guard(() => inner.Write(buffer, index, count));

    public override void Write(ReadOnlySpan<char> buffer)
    {
        try
        {
            inner.Write(buffer);
        }
        catch (Exception ex) when (IsWriteFailure(ex))
        {
            throw Wrap(ex);
        }
    }

    public override void Write(string? value) => Guard(() => inner.Write(value));

    public override void WriteLine() => Guard(inner.WriteLine);

    public override void WriteLine(string? value) => Guard(() => inner.WriteLine(value));

    public override void WriteLine(ReadOnlySpan<char> buffer)
    {
        try
        {
            inner.WriteLine(buffer);
        }
        catch (Exception ex) when (IsWriteFailure(ex))
        {
            throw Wrap(ex);
        }
    }

    public override void Flush() => Guard(inner.Flush);

    private static void Guard(Action action)
    {
        try
        {
            action();
        }
        catch (Exception ex) when (IsWriteFailure(ex))
        {
            throw Wrap(ex);
        }
    }

    private static bool IsWriteFailure(Exception exception) =>
        exception is IOException or UnauthorizedAccessException or ObjectDisposedException;

    private static ReportWriteException Wrap(Exception exception) =>
        new("レポートの書き込みに失敗しました。", exception);
}

public sealed class ReportWriteException : IOException
{
    public ReportWriteException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
