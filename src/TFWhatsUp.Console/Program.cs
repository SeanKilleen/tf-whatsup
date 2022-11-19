using System.ComponentModel;
using System.Diagnostics.CodeAnalysis;
using System.Text;
using HtmlAgilityPack;
using Microsoft.Playwright;
using Octokit;
using Octopus.CoreParsers.Hcl;
using Semver;
using Spectre.Console;
using Spectre.Console.Cli;
using Sprache;

var app = new CommandApp<WhatsUpCommand>();
return app.Run(args);

public record ProviderInfo(string Vendor, string Name, string UrlToLatest, string Version, string GitHubOrg, string GitHubRepo);

internal sealed class WhatsUpCommand : AsyncCommand<WhatsUpCommand.Settings>
{
    public sealed class Settings : CommandSettings
    {
        [Description("Path where your Terraform is located. Defaults to current directory.")]
        [CommandArgument(0, "[tfFilesPath]")]
        public string? TerraformFilesPath { get; init; }
    }

    public override async Task<int> ExecuteAsync([NotNull] CommandContext context, [NotNull] Settings settings)
    {
        var StartDirectory = settings.TerraformFilesPath ?? Directory.GetCurrentDirectory();
       
        //TODO: Throw/exit if lockfile not found
        //TODO: Throw/exit if no TF files found
        var lockfileLocation = Path.Combine(StartDirectory, ".terraform.lock.hcl");
        var allTerraformFiles = Directory.GetFiles(StartDirectory, "*.tf", SearchOption.AllDirectories);

        var tfFilesTable = new Table();
        tfFilesTable.AddColumn($"{allTerraformFiles.Length} Terraform Files Found:");
        foreach (var tfFile in allTerraformFiles)
        {
            tfFilesTable.AddRow(tfFile);
        }
        AnsiConsole.Write(tfFilesTable);
    
        var parsedThing = HclParser.HclTemplate.Parse(File.ReadAllText(lockfileLocation));
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

        var providerTable = new Table();
        providerTable.AddColumn("Vendor");
        providerTable.AddColumn("Provider");
        providerTable.AddColumn("Version");
        providerTable.AddColumn("Registry Link");

        foreach (var provider in providerInfo)
        {
            providerTable.AddRow(provider.Vendor, provider.Name, provider.Version, $"[link]{provider.UrlToLatest}[/]");
        }
        
        AnsiConsole.Write(providerTable);
        
        AnsiConsole.WriteLine("Concatenating TF files");
        var sb = new StringBuilder();
        foreach (var tfFile in allTerraformFiles)
        {
            sb.Append(File.ReadAllText(tfFile));
        }
        
        var parsedBigThing = HclParser.HclTemplate.Parse(sb.ToString());

        var allResources = parsedBigThing.Children.Where(x => x.Name == "resource");
        var allData = parsedBigThing.Children.Where(x => x.Name == "data");

        var resourceTypes = new HashSet<string>(allResources.Select(x => x.Value));
        var dataTypes = new HashSet<string>(allData.Select(x => x.Value));
        var totalTypes = resourceTypes.Union(dataTypes).Order();

        var table = new Table();
        table.AddColumn($"{totalTypes.Count()} Resource Types Found:");
        foreach (var thing in totalTypes)
        {
            table.AddRow(thing);
        }
        AnsiConsole.Write(table);
        
        AnsiConsole.WriteLine("Determining GitHub URLs");
        var ghUrlTable = new Table();
        ghUrlTable.AddColumn("Provider");
        ghUrlTable.AddColumn("GitHub URL");
        List<ProviderInfo> providersWithGitHubInfo = new();
        using (var playwright = await Playwright.CreateAsync())
        {
            var browser = await playwright.Chromium.LaunchAsync();
            foreach (var provider in providerInfo)
            {
                var page = await browser.NewPageAsync();
                await page.GotoAsync(provider.UrlToLatest);
                var githubLink = page.Locator(".github-source-link > a").First;
                
                var githubUrl = await githubLink.GetAttributeAsync("href");
                var pathItems = new Uri(githubUrl, UriKind.Absolute).AbsolutePath.Split('/',StringSplitOptions.RemoveEmptyEntries);

                providersWithGitHubInfo.Add(provider with { GitHubOrg = pathItems[0], GitHubRepo = pathItems[1] });
                ghUrlTable.AddRow(provider.Name, githubUrl);
            }
        }
        
        AnsiConsole.Write(ghUrlTable);

        foreach (var provider in providersWithGitHubInfo)
        {
            var apiClient = new GitHubClient(new ProductHeaderValue("TFWhatsUp"));
            var matchingRelease = await apiClient.Repository.Release.Get(provider.GitHubOrg, provider.GitHubRepo, $"v{provider.Version}");
            var releaseDate = matchingRelease.CreatedAt;
            var releaseSemver = SemVersion.Parse(matchingRelease.TagName,SemVersionStyles.Any);

            var allReleases = await apiClient.Repository.Release.GetAll(provider.GitHubOrg, provider.GitHubRepo);
            var releasesPublishedAfterOurs = allReleases.Where(x => x.CreatedAt > releaseDate);
            var greaterSemverReleases = releasesPublishedAfterOurs
                .Where(x => SemVersion.Parse(x.TagName,SemVersionStyles.Any).CompareSortOrderTo(releaseSemver) == 1)
                .OrderBy(x=>SemVersion.Parse(x.TagName, SemVersionStyles.Any), SemVersion.SortOrderComparer);
            var latestReleasesTable = new Table();
            latestReleasesTable.AddColumn($"Newer releases for {provider.Name}:");
            foreach (var thing in greaterSemverReleases)
            {
                latestReleasesTable.AddRow(thing.TagName);
            }
            
            AnsiConsole.Write(latestReleasesTable);
        }
        
        return 0;
    }
}
