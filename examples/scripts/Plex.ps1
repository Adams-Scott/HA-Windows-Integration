# HA-Name: Plex Server
# HA-Check: process:Plex Media Server.exe
# HA-Icon: mdi:plex
param([Parameter(Position=0)][ValidateSet('On','Off')][string]$State)

if ($State -eq 'On') {
    Start-Process 'C:\Program Files\Plex\Plex Media Server\Plex Media Server.exe'
} else {
    Get-Process 'Plex Media Server' -ErrorAction SilentlyContinue | Stop-Process
}
