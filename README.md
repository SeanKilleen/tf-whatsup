# tf-whatsup

Highlights Terraform provider release notes you should care about.

## The Use Case

I sometimes look at Terraform projects with providers that haven't been updated in a bit, especially because it's a good practice to peg a provider at a specific version. I want to upgrade them incrementally or perhaps in a large chunk, but it's not immediately clear which of the many, lovely, detailed Terraform notes across providers will actually apply to me (this is especially true in projects I'm parachuting into). This tool takes care of a lot of that work and helps me find the signal in the noise.

## Current Status

Using this as an excuse to mess around with Playwright, Octokit, Spectre.Console, etc.

Maybe it'll grow up to be a real thing someday and make it a dotnet tool etc. etc. -- for now, it's just an unfinished mess of procedural code that I have yet to clean up, and I'm following no rules in this repository yet.

## The Gist

* Finds Terraform files
* Uses `Octopus.CoreParsers.Hcl` to parse the file and pull the provider information
* Uses `Playwright` to hit the Provider URL in order to obtain the GitHub URL
* Uses `Octokit` to find the release that matches yours
* Pulls all releases
* Filters them to those published after yours
* Uses `SemVersion` to filter to the releases Semantically greater than yours
* Orders the release notes earliest to latest for a given provider
* Prints the release notes line-by-line, highlighting the ones you might care about.
