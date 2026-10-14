using System.Runtime.Versioning;
using System.Security.Principal;
using Microsoft.Win32;
using TrashSweepr.Limpeza.Core.Abstractions;

namespace TrashSweepr.Limpeza.Infrastructure;

[SupportedOSPlatform("windows")]
public sealed class WindowsInfo : IWindowsInfo
{
    public int Build { get; } = Environment.OSVersion.Version.Build;

    public string? DisplayVersion { get; } = ReadDisplayVersion();

    public bool IsElevated { get; } = CheckElevated();

    private static string? ReadDisplayVersion()
    {
        using var key = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows NT\CurrentVersion");
        return key?.GetValue("DisplayVersion") as string ?? key?.GetValue("ReleaseId") as string;
    }

    private static bool CheckElevated()
    {
        using var identity = WindowsIdentity.GetCurrent();
        return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
    }
}
