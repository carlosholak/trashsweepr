using System.ComponentModel;
using System.Diagnostics;
using System.Windows;

namespace TrashSweepr.Cleanup.Services;

/// <summary>Reabre o app com permissão de administrador (o trashsweepr só pede isso quando a Limpeza vai alterar o sistema).</summary>
public interface IAppRelauncher
{
    /// <summary>Com <see cref="RelaunchOutcome.Started"/> o novo processo foi iniciado e o atual se encerra em seguida.</summary>
    RelaunchOutcome RelaunchElevated();
}

public enum RelaunchOutcome
{
    Started,

    /// <summary>O usuário recusou o pedido de permissão do Windows.</summary>
    Declined,

    /// <summary>Não deu para reabrir (rodando via dotnet.exe, caminho do exe desconhecido ou falha ao iniciar).</summary>
    Unavailable,
}

/// <summary>Impede que duas janelas apliquem ou desfaçam ajustes ao mesmo tempo.</summary>
public interface IRunGate
{
    /// <summary>Retorna o "bilhete" da vez ou null se outra janela já está executando.</summary>
    IDisposable? TryEnter();
}

public sealed class ElevatedRelauncher : IAppRelauncher
{
    private const int ErrorCancelled = 1223; // ERROR_CANCELLED: o usuário disse "Não" no UAC

    public RelaunchOutcome RelaunchElevated()
    {
        var exe = Environment.ProcessPath;
        if (exe is null || Path.GetFileNameWithoutExtension(exe).Equals("dotnet", StringComparison.OrdinalIgnoreCase))
        {
            return RelaunchOutcome.Unavailable;
        }

        try
        {
            // "runas" mostra o pedido de permissão do Windows (UAC); a seção da Limpeza abre direto.
            Process.Start(new ProcessStartInfo(exe) { UseShellExecute = true, Verb = "runas", Arguments = "--cleanup" })?.Dispose();
        }
        catch (Win32Exception ex) when (ex.NativeErrorCode == ErrorCancelled)
        {
            return RelaunchOutcome.Declined;
        }
        catch (Win32Exception)
        {
            return RelaunchOutcome.Unavailable;
        }

        Application.Current?.Dispatcher.BeginInvoke(() => Application.Current.Shutdown());
        return RelaunchOutcome.Started;
    }
}

/// <summary>Semáforo nomeado da máquina (e não Mutex, que exige liberar na mesma thread que o pegou).</summary>
public sealed class SystemRunGate : IRunGate
{
    private const string Name = @"Global\TrashSweepr.Cleanup.Run";

    public IDisposable? TryEnter()
    {
        Semaphore semaphore;
        try
        {
            semaphore = new Semaphore(1, 1, Name);
        }
        catch (UnauthorizedAccessException)
        {
            return null; // criado por um processo com outras permissões: tratado como "em uso"
        }

        if (!semaphore.WaitOne(0))
        {
            semaphore.Dispose();
            return null;
        }

        return new Lease(semaphore);
    }

    private sealed class Lease(Semaphore semaphore) : IDisposable
    {
        public void Dispose()
        {
            try
            {
                semaphore.Release();
            }
            catch (SemaphoreFullException)
            {
                // já liberado
            }

            semaphore.Dispose();
        }
    }
}