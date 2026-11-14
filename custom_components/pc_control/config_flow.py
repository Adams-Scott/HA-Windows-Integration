"""Config, reauth, reconfigure and options flows for PC Control."""

from __future__ import annotations

import logging
from typing import Any

import voluptuous as vol

from homeassistant.config_entries import ConfigFlow, ConfigFlowResult, OptionsFlow
from homeassistant.const import CONF_HOST, CONF_PORT
from homeassistant.core import HomeAssistant, callback
from homeassistant.helpers.aiohttp_client import async_get_clientsession
from homeassistant.helpers.selector import (
    NumberSelector,
    NumberSelectorConfig,
    NumberSelectorMode,
    SelectOptionDict,
    SelectSelector,
    SelectSelectorConfig,
    SelectSelectorMode,
    TextSelector,
    TextSelectorConfig,
    TextSelectorType,
)

from .api import CannotConnect, InvalidAuth, PcControlApi, PcControlError
from .const import (
    CONF_BROADCAST,
    CONF_HOSTNAME,
    CONF_MAC,
    CONF_POLL_INTERVAL,
    CONF_TOKEN,
    CONF_WAKE_TIMEOUT,
    CONF_WOL_PORT,
    DEFAULT_BROADCAST,
    DEFAULT_POLL_INTERVAL,
    DEFAULT_PORT,
    DEFAULT_WAKE_TIMEOUT,
    DEFAULT_WOL_PORT,
    DOMAIN,
)
from .wol import format_mac, normalize_mac

_LOGGER = logging.getLogger(__package__)

PORT_SELECTOR = NumberSelector(NumberSelectorConfig(min=1, max=65535, mode=NumberSelectorMode.BOX))
TOKEN_SELECTOR = TextSelector(TextSelectorConfig(type=TextSelectorType.PASSWORD))


def _connect_schema(defaults: dict[str, Any]) -> vol.Schema:
    return vol.Schema(
        {
            vol.Required(CONF_HOST, default=defaults.get(CONF_HOST, vol.UNDEFINED)): str,
            vol.Required(CONF_PORT, default=defaults.get(CONF_PORT, DEFAULT_PORT)): PORT_SELECTOR,
            vol.Required(CONF_TOKEN): TOKEN_SELECTOR,
        }
    )


async def _validate(hass: HomeAssistant, host: str, port: int, token: str) -> dict[str, Any]:
    """Call /info and /entities. Raises CannotConnect or InvalidAuth."""
    api = PcControlApi(async_get_clientsession(hass), host, port, token)
    info = await api.info()
    await api.entities()
    return info


async def _try_validate(
    hass: HomeAssistant, host: str, port: int, token: str, errors: dict[str, str]
) -> dict[str, Any] | None:
    try:
        return await _validate(hass, host, port, token)
    except InvalidAuth:
        errors["base"] = "invalid_auth"
    except CannotConnect:
        errors["base"] = "cannot_connect"
    except PcControlError:
        _LOGGER.exception("Unexpected response from the PC Control agent")
        errors["base"] = "unknown"
    return None


def _mac_options(info: dict[str, Any]) -> list[SelectOptionDict]:
    interfaces = sorted(info.get("interfaces", []), key=lambda i: not i.get("wired"))
    options: list[SelectOptionDict] = []
    for iface in interfaces:
        try:
            mac = format_mac(str(iface.get("mac", "")))
        except ValueError:
            continue
        label = f"{iface.get('name', 'Interface')} — {mac.upper()}"
        if iface.get("ipv4"):
            label += f" ({iface['ipv4']})"
        if iface.get("wired"):
            label += " · wired"
        options.append(SelectOptionDict(value=mac, label=label))
    return options


class PcControlConfigFlow(ConfigFlow, domain=DOMAIN):
    """Two steps: connect, then wake settings."""

    VERSION = 1

    def __init__(self) -> None:
        self._connect: dict[str, Any] = {}
        self._info: dict[str, Any] = {}

    async def async_step_user(self, user_input: dict[str, Any] | None = None) -> ConfigFlowResult:
        errors: dict[str, str] = {}
        if user_input is not None:
            host = user_input[CONF_HOST].strip()
            port = int(user_input[CONF_PORT])
            token = user_input[CONF_TOKEN].strip()
            info = await _try_validate(self.hass, host, port, token, errors)
            if info is not None:
                self._connect = {CONF_HOST: host, CONF_PORT: port, CONF_TOKEN: token}
                self._info = info
                return await self.async_step_wake()

        return self.async_show_form(
            step_id="user", data_schema=_connect_schema(user_input or {}), errors=errors
        )

    async def async_step_wake(self, user_input: dict[str, Any] | None = None) -> ConfigFlowResult:
        errors: dict[str, str] = {}
        options = _mac_options(self._info)

        if user_input is not None:
            try:
                mac = normalize_mac(user_input[CONF_MAC])
            except ValueError:
                errors[CONF_MAC] = "invalid_mac"
            else:
                await self.async_set_unique_id(mac)
                self._abort_if_unique_id_configured(updates=self._connect)
                hostname = str(self._info.get("hostname") or self._connect[CONF_HOST])
                return self.async_create_entry(
                    title=hostname,
                    data={
                        **self._connect,
                        CONF_HOSTNAME: hostname,
                        CONF_MAC: format_mac(mac),
                        CONF_BROADCAST: user_input[CONF_BROADCAST].strip(),
                        CONF_WOL_PORT: int(user_input[CONF_WOL_PORT]),
                    },
                )

        default_mac = options[0]["value"] if options else vol.UNDEFINED
        schema = vol.Schema(
            {
                vol.Required(CONF_MAC, default=default_mac): SelectSelector(
                    SelectSelectorConfig(
                        options=options, custom_value=True, mode=SelectSelectorMode.DROPDOWN
                    )
                ),
                vol.Required(CONF_BROADCAST, default=DEFAULT_BROADCAST): str,
                vol.Required(CONF_WOL_PORT, default=DEFAULT_WOL_PORT): PORT_SELECTOR,
            }
        )
        return self.async_show_form(
            step_id="wake",
            data_schema=schema,
            errors=errors,
            description_placeholders={"hostname": str(self._info.get("hostname", ""))},
        )

    async def async_step_reauth(self, entry_data: dict[str, Any]) -> ConfigFlowResult:
        return await self.async_step_reauth_confirm()

    async def async_step_reauth_confirm(
        self, user_input: dict[str, Any] | None = None
    ) -> ConfigFlowResult:
        entry = self._get_reauth_entry()
        errors: dict[str, str] = {}
        if user_input is not None:
            token = user_input[CONF_TOKEN].strip()
            if await _try_validate(
                self.hass, entry.data[CONF_HOST], entry.data[CONF_PORT], token, errors
            ) is not None:
                return self.async_update_reload_and_abort(entry, data_updates={CONF_TOKEN: token})
        return self.async_show_form(
            step_id="reauth_confirm",
            data_schema=vol.Schema({vol.Required(CONF_TOKEN): TOKEN_SELECTOR}),
            errors=errors,
            description_placeholders={"name": entry.title},
        )

    async def async_step_reconfigure(
        self, user_input: dict[str, Any] | None = None
    ) -> ConfigFlowResult:
        """Change host, port or token (for example after the PC's IP changed)."""
        entry = self._get_reconfigure_entry()
        errors: dict[str, str] = {}
        if user_input is not None:
            host = user_input[CONF_HOST].strip()
            port = int(user_input[CONF_PORT])
            token = user_input[CONF_TOKEN].strip()
            if await _try_validate(self.hass, host, port, token, errors) is not None:
                return self.async_update_reload_and_abort(
                    entry, data_updates={CONF_HOST: host, CONF_PORT: port, CONF_TOKEN: token}
                )
        return self.async_show_form(
            step_id="reconfigure",
            data_schema=_connect_schema(user_input or dict(entry.data)),
            errors=errors,
        )

    @staticmethod
    @callback
    def async_get_options_flow(config_entry) -> PcControlOptionsFlow:
        return PcControlOptionsFlow()


class PcControlOptionsFlow(OptionsFlow):
    """Poll interval, wake timeout and WOL settings."""

    async def async_step_init(self, user_input: dict[str, Any] | None = None) -> ConfigFlowResult:
        errors: dict[str, str] = {}
        current = {**self.config_entry.data, **self.config_entry.options}

        if user_input is not None:
            try:
                mac = format_mac(user_input[CONF_MAC])
            except ValueError:
                errors[CONF_MAC] = "invalid_mac"
            else:
                return self.async_create_entry(
                    data={
                        CONF_POLL_INTERVAL: int(user_input[CONF_POLL_INTERVAL]),
                        CONF_WAKE_TIMEOUT: int(user_input[CONF_WAKE_TIMEOUT]),
                        CONF_MAC: mac,
                        CONF_BROADCAST: user_input[CONF_BROADCAST].strip(),
                        CONF_WOL_PORT: int(user_input[CONF_WOL_PORT]),
                    }
                )

        schema = vol.Schema(
            {
                vol.Required(
                    CONF_POLL_INTERVAL, default=current.get(CONF_POLL_INTERVAL, DEFAULT_POLL_INTERVAL)
                ): NumberSelector(
                    NumberSelectorConfig(min=2, max=300, step=1, unit_of_measurement="s", mode=NumberSelectorMode.BOX)
                ),
                vol.Required(
                    CONF_WAKE_TIMEOUT, default=current.get(CONF_WAKE_TIMEOUT, DEFAULT_WAKE_TIMEOUT)
                ): NumberSelector(
                    NumberSelectorConfig(min=30, max=900, step=1, unit_of_measurement="s", mode=NumberSelectorMode.BOX)
                ),
                vol.Required(CONF_MAC, default=current.get(CONF_MAC, "")): str,
                vol.Required(CONF_BROADCAST, default=current.get(CONF_BROADCAST, DEFAULT_BROADCAST)): str,
                vol.Required(CONF_WOL_PORT, default=current.get(CONF_WOL_PORT, DEFAULT_WOL_PORT)): PORT_SELECTOR,
            }
        )
        return self.async_show_form(step_id="init", data_schema=schema, errors=errors)
