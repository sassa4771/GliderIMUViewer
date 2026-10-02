#!/usr/bin/env python3
"""serial_udp_bridge.py - forward glider telemetry lines from a serial port to UDP.

Reads the HDR / DAT / LOG text lines that the ground-side ESP32 (child_uart_bridge) or the
XIAO itself prints on the serial port and sends every line unchanged as one UDP datagram
(no trailing newline). The last HDR line is remembered and sent again every --hdr-interval
seconds, so a receiver that starts late or misses a datagram still learns the field names.
If the port disappears (USB unplugged) the bridge keeps retrying until it comes back.

Requires pyserial (pip install pyserial), Python >= 3.10.

Examples:
  python serial_udp_bridge.py --list
  python serial_udp_bridge.py --serial COM5
  python serial_udp_bridge.py --serial COM5 --host 192.168.0.20 --port 9000 --print

Note: a COM port can be opened by one program only. Close viewer_serialsend.py /
logger_gui.py / the Arduino serial monitor before starting this bridge.
"""
from __future__ import annotations

import argparse
import socket
import sys
import time

PREFIXES = (b"HDR,", b"DAT,", b"LOG,")
MAX_LINE = 1024          # longest line kept; the firmware never sends more than ~400 bytes


class LineBridge:
    """Splits a byte stream into lines and forwards them as UDP datagrams."""

    def __init__(self, sock: socket.socket, dest: tuple, hdr_interval: float = 2.0,
                 all_lines: bool = False, echo: bool = False) -> None:
        self.sock, self.dest = sock, dest
        self.hdr_interval, self.all_lines, self.echo = hdr_interval, all_lines, echo
        self.buf = bytearray()
        self.last_hdr: bytes | None = None
        self.next_hdr = 0.0
        self.data_since_hdr = False
        self.counts = {"HDR": 0, "DAT": 0, "LOG": 0, "other": 0, "hdr_resent": 0, "send_errors": 0}

    def feed(self, chunk: bytes) -> None:
        """Add received bytes and forward every complete line (the firmware ends lines with \\r\\n)."""
        self.buf += chunk
        while (cut := self.buf.find(b"\n")) >= 0:
            line = bytes(self.buf[:cut]).strip()
            del self.buf[:cut + 1]
            if line:
                self.on_line(line)
        if len(self.buf) > MAX_LINE:         # noise without line ends: do not grow forever
            self.buf.clear()

    def on_line(self, line: bytes) -> None:
        kind = line[:4]
        if kind in PREFIXES:
            self.counts[kind[:3].decode()] += 1
            if kind == b"HDR,":
                self.last_hdr = line
                self.next_hdr = time.monotonic() + self.hdr_interval
            else:
                self.data_since_hdr = True
        else:
            self.counts["other"] += 1
            if not self.all_lines:
                return
        self.send(line)

    def tick(self) -> None:
        """Call regularly: re-sends the cached HDR line when it is due and data is flowing."""
        if self.last_hdr is None or self.hdr_interval <= 0 or time.monotonic() < self.next_hdr:
            return
        self.next_hdr = time.monotonic() + self.hdr_interval
        if self.data_since_hdr:              # stay silent while the serial link is silent
            self.data_since_hdr = False
            self.counts["hdr_resent"] += 1
            self.send(self.last_hdr)

    def total_lines(self) -> int:
        return sum(self.counts[k] for k in ("HDR", "DAT", "LOG", "other"))

    def send(self, line: bytes) -> None:
        if self.echo:
            print(line.decode("utf-8", errors="backslashreplace"))
        try:
            self.sock.sendto(line, self.dest)
        except OSError as exc:               # e.g. network unreachable; keep the bridge alive
            self.counts["send_errors"] += 1
            if self.counts["send_errors"] in (1, 100, 10000):
                print(f"[udp] send failed: {exc}", file=sys.stderr)


def pump(ser, bridge: LineBridge, stats_interval: float = 5.0, stop=lambda: False) -> None:
    """Read from an open pyserial port until it fails or stop() returns True."""
    next_stats = time.monotonic() + stats_interval
    last_total = bridge.total_lines()
    while not stop():
        chunk = ser.read(ser.in_waiting or 1)        # returns after the port timeout at the latest
        if chunk:
            bridge.feed(chunk)
        bridge.tick()
        if stats_interval > 0 and time.monotonic() >= next_stats:
            c, total = bridge.counts, bridge.total_lines()
            print(f"[stat] DAT={c['DAT']} HDR={c['HDR']} (+{c['hdr_resent']} re-sent) LOG={c['LOG']} "
                  f"other={c['other']}  {(total - last_total) / stats_interval:.1f} lines/s")
            last_total = total
            next_stats = time.monotonic() + stats_interval


def open_udp(host: str, port: int) -> tuple[socket.socket, tuple]:
    family, _, _, _, dest = socket.getaddrinfo(host, port, type=socket.SOCK_DGRAM)[0]
    sock = socket.socket(family, socket.SOCK_DGRAM)
    if family == socket.AF_INET:
        sock.setsockopt(socket.SOL_SOCKET, socket.SO_BROADCAST, 1)
    return sock, dest


class HelpFormatter(argparse.ArgumentDefaultsHelpFormatter, argparse.RawDescriptionHelpFormatter):
    """Keep the layout of the module docstring and show default values."""


def main(argv: list[str] | None = None) -> int:
    p = argparse.ArgumentParser(description=__doc__, formatter_class=HelpFormatter)
    p.add_argument("--list", action="store_true", help="list serial ports and exit")
    p.add_argument("--serial", metavar="PORT", help="serial port (COM5, /dev/ttyUSB0) or a pyserial URL")
    p.add_argument("--baud", type=int, default=115200, help="serial baud rate")
    p.add_argument("--host", default="127.0.0.1", help="receiver address (the PC running Unity)")
    p.add_argument("--port", type=int, default=9000, help="receiver UDP port")
    p.add_argument("--hdr-interval", type=float, default=2.0, help="re-send the last HDR every N seconds (0 = off)")
    p.add_argument("--all-lines", action="store_true", help="also forward lines that are not HDR/DAT/LOG")
    p.add_argument("--retry", type=float, default=2.0, help="seconds between attempts to (re)open the port")
    p.add_argument("--stats", type=float, default=5.0, help="seconds between status lines (0 = off)")
    p.add_argument("--print", dest="echo", action="store_true", help="print every forwarded line")
    args = p.parse_args(argv)
    for stream in (sys.stdout, sys.stderr):              # never die on a console that is not UTF-8
        if hasattr(stream, "reconfigure"):
            stream.reconfigure(errors="backslashreplace")

    try:
        import serial
        from serial.tools import list_ports
    except ImportError:
        print("pyserial is required:  pip install pyserial", file=sys.stderr)
        return 1

    if args.list or not args.serial:
        ports = list(list_ports.comports())
        for port in ports:
            print(f"{port.device}\t{port.description}")
        if not ports:
            print("(no serial ports found)")
        if not args.list:
            print("choose one with --serial PORT", file=sys.stderr)
        return 0 if args.list else 2

    try:
        sock, dest = open_udp(args.host, args.port)
    except OSError as exc:
        print(f"cannot use {args.host}:{args.port}: {exc}", file=sys.stderr)
        return 2
    bridge = LineBridge(sock, dest, args.hdr_interval, args.all_lines, args.echo)
    print(f"[udp] forwarding to {dest[0]}:{dest[1]}  (Ctrl+C to stop)")
    try:
        while True:
            ser = None
            try:
                ser = serial.serial_for_url(args.serial, args.baud, timeout=0.2)
                print(f"[serial] connected to {args.serial} @ {args.baud}")
                bridge.buf.clear()
                pump(ser, bridge, args.stats)
            except ValueError as exc:        # unknown URL scheme, bad baud rate: retrying will not help
                print(f"[serial] {exc}", file=sys.stderr)
                return 2
            except (serial.SerialException, OSError) as exc:
                print(f"[serial] {exc}")
            finally:
                if ser is not None:
                    try:
                        ser.close()
                    except Exception:        # noqa: BLE001 - closing a vanished port may fail
                        pass
            print(f"[serial] retrying in {args.retry:g} s")
            time.sleep(args.retry)
    except KeyboardInterrupt:
        print("stopped")
    c = bridge.counts
    print(f"forwarded DAT={c['DAT']} HDR={c['HDR']} (+{c['hdr_resent']} re-sent) LOG={c['LOG']}, "
          f"skipped/other={c['other']}, send errors={c['send_errors']}")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
