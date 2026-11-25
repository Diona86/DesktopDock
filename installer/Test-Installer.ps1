[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$BaseInstaller,
    [Parameter(Mandatory)][string]$UpdateInstaller,
    [Parameter(Mandatory)][string]$RunDirectory
)

$ErrorActionPreference = 'Stop'
$OutputEncoding = [Text.UTF8Encoding]::new($false)
[Console]::OutputEncoding = [Text.UTF8Encoding]::new($false)
$base = (Resolve-Path -LiteralPath $BaseInstaller).Path
$update = (Resolve-Path -LiteralPath $UpdateInstaller).Path
foreach ($package in @($base, $update)) {
    if (!(Get-Item -LiteralPath $package).BaseName.EndsWith('-validation') -or
        (Get-Item -LiteralPath $package).VersionInfo.FileDescription.Trim() -ne 'Desktop Dock P6 Validation Setup') {
        throw 'Use only a package built with -Validation.'
    }
}
$run = $ExecutionContext.SessionState.Path.GetUnresolvedProviderPathFromPSPath($RunDirectory)
if (Test-Path -LiteralPath $run) { throw 'RunDirectory must be new.' }
$profile = Join-Path $run '隔离配置'
$backups = Join-Path $run '升级备份'
$links = Join-Path $run '测试快捷方式'
$target = Join-Path $run '原始项目目标.txt'
$destination = Join-Path $run 'Program Files'
$desktopLink = Join-Path $links 'Desktop Dock P6 Validation.lnk'
$uninstallKey = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Uninstall\DesktopDock.Windows.P6.Validation_is1'
$startupKey = 'HKCU:\Software\DesktopDock.Windows.P6.Validation\StartupProbe'
$productGroup = Join-Path ([Environment]::GetFolderPath([Environment+SpecialFolder]::Programs)) 'Desktop Dock P6 Validation'
if ((Test-Path -LiteralPath $uninstallKey) -or (Test-Path -LiteralPath $startupKey) -or
    (Test-Path -LiteralPath $productGroup)) { throw 'An earlier validation installation exists.' }
New-Item -ItemType Directory -Path $run, $profile, $links | Out-Null
$summary = Join-Path $run 'installer-validation.log'
$script:passed = 0

function Write-Evidence([string]$Message) {
    $line = "$([DateTimeOffset]::UtcNow.ToString('o')) $Message"
    $line | Add-Content -LiteralPath $summary -Encoding utf8
    Write-Host $line
}

function Check([bool]$Condition, [string]$Description) {
    if (!$Condition) { throw "Failed check: $Description" }
    $script:passed++
    Write-Evidence "PASS $Description"
}

function Package-Arguments([string]$LogFile) {
    @('/VERYSILENT', '/SUPPRESSMSGBOXES', '/NORESTART', '/NOCLOSEAPPLICATIONS', '/SP-', '/LANG=chinesesimp',
        ('/LOG="' + $LogFile + '"'), ('/PROFILE="' + $profile + '"'), ('/BACKUPS="' + $backups + '"'),
        ('/SHORTCUTDIR="' + $links + '"'))
}

function Open-Package([string]$File, [string]$Label, [string[]]$Options = @()) {
    $detail = Join-Path $run "$Label.log"
    $arguments = @(Package-Arguments $detail) + $Options
    $process = Start-Process -FilePath $File -ArgumentList $arguments -WindowStyle Hidden -PassThru -Wait
    Write-Evidence "$Label exit=$($process.ExitCode) log=$detail"
    return $process.ExitCode
}

function Setup([string]$File, [string]$Label, [string[]]$Options = @()) {
    Open-Package $File $Label (@('/DIR="' + $destination + '"') + $Options)
}

function Interrupt-Update {
    $detail = Join-Path $run 'interrupted-upgrade.log'
    $marker = Join-Path $run 'interrupted-upgrade.worker'
    $arguments = @(Package-Arguments $detail) + @(
        ('/DIR="' + $destination + '"'), ('/INTERRUPT_MARKER="' + $marker + '"'))
    $launcher = Start-Process -FilePath $update -ArgumentList $arguments -WindowStyle Hidden -PassThru
    try {
        $deadline = [DateTimeOffset]::UtcNow.AddSeconds(30)
        while (!(Test-Path -LiteralPath $marker) -and !$launcher.HasExited -and [DateTimeOffset]::UtcNow -lt $deadline) {
            Start-Sleep -Milliseconds 100
        }
        if (!(Test-Path -LiteralPath $marker)) { throw 'Update did not reach its first-file checkpoint.' }
        $workerId = [int](Get-Content -LiteralPath $marker -Raw -Encoding utf8)
        $worker = Get-CimInstance -ClassName Win32_Process -Filter "ProcessId=$workerId"
        if (!$worker -or $worker.ParentProcessId -ne $launcher.Id -or
            !$worker.Name.EndsWith('.tmp') -or !$worker.CommandLine.Contains($marker)) {
            throw 'Checkpoint did not identify the validation installer child process.'
        }
        $firstFile = Join-Path $destination ("versions/$version2/DesktopDock.SetupHelper.exe")
        if (!(Test-Path -LiteralPath $firstFile -PathType Leaf)) { throw 'Checkpoint occurred before the first file was written.' }
        Stop-Process -Id $workerId -Force
        if (!$launcher.WaitForExit(5000)) { throw 'Validation launcher is still running after interruption.' }
        Write-Evidence "interrupted-upgrade worker=$workerId exit=$($launcher.ExitCode) log=$detail"
    }
    finally {
        if (!$launcher.HasExited) { $null = $launcher.WaitForExit(20000) }
    }
}

function Read-Version {
    (Get-ItemProperty -LiteralPath $uninstallKey -ErrorAction Stop).DisplayVersion
}

function Find-Uninstaller {
    $location = (Get-ItemProperty -LiteralPath $uninstallKey -ErrorAction Stop).InstallLocation
    if ([IO.Path]::GetFullPath($location).TrimEnd('\') -ne [IO.Path]::GetFullPath($destination).TrimEnd('\')) {
        throw 'Uninstaller location is not this test installation.'
    }
    $exe = @(Get-ChildItem -LiteralPath $destination -File -Filter 'unins*.exe')
    if ($exe.Count -ne 1) { throw 'Expected one owned uninstaller.' }
    return $exe[0].FullName
}

'preserve original project target' | Set-Content -LiteralPath $target -Encoding utf8
$originalTargetHash = (Get-FileHash -LiteralPath $target).Hash
$dataFile = Join-Path $profile 'dock.json'
$unrelated = Join-Path $profile 'keep.txt'
'unrelated profile file' | Set-Content -LiteralPath $unrelated -Encoding utf8
$legacyProfile = [ordered]@{
    schemaVersion = 1
    options = @{ showDockOnStartup = $false }
    items = @(@{ id = [guid]::NewGuid().ToString(); name = 'Fixture'; kind = 'File'; target = $target })
}
$legacyProfile | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath $dataFile -Encoding utf8
$originalProfileHash = (Get-FileHash -LiteralPath $dataFile).Hash
$settingsShortcut = Join-Path $productGroup 'Desktop Dock Settings.lnk'
$version1 = (Get-Item -LiteralPath $base).VersionInfo.FileVersion
$version2 = (Get-Item -LiteralPath $update).VersionInfo.FileVersion
$version1 = ([version]$version1.Trim()).ToString(3)
$version2 = ([version]$version2.Trim()).ToString(3)
if ([version]$version1 -ge [version]$version2) { throw 'UpdateInstaller must have a newer version.' }
$installedV1 = Join-Path $destination "versions/$version1/DesktopDock.exe"
$installedV2 = Join-Path $destination "versions/$version2/DesktopDock.exe"
$wscript = New-Object -ComObject WScript.Shell

try {
    New-Item -ItemType Directory -Path $backups | Out-Null
    $originalAcl = Get-Acl -LiteralPath $backups
    $deniedAcl = Get-Acl -LiteralPath $backups
    $identity = [System.Security.Principal.WindowsIdentity]::GetCurrent().User
    $deny = [System.Security.AccessControl.FileSystemAccessRule]::new(
        $identity, [System.Security.AccessControl.FileSystemRights]::CreateDirectories,
        [System.Security.AccessControl.AccessControlType]::Deny)
    $deniedAcl.AddAccessRule($deny)
    Set-Acl -LiteralPath $backups -AclObject $deniedAcl
    try {
        Check ((Setup $base 'blocked-backup-permission') -ne 0 -and
            !(Test-Path -LiteralPath $installedV1) -and !(Test-Path -LiteralPath $uninstallKey) -and
            (Get-FileHash -LiteralPath $dataFile).Hash -eq $originalProfileHash) 'backup permission denial blocks setup before program files or profile changes'
    }
    finally { Set-Acl -LiteralPath $backups -AclObject $originalAcl }

    Check ((Setup $base 'clean-install') -eq 0) 'installer runs without elevation prompt into the selected isolated path'
    Check ((Read-Version) -eq $version1 -and (Test-Path -LiteralPath $installedV1 -PathType Leaf)) 'installed version and program files are registered'
    Check (!(Test-Path -LiteralPath $desktopLink) -and !(Test-Path -LiteralPath $startupKey)) 'desktop icon and login startup are opt-in'
    $startMenu = $wscript.CreateShortcut($settingsShortcut)
    Check ($startMenu.TargetPath -eq $installedV1 -and $startMenu.Arguments.Contains('--settings')) 'real Start menu shortcut points to the installed executable'
    Check ((Get-FileHash -LiteralPath $dataFile).Hash -eq $originalProfileHash -and
        @(Get-ChildItem -LiteralPath $backups -Filter 'backup.json' -Recurse).Count -eq 1) 'install backs up the old schema without rewriting it'

    $lease = [IO.File]::Open((Join-Path $profile 'dock.lock'), [IO.FileMode]::OpenOrCreate, [IO.FileAccess]::ReadWrite, [IO.FileShare]::None)
    try {
        Check ((Setup $update 'blocked-running-profile') -ne 0 -and (Read-Version) -eq $version1) 'upgrade stops when another process owns the profile lease'
    }
    finally { $lease.Dispose() }

    $badThemes = Join-Path $profile 'themes.json'
    '{"schemaVersion":999,"state":{}}' | Set-Content -LiteralPath $badThemes -Encoding utf8
    $badHash = (Get-FileHash -LiteralPath $badThemes).Hash
    try {
        Check ((Setup $update 'blocked-incompatible-theme') -ne 0 -and (Read-Version) -eq $version1 -and
            (Get-FileHash -LiteralPath $badThemes).Hash -eq $badHash) 'incompatible themes block upgrade and retain original bytes'
    }
    finally { Remove-Item -LiteralPath $badThemes }

    $previousBinaryHash = (Get-FileHash -LiteralPath $installedV1).Hash
    Interrupt-Update
    Check ((Read-Version) -eq $version1 -and (Get-FileHash -LiteralPath $installedV1).Hash -eq $previousBinaryHash -and
        (Get-FileHash -LiteralPath $dataFile).Hash -eq $originalProfileHash -and !(Test-Path -LiteralPath $installedV2)) 'interruption after first new-file write preserves the old executable and profile'

    Check ((Setup $update 'upgrade' @('/TASKS=desktopicon,startup')) -eq 0 -and (Read-Version) -eq $version2) 'upgrade installs a separately versioned program'
    Check ((Get-FileHash -LiteralPath $installedV1).Hash -eq $previousBinaryHash) 'successful upgrade retains prior executable for recovery'
    $startup = (Get-ItemProperty -LiteralPath $startupKey).'DesktopDock.Windows'
    Check ($startup -eq ('"' + $installedV2 + '" --startup')) 'opt-in login startup targets the new version in an isolated non-Run key'
    Check ((Test-Path -LiteralPath $desktopLink) -and $wscript.CreateShortcut($desktopLink).TargetPath -eq $installedV2) 'desktop task creates a real shortcut in the isolated junction-free directory'
    Check ((Setup $base 'blocked-downgrade') -ne 0 -and (Read-Version) -eq $version2) 'older installer stops before replacing the newer installation'

    $journal = Join-Path $profile 'desktop-icons.json'
    '{"version":999}' | Set-Content -LiteralPath $journal -Encoding utf8
    $journalHash = (Get-FileHash -LiteralPath $journal).Hash
    $installedHash = (Get-FileHash -LiteralPath $installedV2).Hash
    try {
        $blocked = Open-Package (Find-Uninstaller) 'blocked-uninstall-recovery' @('/REMOVEDATA=1')
        Check ($blocked -ne 0 -and (Read-Version) -eq $version2 -and
            (Get-FileHash -LiteralPath $installedV2).Hash -eq $installedHash -and
            (Get-FileHash -LiteralPath $dataFile).Hash -eq $originalProfileHash -and
            (Get-FileHash -LiteralPath $journal).Hash -eq $journalHash -and
            (Get-ItemProperty -LiteralPath $startupKey).'DesktopDock.Windows' -eq $startup) 'unresolved desktop recovery blocks data removal and retains installed program, profile and startup entry'
    }
    finally { Remove-Item -LiteralPath $journal }

    'replacement belongs to the user' | Set-Content -LiteralPath $desktopLink -Encoding utf8
    $replacementHash = (Get-FileHash -LiteralPath $desktopLink).Hash
    Check ((Open-Package (Find-Uninstaller) 'uninstall-retain') -eq 0 -and !(Test-Path -LiteralPath $uninstallKey) -and
        !(Test-Path -LiteralPath $installedV2) -and !(Test-Path -LiteralPath $installedV1)) 'uninstall removes installed versions and registration'
    Check (!(Test-Path -LiteralPath $startupKey) -and !(Test-Path -LiteralPath $settingsShortcut) -and
        (Get-FileHash -LiteralPath $desktopLink).Hash -eq $replacementHash -and
        (Get-FileHash -LiteralPath $dataFile).Hash -eq $originalProfileHash) 'default uninstall retains configuration and user-replaced shortcut'

    $destination = Join-Path $run 'Reinstall Files'
    $installedV2 = Join-Path $destination "versions/$version2/DesktopDock.exe"
    Check ((Setup $update 'reinstall' @('/TASKS=desktopicon')) -eq 0 -and
        (Get-FileHash -LiteralPath $desktopLink).Hash -eq $replacementHash) 'installer respects an existing unowned shortcut'
    Check ((Open-Package (Find-Uninstaller) 'uninstall-remove-profile' @('/REMOVEDATA=1')) -eq 0 -and
        !(Test-Path -LiteralPath $dataFile) -and (Test-Path -LiteralPath $unrelated) -and
        (Get-FileHash -LiteralPath $target).Hash -eq $originalTargetHash -and
        @(Get-ChildItem -LiteralPath $backups -Filter 'backup.json' -Recurse).Count -gt 0) 'explicit data cleanup preserves project targets, unrelated files and backups'
    Write-Evidence "END passed=$script:passed failed=0"
}
catch {
    Write-Evidence "FAIL $($_.Exception.Message)"
    throw
}
finally {
    if (Test-Path -LiteralPath $uninstallKey) {
        $uninstaller = Find-Uninstaller
        $exitCode = Open-Package $uninstaller 'fixture-cleanup'
        if ($exitCode -ne 0) { Write-Evidence "Validation cleanup returned $exitCode" }
    }
}
