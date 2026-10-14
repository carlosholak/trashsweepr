using System.Text.Json;
using System.Text.RegularExpressions;
using TrashSweepr.Limpeza.Core.Models;

namespace TrashSweepr.Limpeza.Core.Catalog;

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
            return ["Tweak vazio."];
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
            errors.Add("Id inválido: use minúsculas, números e hífens separados por pontos (ex.: privacy.advertising-id).");
        }

        if (!tweak.Name.HasDefault)
        {
            errors.Add($"'name' precisa ter texto em {LocalizedText.DefaultCulture}.");
        }

        if (!tweak.Description.HasDefault)
        {
            errors.Add($"'description' precisa ter texto em {LocalizedText.DefaultCulture}.");
        }

        if (!Enum.IsDefined(tweak.Category))
        {
            errors.Add("Categoria inválida.");
        }

        if (!Enum.IsDefined(tweak.Risk))
        {
            errors.Add("Nível de risco inválido.");
        }

        if (tweak.Risk >= RiskLevel.Moderate && tweak.RiskNote is not { HasDefault: true })
        {
            errors.Add("Tweaks Moderados/Avançados exigem 'riskNote' explicando o risco.");
        }

        if (tweak.MinBuild is < Windows11MinBuild)
        {
            errors.Add($"'minBuild' não pode ser menor que {Windows11MinBuild} (Windows 11).");
        }

        if (tweak.MinBuild is { } min && tweak.MaxBuild is { } max && max < min)
        {
            errors.Add("'maxBuild' não pode ser menor que 'minBuild'.");
        }

        if (tweak.Apply.Count == 0)
        {
            errors.Add("'apply' precisa ter ao menos uma ação.");
        }

        if (tweak.Revert.Count == 0)
        {
            errors.Add("'revert' precisa ter ao menos uma ação: todo tweak deve ser reversível.");
        }

        ValidateActions(tweak.Apply, "apply", errors);
        ValidateActions(tweak.Revert, "revert", errors);
        ValidateDeleteKeyTargets(tweak, errors);

        return errors;
    }

    public static IReadOnlyList<string> ValidateProfile(Profile profile)
    {
        if (profile is null)
        {
            return ["Perfil vazio."];
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
            errors.Add("Id de perfil inválido: use minúsculas, números e hífens.");
        }

        if (!profile.Name.HasDefault)
        {
            errors.Add($"'name' precisa ter texto em {LocalizedText.DefaultCulture}.");
        }

        if (!profile.Description.HasDefault)
        {
            errors.Add($"'description' precisa ter texto em {LocalizedText.DefaultCulture}.");
        }

        if (!Enum.IsDefined(profile.MaxRisk) || profile.MaxRisk >= RiskLevel.Advanced)
        {
            errors.Add("Perfis pré-definidos não podem incluir tweaks Avançados ('maxRisk' deve ser safe ou moderate).");
        }

        return errors;
    }

    /// <summary>
    /// Valida uma única ação. O engine chama isto antes de executar qualquer ação, mesmo que ela
    /// tenha vindo de um catálogo já validado (defesa em profundidade).
    /// </summary>
    public static IReadOnlyList<string> ValidateAction(TweakAction action, bool allowDeleteKey)
    {
        var errors = new List<string>();
        ValidateOne(action, "ação", allowDeleteKey, errors);
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
        [.. fields.Where(f => f.Value is null).Select(f => $"'{f.Field}' é obrigatório e não pode ser null.")];

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
                errors.Add($"revert[{i}]: 'deleteKey' só pode apagar uma chave criada pelo 'apply' do mesmo tweak (mesmo hive e caminho).");
            }
        }
    }

    private static void ValidateOne(TweakAction? action, string where, bool allowDeleteKey, List<string> errors)
    {
        switch (action)
        {
            case null:
                errors.Add($"{where}: ação vazia (null).");
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
            default:
                errors.Add($"{where}: tipo de ação não suportado.");
                break;
        }
    }

    private static void ValidateRegistry(RegistryAction action, string where, bool allowDeleteKey, List<string> errors)
    {
        var path = action.Path;

        if (path is null)
        {
            errors.Add($"{where}: 'path' é obrigatório.");
            return;
        }

        if (!Enum.IsDefined(action.Hive) || !Enum.IsDefined(action.Operation) || !Enum.IsDefined(action.Kind))
        {
            errors.Add($"{where}: hive, operação ou tipo de valor inválido.");
            return;
        }

        if (!RegistryPathRegex().IsMatch(path)
            || path.Split('\\').Any(segment => segment.Trim() is "." or ".." || segment.Trim().Length == 0))
        {
            errors.Add($"{where}: caminho de registro inválido '{path}'.");
            return;
        }

        if (CriticalComponents.IsProtectedRegistryPath(path))
        {
            errors.Add($"{where}: o caminho '{path}' pertence a um componente crítico protegido.");
            return;
        }

        // Exclusões não gravam nada: 'value' ou 'kind' nelas indica um JSON trocado (ex.: 'set' sem 'operation').
        if (action.Operation != RegistryOperation.Set
            && (action.Value.ValueKind != JsonValueKind.Undefined || action.HasExplicitKind))
        {
            errors.Add($"{where}: '{(action.Operation == RegistryOperation.DeleteKey ? "deleteKey" : "deleteValue")}' não aceita 'value' nem 'kind'.");
        }

        if (action.Operation == RegistryOperation.DeleteKey)
        {
            if (!allowDeleteKey)
            {
                errors.Add($"{where}: 'deleteKey' só é permitido em 'revert' (não há como fazer backup de uma chave apagada).");
            }

            if (action.Hive != RegistryHive.Hkcu || !DeletableKeyRegex().IsMatch(path))
            {
                errors.Add($"{where}: 'deleteKey' só é permitido em HKCU\\Software\\Classes\\CLSID\\{{GUID}}.");
            }

            if (!string.IsNullOrEmpty(action.Name))
            {
                errors.Add($"{where}: 'deleteKey' não aceita 'name'.");
            }

            return;
        }

        if (action.Name is null)
        {
            errors.Add($"{where}: 'name' é obrigatório (use \"\" para o valor padrão da chave).");
            return;
        }

        if (action.Name.Length > MaxNameLength || action.Name.Any(c => char.IsControl(c) || c == '\\'))
        {
            errors.Add($"{where}: nome de valor inválido.");
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
            errors.Add($"{where}: valor de texto inválido (muito longo ou com caracteres de controle).");
        }
        else if (value is string { Length: > 0 } && CriticalComponents.IsCommandRegistryPath(path))
        {
            // Texto nestas chaves vira um executável ou DLL que o Windows carrega.
            errors.Add($"{where}: '{path}' registra um comando, servidor COM ou DLL; só é aceito texto vazio (componente protegido).");
        }
    }

    private static void ValidateService(ServiceAction action, string where, List<string> errors)
    {
        if (!Enum.IsDefined(action.StartType))
        {
            errors.Add($"{where}: tipo de inicialização inválido.");
        }

        if (action.Name is null)
        {
            errors.Add($"{where}: 'name' do serviço é obrigatório.");
        }
        else if (!ServiceNameRegex().IsMatch(action.Name))
        {
            errors.Add($"{where}: nome de serviço inválido '{action.Name}'.");
        }
        else if (CriticalComponents.IsProtectedService(action.Name))
        {
            errors.Add($"{where}: o serviço '{action.Name}' é crítico e protegido.");
        }
    }

    private static void ValidateTask(ScheduledTaskAction action, string where, List<string> errors)
    {
        if (action.TaskPath is null || action.TaskName is null)
        {
            errors.Add($"{where}: 'taskPath' e 'taskName' são obrigatórios.");
            return;
        }

        var valid = true;

        if (!TaskPathRegex().IsMatch(action.TaskPath)
            || action.TaskPath.Split('\\').Any(segment => segment.Trim() is "." or ".."))
        {
            errors.Add($"{where}: 'taskPath' inválido (ex.: \\Microsoft\\Windows\\Pasta\\).");
            valid = false;
        }

        if (!TaskNameRegex().IsMatch(action.TaskName))
        {
            errors.Add($"{where}: 'taskName' inválido.");
            valid = false;
        }

        if (valid && CriticalComponents.IsProtectedTask(action.TaskPath, action.TaskName))
        {
            errors.Add($"{where}: a tarefa '{action.TaskPath}{action.TaskName}' é crítica e protegida.");
        }
    }

    private static void ValidateAppx(AppxAction action, string where, List<string> errors)
    {
        if (!Enum.IsDefined(action.Operation))
        {
            errors.Add($"{where}: operação inválida.");
        }

        if (action.PackageName is null)
        {
            errors.Add($"{where}: 'packageName' é obrigatório.");
        }
        else if (!PackageNameRegex().IsMatch(action.PackageName))
        {
            errors.Add($"{where}: nome de pacote inválido '{action.PackageName}' (curingas não são permitidos).");
        }
        else if (CriticalComponents.IsProtectedPackage(action.PackageName))
        {
            errors.Add($"{where}: o pacote '{action.PackageName}' é um componente crítico protegido.");
        }
    }
}
