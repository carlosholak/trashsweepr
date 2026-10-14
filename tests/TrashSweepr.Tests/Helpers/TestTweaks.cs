using TrashSweepr.Cleanup.Core.Models;
using static TrashSweepr.Tests.Helpers.CatalogJson;

namespace TrashSweepr.Tests.Helpers;

internal static class TestTweaks
{
    public const string Key = @"Software\Cleanup\Test";

    public static CancellationToken Ct => CancellationToken.None;

    /// <summary>Cria um tweak passando pelo loader (portanto pelo validador), como no uso real.</summary>
    public static Tweak Make(
        string id,
        string apply,
        string revert,
        string risk = "safe",
        string? riskNote = null,
        string extra = "")
    {
        var result = Load(Tweak(id: id, risk: risk, riskNote: riskNote, apply: apply, revert: revert, extra: extra));
        Assert.False(result.HasErrors, string.Join(Environment.NewLine, result.Issues));
        return Assert.Single(result.Catalog.Tweaks);
    }

    /// <summary>Tweak simples de registro: apply escreve 0, revert escreve 1.</summary>
    public static Tweak RegistryTweak(string id = "test.reg", string path = Key, string name = "V", string? risk = null, string? riskNote = null) =>
        Make(
            id,
            Registry(path: path, name: name, valueJson: "0"),
            Registry(path: path, name: name, valueJson: "1"),
            risk ?? "safe",
            riskNote);

    public static Tweak ServiceTweak(string id = "test.svc", string service = "FooSvc") =>
        Make(id, Service(service, "disabled", stop: true), Service(service, "automatic"));

    public static Tweak AppTweak(string id = "test.app", string package = "Contoso.Sample") =>
        Make(id, Appx(package, "remove"), Appx(package, "reinstall"));

    /// <summary>Tweak montado direto no código, sem passar pelo loader, para testar a defesa em profundidade.</summary>
    public static Tweak Unvalidated(params TweakAction[] apply) => new()
    {
        Id = "test.unvalidated",
        Name = LocalizedText.FromDefault("x"),
        Description = LocalizedText.FromDefault("x"),
        Category = TweakCategory.Services,
        Risk = RiskLevel.Safe,
        Apply = apply,
        Revert = apply,
    };
}

/// <summary>IProgress síncrono (Progress&lt;T&gt; despacha de forma assíncrona e atrapalha os testes).</summary>
internal sealed class SyncProgress<T> : IProgress<T>
{
    public List<T> Reports { get; } = [];

    public void Report(T value) => Reports.Add(value);
}
