"""Switch, storage and wake-then-start tests."""

from __future__ import annotations

from unittest.mock import AsyncMock, patch

from homeassistant.const import STATE_OFF, STATE_ON
from homeassistant.core import HomeAssistant
from homeassistant.helpers import entity_registry as er

from custom_components.pc_control.api import CannotConnect

from .conftest import ENTITIES, ENTRY_ID

STORED = {
    "version": 1,
    "minor_version": 1,
    "key": f"pc_control.{ENTRY_ID}",
    "data": {
        "entities": [
            {"id": "script.plex", "kind": "script", "name": "Plex Server", "icon": "mdi:plex", "assumed": False},
            {"id": "steam.620", "kind": "game", "name": "Portal 2", "icon": "mdi:steam", "assumed": False},
        ],
        "agent_version": "1.0.0",
        "assumed_on": {},
    },
}

FAST_WAKE = {
    "custom_components.pc_control.wake.WOL_REPEAT_GAP": 0,
    "custom_components.pc_control.wake.HEALTH_POLL_EVERY": 0.01,
}


def _fast():
    patches = [patch(k, v) for k, v in FAST_WAKE.items()]
    for p in patches:
        p.start()
    return patches


async def _setup(hass: HomeAssistant, entry) -> None:
    entry.add_to_hass(hass)
    assert await hass.config_entries.async_setup(entry.entry_id)
    await hass.async_block_till_done()


async def test_pc_off_switches_from_storage(hass: HomeAssistant, hass_storage, config_entry, api_offline) -> None:
    """With the PC off, stored switches exist, are available and show off."""
    hass_storage[f"pc_control.{ENTRY_ID}"] = STORED
    await _setup(hass, config_entry)

    plex = hass.states.get("switch.gaming_pc_plex_server")
    portal = hass.states.get("switch.gaming_pc_portal_2")
    assert plex is not None and plex.state == STATE_OFF
    assert portal is not None and portal.state == STATE_OFF
    assert hass.states.get("binary_sensor.gaming_pc_agent").state == STATE_OFF
    assert hass.states.get("switch.gaming_pc_power").state == STATE_OFF


async def test_online_states_and_turn_on(hass: HomeAssistant, config_entry, api_online) -> None:
    await _setup(hass, config_entry)

    assert hass.states.get("switch.gaming_pc_plex_server").state == STATE_ON
    assert hass.states.get("switch.gaming_pc_portal_2").state == STATE_OFF
    vpn = hass.states.get("switch.gaming_pc_vpn")
    assert vpn.attributes.get("assumed_state") is True
    assert hass.states.get("binary_sensor.gaming_pc_agent").state == STATE_ON

    await hass.services.async_call("switch", "turn_on", {"entity_id": "switch.gaming_pc_portal_2"}, blocking=True)
    api_online["turn"].assert_awaited_with("steam.620", True)
    # Optimistic: stays on even though the next poll still says off.
    await config_entry.runtime_data.coordinator.async_refresh()
    await hass.async_block_till_done()
    assert hass.states.get("switch.gaming_pc_portal_2").state == STATE_ON


async def test_rescan_removes_entities(hass: HomeAssistant, config_entry, api_online) -> None:
    await _setup(hass, config_entry)
    assert hass.states.get("switch.gaming_pc_portal_2") is not None

    smaller = {"revision": 4, "entities": ENTITIES["entities"][:2]}
    api_online["entities"].return_value = smaller
    await config_entry.runtime_data.coordinator.async_refresh()
    await hass.async_block_till_done()

    registry = er.async_get(hass)
    assert registry.async_get("switch.gaming_pc_portal_2") is None
    assert hass.states.get("switch.gaming_pc_portal_2") is None


async def test_wake_then_start(hass: HomeAssistant, hass_storage, config_entry, api_offline, mock_wol) -> None:
    hass_storage[f"pc_control.{ENTRY_ID}"] = STORED
    await _setup(hass, config_entry)
    patches = _fast()
    try:
        # Agent comes up on the third health poll.
        api_offline["health"].side_effect = [CannotConnect("x"), CannotConnect("x"), {"status": "ok"}] + [
            {"status": "ok"}
        ] * 10
        api_offline["entities"].side_effect = None
        api_offline["entities"].return_value = ENTITIES
        api_offline["turn"].side_effect = None
        api_offline["turn"].return_value = {"result": "started"}

        # Agent is "offline" for the coordinator until it refreshes, so both presses queue.
        await hass.services.async_call("switch", "turn_on", {"entity_id": "switch.gaming_pc_portal_2"}, blocking=True)
        await hass.services.async_call("switch", "turn_on", {"entity_id": "switch.gaming_pc_plex_server"}, blocking=True)
        st = hass.states.get("switch.gaming_pc_portal_2")
        assert st.state == STATE_ON
        assert st.attributes.get("pending") == "waking"

        await hass.async_block_till_done(wait_background_tasks=True)
    finally:
        for p in patches:
            p.stop()

    assert mock_wol.call_count >= 3
    calls = [c.args for c in api_offline["turn"].await_args_list]
    assert calls == [("steam.620", True), ("script.plex", True)]  # press order
    assert "pending" not in hass.states.get("switch.gaming_pc_portal_2").attributes


async def test_wake_timeout(hass: HomeAssistant, hass_storage, config_entry, api_offline, mock_wol) -> None:
    hass_storage[f"pc_control.{ENTRY_ID}"] = STORED
    config_entry.add_to_hass(hass)
    hass.config_entries.async_update_entry(config_entry, options={"wake_timeout": 1})
    assert await hass.config_entries.async_setup(config_entry.entry_id)
    await hass.async_block_till_done()

    patches = _fast()
    try:
        with patch("custom_components.pc_control.wake.persistent_notification.async_create") as notify:
            await hass.services.async_call("switch", "turn_on", {"entity_id": "switch.gaming_pc_portal_2"}, blocking=True)
            assert hass.states.get("switch.gaming_pc_portal_2").state == STATE_ON
            await hass.async_block_till_done(wait_background_tasks=True)
    finally:
        for p in patches:
            p.stop()

    st = hass.states.get("switch.gaming_pc_portal_2")
    assert st.state == STATE_OFF
    assert "pending" not in st.attributes
    api_offline["turn"].assert_not_awaited()
    notify.assert_called_once()
    message = notify.call_args.args[1]
    assert message == "GAMING-PC did not come online within 1 seconds; Portal 2 was not started."


async def test_power_on_sends_wol(hass: HomeAssistant, config_entry, api_offline, mock_wol) -> None:
    await _setup(hass, config_entry)
    patches = _fast()
    try:
        await hass.services.async_call("switch", "turn_on", {"entity_id": "switch.gaming_pc_power"}, blocking=True)
    finally:
        for p in patches:
            p.stop()
    assert mock_wol.call_count == 3
    # Shows on while the PC boots, even though ping doesn't answer yet.
    assert hass.states.get("switch.gaming_pc_power").state == STATE_ON


async def test_power_on_when_online_does_nothing(hass: HomeAssistant, config_entry, api_online, mock_wol) -> None:
    await _setup(hass, config_entry)
    assert hass.states.get("switch.gaming_pc_power").state == STATE_ON  # agent answers
    await hass.services.async_call("switch", "turn_on", {"entity_id": "switch.gaming_pc_power"}, blocking=True)
    mock_wol.assert_not_called()


async def test_power_off_shuts_down(hass: HomeAssistant, config_entry, api_online) -> None:
    await _setup(hass, config_entry)
    with patch(
        "custom_components.pc_control.api.PcControlApi.shutdown", AsyncMock(return_value={"delaySeconds": 5})
    ) as shutdown:
        await hass.services.async_call("switch", "turn_off", {"entity_id": "switch.gaming_pc_power"}, blocking=True)
    shutdown.assert_awaited_once()
    assert hass.states.get("switch.gaming_pc_power").state == STATE_OFF


async def test_power_off_unreachable(hass: HomeAssistant, config_entry, api_offline, no_ping) -> None:
    import pytest
    from homeassistant.exceptions import HomeAssistantError

    no_ping.return_value = True  # PC answers ping but the agent doesn't (still booting)
    await _setup(hass, config_entry)
    assert hass.states.get("switch.gaming_pc_power").state == STATE_ON
    with pytest.raises(HomeAssistantError, match="not reachable"):
        await hass.services.async_call("switch", "turn_off", {"entity_id": "switch.gaming_pc_power"}, blocking=True)


async def test_retired_entities_removed(hass: HomeAssistant, config_entry, api_offline) -> None:
    config_entry.add_to_hass(hass)
    registry = er.async_get(hass)
    for domain, key in (("binary_sensor", "online"), ("button", "wake"), ("button", "shutdown")):
        registry.async_get_or_create(domain, "pc_control", f"aabbccddeeff_{key}", config_entry=config_entry)
    assert await hass.config_entries.async_setup(config_entry.entry_id)
    await hass.async_block_till_done()
    for domain, key in (("binary_sensor", "online"), ("button", "wake"), ("button", "shutdown")):
        assert registry.async_get_entity_id(domain, "pc_control", f"aabbccddeeff_{key}") is None
