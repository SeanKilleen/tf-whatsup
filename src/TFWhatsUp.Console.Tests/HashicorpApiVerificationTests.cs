using FluentAssertions;
using VerifyTests;

namespace TFWhatsUp.Console.Tests;

[UsesVerify]
public class HashicorpApiVerificationTests
{
    [Fact]
    public async Task VerifyAzureRM()
    {
        var tfReg = new TerraformRegistryService();

        var providerInfo = new List<ProviderInfo>()
        {
            new("hashicorp", "azurerm", string.Empty, string.Empty, string.Empty, string.Empty)
        };

        var result = await tfReg.GetGithubUrlsForProviders(providerInfo);

        result.Count.Should().Be(1);

        var item = result.First();

        item.Name.Should().Be("azurerm");
        item.Vendor.Should().Be("hashicorp");
        item.GitHubOrg.Should().Be("hashicorp");
        item.GitHubRepo.Should().Be("terraform-provider-azurerm");

        // Will tell us if any of the structure of the the response changes.
        await Verify(item);
    }
}