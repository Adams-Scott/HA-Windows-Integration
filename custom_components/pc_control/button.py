"""Wake, Shut down and Rescan buttons."""

from __future__ import annotations

import logging

from homeassistant.components.button import ButtonEntity
from homeassistant.const import EntityCategory
from homeassistant.core import HomeAssistant
from homeassistant.exceptions import HomeAssistantError
from homeassistant.helpers.entity_platform import AddConfigEntryEntitiesCallback

from . import PcControlConfigEntry
from .api import PcControlError
from .const import WOL_REPEAT
from .entity import PcControlEntity

_LOGGER = logging.getLogger(__package__)

PARALLEL_UPDATES = 0


async def async_setup_entry(
    hass: HomeAssistant,
    entry: PcControlConfigEntry,
    async_add_entities: AddConfigEntryEntitiesCallback,
) -> None:
    async_add_entities([WakeButton(entry), ShutdownButton(entry), RescanButton(entry)])


class WakeButton(PcControlEntity, ButtonEntity):
    """Sends Wake-on-LAN. Does nothing if the PC is already online."""

    _attr_translation_key = "wake"
    _attr_icon = "mdi:power"

    def __init__(self, entry: PcControlConfigEntry) -> None:
        super().__init__(entry, "wake")

    async def async_press(self) -> None:
        if self._rt.ping.data or self._rt.coordinator.agent_online:
            _LOGGER.debug("%s is already online; not sending WOL", self._rt.host_name)
            return
        await self._rt.wake.async_send_wol(WOL_REPEAT)


class ShutdownButton(PcControlEntity, ButtonEntity):
    """Calls /power/shutdown."""

    _attr_translation_key = "shutdown"
    _attr_icon = "mdi:power-standby"

    def __init__(self, entry: PcControlConfigEntry) -> None:
        super().__init__(entry, "shutdown")

    async def async_press(self) -> None:
        if not self._rt.coordinator.agent_online:
            raise HomeAssistantError("PC agent is not reachable")
        try:
            await self._rt.api.shutdown()
        except PcControlError as err:
            raise HomeAssistantError("PC agent is not reachable") from err
        await self._rt.ping.async_request_refresh()


class RescanButton(PcControlEntity, ButtonEntity):
    """Calls /rescan, then refreshes."""

    _attr_translation_key = "rescan"
    _attr_icon = "mdi:refresh"
    _attr_entity_category = EntityCategory.CONFIG

    def __init__(self, entry: PcControlConfigEntry) -> None:
        super().__init__(entry, "rescan")

    async def async_press(self) -> None:
        try:
            await self._rt.api.rescan()
        except PcControlError as err:
            raise HomeAssistantError("PC agent is not reachable") from err
        await self._rt.coordinator.async_refresh()
