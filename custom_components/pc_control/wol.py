"""Wake-on-LAN magic packets and MAC helpers."""

from __future__ import annotations

import re
import socket

_HEX = re.compile(r"[^0-9a-fA-F]")


def normalize_mac(mac: str) -> str:
    """'AA:BB:CC:DD:EE:FF' / 'aa-bb-...' / 'aabb.ccdd.eeff' -> 'aabbccddeeff'. Raises ValueError."""
    digits = _HEX.sub("", mac or "").lower()
    if len(digits) != 12:
        raise ValueError(f"Invalid MAC address: {mac!r}")
    return digits


def format_mac(mac: str) -> str:
    """-> 'aa:bb:cc:dd:ee:ff'."""
    digits = normalize_mac(mac)
    return ":".join(digits[i : i + 2] for i in range(0, 12, 2))


def magic_packet(mac: str) -> bytes:
    """6 x 0xFF followed by the MAC 16 times."""
    return b"\xff" * 6 + bytes.fromhex(normalize_mac(mac)) * 16


def send_magic_packet(mac: str, broadcast: str, port: int) -> None:
    """Send one magic packet (blocking; run in an executor)."""
    packet = magic_packet(mac)
    with socket.socket(socket.AF_INET, socket.SOCK_DGRAM) as sock:
        sock.setsockopt(socket.SOL_SOCKET, socket.SO_BROADCAST, 1)
        sock.sendto(packet, (broadcast, port))
