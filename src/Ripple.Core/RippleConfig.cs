using System.Text.Json;

namespace Ripple.Core;

/// <summary>Which repo to index and which projects are deployable services.</summary>
public sealed record RippleConfig(string Graph, IReadOnlyList<string> Services)
{
    public static RippleConfig Load(string path) =>
        JsonSerializer.Deserialize<RippleConfig>(File.ReadAllText(path), new JsonSerializerOptions(JsonSerializerDefaults.Web))
        ?? throw new InvalidOperationException($"Empty config {path}");
}
