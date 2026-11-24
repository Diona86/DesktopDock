[CmdletBinding()]
param([Parameter(Mandatory)][string]$Directory)

$ErrorActionPreference = 'Stop'
[Console]::OutputEncoding = [Text.UTF8Encoding]::new($false)
$root = (Resolve-Path -LiteralPath $Directory).Path
$portFile = Join-Path $root 'browser-port.txt'
$receipt = Join-Path $root 'browser-receipt.txt'
if ((Test-Path -LiteralPath $portFile) -or (Test-Path -LiteralPath $receipt)) {
    throw 'The browser probe requires unused receipt paths.'
}

$listener = [Net.Sockets.TcpListener]::new([Net.IPAddress]::Loopback, 0)
try {
    $listener.Start()
    $port = ([Net.IPEndPoint]$listener.LocalEndpoint).Port
    "http://127.0.0.1:$port/p1-dock" | Set-Content -LiteralPath $portFile -Encoding utf8
    Get-Content -LiteralPath $portFile -Encoding utf8
    $timer = [Diagnostics.Stopwatch]::StartNew()
    while ($timer.Elapsed.TotalMinutes -lt 10 -and -not (Test-Path -LiteralPath $receipt)) {
        if (-not $listener.Pending()) {
            Start-Sleep -Milliseconds 100
            continue
        }
        $client = $listener.AcceptTcpClient()
        try {
            $client.ReceiveTimeout = 3000
            $stream = $client.GetStream()
            $reader = [IO.StreamReader]::new($stream)
            $first = $reader.ReadLine()
            if ($first -notmatch '^GET /p1-dock HTTP/1\.[01]$') {
                continue
            }
            $agent = ''
            for ($count = 0; $count -lt 32; $count++) {
                $header = $reader.ReadLine()
                if ([string]::IsNullOrEmpty($header)) { break }
                if ($header -match '^User-Agent:\s*(.+)$') { $agent = $Matches[1] }
            }
            $body = 'Desktop Dock P1 browser probe received.'
            $response = "HTTP/1.1 200 OK`r`nContent-Type: text/plain; charset=utf-8`r`nContent-Length: $($body.Length)`r`nConnection: close`r`n`r`n$body"
            $stream.Write([Text.Encoding]::ASCII.GetBytes($response))
            @("utc=$([DateTimeOffset]::UtcNow.ToString('o'))", "request=$first", "userAgent=$agent", 'response=200') |
                Set-Content -LiteralPath $receipt -Encoding utf8
        } catch [IO.IOException] {
            continue
        } finally {
            $client.Dispose()
        }
    }
    if (-not (Test-Path -LiteralPath $receipt)) { throw 'No browser probe request was received.' }
} finally {
    $listener.Stop()
}
