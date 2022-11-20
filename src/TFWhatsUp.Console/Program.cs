﻿using System.ComponentModel;
using System.Diagnostics.CodeAnalysis;
using System.Text;
using Microsoft.Playwright;
using Octokit;
using Octopus.CoreParsers.Hcl;
using Semver;
using Spectre.Console;
using Spectre.Console.Cli;
using Spectre.Console.Rendering;
using Sprache;

var app = new CommandApp<WhatsUpCommand>();
return app.Run(args);

public record ProviderInfo(string Vendor, string Name, string UrlToLatest, string Version, string GitHubOrg, string GitHubRepo);

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
    }

    public class LockfileNotFoundException : Exception
    {
        public LockfileNotFoundException(string expectedLocation) : base($"Lock file not found at {expectedLocation}"){}
    }
    private string GetLockfileContents(string lockfilePath)
    {
        var lockfileExists = Path.Exists(lockfilePath);
        if (!lockfileExists)
        {
            throw new LockfileNotFoundException(lockfilePath);
        }

        return File.ReadAllText(lockfilePath);

    }
    public override async Task<int> ExecuteAsync([NotNull] CommandContext context, [NotNull] Settings settings)
    {
        var StartDirectory = settings.TerraformFilesPath ?? Directory.GetCurrentDirectory();
        var lockfileLocation = Path.Combine(StartDirectory, ".terraform.lock.hcl");
        string lockfileContents;
        try
        {
            lockfileContents = GetLockfileContents(lockfileLocation);
        }
        catch (LockfileNotFoundException ex)
        {
            AnsiConsole.WriteException(ex);
            return ExitCodes.LOCKFILE_NOT_FOUND;
        }
        
        //TODO: Throw/exit if no TF files found
        var allTerraformFiles = Directory.GetFiles(StartDirectory, "*.tf", SearchOption.AllDirectories);

        var tfFilesTable = new Table();
        tfFilesTable.AddColumn($"{allTerraformFiles.Length} Terraform Files Found:");
        foreach (var tfFile in allTerraformFiles)
        {
            tfFilesTable.AddRow(tfFile);
        }
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
        var totalTypes = uniqueResourceNames.Union(uniqueDataNames).Order();

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
            latestReleasesTable.AddColumn($"Version number");
            latestReleasesTable.AddColumn($"Release Notes");
            foreach (var thing in greaterSemverReleases)
            {
                var bodySplit = thing.Body.EscapeMarkup().Split(new[]{'\n'},StringSplitOptions.None);
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
                
                latestReleasesTable.AddRow(thing.TagName, notesResult.ToString());
            }
            
            AnsiConsole.Write(latestReleasesTable);
        }
        
        return 0;
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
        List<ProviderInfo> providersWithGitHubInfo = new();
        using var playwright = await Playwright.CreateAsync();
        var browser = await playwright.Chromium.LaunchAsync();
        foreach (var provider in providerInfoList)
        {
            var page = await browser.NewPageAsync();
            await page.GotoAsync(provider.UrlToLatest);
            var githubLink = page.Locator(".github-source-link > a").First;
                
            var githubUrl = await githubLink.GetAttributeAsync("href");
            var pathItems = new Uri(githubUrl, UriKind.Absolute).AbsolutePath.Split('/',StringSplitOptions.RemoveEmptyEntries);

            providersWithGitHubInfo.Add(provider with { GitHubOrg = pathItems[0], GitHubRepo = pathItems[1] });
        }
        return providersWithGitHubInfo;
    }

    private Table GenerateResourceTypesTable(IOrderedEnumerable<string> totalTypes)
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
