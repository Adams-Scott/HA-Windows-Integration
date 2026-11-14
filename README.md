# PC Control for Home Assistant

Start and stop programs and Steam games on a Windows PC from Home Assistant. If the PC is off, it is woken with Wake-on-LAN first. You can also shut it down.

The project has two parts:

| Part | What it is |
| --- | --- |
| **PC Control Agent** (`agent/`) | A .NET 10 tray app for Windows. It scans a folder of PowerShell scripts and your Steam library, and serves them through a token-protected REST API on your LAN. |
| **PC Control integration** (`custom_components/pc_control/`) | A Home Assistant custom integration, installed through HACS. It shows each script and game as a switch, adds a Power switch (wake, shut down, online status), an Agent sensor and a Rescan button. |

## 1. Set up the PC

### Install the agent

Download `PcControl.Agent.exe` from the [latest release](https://github.com/Adams-Scott/HA-Windows-Integration/releases), or build it yourself with the [.NET 10 SDK](https://dotnet.microsoft.com/download):

```powershell
dotnet publish agent/PcControl.Agent -c Release -r win-x64 --self-contained -p:PublishSingleFile=true -o publish
```

Copy `PcControl.Agent.exe` to `%LOCALAPPDATA%\PcControl\` and run it once. On first run it:

- creates `%APPDATA%\PcControl\config.json` with a random API token,
- creates the scripts folder `Documents\PcControl\Scripts`,
- registers itself to start with Windows (`HKCU\...\Run`; toggle it from the tray menu).

The agent runs as your normal user and does not need admin rights. Only one copy runs at a time.

### Allow the agent through the firewall (the only admin step)

From an **elevated** PowerShell:

```powershell
New-NetFirewallRule -DisplayName 'PC Control Agent' -Direction Inbound -Protocol TCP -LocalPort 8765 -Action Allow -Profile Private
```

The network profile must be **Private** (Settings → Network & internet → Ethernet → Network profile type).

### Add scripts

Put `.ps1` files in `Documents\PcControl\Scripts`, then choose **Rescan** from the tray menu. Each script is called with one argument, `On` or `Off`:

```powershell
# HA-Name: Plex Server
# HA-Check: process:Plex Media Server.exe
# HA-Icon: mdi:plex
param([Parameter(Position=0)][ValidateSet('On','Off')][string]$State)

if ($State -eq 'On') {
    Start-Process 'C:\Program Files\Plex\Plex Media Server\Plex Media Server.exe'
} else {
    Get-Process 'Plex Media Server' -ErrorAction SilentlyContinue | Stop-Process
}
```

The header keys are read from the first 30 lines, as `# HA-Key: value` lines or inside a `<# ... #>` block:

| Key | Meaning | Default |
| --- | --- | --- |
| `HA-Name` | Display name | File name |
| `HA-Icon` | [MDI icon](https://pictogrammers.com/library/mdi/) | `mdi:application` |
| `HA-Check` | How to tell whether it is on (below) | None: an assumed-state switch with separate on and off buttons |
| `HA-Disabled: true` | Skip this script | |

`HA-Check` forms:

| Check | On when |
| --- | --- |
| `process:name.exe[,other.exe]` | A process with any of those names runs in your session |
| `service:Name` | The Windows service (name or display name) is running |
| `port:32400` | Something listens on that local TCP port |
| `window:text` | A visible window's title contains the text |
| `ps:<expression>` | The PowerShell expression returns `$true` (5 s timeout) |

The entity ID is `script.<file name>`, lowercased, with other characters replaced by `_`. Renaming a file changes its ID. See [`examples/scripts`](examples/scripts) for more.

Scripts may start long-running programs: the agent waits up to `scriptTimeoutSeconds` (60) for PowerShell to exit, then stops waiting without killing anything and relies on the check.

### Steam games

Every installed Steam game becomes a switch (`steam.<appid>`). Turning it on opens `steam://rungameid/<appid>` (this starts Steam if needed). Turning it off kills every process running from the game's install folder. If something refuses to close (for example an elevated launcher or anti-cheat), the agent reports `partial` with the processes that survived and logs them.

Redistributables, runtimes, SDKs, dedicated servers and soundtracks are skipped. Adjust this in `config.json`.

### Tray menu

Version · API address · **Rescan** · **Open scripts folder** · **Copy API token** · **Open settings** · **Open log folder** · **Start with Windows** · **Exit**

The icon shows a red badge if the API could not bind to its port. Logs are in `%APPDATA%\PcControl\logs` (kept for 7 days).

### Settings (`config.json`)

**Open settings** opens the file. The agent reloads it when you save it: a port change restarts the API, and a folder or Steam change triggers a rescan.

```json
{
  "port": 8765,
  "bindAddress": "0.0.0.0",
  "apiToken": "<generated>",
  "scriptsFolder": "%USERPROFILE%\\Documents\\PcControl\\Scripts",
  "scriptTimeoutSeconds": 60,
  "stateIntervalSeconds": 5,
  "steam": {
    "enabled": true,
    "excludeAppIds": [],
    "excludeNamePatterns": ["Redistributable", "Runtime", "SDK", "Dedicated Server", "Soundtrack"]
  },
  "shutdownDelaySeconds": 5
}
```

`excludeNamePatterns` are case-insensitive substrings of the game name.

## 2. Set up Home Assistant

### Install with HACS

[![Open your Home Assistant instance and open this repository in HACS.](https://my.home-assistant.io/badges/hacs_repository.svg)](https://my.home-assistant.io/redirect/hacs_repository/?owner=Adams-Scott&repository=HA-Windows-Integration&category=integration)

Or by hand:

1. In HACS, open the ⋮ menu → **Custom repositories**.
2. Add `https://github.com/Adams-Scott/HA-Windows-Integration` with type **Integration**.
3. Find **PC Control** in HACS, download it, and restart Home Assistant.

### Add the PC

1. Go to **Settings → Devices & services → Add integration → PC Control**.
2. Enter the PC's IP address or hostname, the port (8765) and the API token (tray → **Copy API token**).
3. Pick the MAC address for Wake-on-LAN (the wired adapter is listed first) and keep the defaults for broadcast address and port unless you need different ones.

Options (⋮ → **Configure**): poll interval (10 s), wake timeout (180 s), MAC, broadcast address and WOL port. Use **Reconfigure** if the PC's IP address changes.

### What you get

All entities belong to one device named after the PC:

| Entity | Behaviour |
| --- | --- |
| `<PC> Power` (switch) | On while the PC answers ping (checked every 10 s) or the agent answers. Turning it on sends Wake-on-LAN; turning it off shuts the PC down after 5 s. After either, it shows the new state while the PC boots (up to 3 min) or shuts down (up to 2 min) |
| `<PC> Agent` (diagnostic) | On when the agent API answers |
| One switch per script and game | On/off runs the script or starts/stops the game |
| `<PC> Rescan` | Rescans scripts and games |

Upgrading from 1.0: the Power switch replaces the `Online` sensor and the `Wake` and `Shut down` buttons. They're removed automatically, so update any automations that used them.

**Switches stay available while the PC is off** and show off. Turning one on then:

1. sends Wake-on-LAN (3 packets, then once more at 60 s),
2. waits for the agent's `/health` to answer (up to the wake timeout, 3 minutes by default),
3. starts it. Switches turned on during the same wake are started in the order you pressed them.

While waiting, the switch shows on with the attribute `pending: waking`. If the PC doesn't come up in time, the switch goes back to off and a persistent notification names what was not started.

The entity list is saved in Home Assistant, so the switches survive a restart of Home Assistant while the PC is off. Rescans that add or remove scripts or games are picked up on the next poll without reloading.

## Requirements and assumptions

- **Home Assistant and the PC are on the same subnet.** Wake-on-LAN packets don't cross routers. If Home Assistant runs in Docker, the container needs `network_mode: host`. HAOS and supervised installs work as they are.
- **Wake-on-LAN from shutdown works** on the PC (BIOS/UEFI and the network adapter must allow it).
- **The PC has a stable IP**, ideally a DHCP reservation.
- **The PC logs in automatically** after boot, so the agent (which runs in your user session) starts on its own.

## Troubleshooting

### "Cannot connect" when adding the PC

Check the agent locally first: `http://localhost:8765/api/v1/health` in a browser on the PC should show `{"status":"ok",...}`. If it does, Windows Firewall is blocking the LAN. From an elevated PowerShell:

```powershell
Get-NetConnectionProfile                                  # which networks are Public?
Get-NetFirewallRule -DisplayName 'PC Control Agent'       # the rule from setup must exist
```

- **The network is Public.** The firewall rule only covers Private networks. On a trusted home network, run `Set-NetConnectionProfile -InterfaceAlias '<name>' -NetworkCategory Private`.
- **Hyper-V external switch.** If Hyper-V shares your Ethernet card, the PC's IP sits on `vEthernet (...)`, and that adapter's profile is what counts. It is often Public even when your Ethernet was set to Private.
- **Block rules from a dismissed prompt.** If you closed the "Allow access?" prompt when the agent first started, Windows may have added Block rules for `PcControl.Agent.exe`. Remove them with:

  ```powershell
  Get-NetFirewallApplicationFilter | Where-Object Program -like '*pccontrol.agent.exe' | Get-NetFirewallRule | Where-Object Action -eq Block | Remove-NetFirewallRule
  ```

## API

JSON over HTTP at `http://<pc>:8765/api/v1`. Every endpoint except `/health` requires `Authorization: Bearer <token>`.

| Method | Path | Purpose |
| --- | --- | --- |
| GET | `/health` | Liveness (no auth) |
| GET | `/info` | Hostname, agent version, physical interfaces |
| GET | `/entities` | All entities with cached state and a `revision` |
| GET | `/entities/{id}` | One entity |
| POST | `/entities/{id}/on` | Run the script with `On`, or launch the game |
| POST | `/entities/{id}/off` | Run the script with `Off`, or kill the game |
| POST | `/rescan` | Rescan scripts and Steam |
| POST | `/power/shutdown` | Shut down after the configured delay |

Errors look like `{"error": "busy", "message": "..."}` with status 401 `unauthorized`, 404 `not_found`, 409 `busy` or 500 `action_failed`. The API only accepts IDs from the scanned set, never paths or commands.

## Development

```powershell
dotnet test agent/PcControl.Agent.slnx
```

Set `PCCONTROL_DATA_DIR` to run the agent with a separate config and log folder.
