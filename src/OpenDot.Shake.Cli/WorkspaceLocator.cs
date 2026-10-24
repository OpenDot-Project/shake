// SPDX-FileCopyrightText: 2026 OpenDot Project <opendotproject.opendot@gmail.com>
// SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception

namespace OpenDot.Shake.Cli;

internal sealed record ShakeWorkspace(string Root, string ModuleFile, IReadOnlyList<string> BuildFiles);

internal static class WorkspaceLocator
{
    private static readonly HashSet<string> ExcludedDirectories = new(StringComparer.OrdinalIgnoreCase)
    {
        ".git",
        ".codex",
        "bin",
        "obj",
        "out",
        "vendor"
    };

    public static ShakeWorkspace FindFromCurrentDirectory()
    {
        var current = new DirectoryInfo(Path.GetFullPath(Environment.CurrentDirectory));
        while (current is not null)
        {
            var moduleFile = Path.Combine(current.FullName, "MODULE.rb");
            if (File.Exists(moduleFile))
                return new ShakeWorkspace(
                    current.FullName,
                    moduleFile,
                    FindBuildFiles(current.FullName));

            current = current.Parent;
        }

        throw new CommandLineException("no MODULE.rb was found in the current directory or its parents.");
    }

    private static IReadOnlyList<string> FindBuildFiles(string root)
    {
        var paths = new List<string>();
        var pending = new Stack<string>();
        pending.Push(root);

        while (pending.TryPop(out var directory))
        {
            var fullDirectory = Path.GetFullPath(directory);
            foreach (var file in Directory.EnumerateFiles(fullDirectory, "BUILD.rb", SearchOption.TopDirectoryOnly))
                paths.Add(Path.GetRelativePath(root, file).Replace(Path.DirectorySeparatorChar, '/'));

            foreach (var child in Directory.EnumerateDirectories(fullDirectory, "*", SearchOption.TopDirectoryOnly))
            {
                var name = Path.GetFileName(child);
                if (ExcludedDirectories.Contains(name) || IsReparsePoint(child)) continue;

                if (!Path.GetFullPath(child).Equals(root, StringComparison.OrdinalIgnoreCase)
                    && File.Exists(Path.Combine(child, "MODULE.rb")))
                    continue;

                pending.Push(child);
            }
        }

        paths.Sort(StringComparer.Ordinal);
        return paths;
    }

    private static bool IsReparsePoint(string path)
    {
        try
        {
            return (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0;
        }
        catch (IOException)
        {
            return true;
        }
        catch (UnauthorizedAccessException)
        {
            return true;
        }
    }
}
