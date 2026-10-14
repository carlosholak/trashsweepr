namespace TrashSweepr.Cleanup.Core.Catalog;

/// <summary>
/// Deny-list de componentes críticos. Vive no código (e não no JSON) de propósito:
/// nenhum arquivo de catálogo consegue desligá-la. O validador rejeita qualquer tweak que a toque.
/// </summary>
public static class CriticalComponents
{
    private static readonly HashSet<string> ProtectedServices = new(StringComparer.OrdinalIgnoreCase)
    {
        // Defender / segurança
        "WinDefend", "WdNisSvc", "WdNisDrv", "WdFilter", "WdBoot", "Sense", "SecurityHealthService", "wscsvc",
        // Windows Update e instalação
        "wuauserv", "UsoSvc", "WaaSMedicSvc", "BITS", "TrustedInstaller", "msiserver", "CryptSvc",
        // Microsoft Store e Edge
        "ClipSVC", "InstallService", "AppXSvc", "edgeupdate", "edgeupdatem", "MicrosoftEdgeElevationService",
        // Rede
        "Dhcp", "Dnscache", "NlaSvc", "nsi", "netprofm", "Netman", "LanmanWorkstation", "LanmanServer",
        "WlanSvc", "Wcmsvc", "WinHttpAutoProxySvc", "lmhosts", "mpssvc", "BFE", "Tcpip", "NetBT",
        // Áudio
        "Audiosrv", "AudioEndpointBuilder", "AudioSrv", "MMCSS",
        // Núcleo do sistema
        "RpcSs", "RpcEptMapper", "DcomLaunch", "Winmgmt", "EventLog", "PlugPlay", "Power", "ProfSvc",
        "StateRepository", "BrokerInfrastructure", "SystemEventsBroker", "LSM", "gpsvc", "Schedule", "SamSs",
        "W32Time", "TimeBrokerSvc", "UserManager", "Themes", "ShellHWDetection",
    };

    private static readonly string[] ProtectedTaskPathPrefixes =
    [
        @"\Microsoft\Windows\WindowsUpdate\",
        @"\Microsoft\Windows\UpdateOrchestrator\",
        @"\Microsoft\Windows\WaaSMedic\",
        @"\Microsoft\Windows\Windows Defender\",
        @"\Microsoft\Windows\Defrag\",
        @"\Microsoft\Windows\SystemRestore\",
        @"\Microsoft\Windows\Time Synchronization\",
        @"\Microsoft\Windows\Servicing\",
        @"\Microsoft\EdgeUpdate\",
    ];

    private static readonly string[] ProtectedTaskNamePrefixes =
    [
        "MicrosoftEdgeUpdate",
    ];

    private static readonly string[] ProtectedPackageExact =
    [
        "Microsoft.WindowsStore",
        "Microsoft.StorePurchaseApp",
        "Microsoft.DesktopAppInstaller",
        "Microsoft.SecHealthUI",
        "Microsoft.Services.Store.Engagement",
        "Microsoft.AAD.BrokerPlugin",
        "Microsoft.AccountsControl",
        "Microsoft.AsyncTextService",
        "Microsoft.BioEnrollment",
        "Microsoft.CredDialogHost",
        "Microsoft.LockApp",
        "Microsoft.Win32WebViewHost",
    ];

    private static readonly string[] ProtectedPackagePrefixes =
    [
        "Microsoft.MicrosoftEdge",
        "Microsoft.Edge",
        "Microsoft.Windows.",
        "MicrosoftWindows.",
        "Windows.",
        "Microsoft.UI.",
        "Microsoft.VCLibs",
        "Microsoft.NET.",
        "Microsoft.WindowsAppRuntime",
        "WindowsAppRuntime",
    ];

    // Prefixos de chave de registro (sem o hive), comparados por segmento e sem diferenciar maiúsculas.
    // Valem para HKCU e HKLM: as áreas de inicialização e de política existem nos dois hives.
    private static readonly string[][] ProtectedRegistryPrefixes =
    [
        // Programas iniciados automaticamente e políticas de sistema/Explorer.
        ["SOFTWARE", "Microsoft", "Windows", "CurrentVersion", "Run"],
        ["SOFTWARE", "Microsoft", "Windows", "CurrentVersion", "RunOnce"],
        ["SOFTWARE", "Microsoft", "Windows", "CurrentVersion", "Policies", "System"],
        ["SOFTWARE", "Microsoft", "Windows", "CurrentVersion", "Policies", "Explorer"],
        ["SOFTWARE", "Microsoft", "Windows NT", "CurrentVersion", "Windows"],
        ["SYSTEM", "CurrentControlSet", "Control", "Session Manager"],
        ["SYSTEM", "CurrentControlSet", "Control", "SafeBoot"],
        ["SOFTWARE", "Microsoft", "Windows Defender"],
        ["SOFTWARE", "Microsoft", "Windows Defender Security Center"],
        ["SOFTWARE", "Policies", "Microsoft", "Windows Defender"],
        ["SOFTWARE", "Policies", "Microsoft", "Windows Defender Security Center"],
        ["SOFTWARE", "Microsoft", "Windows", "CurrentVersion", "WindowsUpdate"],
        ["SOFTWARE", "Microsoft", "WindowsUpdate"],
        ["SOFTWARE", "Policies", "Microsoft", "Windows", "WindowsUpdate"],
        ["SOFTWARE", "Policies", "Microsoft", "Edge"],
        ["SOFTWARE", "Microsoft", "Edge"],
        ["SOFTWARE", "Microsoft", "EdgeUpdate"],
        ["SOFTWARE", "Policies", "Microsoft", "EdgeUpdate"],
        ["SOFTWARE", "Policies", "Microsoft", "WindowsStore"],
        ["SOFTWARE", "Microsoft", "Windows NT", "CurrentVersion", "Winlogon"],
        ["SOFTWARE", "Microsoft", "Windows NT", "CurrentVersion", "Image File Execution Options"],
        ["SYSTEM", "CurrentControlSet", "Control", "SecurityProviders"],
        ["SYSTEM", "CurrentControlSet", "Control", "Lsa"],
    ];

    public static bool IsProtectedService(string name) => ProtectedServices.Contains(name);

    public static bool IsProtectedTask(string taskPath, string taskName) =>
        ProtectedTaskPathPrefixes.Any(p => taskPath.StartsWith(p, StringComparison.OrdinalIgnoreCase))
        || ProtectedTaskNamePrefixes.Any(p => taskName.StartsWith(p, StringComparison.OrdinalIgnoreCase));

    public static bool IsProtectedPackage(string packageName) =>
        ProtectedPackageExact.Contains(packageName, StringComparer.OrdinalIgnoreCase)
        || ProtectedPackagePrefixes.Any(p => packageName.StartsWith(p, StringComparison.OrdinalIgnoreCase));

    // Chaves cujo texto o Windows executa ou carrega (servidores COM e verbos de shell).
    private static readonly string[] CommandKeySegments = ["InprocServer32", "LocalServer32", "command"];

    /// <summary>
    /// Algum segmento do caminho é InprocServer32, LocalServer32 ou command: um valor de texto ali aponta
    /// para uma DLL ou um executável.
    /// </summary>
    public static bool IsCommandRegistryPath(string path) =>
        path.Split('\\', StringSplitOptions.RemoveEmptyEntries)
            .Any(segment => CommandKeySegments.Contains(segment.Trim(), StringComparer.OrdinalIgnoreCase));

    /// <summary>Verifica se o caminho (sem hive) está em uma área protegida ou configura um serviço protegido.</summary>
    public static bool IsProtectedRegistryPath(string path)
    {
        var segments = NormalizeRegistrySegments(path.Split('\\', StringSplitOptions.RemoveEmptyEntries));

        foreach (var prefix in ProtectedRegistryPrefixes)
        {
            if (StartsWithSegments(segments, prefix))
            {
                return true;
            }
        }

        // SYSTEM\<ControlSet>\Services\<serviço protegido>
        return segments.Length >= 4
            && segments[0].Equals("SYSTEM", StringComparison.OrdinalIgnoreCase)
            && (segments[1].StartsWith("CurrentControlSet", StringComparison.OrdinalIgnoreCase)
                || segments[1].StartsWith("ControlSet", StringComparison.OrdinalIgnoreCase))
            && segments[2].Equals("Services", StringComparison.OrdinalIgnoreCase)
            && IsProtectedService(segments[3]);
    }

    /// <summary>
    /// Leva aliases para a forma canônica antes de comparar com a deny-list: a visão de 32 bits
    /// (SOFTWARE\WOW6432Node\..., SOFTWARE\Classes\WOW6432Node\...) e os conjuntos de controle numerados
    /// (SYSTEM\ControlSet001\...), que são a mesma área que SYSTEM\CurrentControlSet.
    /// </summary>
    private static string[] NormalizeRegistrySegments(string[] segments)
    {
        var list = segments.Select(s => s.Trim()).ToList();

        if (list.Count > 1 && Is(list[0], "SOFTWARE") && Is(list[1], "WOW6432Node"))
        {
            list.RemoveAt(1);
        }

        if (list.Count > 2 && Is(list[0], "SOFTWARE") && Is(list[1], "Classes") && Is(list[2], "WOW6432Node"))
        {
            list.RemoveAt(2);
        }

        if (list.Count > 1 && Is(list[0], "SYSTEM") && IsNumberedControlSet(list[1]))
        {
            list[1] = "CurrentControlSet";
        }

        return [.. list];

        static bool Is(string segment, string expected) => segment.Equals(expected, StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsNumberedControlSet(string segment) =>
        segment.Length > "ControlSet".Length
        && segment.StartsWith("ControlSet", StringComparison.OrdinalIgnoreCase)
        && segment["ControlSet".Length..].All(char.IsAsciiDigit);

    private static bool StartsWithSegments(string[] segments, string[] prefix)
    {
        if (segments.Length < prefix.Length)
        {
            return false;
        }

        for (var i = 0; i < prefix.Length; i++)
        {
            if (!segments[i].Equals(prefix[i], StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }
        }

        return true;
    }
}
