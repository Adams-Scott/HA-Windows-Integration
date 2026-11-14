"""Shared entity base for PC Control."""

from __future__ import annotations

from typing import TYPE_CHECKING

from homeassistant.helpers.device_registry import CONNECTION_NETWORK_MAC, DeviceInfo
from homeassistant.helpers.entity import Entity

from .const import CONF_MAC, DOMAIN
from .wol import format_mac

if TYPE_CHECKING:
    from . import PcControlConfigEntry


def device_info(entry: PcControlConfigEntry) -> DeviceInfo:
    """All entities of one entry belong to one device named after the hostname."""
    rt = entry.runtime_data
    info = DeviceInfo(
        identifiers={(DOMAIN, rt.uid_base)},
        name=rt.host_name,
        manufacturer="PC Control",
        model="Windows PC",
        sw_version=rt.coordinator.agent_version,
    )
    try:
        info["connections"] = {(CONNECTION_NETWORK_MAC, format_mac(entry.data[CONF_MAC]))}
    except (KeyError, ValueError):
        pass
    return info


class PcControlEntity(Entity):
    """Base: device info and has_entity_name."""

    _attr_has_entity_name = True

    def __init__(self, entry: PcControlConfigEntry, key: str) -> None:
        self._entry = entry
        self._rt = entry.runtime_data
        self._attr_unique_id = f"{self._rt.uid_base}_{key}"
        self._attr_device_info = device_info(entry)
