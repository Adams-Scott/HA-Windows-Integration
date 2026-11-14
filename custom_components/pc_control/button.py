"""Rescan button. (Wake and Shut down are handled by the Power switch.)"""

from __future__ import annotations

from homeassistant.components.button import ButtonEntity
from homeassistant.const import EntityCategory
from homeassistant.core import HomeAssistant
from homeassistant.exceptions import HomeAssistantError
from homeassistant.helpers.entity_platform import AddConfigEntryEntitiesCallback

from . import PcControlConfigEntry
from .api import PcControlError
from .entity import PcControlEntity

PARALLEL_UPDATES = 0


async def async_setup_entry(
    hass: HomeAssistant,
    entry: PcControlConfigEntry,
    async_add_entities: AddConfigEntryEntitiesCallback,
) -> None:
    async_add_entities([RescanButton(entry)])


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
