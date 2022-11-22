using System.ComponentModel;
using System.Diagnostics.CodeAnalysis;
using System.Text;
using Octokit;
using Octopus.CoreParsers.Hcl;
using Semver;
using Spectre.Console;
using Spectre.Console.Cli;
using Sprache;
using System.Net.Http.Json;
using System.Text.Json;

var app = new CommandApp<WhatsUpCommand>();
return app.Run(args);

public record ProviderInfo(string Vendor, string Name, string UrlToLatest, string Version, string GitHubOrg, string GitHubRepo);

public record ReleaseInfo(string OrgName, string RepoName, SemVersion Version, DateTimeOffset CreatedOn);

public record ReleaseInfoWithBody(ReleaseInfo ReleaseInfo, string Body);
public static class ExitCodes
{
    public const int UNKNOWN_ERROR = 1;
    public const int LOCKFILE_NOT_FOUND = 2;
    public const int TERRAFORM_FILES_NOT_FOUND = 3;
}

internal sealed class WhatsUpCommand : AsyncCommand<WhatsUpCommand.Settings>
{
    public sealed class Settings : CommandSettings
    {
        [Description("Path where your Terraform is located. Defaults to current directory.")]
        [CommandArgument(0, "[tfFilesPath]")]
        public string? TerraformFilesPath { get; init; }
        
        [CommandOption("-t|--github-api-token")]
        public string? GitHubApiToken { get; set; }
    }
    
    public override async Task<int> ExecuteAsync([NotNull] CommandContext context, [NotNull] Settings settings)
    {
        var StartDirectory = settings.TerraformFilesPath ?? Directory.GetCurrentDirectory();
        
        // TODO: Wrap API calls in methods that detect API rate limits and write messages that suggest using a token
        var apiClient = CreateOctokitApiClient(settings.GitHubApiToken ?? string.Empty);

        var lockfileLocation = Path.Combine(StartDirectory, ".terraform.lock.hcl");
        if (!Path.Exists(lockfileLocation))
        {
            WriteError($"Lock file not found at {lockfileLocation}");
            return ExitCodes.LOCKFILE_NOT_FOUND;
        }
        string lockfileContents = await File.ReadAllTextAsync(lockfileLocation);
        
        var allTerraformFiles = Directory.GetFiles(StartDirectory, "*.tf", SearchOption.AllDirectories);
        if (!allTerraformFiles.Any())
        {
            WriteError($"Exiting: No Terraform files found in '{StartDirectory}'");
            return ExitCodes.TERRAFORM_FILES_NOT_FOUND;
        }

        var tfFilesTable = GenerateTFFilesTable(allTerraformFiles);
        AnsiConsole.Write(tfFilesTable);
        
        var parsedLockFile = HclParser.HclTemplate.Parse(lockfileContents);
        
        var providerInfoList = ExtractProviderInfoFromParsedLockFile(parsedLockFile);

        var providerTable = GenerateProviderTable(providerInfoList);
        AnsiConsole.Write(providerTable);
        
        AnsiConsole.WriteLine("Concatenating TF files");
        var concatenatedTerraformFiles = ConcatenateTfFiles(allTerraformFiles);
        
        var parsedTerraformFiles = HclParser.HclTemplate.Parse(concatenatedTerraformFiles);

        var uniqueResourceNames = ExtractResourceTypesFromParsedTerraformFile(parsedTerraformFiles);
        var uniqueDataNames = ExtractDataTypesFromParsedTerraformFile(parsedTerraformFiles);
        var totalTypes = uniqueResourceNames.Union(uniqueDataNames).Order().ToList();

        var resourceTypesTable = GenerateResourceTypesTable(totalTypes);
        AnsiConsole.Write(resourceTypesTable);

        List<ProviderInfo> providersWithGitHubInfo = new();
        
        await AnsiConsole.Status()
            .Spinner(Spinner.Known.Earth)
            .StartAsync("Determining GitHub URLs...", async ctx =>
            {
                providersWithGitHubInfo = await GetGithubUrlsForProviders(providerInfoList);
            });

        var ghUrlTable = GenerateGitHubUrlTable(providersWithGitHubInfo);
        AnsiConsole.Write(ghUrlTable);

        
        // TODO: Split this into a function that returns the data, to hide behind a spinner. Then format the table as applicable once it's done.
        foreach (var provider in providersWithGitHubInfo)
        {
            var matchingRelease = await GetMatchingGitHubRelease(apiClient, provider.GitHubOrg, provider.GitHubRepo, provider.Version);
            if (matchingRelease is null)
            {
                WriteWarning($"Could not find/parse a matching release for {provider.Name} - {provider.Version}. Skipping it.");
                continue;
            }

            var allReleases = await apiClient.Repository.Release.GetAll(provider.GitHubOrg, provider.GitHubRepo);

            var applicableReleases = GetApplicableReleases(matchingRelease, allReleases);

            if (applicableReleases.Any())
            {
                var latestReleasesTable = GenerateReleaseNotesTable(provider.Name, applicableReleases, totalTypes.ToList());
            
                AnsiConsole.Write(latestReleasesTable);
            }
            else
            {
                AnsiConsole.MarkupLine($"[green]Provider '{provider.Name}' is up to date![/]");
            }
        }
        
        return 0;
    }

    private void WriteWarning(string message)
    {
        AnsiConsole.MarkupLine($"[bold yellow]WARNING:[/] {message}");
    }

    private void WriteError(string message)
    {
        AnsiConsole.MarkupLine($"[bold red]ERROR[/]: {message}");
    }

    private Table GenerateTFFilesTable(string[] allTerraformFiles)
    {
        var tfFilesTable = new Table();
        tfFilesTable.AddColumn($"{allTerraformFiles.Length} Terraform Files Found:");
        foreach (var tfFile in allTerraformFiles)
        {
            tfFilesTable.AddRow(tfFile);
        }

        return tfFilesTable;
    }

    private Table GenerateReleaseNotesTable(string providerName, List<ReleaseInfoWithBody> applicableReleases, List<string> totalTypes)
    {
        var latestReleasesTable = new Table();
        latestReleasesTable.AddColumn($"{providerName} Version number");
        latestReleasesTable.AddColumn("Release Notes");
        foreach (var release in applicableReleases)
        {
            var highlightedBody = ProcessBodyForHighlights(release.Body, totalTypes);
                
            latestReleasesTable.AddRow(release.ReleaseInfo.Version.ToString(), highlightedBody);
        }

        return latestReleasesTable;
    }

    private string ProcessBodyForHighlights(string releaseBody, List<string> totalTypes)
    {
        var bodySplit = releaseBody
            .EscapeMarkup() // For Spectre.Console purposes
            .Split(new[]{'\n'},StringSplitOptions.None);
        StringBuilder notesResult = new();
        foreach (var bodyLine in bodySplit)
        {
            var result = bodyLine;
            if (totalTypes.Any(x => bodyLine.Contains(x)))
            {
                result = "[bold yellow]" + bodyLine + "[/]";
            }

            notesResult.AppendLine(result);
        }

        return notesResult.ToString();
    }

    private List<ReleaseInfoWithBody> GetApplicableReleases(ReleaseInfo matchingRelease, IReadOnlyList<Release> allReleases)
    {
        var releasesPublishedAfterTheMatchingRelease = allReleases.Where(x => x.CreatedAt > matchingRelease.CreatedOn);
        
        // TODO: Move to TryParse here rather than assuming they'll be parseable. If they're not, show a warning.
        // TODO: Parse semver earlier so we're not repeating ourselves as much
        var greaterSemverReleases = releasesPublishedAfterTheMatchingRelease
            .Where(x => SemVersion.Parse(x.TagName, SemVersionStyles.Any).CompareSortOrderTo(matchingRelease.Version) ==
                        1)
            .OrderBy(x => SemVersion.Parse(x.TagName, SemVersionStyles.Any), SemVersion.SortOrderComparer)
            .Select(x =>
                new ReleaseInfoWithBody(
                    new ReleaseInfo(
                        matchingRelease.OrgName, 
                        matchingRelease.RepoName,
                        SemVersion.Parse(x.TagName, SemVersionStyles.Any), 
                        x.CreatedAt)
                    , x.Body));

        return greaterSemverReleases.ToList();
    }

    private async Task<ReleaseInfo?> GetMatchingGitHubRelease(GitHubClient apiClient, string providerGitHubOrg, string providerGitHubRepo, string providerVersion)
    {
        // TODO: Fail gracefully if API client has an error
        Release? matchingRelease;
        try
        {
            matchingRelease = await apiClient.Repository.Release.Get(providerGitHubOrg, providerGitHubRepo, $"v{providerVersion}");
        }
        catch (ApiException ex)
        {
            AnsiConsole.WriteException(ex);
            return null;
        }
        if (matchingRelease is null) { return null; }
        
        var releaseDate = matchingRelease.CreatedAt;
        var releaseSemver = SemVersion.Parse(matchingRelease.TagName,SemVersionStyles.Any);
        if (releaseSemver is null)
        {
            WriteWarning($"Could not determine Semantic Version for provider '{providerGitHubOrg}/{providerVersion}' release '{providerVersion}'");
            return null;
        }

        return new ReleaseInfo(providerGitHubOrg, providerGitHubRepo, releaseSemver, releaseDate);
    }

    private GitHubClient CreateOctokitApiClient(string token)
    {
        var client = new GitHubClient(new ProductHeaderValue("TFWhatsUp"));
        
        if (!string.IsNullOrWhiteSpace(token))
        {
            AnsiConsole.WriteLine("Using provided GitHub client token.");
            client.Credentials = new Credentials(token);
        }

        return client;
    }

    private Table GenerateGitHubUrlTable(List<ProviderInfo> providersWithGitHubInfo)
    {
        var ghUrlTable = new Table();
        ghUrlTable.AddColumn("Provider");
        ghUrlTable.AddColumn("GitHub Org");
        ghUrlTable.AddColumn("GitHub Repo");
        foreach (var provider in providersWithGitHubInfo)
        {
            ghUrlTable.AddRow(provider.Name, provider.GitHubOrg, provider.GitHubRepo);
        }

        return ghUrlTable;
    }

    private async Task<List<ProviderInfo>> GetGithubUrlsForProviders(List<ProviderInfo> providerInfoList)
    {
        // TODO: If GitHub Url isn't found or isn't valid, show a warning here and don't add the provider to the final list. No sense in setting us up for failure later.
        List<ProviderInfo> providersWithGitHubInfo = new();
        using var httpClient = new HttpClient();
        foreach (var provider in providerInfoList)
        {
            var url = $"https://registry.terraform.io/v1/providers/{provider.Vendor}/{provider.Name}";
            try
            {
                var result =  await httpClient.GetFromJsonAsync<TerraformProviderResponse>(url);
                if (result is not null)
                {
                    var pathItems = new Uri(result.Source, UriKind.Absolute).AbsolutePath.Split('/',StringSplitOptions.RemoveEmptyEntries);
                    providersWithGitHubInfo.Add(provider with { GitHubOrg = pathItems[0], GitHubRepo = pathItems[1] });
                }
                else
                {
                    WriteWarning($"Terraform registry response was null for provider '{provider.Vendor}/{provider.Name}'");
                }
            }
            catch (HttpRequestException ex) // Non success
            {
                WriteError($"HTTP error when querying Terraform registry for provider '{provider.Vendor}/{provider.Name}'");
                AnsiConsole.WriteException(ex);
            }
            catch (NotSupportedException ex) // When content type is not valid
            {
                WriteError($"HTTP error when querying Terraform registry for provider '{provider.Vendor}/{provider.Name}'");
                AnsiConsole.WriteException(ex);
            }
            catch (JsonException ex) // Invalid JSON
            {
                WriteError($"Invalid JSON Response when querying Terraform registry for provider '{provider.Vendor}/{provider.Name}'");
                AnsiConsole.WriteException(ex);
            }
        }
        return providersWithGitHubInfo;
    }

    private Table GenerateResourceTypesTable(List<string> totalTypes)
    {
        var table = new Table();
        table.AddColumn($"{totalTypes.Count()} Resource Types Found:");
        foreach (var thing in totalTypes)
        {
            table.AddRow(thing);
        }

        return table;
    }

    private HashSet<string> ExtractDataTypesFromParsedTerraformFile(HclElement parsedTerraformFiles)
    {
        var allData = parsedTerraformFiles.Children.Where(x => x.Name == "data");
        var dataTypes = new HashSet<string>(allData.Select(x => x.Value));
        return dataTypes;
    }

    private HashSet<string> ExtractResourceTypesFromParsedTerraformFile(HclElement parsedTerraformFiles)
    {
        var allResources = parsedTerraformFiles.Children.Where(x => x.Name == "resource");
        var resourceTypes = new HashSet<string>(allResources.Select(x => x.Value));
        
        return resourceTypes;
    }

    private string ConcatenateTfFiles(string[] allTerraformFiles)
    {
        var sb = new StringBuilder();
        foreach (var tfFile in allTerraformFiles)
        {
            sb.Append(File.ReadAllText(tfFile));
        }

        return sb.ToString();
    }

    private Table GenerateProviderTable(List<ProviderInfo> providerInfo)
    {
        
        var providerTable = new Table();
        providerTable.AddColumn("Vendor");
        providerTable.AddColumn("Provider");
        providerTable.AddColumn("Version");
        providerTable.AddColumn("Registry Link");

        foreach (var provider in providerInfo)
        {
            providerTable.AddRow(provider.Vendor, provider.Name, provider.Version, $"[link]{provider.UrlToLatest}[/]");
        }

        return providerTable;
    }

    private List<ProviderInfo> ExtractProviderInfoFromParsedLockFile(HclElement parsedThing)
    {
        var providers = parsedThing.Children.Where(x => x.Name == "provider").ToList();
        var providerInfo = providers.Select(x =>
        {
            var valueSplit = x.Value.Split('/');
            var vendor = valueSplit[1];
            var name = valueSplit[2];
            return new ProviderInfo(vendor, name,
                $"https://registry.terraform.io/providers/{vendor}/{name}/latest",
                x.Children.First(x => x.Name == "version").Value, string.Empty, String.Empty);
        });
        return providerInfo.ToList();
    }
}

public class TerraformProviderResponse
{
    public string Id { get; set; } = null!;
    public string Owner { get; set; } = null!;
    public string Name { get; set; } = null!;
    public string Alias { get; set; } = null!;
    public string Version { get; set; } = null!;
    public string Tag { get; set; } = null!;
    public string Description { get; set; } = null!;
    public string Source { get; set; } = null!;
    public string PublishedAt { get; set; } = null!;
    public long Downloads { get; set; }
    public string Tier { get; set; } = null!;
}
