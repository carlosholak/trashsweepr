using TrashSweepr.Limpeza.Core.Models;

namespace TrashSweepr.Limpeza.Core.Abstractions;

/// <summary>
/// Todos os scripts PowerShell do trashsweepr. São constantes: o que varia (nome do serviço, pacote...) entra
/// apenas como argumento ($a.name, $a.taskPath...), já validado, e nunca como parte do texto do script.
/// Quem executa (Infrastructure) e quem descreve o dry run (Core) usam estas mesmas definições.
/// </summary>
public static class PowerShellCalls
{
    private static PowerShellCall Call(string script, params (string Key, object? Value)[] arguments) =>
        new(script, arguments.ToDictionary(a => a.Key, a => a.Value));

    // ---------- Serviços ----------

    public static PowerShellCall GetService(string name) => Call(
        """
        $s = Get-Service -Name $a.name -ErrorAction SilentlyContinue
        if (-not $s) { ConvertTo-Json -Compress -InputObject @{ exists = $false }; return }
        $k = Get-ItemProperty -LiteralPath ('HKLM:\SYSTEM\CurrentControlSet\Services\' + $a.name) -ErrorAction SilentlyContinue
        $delayed = [bool]($k -and $k.Start -eq 2 -and $k.DelayedAutostart -eq 1)
        ConvertTo-Json -Compress -InputObject @{ exists = $true; startType = [string]$s.StartType; delayed = $delayed; running = ($s.Status -eq 'Running') }
        """,
        ("name", name));

    public static PowerShellCall SetServiceStartType(string name, ServiceStartType startType) => Call(
        """
        $mode = switch ($a.startType) {
            'Automatic' { 'auto' }
            'AutomaticDelayed' { 'delayed-auto' }
            'Manual' { 'demand' }
            'Disabled' { 'disabled' }
            default { throw 'Tipo de inicialização inválido.' }
        }
        # sc.exe escreve o motivo da falha na saída padrão; sem 2>&1, que no PS 5.1 com 'Stop' viraria exceção.
        $out = [string](& sc.exe config $a.name start= $mode | Out-String)
        if ($LASTEXITCODE -ne 0) { throw "sc.exe config falhou (código $LASTEXITCODE): $($out.Trim())" }
        """,
        ("name", name),
        ("startType", startType.ToString()));

    public static PowerShellCall StopService(string name) => Call(
        "Stop-Service -Name $a.name",
        ("name", name));

    public static PowerShellCall StartService(string name) => Call(
        "Start-Service -Name $a.name",
        ("name", name));

    // ---------- Tarefas agendadas ----------

    public static PowerShellCall GetTask(string taskPath, string taskName) => Call(
        """
        $t = Get-ScheduledTask -TaskPath $a.taskPath -TaskName $a.taskName -ErrorAction SilentlyContinue
        if (-not $t) { ConvertTo-Json -Compress -InputObject @{ exists = $false }; return }
        ConvertTo-Json -Compress -InputObject @{ exists = $true; enabled = ($t.State -ne 'Disabled') }
        """,
        ("taskPath", taskPath),
        ("taskName", taskName));

    public static PowerShellCall SetTaskEnabled(string taskPath, string taskName, bool enabled) => Call(
        """
        if ($a.enabled) { Enable-ScheduledTask -TaskPath $a.taskPath -TaskName $a.taskName | Out-Null }
        else { Disable-ScheduledTask -TaskPath $a.taskPath -TaskName $a.taskName | Out-Null }
        """,
        ("taskPath", taskPath),
        ("taskName", taskName),
        ("enabled", enabled));

    // ---------- Apps (Appx) ----------

    public static PowerShellCall ListAppx() => Call(
        "ConvertTo-Json -Compress -InputObject @(Get-AppxPackage | ForEach-Object { $_.Name })");

    public static PowerShellCall GetAppx(string packageName) => Call(
        """
        $mine = @(Get-AppxPackage -Name $a.name)
        $all = @(Get-AppxPackage -AllUsers -Name $a.name)
        $files = @($all | ForEach-Object { @{ fullName = $_.PackageFullName; installLocation = $_.InstallLocation } })
        ConvertTo-Json -Compress -Depth 4 -InputObject @{ installed = ($mine.Count -gt 0); packages = $files }
        """,
        ("name", packageName));

    /// <summary>Remove apenas para o usuário atual: sem -AllUsers e sem desprovisionar.</summary>
    public static PowerShellCall RemoveAppx(string packageName) => Call(
        "Get-AppxPackage -Name $a.name | Remove-AppxPackage",
        ("name", packageName));

    public static PowerShellCall ReinstallAppx(string packageName) => Call(
        """
        $pkgs = @(Get-AppxPackage -AllUsers -Name $a.name)
        if ($pkgs.Count -eq 0) { throw 'Pacote não encontrado no sistema. Reinstale pela Microsoft Store.' }
        foreach ($p in $pkgs) {
            $manifest = Join-Path $p.InstallLocation 'AppxManifest.xml'
            if (-not (Test-Path -LiteralPath $manifest)) { throw "Manifesto não encontrado: $manifest" }
            Add-AppxPackage -DisableDevelopmentMode -Register $manifest
        }
        """,
        ("name", packageName));

    // ---------- Ponto de restauração ----------

    public static PowerShellCall GetRestoreProtection() => Call(
        """
        $reason = $null
        $pol = Get-ItemProperty -LiteralPath 'HKLM:\SOFTWARE\Policies\Microsoft\Windows NT\SystemRestore' -ErrorAction SilentlyContinue
        $sr = Get-ItemProperty -LiteralPath 'HKLM:\SOFTWARE\Microsoft\Windows NT\CurrentVersion\SystemRestore' -ErrorAction SilentlyContinue
        if ($pol -and $pol.DisableSR -eq 1) { $reason = 'policy' }
        elseif ($sr -and $sr.RPSessionInterval -eq 0) { $reason = 'interval' }
        else {
            $vol = Get-CimInstance Win32_Volume | Where-Object { $_.DriveLetter -eq $env:SystemDrive } | Select-Object -First 1
            $storage = @(Get-CimInstance Win32_ShadowStorage | Where-Object { $vol -and $_.Volume.DeviceID -eq $vol.DeviceID })
            if ($storage.Count -eq 0) { $reason = 'noStorage' }
        }
        ConvertTo-Json -Compress -InputObject @{ enabled = ($null -eq $reason); reason = $reason }
        """);

    public static PowerShellCall CreateRestorePoint(string description) => Call(
        """
        $last = { try { (Get-ComputerRestorePoint | Sort-Object SequenceNumber | Select-Object -Last 1).SequenceNumber } catch { $null } }
        $before = & $last
        Checkpoint-Computer -Description $a.description -RestorePointType MODIFY_SETTINGS -WarningAction SilentlyContinue
        $after = & $last
        ConvertTo-Json -Compress -InputObject @{ created = ($after -ne $before) }
        """,
        ("description", description));
}
