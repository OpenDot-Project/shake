// SPDX-FileCopyrightText: 2026 OpenDot Project <opendotproject.opendot@gmail.com>
// SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception

namespace OpenDot.Shake.Cli;

internal enum ShakeCommand
{
    Help,
    Init,
    Doctor,
    Graph
}

internal enum GraphFormat
{
    Dot,
    Json
}

internal sealed record ShakeInvocation(
    ShakeCommand Command,
    string? HelpTopic = null,
    string? ProjectName = null,
    bool Force = false,
    string? Target = null,
    GraphFormat GraphFormat = GraphFormat.Dot,
    bool Json = false);

internal sealed class CommandLineException(string message) : Exception(message);

internal static class CommandLineParser
{
    public static ShakeInvocation Parse(string[] arguments)
    {
        if (arguments.Length == 0) return new ShakeInvocation(ShakeCommand.Help);

        if (arguments[0] is "--help" or "-h")
        {
            RequireNoAdditionalArguments(arguments);
            return new ShakeInvocation(ShakeCommand.Help);
        }

        if (arguments[0] is "--version" or "-V")
        {
            RequireNoAdditionalArguments(arguments);
            return new ShakeInvocation(ShakeCommand.Help, "__version");
        }

        if (arguments[0] == "help")
        {
            if (arguments.Length > 2) throw new CommandLineException("shake help accepts at most one command name.");

            return new ShakeInvocation(
                ShakeCommand.Help,
                arguments.Length == 2 ? arguments[1] : null);
        }

        var command = arguments[0] switch
        {
            "init" => ShakeCommand.Init,
            "doctor" => ShakeCommand.Doctor,
            "graph" => ShakeCommand.Graph,
            _ => throw new CommandLineException($"unknown command {arguments[0]}")
        };

        var options = arguments.AsSpan(1);
        if (options.Contains("--help") || options.Contains("-h"))
        {
            if (options.Length != 1)
                throw new CommandLineException($"shake {arguments[0]} --help cannot be combined with other arguments.");

            return new ShakeInvocation(ShakeCommand.Help, arguments[0]);
        }

        return command switch
        {
            ShakeCommand.Init => ParseInit(options),
            ShakeCommand.Doctor => ParseDoctor(options),
            ShakeCommand.Graph => ParseGraph(options),
            _ => throw new InvalidOperationException("The command parser reached an invalid dispatch state.")
        };
    }

    private static ShakeInvocation ParseInit(ReadOnlySpan<string> arguments)
    {
        string? name = null;
        var nameSeen = false;
        var force = false;

        for (var index = 0; index < arguments.Length; index++)
        {
            var argument = arguments[index];
            switch (argument)
            {
                case "--name":
                    if (nameSeen) throw new CommandLineException("--name may be supplied only once.");

                    nameSeen = true;
                    name = ReadValue(arguments, ref index, "--name");
                    if (!IsIdentifier(name))
                        throw new CommandLineException(
                            "--name must be a lowercase identifier containing letters, digits, or underscores.");

                    break;
                case "--force":
                    if (force) throw new CommandLineException("--force may be supplied only once.");

                    force = true;
                    break;
                default:
                    ThrowUnexpected("init", argument);
                    break;
            }
        }

        return new ShakeInvocation(ShakeCommand.Init, ProjectName: name, Force: force);
    }

    private static ShakeInvocation ParseDoctor(ReadOnlySpan<string> arguments)
    {
        var json = false;
        var formatSeen = false;

        for (var index = 0; index < arguments.Length; index++)
            switch (arguments[index])
            {
                case "--format":
                    if (formatSeen) throw new CommandLineException("--format may be supplied only once.");

                    formatSeen = true;
                    var value = ReadValue(arguments, ref index, "--format");
                    json = value switch
                    {
                        "human" => false,
                        "json" => true,
                        _ => throw new CommandLineException("doctor --format must be human or json.")
                    };
                    break;
                default:
                    ThrowUnexpected("doctor", arguments[index]);
                    break;
            }

        return new ShakeInvocation(ShakeCommand.Doctor, Json: json);
    }

    private static ShakeInvocation ParseGraph(ReadOnlySpan<string> arguments)
    {
        string? target = null;
        var format = GraphFormat.Dot;
        var formatSeen = false;

        for (var index = 0; index < arguments.Length; index++)
        {
            var argument = arguments[index];
            switch (argument)
            {
                case "--format":
                    if (formatSeen) throw new CommandLineException("--format may be supplied only once.");

                    formatSeen = true;
                    var value = ReadValue(arguments, ref index, "--format");
                    format = value switch
                    {
                        "dot" => GraphFormat.Dot,
                        "json" => GraphFormat.Json,
                        _ => throw new CommandLineException("graph --format must be dot or json.")
                    };
                    break;
                case "--":
                    throw new CommandLineException("graph does not accept an argument separator.");
                default:
                    if (argument.StartsWith("-", StringComparison.Ordinal)) ThrowUnexpected("graph", argument);

                    if (target is not null) throw new CommandLineException("graph accepts exactly one target label.");

                    target = argument;
                    break;
            }
        }

        if (string.IsNullOrWhiteSpace(target))
            throw new CommandLineException("graph requires a target label, for example //:myapp.");

        return new ShakeInvocation(ShakeCommand.Graph, Target: target, GraphFormat: format);
    }

    private static string ReadValue(ReadOnlySpan<string> arguments, ref int index, string option)
    {
        if (index + 1 >= arguments.Length || arguments[index + 1].StartsWith("-", StringComparison.Ordinal))
            throw new CommandLineException($"{option} requires a value.");

        return arguments[++index];
    }

    private static bool IsIdentifier(string value)
    {
        if (value.Length == 0 || value[0] is < 'a' or > 'z') return false;

        return value.All(character =>
            character is >= 'a' and <= 'z'
                or >= '0' and <= '9'
                or '_');
    }

    private static void RequireNoAdditionalArguments(string[] arguments)
    {
        if (arguments.Length > 1)
            throw new CommandLineException($"{arguments[0]} does not accept additional arguments.");
    }

    private static void ThrowUnexpected(string command, string argument)
    {
        throw new CommandLineException($"unexpected argument {argument} for shake {command}");
    }
}
