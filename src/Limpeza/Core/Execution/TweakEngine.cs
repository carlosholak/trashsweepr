using TrashSweepr.Limpeza.Core.Abstractions;
using TrashSweepr.Limpeza.Core.Backup;
using TrashSweepr.Limpeza.Core.Catalog;
using TrashSweepr.Limpeza.Core.Logging;
using TrashSweepr.Limpeza.Core.Models;
using TrashSweepr.Limpeza.Core.Operations;

namespace TrashSweepr.Limpeza.Core.Execution;

/// <summary>
/// Aplica e reverte tweaks. Ordem de segurança de cada aplicação:
/// compatibilidade, confirmação, validação do plano, detecção de estado, backup gravado em disco,
/// só então as alterações, cada uma verificada; qualquer falha desfaz o que já foi feito.
/// Não é thread-safe: use uma operação por vez.
/// </summary>
public sealed class TweakEngine(
    SystemServices system,
    IRestorePointService restorePoints,
    IWindowsInfo windows,
    BackupStore backups,
    IOperationLog log,
    TimeProvider? timeProvider = null)
{
    private readonly TimeProvider _time = timeProvider ?? TimeProvider.System;
    private readonly OperationExecutor _executor = new(system);

    // ---------- Aplicar ----------

    public async Task<TweakResult> ApplyAsync(Tweak tweak, ExecutionOptions options, CancellationToken ct)
    {
        try
        {
            return await ApplyCoreAsync(tweak, options, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            Write(LogSeverity.Error, tweak.Id, "apply", null, "failed", ex.Message);
            return Failed(tweak.Id, ex.Message);
        }
    }

    private async Task<TweakResult> ApplyCoreAsync(Tweak tweak, ExecutionOptions options, CancellationToken ct)
    {
        var compatibility = CompatibilityChecker.Evaluate(tweak, windows.Build);
        if (!compatibility.IsCompatible)
        {
            Write(LogSeverity.Info, tweak.Id, "apply", null, $"skipped:{compatibility.Status}");
            return Skipped(tweak.Id, SkipReason.Incompatible);
        }

        if (tweak.Risk >= RiskLevel.Moderate && !options.DryRun && !options.ConfirmedTweakIds.Contains(tweak.Id))
        {
            Write(LogSeverity.Warning, tweak.Id, "apply", null, "skipped:not-confirmed");
            return Skipped(tweak.Id, SkipReason.NotConfirmed);
        }

        var plan = OperationPlanner.Plan(tweak.Apply, allowDeleteKey: false);
        if (!plan.IsValid)
        {
            var error = "Ações inválidas: " + string.Join(" ", plan.Errors);
            Write(LogSeverity.Error, tweak.Id, "apply", null, "failed", error);
            return Failed(tweak.Id, error);
        }

        var pending = await FindPendingAsync(plan, new SystemStateReader(system), ct).ConfigureAwait(false);
        if (pending.Operations.Count == 0)
        {
            var reason = pending.AnySatisfied ? SkipReason.AlreadyApplied : SkipReason.NotApplicable;
            Write(LogSeverity.Info, tweak.Id, "apply", null, $"skipped:{reason}");
            return Skipped(tweak.Id, reason);
        }

        if (options.DryRun)
        {
            return DryRun(tweak.Id, pending.Operations, "apply", tweak.RequiresReboot);
        }

        // 1) Backup do estado atual, gravado em disco ANTES de qualquer alteração.
        IReadOnlyList<BackupItem> attemptItems;
        BackupRecord record;
        BackupRecord? existing;
        try
        {
            attemptItems = await BackupBuilder.CaptureAsync(pending.Operations, system, ct).ConfigureAwait(false);
            existing = await backups.GetActiveAsync(tweak.Id, ct).ConfigureAwait(false);
            record = BuildRecord(tweak, existing, attemptItems);
            await backups.SaveAsync(record, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is BackupNotPossibleException or IOException or UnauthorizedAccessException)
        {
            var error = $"Backup não realizado; nada foi alterado. {ex.Message}";
            Write(LogSeverity.Error, tweak.Id, "backup", null, "failed", ex.Message);
            return Failed(tweak.Id, error);
        }

        Write(LogSeverity.Info, tweak.Id, "backup", null, $"saved:{record.Id}");

        // 2) Alterações, uma a uma, cada uma verificada.
        var results = new List<OperationResult>();
        foreach (var operation in pending.Operations)
        {
            try
            {
                await _executor.ExecuteAsync(operation, ct).ConfigureAwait(false);
                await VerifyAsync(operation, ct).ConfigureAwait(false);
                results.Add(new OperationResult(operation, ResultStatus.Success));
                Write(LogSeverity.Info, tweak.Id, "apply", operation.Command, "ok");
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                var message = ex is OperationCanceledException ? "Cancelado pelo usuário." : ex.Message;
                results.Add(new OperationResult(operation, ResultStatus.Failed, message));
                Write(LogSeverity.Error, tweak.Id, "apply", operation.Command, "failed", message);

                var rollbackError = await RollbackAsync(tweak.Id, record, existing is null, attemptItems).ConfigureAwait(false);
                return new TweakResult
                {
                    TweakId = tweak.Id,
                    Status = ResultStatus.Failed,
                    Operations = results,
                    Error = rollbackError is null
                        ? message
                        : $"{message} A restauração automática também falhou ({rollbackError}); use Desfazer para tentar de novo.",
                    RolledBack = rollbackError is null,
                    RequiresReboot = false,
                };
            }
        }

        return new TweakResult
        {
            TweakId = tweak.Id,
            Status = ResultStatus.Success,
            Operations = results,
            RequiresReboot = tweak.RequiresReboot,
        };
    }

    private BackupRecord BuildRecord(Tweak tweak, BackupRecord? existing, IReadOnlyList<BackupItem> attemptItems)
    {
        if (existing is null)
        {
            return new BackupRecord
            {
                Id = Guid.NewGuid().ToString("N"),
                TweakId = tweak.Id,
                CreatedAt = _time.GetUtcNow(),
                RequiresReboot = tweak.RequiresReboot,
                WindowsBuild = windows.Build,
                Items = attemptItems,
            };
        }

        // Já havia um backup ativo (aplicação parcial anterior): preserva o estado ORIGINAL de cada alvo
        // e só acrescenta os alvos novos.
        var known = existing.Items.Select(i => i.Key).ToHashSet(StringComparer.Ordinal);
        return existing with
        {
            RequiresReboot = existing.RequiresReboot || tweak.RequiresReboot,
            Items = [.. existing.Items, .. attemptItems.Where(i => !known.Contains(i.Key))],
        };
    }

    private async Task VerifyAsync(PlannedOperation operation, CancellationToken ct)
    {
        var state = await new SystemStateReader(system).CheckAsync(operation, ct).ConfigureAwait(false);
        if (state == OperationState.NotSatisfied)
        {
            throw new InvalidOperationException("A verificação após a execução falhou: o sistema não ficou no estado esperado.");
        }
    }

    /// <summary>Desfaz o que a tentativa atual já alterou. Retorna a mensagem de erro, ou null se tudo foi restaurado.</summary>
    private async Task<string?> RollbackAsync(string tweakId, BackupRecord record, bool recordIsNew, IReadOnlyList<BackupItem> attemptItems)
    {
        var temp = record with { Items = attemptItems };
        var plan = RestorePlanner.Plan(temp);
        var errors = new List<string>(plan.Errors);

        foreach (var operation in plan.Operations)
        {
            try
            {
                await _executor.ExecuteAsync(operation, CancellationToken.None).ConfigureAwait(false);
                Write(LogSeverity.Info, tweakId, "rollback", operation.Command, "ok");
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                errors.Add(ex.Message);
                Write(LogSeverity.Error, tweakId, "rollback", operation.Command, "failed", ex.Message);
            }
        }

        if (errors.Count == 0 && recordIsNew)
        {
            await MarkRevertedAsync(record).ConfigureAwait(false);
        }

        return errors.Count == 0 ? null : string.Join(" ", errors);
    }

    // ---------- Reverter ----------

    public async Task<TweakResult> RevertAsync(Tweak tweak, ExecutionOptions options, CancellationToken ct)
    {
        try
        {
            var active = await backups.GetActiveAsync(tweak.Id, ct).ConfigureAwait(false);
            return active is not null
                ? await RevertRecordAsync(active, options, ct).ConfigureAwait(false)
                : await RevertToDefaultsAsync(tweak, options, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            Write(LogSeverity.Error, tweak.Id, "revert", null, "failed", ex.Message);
            return Failed(tweak.Id, ex.Message);
        }
    }

    /// <summary>Desfaz um backup específico, mesmo que o tweak não exista mais no catálogo.</summary>
    public async Task<TweakResult> RevertRecordAsync(BackupRecord record, ExecutionOptions options, CancellationToken ct)
    {
        try
        {
            var plan = RestorePlanner.Plan(record);
            if (!plan.IsValid)
            {
                var error = "Backup inválido, nada foi alterado: " + string.Join(" ", plan.Errors);
                Write(LogSeverity.Error, record.TweakId, "revert", null, "failed", error);
                return Failed(record.TweakId, error);
            }

            var operations = plan.Operations.ToList();
            if (options.DryRun)
            {
                return DryRun(record.TweakId, operations, "revert", record.RequiresReboot, usedBackup: true);
            }

            var results = new List<OperationResult>();
            foreach (var operation in operations)
            {
                try
                {
                    await _executor.ExecuteAsync(operation, ct).ConfigureAwait(false);

                    // Como na aplicação: só conta como restaurado se o sistema ficou no estado esperado
                    // (operações sem estado verificável, como iniciar serviço ou podar chaves, passam direto).
                    await VerifyAsync(operation, ct).ConfigureAwait(false);
                    results.Add(new OperationResult(operation, ResultStatus.Success));
                    Write(LogSeverity.Info, record.TweakId, "revert", operation.Command, "ok");
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception ex) when (ex is not OutOfMemoryException)
                {
                    // Restauração é "melhor esforço": continua nos demais itens e mantém o backup ativo para nova tentativa.
                    results.Add(new OperationResult(operation, ResultStatus.Failed, ex.Message));
                    Write(LogSeverity.Error, record.TweakId, "revert", operation.Command, "failed", ex.Message);
                }
            }

            var failures = results.Where(r => r.Status == ResultStatus.Failed).Select(r => r.Error).ToList();
            if (failures.Count > 0)
            {
                return new TweakResult
                {
                    TweakId = record.TweakId,
                    Status = ResultStatus.Failed,
                    Operations = results,
                    Error = string.Join(" ", failures),
                    UsedBackup = true,
                };
            }

            await MarkRevertedAsync(record).ConfigureAwait(false);
            return new TweakResult
            {
                TweakId = record.TweakId,
                Status = ResultStatus.Success,
                Operations = results,
                RequiresReboot = record.RequiresReboot,
                UsedBackup = true,
            };
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            Write(LogSeverity.Error, record.TweakId, "revert", null, "failed", ex.Message);
            return Failed(record.TweakId, ex.Message);
        }
    }

    private async Task<TweakResult> RevertToDefaultsAsync(Tweak tweak, ExecutionOptions options, CancellationToken ct)
    {
        var applyPlan = OperationPlanner.Plan(tweak.Apply, allowDeleteKey: false);
        var revertPlan = OperationPlanner.Plan(tweak.Revert, allowDeleteKey: true);
        if (!applyPlan.IsValid || !revertPlan.IsValid)
        {
            var error = "Ações inválidas: " + string.Join(" ", applyPlan.Errors.Concat(revertPlan.Errors));
            Write(LogSeverity.Error, tweak.Id, "revert", null, "failed", error);
            return Failed(tweak.Id, error);
        }

        // Só reverte se o tweak estiver (ao menos em parte) aplicado.
        var reader = new SystemStateReader(system);
        var applied = await FindPendingAsync(applyPlan, reader, ct).ConfigureAwait(false);
        var anyApplied = applied.AnySatisfied || applied.AnyUnknown;
        if (!anyApplied)
        {
            var reason = applied.Operations.Count > 0 ? SkipReason.NotApplied : SkipReason.NotApplicable;
            Write(LogSeverity.Info, tweak.Id, "revert", null, $"skipped:{reason}");
            return Skipped(tweak.Id, reason);
        }

        var pending = await FindPendingAsync(revertPlan, reader: new SystemStateReader(system), ct).ConfigureAwait(false);
        if (options.DryRun)
        {
            return DryRun(tweak.Id, pending.Operations, "revert", tweak.RequiresReboot);
        }

        var results = new List<OperationResult>();
        foreach (var operation in pending.Operations)
        {
            try
            {
                await _executor.ExecuteAsync(operation, ct).ConfigureAwait(false);
                await VerifyAsync(operation, ct).ConfigureAwait(false);
                results.Add(new OperationResult(operation, ResultStatus.Success));
                Write(LogSeverity.Info, tweak.Id, "revert", operation.Command, "ok");
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                results.Add(new OperationResult(operation, ResultStatus.Failed, ex.Message));
                Write(LogSeverity.Error, tweak.Id, "revert", operation.Command, "failed", ex.Message);
                return new TweakResult { TweakId = tweak.Id, Status = ResultStatus.Failed, Operations = results, Error = ex.Message };
            }
        }

        return new TweakResult
        {
            TweakId = tweak.Id,
            Status = ResultStatus.Success,
            Operations = results,
            RequiresReboot = tweak.RequiresReboot,
        };
    }

    // ---------- Lotes ----------

    public async Task<BatchResult> ApplyAllAsync(
        IReadOnlyList<Tweak> tweaks,
        ExecutionOptions options,
        IProgress<BatchProgress>? progress,
        CancellationToken ct)
    {
        bool? protection = null;
        RestorePointResult? restorePoint = null;

        if (tweaks.Count > 0)
        {
            (protection, restorePoint) = await PrepareRestorePointAsync(options, ct).ConfigureAwait(false);

            var mayProceed = options.DryRun
                || restorePoint is { Outcome: RestorePointOutcome.Created or RestorePointOutcome.Throttled }
                || options.ProceedWithoutRestorePoint;

            if (!mayProceed)
            {
                Write(LogSeverity.Warning, null, "batch", null, "aborted:no-restore-point", restorePoint?.Detail);
                var skipped = tweaks.Select(t => Skipped(t.Id, SkipReason.NoRestorePoint)).ToList();
                return new BatchResult(skipped, restorePoint, protection, Aborted: true);
            }
        }

        var results = new List<TweakResult>();
        foreach (var tweak in tweaks)
        {
            if (ct.IsCancellationRequested)
            {
                break;
            }

            progress?.Report(new BatchProgress(results.Count, tweaks.Count, tweak.Id, null));
            var result = await ApplyAsync(tweak, options, ct).ConfigureAwait(false);
            results.Add(result);
            progress?.Report(new BatchProgress(results.Count, tweaks.Count, tweak.Id, result));
        }

        return new BatchResult(results, restorePoint, protection, Aborted: false);
    }

    /// <summary>Desfaz todos os backups ativos, do mais recente para o mais antigo.</summary>
    public async Task<BatchResult> RevertAllAsync(ExecutionOptions options, IProgress<BatchProgress>? progress, CancellationToken ct)
    {
        var active = await backups.GetAllActiveNewestFirstAsync(ct).ConfigureAwait(false);
        return await RevertAllAsync(active, options, progress, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Desfaz exatamente os backups informados, na ordem dada (ex.: os que a interface mostrou na confirmação).
    /// Cada um é relido do disco pelo id: um backup que deixou de estar ativo é pulado, e um que ganhou itens
    /// depois da confirmação é desfeito por inteiro, na versão atual.
    /// </summary>
    public async Task<BatchResult> RevertAllAsync(
        IReadOnlyList<BackupRecord> records,
        ExecutionOptions options,
        IProgress<BatchProgress>? progress,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(records);

        var active = (await backups.GetAllActiveNewestFirstAsync(ct).ConfigureAwait(false))
            .GroupBy(r => r.Id, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.First(), StringComparer.Ordinal);
        var results = new List<TweakResult>();

        foreach (var requested in records)
        {
            if (ct.IsCancellationRequested)
            {
                break;
            }

            progress?.Report(new BatchProgress(results.Count, records.Count, requested.TweakId, null));

            TweakResult result;
            if (active.TryGetValue(requested.Id, out var current))
            {
                result = await RevertRecordAsync(current, options, ct).ConfigureAwait(false);
            }
            else
            {
                Write(LogSeverity.Info, requested.TweakId, "revert", null, $"skipped:{SkipReason.NotApplied}", $"backup {requested.Id} não está mais ativo");
                result = Skipped(requested.TweakId, SkipReason.NotApplied);
            }

            results.Add(result);
            progress?.Report(new BatchProgress(results.Count, records.Count, requested.TweakId, result));
        }

        return new BatchResult(results, null, null, Aborted: false);
    }

    private async Task<(bool? Protection, RestorePointResult? Result)> PrepareRestorePointAsync(ExecutionOptions options, CancellationToken ct)
    {
        try
        {
            var enabled = await restorePoints.IsProtectionEnabledAsync(ct).ConfigureAwait(false);
            if (!enabled)
            {
                Write(LogSeverity.Warning, null, "restore-point", null, "protection-disabled");
                return (false, new RestorePointResult(RestorePointOutcome.ProtectionDisabled));
            }

            if (options.DryRun)
            {
                // Simulação: confirma que haveria proteção, mas não cria nada.
                return (true, null);
            }

            var created = await restorePoints.CreateAsync(options.RestorePointDescription, ct).ConfigureAwait(false);
            Write(
                created.Outcome == RestorePointOutcome.Failed ? LogSeverity.Error : LogSeverity.Info,
                null,
                "restore-point",
                null,
                created.Outcome.ToString(),
                created.Detail);
            return (true, created);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            Write(LogSeverity.Error, null, "restore-point", null, "failed", ex.Message);
            return (null, new RestorePointResult(RestorePointOutcome.Failed, ex.Message));
        }
    }

    // ---------- Utilitários ----------

    private sealed record PendingOperations(List<PlannedOperation> Operations, bool AnySatisfied, bool AnyUnknown);

    /// <summary>Separa as operações que ainda precisam ser executadas das que já estão satisfeitas.</summary>
    private static async Task<PendingOperations> FindPendingAsync(OperationPlan plan, SystemStateReader reader, CancellationToken ct)
    {
        var pending = new List<PlannedOperation>();
        var anySatisfied = false;
        var anyUnknown = false;

        foreach (var group in plan.Groups)
        {
            var needed = false;
            foreach (var operation in group.Operations)
            {
                switch (await reader.CheckAsync(operation, ct).ConfigureAwait(false))
                {
                    case OperationState.NotSatisfied:
                        needed = true;
                        break;
                    case OperationState.Unknown:
                        needed = true;
                        anyUnknown = true;
                        break;
                    case OperationState.Satisfied:
                        anySatisfied = true;
                        break;
                }
            }

            if (needed)
            {
                pending.AddRange(group.Operations);
            }
        }

        return new PendingOperations(pending, anySatisfied, anyUnknown);
    }

    private TweakResult DryRun(string tweakId, IReadOnlyList<PlannedOperation> operations, string verb, bool requiresReboot, bool usedBackup = false)
    {
        foreach (var operation in operations)
        {
            Write(LogSeverity.Info, tweakId, verb, operation.Command, "dry-run");
        }

        return new TweakResult
        {
            TweakId = tweakId,
            Status = ResultStatus.DryRun,
            Operations = [.. operations.Select(o => new OperationResult(o, ResultStatus.DryRun))],
            RequiresReboot = requiresReboot,
            UsedBackup = usedBackup,
        };
    }

    private async Task MarkRevertedAsync(BackupRecord record)
    {
        try
        {
            await backups.SaveAsync(record with { Status = BackupStatus.Reverted, RevertedAt = _time.GetUtcNow() }, CancellationToken.None)
                .ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Write(LogSeverity.Warning, record.TweakId, "backup", null, "mark-reverted-failed", ex.Message);
        }
    }

    private static TweakResult Skipped(string tweakId, SkipReason reason) =>
        new() { TweakId = tweakId, Status = ResultStatus.Skipped, SkipReason = reason };

    private static TweakResult Failed(string tweakId, string error) =>
        new() { TweakId = tweakId, Status = ResultStatus.Failed, Error = error };

    private void Write(LogSeverity severity, string? tweakId, string operation, string? command, string result, string? error = null) =>
        log.Write(new LogEntry(_time.GetLocalNow(), severity, tweakId, operation, command, result, error));
}
