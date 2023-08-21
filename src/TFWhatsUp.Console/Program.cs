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
using TFWhatsUp.Console;

var app = new CommandApp<WhatsUpCommand>();
return app.Run(args);

public record ProviderInfo(string Vendor, string Name, string UrlToLatest, string Version, string GitHubOrg, string GitHubRepo);

public static class Extensions
{
    public static bool IsSemanticallyGreaterThan(this string currentVersionNumber, string versionToCompareTo)
    {
        return currentVersionNumber.ToSemVer().CompareSortOrderTo(versionToCompareTo.ToSemVer()) == 1;
    }

    public static SemVersion ToSemVer(this string versionNumber)
    {
        // TODO: Move to TryParse here rather than assuming they'll be parseable. If they're not, show a warning.
        // https://github.com/SeanKilleen/tf-whatsup/issues/46

        return SemVersion.Parse(versionNumber, SemVersionStyles.Any);
    }
}

public record ReleaseInfoWithBody(SemVersion Version, string Body);
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

        [Description("A GitHub Personal Access Token. If you generate one and pass it, you won't hit the smaller rate-limits of un-authenticated accounts.")]
        [CommandOption("-t|--github-api-token")]
        public string? GitHubApiToken { get; set; }

        [Description("Typically we show one provider information at a time. This will show all of them without pause.")]
        [CommandOption("-a|--all")]
        public bool? ShowAllInfo { get; set; }

        [Description("Instead of highlighting text, will show it in all caps with a '***' indicator at the start of a line.")]
        [CommandOption("-c|--caps")]
        public bool? AllCaps { get; set; }
    }

    readonly OutputHelper _output = new ();
    private readonly TerraformRegistryService _tfRegistry = new();

    public override async Task<int> ExecuteAsync([NotNull] CommandContext context, [NotNull] Settings settings)
    {
        var StartDirectory = settings.TerraformFilesPath ?? Directory.GetCurrentDirectory();

        // TODO: Wrap API calls in methods that detect API rate limits and write messages that suggest using a token
        // https://github.com/SeanKilleen/tf-whatsup/issues/44
        var apiClient = CreateOctokitApiClient(settings.GitHubApiToken ?? string.Empty);

        var lockfileLocation = Path.Combine(StartDirectory, ".terraform.lock.hcl");
        if (!Path.Exists(lockfileLocation))
        {
            _output.WriteError($"Lock file not found at {lockfileLocation}");
            return ExitCodes.LOCKFILE_NOT_FOUND;
        }
        string lockfileContents = await File.ReadAllTextAsync(lockfileLocation);

        var allTerraformFiles = Directory.GetFiles(StartDirectory, "*.tf", SearchOption.AllDirectories);
        if (!allTerraformFiles.Any())
        {
            _output.WriteError($"Exiting: No Terraform files found in '{StartDirectory}'");
            return ExitCodes.TERRAFORM_FILES_NOT_FOUND;
        }

        var tfFilesTable = GenerateTFFilesTable(allTerraformFiles);
        AnsiConsole.Write(tfFilesTable);

        var parsedLockFile = HclParser.HclTemplate.Parse(lockfileContents);

        var providerInfoList = ExtractProviderInfoFromParsedLockFile(parsedLockFile);

        var providerTable = GenerateProviderTable(providerInfoList);
        AnsiConsole.Write(providerTable);

        AnsiConsole.WriteLine("Concatenating TF files");
        var totalTypes = await ExtractResourcesFromFiles(allTerraformFiles);

        var resourceTypesTable = GenerateResourceTypesTable(totalTypes);
        AnsiConsole.Write(resourceTypesTable);

        List<ProviderInfo> providersWithGitHubInfo = new();

        await AnsiConsole.Status()
            .Spinner(Spinner.Known.Earth)
            .StartAsync("Determining GitHub URLs...", async ctx =>
            {
                providersWithGitHubInfo = await _tfRegistry.GetGithubUrlsForProviders(providerInfoList);
            });

        var ghUrlTable = GenerateGitHubUrlTable(providersWithGitHubInfo);
        AnsiConsole.Write(ghUrlTable);


        // TODO: Split this into a function that returns the data, to hide behind a spinner. Then format the table as applicable once it's done.
        // https://github.com/SeanKilleen/tf-whatsup/issues/45
        foreach (var provider in providersWithGitHubInfo)
        {
            var allReleases = await apiClient.Repository.Release.GetAll(provider.GitHubOrg, provider.GitHubRepo);

            var applicableReleases = GetApplicableReleases(provider.Version, allReleases);

            if (applicableReleases.Any())
            {
                var latestReleasesTable = GenerateReleaseNotesTable(provider.Name, applicableReleases, totalTypes.ToList(), settings);

                AnsiConsole.Write(latestReleasesTable);

                if (settings.ShowAllInfo.HasValue && settings.ShowAllInfo.Value)
                {
                    continue;
                }

                AnsiConsole.Confirm("Show next provider?");
            }
            else
            {
                AnsiConsole.MarkupLine($"[green]Provider '{provider.Name}' is up to date![/]");
            }
        }

        AnsiConsole.MarkupLine($"[green]Finished successfully.[/]");
        return 0;
    }

    private async Task<List<string>> ExtractResourcesFromFiles(string[] allTerraformFiles)
    {
        List<string> allNames = new();
        foreach (var filePath in allTerraformFiles)
        {
            try
            {
                var fileContents = await File.ReadAllTextAsync(filePath);
                var parsedTerraformFile = HclParser.HclTemplate.Parse(fileContents);
                allNames.AddRange(ExtractResourceTypesFromParsedTerraformFile(parsedTerraformFile));
                allNames.AddRange(ExtractDataTypesFromParsedTerraformFile(parsedTerraformFile));
            }
            catch (Exception ex)
            {
                _output.WriteError($"Encountered an issue processing ${filePath}");
                AnsiConsole.WriteException(ex);
                continue;
            }
        }

        return allNames.Distinct().ToList();
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

    private Table GenerateReleaseNotesTable(string providerName, List<ReleaseInfoWithBody> applicableReleases, List<string> totalTypes, Settings settings)
    {
        var latestReleasesTable = new Table();
        latestReleasesTable.AddColumn($"{providerName} Version number");
        latestReleasesTable.AddColumn("Release Notes");
        foreach (var release in applicableReleases)
        {
            var highlightedBody = ProcessBodyForHighlights(release.Body, totalTypes, settings);

            latestReleasesTable.AddRow(release.Version.ToString(), highlightedBody);
        }

        return latestReleasesTable;
    }

    private string ProcessBodyForHighlights(string releaseBody, List<string> totalTypes, Settings settings)
    {
        var bodySplit = releaseBody
            .EscapeMarkup() // For Spectre.Console purposes
            .Split(new[] { '\n' }, StringSplitOptions.None);
        StringBuilder notesResult = new();
        foreach (var bodyLine in bodySplit)
        {
            var result = bodyLine;
            if (totalTypes.Any(x => bodyLine.Contains(x)))
            {
                if (settings.AllCaps.HasValue && settings.AllCaps.Value)
                {
                    result = "*** " + bodyLine.ToUpperInvariant();
                }
                else
                {
                    result = "[bold yellow]" + bodyLine + "[/]";
                }
            }

            notesResult.AppendLine(result);
        }

        return notesResult.ToString();
    }

    private List<ReleaseInfoWithBody> GetApplicableReleases(string versionNumber, IReadOnlyList<Release> allReleases)
    {
        // TODO: Parse semver earlier so we're not repeating ourselves as much
        var greaterSemverReleases = allReleases
            .Where(x => x.TagName.IsSemanticallyGreaterThan(versionNumber))
            .OrderBy(x => x.TagName.ToSemVer(), SemVersion.SortOrderComparer)
            .Select(x =>
                new ReleaseInfoWithBody(x.TagName.ToSemVer(), x.Body));

        return greaterSemverReleases.ToList();
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

    private List<string> ExtractDataTypesFromParsedTerraformFile(HclElement parsedTerraformFiles)
    {
        var allData = parsedTerraformFiles.Children.Where(x => x.Name == "data");
        var dataTypes = new List<string>(allData.Select(x => x.Value));
        return dataTypes;
    }

    private List<string> ExtractResourceTypesFromParsedTerraformFile(HclElement parsedTerraformFiles)
    {
        var allResources = parsedTerraformFiles.Children.Where(x => x.Name == "resource");
        var resourceTypes = new List<string>(allResources.Select(x => x.Value));

        return resourceTypes;
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
