"""One switch per agent script and Steam game."""

from __future__ import annotations

import logging
import time
from typing import Any

from homeassistant.components.switch import SwitchEntity
from homeassistant.const import Platform
from homeassistant.core import HomeAssistant, callback
from homeassistant.exceptions import HomeAssistantError
from homeassistant.helpers import entity_registry as er
from homeassistant.helpers.entity_platform import AddConfigEntryEntitiesCallback
from homeassistant.helpers.update_coordinator import CoordinatorEntity

from . import PcControlConfigEntry
from .api import ApiError, CannotConnect, InvalidAuth, PcControlError
from .const import ATTR_PENDING, PENDING_WAKING, STATE_OFF, STATE_ON, WOL_REPEAT
from .coordinator import AgentEntityInfo, PcControlCoordinator, PingCoordinator
from .entity import PcControlEntity

_LOGGER = logging.getLogger(__package__)

PARALLEL_UPDATES = 0

POWER_KEY = "power"
# How long a Power command's target state is shown before ping/agent state wins again.
# A boot (with auto-login) and a shutdown both take a while, so these are longer than
# the 15 s hold used for program switches.
POWER_ON_HOLD = 180
POWER_OFF_HOLD = 120


async def async_setup_entry(
    hass: HomeAssistant,
    entry: PcControlConfigEntry,
    async_add_entities: AddConfigEntryEntitiesCallback,
) -> None:
    """Create switches from the known entity list and keep them in sync with the agent."""
    rt = entry.runtime_data
    coordinator = rt.coordinator
    added: dict[str, PcControlSwitch] = {}

    async_add_entities([PowerSwitch(entry)])

    @callback
    def _sync() -> None:
        new = [
            PcControlSwitch(entry, agent_id)
            for agent_id in coordinator.known
            if agent_id not in added
        ]
        for ent in new:
            added[ent.agent_id] = ent
        if new:
            async_add_entities(new)

        # Only prune when the agent actually answered: an offline PC must never remove switches.
        if not coordinator.agent_online:
            return
        expected = {f"{rt.uid_base}_{agent_id}" for agent_id in coordinator.known}
        expected.add(f"{rt.uid_base}_{POWER_KEY}")
        registry = er.async_get(hass)
        for reg in er.async_entries_for_config_entry(registry, entry.entry_id):
            if reg.domain == Platform.SWITCH and reg.unique_id not in expected:
                _LOGGER.debug("Removing %s: no longer on the PC", reg.entity_id)
                registry.async_remove(reg.entity_id)
        for agent_id in [a for a in added if a not in coordinator.known]:
            added.pop(agent_id)

    _sync()
    entry.async_on_unload(coordinator.async_add_listener(_sync))


class PowerSwitch(PcControlEntity, CoordinatorEntity[PingCoordinator], SwitchEntity):
    """The PC's power: on while it answers ping (or the agent answers).

    Turning on sends Wake-on-LAN (nothing if already on); turning off shuts the PC down.
    After a command the target state is shown until the PC reaches it or the hold expires.
    """

    _attr_translation_key = "power"
    _attr_icon = "mdi:power"

    def __init__(self, entry: PcControlConfigEntry) -> None:
        CoordinatorEntity.__init__(self, entry.runtime_data.ping)
        PcControlEntity.__init__(self, entry, POWER_KEY)
        self._target: bool | None = None
        self._target_until = 0.0

    async def async_added_to_hass(self) -> None:
        await super().async_added_to_hass()
        # The agent answering also means the PC is on (e.g. where ICMP is blocked).
        self.async_on_remove(
            self._rt.coordinator.async_add_listener(self._handle_coordinator_update)
        )

    @property
    def _actual(self) -> bool:
        return bool(self.coordinator.data) or self._rt.coordinator.agent_online

    @property
    def is_on(self) -> bool:
        actual = self._actual
        if self._target is not None:
            if actual == self._target or time.monotonic() >= self._target_until:
                self._target = None
            else:
                return self._target
        return actual

    def _set_target(self, on: bool, hold: float) -> None:
        self._target = on
        self._target_until = time.monotonic() + hold
        self.async_write_ha_state()

    async def async_turn_on(self, **kwargs: Any) -> None:
        if self._actual:
            _LOGGER.debug("%s is already on; not sending WOL", self._rt.host_name)
            return
        self._set_target(True, POWER_ON_HOLD)
        await self._rt.wake.async_send_wol(WOL_REPEAT)

    async def async_turn_off(self, **kwargs: Any) -> None:
        if not self._rt.coordinator.agent_online:
            if not self._actual:
                return  # Already off.
            raise HomeAssistantError("PC agent is not reachable")
        try:
            await self._rt.api.shutdown()
        except PcControlError as err:
            raise HomeAssistantError("PC agent is not reachable") from err
        self._set_target(False, POWER_OFF_HOLD)
        await self.coordinator.async_request_refresh()


class PcControlSwitch(PcControlEntity, CoordinatorEntity[PcControlCoordinator], SwitchEntity):
    """A script or game on the PC.

    Stays available while the PC is off (showing off), so that turning it on can wake the PC.
    """

    def __init__(self, entry: PcControlConfigEntry, agent_id: str) -> None:
        CoordinatorEntity.__init__(self, entry.runtime_data.coordinator)
        PcControlEntity.__init__(self, entry, agent_id)
        self.agent_id = agent_id

    @property
    def _info(self) -> AgentEntityInfo | None:
        return self.coordinator.known.get(self.agent_id)

    @property
    def name(self) -> str:
        info = self._info
        return info.name if info else self.agent_id

    @property
    def icon(self) -> str | None:
        info = self._info
        return info.icon if info else None

    @property
    def assumed_state(self) -> bool:
        info = self._info
        return bool(info and info.assumed)

    @property
    def is_on(self) -> bool | None:
        if self._rt.wake.is_pending(self.agent_id):
            return True
        optimistic = self.coordinator.optimistic_state(self.agent_id)
        if optimistic is not None:
            return optimistic
        if not self.coordinator.agent_online:
            return False
        if self.assumed_state:
            return self.coordinator.assumed_on.get(self.agent_id, False)
        state = self.coordinator.data.states.get(self.agent_id, STATE_OFF)
        if state == STATE_ON:
            return True
        if state == STATE_OFF:
            return False
        return None

    @property
    def extra_state_attributes(self) -> dict[str, Any]:
        attrs: dict[str, Any] = {"agent_id": self.agent_id}
        if info := self._info:
            attrs["kind"] = info.kind
        if self._rt.wake.is_pending(self.agent_id):
            attrs[ATTR_PENDING] = PENDING_WAKING
        if self.coordinator.data and self.coordinator.data.busy.get(self.agent_id):
            attrs["busy"] = True
        return attrs

    async def async_turn_on(self, **kwargs: Any) -> None:
        if not self.coordinator.agent_online:
            self._start_wake()
            return
        try:
            await self._rt.api.turn(self.agent_id, True)
        except CannotConnect:
            # Agent just went away (PC shutting down or booting): wake-then-start.
            self._start_wake()
            return
        except InvalidAuth as err:
            raise HomeAssistantError("The PC Control API token was rejected") from err
        except ApiError as err:
            self._handle_api_error(err, "on")
            return
        self.coordinator.set_optimistic(self.agent_id, True)
        self.async_write_ha_state()

    async def async_turn_off(self, **kwargs: Any) -> None:
        if self._rt.wake.is_pending(self.agent_id):
            self._rt.wake.cancel_pending(self.agent_id)
            self.coordinator.set_optimistic(self.agent_id, False)
            self.async_write_ha_state()
            return
        if not self.coordinator.agent_online:
            # Already off.
            self.coordinator.clear_optimistic(self.agent_id)
            self.async_write_ha_state()
            return
        try:
            result = await self._rt.api.turn(self.agent_id, False)
        except CannotConnect:
            self.coordinator.set_optimistic(self.agent_id, False)
            self.async_write_ha_state()
            return
        except InvalidAuth as err:
            raise HomeAssistantError("The PC Control API token was rejected") from err
        except ApiError as err:
            self._handle_api_error(err, "off")
            return
        if result.get("result") == "partial":
            _LOGGER.warning(
                "%s: some processes could not be stopped: %s",
                self.name,
                ", ".join(result.get("survivors", [])),
            )
        self.coordinator.set_optimistic(self.agent_id, False)
        self.async_write_ha_state()

    @callback
    def _start_wake(self) -> None:
        self._rt.wake.request_start(self.agent_id)
        self.async_write_ha_state()

    def _handle_api_error(self, err: ApiError, action: str) -> None:
        if err.status == 409:
            _LOGGER.debug("%s is busy; ignoring %s", self.agent_id, action)
            return
        if err.status == 404:
            raise HomeAssistantError(f"{self.name} no longer exists on the PC; press Rescan") from err
        raise HomeAssistantError(f"Could not turn {action} {self.name}: {err.message or err.code}") from err
