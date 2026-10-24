// SPDX-FileCopyrightText: 2026 OpenDot Project <opendotproject.opendot@gmail.com>
// SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception

using System.Text;

namespace OpenDot.Shake.Cli;

internal static class ProjectInitializer
{
    public static IReadOnlyList<string> Create(string? requestedName, bool force)
    {
        var root = Path.GetFullPath(Environment.CurrentDirectory);
        var name = requestedName ?? SanitizeDirectoryName(Path.GetFileName(root));
        if (!IsIdentifier(name))
            throw new CommandLineException(
                $"project directory name {Path.GetFileName(root)} cannot be used as a target name; pass --name.");

        var modulePath = Path.Combine(root, "MODULE.rb");
        var buildPath = Path.Combine(root, "BUILD.rb");
        var existing = new[] { modulePath, buildPath }.Where(File.Exists).ToArray();
        if (existing.Length != 0 && !force)
            throw new CommandLineException(
                $"Shake files already exist: {string.Join(", ", existing.Select(Path.GetFileName))}. Pass --force to replace them.");

        var build = $$"""
                      sol_binary "{{name}}" do |app|
                        app.sources "src/main.sol"
                      end
                      """;

        WriteAtomically(modulePath, string.Empty, force);
        WriteAtomically(buildPath, build + Environment.NewLine, force);
        return [Path.GetRelativePath(root, modulePath), Path.GetRelativePath(root, buildPath)];
    }

    private static void WriteAtomically(string path, string contents, bool overwrite)
    {
        var temporary = Path.Combine(Path.GetDirectoryName(path)!, $".{Path.GetFileName(path)}.{Guid.NewGuid():N}.tmp");
        try
        {
            File.WriteAllText(temporary, contents, new UTF8Encoding(false));
            File.Move(temporary, path, overwrite);
        }
        finally
        {
            if (File.Exists(temporary)) File.Delete(temporary);
        }
    }

    private static string SanitizeDirectoryName(string name)
    {
        var value = name.ToLowerInvariant();
        var characters = value.Select(character =>
            character is >= 'a' and <= 'z' or >= '0' and <= '9' or '_' ? character : '_');
        var sanitized = new string(characters.ToArray()).Trim('_');
        return sanitized.Length == 0 || sanitized[0] is < 'a' or > 'z'
            ? "my_project"
            : sanitized;
    }

    private static bool IsIdentifier(string value)
    {
        return value.Length != 0
               && value[0] is >= 'a' and <= 'z'
               && value.All(character => character is >= 'a' and <= 'z' or >= '0' and <= '9' or '_');
    }
}
