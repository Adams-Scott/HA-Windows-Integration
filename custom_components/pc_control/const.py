"""Constants for the PC Control integration."""

from __future__ import annotations

from typing import Final

DOMAIN: Final = "pc_control"

CONF_TOKEN: Final = "token"
CONF_MAC: Final = "mac"
CONF_BROADCAST: Final = "broadcast_address"
CONF_WOL_PORT: Final = "wol_port"
CONF_HOSTNAME: Final = "hostname"
CONF_POLL_INTERVAL: Final = "poll_interval"
CONF_WAKE_TIMEOUT: Final = "wake_timeout"

DEFAULT_PORT: Final = 8765
DEFAULT_BROADCAST: Final = "255.255.255.255"
DEFAULT_WOL_PORT: Final = 9
DEFAULT_POLL_INTERVAL: Final = 10  # seconds
DEFAULT_WAKE_TIMEOUT: Final = 180  # seconds

PING_INTERVAL: Final = 10  # seconds
OPTIMISTIC_HOLD: Final = 15  # seconds a command's optimistic state beats the poll

# Wake-then-start timing
WOL_REPEAT: Final = 3
WOL_REPEAT_GAP: Final = 1.0
WOL_RESEND_AT: Final = 60
HEALTH_POLL_EVERY: Final = 3

STORAGE_VERSION: Final = 1

STATE_ON: Final = "on"
STATE_OFF: Final = "off"
STATE_UNKNOWN: Final = "unknown"
STATE_ASSUMED: Final = "assumed"

ATTR_PENDING: Final = "pending"
PENDING_WAKING: Final = "waking"
