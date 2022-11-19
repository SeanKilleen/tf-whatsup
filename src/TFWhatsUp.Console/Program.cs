using System.ComponentModel;
using System.Diagnostics.CodeAnalysis;
using System.Text;
using HtmlAgilityPack;
using Microsoft.Playwright;
using Octopus.CoreParsers.Hcl;
using Spectre.Console;
using Spectre.Console.Cli;
using Sprache;

var app = new CommandApp<WhatsUpCommand>();
return app.Run(args);
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
            return new
            {
                providerVendor = valueSplit[1],
                providerName = valueSplit[2],
                urlToLatest = $"https://registry.terraform.io/providers/{valueSplit[1]}/{valueSplit[2]}/latest",
                providerVersion = x.Children.First(x => x.Name == "version").Value
            };
        });

        var providerTable = new Table();
        providerTable.AddColumn("Vendor");
        providerTable.AddColumn("Provider");
        providerTable.AddColumn("Version");
        providerTable.AddColumn("Registry Link");

        foreach (var provider in providerInfo)
        {
            providerTable.AddRow(provider.providerVendor, provider.providerName, provider.providerVersion, $"[link]{provider.urlToLatest}[/]");
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
                await page.GotoAsync(provider.urlToLatest);
                AnsiConsole.WriteLine(page.Url);
                var githubLink = page.Locator(".github-source-link > a").First;
                
                var githubUrl = await githubLink.GetAttributeAsync("href");

                ghUrlTable.AddRow(provider.providerName, githubUrl);
            }
        }
        
        AnsiConsole.Write(ghUrlTable);
        return 0;
    }
}
