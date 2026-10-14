using TrashSweepr.Cleanup.Core.Abstractions;
using TrashSweepr.Cleanup.Core.Catalog;
using TrashSweepr.Cleanup.Core.Operations;

namespace TrashSweepr.Cleanup.Core.Execution;

public enum ResultStatus
{
    Success,
    Failed,
    Skipped,

    /// <summary>Simulação: nada foi alterado.</summary>
    DryRun,
}

public enum SkipReason
{
    None,
    AlreadyApplied,
    NotApplied,

    /// <summary>O alvo não existe neste sistema (ex.: serviço ou tarefa ausente).</summary>
    NotApplicable,
    Incompatible,

    /// <summary>Tweak Moderado/Avançado sem confirmação explícita do usuário.</summary>
    NotConfirmed,

    /// <summary>Execução abortada porque não foi possível criar o ponto de restauração.</summary>
    NoRestorePoint,
}

public sealed record OperationResult(PlannedOperation Operation, ResultStatus Status, string? Error = null);

public sealed record TweakResult
{
    public required string TweakId { get; init; }

    public required ResultStatus Status { get; init; }

    public SkipReason SkipReason { get; init; }

    /// <summary>Operações executadas (ou, no dry run, que seriam executadas), na ordem.</summary>
    public IReadOnlyList<OperationResult> Operations { get; init; } = [];

    public string? Error { get; init; }

    /// <summary>Houve falha e as alterações já feitas foram desfeitas automaticamente.</summary>
    public bool RolledBack { get; init; }

    public bool RequiresReboot { get; init; }

    /// <summary>A reversão usou o backup do valor anterior (e não os padrões do Windows).</summary>
    public bool UsedBackup { get; init; }
}

public sealed record ExecutionOptions
{
    /// <summary>Mostra o que seria feito sem alterar nada (inclusive sem criar backup ou ponto de restauração).</summary>
    public bool DryRun { get; init; }

    /// <summary>Ids dos tweaks Moderados/Avançados que o usuário confirmou. O engine recusa os demais.</summary>
    public IReadOnlySet<string> ConfirmedTweakIds { get; init; } = new HashSet<string>();

    /// <summary>Permite continuar quando o ponto de restauração não pôde ser criado (proteção desativada ou falha).</summary>
    public bool ProceedWithoutRestorePoint { get; init; }

    public string RestorePointDescription { get; init; } = CoreText.Get("RestorePoint.DefaultDescription");
}

/// <param name="ProtectionEnabled">Resultado da checagem da Proteção do Sistema (null se não houve checagem).</param>
/// <param name="Aborted">Nenhum tweak foi executado porque não havia ponto de restauração e o usuário não autorizou seguir.</param>
public sealed record BatchResult(
    IReadOnlyList<TweakResult> Results,
    RestorePointResult? RestorePoint,
    bool? ProtectionEnabled,
    bool Aborted);

public sealed record BatchProgress(int Completed, int Total, string? CurrentTweakId, TweakResult? LastResult);

public enum TweakAppliedState
{
    NotApplied,
    Applied,

    /// <summary>Parte das ações já está aplicada.</summary>
    Partial,

    /// <summary>Nenhum alvo existe neste sistema, ou o tweak é incompatível com esta versão do Windows.</summary>
    NotApplicable,
    Unknown,
}

public sealed record TweakState(TweakAppliedState Applied, CompatibilityResult Compatibility);
