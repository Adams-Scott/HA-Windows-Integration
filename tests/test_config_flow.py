"""Config flow tests."""

from __future__ import annotations

from unittest.mock import AsyncMock, patch

from homeassistant import config_entries
from homeassistant.core import HomeAssistant
from homeassistant.data_entry_flow import FlowResultType

from custom_components.pc_control.api import CannotConnect, InvalidAuth
from custom_components.pc_control.const import DOMAIN

from .conftest import ENTITIES, INFO

USER_INPUT = {"host": "192.168.1.50", "port": 8765, "token": "tok"}


def _patch_api(info=None, entities=None):
    return (
        patch("custom_components.pc_control.config_flow.PcControlApi.info", AsyncMock(**info)),
        patch("custom_components.pc_control.config_flow.PcControlApi.entities", AsyncMock(**entities)),
    )


async def test_full_flow(hass: HomeAssistant) -> None:
    result = await hass.config_entries.flow.async_init(DOMAIN, context={"source": config_entries.SOURCE_USER})
    assert result["type"] is FlowResultType.FORM
    assert result["step_id"] == "user"

    p1, p2 = _patch_api({"return_value": INFO}, {"return_value": ENTITIES})
    with p1, p2:
        result = await hass.config_entries.flow.async_configure(result["flow_id"], USER_INPUT)
    assert result["type"] is FlowResultType.FORM
    assert result["step_id"] == "wake"
    # Wired adapter first and preselected.
    mac_key = next(k for k in result["data_schema"].schema if k == "mac")
    assert mac_key.default() == "aa:bb:cc:dd:ee:ff"

    with patch("custom_components.pc_control.async_setup_entry", AsyncMock(return_value=True)):
        result = await hass.config_entries.flow.async_configure(
            result["flow_id"],
            {"mac": "AA-BB-CC-DD-EE-FF", "broadcast_address": "192.168.1.255", "wol_port": 9},
        )
    assert result["type"] is FlowResultType.CREATE_ENTRY
    assert result["title"] == "GAMING-PC"
    assert result["result"].unique_id == "aabbccddeeff"
    assert result["data"]["mac"] == "aa:bb:cc:dd:ee:ff"
    assert result["data"]["broadcast_address"] == "192.168.1.255"
    assert result["data"]["token"] == "tok"


async def test_invalid_auth(hass: HomeAssistant) -> None:
    result = await hass.config_entries.flow.async_init(DOMAIN, context={"source": config_entries.SOURCE_USER})
    p1, p2 = _patch_api({"side_effect": InvalidAuth("x")}, {"return_value": ENTITIES})
    with p1, p2:
        result = await hass.config_entries.flow.async_configure(result["flow_id"], USER_INPUT)
    assert result["type"] is FlowResultType.FORM
    assert result["errors"] == {"base": "invalid_auth"}


async def test_cannot_connect(hass: HomeAssistant) -> None:
    result = await hass.config_entries.flow.async_init(DOMAIN, context={"source": config_entries.SOURCE_USER})
    p1, p2 = _patch_api({"side_effect": CannotConnect("x")}, {"return_value": ENTITIES})
    with p1, p2:
        result = await hass.config_entries.flow.async_configure(result["flow_id"], USER_INPUT)
    assert result["errors"] == {"base": "cannot_connect"}


async def test_already_configured(hass: HomeAssistant, config_entry) -> None:
    config_entry.add_to_hass(hass)
    result = await hass.config_entries.flow.async_init(DOMAIN, context={"source": config_entries.SOURCE_USER})
    p1, p2 = _patch_api({"return_value": INFO}, {"return_value": ENTITIES})
    with p1, p2:
        result = await hass.config_entries.flow.async_configure(result["flow_id"], USER_INPUT)
        result = await hass.config_entries.flow.async_configure(
            result["flow_id"], {"mac": "aa:bb:cc:dd:ee:ff", "broadcast_address": "255.255.255.255", "wol_port": 9}
        )
    assert result["type"] is FlowResultType.ABORT
    assert result["reason"] == "already_configured"
