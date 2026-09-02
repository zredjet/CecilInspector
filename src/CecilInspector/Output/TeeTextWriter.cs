using System.Text;

namespace CecilInspector.Output;

internal sealed class TeeTextWriter(TextWriter first, TextWriter second) : TextWriter
{
    public override Encoding Encoding => first.Encoding;

    public override void Write(char value)
    {
        first.Write(value);
        second.Write(value);
    }

    public override void Write(string? value)
    {
        first.Write(value);
        second.Write(value);
    }

    public override void WriteLine(string? value)
    {
        first.WriteLine(value);
        second.WriteLine(value);
    }

    public override void Flush()
    {
        first.Flush();
        second.Flush();
    }
}

internal sealed class GuardedTextWriter(TextWriter inner) : TextWriter
{
    public override Encoding Encoding => inner.Encoding;

    public override void Write(char value) => Guard(() => inner.Write(value));

    public override void Write(string? value) => Guard(() => inner.Write(value));

    public override void WriteLine(string? value) => Guard(() => inner.WriteLine(value));

    public override void WriteLine() => Guard(inner.WriteLine);

    public override void Flush() => Guard(inner.Flush);

    private static void Guard(Action action)
    {
        try
        {
            action();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ObjectDisposedException)
        {
            throw new ReportWriteException("レポートの書き込みに失敗しました。", ex);
        }
    }
}

public sealed class ReportWriteException : IOException
{
    public ReportWriteException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
