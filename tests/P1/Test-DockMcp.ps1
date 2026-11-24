[CmdletBinding()]
param(
    [string]$Executable = (Join-Path $PSScriptRoot '../../artifacts/DesktopDock-P1-win-x64/DesktopDock.exe'),
    [string]$RunDirectory = (Join-Path $PSScriptRoot "../../artifacts/p1-mcp-$([guid]::NewGuid().ToString('N'))"),
    [ValidateRange(5, 180)][int]$RequestTimeoutSeconds = 45,
    [switch]$SkipBrowser,
    [switch]$SkipShellAssociations
)

$ErrorActionPreference = 'Stop'
[Console]::OutputEncoding = [Text.UTF8Encoding]::new($false)
$exe = (Resolve-Path -LiteralPath $Executable).Path
$run = [IO.Path]::GetFullPath($RunDirectory)
if (Test-Path -LiteralPath $run) { throw 'The validation run directory must be new.' }
New-Item -ItemType Directory -Path $run | Out-Null
$log = Join-Path $run 'mcp-results.log'
$profileDir = Join-Path $run 'profile'
$script:client = $null
$script:failed = 0
$browser = $null

function Write-Evidence([string]$Message) {
    $line = "$([DateTimeOffset]::UtcNow.ToString('o')) $Message"
    Add-Content -LiteralPath $log -Value $line -Encoding utf8
    Write-Host $line
}

function Assert-Check([bool]$Condition, [string]$Message) {
    if (-not $Condition) { throw $Message }
}

function Test-Check([string]$Name, [scriptblock]$Steps) {
    try {
        & $Steps
        Write-Evidence "PASS $Name"
    } catch {
        if ($script:client.Faulted) { throw }
        $script:failed++
        Write-Evidence "FAIL $Name $($_.Exception.Message)"
    }
}

function Send-Rpc([string]$Method, [hashtable]$Parameters = @{}, [switch]$Notification) {
    $message = @{ jsonrpc = '2.0'; method = $Method; params = $Parameters }
    $label = if ($Method -eq 'tools/call') { [string]$Parameters.name } else { $Method }
    if (-not $Notification) {
        $message.id = ++$script:client.Sequence
        if ($label -ne 'dock_state') { Write-Evidence "MCP request $label id=$($message.id)" }
    }
    $script:client.Process.StandardInput.WriteLine(($message | ConvertTo-Json -Depth 24 -Compress))
    $script:client.Process.StandardInput.Flush()
    if ($Notification) { return }

    $timer = [Diagnostics.Stopwatch]::StartNew()
    $pending = $script:client.Process.StandardOutput.ReadLineAsync()
    if (-not $pending.Wait([TimeSpan]::FromSeconds($RequestTimeoutSeconds))) {
        $script:client.Faulted = $true
        $process = $script:client.Process
        $process.Refresh()
        if (-not $process.HasExited) {
            Write-Evidence "MCP timeout diagnostics pid=$($process.Id) cpuMs=$($process.TotalProcessorTime.TotalMilliseconds) workingSetBytes=$($process.WorkingSet64)"
        }
        throw "MCP timeout for $label id=$($message.id)"
    }
    $line = $pending.GetAwaiter().GetResult()
    if ([string]::IsNullOrWhiteSpace($line)) {
        $script:client.Faulted = $true
        throw "MCP ended before responding to $label id=$($message.id)"
    }
    $response = ConvertFrom-Json -InputObject $line -Depth 64
    if ($response.jsonrpc -ne '2.0' -or $response.id -ne $message.id) {
        $script:client.Faulted = $true
        throw "Invalid MCP response for $label id=$($message.id)"
    }
    if ($label -ne 'dock_state' -or $timer.ElapsedMilliseconds -gt 1000) {
        Write-Evidence "MCP response $label id=$($message.id) ms=$($timer.ElapsedMilliseconds) bytes=$([Text.Encoding]::UTF8.GetByteCount($line))"
    }
    if ($response.error) { throw "MCP $Method error $($response.error.code) $($response.error.message)" }
    return $response.result
}

function Invoke-DockTool([string]$Name, [hashtable]$Arguments = @{}) {
    $result = Send-Rpc 'tools/call' @{ name = $Name; arguments = $Arguments }
    if ($result.isError) {
        $diagnostic = @($result.content | Where-Object type -eq 'text' | ForEach-Object text) -join ' '
        throw "MCP tool $Name failed $diagnostic"
    }
    return $result
}

function Get-DockState {
    $state = (Invoke-DockTool 'dock_state').structuredContent
    Assert-Check ($null -ne $state.options -and $null -ne $state.view) 'dock_state must expose structured options and view.'
    return $state
}

function Start-DockClient {
    $info = [Diagnostics.ProcessStartInfo]::new($exe)
    $info.UseShellExecute = $false
    $info.CreateNoWindow = $true
    $info.RedirectStandardInput = $true
    $info.RedirectStandardOutput = $true
    $info.RedirectStandardError = $true
    $info.StandardInputEncoding = [Text.UTF8Encoding]::new($false)
    $info.StandardOutputEncoding = [Text.Encoding]::UTF8
    $info.StandardErrorEncoding = [Text.Encoding]::UTF8
    foreach ($argument in @('--mcp-stdio', '--config-dir', $profileDir)) { $info.ArgumentList.Add($argument) }
    $process = [Diagnostics.Process]::Start($info)
    $script:client = @{ Process = $process; Sequence = 0; Stderr = $process.StandardError.ReadToEndAsync(); Faulted = $false }
    $initialized = Send-Rpc 'initialize' @{
        protocolVersion = '2025-11-25'; capabilities = @{}; clientInfo = @{ name = 'Desktop Dock P1'; version = '1.0' }
    }
    Assert-Check ($initialized.protocolVersion -in @('2025-06-18', '2025-11-25') -and $null -ne $initialized.serverInfo -and $null -ne $initialized.capabilities.tools) 'MCP initialization did not negotiate a supported tool server.'
    Send-Rpc 'notifications/initialized' -Notification
    $tools = (Send-Rpc 'tools/list').tools
    $required = @('dock_state', 'dock_add', 'dock_edit', 'dock_remove', 'dock_open', 'dock_show_in_folder',
        'dock_options', 'dock_visibility', 'dock_settings', 'dock_drag_item', 'dock_drag_handle', 'dock_tooltip', 'dock_capture')
    foreach ($name in $required) { Assert-Check ($name -in $tools.name) "Missing advertised MCP tool $name" }
    Write-Evidence "MCP initialized protocol=$($initialized.protocolVersion) pid=$($process.Id) tools=$($tools.Count)"
}

function Stop-DockClient {
    if ($null -eq $script:client) { return }
    $process = $script:client.Process
    try {
        $process.StandardInput.Close()
        if (-not $process.WaitForExit(8000)) {
            $process.Kill()
            [void]$process.WaitForExit(3000)
            throw 'Dock did not exit within eight seconds of stdin EOF.'
        }
        $diagnostic = $script:client.Stderr.GetAwaiter().GetResult().Trim()
        if ($diagnostic) { Write-Evidence "MCP stderr $diagnostic" }
        Write-Evidence "MCP stdin EOF processExitCode=$($process.ExitCode)"
        Assert-Check ($process.ExitCode -eq 0) 'Dock returned a nonzero exit code after stdin EOF.'
    } finally {
        $process.Dispose()
        $script:client = $null
    }
}

function Wait-Receipt([string]$Path, [string]$Pattern, [int]$Seconds = 15) {
    $timer = [Diagnostics.Stopwatch]::StartNew()
    while ($timer.Elapsed.TotalSeconds -lt $Seconds) {
        if (Test-Path -LiteralPath $Path -PathType Leaf) {
            try {
                $text = ([string](Get-Content -LiteralPath $Path -Encoding utf8 -Raw)).Trim()
                if ($text -match $Pattern) { return $text }
            } catch [IO.IOException] {
                # The fixture may still be writing its receipt.
            }
        }
        Start-Sleep -Milliseconds 100
    }
    throw "Timed out waiting for $(Split-Path -Leaf $Path)"
}

function Save-Capture([string]$Window, [string]$Name) {
    $images = @((Invoke-DockTool 'dock_capture' @{ window = $Window }).content | Where-Object type -eq 'image')
    Assert-Check ($images.Count -eq 1 -and $images[0].mimeType -eq 'image/png') "No PNG returned for $Window"
    $bytes = [Convert]::FromBase64String($images[0].data)
    Assert-Check ($bytes.Length -gt 32 -and [Convert]::ToHexString($bytes, 0, 8) -eq '89504E470D0A1A0A') "Invalid PNG returned for $Window"
    [IO.File]::WriteAllBytes((Join-Path $run $Name), $bytes)
    Write-Evidence "MCP screenshot $Name bytes=$($bytes.Length)"
}

function Get-Order($State) { return ($State.items.id -join ',') }

Write-Evidence "start exe=$exe run=$run"
try {
    $fixtures = & (Join-Path $PSScriptRoot 'New-Fixtures.ps1') -Directory (Join-Path $run 'fixtures')
    $app = Join-Path $fixtures 'P1 Test App.exe'
    $shortcut = Join-Path $fixtures 'P1 Shortcut.lnk'
    $file = Join-Path $fixtures 'P1 Note.txt'
    $folder = Join-Path $fixtures '样例目录'
    $icon = Join-Path $fixtures 'P1 Custom.png'

    $url = 'http://127.0.0.1:1/p1-dock'
    if (-not $SkipBrowser) {
        $info = [Diagnostics.ProcessStartInfo]::new((Join-Path $PSHOME 'pwsh.exe'))
        $info.UseShellExecute = $false
        $info.CreateNoWindow = $true
        foreach ($argument in @('-NoProfile', '-File', (Join-Path $PSScriptRoot 'Receive-BrowserProbe.ps1'), '-Directory', $fixtures)) {
            $info.ArgumentList.Add($argument)
        }
        $browser = [Diagnostics.Process]::Start($info)
        try { $url = Wait-Receipt (Join-Path $fixtures 'browser-port.txt') '^http://127\.0\.0\.1:\d+/p1-dock$' 10 }
        catch { $script:failed++; Write-Evidence "FAIL P1-04 loopback listener $($_.Exception.Message)" }
    } else {
        Write-Evidence 'SKIP P1-04 actual browser navigation requested by caller'
    }

    Start-DockClient
    $initial = Get-DockState
    Assert-Check (@($initial.items).Count -eq 0) 'The test profile began with existing items.'
    Assert-Check ($initial.view.visible -and $initial.view.transparent -and $initial.view.windowStyle -eq 'None' -and -not $initial.view.activatesOnShow) 'The initial Dock was not a visible transparent, borderless, nonactivating-show window.'
    [void](Invoke-DockTool 'dock_add' @{ targets = @($app); input = 'picker' })
    [void](Invoke-DockTool 'dock_add' @{ targets = @($shortcut, $file, $folder); input = 'drop' })
    [void](Invoke-DockTool 'dock_add' @{ targets = @($url, 'https://example.test/'); input = 'picker' })
    $state = Get-DockState
    Assert-Check ((($state.items.kind) -join ',') -eq 'Application,Shortcut,File,Folder,Url,Url') 'Add did not preserve the six fixture types and order.'
    $appItem, $linkItem, $fileItem, $folderItem, $httpItem, $httpsItem = $state.items
    Write-Evidence 'MCP added EXE with picker handler. All filesystem targets were explicitly provided.'

    Test-Check 'P1-01 EXE launch receipt' {
        [void](Invoke-DockTool 'dock_open' @{ id = $appItem.id })
        $receipt = Wait-Receipt (Join-Path $fixtures 'exe-receipt.txt') 'fromShortcut=False'
        Write-Evidence "EXE receipt $($receipt -replace '\s*\r?\n\s*', '; ')"
    }

    Test-Check 'P1-02 LNK launch arguments and working directory' {
        [void](Invoke-DockTool 'dock_open' @{ id = $linkItem.id })
        $receipt = Wait-Receipt (Join-Path $fixtures 'shortcut-receipt.txt') 'fromShortcut=True'
        Assert-Check ($receipt -match "(?m)^workingDirectory=$([regex]::Escape($folder))\r?$") 'LNK working directory was not preserved.'
        Write-Evidence "LNK receipt $($receipt -replace '\s*\r?\n\s*', '; ')"
    }

    Test-Check 'P1-03 ordinary file and folder added through drop handler' {
        Assert-Check ($fileItem.target -eq $file -and $folderItem.target -eq $folder) 'File or folder target differs from the fixture path.'
        if ($SkipShellAssociations) {
            Write-Evidence 'SKIP P1-03 Shell association requests requested by caller'
        } else {
            foreach ($item in @($fileItem, $folderItem)) { [void](Invoke-DockTool 'dock_open' @{ id = $item.id }) }
            [void](Invoke-DockTool 'dock_show_in_folder' @{ id = $fileItem.id })
            Write-Evidence 'File open, folder open and show-in-folder Shell requests returned successfully; external window contents were not inspected.'
        }
    }

    Test-Check 'P1-04 URL validation' {
        Assert-Check ($httpItem.target -eq $url -and $httpsItem.target -eq 'https://example.test/') 'HTTP and HTTPS targets were not stored.'
    }
    if (-not $SkipBrowser -and $url -ne 'http://127.0.0.1:1/p1-dock') {
        Test-Check 'P1-04 system default browser loopback HTTP receipt' {
            [void](Invoke-DockTool 'dock_open' @{ id = $httpItem.id })
            $receipt = Wait-Receipt (Join-Path $fixtures 'browser-receipt.txt') '(?m)^response=200\r?$'
            Assert-Check ($receipt -match '(?m)^userAgent=\S+') 'Loopback request had no browser user agent.'
            Write-Evidence "Browser receipt $($receipt -replace '\s*\r?\n\s*', '; ')"
        }
    }

    Test-Check 'P1-05 custom icon and actual Dock capture' {
        [void](Invoke-DockTool 'dock_edit' @{ id = $appItem.id; target = $app; name = 'P1 自定义图标'; iconPath = $icon })
        $edited = (Get-DockState).items | Where-Object id -eq $appItem.id
        Assert-Check ($edited.customIconPath -eq $icon -and $edited.name -eq 'P1 自定义图标') 'Edited name or custom icon path was not retained.'
        $timer = [Diagnostics.Stopwatch]::StartNew()
        do {
            $visual = (Get-DockState).view.items
            if (@($visual | Where-Object { -not $_.imageLoaded }).Count -eq 0) { break }
            Start-Sleep -Milliseconds 120
        } while ($timer.Elapsed.TotalSeconds -lt 8)
        Assert-Check ($visual.Count -eq 6 -and @($visual | Where-Object { -not $_.imageLoaded }).Count -eq 0) 'One or more rendered items have no image source.'
        $filesystemVisuals = @($visual | Where-Object id -in @($appItem.id, $linkItem.id, $fileItem.id, $folderItem.id))
        Assert-Check ($filesystemVisuals.Count -eq 4 -and @($filesystemVisuals | Where-Object { $_.imagePixelWidth -gt 0 -and $_.imagePixelHeight -gt 0 }).Count -eq 4) 'A filesystem item was missing its bitmap.'
        Write-Evidence "Loaded Dock visuals $(@($visual | ForEach-Object { "$($_.name)=$($_.imagePixelWidth)x$($_.imagePixelHeight)" }) -join ', ')"
        Save-Capture 'dock' 'dock-visible.png'
    }

    Test-Check 'P1-06 WPF Tooltip through MCP open and close' {
        $result = (Invoke-DockTool 'dock_tooltip' @{ id = $appItem.id; open = $true }).structuredContent
        $observed = $result | ConvertTo-Json -Depth 8 -Compress
        Assert-Check ($result.content -eq 'P1 自定义图标' -and $result.isOpen -and $result.popupAttached) 'The WPF tooltip did not open an attached popup with the edited name.'
        Write-Evidence "Tooltip open state $observed"
        $closed = (Invoke-DockTool 'dock_tooltip' @{ id = $appItem.id; open = $false }).structuredContent
        Assert-Check (-not $closed.isOpen) 'The WPF tooltip did not close.'
        Write-Evidence 'Tooltip was controlled through MCP. Physical pointer hover was not used.'
    }

    Test-Check 'P1-07 item reorder via Dock movement path' {
        $order = Get-Order (Get-DockState)
        $drop = (Invoke-DockTool 'dock_drag_item' @{ id = $appItem.id; targetId = $fileItem.id; after = $true }).structuredContent
        $reordered = Get-DockState
        Assert-Check ((Get-Order $reordered) -ne $order -and $reordered.items[2].id -eq $appItem.id) 'Order did not change to the requested insertion.'
        Assert-Check ($drop.feedback -and $drop.committed -and $drop.orderChanged -and -not $drop.windowMoved) 'In-app drag did not show feedback, commit order, and keep window bounds.'
        Write-Evidence "Reorder feedback=$($drop.feedback) windowMoved=$($drop.windowMoved) kinds=$($reordered.items.kind -join ',')"
    }

    Test-Check 'P1-08 four edges, floating movement and independent locks' {
        foreach ($edge in @('Bottom', 'Top', 'Left', 'Right')) {
            [void](Invoke-DockTool 'dock_options' @{ placement = $edge })
            $placement = Get-DockState
            Assert-Check ($placement.options.placement -eq $edge -and $placement.view.placement -eq $edge -and $placement.view.vertical -eq ($edge -in @('Left', 'Right'))) "Dock placement or orientation $edge was rejected."
            Write-Evidence "Placement $edge vertical=$($placement.view.vertical) bounds=$($placement.view.bounds | ConvertTo-Json -Compress)"
        }
        [void](Invoke-DockTool 'dock_options' @{ placement = 'Floating'; floatingLeft = 180; floatingTop = 200; floatingVertical = $true; iconSize = 48; dockThickness = 76; dockLength = 400 })
        $before = Get-DockState
        $movement = (Invoke-DockTool 'dock_drag_handle' @{ dx = 48; dy = 32 }).structuredContent
        $moved = Get-DockState
        Assert-Check ($moved.options.floatingLeft -ne $before.options.floatingLeft -and $moved.options.floatingTop -ne $before.options.floatingTop -and (Get-Order $before) -eq (Get-Order $moved)) 'Handle movement did not preserve item order or change both coordinates.'
        Assert-Check (-not $movement.gripVisibleBefore -and $movement.gripVisibleOnBackground -and $movement.moved -and $movement.committed -and -not $movement.orderChanged) 'The handle did not reveal on the background and move only the window.'
        foreach ($lockName in @('globalPositionLocked', 'dockPositionLocked')) {
            [void](Invoke-DockTool 'dock_options' @{$lockName = $true })
            $blocked = (Invoke-DockTool 'dock_drag_handle' @{ dx = 30; dy = 18 }).structuredContent
            $locked = (Get-DockState).options
            Assert-Check ($locked.($lockName) -and $blocked.locked -and -not $blocked.gripVisibleOnBackground -and -not $blocked.moved -and $locked.floatingLeft -eq $moved.options.floatingLeft -and $locked.floatingTop -eq $moved.options.floatingTop) "Lock $lockName allowed a position change."
            Write-Evidence "Locked handle $lockName moved=$($blocked.moved) gripVisible=$($blocked.gripVisibleOnBackground)"
            [void](Invoke-DockTool 'dock_options' @{$lockName = $false })
        }
        Write-Evidence "Movement coordinates $($before.options.floatingLeft),$($before.options.floatingTop) -> $($moved.options.floatingLeft),$($moved.options.floatingTop)"
    }

    Test-Check 'P1-11 settings lifecycle and visibility' {
        [void](Invoke-DockTool 'dock_settings' @{ visible = $true; page = 'appearance' })
        Assert-Check ((Get-DockState).settingsVisible -eq $true) 'Settings did not open.'
        Save-Capture 'settings' 'settings-appearance.png'
        foreach ($page in @('items', 'position', 'general')) {
            [void](Invoke-DockTool 'dock_settings' @{ visible = $true; page = $page })
            Save-Capture 'settings' "settings-$page.png"
        }
        [void](Invoke-DockTool 'dock_settings' @{ visible = $false })
        Assert-Check ((Get-DockState).settingsVisible -eq $false) 'Settings did not close while Dock was available.'
        [void](Invoke-DockTool 'dock_visibility' @{ visible = $false })
        Assert-Check ((Get-DockState).view.visible -eq $false) 'Dock did not hide.'
        [void](Invoke-DockTool 'dock_visibility' @{ visible = $true })
        Assert-Check ((Get-DockState).view.visible -eq $true) 'Dock did not show.'
    }

    Test-Check 'P1-11 invalid path and non-HTTP scheme are rejected' {
        $order = Get-Order (Get-DockState)
        foreach ($invalid in @((Join-Path $fixtures 'missing.exe'), 'ftp://example.test/')) {
            $rejected = $false
            try { [void](Invoke-DockTool 'dock_add' @{ targets = @($invalid); input = 'picker' }) }
            catch { $rejected = $_.Exception.Message -like 'MCP tool dock_add failed*' }
            Assert-Check $rejected 'The invalid target did not produce a Dock validation error.'
        }
        Assert-Check ((Get-Order (Get-DockState)) -eq $order) 'A rejected target changed the Dock configuration.'
    }

    $beforeRestart = Get-DockState
    Stop-DockClient
    Start-DockClient
    Test-Check 'P1-09 clean restart preserves options, items, order and custom icon' {
        $after = Get-DockState
        Assert-Check ((Get-Order $after) -eq (Get-Order $beforeRestart)) 'Item IDs or item order changed on restart.'
        Assert-Check (($after.items | ConvertTo-Json -Depth 12 -Compress) -eq ($beforeRestart.items | ConvertTo-Json -Depth 12 -Compress)) 'Item settings changed on restart.'
        Assert-Check (($after.options | ConvertTo-Json -Depth 12 -Compress) -eq ($beforeRestart.options | ConvertTo-Json -Depth 12 -Compress)) 'Dock options changed on restart.'
        $stored = Get-Content -LiteralPath (Join-Path $profileDir 'dock.json') -Encoding utf8 -Raw | ConvertFrom-Json
        Assert-Check ($stored.schemaVersion -eq 1) 'Persisted JSON schemaVersion did not equal 1.'
    }

    Test-Check 'P1-10 remove reference and retain original hashes' {
        [void](Invoke-DockTool 'dock_remove' @{ id = $appItem.id })
        Assert-Check ($appItem.id -notin (Get-DockState).items.id) 'Removed reference remains in Dock state.'
        $initialHashes = Get-Content -LiteralPath (Join-Path $fixtures 'initial-hashes.txt') -Encoding utf8
        $endingHashes = Get-FileHash -LiteralPath $app,$shortcut,$file,$icon -Algorithm SHA256 | ForEach-Object {
            "$(Split-Path -Leaf $_.Path) SHA256=$($_.Hash)"
        }
        Assert-Check ((-not (Compare-Object $initialHashes $endingHashes)) -and (Test-Path -LiteralPath $folder -PathType Container)) 'Fixture hashes or directory changed.'
        Write-Evidence 'EXE, LNK, TXT and PNG matched their original SHA-256. Test folder still exists.'
    }
} catch {
    $script:failed++
    Write-Evidence "FAIL integration $($_.Exception.Message)"
} finally {
    try { Stop-DockClient } catch { $script:failed++; Write-Evidence "FAIL MCP shutdown $($_.Exception.Message)" }
    if ($null -ne $browser) {
        if (-not $browser.HasExited) {
            $browser.Kill()
            [void]$browser.WaitForExit(3000)
        }
        $browser.Dispose()
    }
    Write-Evidence "end failed=$script:failed log=$log"
}
exit [int]($script:failed -ne 0)
