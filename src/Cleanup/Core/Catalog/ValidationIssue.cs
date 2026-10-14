namespace TrashSweepr.Cleanup.Core.Catalog;

public enum IssueSeverity
{
    Warning,
    Error,
}

/// <param name="Source">Arquivo (ou origem) onde o problema foi encontrado.</param>
/// <param name="TweakId">Id do tweak afetado, quando houver.</param>
public sealed record ValidationIssue(IssueSeverity Severity, string Source, string? TweakId, string Message)
{
    public override string ToString() =>
        $"[{Severity}] {Source}{(TweakId is null ? string.Empty : $" ({TweakId})")}: {Message}";
}
