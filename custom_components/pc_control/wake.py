"""Wake-then-start: send WOL, wait for the agent's /health, then start the queued switches."""

from __future__ import annotations

import asyncio
import logging
import time
from typing import TYPE_CHECKING

from homeassistant.components import persistent_notification
from homeassistant.core import HomeAssistant, callback

from .api import ApiError, InvalidAuth, PcControlApi, PcControlError
from .const import DOMAIN, HEALTH_POLL_EVERY, WOL_REPEAT, WOL_REPEAT_GAP, WOL_RESEND_AT
from .wol import send_magic_packet

if TYPE_CHECKING:
    from homeassistant.config_entries import ConfigEntry

    from .coordinator import PcControlCoordinator, PingCoordinator

_LOGGER = logging.getLogger(__package__)


class WakeManager:
    """One shared wake task per PC; presses during a wake join its queue in press order."""

    def __init__(
        self,
        hass: HomeAssistant,
        entry: ConfigEntry,
        api: PcControlApi,
        coordinator: PcControlCoordinator,
        ping: PingCoordinator,
        *,
        mac: str,
        broadcast: str,
        wol_port: int,
        timeout: int,
        host_name: str,
    ) -> None:
        self._hass = hass
        self._entry = entry
        self._api = api
        self._coordinator = coordinator
        self._ping = ping
        self._mac = mac
        self._broadcast = broadcast
        self._wol_port = wol_port
        self._timeout = timeout
        self._host_name = host_name
        self._queue: list[str] = []
        self._task: asyncio.Task[None] | None = None

    @property
    def waking(self) -> bool:
        return self._task is not None and not self._task.done()

    def is_pending(self, agent_id: str) -> bool:
        return agent_id in self._queue

    async def async_send_wol(self, times: int = 1) -> None:
        """Send `times` magic packets, WOL_REPEAT_GAP seconds apart."""
        for i in range(times):
            if i:
                await asyncio.sleep(WOL_REPEAT_GAP)
            try:
                await self._hass.async_add_executor_job(
                    send_magic_packet, self._mac, self._broadcast, self._wol_port
                )
            except OSError as err:
                _LOGGER.warning("Could not send Wake-on-LAN packet to %s: %s", self._broadcast, err)
        _LOGGER.debug("Sent %d WOL packet(s) for %s", times, self._mac)

    @callback
    def request_start(self, agent_id: str) -> None:
        """Queue a switch to start once the PC is up; starts the shared wake task if needed."""
        if agent_id not in self._queue:
            self._queue.append(agent_id)
        if not self.waking:
            self._task = self._entry.async_create_background_task(
                self._hass, self._run(), f"{DOMAIN} wake {self._host_name}"
            )
        self._coordinator.async_update_listeners()

    @callback
    def cancel_pending(self, agent_id: str) -> None:
        """Drop a switch from the queue (it was turned off while waking)."""
        if agent_id in self._queue:
            self._queue.remove(agent_id)
            self._coordinator.async_update_listeners()

    @callback
    def async_cancel(self) -> None:
        if self._task and not self._task.done():
            self._task.cancel()
        self._queue.clear()

    async def _run(self) -> None:
        start = time.monotonic()

        def elapsed() -> float:
            return time.monotonic() - start

        try:
            await self.async_send_wol(WOL_REPEAT)

            # Readiness is the agent's /health, not ping: ping answers long before auto-login.
            ready = False
            resent = False
            while elapsed() < self._timeout:
                try:
                    await self._api.health(timeout=2)
                    ready = True
                    break
                except PcControlError:
                    pass
                if not resent and elapsed() >= WOL_RESEND_AT:
                    resent = True
                    await self.async_send_wol(1)
                await asyncio.sleep(HEALTH_POLL_EVERY)

            # Make sure /entities has loaded so a still-loading game list doesn't 404.
            loaded = False
            while ready and elapsed() < self._timeout:
                try:
                    body = await self._api.entities()
                except InvalidAuth:
                    break
                except PcControlError:
                    await asyncio.sleep(HEALTH_POLL_EVERY)
                    continue
                ids = {str(e.get("id")) for e in body.get("entities", [])}
                if all(q in ids for q in self._queue) or elapsed() >= self._timeout - HEALTH_POLL_EVERY:
                    loaded = True
                    break
                await asyncio.sleep(HEALTH_POLL_EVERY)

            if not (ready and loaded):
                self._on_timeout()
                return

            await self._coordinator.async_refresh()
            while self._queue:
                agent_id = self._queue[0]
                try:
                    await self._api.turn(agent_id, True)
                    self._coordinator.set_optimistic(agent_id, True)
                    _LOGGER.info("Started %s after waking %s", agent_id, self._host_name)
                except ApiError as err:
                    if err.status == 409:
                        _LOGGER.debug("%s is busy on the agent", agent_id)
                    else:
                        _LOGGER.warning("Could not start %s after wake: %s", agent_id, err)
                        self._coordinator.set_optimistic(agent_id, False)
                except PcControlError as err:
                    _LOGGER.warning("Could not start %s after wake: %s", agent_id, err)
                    self._coordinator.set_optimistic(agent_id, False)
                finally:
                    if self._queue and self._queue[0] == agent_id:
                        self._queue.pop(0)
                    self._coordinator.async_update_listeners()

            await self._coordinator.async_request_refresh()
            await self._ping.async_request_refresh()
        finally:
            self._task = None
            if self._queue and elapsed() >= self._timeout:
                self._on_timeout()

    @callback
    def _on_timeout(self) -> None:
        ids = list(self._queue)
        self._queue.clear()
        if not ids:
            return
        names = [
            info.name if (info := self._coordinator.known.get(i)) else i for i in ids
        ]
        for agent_id in ids:
            self._coordinator.clear_optimistic(agent_id)
            self._coordinator.assumed_on.pop(agent_id, None)
        self._coordinator.async_update_listeners()

        minutes = self._timeout / 60
        span = f"{minutes:g} minutes" if self._timeout % 60 == 0 else f"{self._timeout} seconds"
        message = f"{self._host_name} did not come online within {span}; {', '.join(names)} were not started."
        if len(names) == 1:
            message = message.replace(" were not started", " was not started")
        _LOGGER.warning(message)
        persistent_notification.async_create(
            self._hass,
            message,
            title="PC Control",
            notification_id=f"{DOMAIN}_{self._entry.entry_id}_wake_timeout",
        )
