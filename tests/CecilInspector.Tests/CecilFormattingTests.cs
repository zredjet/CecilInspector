using CecilInspector.Core;
using Xunit;

namespace CecilInspector.Tests;

public sealed class CecilFormattingTests
{
    [Theory]
    [InlineData("Cache`1", "Cache")]
    [InlineData("Fixtures.Cache`1", "Fixtures.Cache")]
    [InlineData("Outer`1+Inner`2", "Outer+Inner")]
    [InlineData("System.Func`2<System.Int32, System.String>", "System.Func<System.Int32, System.String>")]
    [InlineData("Odd`Name", "Odd`Name")]
    [InlineData("Trailing`", "Trailing`")]
    public void WithoutArityStripsBacktickCounts(string name, string expected)
    {
        Assert.Equal(expected, CecilFormatting.WithoutArity(name));
    }

    [Theory]
    [InlineData("Plain")]
    [InlineData("Fixtures.Plain+Nested")]
    [InlineData("")]
    public void WithoutArityIsNullWhenThereIsNoArity(string name)
    {
        Assert.Null(CecilFormatting.WithoutArity(name));
    }
}
