using System.Text.Json;
using System.Text.Json.Serialization;
using TrashSweepr.Limpeza.Core.Abstractions;
using TrashSweepr.Limpeza.Core.Models;

namespace TrashSweepr.Limpeza.Core.Backup;

public enum BackupStatus
{
    Active,
    Reverted,
}

/// <summary>Estado anterior de um alvo, salvo antes de qualquer alteração.</summary>
[JsonPolymorphic(TypeDiscriminatorPropertyName = "itemType")]
[JsonDerivedType(typeof(RegistryValueBackup), "registryValue")]
[JsonDerivedType(typeof(ServiceBackup), "service")]
[JsonDerivedType(typeof(TaskBackup), "task")]
[JsonDerivedType(typeof(AppxBackup), "appx")]
public abstract record BackupItem
{
    /// <summary>Identifica o alvo; dois itens com a mesma chave guardam o mesmo estado original.</summary>
    [JsonIgnore]
    public abstract string Key { get; }
}

/// <param name="Existed">O valor existia antes?</param>
/// <param name="CreatedKeyRoot">Se a chave não existia, a chave mais alta que o trashsweepr criou (para remover ao desfazer).</param>
public sealed record RegistryValueBackup(
    RegistryHive Hive,
    string Path,
    string Name,
    bool Existed,
    RegistryValueType? Type,
    JsonElement? Value,
    string? CreatedKeyRoot) : BackupItem
{
    public override string Key => $"reg:{Hive}:{Path}:{Name}".ToUpperInvariant();
}

public sealed record ServiceBackup(string Name, bool Existed, ServiceStartType StartType, bool WasRunning) : BackupItem
{
    public override string Key => $"svc:{Name}".ToUpperInvariant();
}

public sealed record TaskBackup(string TaskPath, string TaskName, bool Existed, bool Enabled) : BackupItem
{
    public override string Key => $"task:{TaskPath}{TaskName}".ToUpperInvariant();
}

public sealed record AppxBackup(string PackageName, bool InstalledForCurrentUser, IReadOnlyList<AppxPackageFile> Packages) : BackupItem
{
    public override string Key => $"appx:{PackageName}".ToUpperInvariant();
}

/// <summary>Backup de uma aplicação de tweak: tudo o que é preciso para desfazê-lo.</summary>
public sealed record BackupRecord
{
    public required string Id { get; init; }

    public required string TweakId { get; init; }

    public required DateTimeOffset CreatedAt { get; init; }

    public BackupStatus Status { get; init; } = BackupStatus.Active;

    public DateTimeOffset? RevertedAt { get; init; }

    public bool RequiresReboot { get; init; }

    public int? WindowsBuild { get; init; }

    public required IReadOnlyList<BackupItem> Items { get; init; }
}

public static class BackupJson
{
    public static JsonSerializerOptions Options { get; } = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
    };
}
