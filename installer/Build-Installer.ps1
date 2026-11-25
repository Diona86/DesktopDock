[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$AppDirectory,
    [Parameter(Mandatory)][string]$OutputDirectory,
    [Parameter(Mandatory)][string]$IsccPath,
    [string]$DotnetPath = 'dotnet',
    [ValidatePattern('^[0-9]+\.[0-9]+\.[0-9]+$')][string]$Version = '1.0.0',
    [switch]$Validation
)

$ErrorActionPreference = 'Stop'
$OutputEncoding = [Text.UTF8Encoding]::new($false)
[Console]::OutputEncoding = [Text.UTF8Encoding]::new($false)
$app = (Resolve-Path -LiteralPath $AppDirectory).Path
$iscc = (Resolve-Path -LiteralPath $IsccPath).Path
$output = $ExecutionContext.SessionState.Path.GetUnresolvedProviderPathFromPSPath($OutputDirectory)
$appExe = Join-Path $app 'DesktopDock.exe'
if (!(Test-Path -LiteralPath $appExe -PathType Leaf) -or
    !(Test-Path -LiteralPath (Join-Path $app 'coreclr.dll') -PathType Leaf)) {
    throw 'AppDirectory must contain the self-contained win-x64 publish.'
}
if (!$Validation -and ([version](Get-Item -LiteralPath $appExe).VersionInfo.FileVersion).ToString(3) -ne $Version) {
    throw 'The published executable version does not match -Version.'
}
if ($output.StartsWith($app.TrimEnd('\') + '\', [StringComparison]::OrdinalIgnoreCase) -or
    $app.Equals($output, [StringComparison]::OrdinalIgnoreCase)) {
    throw 'Use an output directory outside of the app payload.'
}
New-Item -ItemType Directory -Path $output -Force | Out-Null
$setupSuffix = if ($Validation) { '-validation' } else { '' }
$helper = Join-Path $output ".setup-helper$setupSuffix-$Version"
$helperProject = Join-Path $PSScriptRoot 'SetupHelper/SetupHelper.csproj'
$publishArguments = @(
    'publish', $helperProject, '-c', 'Release', '-r', 'win-x64', '--self-contained', 'true',
    '-p:PublishSingleFile=true', '-p:IncludeNativeLibrariesForSelfExtract=true',
    '-p:EnableCompressionInSingleFile=true', '-p:DebugType=None', '-o', $helper
)
& $DotnetPath @publishArguments
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
$helperExe = Join-Path $helper 'DesktopDock.SetupHelper.exe'
if (!(Test-Path -LiteralPath $helperExe -PathType Leaf)) { throw 'The standalone setup helper is missing.' }

$name = "DesktopDock-$Version-win-x64-setup$setupSuffix"
$arguments = @("/DReleaseVersion=$Version", "/DAppPayload=$app", "/DSetupHelper=$helperExe", "/O$output", "/F$name")
if ($Validation) { $arguments += '/DValidation=1' }
& $iscc @arguments (Join-Path $PSScriptRoot 'DesktopDock.iss')
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
Get-Item -LiteralPath (Join-Path $output "$name.exe") | Select-Object FullName, Length
