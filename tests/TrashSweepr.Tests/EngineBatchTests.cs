using TrashSweepr.Limpeza.Core.Abstractions;
using TrashSweepr.Limpeza.Core.Catalog;
using TrashSweepr.Limpeza.Core.Execution;
using TrashSweepr.Limpeza.Core.Models;
using TrashSweepr.Tests.Helpers;
using static TrashSweepr.Tests.Helpers.TestTweaks;

namespace TrashSweepr.Tests;

public sealed class EngineBatchTests : IDisposable
{
    private readonly FakeSystem _sys = new();

    public void Dispose() => _sys.Dispose();

    private static Tweak[] TwoTweaks() => [RegistryTweak("test.one", name: "A"), RegistryTweak("test.two", name: "B")];

    [Fact]
    public async Task One_restore_point_is_created_before_the_whole_batch()
    {
        var batch = await _sys.Engine.ApplyAllAsync(TwoTweaks(), FakeSystem.Real, null, Ct);

        Assert.Equal(1, _sys.RestorePoints.CreateCalls);
        Assert.Equal(RestorePointOutcome.Created, batch.RestorePoint!.Outcome);
        Assert.True(batch.ProtectionEnabled);
        Assert.False(batch.Aborted);
        Assert.All(batch.Results, r => Assert.Equal(ResultStatus.Success, r.Status));
    }

    [Fact]
    public async Task Disabled_protection_aborts_the_batch_without_changing_anything()
    {
        _sys.RestorePoints.ProtectionEnabled = false;

        var batch = await _sys.Engine.ApplyAllAsync(TwoTweaks(), FakeSystem.Real, null, Ct);

        Assert.True(batch.Aborted);
        Assert.False(batch.ProtectionEnabled);
        Assert.Equal(RestorePointOutcome.ProtectionDisabled, batch.RestorePoint!.Outcome);
        Assert.All(batch.Results, r => Assert.Equal(SkipReason.NoRestorePoint, r.SkipReason));
        Assert.Empty(_sys.Mutations);
        Assert.Equal(0, _sys.RestorePoints.CreateCalls);
    }

    [Fact]
    public async Task Disabled_protection_proceeds_only_with_explicit_permission()
    {
        _sys.RestorePoints.ProtectionEnabled = false;
        var options = new ExecutionOptions { ProceedWithoutRestorePoint = true };

        var batch = await _sys.Engine.ApplyAllAsync(TwoTweaks(), options, null, Ct);

        Assert.False(batch.Aborted);
        Assert.Equal(RestorePointOutcome.ProtectionDisabled, batch.RestorePoint!.Outcome);
        Assert.All(batch.Results, r => Assert.Equal(ResultStatus.Success, r.Status));
        Assert.Equal(0, _sys.RestorePoints.CreateCalls);
    }

    [Fact]
    public async Task A_recent_restore_point_is_enough_to_proceed()
    {
        _sys.RestorePoints.CreateResult = new RestorePointResult(RestorePointOutcome.Throttled);

        var batch = await _sys.Engine.ApplyAllAsync(TwoTweaks(), FakeSystem.Real, null, Ct);

        Assert.False(batch.Aborted);
        Assert.Equal(RestorePointOutcome.Throttled, batch.RestorePoint!.Outcome);
        Assert.All(batch.Results, r => Assert.Equal(ResultStatus.Success, r.Status));
    }

    [Fact]
    public async Task A_failed_restore_point_aborts_unless_the_user_allows_proceeding()
    {
        _sys.RestorePoints.CreateResult = new RestorePointResult(RestorePointOutcome.Failed, "erro do VSS");

        var aborted = await _sys.Engine.ApplyAllAsync(TwoTweaks(), FakeSystem.Real, null, Ct);
        Assert.True(aborted.Aborted);
        Assert.Empty(_sys.Mutations);

        var proceeded = await _sys.Engine.ApplyAllAsync(TwoTweaks(), new ExecutionOptions { ProceedWithoutRestorePoint = true }, null, Ct);
        Assert.False(proceeded.Aborted);
    }

    [Fact]
    public async Task Dry_run_never_creates_a_restore_point_but_reports_the_protection_state()
    {
        var enabled = await _sys.Engine.ApplyAllAsync(TwoTweaks(), FakeSystem.Dry, null, Ct);
        Assert.Equal(0, _sys.RestorePoints.CreateCalls);
        Assert.True(enabled.ProtectionEnabled);
        Assert.All(enabled.Results, r => Assert.Equal(ResultStatus.DryRun, r.Status));

        _sys.RestorePoints.ProtectionEnabled = false;
        var disabled = await _sys.Engine.ApplyAllAsync(TwoTweaks(), FakeSystem.Dry, null, Ct);
        Assert.False(disabled.ProtectionEnabled);
        Assert.False(disabled.Aborted);
        Assert.Empty(_sys.Mutations);
    }

    [Fact]
    public async Task Restore_point_service_errors_are_handled()
    {
        var engine = new TweakEngine(_sys.Bundle, new ThrowingRestorePoints(), _sys.Windows, _sys.Backups, _sys.Log, _sys.Time);

        var batch = await engine.ApplyAllAsync(TwoTweaks(), FakeSystem.Real, null, Ct);

        Assert.True(batch.Aborted);
        Assert.Equal(RestorePointOutcome.Failed, batch.RestorePoint!.Outcome);
    }

    [Fact]
    public async Task One_failing_tweak_does_not_stop_the_others()
    {
        _sys.Registry.FailOnSet = target => target.EndsWith(@"\A", StringComparison.Ordinal);

        var batch = await _sys.Engine.ApplyAllAsync(TwoTweaks(), FakeSystem.Real, null, Ct);

        Assert.Equal([ResultStatus.Failed, ResultStatus.Success], batch.Results.Select(r => r.Status));
        Assert.Equal(0, _sys.Registry.Get(RegistryHive.Hkcu, Key, "B"));
    }

    [Fact]
    public async Task Progress_is_reported_before_and_after_each_tweak()
    {
        var progress = new SyncProgress<BatchProgress>();

        await _sys.Engine.ApplyAllAsync(TwoTweaks(), FakeSystem.Real, progress, Ct);

        Assert.Equal(4, progress.Reports.Count);
        Assert.Equal([0, 1, 1, 2], progress.Reports.Select(r => r.Completed));
        Assert.All(progress.Reports, r => Assert.Equal(2, r.Total));
        Assert.Equal("test.one", progress.Reports[0].CurrentTweakId);
        Assert.Null(progress.Reports[0].LastResult);
        Assert.NotNull(progress.Reports[1].LastResult);
    }

    [Fact]
    public async Task Cancelling_stops_before_the_next_tweak()
    {
        using var cts = new CancellationTokenSource();
        var progress = new CancelAfterFirst(cts);

        var batch = await _sys.Engine.ApplyAllAsync(TwoTweaks(), FakeSystem.Real, progress, cts.Token);

        Assert.Single(batch.Results);
        Assert.Null(_sys.Registry.Get(RegistryHive.Hkcu, Key, "B"));
    }

    [Fact]
    public async Task Empty_batch_does_not_create_a_restore_point()
    {
        var batch = await _sys.Engine.ApplyAllAsync([], FakeSystem.Real, null, Ct);

        Assert.Empty(batch.Results);
        Assert.Equal(0, _sys.RestorePoints.CreateCalls);
    }

    [Fact]
    public async Task Unconfirmed_moderate_tweaks_in_a_batch_are_skipped_not_applied()
    {
        var moderate = RegistryTweak("test.mod", name: "M", risk: "moderate", riskNote: "Risco.");

        var batch = await _sys.Engine.ApplyAllAsync([RegistryTweak("test.safe", name: "S"), moderate], FakeSystem.Real, null, Ct);

        Assert.Equal(ResultStatus.Success, batch.Results[0].Status);
        Assert.Equal(SkipReason.NotConfirmed, batch.Results[1].SkipReason);
        Assert.Null(_sys.Registry.Get(RegistryHive.Hkcu, Key, "M"));
    }

    private sealed class ThrowingRestorePoints : IRestorePointService
    {
        public Task<bool> IsProtectionEnabledAsync(CancellationToken cancellationToken) =>
            throw new InvalidOperationException("WMI indisponível.");

        public Task<RestorePointResult> CreateAsync(string description, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("WMI indisponível.");
    }

    private sealed class CancelAfterFirst(CancellationTokenSource cts) : IProgress<BatchProgress>
    {
        public void Report(BatchProgress value)
        {
            if (value.LastResult is not null)
            {
                cts.Cancel();
            }
        }
    }
}

public sealed class TweakStateDetectorTests : IDisposable
{
    private readonly FakeSystem _sys = new();

    public void Dispose() => _sys.Dispose();

    private async Task<TweakAppliedState> DetectAsync(Tweak tweak) =>
        (await _sys.Detector.DetectAsync([tweak], Ct))[tweak.Id].Applied;

    [Fact]
    public async Task Registry_tweak_states()
    {
        var tweak = RegistryTweak();
        Assert.Equal(TweakAppliedState.NotApplied, await DetectAsync(tweak));

        _sys.Registry.Seed(RegistryHive.Hkcu, Key, "V", RegistryValueType.Dword, 5);
        Assert.Equal(TweakAppliedState.NotApplied, await DetectAsync(tweak));

        _sys.Registry.Seed(RegistryHive.Hkcu, Key, "V", RegistryValueType.Dword, 0);
        Assert.Equal(TweakAppliedState.Applied, await DetectAsync(tweak));
    }

    [Fact]
    public async Task A_value_of_the_wrong_type_does_not_count_as_applied()
    {
        _sys.Registry.Seed(RegistryHive.Hkcu, Key, "V", RegistryValueType.Sz, "0");

        Assert.Equal(TweakAppliedState.NotApplied, await DetectAsync(RegistryTweak()));
    }

    [Fact]
    public async Task String_values_compare_ignoring_case()
    {
        var tweak = Make(
            "test.str",
            CatalogJson.Registry(path: Key, name: "S", kind: "sz", valueJson: "\"Abc\""),
            CatalogJson.Registry(path: Key, name: "S", kind: "sz", valueJson: "\"x\""));
        _sys.Registry.Seed(RegistryHive.Hkcu, Key, "S", RegistryValueType.Sz, "aBC");

        Assert.Equal(TweakAppliedState.Applied, await DetectAsync(tweak));
    }

    [Fact]
    public async Task Partially_applied_tweak_is_reported_as_partial()
    {
        var tweak = Make(
            "test.two",
            CatalogJson.Registry(path: Key, name: "A", valueJson: "0") + "," + CatalogJson.Registry(path: Key, name: "B", valueJson: "0"),
            CatalogJson.Registry(path: Key, name: "A", valueJson: "1"));
        _sys.Registry.Seed(RegistryHive.Hkcu, Key, "A", RegistryValueType.Dword, 0);

        Assert.Equal(TweakAppliedState.Partial, await DetectAsync(tweak));
    }

    [Fact]
    public async Task Missing_service_is_not_applicable_and_disabled_service_is_applied()
    {
        Assert.Equal(TweakAppliedState.NotApplicable, await DetectAsync(ServiceTweak()));

        _sys.Services.Services["FooSvc"] = new ServiceState(true, ServiceStartType.Disabled, false);
        Assert.Equal(TweakAppliedState.Applied, await DetectAsync(ServiceTweak()));
    }

    [Fact]
    public async Task Task_states()
    {
        var tweak = Make(
            "test.task",
            CatalogJson.SchedTask(@"\Limpeza\Test\", "Job", enabled: false),
            CatalogJson.SchedTask(@"\Limpeza\Test\", "Job", enabled: true));
        _sys.Tasks.Tasks[@"\Limpeza\Test\Job"] = new TaskState(true, true);
        Assert.Equal(TweakAppliedState.NotApplied, await DetectAsync(tweak));

        _sys.Tasks.Tasks[@"\Limpeza\Test\Job"] = new TaskState(true, false);
        Assert.Equal(TweakAppliedState.Applied, await DetectAsync(tweak));
    }

    [Fact]
    public async Task Absent_app_counts_as_applied_and_installed_app_as_not_applied()
    {
        Assert.Equal(TweakAppliedState.Applied, await DetectAsync(AppTweak()));

        _sys.Appx.Install("Contoso.Sample");
        Assert.Equal(TweakAppliedState.NotApplied, await DetectAsync(AppTweak()));
    }

    [Fact]
    public async Task Incompatible_tweaks_are_marked_not_applicable_with_the_reason()
    {
        var tweak = Make("test.new", CatalogJson.Registry(path: Key, valueJson: "0"), CatalogJson.Registry(path: Key, valueJson: "1"), extra: ", \"minBuild\": 26100");

        var state = (await _sys.Detector.DetectAsync([tweak], Ct))[tweak.Id];

        Assert.Equal(TweakAppliedState.NotApplicable, state.Applied);
        Assert.Equal(CompatibilityStatus.BuildTooOld, state.Compatibility.Status);
        Assert.Equal(26100, state.Compatibility.RequiredBuild);
    }

    [Fact]
    public async Task Many_apps_are_detected_with_a_single_package_listing()
    {
        var tweaks = Enumerable.Range(0, 10).Select(i => AppTweak($"test.app{i}", $"Contoso.App{i}")).ToArray();
        foreach (var i in Enumerable.Range(0, 10))
        {
            _sys.Appx.Install($"Contoso.App{i}");
        }

        var states = await _sys.Detector.DetectAsync(tweaks, Ct);

        Assert.Equal(1, _sys.Appx.ListCalls);
        Assert.All(states.Values, s => Assert.Equal(TweakAppliedState.NotApplied, s.Applied));
    }

    [Fact]
    public async Task Detection_never_changes_anything_and_survives_read_errors()
    {
        var tweak = Unvalidated(new ServiceAction { Name = "wuauserv", StartType = ServiceStartType.Disabled });

        var state = (await _sys.Detector.DetectAsync([tweak, RegistryTweak()], Ct));

        Assert.Equal(TweakAppliedState.Unknown, state["test.unvalidated"].Applied);
        Assert.Equal(TweakAppliedState.NotApplied, state["test.reg"].Applied);
        Assert.Empty(_sys.Mutations);
    }
}
