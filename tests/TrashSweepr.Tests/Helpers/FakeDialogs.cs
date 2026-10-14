using TrashSweepr.Cleanup.Services;

namespace TrashSweepr.Tests.Helpers;

/// <summary>Diálogos roteirizados: registra o que foi mostrado e responde o que o teste mandar.</summary>
internal sealed class FakeDialogs : IDialogService
{
    public bool ApproveRisks { get; set; } = true;

    public bool ApproveConfirm { get; set; } = true;

    public List<IReadOnlyList<RiskConfirmationItem>> RiskPrompts { get; } = [];

    public List<(string Title, string Message, string Confirm, bool IsDanger)> ConfirmPrompts { get; } = [];

    public List<(string Title, string Message)> Messages { get; } = [];

    public Task<bool> ConfirmRisksAsync(IReadOnlyList<RiskConfirmationItem> items)
    {
        RiskPrompts.Add(items);
        return Task.FromResult(ApproveRisks);
    }

    public Task<bool> ConfirmAsync(string title, string message, string confirmText, bool isDanger = false)
    {
        ConfirmPrompts.Add((title, message, confirmText, isDanger));
        return Task.FromResult(ApproveConfirm);
    }

    public Task ShowMessageAsync(string title, string message)
    {
        Messages.Add((title, message));
        return Task.CompletedTask;
    }
}

/// <summary>Reabrir como administrador: só conta as chamadas e responde o que o teste mandar.</summary>
internal sealed class FakeRelauncher : IAppRelauncher
{
    public RelaunchOutcome Outcome { get; set; } = RelaunchOutcome.Started;

    public int Calls { get; private set; }

    public RelaunchOutcome RelaunchElevated()
    {
        Calls++;
        return Outcome;
    }
}

/// <summary>Bloqueio entre janelas: "Busy" simula outra janela executando.</summary>
internal sealed class FakeGate : IRunGate
{
    public bool Busy { get; set; }

    public int Active { get; private set; }

    public int Entered { get; private set; }

    public IDisposable? TryEnter()
    {
        if (Busy)
        {
            return null;
        }

        Active++;
        Entered++;
        return new Lease(this);
    }

    private sealed class Lease(FakeGate gate) : IDisposable
    {
        public void Dispose() => gate.Active--;
    }
}
