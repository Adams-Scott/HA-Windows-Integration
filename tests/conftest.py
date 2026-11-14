"""Fixtures for PC Control tests."""

from __future__ import annotations

from unittest.mock import AsyncMock, patch

import pytest
from pytest_homeassistant_custom_component.common import MockConfigEntry

from custom_components.pc_control.api import CannotConnect
from custom_components.pc_control.const import (
    CONF_BROADCAST,
    CONF_HOSTNAME,
    CONF_MAC,
    CONF_TOKEN,
    CONF_WOL_PORT,
    DOMAIN,
)

ENTRY_ID = "01TESTENTRY"
UID = "aabbccddeeff"

INFO = {
    "hostname": "GAMING-PC",
    "agentVersion": "1.0.0",
    "interfaces": [
        {"name": "Wi-Fi", "mac": "11:22:33:44:55:66", "ipv4": "192.168.1.51", "wired": False},
        {"name": "Ethernet", "mac": "AA:BB:CC:DD:EE:FF", "ipv4": "192.168.1.50", "wired": True},
    ],
}

ENTITIES = {
    "revision": 3,
    "entities": [
        {"id": "script.plex", "kind": "script", "name": "Plex Server", "icon": "mdi:plex", "state": "on", "busy": False},
        {"id": "script.vpn", "kind": "script", "name": "VPN", "icon": "mdi:application", "state": "assumed", "busy": False},
        {"id": "steam.620", "kind": "game", "name": "Portal 2", "icon": "mdi:steam", "state": "off", "busy": False},
    ],
}


@pytest.fixture(autouse=True)
def auto_enable_custom_integrations(enable_custom_integrations):
    yield


@pytest.fixture(autouse=True)
def no_ping():
    """ICMP is not available in CI: the PC answers ping only when a test says so."""
    with patch(
        "custom_components.pc_control.coordinator.PingCoordinator._async_update_data",
        AsyncMock(return_value=False),
    ) as m:
        yield m


@pytest.fixture
def mock_wol():
    with patch("custom_components.pc_control.wake.send_magic_packet") as m:
        yield m


@pytest.fixture
def config_entry() -> MockConfigEntry:
    return MockConfigEntry(
        domain=DOMAIN,
        entry_id=ENTRY_ID,
        unique_id=UID,
        title="GAMING-PC",
        data={
            "host": "192.168.1.50",
            "port": 8765,
            CONF_TOKEN: "tok",
            CONF_HOSTNAME: "GAMING-PC",
            CONF_MAC: "aa:bb:cc:dd:ee:ff",
            CONF_BROADCAST: "255.255.255.255",
            CONF_WOL_PORT: 9,
        },
    )


@pytest.fixture
def api_offline():
    """Every API call fails to connect."""
    err = CannotConnect("offline")
    with (
        patch("custom_components.pc_control.api.PcControlApi.entities", AsyncMock(side_effect=err)) as ents,
        patch("custom_components.pc_control.api.PcControlApi.health", AsyncMock(side_effect=err)) as health,
        patch("custom_components.pc_control.api.PcControlApi.turn", AsyncMock(side_effect=err)) as turn,
    ):
        yield {"entities": ents, "health": health, "turn": turn}


@pytest.fixture
def api_online():
    with (
        patch("custom_components.pc_control.api.PcControlApi.entities", AsyncMock(return_value=ENTITIES)) as ents,
        patch("custom_components.pc_control.api.PcControlApi.health", AsyncMock(return_value={"status": "ok", "version": "1.0.0"})) as health,
        patch("custom_components.pc_control.api.PcControlApi.turn", AsyncMock(return_value={"result": "started"})) as turn,
        patch("custom_components.pc_control.api.PcControlApi.info", AsyncMock(return_value=INFO)) as info,
    ):
        yield {"entities": ents, "health": health, "turn": turn, "info": info}
