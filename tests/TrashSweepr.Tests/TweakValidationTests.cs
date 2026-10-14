using TrashSweepr.Limpeza.Core.Catalog;
using TrashSweepr.Limpeza.Core.Models;
using TrashSweepr.Tests.Helpers;
using static TrashSweepr.Tests.Helpers.CatalogJson;

namespace TrashSweepr.Tests;

public class TweakValidationTests
{
    private static void AssertRejected(CatalogLoadResult result, string expectedMessagePart)
    {
        Assert.Empty(result.Catalog.Tweaks);
        Assert.True(result.HasErrors);
        Assert.Contains(result.Issues, i => i.Message.Contains(expectedMessagePart, StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Valid_tweak_is_accepted()
    {
        var result = Load(Tweak());

        Assert.False(result.HasErrors);
        var tweak = Assert.Single(result.Catalog.Tweaks);
        Assert.Equal("test.sample", tweak.Id);
        Assert.Equal(RiskLevel.Safe, tweak.Risk);
    }

    [Theory]
    [InlineData("Privacy.Test")]
    [InlineData("privacytest")]
    [InlineData("privacy..x")]
    [InlineData("privacy.test_x")]
    [InlineData("privacy.test;calc")]
    [InlineData("")]
    public void Invalid_ids_are_rejected(string id)
    {
        AssertRejected(Load(Tweak(id: id)), "Id inválido");
    }

    [Theory]
    [InlineData("moderate")]
    [InlineData("advanced")]
    public void Moderate_and_advanced_require_a_risk_note(string risk)
    {
        AssertRejected(Load(Tweak(risk: risk)), "riskNote");
    }

    [Theory]
    [InlineData("moderate")]
    [InlineData("advanced")]
    public void Moderate_and_advanced_with_risk_note_are_accepted(string risk)
    {
        var result = Load(Tweak(risk: risk, riskNote: "Explica o risco."));

        Assert.False(result.HasErrors);
        Assert.Single(result.Catalog.Tweaks);
    }

    [Fact]
    public void Empty_revert_list_is_rejected()
    {
        const string json = """
            { "id": "test.norevert", "name": "T", "description": "D", "category": "privacy", "risk": "safe",
              "apply": [ { "type": "service", "name": "LimpezaTestSvc", "startType": "disabled" } ],
              "revert": [] }
            """;

        AssertRejected(Load(json), "reversível");
    }

    [Fact]
    public void Build_below_windows_11_is_rejected()
    {
        AssertRejected(Load(Tweak(extra: ", \"minBuild\": 19045")), "minBuild");
    }

    [Fact]
    public void Max_build_below_min_build_is_rejected()
    {
        AssertRejected(Load(Tweak(extra: ", \"minBuild\": 26100, \"maxBuild\": 22631")), "maxBuild");
    }

    [Fact]
    public void Unknown_field_is_rejected_instead_of_ignored()
    {
        AssertRejected(Load(Tweak(extra: ", \"requiresReboto\": true")), "requiresReboto");
    }

    [Fact]
    public void Unknown_action_type_is_rejected()
    {
        AssertRejected(Load(Tweak(apply: """{ "type": "powershell", "script": "Remove-Item C:\\ -Recurse" }""")), "powershell");
    }

    [Fact]
    public void Action_without_type_is_rejected()
    {
        AssertRejected(Load(Tweak(apply: """{ "name": "x" }""")), "type");
    }

    [Fact]
    public void Invalid_enum_value_is_rejected()
    {
        AssertRejected(Load(Tweak(risk: "extreme")), "risk");
    }

    [Fact]
    public void Numeric_enum_value_is_rejected()
    {
        var json = Tweak().Replace("\"risk\": \"safe\"", "\"risk\": 0", StringComparison.Ordinal);

        Assert.Empty(Load(json).Catalog.Tweaks);
    }

    [Fact]
    public void One_bad_tweak_does_not_discard_the_others()
    {
        var result = Load(Tweak(id: "test.good"), Tweak(id: "Bad Id"));

        Assert.Equal(["test.good"], result.Catalog.Tweaks.Select(t => t.Id));
        Assert.Single(result.Issues.Where(i => i.Severity == IssueSeverity.Error).GroupBy(i => i.TweakId));
    }

    [Fact]
    public void Duplicate_ids_are_rejected_keeping_the_first()
    {
        var result = Load(Tweak(id: "test.dup"), Tweak(id: "test.dup"));

        Assert.Single(result.Catalog.Tweaks);
        Assert.Contains(result.Issues, i => i.Message.Contains("duplicado", StringComparison.OrdinalIgnoreCase));
    }

    [Theory]
    [InlineData(@"Software\Test'; Remove-Item C:\ -Recurse; '")]
    [InlineData(@"Software\Test$(calc)")]
    [InlineData("Software\\Test\"; calc; \"")]
    [InlineData(@"Software\..\..\SYSTEM")]
    [InlineData(@"Software\.\Test")]
    [InlineData(@"Software\\Test")]
    [InlineData(@"\Software\Test")]
    [InlineData(@"Software\Test\")]
    [InlineData("Software\\Test\nCalc")]
    [InlineData("")]
    public void Malicious_or_malformed_registry_paths_are_rejected(string path)
    {
        AssertRejected(Load(Tweak(apply: Registry(path: path))), "registro");
    }

    [Theory]
    [InlineData("hklm", @"SOFTWARE\Microsoft\Windows Defender\Features")]
    [InlineData("hklm", @"SOFTWARE\Policies\Microsoft\Windows Defender")]
    [InlineData("hklm", @"software\policies\microsoft\windows defender\Real-Time Protection")]
    [InlineData("hklm", @"SOFTWARE\Policies\Microsoft\Windows\WindowsUpdate\AU")]
    [InlineData("hklm", @"SOFTWARE\Microsoft\Windows\CurrentVersion\WindowsUpdate")]
    [InlineData("hklm", @"SOFTWARE\Policies\Microsoft\Edge")]
    [InlineData("hklm", @"SYSTEM\CurrentControlSet\Services\WinDefend")]
    [InlineData("hklm", @"SYSTEM\ControlSet001\Services\wuauserv\Parameters")]
    [InlineData("hklm", @"SYSTEM\CurrentControlSet\Services\Audiosrv")]
    [InlineData("hkcu", @"Software\Policies\Microsoft\Windows Defender")]
    public void Registry_paths_of_critical_components_are_rejected(string hive, string path)
    {
        AssertRejected(Load(Tweak(apply: Registry(hive: hive, path: path))), "protegido");
    }

    [Fact]
    public void Unprotected_service_registry_path_is_accepted()
    {
        var result = Load(Tweak(apply: Registry(hive: "hklm", path: @"SYSTEM\CurrentControlSet\Services\DiagTrack", name: "Start")));

        Assert.False(result.HasErrors);
    }

    [Fact]
    public void Set_without_name_is_rejected()
    {
        AssertRejected(Load(Tweak(apply: Registry(name: null))), "name");
    }

    [Fact]
    public void Set_with_empty_name_targets_the_default_value()
    {
        var result = Load(Tweak(apply: Registry(name: "", kind: "sz", valueJson: "\"\"")));

        Assert.False(result.HasErrors);
    }

    [Theory]
    [InlineData("Va\\lue")]
    [InlineData("Val\u0007ue")]
    public void Invalid_value_names_are_rejected(string name)
    {
        AssertRejected(Load(Tweak(apply: Registry(name: name))), "nome de valor");
    }

    [Theory]
    [InlineData("dword", "\"texto\"")]
    [InlineData("dword", "4294967296")]
    [InlineData("dword", "-2147483649")]
    [InlineData("dword", "1.5")]
    [InlineData("dword", "null")]
    [InlineData("qword", "\"1\"")]
    [InlineData("sz", "1")]
    [InlineData("sz", "\"linha1\\nlinha2\"")]
    public void Registry_values_must_match_their_kind(string kind, string valueJson)
    {
        AssertRejected(Load(Tweak(apply: Registry(kind: kind, valueJson: valueJson))), "valor");
    }

    [Theory]
    [InlineData("dword", "0")]
    [InlineData("dword", "4294967295")]
    [InlineData("dword", "-1")]
    [InlineData("qword", "9223372036854775807")]
    [InlineData("sz", "\"texto\"")]
    [InlineData("expandSz", "\"%SystemRoot%\\\\x\"")]
    public void Valid_registry_values_are_accepted(string kind, string valueJson)
    {
        Assert.False(Load(Tweak(apply: Registry(kind: kind, valueJson: valueJson))).HasErrors);
    }

    [Fact]
    public void Dword_above_int_max_is_stored_as_the_equivalent_signed_value()
    {
        var tweak = Assert.Single(Load(Tweak(apply: Registry(valueJson: "4294967295"))).Catalog.Tweaks);
        var action = Assert.IsType<RegistryAction>(Assert.Single(tweak.Apply));

        Assert.True(action.TryGetTypedValue(out var value, out _));
        Assert.Equal(-1, value);
    }

    [Fact]
    public void Delete_value_does_not_need_a_value()
    {
        var result = Load(Tweak(revert: """{ "type": "registry", "operation": "deleteValue", "hive": "hkcu", "path": "Software\\Limpeza\\Test", "name": "Value" }"""));

        Assert.False(result.HasErrors);
    }

    [Fact]
    public void Delete_key_is_accepted_only_for_an_hkcu_clsid()
    {
        const string guid = "{86ca1aa0-34aa-4e8b-a509-50c905bae2a2}";
        string DeleteKey(string hive, string path) =>
            $$"""{ "type": "registry", "operation": "deleteKey", "hive": "{{hive}}", "path": {{System.Text.Json.JsonSerializer.Serialize(path)}} }""";

        // O apply cria a chave do CLSID: só a regra de HKCU\...\CLSID\{GUID} decide abaixo.
        var apply = ClsidApply(guid);

        Assert.False(Load(Tweak(apply: apply, revert: DeleteKey("hkcu", $@"Software\Classes\CLSID\{guid}"))).HasErrors);

        AssertRejected(Load(Tweak(apply: apply, revert: DeleteKey("hklm", $@"Software\Classes\CLSID\{guid}"))), "deleteKey");
        AssertRejected(Load(Tweak(apply: apply, revert: DeleteKey("hkcu", @"Software\Microsoft"))), "deleteKey");
        AssertRejected(Load(Tweak(apply: apply, revert: DeleteKey("hkcu", $@"Software\Classes\CLSID\{guid}\InprocServer32"))), "deleteKey");
        AssertRejected(Load(Tweak(apply: apply, revert: DeleteKey("hkcu", @"Software\Classes\CLSID"))), "deleteKey");
    }

    private static string ClsidApply(string guid, string hive = "hkcu") =>
        Registry(hive: hive, path: $@"Software\Classes\CLSID\{guid}\InprocServer32", name: "", kind: "sz", valueJson: "\"\"");

    [Theory]
    [InlineData("wuauserv")]
    [InlineData("WinDefend")]
    [InlineData("Dnscache")]
    [InlineData("Dhcp")]
    [InlineData("Audiosrv")]
    [InlineData("AudioEndpointBuilder")]
    [InlineData("AUDIOSRV")]
    [InlineData("BITS")]
    [InlineData("mpssvc")]
    public void Critical_services_are_rejected(string name)
    {
        AssertRejected(Load(Tweak(apply: Service(name))), "crítico");
    }

    [Theory]
    [InlineData("Foo; calc")]
    [InlineData("Foo$(calc)")]
    [InlineData("Foo Bar")]
    [InlineData("Foo*")]
    [InlineData("")]
    public void Malformed_service_names_are_rejected(string name)
    {
        AssertRejected(Load(Tweak(apply: Service(name))), "serviço");
    }

    [Fact]
    public void Non_critical_service_is_accepted()
    {
        Assert.False(Load(Tweak(apply: Service("DiagTrack"))).HasErrors);
    }

    [Theory]
    [InlineData(@"\Microsoft\Windows\WindowsUpdate\", "Scheduled Start")]
    [InlineData(@"\Microsoft\Windows\UpdateOrchestrator\", "Schedule Scan")]
    [InlineData(@"\Microsoft\Windows\WaaSMedic\", "PerformRemediation")]
    [InlineData(@"\Microsoft\Windows\Windows Defender\", "Windows Defender Scheduled Scan")]
    [InlineData(@"\microsoft\windows\windows defender\", "x")]
    [InlineData(@"\", "MicrosoftEdgeUpdateTaskMachineCore")]
    public void Critical_tasks_are_rejected(string path, string name)
    {
        AssertRejected(Load(Tweak(apply: SchedTask(path, name))), "tarefa");
    }

    [Theory]
    [InlineData(@"\Limpeza\Test\", "Task; calc")]
    [InlineData(@"\Limpeza\Test\", "Task$(calc)")]
    [InlineData(@"\Limpeza\Test\", "Task\"")]
    [InlineData(@"\Limpeza\Test\", "")]
    [InlineData(@"Limpeza\Test\", "Task")]
    [InlineData(@"\Limpeza\Test", "Task")]
    [InlineData(@"\Limpeza\..\Test\", "Task")]
    [InlineData(@"\Limpeza'; calc; '\", "Task")]
    public void Malformed_task_paths_and_names_are_rejected(string path, string name)
    {
        var result = Load(Tweak(apply: SchedTask(path, name)));

        Assert.Empty(result.Catalog.Tweaks);
        Assert.True(result.HasErrors);
    }

    [Theory]
    [InlineData("Microsoft.WindowsStore")]
    [InlineData("Microsoft.StorePurchaseApp")]
    [InlineData("Microsoft.DesktopAppInstaller")]
    [InlineData("Microsoft.MicrosoftEdge.Stable")]
    [InlineData("Microsoft.MicrosoftEdgeDevToolsClient")]
    [InlineData("Microsoft.SecHealthUI")]
    [InlineData("Microsoft.Windows.ShellExperienceHost")]
    [InlineData("Microsoft.Windows.StartMenuExperienceHost")]
    [InlineData("MicrosoftWindows.Client.CBS")]
    [InlineData("Microsoft.VCLibs.140.00")]
    [InlineData("Microsoft.UI.Xaml.2.8")]
    [InlineData("Microsoft.NET.Native.Runtime.2.2")]
    [InlineData("Microsoft.WindowsAppRuntime.1.5")]
    [InlineData("microsoft.windowsstore")]
    public void Critical_packages_are_rejected(string package)
    {
        AssertRejected(Load(Tweak(apply: Appx(package))), "crítico");
    }

    [Theory]
    [InlineData("*")]
    [InlineData("Microsoft.*")]
    [InlineData("Clipchamp.Clipchamp; calc")]
    [InlineData("Foo Bar")]
    [InlineData("A")]
    [InlineData("")]
    public void Wildcards_and_malformed_package_names_are_rejected(string package)
    {
        var result = Load(Tweak(apply: Appx(package)));

        Assert.Empty(result.Catalog.Tweaks);
        Assert.True(result.HasErrors);
    }

    [Theory]
    [InlineData("Microsoft.WindowsFeedbackHub")]
    [InlineData("Microsoft.WindowsMaps")]
    [InlineData("Microsoft.MicrosoftOfficeHub")]
    [InlineData("Microsoft.GamingApp")]
    [InlineData("Microsoft.Xbox.TCUI")]
    [InlineData("MSTeams")]
    [InlineData("Clipchamp.Clipchamp")]
    public void Removable_inbox_apps_are_not_caught_by_the_deny_list(string package)
    {
        Assert.False(Load(Tweak(apply: Appx(package), revert: Appx(package, "reinstall"))).HasErrors);
    }

    [Fact]
    public void Delete_key_is_rejected_in_apply_because_it_cannot_be_backed_up()
    {
        const string deleteKey = """{ "type": "registry", "operation": "deleteKey", "hive": "hkcu", "path": "Software\\Classes\\CLSID\\{86ca1aa0-34aa-4e8b-a509-50c905bae2a2}" }""";

        AssertRejected(Load(Tweak(apply: deleteKey)), "revert");
        Assert.False(Load(Tweak(apply: ClsidApply("{86ca1aa0-34aa-4e8b-a509-50c905bae2a2}"), revert: deleteKey)).HasErrors);
    }

    [Theory]
    [InlineData("-DeleteMe")]
    [InlineData("--help")]
    [InlineData("/?")]
    public void Service_names_that_sc_exe_would_read_as_options_are_rejected(string name)
    {
        var result = Load(Tweak(apply: Service(name)));

        Assert.Empty(result.Catalog.Tweaks);
        Assert.True(result.HasErrors);
    }

    [Fact]
    public void Delayed_automatic_start_type_is_accepted()
    {
        Assert.False(Load(Tweak(apply: Service("FooSvc", "automaticDelayed"))).HasErrors);
    }

    [Fact]
    public void Profiles_cannot_reach_advanced_risk()
    {
        var result = TweakCatalogLoader.LoadFromSources([("p.json", ProfileFile(Profile("max", "advanced")))]);

        Assert.Empty(result.Catalog.Profiles);
        Assert.True(result.HasErrors);
    }

    [Fact]
    public void Valid_profiles_are_loaded_and_duplicates_rejected()
    {
        var result = TweakCatalogLoader.LoadFromSources([
            ("p.json", ProfileFile(Profile("light", "safe"), Profile("moderate", "moderate"), Profile("light", "safe"))),
        ]);

        Assert.Equal(["light", "moderate"], result.Catalog.Profiles.Select(p => p.Id));
        Assert.Contains(result.Issues, i => i.Message.Contains("duplicado", StringComparison.OrdinalIgnoreCase));
    }

    // ---------- Campos null no JSON ----------

    public static TheoryData<string> TweaksWithNullFields => new()
    {
        Tweak().Replace("\"id\": \"test.sample\"", "\"id\": null", StringComparison.Ordinal),
        Tweak().Replace("\"name\": \"Teste\"", "\"name\": null", StringComparison.Ordinal),
        Tweak(description: "Desc").Replace("\"description\": \"Desc\"", "\"description\": null", StringComparison.Ordinal),
        Tweak(apply: "__A__").Replace("[ __A__ ]", "null", StringComparison.Ordinal),
        Tweak(revert: "__R__").Replace("[ __R__ ]", "null", StringComparison.Ordinal),
    };

    [Theory]
    [MemberData(nameof(TweaksWithNullFields))]
    public void Null_required_fields_drop_the_tweak_without_crashing_the_loader(string tweak)
    {
        var result = Load(Tweak(id: "test.good"), tweak);

        Assert.Equal(["test.good"], result.Catalog.Tweaks.Select(t => t.Id));
        Assert.Contains(result.Issues, i => i.Severity == IssueSeverity.Error && i.Message.Contains("null", StringComparison.OrdinalIgnoreCase));
    }

    [Theory]
    [InlineData("null")]
    [InlineData("""{ "type": "registry", "hive": "hkcu", "path": null, "name": "V", "kind": "dword", "value": 1 }""")]
    [InlineData("""{ "type": "service", "name": null, "startType": "disabled" }""")]
    [InlineData("""{ "type": "scheduledTask", "taskPath": null, "taskName": "Job", "enabled": false }""")]
    [InlineData("""{ "type": "scheduledTask", "taskPath": "\\Limpeza\\", "taskName": null, "enabled": false }""")]
    [InlineData("""{ "type": "appx", "operation": "remove", "packageName": null }""")]
    public void Null_actions_or_action_fields_drop_the_tweak_without_crashing(string action)
    {
        var inApply = Load(Tweak(id: "test.good"), Tweak(id: "test.bad", apply: action));
        var inRevert = Load(Tweak(id: "test.good"), Tweak(id: "test.bad", revert: action));

        Assert.Equal(["test.good"], inApply.Catalog.Tweaks.Select(t => t.Id));
        Assert.Equal(["test.good"], inRevert.Catalog.Tweaks.Select(t => t.Id));
        Assert.Contains(inApply.Issues, i => i.TweakId == "test.bad" && i.Severity == IssueSeverity.Error);
        Assert.Contains(inRevert.Issues, i => i.TweakId == "test.bad" && i.Severity == IssueSeverity.Error);
    }

    [Theory]
    [InlineData("""{ "id": null, "name": "P", "description": "D", "maxRisk": "safe" }""")]
    [InlineData("""{ "id": "light", "name": null, "description": "D", "maxRisk": "safe" }""")]
    [InlineData("""{ "id": "light", "name": "P", "description": null, "maxRisk": "safe" }""")]
    public void Null_profile_fields_drop_the_profile_without_crashing(string profile)
    {
        var result = TweakCatalogLoader.LoadFromSources([("p.json", ProfileFile(Profile("moderate", "moderate"), profile))]);

        Assert.Equal(["moderate"], result.Catalog.Profiles.Select(p => p.Id));
        Assert.True(result.HasErrors);
    }

    [Fact]
    public void Validator_rejects_null_actions_passed_directly()
    {
        Assert.NotEmpty(TweakValidator.ValidateAction(null!, allowDeleteKey: false));
        Assert.NotEmpty(TweakValidator.ValidateAction(new ServiceAction { Name = null!, StartType = ServiceStartType.Disabled }, allowDeleteKey: false));
        Assert.NotEmpty(TweakValidator.ValidateAction(new RegistryAction { Hive = RegistryHive.Hkcu, Path = null!, Name = "V" }, allowDeleteKey: false));
    }

    // ---------- Aliases de caminho (WOW6432Node, ControlSetNNN) ----------

    [Theory]
    [InlineData("hklm", @"SOFTWARE\WOW6432Node\Microsoft\Windows Defender")]
    [InlineData("hklm", @"SOFTWARE\WOW6432Node\Policies\Microsoft\Windows\WindowsUpdate\AU")]
    [InlineData("hklm", @"software\wow6432node\microsoft\windows nt\currentversion\winlogon")]
    [InlineData("hklm", @"SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Run")]
    [InlineData("hklm", @"SOFTWARE\WOW6432Node\Microsoft\Edge")]
    [InlineData("hklm", @"SYSTEM\ControlSet001\Control\Lsa")]
    [InlineData("hklm", @"SYSTEM\ControlSet002\Control\SecurityProviders")]
    [InlineData("hklm", @"SYSTEM\ControlSet001\Control\Session Manager")]
    public void Registry_aliases_of_protected_areas_are_rejected(string hive, string path)
    {
        AssertRejected(Load(Tweak(apply: Registry(hive: hive, path: path))), "protegido");
    }

    [Theory]
    [InlineData(@"SOFTWARE\WOW6432Node\Microsoft\Windows Defender\Features", true)]
    [InlineData(@"SYSTEM\ControlSet003\Services\WinDefend", true)]
    [InlineData(@"SYSTEM\ControlSet001\Control\SafeBoot\Minimal", true)]
    [InlineData(@"SOFTWARE\WOW6432Node\Limpeza\Test", false)]
    [InlineData(@"SYSTEM\ControlSetX\Control\Lsa", false)]
    [InlineData(@"SOFTWARE\Microsoft\WOW6432Node\Windows Defender", false)]
    public void Protected_path_check_normalizes_aliases(string path, bool expected)
    {
        Assert.Equal(expected, CriticalComponents.IsProtectedRegistryPath(path));
    }

    // ---------- Inicialização, políticas e chaves de comando ----------

    [Theory]
    [InlineData("hkcu", @"Software\Microsoft\Windows\CurrentVersion\Run")]
    [InlineData("hklm", @"SOFTWARE\Microsoft\Windows\CurrentVersion\Run")]
    [InlineData("hkcu", @"Software\Microsoft\Windows\CurrentVersion\RunOnce")]
    [InlineData("hklm", @"SOFTWARE\Microsoft\Windows\CurrentVersion\RunOnce")]
    [InlineData("hkcu", @"Software\Microsoft\Windows\CurrentVersion\Policies\System")]
    [InlineData("hklm", @"SOFTWARE\Microsoft\Windows\CurrentVersion\Policies\System")]
    [InlineData("hkcu", @"Software\Microsoft\Windows\CurrentVersion\Policies\Explorer")]
    [InlineData("hklm", @"SOFTWARE\Microsoft\Windows\CurrentVersion\Policies\Explorer\DisallowRun")]
    [InlineData("hkcu", @"Software\Microsoft\Windows NT\CurrentVersion\Windows")]
    [InlineData("hklm", @"SOFTWARE\Microsoft\Windows NT\CurrentVersion\Windows")]
    [InlineData("hkcu", @"Software\Microsoft\Windows NT\CurrentVersion\Winlogon")]
    [InlineData("hklm", @"SOFTWARE\Microsoft\Windows NT\CurrentVersion\Winlogon")]
    [InlineData("hklm", @"SYSTEM\CurrentControlSet\Control\Session Manager")]
    [InlineData("hklm", @"SYSTEM\CurrentControlSet\Control\Session Manager\Environment")]
    [InlineData("hklm", @"SYSTEM\CurrentControlSet\Control\SafeBoot")]
    public void Startup_and_policy_areas_are_rejected_in_both_hives(string hive, string path)
    {
        AssertRejected(Load(Tweak(apply: Registry(hive: hive, path: path))), "protegido");
    }

    [Theory]
    [InlineData("hkcu", @"Software\Policies\Microsoft\Windows\Explorer")]
    [InlineData("hkcu", @"Software\Microsoft\Windows\CurrentVersion\Explorer\Advanced")]
    [InlineData("hkcu", @"Software\Microsoft\Windows\CurrentVersion\RunNotification")]
    [InlineData("hklm", @"SOFTWARE\Policies\Microsoft\Windows\System")]
    public void Neighbours_of_the_startup_and_policy_areas_are_still_accepted(string hive, string path)
    {
        Assert.False(Load(Tweak(apply: Registry(hive: hive, path: path))).HasErrors);
    }

    [Theory]
    [InlineData(@"Software\Classes\CLSID\{86ca1aa0-34aa-4e8b-a509-50c905bae2a2}\InprocServer32", "C:\\\\evil.dll")]
    [InlineData(@"Software\Classes\CLSID\{86ca1aa0-34aa-4e8b-a509-50c905bae2a2}\LocalServer32", "evil.exe")]
    [InlineData(@"Software\Classes\exefile\shell\open\command", "calc.exe")]
    [InlineData(@"Software\Classes\Foo\Shell\Open\COMMAND", "x")]
    public void Text_in_command_and_com_server_keys_is_rejected(string path, string text)
    {
        var json = System.Text.Json.JsonSerializer.Serialize(text);

        AssertRejected(Load(Tweak(apply: Registry(path: path, name: "", kind: "sz", valueJson: json))), "texto vazio");
        AssertRejected(Load(Tweak(apply: Registry(path: path, name: "Outro", kind: "expandSz", valueJson: json))), "texto vazio");
    }

    [Fact]
    public void Empty_text_and_numbers_in_command_keys_are_accepted()
    {
        const string path = @"Software\Classes\CLSID\{86ca1aa0-34aa-4e8b-a509-50c905bae2a2}\InprocServer32";

        Assert.False(Load(Tweak(apply: Registry(path: path, name: "", kind: "sz", valueJson: "\"\""))).HasErrors);
        Assert.False(Load(Tweak(apply: Registry(path: path, name: "N", kind: "dword", valueJson: "1"))).HasErrors);
    }

    // ---------- deleteKey restrito ao que o apply cria ----------

    [Fact]
    public void Delete_key_must_target_a_key_created_by_the_apply_of_the_same_tweak()
    {
        const string guid = "{86ca1aa0-34aa-4e8b-a509-50c905bae2a2}";
        const string other = "{11111111-2222-3333-4444-555555555555}";
        string DeleteKey(string g) =>
            $$"""{ "type": "registry", "operation": "deleteKey", "hive": "hkcu", "path": {{System.Text.Json.JsonSerializer.Serialize($@"Software\Classes\CLSID\{g}")}} }""";

        // A chave apagada é ancestral do valor gravado pelo apply.
        Assert.False(Load(Tweak(apply: ClsidApply(guid), revert: DeleteKey(guid))).HasErrors);

        // A chave apagada é exatamente a do valor gravado.
        var exact = Registry(path: $@"Software\Classes\CLSID\{guid}", name: "", kind: "sz", valueJson: "\"\"");
        Assert.False(Load(Tweak(apply: exact, revert: DeleteKey(guid))).HasErrors);

        // Outro CLSID, outro hive ou um apply que não grava nada ali.
        AssertRejected(Load(Tweak(apply: ClsidApply(other), revert: DeleteKey(guid))), "criada pelo 'apply'");
        AssertRejected(Load(Tweak(apply: ClsidApply(guid, hive: "hklm"), revert: DeleteKey(guid))), "criada pelo 'apply'");
        AssertRejected(Load(Tweak(revert: DeleteKey(guid))), "criada pelo 'apply'");

        // Prefixo de texto não é ancestral: {GUID}0 não fica abaixo de {GUID}.
        var sibling = Registry(path: $@"Software\Classes\CLSID\{guid}0\InprocServer32", name: "", kind: "sz", valueJson: "\"\"");
        AssertRejected(Load(Tweak(apply: sibling, revert: DeleteKey(guid))), "criada pelo 'apply'");
    }

    [Fact]
    public void Delete_key_target_must_be_created_by_a_set_not_by_a_delete_value()
    {
        const string guid = "{86ca1aa0-34aa-4e8b-a509-50c905bae2a2}";
        var deleteValue = $$"""{ "type": "registry", "operation": "deleteValue", "hive": "hkcu", "path": {{System.Text.Json.JsonSerializer.Serialize($@"Software\Classes\CLSID\{guid}\InprocServer32")}}, "name": "" }""";
        var deleteKey = $$"""{ "type": "registry", "operation": "deleteKey", "hive": "hkcu", "path": {{System.Text.Json.JsonSerializer.Serialize($@"Software\Classes\CLSID\{guid}")}} }""";

        AssertRejected(Load(Tweak(apply: deleteValue, revert: deleteKey)), "criada pelo 'apply'");
    }

    // ---------- Exclusões não aceitam value/kind ----------

    [Theory]
    [InlineData("deleteValue", "\"value\": 1")]
    [InlineData("deleteValue", "\"kind\": \"dword\"")]
    [InlineData("deleteValue", "\"value\": null")]
    [InlineData("deleteKey", "\"value\": \"\"")]
    [InlineData("deleteKey", "\"kind\": \"sz\"")]
    public void Delete_operations_reject_value_and_kind(string operation, string extraField)
    {
        const string guid = "{86ca1aa0-34aa-4e8b-a509-50c905bae2a2}";
        var path = operation == "deleteKey" ? $@"Software\Classes\CLSID\{guid}" : @"Software\Limpeza\Test";
        var name = operation == "deleteKey" ? "" : "\"name\": \"Value\",";
        var action = $$"""{ "type": "registry", "operation": "{{operation}}", "hive": "hkcu", "path": {{System.Text.Json.JsonSerializer.Serialize(path)}}, {{name}} {{extraField}} }""";

        AssertRejected(Load(Tweak(apply: ClsidApply(guid), revert: action)), "não aceita 'value' nem 'kind'");
    }
}
