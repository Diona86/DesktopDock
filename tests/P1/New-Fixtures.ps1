[CmdletBinding()]
param([Parameter(Mandatory)][string]$Directory)

$ErrorActionPreference = 'Stop'
[Console]::OutputEncoding = [Text.UTF8Encoding]::new($false)
$root = [IO.Path]::GetFullPath($Directory)
if (Test-Path -LiteralPath $root) {
    throw 'Use a new directory for validation fixtures.'
}
New-Item -ItemType Directory -Path $root | Out-Null

$source = Join-Path $root 'LaunchReceipt.cs'
$app = Join-Path $root 'P1 Test App.exe'
$shortcut = Join-Path $root 'P1 Shortcut.lnk'
$folder = Join-Path $root '样例目录'
$file = Join-Path $root 'P1 Note.txt'
$icon = Join-Path $root 'P1 Custom.png'
New-Item -ItemType Directory -Path $folder | Out-Null
'Desktop Dock P1 validation document' | Set-Content -LiteralPath $file -Encoding utf8
@'
using System;
using System.IO;
using System.Text;

internal static class LaunchReceipt
{
    private static void Main(string[] args)
    {
        bool fromShortcut = args.Length == 1 && args[0] == "--via-shortcut";
        string receipt = Path.Combine(AppDomain.CurrentDomain.BaseDirectory,
            fromShortcut ? "shortcut-receipt.txt" : "exe-receipt.txt");
        File.WriteAllLines(receipt, new[] {
            "utc=" + DateTime.UtcNow.ToString("O"),
            "fromShortcut=" + fromShortcut,
            "workingDirectory=" + Environment.CurrentDirectory
        }, new UTF8Encoding(false));
    }
}
'@ | Set-Content -LiteralPath $source -Encoding utf8

$compiler = Join-Path $env:SystemRoot 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
& $compiler /nologo /target:winexe "/out:$app" $source
if ($LASTEXITCODE -ne 0) {
    throw 'Could not build the Windows launch receipt fixture.'
}

$shell = New-Object -ComObject WScript.Shell
$link = $shell.CreateShortcut($shortcut)
$link.TargetPath = $app
$link.Arguments = '--via-shortcut'
$link.WorkingDirectory = $folder
$link.IconLocation = "$env:SystemRoot\System32\shell32.dll,3"
$link.Save()
[void][Runtime.InteropServices.Marshal]::FinalReleaseComObject($link)
[void][Runtime.InteropServices.Marshal]::FinalReleaseComObject($shell)

Add-Type -AssemblyName System.Drawing.Common
$bitmap = [Drawing.Bitmap]::new(64, 64)
$graphics = [Drawing.Graphics]::FromImage($bitmap)
try {
    $graphics.Clear([Drawing.Color]::FromArgb(23, 120, 99))
    $graphics.FillEllipse([Drawing.Brushes]::White, 16, 16, 32, 32)
    $bitmap.Save($icon, [Drawing.Imaging.ImageFormat]::Png)
} finally {
    $graphics.Dispose()
    $bitmap.Dispose()
}

Get-FileHash -LiteralPath $app,$shortcut,$file,$icon -Algorithm SHA256 | ForEach-Object {
    "$(Split-Path -Leaf $_.Path) SHA256=$($_.Hash)"
} | Set-Content -LiteralPath (Join-Path $root 'initial-hashes.txt') -Encoding utf8
Write-Output $root
