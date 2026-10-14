using System.Text;
using System.Text.Json;
using Anthropic;
using Anthropic.Models.Messages;

namespace Ripple.Core.Review;

/// <summary>Prose around graph findings. It never adds or removes findings.</summary>
public sealed record Narration(string Source, string? Summary, IReadOnlyDictionary<int, FindingExplanation> Explanations)
{
    public FindingExplanation? ForFinding(int number) => Explanations.GetValueOrDefault(number);
}

public sealed record FindingExplanation(string Impact, string? Suggestion);

/// <summary>
/// One Claude call explains what the graph found. The model receives the numbered findings and the diff and returns
/// structured JSON keyed by finding number. Grounding guard: explanations for numbers the graph did not return are
/// dropped, and the report renders the findings and paths from the graph, never from the model. Without
/// ANTHROPIC_API_KEY (or on any API error) Ripple falls back to deterministic template text.
/// </summary>
public static class Narrator
{
    const string Model = "claude-opus-5-5";

    public static async Task<Narration> NarrateAsync(IReadOnlyList<Finding> findings, string diff, Action<string> log)
    {
        var groups = findings.GroupBy(f => (f.Kind, f.Contract, f.ConsumerService, f.Handler)).Select(g => g.ToList()).ToList();
        if (groups.Count == 0) return new Narration("none", null, new Dictionary<int, FindingExplanation>());
        if (string.IsNullOrEmpty(Environment.GetEnvironmentVariable("ANTHROPIC_API_KEY")))
            return Template(groups);

        try
        {
            var client = new AnthropicClient();
            var response = await client.Messages.Create(new MessageCreateParams
            {
                Model = Model,
                MaxTokens = 4000,
                OutputConfig = new OutputConfig { Effort = Effort.Medium, Format = new JsonOutputFormat { Schema = Schema() } },
                System = """
                    You explain the results of a static analysis of a pull request in an event-driven .NET system.
                    The findings are facts produced by graph queries; do not add, remove or dispute findings, and do not
                    mention files, services or fields that are not in them. For each numbered finding, say in one or two
                    sentences what goes wrong at run time for the consuming service (the compiler cannot see it because each
                    service has its own copy of the event type), and suggest the smallest fix (for example keeping the wire
                    name with [JsonPropertyName], or updating the consumer's copy). Then give a one-sentence summary.
                    """,
                Messages = [new() { Role = Role.User, Content = Prompt(groups, diff) }],
            });
            if (response.StopReason == "refusal") return Template(groups);

            var text = string.Concat(response.Content.Select(b => b.Value).OfType<TextBlock>().Select(t => t.Text));
            using var doc = JsonDocument.Parse(text);
            var explanations = new Dictionary<int, FindingExplanation>();
            foreach (var item in doc.RootElement.GetProperty("findings").EnumerateArray())
            {
                var number = item.GetProperty("finding").GetInt32();
                if (number < 1 || number > groups.Count) { log($"narration: dropped explanation for unknown finding {number}"); continue; }   // grounding guard
                explanations[number] = new FindingExplanation(item.GetProperty("impact").GetString() ?? "", item.GetProperty("suggestion").GetString());
            }
            return new Narration($"{Model}, grounded on {groups.Count} graph finding(s)", doc.RootElement.GetProperty("summary").GetString(), explanations);
        }
        catch (Exception e)
        {
            log($"narration failed ({e.GetType().Name}: {e.Message}); using template text");
            return Template(groups);
        }
    }

    static string Prompt(IReadOnlyList<List<Finding>> groups, string diff)
    {
        var sb = new StringBuilder("Findings:\n");
        for (var i = 0; i < groups.Count; i++)
        {
            var f = groups[i][0];
            sb.AppendLine($"{i + 1}. kind={f.Kind} contract={f.Contract} publisher={f.PublisherService} consumer={f.ConsumerService} handler={f.Handler}");
            foreach (var x in groups[i])
            {
                var actual = x.Actual ?? (x.Kind is "dangling-read" or "dangling-forward" ? "missing in publisher" : "n/a");
                sb.AppendLine($"   path={x.Path} expected={x.Expected ?? "n/a"} actual={actual} reader={x.Reader ?? "n/a"}");
            }
        }
        sb.AppendLine("\nDiff:\n" + (diff.Length > 20000 ? diff[..20000] + "\n[diff truncated]" : diff));
        return sb.ToString();
    }

    static Dictionary<string, JsonElement> Schema() => new()
    {
        ["type"] = JsonSerializer.SerializeToElement("object"),
        ["additionalProperties"] = JsonSerializer.SerializeToElement(false),
        ["required"] = JsonSerializer.SerializeToElement(new[] { "summary", "findings" }),
        ["properties"] = JsonSerializer.SerializeToElement(new
        {
            summary = new { type = "string" },
            findings = new
            {
                type = "array",
                items = new
                {
                    type = "object",
                    additionalProperties = false,
                    required = new[] { "finding", "impact", "suggestion" },
                    properties = new
                    {
                        finding = new { type = "integer" },
                        impact = new { type = "string" },
                        suggestion = new { type = "string" },
                    },
                },
            },
        }),
    };

    static Narration Template(IReadOnlyList<List<Finding>> groups)
    {
        var explanations = new Dictionary<int, FindingExplanation>();
        for (var i = 0; i < groups.Count; i++)
        {
            var f = groups[i][0];
            var paths = string.Join(", ", groups[i].Select(x => $"`{x.Path}`").Distinct());
            explanations[i + 1] = f.Kind switch
            {
                "dangling-forward" => new(
                    $"`{f.ConsumerService}` passes its whole copy of `{f.Contract}` on (serialised, e.g. into a webhook payload), including {paths}, " +
                    $"which `{f.PublisherService}` no longer sends. Downstream receivers silently get default values.",
                    "Keep the old wire name with `[JsonPropertyName]` on the publisher, or update every consumer's copy in the same change."),
                "dangling-read" => new(
                    $"`{f.ConsumerService}` reads {paths} from its own copy of `{f.Contract}`, but `{f.PublisherService}` no longer sends it. " +
                    "Deserialisation succeeds and the value is silently the default.",
                    "Keep the old wire name with `[JsonPropertyName]` on the publisher, or update every consumer's copy in the same change."),
                "type-drift" => new(
                    $"`{f.ConsumerService}` cannot deserialise {paths}: it expects `{f.Expected}`, `{f.PublisherService}` now sends `{f.Actual}`. The message fails to process.",
                    "Keep the publisher's wire type, or change the consumers' copies first."),
                "unbindable-constructor" => new(
                    $"`{f.ConsumerService}`'s copy of `{f.Contract}` has a constructor parameter {paths} that matches no property, so System.Text.Json throws for every message.",
                    "Rename the constructor parameter to match its property (or the property back)."),
                "starved-subscription" => new(
                    $"`{f.ConsumerService}` subscribes to `{f.Contract}`, which no service publishes any more, so its handler never runs.",
                    "Keep publishing the old event name until subscribers have moved."),
                "unfillable-read" => new(
                    $"`{f.ConsumerService}` reads {paths}, which deserialisation can never set (no setter or matching constructor parameter).",
                    "Add a setter, an init accessor or a matching constructor parameter."),
                _ => new(f.Kind, null),
            };
        }
        return new Narration("template (set ANTHROPIC_API_KEY for model-written explanations)", null, explanations);
    }
}
