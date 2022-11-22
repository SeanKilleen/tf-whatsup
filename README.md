# tf-whatsup

Highlights Terraform provider release notes you should care about.

[![ASCII console animation of the tool in action](https://asciinema.org/a/mUou4fBy6cN3SuRTXmfcaAhev.svg)](https://asciinema.org/a/mUou4fBy6cN3SuRTXmfcaAhev)

## Usage

```cmd
USAGE:
    TFWhatsUp.Console.dll [tfFilesPath] [OPTIONS]

ARGUMENTS:
    [tfFilesPath]    Path where your Terraform is located. Defaults to current directory

OPTIONS:
    -h, --help                Prints help information
    -t, --github-api-token    A GitHub Personal Access Token. If you generate one and pass it, you won't hit the smaller rate-limits of un-authenticated accounts.
```

## The Use Case

I sometimes look at Terraform projects with providers that haven't been updated in a bit, especially because it's a good practice to peg a provider at a specific version. I want to upgrade them incrementally or perhaps in a large chunk, but it's not immediately clear which of the many, lovely, detailed Terraform notes across providers will actually apply to me (this is especially true in projects I'm parachuting into). This tool takes care of a lot of that work and helps me find the signal in the noise.

## Current Status

This is usable. I'll probably start putting it out there and seeing what I get. It still needs some tests of course but I'm able to use it in my daily work.

If it seems to work for folks for a while, I'll bump it to `v1.0.0`.

## The Gist

* Finds Terraform files
* Uses [`Octopus.CoreParsers.Hcl`](https://github.com/OctopusDeploy/HCLParser) to parse the file and pull the provider information
* Uses `HttpClient` to hit the Terraform Provider API URL in order to obtain the GitHub URL
* Uses [`Octokit`](https://github.com/octokit/octokit.net) to find the release that matches yours
* Pulls all releases & Filters them to those published after yours (both date-wise and based on Semver thanks to [`Semver'](https://github.com/maxhauser/semver))
* Orders the release notes earliest to latest for a given provider
* Prints the release notes line-by-line, highlighting the ones you might care about.
