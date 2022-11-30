using FluentAssertions;

namespace TFWhatsUp.Console.Tests;

public class HashicorpApiVerificationTests
{
    [Fact]
    public void VerifyAzure()
    {
        (1 + 1).Should().Be(2);
    }
}