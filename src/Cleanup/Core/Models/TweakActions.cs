using System.Text.Json;
using System.Text.Json.Serialization;

namespace TrashSweepr.Cleanup.Core.Models;

/// <summary>
/// Ação declarativa de um tweak. O JSON nunca carrega scripts: cada tipo de ação tem campos
/// tipados e o código monta o comando a partir deles, após validação.
/// </summary>
[JsonConverter(typeof(TweakActionConverter))]
public abstract record TweakAction;

public sealed record RegistryAction : TweakAction
{
    public RegistryOperation Operation { get; init; } = RegistryOperation.Set;

    public required RegistryHive Hive { get; init; }

    /// <summary>Caminho da chave, sem o hive (ex.: Software\Microsoft\Windows\...).</summary>
    public required string Path { get; init; }

    /// <summary>Nome do valor. Uma string vazia representa o valor padrão da chave.</summary>
    public string? Name { get; init; }

    private readonly RegistryValueType _kind = RegistryValueType.Dword;
    private readonly bool _kindSpecified;

    public RegistryValueType Kind
    {
        get => _kind;
        init
        {
            _kind = value;
            _kindSpecified = true;
        }
    }

    /// <summary>'kind' foi informado explicitamente (o validador o rejeita em deleteValue/deleteKey).</summary>
    [JsonIgnore]
    public bool HasExplicitKind => _kindSpecified;

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public JsonElement Value { get; init; }

    private string RawValue => Value.ValueKind == JsonValueKind.Undefined ? string.Empty : Value.GetRawText();

    // JsonElement compara por referência; sem isto, duas ações idênticas nunca seriam iguais.
    public bool Equals(RegistryAction? other) =>
        other is not null
        && Operation == other.Operation
        && Hive == other.Hive
        && Kind == other.Kind
        && string.Equals(Path, other.Path, StringComparison.Ordinal)
        && string.Equals(Name, other.Name, StringComparison.Ordinal)
        && string.Equals(RawValue, other.RawValue, StringComparison.Ordinal);

    public override int GetHashCode() => HashCode.Combine(Operation, Hive, Kind, Path, Name, RawValue);

    /// <summary>Converte o valor bruto do JSON para o tipo .NET correspondente ao <see cref="Kind"/>.</summary>
    public bool TryGetTypedValue(out object? value, out string? error) =>
        RegistryValueConverter.TryConvert(Kind, Value, out value, out error);
}

/// <summary>Converte valores JSON em valores .NET de registro (e é reutilizado ao restaurar backups).</summary>
public static class RegistryValueConverter
{
    public static bool TryConvert(RegistryValueType kind, JsonElement element, out object? value, out string? error)
    {
        value = null;
        error = null;

        if (element.ValueKind is JsonValueKind.Undefined or JsonValueKind.Null)
        {
            error = CoreText.Get("Catalog.ValueRequired");
            return false;
        }

        switch (kind)
        {
            case RegistryValueType.Dword:
                if (element.ValueKind == JsonValueKind.Number && element.TryGetInt64(out var dword)
                    && dword is >= int.MinValue and <= uint.MaxValue)
                {
                    value = unchecked((int)dword);
                    return true;
                }

                error = CoreText.Get("Catalog.DwordRange");
                return false;

            case RegistryValueType.Qword:
                if (element.ValueKind == JsonValueKind.Number && element.TryGetInt64(out var qword))
                {
                    value = qword;
                    return true;
                }

                error = CoreText.Get("Catalog.QwordRange");
                return false;

            default:
                if (element.ValueKind == JsonValueKind.String)
                {
                    value = element.GetString();
                    return true;
                }

                error = CoreText.Get("Catalog.StringValue");
                return false;
        }
    }
}

public sealed record ServiceAction : TweakAction
{
    public required string Name { get; init; }

    public required ServiceStartType StartType { get; init; }

    /// <summary>Se verdadeiro, também para o serviço quando ele estiver em execução.</summary>
    public bool Stop { get; init; }
}

public sealed record ScheduledTaskAction : TweakAction
{
    /// <summary>Pasta da tarefa, com barras iniciais e finais (ex.: \Microsoft\Windows\Application Experience\).</summary>
    public required string TaskPath { get; init; }

    public required string TaskName { get; init; }

    /// <summary>Estado desejado da tarefa após a ação.</summary>
    public required bool Enabled { get; init; }
}

public sealed record AppxAction : TweakAction
{
    public required AppxOperation Operation { get; init; }

    /// <summary>Nome exato do pacote (sem curingas), ex.: Clipchamp.Clipchamp.</summary>
    public required string PackageName { get; init; }
}

/// <summary>
/// Desinstala ou reinstala um programa da lista fechada <see cref="ManagedProgram"/>. O JSON não traz caminho nem
/// comando: cada par (programa, operação) corresponde a um script fixo em PowerShellCalls.
/// </summary>
public sealed record ProgramAction : TweakAction
{
    [JsonConverter(typeof(ManagedProgramJsonConverter))]
    public required ManagedProgram Program { get; init; }

    public required ProgramOperation Operation { get; init; }
}

/// <summary>Nomes dos programas gerenciados no JSON (catálogo e backups) e na tela.</summary>
public static class ManagedPrograms
{
    /// <summary>Nome exato no JSON. Só estes dois valores são aceitos.</summary>
    public static string JsonName(ManagedProgram program) => program switch
    {
        ManagedProgram.Edge => "edge",
        ManagedProgram.OneDrive => "onedrive",
        _ => throw new ArgumentOutOfRangeException(nameof(program)),
    };

    /// <summary>Nome do produto (igual em todas as línguas).</summary>
    public static string DisplayName(ManagedProgram program) => program switch
    {
        ManagedProgram.Edge => "Microsoft Edge",
        ManagedProgram.OneDrive => "OneDrive",
        _ => throw new ArgumentOutOfRangeException(nameof(program)),
    };

    internal static bool TryParse(string? text, out ManagedProgram program)
    {
        // Comparação exata (ordinal): "Edge", "EDGE" ou "1" não passam. A lista é fechada de propósito.
        switch (text)
        {
            case "edge":
                program = ManagedProgram.Edge;
                return true;
            case "onedrive":
                program = ManagedProgram.OneDrive;
                return true;
            default:
                program = default;
                return false;
        }
    }
}

/// <summary>
/// Lê e grava <see cref="ManagedProgram"/> só como "edge" ou "onedrive". Fica no atributo da propriedade porque o
/// conversor genérico de enums das opções transformaria OneDrive em "oneDrive" e aceitaria variações de caixa.
/// </summary>
public sealed class ManagedProgramJsonConverter : JsonConverter<ManagedProgram>
{
    public override bool HandleNull => true;

    public override ManagedProgram Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        var text = reader.TokenType == JsonTokenType.String ? reader.GetString() : null;
        if (!ManagedPrograms.TryParse(text, out var program))
        {
            var shown = reader.TokenType == JsonTokenType.String ? text : reader.TokenType.ToString();
            throw new JsonException(CoreText.Format("Catalog.UnknownProgram", shown));
        }

        return program;
    }

    public override void Write(Utf8JsonWriter writer, ManagedProgram value, JsonSerializerOptions options)
    {
        ArgumentNullException.ThrowIfNull(writer);
        writer.WriteStringValue(ManagedPrograms.JsonName(value));
    }
}

internal sealed class TweakActionConverter : JsonConverter<TweakAction>
{
    public override TweakAction Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        using var document = JsonDocument.ParseValue(ref reader);
        var root = document.RootElement;

        if (root.ValueKind != JsonValueKind.Object
            || !root.TryGetProperty("type", out var typeElement)
            || typeElement.ValueKind != JsonValueKind.String)
        {
            throw new JsonException(CoreText.Get("Catalog.ActionTypeRequired"));
        }

        var type = typeElement.GetString();
        var raw = WithoutTypeProperty(root);

        // Os tipos derivados são desserializados diretamente: este conversor só atua sobre a base.
        TweakAction? action = type switch
        {
            "registry" => JsonSerializer.Deserialize<RegistryAction>(raw, options),
            "service" => JsonSerializer.Deserialize<ServiceAction>(raw, options),
            "scheduledTask" => JsonSerializer.Deserialize<ScheduledTaskAction>(raw, options),
            "appx" => JsonSerializer.Deserialize<AppxAction>(raw, options),
            "program" => JsonSerializer.Deserialize<ProgramAction>(raw, options),
            _ => throw new JsonException(CoreText.Format("Catalog.UnknownActionType", type)),
        };

        return action ?? throw new JsonException(CoreText.Get("Catalog.InvalidAction"));
    }

    // Os tipos derivados não declaram 'type'; retirá-lo permite rejeitar campos desconhecidos (typos).
    private static string WithoutTypeProperty(JsonElement element)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartObject();
            foreach (var property in element.EnumerateObject())
            {
                if (!property.NameEquals("type"))
                {
                    property.WriteTo(writer);
                }
            }

            writer.WriteEndObject();
        }

        return System.Text.Encoding.UTF8.GetString(stream.ToArray());
    }

    public override void Write(Utf8JsonWriter writer, TweakAction value, JsonSerializerOptions options)
    {
        var (type, json) = value switch
        {
            RegistryAction a => ("registry", JsonSerializer.SerializeToElement(a, options)),
            ServiceAction a => ("service", JsonSerializer.SerializeToElement(a, options)),
            ScheduledTaskAction a => ("scheduledTask", JsonSerializer.SerializeToElement(a, options)),
            AppxAction a => ("appx", JsonSerializer.SerializeToElement(a, options)),
            ProgramAction a => ("program", JsonSerializer.SerializeToElement(a, options)),
            _ => throw new JsonException(CoreText.Format("Catalog.UnsupportedActionClass", value.GetType().Name)),
        };

        writer.WriteStartObject();
        writer.WriteString("type", type);
        foreach (var property in json.EnumerateObject())
        {
            property.WriteTo(writer);
        }

        writer.WriteEndObject();
    }
}
