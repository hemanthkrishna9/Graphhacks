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
    string? Actual,
    string? PublisherFile = null,
    int? PublisherLine = null,
    string? ReaderFile = null,
    int? ReaderLine = null)
{
    /// <summary>
    /// Stable identity used to diff base vs head findings: what breaks, where, and how — not which handler class
    /// (renaming a handler must not look like one finding resolved and another introduced).
    /// </summary>
    public string Id => string.Join("|", Kind, Contract, PublisherService, ConsumerService, Path, Expected, Actual);

    public static Finding FromRow(IReadOnlyDictionary<string, object?> row, string check, string severity) => new(
        Str(row, "kind")!, severity, check, Str(row, "contract")!,
        Str(row, "publisherService"), Str(row, "publisher"),
        Str(row, "consumerService")!, Str(row, "handler"), Str(row, "reader"),
        Str(row, "path"), Str(row, "expected"), Str(row, "actual"),
        Str(row, "publisherFile"), Int(row, "publisherLine"), Str(row, "readerFile"), Int(row, "readerLine"));

    static int? Int(IReadOnlyDictionary<string, object?> row, string key) =>
        row.TryGetValue(key, out var v) && v is not null ? Convert.ToInt32(v) : null;

    static string? Str(IReadOnlyDictionary<string, object?> row, string key) =>
        row.TryGetValue(key, out var v) && v is not null ? v.ToString() : null;
}
