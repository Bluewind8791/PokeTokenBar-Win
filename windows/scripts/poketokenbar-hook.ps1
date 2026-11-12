# PokeTokenBar agent hook for Claude Code on Windows (the macOS README's poketokenbar.sh).
# Usage: powershell -NoProfile -ExecutionPolicy Bypass -File poketokenbar.ps1 <working|waiting|done|idle>
# Labels the agent with the project folder name, percent-encoded, and opens
#   poketokenbar://agent?state=<state>&label=<folder>
# The hook never fails: any error is swallowed and it exits 0.
param([string]$State = 'working')

try {
    # Like pgrep on macOS: a hook must not start the app again after you quit it.
    if (-not (Get-Process -Name PokeTokenBar -ErrorAction SilentlyContinue)) { exit 0 }
    $folder = ''
    if ($env:CLAUDE_PROJECT_DIR) { $folder = Split-Path -Leaf $env:CLAUDE_PROJECT_DIR.TrimEnd('\', '/') }
    $url = 'poketokenbar://agent?state=' + [Uri]::EscapeDataString($State) + '&label=' + [Uri]::EscapeDataString($folder)
    # The protocol starts a short-lived PokeTokenBar.exe that hands the URL to the running app and exits.
    Start-Process $url
} catch { }
exit 0
