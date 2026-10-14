using System.Globalization;
using TrashSweepr.Limpeza.Core.Abstractions;
using TrashSweepr.Limpeza.Core.Models;

namespace TrashSweepr.Limpeza.Core.Operations;

public enum OperationKind
{
    RegistrySet,
    RegistryDeleteValue,
    RegistryDeleteKey,
    RegistryPruneKeys,
    ServiceSetStartType,
    ServiceStop,
    ServiceStart,
    TaskSetEnabled,
    AppxRemove,
    AppxReinstall,
}

/// <summary>
/// Uma operação concreta sobre o sistema. O dry run mostra exatamente a lista destas operações
/// (<see cref="Command"/>) e o executor roda a mesma lista: não existe um caminho "só de simulação".
/// </summary>
public abstract record PlannedOperation
{
    public abstract OperationKind Kind { get; }

    /// <summary>O que é alterado (chave, serviço, tarefa, pacote), para agrupar e exibir.</summary>
    public abstract string Target { get; }

    /// <summary>Texto técnico e neutro de idioma: o comando/ação exatos.</summary>
    public abstract string Command { get; }

    public override string ToString() => Command;

    protected static string HiveName(RegistryHive hive) => hive == RegistryHive.Hkcu ? "HKCU" : "HKLM";

    protected static string ValueName(string name) => name.Length == 0 ? "(Default)" : name;
}

public sealed record RegistrySetOperation(RegistryHive Hive, string Path, string Name, RegistryValueType Type, object Value)
    : PlannedOperation
{
    public override OperationKind Kind => OperationKind.RegistrySet;

    public override string Target => $"{HiveName(Hive)}\\{Path}\\{ValueName(Name)}";

    public override string Command
    {
        get
        {
            var type = Type switch
            {
                RegistryValueType.Dword => "REG_DWORD",
                RegistryValueType.Qword => "REG_QWORD",
                RegistryValueType.ExpandSz => "REG_EXPAND_SZ",
                _ => "REG_SZ",
            };
            var value = Value is string text ? $"\"{text}\"" : Convert.ToString(Value, CultureInfo.InvariantCulture);
            return $"registry> SET {Target} = {value} ({type})";
        }
    }
}

public sealed record RegistryDeleteValueOperation(RegistryHive Hive, string Path, string Name) : PlannedOperation
{
    public override OperationKind Kind => OperationKind.RegistryDeleteValue;

    public override string Target => $"{HiveName(Hive)}\\{Path}\\{ValueName(Name)}";

    public override string Command => $"registry> DELETE VALUE {Target}";
}

public sealed record RegistryDeleteKeyOperation(RegistryHive Hive, string Path) : PlannedOperation
{
    public override OperationKind Kind => OperationKind.RegistryDeleteKey;

    public override string Target => $"{HiveName(Hive)}\\{Path}";

    public override string Command => $"registry> DELETE KEY (recursive) {Target}";
}

/// <summary>Remove, de baixo para cima, as chaves vazias criadas pelo trashsweepr (da chave até <see cref="StopAtPath"/>).</summary>
public sealed record RegistryPruneKeysOperation(RegistryHive Hive, string Path, string StopAtPath) : PlannedOperation
{
    public override OperationKind Kind => OperationKind.RegistryPruneKeys;

    public override string Target => $"{HiveName(Hive)}\\{Path}";

    public override string Command => $"registry> DELETE EMPTY KEYS {Target} up to {HiveName(Hive)}\\{StopAtPath}";
}

public sealed record ServiceSetStartTypeOperation(string Name, ServiceStartType StartType) : PlannedOperation
{
    public override OperationKind Kind => OperationKind.ServiceSetStartType;

    public override string Target => $"service:{Name}";

    public override string Command => PowerShellCalls.SetServiceStartType(Name, StartType).ToDisplayString();
}

public sealed record ServiceStopOperation(string Name) : PlannedOperation
{
    public override OperationKind Kind => OperationKind.ServiceStop;

    public override string Target => $"service:{Name}";

    public override string Command => PowerShellCalls.StopService(Name).ToDisplayString();
}

public sealed record ServiceStartOperation(string Name) : PlannedOperation
{
    public override OperationKind Kind => OperationKind.ServiceStart;

    public override string Target => $"service:{Name}";

    public override string Command => PowerShellCalls.StartService(Name).ToDisplayString();
}

public sealed record TaskSetEnabledOperation(string TaskPath, string TaskName, bool Enabled) : PlannedOperation
{
    public override OperationKind Kind => OperationKind.TaskSetEnabled;

    public override string Target => $"task:{TaskPath}{TaskName}";

    public override string Command => PowerShellCalls.SetTaskEnabled(TaskPath, TaskName, Enabled).ToDisplayString();
}

public sealed record AppxRemoveOperation(string PackageName) : PlannedOperation
{
    public override OperationKind Kind => OperationKind.AppxRemove;

    public override string Target => $"appx:{PackageName}";

    public override string Command => PowerShellCalls.RemoveAppx(PackageName).ToDisplayString();
}

/// <param name="OnlyIfMissing">Só registra de novo se o pacote não estiver instalado (usado ao restaurar backups).</param>
public sealed record AppxReinstallOperation(string PackageName, bool OnlyIfMissing = false) : PlannedOperation
{
    public override OperationKind Kind => OperationKind.AppxReinstall;

    public override string Target => $"appx:{PackageName}";

    public override string Command => PowerShellCalls.ReinstallAppx(PackageName).ToDisplayString();
}
