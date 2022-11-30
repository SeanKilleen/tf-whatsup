using Spectre.Console;

namespace TFWhatsUp.Console;

public class OutputHelper
{
    public void WriteWarning(string message)
    {
        AnsiConsole.MarkupLine($"[bold yellow]WARNING:[/] {message}");
    }

    public void WriteError(string message)
    {
        AnsiConsole.MarkupLine($"[bold red]ERROR[/]: {message}");
    }
}