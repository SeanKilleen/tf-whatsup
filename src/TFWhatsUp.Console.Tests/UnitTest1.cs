using FluentAssertions;

namespace TFWhatsUp.Console.Tests;

public class UnitTest1
{
    [Fact]
    public void BasicTest_AdditionWorks()
    {
        (1 + 1).Should().Be(2);
    }
}