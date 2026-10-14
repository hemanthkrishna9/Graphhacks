namespace Ripple.Core.Review;

/// <summary>One contract finding, exactly as a Cypher check returned it, plus the check that produced it.</summary>
public sealed record Finding(
    string Kind,
    string Severity,
    string Check,
    string Contract,
    string? PublisherService,
    string? Publisher,
    string ConsumerService,
    string? Handler,
    string? Reader,
    string? Path,
    string? Expected,
    string? Actual)
{
    /// <summary>Stable identity used to diff base vs head findings.</summary>
    public string Id => string.Join("|", Kind, Contract, PublisherService, ConsumerService, Handler, Path);

    public static Finding FromRow(IReadOnlyDictionary<string, object?> row, string check, string severity) => new(
        Str(row, "kind")!, severity, check, Str(row, "contract")!,
        Str(row, "publisherService"), Str(row, "publisher"),
        Str(row, "consumerService")!, Str(row, "handler"), Str(row, "reader"),
        Str(row, "path"), Str(row, "expected"), Str(row, "actual"));

    static string? Str(IReadOnlyDictionary<string, object?> row, string key) =>
        row.TryGetValue(key, out var v) && v is not null ? v.ToString() : null;
}
