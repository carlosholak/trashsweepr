using TrashSweepr.Cleanup.Core.Models;

namespace TrashSweepr.Cleanup.Core.Abstractions;

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
            default { throw 'Invalid startup type.' }
        }
        # sc.exe escreve o motivo da falha na saída padrão; sem 2>&1, que no PS 5.1 com 'Stop' viraria exceção.
        $out = [string](& sc.exe config $a.name start= $mode | Out-String)
        if ($LASTEXITCODE -ne 0) { throw "sc.exe config failed (exit code $LASTEXITCODE): $($out.Trim())" }
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

    /// <summary>
    /// Mensagem exata do <c>throw</c> de <see cref="ReinstallAppx"/> quando o pacote sumiu do sistema. Os scripts são
    /// constantes e só falam inglês; quem executa troca esta mensagem pela versão traduzida.
    /// </summary>
    public const string PackageNotFoundError = "Package not found on this system.";

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
        if ($pkgs.Count -eq 0) { throw 'Package not found on this system.' }
        foreach ($p in $pkgs) {
            $manifest = Join-Path $p.InstallLocation 'AppxManifest.xml'
            if (-not (Test-Path -LiteralPath $manifest)) { throw "Manifest not found: $manifest" }
            Add-AppxPackage -DisableDevelopmentMode -Register $manifest
        }
        """,
        ("name", packageName));

    // ---------- Programas (Edge, OneDrive) ----------
    //
    // Um script fixo por par (programa, operação), sem argumentos: nada do JSON entra aqui. Os scripts que alteram
    // o sistema devolvem { found, exitCode, installed } e quem executa (Infrastructure) decide se deu certo e
    // traduz a mensagem. 'found' diz se o (des)instalador oficial existe; 'installed' é o estado depois de rodar.
    // O Edge é desinstalado só pelo setup.exe do próprio Edge: nenhum truque de registro para contornar a trava
    // regional, e o WebView2 (Microsoft\EdgeWebView) e o Edge Update nunca são tocados.

    private const string EdgeDetection =
        """
        function Test-EdgeInstalled {
            foreach ($root in @(${env:ProgramFiles(x86)}, $env:ProgramFiles)) {
                if ($root -and (Test-Path -LiteralPath (Join-Path $root 'Microsoft\Edge\Application\msedge.exe'))) { return $true }
            }
            return $false
        }
        """;

    private const string OneDriveDetection =
        """
        function Test-OneDriveInstalled {
            $paths = @()
            if ($env:LOCALAPPDATA) { $paths += Join-Path $env:LOCALAPPDATA 'Microsoft\OneDrive\OneDrive.exe' }
            if ($env:ProgramFiles) { $paths += Join-Path $env:ProgramFiles 'Microsoft OneDrive\OneDrive.exe' }
            if (${env:ProgramFiles(x86)}) { $paths += Join-Path ${env:ProgramFiles(x86)} 'Microsoft OneDrive\OneDrive.exe' }
            foreach ($p in $paths) { if (Test-Path -LiteralPath $p) { return $true } }
            return $false
        }
        """;

    private const string OneDriveSetupLookup =
        """
        function Find-OneDriveSetup {
            foreach ($dir in @('System32', 'SysWOW64')) {
                $setup = Join-Path $env:SystemRoot ($dir + '\OneDriveSetup.exe')
                if (Test-Path -LiteralPath $setup) { return $setup }
            }
            return $null
        }
        """;

    private const string WingetLookup =
        """
        function Find-Winget {
            Get-Command -Name winget.exe -CommandType Application -ErrorAction SilentlyContinue | Select-Object -First 1
        }
        """;

    private const string GetEdgeScript = EdgeDetection + "\n" +
        """
        ConvertTo-Json -Compress -InputObject @{ installed = (Test-EdgeInstalled) }
        """;

    private const string GetOneDriveScript = OneDriveDetection + "\n" +
        """
        ConvertTo-Json -Compress -InputObject @{ installed = (Test-OneDriveInstalled) }
        """;

    private const string UninstallEdgeScript = EdgeDetection + "\n" +
        """
        $setup = $null
        foreach ($root in @(${env:ProgramFiles(x86)}, $env:ProgramFiles)) {
            if (-not $root) { continue }
            $app = Join-Path $root 'Microsoft\Edge\Application'
            if (-not (Test-Path -LiteralPath $app)) { continue }
            # A pasta de versão mais nova que tenha o desinstalador do próprio Edge.
            $newest = Get-ChildItem -LiteralPath $app -Directory |
                Where-Object { ($_.Name -as [version]) -and (Test-Path -LiteralPath (Join-Path $_.FullName 'Installer\setup.exe')) } |
                Sort-Object -Property @{ Expression = { [version]$_.Name } } -Descending |
                Select-Object -First 1
            if ($newest) { $setup = Join-Path $newest.FullName 'Installer\setup.exe'; break }
        }
        if (-not $setup) { ConvertTo-Json -Compress -InputObject @{ found = $false; exitCode = $null; installed = (Test-EdgeInstalled) }; return }
        $p = Start-Process -FilePath $setup -ArgumentList '--uninstall', '--system-level', '--force-uninstall', '--verbose-logging' -Wait -PassThru -WindowStyle Hidden
        # O setup.exe pode terminar um pouco antes de apagar o msedge.exe: espera até 30 s.
        if ($p.ExitCode -eq 0) { for ($i = 0; $i -lt 60 -and (Test-EdgeInstalled); $i++) { Start-Sleep -Milliseconds 500 } }
        ConvertTo-Json -Compress -InputObject @{ found = $true; exitCode = $p.ExitCode; installed = (Test-EdgeInstalled) }
        """;

    private const string ReinstallEdgeScript = EdgeDetection + "\n" + WingetLookup + "\n" +
        """
        $winget = Find-Winget
        if (-not $winget) { ConvertTo-Json -Compress -InputObject @{ found = $false; exitCode = $null; installed = (Test-EdgeInstalled) }; return }
        # A saída do winget (barra de progresso) é descartada: o stdout deste script é só o JSON final.
        $null = & $winget.Source install --id Microsoft.Edge -e --silent --accept-package-agreements --accept-source-agreements
        $code = $LASTEXITCODE
        if ($code -eq 0) { for ($i = 0; $i -lt 60 -and -not (Test-EdgeInstalled); $i++) { Start-Sleep -Milliseconds 500 } }
        ConvertTo-Json -Compress -InputObject @{ found = $true; exitCode = $code; installed = (Test-EdgeInstalled) }
        """;

    private const string UninstallOneDriveScript = OneDriveDetection + "\n" + OneDriveSetupLookup + "\n" +
        """
        $setup = Find-OneDriveSetup
        # Sem o desinstalador, nada é feito (nem o OneDrive é fechado).
        if (-not $setup) { ConvertTo-Json -Compress -InputObject @{ found = $false; exitCode = $null; installed = (Test-OneDriveInstalled) }; return }
        Stop-Process -Name OneDrive -Force -ErrorAction SilentlyContinue
        $p = Start-Process -FilePath $setup -ArgumentList '/uninstall' -Wait -PassThru -WindowStyle Hidden
        # O desinstalador pode terminar um pouco antes de apagar o OneDrive.exe: espera até 30 s.
        if ($p.ExitCode -eq 0) { for ($i = 0; $i -lt 60 -and (Test-OneDriveInstalled); $i++) { Start-Sleep -Milliseconds 500 } }
        ConvertTo-Json -Compress -InputObject @{ found = $true; exitCode = $p.ExitCode; installed = (Test-OneDriveInstalled) }
        """;

    private const string ReinstallOneDriveScript = OneDriveDetection + "\n" + OneDriveSetupLookup + "\n" + WingetLookup + "\n" +
        """
        $setup = Find-OneDriveSetup
        if ($setup) {
            # Instalador que vem com o Windows, em modo silencioso.
            $p = Start-Process -FilePath $setup -ArgumentList '/silent' -Wait -PassThru -WindowStyle Hidden
            $code = $p.ExitCode
        } else {
            $winget = Find-Winget
            if (-not $winget) { ConvertTo-Json -Compress -InputObject @{ found = $false; exitCode = $null; installed = (Test-OneDriveInstalled) }; return }
            # A saída do winget (barra de progresso) é descartada: o stdout deste script é só o JSON final.
            $null = & $winget.Source install --id Microsoft.OneDrive -e --silent --accept-package-agreements --accept-source-agreements
            $code = $LASTEXITCODE
        }
        if ($code -eq 0) { for ($i = 0; $i -lt 120 -and -not (Test-OneDriveInstalled); $i++) { Start-Sleep -Milliseconds 500 } }
        ConvertTo-Json -Compress -InputObject @{ found = $true; exitCode = $code; installed = (Test-OneDriveInstalled) }
        """;

    /// <summary>Só lê arquivos: devolve { installed }.</summary>
    public static PowerShellCall GetProgram(ManagedProgram program) => Call(program switch
    {
        ManagedProgram.Edge => GetEdgeScript,
        ManagedProgram.OneDrive => GetOneDriveScript,
        _ => throw new ArgumentOutOfRangeException(nameof(program)),
    });

    public static PowerShellCall UninstallProgram(ManagedProgram program) => Call(program switch
    {
        ManagedProgram.Edge => UninstallEdgeScript,
        ManagedProgram.OneDrive => UninstallOneDriveScript,
        _ => throw new ArgumentOutOfRangeException(nameof(program)),
    });

    public static PowerShellCall ReinstallProgram(ManagedProgram program) => Call(program switch
    {
        ManagedProgram.Edge => ReinstallEdgeScript,
        ManagedProgram.OneDrive => ReinstallOneDriveScript,
        _ => throw new ArgumentOutOfRangeException(nameof(program)),
    });

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
