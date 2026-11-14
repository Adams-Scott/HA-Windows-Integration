"""Agent (API reachable) sensor. Online status is the Power switch's state."""

from __future__ import annotations

from homeassistant.components.binary_sensor import BinarySensorDeviceClass, BinarySensorEntity
from homeassistant.const import EntityCategory
from homeassistant.core import HomeAssistant
from homeassistant.helpers.entity_platform import AddConfigEntryEntitiesCallback
from homeassistant.helpers.update_coordinator import CoordinatorEntity

from . import PcControlConfigEntry
from .coordinator import PcControlCoordinator
from .entity import PcControlEntity

PARALLEL_UPDATES = 0


async def async_setup_entry(
    hass: HomeAssistant,
    entry: PcControlConfigEntry,
    async_add_entities: AddConfigEntryEntitiesCallback,
) -> None:
    async_add_entities([AgentSensor(entry)])


class AgentSensor(PcControlEntity, CoordinatorEntity[PcControlCoordinator], BinarySensorEntity):
    """On when the last API poll succeeded."""

    _attr_device_class = BinarySensorDeviceClass.CONNECTIVITY
    _attr_entity_category = EntityCategory.DIAGNOSTIC
    _attr_translation_key = "agent"

    def __init__(self, entry: PcControlConfigEntry) -> None:
        CoordinatorEntity.__init__(self, entry.runtime_data.coordinator)
        PcControlEntity.__init__(self, entry, "agent")

    @property
    def is_on(self) -> bool:
        return self.coordinator.agent_online
