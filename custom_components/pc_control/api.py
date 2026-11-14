"""Client for the PC Control Agent REST API.

This module only depends on aiohttp so it can be exercised outside Home Assistant.
"""

from __future__ import annotations

import asyncio
from typing import Any

import aiohttp

API_PREFIX = "/api/v1"
DEFAULT_TIMEOUT = 5


class PcControlError(Exception):
    """Base error."""


class CannotConnect(PcControlError):
    """The agent could not be reached."""


class InvalidAuth(PcControlError):
    """The API token was rejected (401)."""


class ApiError(PcControlError):
    """The agent answered with an error status."""

    def __init__(self, status: int, code: str, message: str) -> None:
        super().__init__(f"{status} {code}: {message}")
        self.status = status
        self.code = code
        self.message = message


class PcControlApi:
    """Thin async wrapper around the agent's endpoints."""

    def __init__(self, session: aiohttp.ClientSession, host: str, port: int, token: str) -> None:
        self._session = session
        self._host = host
        self._port = port
        self._token = token

    @property
    def base_url(self) -> str:
        host = f"[{self._host}]" if ":" in self._host and not self._host.startswith("[") else self._host
        return f"http://{host}:{self._port}{API_PREFIX}"

    async def _request(
        self, method: str, path: str, *, auth: bool = True, timeout: float = DEFAULT_TIMEOUT
    ) -> tuple[int, Any]:
        headers = {"Accept": "application/json"}
        if auth:
            headers["Authorization"] = f"Bearer {self._token}"
        try:
            async with self._session.request(
                method,
                f"{self.base_url}{path}",
                headers=headers,
                timeout=aiohttp.ClientTimeout(total=timeout),
            ) as resp:
                try:
                    body = await resp.json(content_type=None)
                except (aiohttp.ContentTypeError, ValueError):
                    body = None
                status = resp.status
        except (aiohttp.ClientError, asyncio.TimeoutError, OSError) as err:
            raise CannotConnect(str(err) or type(err).__name__) from err

        if status == 401:
            raise InvalidAuth("Invalid API token")
        if status >= 400:
            body = body if isinstance(body, dict) else {}
            raise ApiError(status, str(body.get("error", "error")), str(body.get("message", "")))
        return status, body

    async def health(self, timeout: float = 3) -> dict[str, Any]:
        """GET /health (no auth)."""
        _, body = await self._request("GET", "/health", auth=False, timeout=timeout)
        return body or {}

    async def info(self) -> dict[str, Any]:
        """GET /info."""
        _, body = await self._request("GET", "/info")
        return body or {}

    async def entities(self) -> dict[str, Any]:
        """GET /entities."""
        _, body = await self._request("GET", "/entities")
        return body or {"revision": 0, "entities": []}

    async def turn(self, entity_id: str, on: bool) -> dict[str, Any]:
        """POST /entities/{id}/on|off. A stop may take up to ~3 s to answer."""
        _, body = await self._request(
            "POST", f"/entities/{entity_id}/{'on' if on else 'off'}", timeout=10
        )
        return body or {}

    async def rescan(self) -> dict[str, Any]:
        """POST /rescan."""
        _, body = await self._request("POST", "/rescan", timeout=60)
        return body or {}

    async def shutdown(self) -> dict[str, Any]:
        """POST /power/shutdown."""
        _, body = await self._request("POST", "/power/shutdown")
        return body or {}
