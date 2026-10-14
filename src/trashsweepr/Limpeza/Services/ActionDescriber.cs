using System.Globalization;
using TrashSweepr.Limpeza.Core.Models;

namespace TrashSweepr.Limpeza.Services;

/// <summary>Descreve em português, em uma linha, o que uma ação do catálogo faz.</summary>
public static class ActionDescriber
{
    public static string Describe(TweakAction action) => action switch
    {
        RegistryAction r => DescribeRegistry(r),
        ServiceAction s => Loc.Format(
            s.Stop ? "Action.ServiceStartTypeAndStop" : "Action.ServiceStartType",
            s.Name,
            Loc.Get($"StartType.{s.StartType}")),
        ScheduledTaskAction t => Loc.Format(t.Enabled ? "Action.TaskEnable" : "Action.TaskDisable", t.TaskPath + t.TaskName),
        AppxAction a => Loc.Format(a.Operation == AppxOperation.Remove ? "Action.AppxRemove" : "Action.AppxReinstall", a.PackageName),
        _ => action.GetType().Name,
    };

    private static string DescribeRegistry(RegistryAction action)
    {
        var hive = action.Hive == RegistryHive.Hkcu ? "HKCU" : "HKLM";
        var target = $"{hive}\\{action.Path}";

        switch (action.Operation)
        {
            case RegistryOperation.DeleteKey:
                return Loc.Format("Action.RegistryDeleteKey", target);

            case RegistryOperation.DeleteValue:
                return Loc.Format("Action.RegistryDeleteValue", $"{target}\\{ValueName(action.Name)}");

            default:
                action.TryGetTypedValue(out var value, out _);
                var shown = value is string text ? $"\"{text}\"" : Convert.ToString(value, CultureInfo.InvariantCulture) ?? string.Empty;
                return Loc.Format("Action.RegistrySet", $"{target}\\{ValueName(action.Name)}", shown, Loc.Get($"RegType.{action.Kind}"));
        }
    }

    private static string ValueName(string? name) => string.IsNullOrEmpty(name) ? "(Default)" : name;
}
