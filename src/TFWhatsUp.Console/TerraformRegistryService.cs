using System.Net.Http.Json;
using System.Text.Json;
using Spectre.Console;

namespace TFWhatsUp.Console;

public class TerraformRegistryService
{
    public async Task<List<ProviderInfo>> GetGithubUrlsForProviders(List<ProviderInfo> providerInfoList)
    {
        var output = new OutputHelper();
        
        List<ProviderInfo> providersWithGitHubInfo = new();
        using var httpClient = new HttpClient();
        foreach (var provider in providerInfoList)
        {
            var url = $"https://registry.terraform.io/v1/providers/{provider.Vendor}/{provider.Name}";
            try
            {
                var result = await httpClient.GetFromJsonAsync<TerraformProviderResponse>(url);
                if (result is not null)
                {
                    var pathItems = new Uri(result.Source, UriKind.Absolute).AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries);
                    providersWithGitHubInfo.Add(provider with { GitHubOrg = pathItems[0], GitHubRepo = pathItems[1] });
                }
                else
                {
                    output.WriteWarning($"Terraform registry response was null for provider '{provider.Vendor}/{provider.Name}'");
                }
            }
            catch (HttpRequestException ex) // Non success
            {
                output.WriteError($"HTTP error when querying Terraform registry for provider '{provider.Vendor}/{provider.Name}'");
                AnsiConsole.WriteException(ex);
            }
            catch (NotSupportedException ex) // When content type is not valid
            {
                output.WriteError($"HTTP error when querying Terraform registry for provider '{provider.Vendor}/{provider.Name}'");
                AnsiConsole.WriteException(ex);
            }
            catch (JsonException ex) // Invalid JSON
            {
                output.WriteError($"Invalid JSON Response when querying Terraform registry for provider '{provider.Vendor}/{provider.Name}'");
                AnsiConsole.WriteException(ex);
            }
        }
        return providersWithGitHubInfo;
    }

}