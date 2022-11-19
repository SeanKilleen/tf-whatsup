# tf-whatsup

 Highlights Terraform provider release notes you should care about.

Using this as an excuse to mess around with Playwright, Octokit, Spectre.Console, etc. Maybe it'll grow up to be a real thing someday.

The general gist:

* Finds Terraform files
* Uses `Octopus.CoreParsers.Hcl` to parse the file and pull the provider information
* Uses `Playwright` to hit the Provider URL in order to obtain the GitHub URL
* Uses `Octokit` to find the release that matches yours
* Pulls all releases
* Filters them to those published after yours
* Uses `Semver` to filter to the releases Semantically greater than yours
* Orders the release notes earliest to latest for a given provider
* Prints the release notes line-by-line, highlighting the ones you might care about.
