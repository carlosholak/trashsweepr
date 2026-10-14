using System.Text.Json;
using System.Text.RegularExpressions;
using TrashSweepr.Cleanup.Core.Models;

namespace TrashSweepr.Cleanup.Core.Catalog;

/// <summary>
/// Valida tweaks e perfis antes de qualquer execução. Os campos que viram argumentos de
/// PowerShell ou chaves de registro passam por listas de caracteres permitidos (nunca por escape
/// "criativo"), e a deny-list de <see cref="CriticalComponents"/> é aplicada a tudo.
/// </summary>
public static partial class TweakValidator
{
    public const int Windows11MinBuild = 22000;

    private const int MaxNameLength = 255;
    private const int MaxStringValueLength = 2048;

    [GeneratedRegex(@"^[a-z0-9]+(-[a-z0-9]+)*(\.[a-z0-9]+(-[a-z0-9]+)*)+$")]
    private static partial Regex TweakIdRegex();

    [GeneratedRegex(@"^[a-z0-9]+(-[a-z0-9]+)*$")]
    private static partial Regex ProfileIdRegex();

    [GeneratedRegex(@"^[A-Za-z0-9_ .\-{}()]+(\\[A-Za-z0-9_ .\-{}()]+)*$")]
    private static partial Regex RegistryPathRegex();

    [GeneratedRegex(@"^Software\\Classes\\CLSID\\\{[0-9A-Fa-f]{8}-[0-9A-Fa-f]{4}-[0-9A-Fa-f]{4}-[0-9A-Fa-f]{4}-[0-9A-Fa-f]{12}\}$")]
    private static partial Regex DeletableKeyRegex();

    // Não pode começar com '-': o nome é passado ao sc.exe, que o interpretaria como opção.
    [GeneratedRegex(@"^[A-Za-z0-9_][A-Za-z0-9_.\-]{0,255}$")]
    private static partial Regex ServiceNameRegex();

    [GeneratedRegex(@"^\\([A-Za-z0-9 ._\-]+\\)*$")]
    private static partial Regex TaskPathRegex();

    [GeneratedRegex(@"^[A-Za-z0-9 ._\-]{1,255}$")]
    private static partial Regex TaskNameRegex();

    [GeneratedRegex(@"^[A-Za-z0-9][A-Za-z0-9._\-]{2,127}$")]
    private static partial Regex PackageNameRegex();

    public static IReadOnlyList<string> ValidateTweak(Tweak tweak)
    {
        if (tweak is null)
        {
            return [CoreText.Get("Catalog.EmptyTweak")];
        }

        // Um campo "null" no JSON passa pelo desserializador mesmo sendo obrigatório: barra aqui,
        // antes que qualquer regra abaixo tente usá-lo.
        var missing = MissingFields(
            ("id", tweak.Id),
            ("name", tweak.Name),
            ("description", tweak.Description),
            ("apply", tweak.Apply),
            ("revert", tweak.Revert));
        if (missing.Count > 0)
        {
            return missing;
        }

        var errors = new List<string>();

        if (!TweakIdRegex().IsMatch(tweak.Id))
        {
            errors.Add(CoreText.Get("Validator.InvalidTweakId"));
        }

        RequireText(tweak.Name, "name", errors);
        RequireText(tweak.Description, "description", errors);

        if (!Enum.IsDefined(tweak.Category))
        {
            errors.Add(CoreText.Get("Validator.InvalidCategory"));
        }

        if (!Enum.IsDefined(tweak.Risk))
        {
            errors.Add(CoreText.Get("Validator.InvalidRisk"));
        }

        if (tweak.Risk >= RiskLevel.Moderate && tweak.RiskNote?.HasSupportedText != true)
        {
            errors.Add(CoreText.Get("Validator.RiskNoteRequired"));
        }

        if (tweak.MinBuild is < Windows11MinBuild)
        {
            errors.Add(CoreText.Format("Validator.MinBuild", Windows11MinBuild));
        }

        if (tweak.MinBuild is { } min && tweak.MaxBuild is { } max && max < min)
        {
            errors.Add(CoreText.Get("Validator.MaxBuild"));
        }

        if (tweak.Apply.Count == 0)
        {
            errors.Add(CoreText.Get("Validator.ApplyEmpty"));
        }

        if (tweak.Revert.Count == 0)
        {
            errors.Add(CoreText.Get("Validator.RevertEmpty"));
        }

        ValidateActions(tweak.Apply, "apply", errors);
        ValidateActions(tweak.Revert, "revert", errors);
        ValidateDeleteKeyTargets(tweak, errors);
        ValidateProgramPairs(tweak, errors);

        return errors;
    }

    public static IReadOnlyList<string> ValidateProfile(Profile profile)
    {
        if (profile is null)
        {
            return [CoreText.Get("Catalog.EmptyProfile")];
        }

        var missing = MissingFields(
            ("id", profile.Id),
            ("name", profile.Name),
            ("description", profile.Description));
        if (missing.Count > 0)
        {
            return missing;
        }

        var errors = new List<string>();

        if (!ProfileIdRegex().IsMatch(profile.Id))
        {
            errors.Add(CoreText.Get("Validator.InvalidProfileId"));
        }

        RequireText(profile.Name, "name", errors);
        RequireText(profile.Description, "description", errors);

        if (!Enum.IsDefined(profile.MaxRisk) || profile.MaxRisk >= RiskLevel.Advanced)
        {
            errors.Add(CoreText.Get("Validator.ProfileMaxRisk"));
        }

        return errors;
    }

    /// <summary>
    /// Valida uma única ação. O engine chama isto antes de executar qualquer ação, mesmo que ela
    /// tenha vindo de um catálogo já validado (defesa em profundidade).
    /// <paramref name="allowDeleteKey"/> é verdadeiro exatamente quando a ação vem de 'revert' ou de um backup: é
    /// onde 'deleteKey' e o 'reinstall' de programas são aceitos (e onde o 'uninstall' de programas não é).
    /// </summary>
    public static IReadOnlyList<string> ValidateAction(TweakAction action, bool allowDeleteKey)
    {
        var errors = new List<string>();
        ValidateOne(action, CoreText.Get("Validator.ActionLabel"), allowDeleteKey, errors);
        return errors;
    }

    /// <summary>
    /// <paramref name="path"/> é a própria chave <paramref name="root"/> ou fica abaixo dela. Compara por segmento:
    /// Software\AB não fica abaixo de Software\A.
    /// </summary>
    internal static bool IsSameOrUnderKey(string path, string root) =>
        path.Equals(root, StringComparison.OrdinalIgnoreCase)
        || path.StartsWith(root + "\\", StringComparison.OrdinalIgnoreCase);

    private static List<string> MissingFields(params (string Field, object? Value)[] fields) =>
        [.. fields.Where(f => f.Value is null).Select(f => CoreText.Format("Validator.FieldRequired", f.Field))];

    /// <summary>
    /// Textos do catálogo precisam existir em ao menos uma das línguas do app (en ou pt-BR; string simples conta
    /// como pt-BR). O catálogo distribuído em /tweaks exige as duas: isso é conferido pelos testes do repositório.
    /// </summary>
    private static void RequireText(LocalizedText text, string field, List<string> errors)
    {
        if (!text.HasSupportedText)
        {
            errors.Add(CoreText.Format("Validator.NeedsText", field));
        }
    }

    private static void ValidateActions(IReadOnlyList<TweakAction> actions, string section, List<string> errors)
    {
        for (var i = 0; i < actions.Count; i++)
        {
            // Apagar chaves não pode ser desfeito a partir de um backup, então só é aceito em 'revert'.
            ValidateOne(actions[i], $"{section}[{i}]", allowDeleteKey: section == "revert", errors);
        }
    }

    /// <summary>
    /// 'deleteKey' apaga a árvore inteira: só pode atingir uma chave que o 'apply' do mesmo tweak cria
    /// (mesmo hive; a chave apagada é a do valor gravado ou uma ancestral dela).
    /// </summary>
    private static void ValidateDeleteKeyTargets(Tweak tweak, List<string> errors)
    {
        var created = tweak.Apply
            .OfType<RegistryAction>()
            .Where(a => a.Operation == RegistryOperation.Set && a.Path is not null)
            .ToList();

        for (var i = 0; i < tweak.Revert.Count; i++)
        {
            if (tweak.Revert[i] is not RegistryAction { Operation: RegistryOperation.DeleteKey, Path: { } path } deleteKey)
            {
                continue;
            }

            if (!created.Any(a => a.Hive == deleteKey.Hive && IsSameOrUnderKey(a.Path, path)))
            {
                errors.Add(CoreText.Format("Validator.DeleteKeyNotCreated", i));
            }
        }
    }

    /// <summary>
    /// Todo 'uninstall' de programa no 'apply' precisa do 'reinstall' do mesmo programa no 'revert': sem isso, o
    /// "Desfazer" pelo catálogo não teria como devolver o programa.
    /// </summary>
    private static void ValidateProgramPairs(Tweak tweak, List<string> errors)
    {
        for (var i = 0; i < tweak.Apply.Count; i++)
        {
            if (tweak.Apply[i] is not ProgramAction { Operation: ProgramOperation.Uninstall } uninstall
                || !Enum.IsDefined(uninstall.Program))
            {
                continue;
            }

            if (!tweak.Revert.Any(r => r is ProgramAction { Operation: ProgramOperation.Reinstall } reinstall && reinstall.Program == uninstall.Program))
            {
                errors.Add(CoreText.Format("Validator.ProgramWithoutReinstall", i, ManagedPrograms.JsonName(uninstall.Program)));
            }
        }
    }

    private static void ValidateOne(TweakAction? action, string where, bool allowDeleteKey, List<string> errors)
    {
        switch (action)
        {
            case null:
                errors.Add(CoreText.Format("Validator.NullAction", where));
                break;
            case RegistryAction registry:
                ValidateRegistry(registry, where, allowDeleteKey, errors);
                break;
            case ServiceAction service:
                ValidateService(service, where, errors);
                break;
            case ScheduledTaskAction task:
                ValidateTask(task, where, errors);
                break;
            case AppxAction appx:
                ValidateAppx(appx, where, errors);
                break;
            case ProgramAction program:
                ValidateProgram(program, where, isRevert: allowDeleteKey, errors);
                break;
            default:
                errors.Add(CoreText.Format("Validator.UnsupportedActionType", where));
                break;
        }
    }

    private static void ValidateRegistry(RegistryAction action, string where, bool allowDeleteKey, List<string> errors)
    {
        var path = action.Path;

        if (path is null)
        {
            errors.Add(CoreText.Format("Validator.PathRequired", where));
            return;
        }

        if (!Enum.IsDefined(action.Hive) || !Enum.IsDefined(action.Operation) || !Enum.IsDefined(action.Kind))
        {
            errors.Add(CoreText.Format("Validator.InvalidRegistryEnums", where));
            return;
        }

        if (!RegistryPathRegex().IsMatch(path)
            || path.Split('\\').Any(segment => segment.Trim() is "." or ".." || segment.Trim().Length == 0))
        {
            errors.Add(CoreText.Format("Validator.InvalidRegistryPath", where, path));
            return;
        }

        if (CriticalComponents.IsProtectedRegistryPath(path))
        {
            errors.Add(CoreText.Format("Validator.ProtectedRegistryPath", where, path));
            return;
        }

        // Exclusões não gravam nada: 'value' ou 'kind' nelas indica um JSON trocado (ex.: 'set' sem 'operation').
        if (action.Operation != RegistryOperation.Set
            && (action.Value.ValueKind != JsonValueKind.Undefined || action.HasExplicitKind))
        {
            errors.Add(CoreText.Format("Validator.DeleteTakesNoValue", where, action.Operation == RegistryOperation.DeleteKey ? "deleteKey" : "deleteValue"));
        }

        if (action.Operation == RegistryOperation.DeleteKey)
        {
            if (!allowDeleteKey)
            {
                errors.Add(CoreText.Format("Validator.DeleteKeyOnlyInRevert", where));
            }

            if (action.Hive != RegistryHive.Hkcu || !DeletableKeyRegex().IsMatch(path))
            {
                errors.Add(CoreText.Format("Validator.DeleteKeyOnlyClsid", where));
            }

            if (!string.IsNullOrEmpty(action.Name))
            {
                errors.Add(CoreText.Format("Validator.DeleteKeyTakesNoName", where));
            }

            return;
        }

        if (action.Name is null)
        {
            errors.Add(CoreText.Format("Validator.ValueNameRequired", where));
            return;
        }

        if (action.Name.Length > MaxNameLength || action.Name.Any(c => char.IsControl(c) || c == '\\'))
        {
            errors.Add(CoreText.Format("Validator.InvalidValueName", where));
        }

        if (action.Operation != RegistryOperation.Set)
        {
            return;
        }

        if (!action.TryGetTypedValue(out var value, out var error))
        {
            errors.Add($"{where}: {error}");
        }
        else if (value is string text
                 && (text.Length > MaxStringValueLength || text.Any(c => char.IsControl(c))))
        {
            errors.Add(CoreText.Format("Validator.InvalidStringValue", where));
        }
        else if (value is string { Length: > 0 } && CriticalComponents.IsCommandRegistryPath(path))
        {
            // Texto nestas chaves vira um executável ou DLL que o Windows carrega.
            errors.Add(CoreText.Format("Validator.CommandPath", where, path));
        }
    }

    private static void ValidateService(ServiceAction action, string where, List<string> errors)
    {
        if (!Enum.IsDefined(action.StartType))
        {
            errors.Add(CoreText.Format("Validator.InvalidStartType", where));
        }

        if (action.Name is null)
        {
            errors.Add(CoreText.Format("Validator.ServiceNameRequired", where));
        }
        else if (!ServiceNameRegex().IsMatch(action.Name))
        {
            errors.Add(CoreText.Format("Validator.InvalidServiceName", where, action.Name));
        }
        else if (CriticalComponents.IsProtectedService(action.Name))
        {
            errors.Add(CoreText.Format("Validator.ProtectedService", where, action.Name));
        }
    }

    private static void ValidateTask(ScheduledTaskAction action, string where, List<string> errors)
    {
        if (action.TaskPath is null || action.TaskName is null)
        {
            errors.Add(CoreText.Format("Validator.TaskFieldsRequired", where));
            return;
        }

        var valid = true;

        if (!TaskPathRegex().IsMatch(action.TaskPath)
            || action.TaskPath.Split('\\').Any(segment => segment.Trim() is "." or ".."))
        {
            errors.Add(CoreText.Format("Validator.InvalidTaskPath", where));
            valid = false;
        }

        if (!TaskNameRegex().IsMatch(action.TaskName))
        {
            errors.Add(CoreText.Format("Validator.InvalidTaskName", where));
            valid = false;
        }

        if (valid && CriticalComponents.IsProtectedTask(action.TaskPath, action.TaskName))
        {
            errors.Add(CoreText.Format("Validator.ProtectedTask", where, action.TaskPath + action.TaskName));
        }
    }

    private static void ValidateAppx(AppxAction action, string where, List<string> errors)
    {
        if (!Enum.IsDefined(action.Operation))
        {
            errors.Add(CoreText.Format("Validator.InvalidAppxOperation", where));
        }

        if (action.PackageName is null)
        {
            errors.Add(CoreText.Format("Validator.PackageNameRequired", where));
        }
        else if (!PackageNameRegex().IsMatch(action.PackageName))
        {
            errors.Add(CoreText.Format("Validator.InvalidPackageName", where, action.PackageName));
        }
        else if (CriticalComponents.IsProtectedPackage(action.PackageName))
        {
            errors.Add(CoreText.Format("Validator.ProtectedPackage", where, action.PackageName));
        }
    }

    private static void ValidateProgram(ProgramAction action, string where, bool isRevert, List<string> errors)
    {
        // A lista fechada vive no código: nenhum JSON acrescenta programas.
        if (!Enum.IsDefined(action.Program) || !CriticalComponents.IsRemovableProgram(action.Program))
        {
            errors.Add(CoreText.Format("Validator.InvalidProgram", where));
            return;
        }

        if (!Enum.IsDefined(action.Operation))
        {
            errors.Add(CoreText.Format("Validator.InvalidProgramOperation", where));
            return;
        }

        // Desinstalar só no 'apply' (com backup antes); reinstalar só no 'revert' (ou ao restaurar um backup).
        if (action.Operation == ProgramOperation.Uninstall && isRevert)
        {
            errors.Add(CoreText.Format("Validator.ProgramUninstallOnlyInApply", where));
        }
        else if (action.Operation == ProgramOperation.Reinstall && !isRevert)
        {
            errors.Add(CoreText.Format("Validator.ProgramReinstallOnlyInRevert", where));
        }
    }
}
