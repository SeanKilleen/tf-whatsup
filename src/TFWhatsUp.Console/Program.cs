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

public record ProviderInfo(string Vendor, string Name, string UrlToLatest, string Version);

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
                x.Children.First(x => x.Name == "version").Value);
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
        using (var playwright = await Playwright.CreateAsync())
        {
            var browser = await playwright.Chromium.LaunchAsync();
            foreach (var provider in providerInfo)
            {
                var page = await browser.NewPageAsync();
                await page.GotoAsync(provider.UrlToLatest);
                AnsiConsole.WriteLine(page.Url);
                var githubLink = page.Locator(".github-source-link > a").First;
                
                var githubUrl = await githubLink.GetAttributeAsync("href");

                ghUrlTable.AddRow(provider.Name, githubUrl);
            }
        }
        
        AnsiConsole.Write(ghUrlTable);
        
        // TODO: Parse GitHub Url into username & repo name
        // TODO: Translate the hard-coded example below

        var apiClient = new GitHubClient(new ProductHeaderValue("TFWhatsUp"));
        var matchingRelease = await apiClient.Repository.Release.Get("hashicorp", "terraform-provider-azurerm", "v3.16.0");
        var releaseDate = matchingRelease.CreatedAt;
        var releaseSemver = SemVersion.Parse(matchingRelease.TagName,SemVersionStyles.Any);

        var allReleases = await apiClient.Repository.Release.GetAll("hashicorp", "terraform-provider-azurerm");
        var releasesPublishedAfterOurs = allReleases.Where(x => x.CreatedAt > releaseDate);
        var greaterSemverReleases = releasesPublishedAfterOurs.Where(x => SemVersion.Parse(x.TagName,SemVersionStyles.Any).CompareSortOrderTo(releaseSemver) == 1);

        foreach (var thing in greaterSemverReleases)
        {
            AnsiConsole.WriteLine(thing.TagName);
        }
        return 0;
    }
}
