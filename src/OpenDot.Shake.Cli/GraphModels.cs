// SPDX-FileCopyrightText: 2026 OpenDot Project <opendotproject.opendot@gmail.com>
// SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception

using System.Text.Json;
using System.Text.Json.Serialization;

namespace OpenDot.Shake.Cli;

internal sealed record RubyWorkerRequest(
    string Mode,
    string RuntimeHome,
    string? Root = null,
    string? ModuleFile = null,
    string[]? BuildFiles = null);

internal sealed record RubyWorkerResponse(bool Success, string? Payload, string? Error);

internal sealed record GraphSnapshot(
    GraphModule Module,
    string[] BuildFiles,
    string[] DefaultTargets,
    GraphTarget[] Targets);

internal sealed record GraphModule(
    JsonElement? Metadata,
    JsonElement[] Dependencies,
    JsonElement[] LocalOverrides,
    string? GoManifest);

internal sealed record GraphTarget(
    string Rule,
    string Name,
    string Label,
    string[] Sources,
    string[] Dependencies,
    string[] Visibility,
    Dictionary<string, JsonElement> Options);

internal static class GraphJson
{
    public static readonly JsonSerializerOptions Options = new()
    {
        PropertyNameCaseInsensitive = true,
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    public static GraphSnapshot Parse(string payload)
    {
        return JsonSerializer.Deserialize<GraphSnapshot>(payload, Options)
               ?? throw new InvalidDataException("RubyHost returned an empty graph document.");
    }
}
