// SPDX-FileCopyrightText: 2026 OpenDot Project <opendotproject.opendot@gmail.com>
// SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception

using System.ComponentModel;
using System.Text.Json;
using Spectre.Console;

namespace OpenDot.Shake.Cli;

internal static class ShakeCli
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true
    };

    public static async Task<int> RunAsync(string[] arguments)
    {
        ShakeInvocation invocation;
        try
        {
            invocation = CommandLineParser.Parse(arguments);
        }
        catch (CommandLineException exception)
        {
            WriteError(exception.Message);
            return 2;
        }

        using var cancellation = new ConsoleCancellation();

        try
        {
            return invocation.Command switch
            {
                ShakeCommand.Help => RunHelp(invocation),
                ShakeCommand.Init => RunInit(invocation),
                ShakeCommand.Doctor => await RunDoctorAsync(invocation, cancellation.Token),
                ShakeCommand.Graph => await RunGraphAsync(invocation, cancellation.Token),
                _ => 70
            };
        }
        catch (OperationCanceledException)
        {
            return 130;
        }
        catch (CommandLineException exception)
        {
            WriteError(exception.Message);
            return 2;
        }
        catch (Exception exception)
        {
            WriteError(exception.Message);
            return 70;
        }
    }

    private static int RunHelp(ShakeInvocation invocation)
    {
        try
        {
            CommandHelp.Write(invocation.HelpTopic);
            return 0;
        }
        catch (CommandLineException exception)
        {
            WriteError(exception.Message);
            return 2;
        }
    }

    private static int RunInit(ShakeInvocation invocation)
    {
        var created = ProjectInitializer.Create(invocation.ProjectName, invocation.Force);
        foreach (var path in created) AnsiConsole.MarkupLine($"[green]created[/] {Markup.Escape(path)}");

        AnsiConsole.MarkupLine("[dim]Add src/main.sol before building this workspace.[/]");
        return 0;
    }

    private static async Task<int> RunDoctorAsync(ShakeInvocation invocation, CancellationToken cancellationToken)
    {
        var runtime = RubyRuntimeLocator.Find(out var runtimeFailure);
        string? rubyVersion = null;
        string? rubyFailure = null;

        if (runtime is not null)
            try
            {
                var response = await RubyWorkerClient.EvaluateAsync(
                    new RubyWorkerRequest("probe", runtime.Home),
                    cancellationToken);
                if (response.Success)
                    rubyVersion = response.Payload;
                else
                    rubyFailure = response.Error;
            }
            catch (Exception exception) when (exception is IOException or InvalidOperationException or Win32Exception)
            {
                rubyFailure = exception.Message;
            }

        var healthy = runtime is not null && rubyVersion is not null && Environment.Version.Major == 10;
        if (invocation.Json)
        {
            Console.Out.WriteLine(JsonSerializer.Serialize(
                new
                {
                    healthy,
                    dotnet = new
                    {
                        requiredMajor = 10,
                        currentVersion = Environment.Version.ToString(),
                        supported = Environment.Version.Major == 10
                    },
                    cruby = new
                    {
                        version = rubyVersion,
                        home = runtime?.Home,
                        source = runtime?.Source,
                        error = rubyFailure ?? runtimeFailure
                    }
                },
                JsonOptions));
        }
        else
        {
            var table = new Table().Border(TableBorder.Rounded).AddColumn("Component").AddColumn("Status")
                .AddColumn("Details");
            table.AddRow(
                ".NET",
                Environment.Version.Major == 10 ? "[green]ready[/]" : "[red]unsupported[/]",
                Markup.Escape(Environment.Version.ToString()));
            table.AddRow(
                "CRuby",
                rubyVersion is null ? "[red]unavailable[/]" : "[green]ready[/]",
                rubyVersion is not null
                    ? Markup.Escape($"{rubyVersion} — {runtime!.Source}: {runtime.Home}")
                    : Markup.Escape(rubyFailure ?? runtimeFailure ?? "No CRuby runtime was found."));
            AnsiConsole.Write(table);
        }

        return healthy ? 0 : 1;
    }

    private static async Task<int> RunGraphAsync(ShakeInvocation invocation, CancellationToken cancellationToken)
    {
        var workspace = WorkspaceLocator.FindFromCurrentDirectory();
        var runtime = RubyRuntimeLocator.Find(out var failure)
                      ?? throw new CommandLineException(failure ?? "No CRuby runtime is available.");

        var request = new RubyWorkerRequest(
            "graph",
            runtime.Home,
            workspace.Root,
            Path.GetRelativePath(workspace.Root, workspace.ModuleFile).Replace(Path.DirectorySeparatorChar, '/'),
            workspace.BuildFiles.ToArray());
        var response = await RubyWorkerClient.EvaluateAsync(request, cancellationToken);
        if (!response.Success)
        {
            WriteError(response.Error ?? "Ruby graph evaluation failed.");
            return 2;
        }

        var graph = GraphValidator.Validate(
            GraphJson.Parse(response.Payload ??
                            throw new InvalidDataException("RubyHost returned no graph payload.")),
            workspace.Root);
        var rendered = GraphRenderer.Render(graph, invocation.Target!, invocation.GraphFormat);
        Console.Out.Write(rendered);
        if (!rendered.EndsWith(Environment.NewLine, StringComparison.Ordinal)) Console.Out.WriteLine();

        return 0;
    }

    private static void WriteError(string message)
    {
        AnsiConsole.MarkupLine($"[red]error:[/] {Markup.Escape(message)}");
    }
}
