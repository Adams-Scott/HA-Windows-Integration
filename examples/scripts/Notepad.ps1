<#
  A minimal example to test the setup.
  HA-Name: Notepad
  HA-Check: process:notepad.exe
  HA-Icon: mdi:note-edit
#>
param([Parameter(Position=0)][ValidateSet('On','Off')][string]$State)

if ($State -eq 'On') {
    Start-Process notepad.exe
} else {
    Get-Process notepad -ErrorAction SilentlyContinue | Stop-Process
}
