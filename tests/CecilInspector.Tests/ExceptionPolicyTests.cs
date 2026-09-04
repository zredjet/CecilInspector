using CecilInspector.Core;
using Xunit;

namespace CecilInspector.Tests;

public sealed class ExceptionPolicyTests
{
    [Fact]
    public void UnwrapPrefersAWorkerFailureOverTheCancellationsItCaused()
    {
        var timeout = new SearchQueryException("timeout", new TimeoutException());
        var aggregate = new AggregateException(
            new OperationCanceledException(),
            new AggregateException(timeout),
            new OperationCanceledException());

        Assert.Same(timeout, ExceptionPolicy.Unwrap(aggregate));
    }

    [Fact]
    public void UnwrapPrefersAFatalFailureOverEverythingElse()
    {
        var fatal = new InsufficientMemoryException();
        var aggregate = new AggregateException(new SearchQueryException("timeout", new TimeoutException()), fatal);

        Assert.Same(fatal, ExceptionPolicy.Unwrap(aggregate));
    }

    [Fact]
    public void UnwrapReturnsACancellationWhenNothingElseFailed()
    {
        var first = new OperationCanceledException();
        var aggregate = new AggregateException(first, new OperationCanceledException());

        Assert.Same(first, ExceptionPolicy.Unwrap(aggregate));
        var empty = new AggregateException();
        Assert.Same(empty, ExceptionPolicy.Unwrap(empty));
    }

    [Fact]
    public void UserMessageFallsBackToInnerMessageThenTypeName()
    {
        Assert.Equal("outer", ExceptionPolicy.UserMessage(new BadImageFormatException("outer", new EndOfStreamException("inner"))));
        Assert.Equal("inner", ExceptionPolicy.UserMessage(new BadImageFormatException("", new EndOfStreamException("inner"))));
        Assert.Equal("BadImageFormatException", ExceptionPolicy.UserMessage(new BadImageFormatException(" ")));
    }
}
