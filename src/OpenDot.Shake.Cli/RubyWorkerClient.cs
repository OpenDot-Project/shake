// SPDX-FileCopyrightText: 2026 OpenDot Project <opendotproject.opendot@gmail.com>
// SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception

using System.Diagnostics;
using System.IO.Pipes;
using System.Text;
using System.Text.Json;

namespace OpenDot.Shake.Cli;

internal static class RubyWorkerClient
{
    private const int MaximumCapturedCharacters = 1024 * 1024;
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public static async Task<RubyWorkerResponse> EvaluateAsync(
        RubyWorkerRequest request,
        CancellationToken cancellationToken)
    {
        var workerPath = Path.Combine(AppContext.BaseDirectory, "OpenDot.Shake.RubyHost.exe");
        if (!File.Exists(workerPath))
            throw new InvalidOperationException($"RubyHost worker executable was not found: {workerPath}");

        var pipeName = $"shake-ruby-{Guid.NewGuid():N}";
        await using var pipe = new NamedPipeServerStream(
            pipeName,
            PipeDirection.In,
            1,
            PipeTransmissionMode.Byte,
            PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);

        var startInfo = new ProcessStartInfo
        {
            FileName = workerPath,
            WorkingDirectory = request.Root ?? Environment.CurrentDirectory,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardInputEncoding = new UTF8Encoding(false),
            StandardOutputEncoding = new UTF8Encoding(false),
            StandardErrorEncoding = new UTF8Encoding(false)
        };
        foreach (var variable in new[]
                 {
                     "RUBYOPT",
                     "RUBYLIB",
                     "RUBY_USER_HOME",
                     "RUBYGEMS_GEMDEPS",
                     "GEM_HOME",
                     "GEM_PATH",
                     "BUNDLE_GEMFILE",
                     "BUNDLE_PATH"
                 })
            startInfo.Environment.Remove(variable);

        startInfo.ArgumentList.Add("--internal-worker");
        startInfo.ArgumentList.Add(pipeName);

        using var process = new Process();
        process.StartInfo = startInfo;
        process.EnableRaisingEvents = true;
        if (!process.Start()) throw new InvalidOperationException("RubyHost worker could not be started.");

        var stdoutTask = ReadBoundedAsync(process.StandardOutput, MaximumCapturedCharacters, cancellationToken);
        var stderrTask = ReadBoundedAsync(process.StandardError, MaximumCapturedCharacters, cancellationToken);
        try
        {
            await process.StandardInput.WriteLineAsync(JsonSerializer.Serialize(request, JsonOptions));
            process.StandardInput.Close();

            var connection = pipe.WaitForConnectionAsync(cancellationToken);
            var exit = process.WaitForExitAsync(cancellationToken);
            var first = await Task.WhenAny(connection, exit);
            if (first == exit && !pipe.IsConnected)
            {
                await exit;
                var stderrBeforeConnection = await stderrTask;
                throw new InvalidOperationException(
                    $"RubyHost exited with code {process.ExitCode} before connecting. {stderrBeforeConnection.Text}");
            }

            await connection;
            using var reader = new StreamReader(pipe, new UTF8Encoding(false));
            var responseLine = await reader.ReadLineAsync(cancellationToken);
            if (responseLine is null)
                throw new InvalidDataException("RubyHost closed its response pipe without sending a result.");

            var response = JsonSerializer.Deserialize<RubyWorkerResponse>(responseLine, JsonOptions)
                           ?? throw new InvalidDataException("RubyHost returned an invalid response.");
            await process.WaitForExitAsync(cancellationToken);
            var stdout = await stdoutTask;
            var stderr = await stderrTask;
            WriteCapturedOutput(stdout, stderr);
            return response;
        }
        catch
        {
            if (!process.HasExited) process.Kill(true);

            throw;
        }
    }

    private static async Task<CapturedOutput> ReadBoundedAsync(
        StreamReader reader,
        int maximumCharacters,
        CancellationToken cancellationToken)
    {
        var buffer = new char[4096];
        var output = new StringBuilder(Math.Min(maximumCharacters, 16_384));
        var truncated = false;

        while (true)
        {
            var count = await reader.ReadAsync(buffer.AsMemory(), cancellationToken);
            if (count == 0) break;

            var remaining = maximumCharacters - output.Length;
            if (remaining > 0) output.Append(buffer, 0, Math.Min(remaining, count));

            truncated |= count > remaining;
        }

        return new CapturedOutput(
            output.ToString(),
            truncated ? Environment.NewLine + "[Shake truncated worker output after 1 MiB.]" : string.Empty);
    }

    private static void WriteCapturedOutput(CapturedOutput stdout, CapturedOutput stderr)
    {
        if (stdout.Text.Length != 0 || stdout.Marker.Length != 0)
        {
            Console.Error.Write(stdout.Text);
            Console.Error.Write(stdout.Marker);
        }

        if (stderr.Text.Length != 0 || stderr.Marker.Length != 0)
        {
            Console.Error.Write(stderr.Text);
            Console.Error.Write(stderr.Marker);
        }
    }

    private sealed record CapturedOutput(string Text, string Marker);
}
