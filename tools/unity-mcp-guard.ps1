<#
Singleton launcher for the Unity AI relay MCP server (unity-mcp in .mcp.json).

Why: the Unity Editor's relay (relay_win.exe --relay, started by com.unity.ai.assistant)
only tolerates ONE MCP client session at a time. Each Claude Code session spawns its own
"relay_win.exe --mcp" client; a second one makes the relay drop both connections, which
shows up as unity-mcp "randomly disconnecting". First session wins; later sessions get a
clear error here instead of silently killing the first one.

Note: "--mcp(\s|$)" deliberately does NOT match the editor's own relay process, whose
command line contains "--mcp-client-port" but runs in --relay mode.
#>
$ErrorActionPreference = 'Stop'
$relay = Join-Path $env:USERPROFILE '.unity\relay\relay_win.exe'

if (-not (Test-Path $relay)) {
    [Console]::Error.WriteLine("unity-mcp guard: relay binary not found at $relay")
    exit 1
}

function Get-McpRelayProcesses {
    @(Get-CimInstance Win32_Process -Filter "Name='relay_win.exe'" |
        Where-Object { $_.CommandLine -match '--mcp(\s|$)' })
}

# Reap orphans (their owning session is gone) and ride out the brief window where
# this same session is restarting its MCP server and the old process hasn't exited yet.
$deadline = (Get-Date).AddSeconds(10)
while ($true) {
    $live = @()
    foreach ($p in Get-McpRelayProcesses) {
        $parentAlive = $false
        try { Get-Process -Id $p.ParentProcessId -ErrorAction Stop | Out-Null; $parentAlive = $true } catch {}
        if ($parentAlive) { $live += $p }
        else { try { Stop-Process -Id $p.ProcessId -Force -Confirm:$false -ErrorAction Stop } catch {} }
    }
    if ($live.Count -eq 0) { break }
    if ((Get-Date) -gt $deadline) {
        $pids = ($live | ForEach-Object { $_.ProcessId }) -join ', '
        [Console]::Error.WriteLine("unity-mcp guard: another Claude Code session already holds the Unity MCP relay (relay_win.exe PID $pids). Unity allows only one MCP session at a time - do Unity work from that session, or close it and run /mcp reconnect here.")
        exit 1
    }
    Start-Sleep -Milliseconds 500
}

& $relay --mcp
exit $LASTEXITCODE