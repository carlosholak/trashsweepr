using System.Runtime.Versioning;
using Microsoft.Win32;
using TrashSweepr.Cleanup.Core;
using TrashSweepr.Cleanup.Core.Abstractions;
using TrashSweepr.Cleanup.Core.Models;
using RegistryHive = TrashSweepr.Cleanup.Core.Models.RegistryHive;

namespace TrashSweepr.Cleanup.Infrastructure;

/// <summary>
/// Acesso direto ao registro via Microsoft.Win32 (sem PowerShell). Sempre usa a visão de 64 bits.
/// Os caminhos já chegam validados pelo engine; este serviço só recusa o que for vazio.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class RegistryService : IRegistryService
{
    public RegistryValueSnapshot ReadValue(RegistryHive hive, string path, string name)
    {
        using var root = OpenRoot(hive);
        using var key = root.OpenSubKey(RequirePath(path), writable: false);
        if (key is null)
        {
            return RegistryValueSnapshot.Missing;
        }

        // DoNotExpandEnvironmentNames: o backup guarda o texto original (%SystemRoot%...), não o expandido.
        var value = key.GetValue(name, null, RegistryValueOptions.DoNotExpandEnvironmentNames);
        if (value is null)
        {
            return RegistryValueSnapshot.Missing;
        }

        return key.GetValueKind(name) switch
        {
            RegistryValueKind.DWord => new RegistryValueSnapshot(true, RegistryValueType.Dword, value),
            RegistryValueKind.QWord => new RegistryValueSnapshot(true, RegistryValueType.Qword, value),
            RegistryValueKind.String => new RegistryValueSnapshot(true, RegistryValueType.Sz, value),
            RegistryValueKind.ExpandString => new RegistryValueSnapshot(true, RegistryValueType.ExpandSz, value),
            var other => new RegistryValueSnapshot(true, null, null, other.ToString()),
        };
    }

    public bool KeyExists(RegistryHive hive, string path)
    {
        using var root = OpenRoot(hive);
        using var key = root.OpenSubKey(RequirePath(path), writable: false);
        return key is not null;
    }

    public void SetValue(RegistryHive hive, string path, string name, RegistryValueType type, object value)
    {
        using var root = OpenRoot(hive);
        using var key = root.CreateSubKey(RequirePath(path), writable: true)
            ?? throw new IOException(CoreText.Format("Registry.CannotOpenKey", $"{hive}\\{path}"));

        var kind = type switch
        {
            RegistryValueType.Dword => RegistryValueKind.DWord,
            RegistryValueType.Qword => RegistryValueKind.QWord,
            RegistryValueType.ExpandSz => RegistryValueKind.ExpandString,
            _ => RegistryValueKind.String,
        };

        key.SetValue(name, value, kind);
    }

    public void DeleteValue(RegistryHive hive, string path, string name)
    {
        using var root = OpenRoot(hive);
        using var key = root.OpenSubKey(RequirePath(path), writable: true);
        key?.DeleteValue(name, throwOnMissingValue: false);
    }

    public void DeleteKeyTree(RegistryHive hive, string path)
    {
        using var root = OpenRoot(hive);
        root.DeleteSubKeyTree(RequirePath(path), throwOnMissingSubKey: false);
    }

    public bool DeleteKeyIfEmpty(RegistryHive hive, string path)
    {
        RequirePath(path);
        var separator = path.LastIndexOf('\\');
        var parentPath = separator < 0 ? string.Empty : path[..separator];
        var leaf = separator < 0 ? path : path[(separator + 1)..];

        using var root = OpenRoot(hive);

        using (var key = root.OpenSubKey(path, writable: false))
        {
            if (key is null || key.SubKeyCount > 0 || key.ValueCount > 0)
            {
                return false;
            }
        }

        if (parentPath.Length == 0)
        {
            root.DeleteSubKey(leaf, throwOnMissingSubKey: false);
            return true;
        }

        using var parent = root.OpenSubKey(parentPath, writable: true);
        if (parent is null)
        {
            return false;
        }

        parent.DeleteSubKey(leaf, throwOnMissingSubKey: false);
        return true;
    }

    private static RegistryKey OpenRoot(RegistryHive hive) => RegistryKey.OpenBaseKey(
        hive == RegistryHive.Hkcu ? Microsoft.Win32.RegistryHive.CurrentUser : Microsoft.Win32.RegistryHive.LocalMachine,
        RegistryView.Registry64);

    private static string RequirePath(string path) =>
        string.IsNullOrWhiteSpace(path) ? throw new ArgumentException(CoreText.Get("Registry.EmptyPath"), nameof(path)) : path;
}
