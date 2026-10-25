// SPDX-FileCopyrightText: 2026 OpenDot Project <opendotproject.opendot@gmail.com>
// SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception

using System.Text.Json;

namespace OpenDot.Shake.Cli;

internal static class GraphValidator
{
    private static readonly HashSet<string> SupportedRules = new(StringComparer.Ordinal)
    {
        "sol_library",
        "sol_binary",
        "sol_test",
        "c_library",
        "c_binary",
        "c_test",
        "cc_library",
        "cc_binary",
        "cc_test",
        "go_library",
        "go_binary",
        "go_test",
        "filegroup",
        "genrule"
    };

    private static readonly HashSet<string> ExecutableRules = new(StringComparer.Ordinal)
    {
        "sol_binary",
        "sol_test",
        "c_binary",
        "c_test",
        "cc_binary",
        "cc_test",
        "go_binary",
        "go_test"
    };

    public static GraphSnapshot Validate(GraphSnapshot graph, string workspaceRoot)
    {
        ArgumentNullException.ThrowIfNull(graph);
        ArgumentException.ThrowIfNullOrWhiteSpace(workspaceRoot);

        if (graph.Module is null || graph.Targets is null || graph.DefaultTargets is null || graph.BuildFiles is null)
            throw new InvalidDataException("RubyHost returned a graph with missing required collections.");

        var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(workspaceRoot));
        var originalTargets = graph.Targets;
        var targetsByLabel = new Dictionary<string, GraphTarget>(StringComparer.Ordinal);
        foreach (var target in originalTargets)
        {
            ValidateTargetShape(target);
            if (!targetsByLabel.TryAdd(target.Label, target))
                throw new CommandLineException($"duplicate target label {target.Label}.");
        }

        var normalizedTargets = new GraphTarget[originalTargets.Length];
        for (var index = 0; index < originalTargets.Length; index++)
        {
            var target = originalTargets[index];
            var package = PackageOf(target.Label);
            var options = new Dictionary<string, JsonElement>(target.Options, StringComparer.Ordinal);
            var tools = target.Tools
                .Select(tool => NormalizeReference(tool, target.Label, "tool"))
                .ToList();

            if (options.TryGetValue("tool", out var primaryTool))
            {
                if (primaryTool.ValueKind != JsonValueKind.String || primaryTool.GetString() is not { } rawTool)
                    throw new CommandLineException($"{target.Label} option tool must be a target label.");

                var normalizedTool = NormalizeReference(rawTool, target.Label, "tool");
                tools.Add(normalizedTool);
                options["tool"] = JsonSerializer.SerializeToElement(normalizedTool);
            }

            var normalized = target with
            {
                Sources = NormalizePaths(target.Sources, package, root, target.Label, "sources"),
                Headers = NormalizePaths(target.Headers, package, root, target.Label, "headers"),
                IncludeDirectories = NormalizePaths(target.IncludeDirectories, package, root, target.Label,
                    "include_directories"),
                Outputs = NormalizePaths(target.Outputs, package, root, target.Label, "outputs"),
                Dependencies = NormalizeReferences(target.Dependencies, target.Label, "dependency"),
                Tools = tools.Distinct(StringComparer.Ordinal).ToArray(),
                Visibility = NormalizeVisibility(target.Visibility, target.Label),
                Options = options
            };

            ValidateRuleShape(normalized);
            normalizedTargets[index] = normalized;
        }

        targetsByLabel.Clear();
        foreach (var target in normalizedTargets)
            targetsByLabel.Add(target.Label, target);

        ValidateOutputOwnership(normalizedTargets, root);

        foreach (var target in normalizedTargets)
        {
            foreach (var consumer in target.Visibility.Where(value => value is not ("private" or "public")))
                if (!targetsByLabel.ContainsKey(consumer))
                    throw new CommandLineException(
                        $"{target.Label} visibility references unknown consumer target {consumer}.");

            foreach (var dependencyLabel in target.Dependencies)
            {
                var dependency = FindDependency(target, dependencyLabel, targetsByLabel, "dependency");
                if (target.Label == dependencyLabel)
                    throw new CommandLineException($"{target.Label} cannot depend on itself.");

                ValidateVisible(dependency, target);
            }

            foreach (var toolLabel in target.Tools)
            {
                var tool = FindDependency(target, toolLabel, targetsByLabel, "tool");
                if (!ExecutableRules.Contains(tool.Rule))
                    throw new CommandLineException($"{target.Label} tool {toolLabel} is not an executable target.");

                ValidateVisible(tool, target);
            }
        }

        var defaultTargets = graph.DefaultTargets
            .Select(label => NormalizeReference(label, null, "default target"))
            .ToArray();
        if (defaultTargets.Distinct(StringComparer.Ordinal).Count() != defaultTargets.Length)
            throw new CommandLineException("default_targets contains duplicate labels.");

        foreach (var label in defaultTargets)
            if (!targetsByLabel.ContainsKey(label))
                throw new CommandLineException($"default target {label} does not exist in the evaluated graph.");

        EnsureAcyclic(normalizedTargets, targetsByLabel);
        return graph with { Targets = normalizedTargets, DefaultTargets = defaultTargets };
    }

    public static string NormalizeReference(string label, string? ownerLabel, string kind)
    {
        if (string.IsNullOrWhiteSpace(label))
            throw new CommandLineException($"{kind} label cannot be empty.");

        if (label.StartsWith("//", StringComparison.Ordinal))
        {
            ValidateFullyQualifiedLabel(label, kind);
            return label;
        }

        if (label.StartsWith(':'))
        {
            if (ownerLabel is null)
                throw new CommandLineException($"{kind} {label} must be fully qualified.");

            var package = PackageOf(ownerLabel);
            var canonical = $"//{package}{label}";
            ValidateFullyQualifiedLabel(canonical, kind);
            return canonical;
        }

        if (label.StartsWith('@'))
            throw new CommandLineException(
                $"{kind} {label} requires external dependency resolution, which is not implemented yet.");

        throw new CommandLineException(
            $"{kind} {label} must be fully qualified or package-relative with a leading colon.");
    }

    private static void ValidateTargetShape(GraphTarget target)
    {
        if (target is null)
            throw new InvalidDataException("RubyHost returned a null target.");

        if (target.Rule is null || target.Name is null || target.Label is null || target.Sources is null
            || target.Dependencies is null || target.Headers is null || target.IncludeDirectories is null
            || target.Defines is null || target.Arguments is null || target.Outputs is null || target.Tools is null
            || target.Visibility is null || target.Options is null)
            throw new InvalidDataException("RubyHost returned a target with missing required fields.");

        if (!SupportedRules.Contains(target.Rule))
            throw new CommandLineException($"target {target.Label} uses unsupported rule {target.Rule}.");

        ValidateFullyQualifiedLabel(target.Label, "target");
        var separator = target.Label.LastIndexOf(':');
        if (!string.Equals(target.Name, target.Label[(separator + 1)..], StringComparison.Ordinal))
            throw new CommandLineException($"target {target.Label} has inconsistent name {target.Name}.");
    }

    private static void ValidateRuleShape(GraphTarget target)
    {
        if (target.Rule == "filegroup")
        {
            if (target.Sources.Length == 0 && target.Dependencies.Length == 0)
                throw new CommandLineException($"{target.Label} filegroup requires sources or dependencies.");

            return;
        }

        if (target.Rule == "genrule")
        {
            if (target.Outputs.Length == 0)
                throw new CommandLineException($"{target.Label} genrule requires at least one output.");

            if (!target.Options.ContainsKey("tool"))
                throw new CommandLineException($"{target.Label} genrule requires a tool target.");

            return;
        }

        if (target.Sources.Length == 0)
            throw new CommandLineException($"{target.Label} requires at least one source.");
    }

    private static string[] NormalizePaths(string[] paths, string package, string root, string label, string field)
    {
        var normalized = new string[paths.Length];
        for (var index = 0; index < paths.Length; index++)
        {
            var value = paths[index];
            if (string.IsNullOrWhiteSpace(value) || value.Contains('\0'))
                throw new CommandLineException($"{label} {field} entries must be nonempty relative paths.");

            var path = value.Replace('\\', '/');
            if (path.StartsWith('/') || Path.IsPathRooted(value)
                                     || (path.Length >= 2 && char.IsAsciiLetter(path[0]) && path[1] == ':')
                                     || path.Contains('*') || path.Contains('?'))
                throw new CommandLineException(
                    $"{label} {field} path {value} must be relative and must not contain glob characters.");

            var segments = path.Split('/', StringSplitOptions.RemoveEmptyEntries);
            if (segments.Length == 0 || segments.Any(segment => segment == ".."))
                throw new CommandLineException($"{label} {field} path {value} escapes its package directory.");

            var canonical = string.Join('/', segments.Where(segment => segment != "."));
            if (canonical.Length == 0)
                throw new CommandLineException($"{label} {field} path {value} must name a file or directory.");

            var absolute = Path.GetFullPath(Path.Combine(root, package.Replace('/', Path.DirectorySeparatorChar),
                canonical.Replace('/', Path.DirectorySeparatorChar)));
            var prefix = root.EndsWith(Path.DirectorySeparatorChar) ? root : root + Path.DirectorySeparatorChar;
            if (!absolute.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                throw new CommandLineException($"{label} {field} path {value} escapes the workspace.");

            normalized[index] = canonical;
        }

        return normalized;
    }

    private static void ValidateOutputOwnership(IReadOnlyList<GraphTarget> targets, string root)
    {
        var owners = new Dictionary<string, (string Label, string RelativePath)>(StringComparer.OrdinalIgnoreCase);
        foreach (var target in targets)
        {
            var package = PackageOf(target.Label).Replace('/', Path.DirectorySeparatorChar);
            foreach (var output in target.Outputs)
            {
                var absolute = Path.GetFullPath(Path.Combine(root, package,
                    output.Replace('/', Path.DirectorySeparatorChar)));
                if (owners.TryGetValue(absolute, out var existing))
                    throw new CommandLineException(
                        $"output {output} from {target.Label} conflicts with output {existing.RelativePath} from {existing.Label}.");

                owners.Add(absolute, (target.Label, output));
            }
        }
    }

    private static string[] NormalizeReferences(string[] references, string? ownerLabel, string kind)
    {
        return references
            .Select(reference => NormalizeReference(reference, ownerLabel, kind))
            .Distinct(StringComparer.Ordinal)
            .ToArray();
    }

    private static string[] NormalizeVisibility(string[] visibility, string ownerLabel)
    {
        if (visibility.Length == 0)
            throw new CommandLineException($"{ownerLabel} visibility cannot be empty.");

        var normalized = visibility.Select(value => value switch
        {
            "private" => "private",
            "public" => "public",
            _ => NormalizeReference(value, ownerLabel, "visibility consumer")
        }).Distinct(StringComparer.Ordinal).ToArray();

        if (normalized.Contains("private", StringComparer.Ordinal)
            && normalized.Contains("public", StringComparer.Ordinal))
            throw new CommandLineException($"{ownerLabel} visibility cannot contain both private and public.");

        return normalized;
    }

    private static GraphTarget FindDependency(
        GraphTarget owner,
        string dependencyLabel,
        IReadOnlyDictionary<string, GraphTarget> targetsByLabel,
        string kind)
    {
        if (!targetsByLabel.TryGetValue(dependencyLabel, out var dependency))
            throw new CommandLineException(
                $"{owner.Label} {kind} {dependencyLabel} does not exist in the evaluated graph.");

        return dependency;
    }

    private static void ValidateVisible(GraphTarget dependency, GraphTarget consumer)
    {
        var visibility = dependency.Visibility;
        var samePackage =
            string.Equals(PackageOf(dependency.Label), PackageOf(consumer.Label), StringComparison.Ordinal);
        if (visibility.Contains("public", StringComparer.Ordinal)
            || (samePackage && visibility.Contains("private", StringComparer.Ordinal))
            || visibility.Contains(consumer.Label, StringComparer.Ordinal))
            return;

        throw new CommandLineException(
            $"{consumer.Label} cannot depend on {dependency.Label}; the target is not visible to it.");
    }

    private static void EnsureAcyclic(
        IReadOnlyList<GraphTarget> targets,
        IReadOnlyDictionary<string, GraphTarget> targetsByLabel)
    {
        var states = new Dictionary<string, VisitState>(StringComparer.Ordinal);
        var path = new List<string>();
        var pathPositions = new Dictionary<string, int>(StringComparer.Ordinal);
        var stack = new Stack<TraversalFrame>();

        foreach (var root in targets)
        {
            if (states.ContainsKey(root.Label))
                continue;

            Push(root);
            while (stack.TryPeek(out var frame))
            {
                if (frame.NextEdgeIndex < frame.Edges.Length)
                {
                    var edge = frame.Edges[frame.NextEdgeIndex++];
                    if (!targetsByLabel.TryGetValue(edge, out var dependency))
                        continue;

                    if (states.TryGetValue(edge, out var state))
                    {
                        if (state == VisitState.Visiting)
                        {
                            var start = pathPositions[edge];
                            var cycle = path.Skip(start).Append(edge);
                            throw new CommandLineException($"dependency cycle detected: {string.Join(" -> ", cycle)}.");
                        }

                        continue;
                    }

                    Push(dependency);
                    continue;
                }

                stack.Pop();
                states[frame.Label] = VisitState.Visited;
                pathPositions.Remove(frame.Label);
                path.RemoveAt(path.Count - 1);
            }
        }

        void Push(GraphTarget target)
        {
            states.Add(target.Label, VisitState.Visiting);
            pathPositions.Add(target.Label, path.Count);
            path.Add(target.Label);
            var edges = target.Dependencies.Concat(target.Tools).Distinct(StringComparer.Ordinal).ToArray();
            stack.Push(new TraversalFrame(target.Label, edges));
        }
    }

    private static string PackageOf(string label)
    {
        ValidateFullyQualifiedLabel(label, "target");
        var separator = label.LastIndexOf(':');
        return label[2..separator];
    }

    private static void ValidateFullyQualifiedLabel(string label, string kind)
    {
        if (!label.StartsWith("//", StringComparison.Ordinal))
            throw new CommandLineException($"{kind} label {label} must start with //.");

        var separator = label.IndexOf(':', 2);
        if (separator < 0 || separator != label.LastIndexOf(':') || separator == label.Length - 1)
            throw new CommandLineException($"{kind} label {label} must use //package:name form.");

        var package = label[2..separator];
        if (package.Length != 0)
        {
            var segments = package.Split('/');
            if (segments.Any(segment => segment.Length == 0 || segment is "." or ".."))
                throw new CommandLineException($"{kind} label {label} contains an invalid package path.");
        }

        var name = label[(separator + 1)..];
        if (name[0] is < 'a' or > 'z'
            || name.Any(character => character is not (>= 'a' and <= 'z' or >= '0' and <= '9' or '_')))
            throw new CommandLineException($"{kind} label {label} has an invalid target name.");
    }

    private enum VisitState
    {
        Visiting,
        Visited
    }

    private sealed class TraversalFrame(string label, string[] edges)
    {
        public string Label { get; } = label;

        public string[] Edges { get; } = edges;

        public int NextEdgeIndex { get; set; }
    }
}
