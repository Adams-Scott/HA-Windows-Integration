"""PC Control: start and stop programs and Steam games on a Windows PC, with Wake-on-LAN."""

from __future__ import annotations

from dataclasses import dataclass

from homeassistant.config_entries import ConfigEntry
from homeassistant.const import CONF_HOST, CONF_PORT, Platform
from homeassistant.core import HomeAssistant
from homeassistant.helpers.aiohttp_client import async_get_clientsession

from .api import PcControlApi
from .const import (
    CONF_BROADCAST,
    CONF_HOSTNAME,
    CONF_MAC,
    CONF_POLL_INTERVAL,
    CONF_TOKEN,
    CONF_WAKE_TIMEOUT,
    CONF_WOL_PORT,
    DEFAULT_BROADCAST,
    DEFAULT_POLL_INTERVAL,
    DEFAULT_WAKE_TIMEOUT,
    DEFAULT_WOL_PORT,
)
from .coordinator import PcControlCoordinator, PingCoordinator
from .wake import WakeManager

PLATFORMS: list[Platform] = [Platform.BINARY_SENSOR, Platform.BUTTON, Platform.SWITCH]


@dataclass
class PcControlRuntime:
    """Objects shared by the platforms of one config entry (one PC)."""

    api: PcControlApi
    coordinator: PcControlCoordinator
    ping: PingCoordinator
    wake: WakeManager
    host_name: str
    uid_base: str


type PcControlConfigEntry = ConfigEntry[PcControlRuntime]


def merged_settings(entry: ConfigEntry) -> dict:
    """Entry data overlaid with options."""
    return {**entry.data, **entry.options}


async def async_setup_entry(hass: HomeAssistant, entry: PcControlConfigEntry) -> bool:
    """Set up one PC."""
    s = merged_settings(entry)
    host_name = s.get(CONF_HOSTNAME) or entry.title
    api = PcControlApi(async_get_clientsession(hass), s[CONF_HOST], s[CONF_PORT], s[CONF_TOKEN])

    coordinator = PcControlCoordinator(
        hass, entry, api, int(s.get(CONF_POLL_INTERVAL, DEFAULT_POLL_INTERVAL))
    )
    # Load the stored entity list first, so switches exist even while the PC is off.
    await coordinator.async_load_stored()
    await coordinator.async_config_entry_first_refresh()

    ping = PingCoordinator(hass, entry, s[CONF_HOST])
    await ping.async_config_entry_first_refresh()

    wake = WakeManager(
        hass,
        entry,
        api,
        coordinator,
        ping,
        mac=s[CONF_MAC],
        broadcast=s.get(CONF_BROADCAST, DEFAULT_BROADCAST),
        wol_port=int(s.get(CONF_WOL_PORT, DEFAULT_WOL_PORT)),
        timeout=int(s.get(CONF_WAKE_TIMEOUT, DEFAULT_WAKE_TIMEOUT)),
        host_name=host_name,
    )

    entry.runtime_data = PcControlRuntime(
        api=api,
        coordinator=coordinator,
        ping=ping,
        wake=wake,
        host_name=host_name,
        uid_base=entry.unique_id or entry.entry_id,
    )

    await hass.config_entries.async_forward_entry_setups(entry, PLATFORMS)
    entry.async_on_unload(entry.add_update_listener(_async_update_listener))
    return True


async def _async_update_listener(hass: HomeAssistant, entry: PcControlConfigEntry) -> None:
    await hass.config_entries.async_reload(entry.entry_id)


async def async_unload_entry(hass: HomeAssistant, entry: PcControlConfigEntry) -> bool:
    """Unload a config entry."""
    entry.runtime_data.wake.async_cancel()
    return await hass.config_entries.async_unload_platforms(entry, PLATFORMS)


async def async_remove_entry(hass: HomeAssistant, entry: ConfigEntry) -> None:
    """Delete the stored entity list when the entry is removed."""
    from homeassistant.helpers.storage import Store

    from .const import DOMAIN, STORAGE_VERSION

    await Store(hass, STORAGE_VERSION, f"{DOMAIN}.{entry.entry_id}").async_remove()
