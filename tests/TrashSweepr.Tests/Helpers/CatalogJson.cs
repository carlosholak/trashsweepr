using System.Text.Json;
using TrashSweepr.Cleanup.Core.Catalog;

namespace TrashSweepr.Tests.Helpers;

/// <summary>Monta JSON de catálogo para os testes. Todos os valores inseridos passam por escape JSON.</summary>
internal static class CatalogJson
{
    private static string Q(string? value) => JsonSerializer.Serialize(value);

    public static string Registry(
        string path = @"Software\Cleanup\Test",
        string hive = "hkcu",
        string? name = "Value",
        string valueJson = "1",
        string kind = "dword",
        string? operation = null) =>
        $$"""
        { "type": "registry", {{(operation is null ? "" : $"\"operation\": {Q(operation)},")}} "hive": {{Q(hive)}}, "path": {{Q(path)}}, {{(name is null ? "" : $"\"name\": {Q(name)},")}} "kind": {{Q(kind)}}, "value": {{valueJson}} }
        """;

    public static string Service(string name = "CleanupTestSvc", string startType = "disabled", bool stop = false) =>
        $$"""{ "type": "service", "name": {{Q(name)}}, "startType": {{Q(startType)}}, "stop": {{(stop ? "true" : "false")}} }""";

    public static string SchedTask(string path = @"\Cleanup\Test\", string name = "TestTask", bool enabled = false) =>
        $$"""{ "type": "scheduledTask", "taskPath": {{Q(path)}}, "taskName": {{Q(name)}}, "enabled": {{(enabled ? "true" : "false")}} }""";

    public static string Appx(string package = "Contoso.SampleApp", string operation = "remove") =>
        $$"""{ "type": "appx", "operation": {{Q(operation)}}, "packageName": {{Q(package)}} }""";

    public static string Tweak(
        string id = "test.sample",
        string risk = "safe",
        string? riskNote = null,
        string? apply = null,
        string? revert = null,
        string extra = "",
        string category = "privacy",
        string name = "Teste",
        string description = "Descrição do teste") =>
        $$"""
        {
          "id": {{Q(id)}},
          "name": {{Q(name)}},
          "description": {{Q(description)}},
          "category": {{Q(category)}},
          "risk": {{Q(risk)}},
          {{(riskNote is null ? "" : $"\"riskNote\": {Q(riskNote)},")}}
          "apply": [ {{apply ?? Registry()}} ],
          "revert": [ {{revert ?? Registry(valueJson: "0")}} ]
          {{extra}}
        }
        """;

    public static string TweakFile(params string[] tweaks) =>
        $$"""{ "schemaVersion": 1, "tweaks": [ {{string.Join(",", tweaks)}} ] }""";

    public static string ProfileFile(params string[] profiles) =>
        $$"""{ "schemaVersion": 1, "profiles": [ {{string.Join(",", profiles)}} ] }""";

    public static string Profile(string id = "light", string maxRisk = "safe", string name = "Perfil") =>
        $$"""{ "id": {{Q(id)}}, "name": {{Q(name)}}, "description": "Descrição", "maxRisk": {{Q(maxRisk)}} }""";

    public static CatalogLoadResult Load(params string[] tweaks) =>
        TweakCatalogLoader.LoadFromSources([("test.json", TweakFile(tweaks))]);
}
