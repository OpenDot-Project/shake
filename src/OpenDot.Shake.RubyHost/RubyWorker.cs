// SPDX-FileCopyrightText: 2026 OpenDot Project <opendotproject.opendot@gmail.com>
// SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception

using System.IO.Pipes;
using System.Text;
using System.Text.Json;
using OpenDot.Shake.RubyBridge;

namespace OpenDot.Shake.RubyHost;

internal sealed record WorkerRequest(
    string Mode,
    string RuntimeHome,
    string? Root = null,
    string? ModuleFile = null,
    string[]? BuildFiles = null);

internal sealed record WorkerResponse(bool Success, string? Payload = null, string? Error = null);

internal static class RubyWorker
{
    private const int MaximumRequestBytes = 8 * 1024 * 1024;
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public static async Task<int> RunAsync(string[] arguments)
    {
        if (!TryGetPipeName(arguments, out var pipeName)) return 64;

        try
        {
            var requestLine = await Console.In.ReadLineAsync();
            if (requestLine is null || Encoding.UTF8.GetByteCount(requestLine) > MaximumRequestBytes)
                throw new InvalidDataException("RubyHost received an empty or oversized request.");

            var request = JsonSerializer.Deserialize<WorkerRequest>(requestLine, JsonOptions)
                          ?? throw new InvalidDataException("RubyHost received an invalid request document.");

            ClearRubyEnvironment();
            var response = Evaluate(request);
            await WriteResponseAsync(pipeName, response);
            return response.Success ? 0 : 2;
        }
        catch (Exception exception)
        {
            try
            {
                await WriteResponseAsync(pipeName, new WorkerResponse(false, Error: exception.Message));
            }
            catch (IOException)
            {
                Console.Error.WriteLine(exception);
            }

            return 70;
        }
    }

    private static void ClearRubyEnvironment()
    {
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
            Environment.SetEnvironmentVariable(variable, null);
    }

    private static WorkerResponse Evaluate(WorkerRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.RuntimeHome))
            return new WorkerResponse(false, Error: "The CRuby runtime directory was not supplied.");

        var source = request.Mode switch
        {
            "probe" => "$stdout = STDERR\n$stderr = STDERR\nRUBY_VERSION",
            "graph" => CreateGraphProgram(request),
            _ => throw new InvalidDataException($"Unsupported RubyHost mode: {request.Mode}")
        };

        try
        {
            var result = RubyRuntime.Evaluate(request.RuntimeHome, source);
            if (request.Mode == "graph") return DecodeGraphResult(result);

            return new WorkerResponse(true, result);
        }
        catch (Exception exception)
        {
            return new WorkerResponse(false, Error: exception.Message);
        }
    }

    private static string CreateGraphProgram(WorkerRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.Root)
            || string.IsNullOrWhiteSpace(request.ModuleFile)
            || request.BuildFiles is null)
            throw new InvalidDataException(
                "Graph requests must include the workspace root, MODULE.rb, and BUILD.rb paths.");

        var resourceName = typeof(RubyWorker).Assembly
                               .GetManifestResourceNames()
                               .SingleOrDefault(name => name.EndsWith("RubyBootstrap.rb", StringComparison.Ordinal))
                           ?? throw new InvalidOperationException(
                               "The Ruby graph bootstrap resource was not embedded.");

        using var resource = typeof(RubyWorker).Assembly.GetManifestResourceStream(resourceName)
                             ?? throw new InvalidOperationException(
                                 "The Ruby graph bootstrap resource could not be opened.");
        using var reader = new StreamReader(resource, Encoding.UTF8, false);
        var bootstrap = reader.ReadToEnd();

        var payload = JsonSerializer.Serialize(
            new
            {
                root = request.Root,
                module_file = request.ModuleFile,
                build_files = request.BuildFiles
            },
            JsonOptions);

        return $$"""
                 require "json"
                 $stdout = STDERR
                 $stderr = STDERR
                 bootstrap = {{RubyStringLiteral(bootstrap)}}
                 eval(bootstrap, TOPLEVEL_BINDING, "shake-bootstrap.rb", 1)
                 payload = JSON.parse({{RubyStringLiteral(payload)}})
                 begin
                   Shake::Session.evaluate(payload)
                 rescue Exception => error
                   JSON.generate({
                     "__shake_error__" => true,
                     "class" => error.class.name,
                     "message" => error.message,
                     "backtrace" => error.backtrace
                   })
                 end
                 """;
    }

    private static string RubyStringLiteral(string value)
    {
        return JsonSerializer.Serialize(value)
            .Replace("#", "\\#", StringComparison.Ordinal);
    }

    private static WorkerResponse DecodeGraphResult(string result)
    {
        using var document = JsonDocument.Parse(result);
        var root = document.RootElement;
        if (root.TryGetProperty("__shake_error__", out var failed) && failed.GetBoolean())
        {
            var exceptionType = root.TryGetProperty("class", out var type) ? type.GetString() : "RubyError";
            var message = root.TryGetProperty("message", out var detail)
                ? detail.GetString()
                : "Ruby graph evaluation failed.";
            var backtrace = root.TryGetProperty("backtrace", out var frames)
                            && frames.ValueKind == JsonValueKind.Array
                ? string.Join(Environment.NewLine, frames.EnumerateArray().Select(frame => frame.GetString()))
                : string.Empty;
            return new WorkerResponse(false,
                Error: $"{exceptionType}: {message}{Environment.NewLine}{backtrace}".TrimEnd());
        }

        return new WorkerResponse(true, result);
    }

    private static async Task WriteResponseAsync(string pipeName, WorkerResponse response)
    {
        await using var pipe = new NamedPipeClientStream(
            ".",
            pipeName,
            PipeDirection.Out,
            PipeOptions.Asynchronous);

        await pipe.ConnectAsync(10_000);
        await using var writer = new StreamWriter(pipe, new UTF8Encoding(false));
        writer.AutoFlush = true;
        await writer.WriteLineAsync(JsonSerializer.Serialize(response, JsonOptions));
    }

    private static bool TryGetPipeName(string[] arguments, out string pipeName)
    {
        pipeName = string.Empty;
        if (arguments.Length != 2 || arguments[0] != "--internal-worker" ||
            string.IsNullOrWhiteSpace(arguments[1])) return false;

        pipeName = arguments[1];
        return true;
    }
}
