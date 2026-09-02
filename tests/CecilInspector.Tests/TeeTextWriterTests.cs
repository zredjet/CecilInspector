using CecilInspector.Output;
using Xunit;

namespace CecilInspector.Tests;

public sealed class TeeTextWriterTests
{
    [Fact]
    public void EveryWriteReachesBothWriters()
    {
        using var first = new StringWriter();
        using var second = new StringWriter();
        using var tee = new TeeTextWriter(first, second);

        tee.Write('a');
        tee.Write("bc");
        tee.Write("xdefx".AsSpan(1, 3));
        tee.Write(['g', 'h', 'i'], 1, 2);
        tee.WriteLine();
        tee.WriteLine("line");
        tee.WriteLine("span".AsSpan());
        tee.Write($"{1 + 1}");
        tee.Flush();

        var expected = $"abcdefhi{Environment.NewLine}line{Environment.NewLine}span{Environment.NewLine}2";
        Assert.Equal(expected, first.ToString());
        Assert.Equal(expected, second.ToString());
        Assert.Equal(first.Encoding, tee.Encoding);
    }

    [Fact]
    public void GuardedWriterWrapsIoFailures()
    {
        using var guarded = new GuardedTextWriter(new ThrowingWriter(new IOException("disk full")));

        var error = Assert.Throws<ReportWriteException>(() => guarded.WriteLine("x"));

        Assert.IsType<IOException>(error.InnerException);
        Assert.Throws<ReportWriteException>(() => guarded.Write('x'));
        Assert.Throws<ReportWriteException>(() => guarded.Write("x".AsSpan()));
        Assert.Throws<ReportWriteException>(guarded.Flush);
    }

    [Fact]
    public void GuardedWriterLetsOtherExceptionsThrough()
    {
        using var guarded = new GuardedTextWriter(new ThrowingWriter(new InvalidOperationException("bug")));

        Assert.Throws<InvalidOperationException>(() => guarded.WriteLine("x"));
    }

    [Fact]
    public void GuardedWriterForwardsSuccessfulWrites()
    {
        using var inner = new StringWriter();
        using var guarded = new GuardedTextWriter(inner);

        guarded.Write("a");
        guarded.WriteLine();
        guarded.WriteLine("b".AsSpan());

        Assert.Equal($"a{Environment.NewLine}b{Environment.NewLine}", inner.ToString());
    }

    private sealed class ThrowingWriter(Exception exception) : StringWriter
    {
        public override void Write(char value) => throw exception;

        public override void Write(string? value) => throw exception;

        public override void Write(ReadOnlySpan<char> buffer) => throw exception;

        public override void WriteLine(string? value) => throw exception;

        public override void Flush() => throw exception;
    }
}
