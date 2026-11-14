"""Coordinators for PC Control: agent polling (with a stored entity list) and ICMP ping."""

from __future__ import annotations

import asyncio
from dataclasses import asdict, dataclass, field
from datetime import timedelta
import logging
import sys
import time
from typing import TYPE_CHECKING, Any

from icmplib import ICMPLibError, NameLookupError, SocketPermissionError, async_ping

from homeassistant.core import HomeAssistant
from homeassistant.exceptions import ConfigEntryAuthFailed
from homeassistant.helpers.storage import Store
from homeassistant.helpers.update_coordinator import DataUpdateCoordinator

from .api import ApiError, CannotConnect, InvalidAuth, PcControlApi
from .const import DOMAIN, OPTIMISTIC_HOLD, PING_INTERVAL, STATE_ASSUMED, STORAGE_VERSION

if TYPE_CHECKING:
    from homeassistant.config_entries import ConfigEntry

_LOGGER = logging.getLogger(__package__)


@dataclass(frozen=True)
class AgentEntityInfo:
    """What HA needs to create a switch, persisted so switches exist while the PC is off."""

    id: str
    kind: str
    name: str
    icon: str
    assumed: bool = False


@dataclass
class AgentData:
    """Result of one /entities poll."""

    agent_online: bool
    revision: int | None = None
    states: dict[str, str] = field(default_factory=dict)
    busy: dict[str, bool] = field(default_factory=dict)


class PcControlCoordinator(DataUpdateCoordinator[AgentData]):
    """Polls GET /entities.

    An unreachable agent is not an update failure: it yields AgentData(agent_online=False),
    so switches stay available (and show off) while the PC is off or booting.
    """

    config_entry: ConfigEntry

    def __init__(
        self,
        hass: HomeAssistant,
        entry: ConfigEntry,
        api: PcControlApi,
        poll_interval: int,
    ) -> None:
        super().__init__(
            hass,
            _LOGGER,
            config_entry=entry,
            name=f"{DOMAIN} {entry.title}",
            update_interval=timedelta(seconds=poll_interval),
            always_update=True,
        )
        self.api = api
        self._store: Store[dict[str, Any]] = Store(hass, STORAGE_VERSION, f"{DOMAIN}.{entry.entry_id}")
        self.known: dict[str, AgentEntityInfo] = {}
        self.agent_version: str | None = None
        self._optimistic: dict[str, tuple[bool, float]] = {}
        # Last commanded state of assumed-state switches (scripts without HA-Check).
        self.assumed_on: dict[str, bool] = {}

    # ----- storage -------------------------------------------------------------------------

    async def async_load_stored(self) -> None:
        """Load the entity list saved by the last successful poll."""
        stored = await self._store.async_load()
        if not stored:
            return
        known: dict[str, AgentEntityInfo] = {}
        for item in stored.get("entities", []):
            try:
                info = AgentEntityInfo(**item)
            except TypeError:
                continue
            known[info.id] = info
        self.known = known
        self.agent_version = stored.get("agent_version")
        self.assumed_on = {k: bool(v) for k, v in stored.get("assumed_on", {}).items()}

    def _data_to_store(self) -> dict[str, Any]:
        return {
            "entities": [asdict(i) for i in self.known.values()],
            "agent_version": self.agent_version,
            "assumed_on": self.assumed_on,
        }

    def _schedule_save(self) -> None:
        self._store.async_delay_save(self._data_to_store, 1)

    async def async_remove_store(self) -> None:
        await self._store.async_remove()

    # ----- polling -------------------------------------------------------------------------

    @property
    def agent_online(self) -> bool:
        return bool(self.data and self.data.agent_online)

    async def _async_update_data(self) -> AgentData:
        try:
            body = await self.api.entities()
        except InvalidAuth as err:
            raise ConfigEntryAuthFailed("The PC Control API token was rejected") from err
        except (CannotConnect, ApiError) as err:
            _LOGGER.debug("Agent not reachable: %s", err)
            return AgentData(agent_online=False, revision=self.data.revision if self.data else None)

        states: dict[str, str] = {}
        busy: dict[str, bool] = {}
        known: dict[str, AgentEntityInfo] = {}
        for item in body.get("entities", []):
            try:
                agent_id = str(item["id"])
            except (KeyError, TypeError):
                continue
            state = str(item.get("state", "unknown"))
            states[agent_id] = state
            busy[agent_id] = bool(item.get("busy", False))
            known[agent_id] = AgentEntityInfo(
                id=agent_id,
                kind=str(item.get("kind", "script")),
                name=str(item.get("name", agent_id)),
                icon=str(item.get("icon", "mdi:application")),
                assumed=state == STATE_ASSUMED,
            )

        if known != self.known:
            self.known = known
            self.assumed_on = {k: v for k, v in self.assumed_on.items() if k in known}
            self._schedule_save()

        if self.agent_version is None:
            try:
                health = await self.api.health()
                self.agent_version = health.get("version")
                self._schedule_save()
            except Exception:  # noqa: BLE001 - version is cosmetic
                pass

        return AgentData(
            agent_online=True,
            revision=body.get("revision"),
            states=states,
            busy=busy,
        )

    # ----- optimistic state ----------------------------------------------------------------

    def set_optimistic(self, agent_id: str, on: bool) -> None:
        """Show `on` for OPTIMISTIC_HOLD seconds regardless of what polls say."""
        self._optimistic[agent_id] = (on, time.monotonic() + OPTIMISTIC_HOLD)
        info = self.known.get(agent_id)
        if info and info.assumed:
            self.assumed_on[agent_id] = on
            self._schedule_save()

    def clear_optimistic(self, agent_id: str) -> None:
        self._optimistic.pop(agent_id, None)

    def optimistic_state(self, agent_id: str) -> bool | None:
        item = self._optimistic.get(agent_id)
        if item is None:
            return None
        on, until = item
        if time.monotonic() >= until:
            del self._optimistic[agent_id]
            return None
        return on


class PingCoordinator(DataUpdateCoordinator[bool]):
    """ICMP ping every 10 s, unprivileged like the core ping integration, with fallbacks."""

    config_entry: ConfigEntry

    def __init__(self, hass: HomeAssistant, entry: ConfigEntry, host: str) -> None:
        super().__init__(
            hass,
            _LOGGER,
            config_entry=entry,
            name=f"{DOMAIN} ping {host}",
            update_interval=timedelta(seconds=PING_INTERVAL),
        )
        self._host = host
        # None = undecided, True/False = icmplib privileged flag, "subprocess" = system ping.
        self._mode: bool | str | None = None

    async def _async_update_data(self) -> bool:
        if self._mode is None:
            self._mode = await self._detect_mode()
        if self._mode == "subprocess":
            return await self._subprocess_ping()
        try:
            host = await async_ping(self._host, count=1, timeout=2, privileged=bool(self._mode))
        except NameLookupError:
            return False
        except SocketPermissionError:
            self._mode = None
            return False
        except ICMPLibError as err:
            _LOGGER.debug("Ping %s failed: %s", self._host, err)
            return False
        return host.is_alive

    async def _detect_mode(self) -> bool | str:
        for privileged in (False, True):
            try:
                await async_ping("127.0.0.1", count=1, timeout=1, privileged=privileged)
            except SocketPermissionError:
                continue
            except ICMPLibError:
                return privileged
            return privileged
        _LOGGER.debug("icmplib cannot open ICMP sockets; falling back to the system ping command")
        return "subprocess"

    async def _subprocess_ping(self) -> bool:
        if sys.platform == "win32":
            args = ["ping", "-n", "1", "-w", "2000", self._host]
        else:
            args = ["ping", "-c", "1", "-W", "2", self._host]
        try:
            proc = await asyncio.create_subprocess_exec(
                *args, stdout=asyncio.subprocess.DEVNULL, stderr=asyncio.subprocess.DEVNULL
            )
            return await asyncio.wait_for(proc.wait(), timeout=5) == 0
        except (OSError, TimeoutError):
            return False
