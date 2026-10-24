// SPDX-FileCopyrightText: 2026 OpenDot Project <opendotproject.opendot@gmail.com>
// SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception

using System.Text;
using System.Text.Json;

namespace OpenDot.Shake.Cli;

internal static class GraphRenderer
{
    public static string Render(GraphSnapshot graph, string requestedTarget, GraphFormat format)
    {
        var targetsByLabel = graph.Targets.ToDictionary(target => target.Label, StringComparer.Ordinal);
        var canonicalTarget = NormalizeLabel(requestedTarget, null);
        if (!targetsByLabel.ContainsKey(canonicalTarget))
            throw new CommandLineException($"target {requestedTarget} does not exist in the evaluated graph.");

        var selected = new Dictionary<string, GraphTarget>(StringComparer.Ordinal);
        var pending = new Stack<string>();
        pending.Push(canonicalTarget);

        while (pending.TryPop(out var label))
        {
            if (!targetsByLabel.TryGetValue(label, out var target) || !selected.TryAdd(label, target)) continue;
            foreach (var dependency in target.Dependencies)
            {
                var dependencyLabel = NormalizeLabel(dependency, target.Label);
                if (!targetsByLabel.ContainsKey(dependencyLabel))
                {
                    throw new CommandLineException(
                        $"target {target.Label} depends on unresolved target {dependencyLabel}; external dependency resolution is not implemented yet.");
                }

                pending.Push(dependencyLabel);
            }
        }

        var ordered = selected.Values
            .Select(target => target with
            {
                Dependencies = target.Dependencies
                    .Select(dependency => NormalizeLabel(dependency, target.Label))
                    .ToArray()
            })
            .OrderBy(target => target.Label, StringComparer.Ordinal)
            .ToArray();
        return format switch
        {
            GraphFormat.Json => JsonSerializer.Serialize(
                new
                {
                    module = graph.Module,
                    buildFiles = graph.BuildFiles,
                    defaultTargets = graph.DefaultTargets,
                    targets = ordered
                },
                GraphJson.Options),
            GraphFormat.Dot => RenderDot(ordered),
            _ => throw new InvalidOperationException("Unknown graph output format.")
        };
    }

    private static string RenderDot(IReadOnlyList<GraphTarget> targets)
    {
        var included = targets.Select(target => target.Label).ToHashSet(StringComparer.Ordinal);
        var output = new StringBuilder();
        var quote = (char)34;
        var slash = (char)92;
        output.AppendLine("digraph shake {");
        output.AppendLine("  rankdir=LR;");

        foreach (var target in targets)
            output.Append("  ").Append(quote).Append(Escape(target.Label)).Append(quote)
                .Append(" [label=").Append(quote).Append(Escape(target.Label)).Append(slash).Append('n')
                .Append(Escape(target.Rule)).Append(quote).AppendLine("];");

        foreach (var target in targets)
        foreach (var dependency in target.Dependencies)
        {
            var label = NormalizeLabel(dependency, target.Label);
            if (included.Contains(label))
                output.Append("  ").Append(quote).Append(Escape(label)).Append(quote)
                    .Append(" -> ").Append(quote).Append(Escape(target.Label)).Append(quote).AppendLine(";");
        }

        output.AppendLine("}");
        return output.ToString();
    }

    private static string NormalizeLabel(string label, string? ownerLabel)
    {
        if (label.StartsWith("//", StringComparison.Ordinal) || label.StartsWith('@')) return label;

        if (label.StartsWith(':') && ownerLabel is not null)
        {
            var separator = ownerLabel.LastIndexOf(':');
            return ownerLabel[..separator] + label;
        }

        return label;
    }

    private static string Escape(string value)
    {
        var slash = (char)92;
        var quote = (char)34;
        return value
            .Replace(slash.ToString(), new string(slash, 2), StringComparison.Ordinal)
            .Replace(quote.ToString(), string.Concat(slash, quote), StringComparison.Ordinal)
            .Replace(Environment.NewLine, string.Concat(slash, 'n'), StringComparison.Ordinal)
            .Replace("\r", string.Concat(slash, 'r'), StringComparison.Ordinal)
            .Replace("\n", string.Concat(slash, 'n'), StringComparison.Ordinal);
    }
}
