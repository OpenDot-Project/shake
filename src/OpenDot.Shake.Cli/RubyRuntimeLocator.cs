// SPDX-FileCopyrightText: 2026 OpenDot Project <opendotproject.opendot@gmail.com>
// SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception

namespace OpenDot.Shake.Cli;

internal sealed record RubyRuntimeLocation(string Home, string Source);

internal static class RubyRuntimeLocator
{
    public static RubyRuntimeLocation? Find(out string? failure)
    {
        failure = null;
        var configuredHome = Environment.GetEnvironmentVariable("SHAKE_RUBY_HOME");
        if (!string.IsNullOrWhiteSpace(configuredHome))
        {
            if (IsRuntimeHome(configuredHome))
                return new RubyRuntimeLocation(Path.GetFullPath(configuredHome), "SHAKE_RUBY_HOME");

            failure = $"SHAKE_RUBY_HOME does not contain one CRuby VM DLL: {configuredHome}";
            return null;
        }

        var bundledHome = Path.Combine(AppContext.BaseDirectory, "runtime", "cruby");
        if (Directory.Exists(bundledHome))
        {
            if (IsRuntimeHome(bundledHome)) return new RubyRuntimeLocation(bundledHome, "Shake runtime bundle");

            failure = $"The bundled CRuby runtime is incomplete: {bundledHome}";
            return null;
        }

        foreach (var directory in (Environment.GetEnvironmentVariable("PATH") ?? string.Empty)
                 .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var rubyExecutable = Path.Combine(directory, "ruby.exe");
            if (!File.Exists(rubyExecutable)) continue;

            var home = Directory.GetParent(directory)?.FullName ?? directory;
            if (IsRuntimeHome(home)) return new RubyRuntimeLocation(home, "PATH development fallback");
        }

        failure =
            "No CRuby runtime was found. Install the Shake runtime bundle or set SHAKE_RUBY_HOME for development.";
        return null;
    }

    private static bool IsRuntimeHome(string home)
    {
        if (!Directory.Exists(home)) return false;

        var bin = Directory.Exists(Path.Combine(home, "bin"))
            ? Path.Combine(home, "bin")
            : home;

        try
        {
            return Directory.EnumerateFiles(bin, "*ruby*.dll", SearchOption.TopDirectoryOnly)
                .Count(IsVmLibrary) == 1;
        }
        catch (IOException)
        {
            return false;
        }
        catch (UnauthorizedAccessException)
        {
            return false;
        }
    }

    private static bool IsVmLibrary(string path)
    {
        var fileName = Path.GetFileName(path);
        return fileName.Contains("ruby", StringComparison.OrdinalIgnoreCase)
               && !fileName.Contains("rubygems", StringComparison.OrdinalIgnoreCase)
               && !fileName.Contains("readline", StringComparison.OrdinalIgnoreCase)
               && !fileName.Contains("openssl", StringComparison.OrdinalIgnoreCase);
    }
}
