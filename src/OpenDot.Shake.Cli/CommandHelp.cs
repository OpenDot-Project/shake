// SPDX-FileCopyrightText: 2026 OpenDot Project <opendotproject.opendot@gmail.com>
// SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception

using System.Reflection;
using Spectre.Console;

namespace OpenDot.Shake.Cli;

internal static class CommandHelp
{
    private const string VersionTopic = "__version";

    private static readonly IReadOnlyDictionary<string, string> CommandHelpText =
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["init"] = """
                       Usage: shake init [--name NAME] [--force]

                       Create MODULE.rb and BUILD.rb in the current directory.
                       --name NAME   Name the starter Sol binary target. Defaults to the directory name.
                       --force       Replace existing Shake files.
                       """,
            ["doctor"] = """
                         Usage: shake doctor [--format human|json]

                         Inspect the .NET runtime and the CRuby runtime used by Shake.
                         --format      Select human-readable or JSON output.
                         """,
            ["graph"] = """
                        Usage: shake graph TARGET [--format dot|json]

                        Evaluate MODULE.rb and BUILD.rb with CRuby and print the selected target
                        and its local dependency closure.
                        --format      Select Graphviz DOT (default) or JSON.
                        """
        };

    public static void Write(string? topic)
    {
        if (topic == VersionTopic)
        {
            var version = typeof(CommandHelp).Assembly
                .GetCustomAttributes(typeof(AssemblyInformationalVersionAttribute), false)
                .OfType<AssemblyInformationalVersionAttribute>()
                .SingleOrDefault()?.InformationalVersion ?? "0.0.0";
            AnsiConsole.MarkupLine($"shake {Markup.Escape(version)}");
            return;
        }

        if (topic is null)
        {
            AnsiConsole.MarkupLine("[bold]Shake[/] — Windows build system");
            AnsiConsole.WriteLine();
            AnsiConsole.MarkupLine("[bold]Usage:[/] shake [COMMAND] [OPTIONS]");
            AnsiConsole.WriteLine();
            AnsiConsole.MarkupLine("[bold]Commands:[/]");
            AnsiConsole.MarkupLine("  [green]init[/]      Create a workspace starter");
            AnsiConsole.MarkupLine("  [green]doctor[/]    Inspect Shake's local runtime");
            AnsiConsole.MarkupLine("  [green]graph[/]     Evaluate build files and print a target graph");
            AnsiConsole.MarkupLine("  [green]help[/]      Show help for Shake or one command");
            AnsiConsole.WriteLine();
            AnsiConsole.MarkupLine("[dim]Use shake help COMMAND for command-specific options.[/]");
            return;
        }

        if (CommandHelpText.TryGetValue(topic, out var help))
        {
            AnsiConsole.WriteLine(help);
            return;
        }

        throw new CommandLineException($"no help is available for command {topic}");
    }
}
