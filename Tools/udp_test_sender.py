#!/usr/bin/env python3
"""udp_test_sender.py - send synthetic or recorded glider attitude telemetry over UDP.

Test source for the GliderIMUViewer "RealtimePreview" scene. Standard library only,
Python >= 3.10. One message per datagram, UTF-8 text unless noted.

Formats (--format):
  dat   HDR,1,GLDR,fields=<names>          re-sent every --hdr-interval seconds
        DAT,<seq>,<t_ms>,<v0>,<v1>,...      same text the ground ESP32 prints on serial
        LOG,<text>                          every --log-interval seconds
  json  {"seq":1,"t_ms":1234,"roll":1.5,"pitch":-2.25,"yaw":180.0,...}   and   {"log":"text"}
  csv   roll,pitch,yaw
  osc   OSC 1.0 message "/plane/data" with float32 args roll pitch yaw sv1 sv3 (binary;
        byte-identical to python-osc's SimpleUDPClient.send_message)

Examples:
  python udp_test_sender.py                                  synthetic motion, DAT lines, 30 Hz
  python udp_test_sender.py --format osc --rate 50
  python udp_test_sender.py --csv flight.csv --loop --format json
  python udp_test_sender.py --loss 0.1 --jitter-ms 30 --burst 3   simulate a bad link
"""
from __future__ import annotations

import argparse
import csv
import json
import math
import random
import socket
import struct
import sys
import time
from collections.abc import Iterable, Iterator
from typing import NamedTuple

FIRMWARE_FIELDS = ["dt_ms", "ax", "ay", "az", "gx", "gy", "gz", "roll", "pitch", "yaw", "s0", "s1", "s2"]
PARAM_LOG = "Param:Pp=2.00,Dp=3.00,Ip=0.00,Pr=2.00,Dr=3.00,Ir=0.00,G=0,T0=85,T1=105,T2=70"


class Sample(NamedTuple):
    t_rel: float            # seconds since the first sample (pacing)
    seq: int
    t_ms: float             # sender clock in milliseconds
    fields: dict[str, float]


# --------------------------------------------------------------------------- OSC 1.0
def osc_string(text: str) -> bytes:
    """UTF-8 bytes + 1..4 NUL bytes so that the length is a multiple of 4."""
    raw = text.encode("utf-8")
    return raw + b"\x00" * (4 - len(raw) % 4)


def osc_message(address: str, args: Iterable[object]) -> bytes:
    """Encode one OSC message. float -> 'f' (big-endian float32), int -> 'i' (or 'h' if it
    needs more than 32 bits), str -> 's', True/False/None -> 'T'/'F'/'N' (no data bytes)."""
    tags, data = [","], []
    for arg in args:
        if arg is True:
            tags.append("T")
        elif arg is False:
            tags.append("F")
        elif arg is None:
            tags.append("N")
        elif isinstance(arg, int):
            if arg.bit_length() > 31:
                tags.append("h")
                data.append(struct.pack(">q", arg))
            else:
                tags.append("i")
                data.append(struct.pack(">i", arg))
        elif isinstance(arg, float):
            tags.append("f")
            data.append(struct.pack(">f", arg))
        elif isinstance(arg, str):
            tags.append("s")
            data.append(osc_string(arg))
        else:
            raise TypeError(f"unsupported OSC argument type: {type(arg).__name__}")
    return osc_string(address) + osc_string("".join(tags)) + b"".join(data)


def osc_bundle(packets: Iterable[bytes], timetag: int = 1) -> bytes:
    """'#bundle\\0' + 64-bit NTP timetag (1 = immediately) + (int32 size + packet) per element."""
    out = b"#bundle\x00" + struct.pack(">Q", timetag)
    for packet in packets:
        out += struct.pack(">i", len(packet)) + packet
    return out


# --------------------------------------------------------------------------- sources
def servo(angle: float) -> float:
    return max(15.0, min(165.0, angle))                  # firmware clamps servo angles to 15..165


def synthetic(rate: float) -> Iterator[Sample]:
    """Smooth glider-like motion: a slow banked circle with gentle pitch oscillation.
    Yaw follows the coordinated-turn rate g/V*tan(roll) and wraps at 0/360 like the firmware."""
    dt = 1.0 / rate
    yaw = 350.0                     # start near the wrap so the receiver's unwrap is exercised
    prev_roll = prev_pitch = None
    n = 0
    while True:
        t = n * dt
        roll = 12.0 + 20.0 * math.sin(2 * math.pi * t / 10.0) + 3.0 * math.sin(2 * math.pi * t / 1.7)
        pitch = -4.0 + 6.0 * math.sin(2 * math.pi * t / 6.0 + 0.7)
        yaw_rate = math.degrees(9.81 / 12.0 * math.tan(math.radians(roll)))   # deg/s at 12 m/s
        yaw = (yaw + yaw_rate * dt) % 360.0
        gx = 0.0 if prev_roll is None else (roll - prev_roll) / dt
        gy = 0.0 if prev_pitch is None else (pitch - prev_pitch) / dt
        prev_roll, prev_pitch = roll, pitch
        r, p = math.radians(roll), math.radians(pitch)
        fields = {
            "dt_ms": dt * 1000.0,
            "ax": -math.sin(p), "ay": math.sin(r) * math.cos(p), "az": math.cos(r) * math.cos(p),
            "gx": gx, "gy": gy, "gz": yaw_rate,
            "roll": roll, "pitch": pitch, "yaw": yaw,
            "s0": servo(85.0 - 0.5 * roll), "s1": servo(105.0 - 0.5 * roll), "s2": servo(70.0 + pitch),
        }
        n += 1
        yield Sample(t, n, t * 1000.0, fields)


def load_csv(path: str) -> tuple[list[str], list[tuple[float | None, float | None, dict[str, float]]]]:
    """Return (field names, rows). Rows that are not fully numeric (for example the header
    lines some loggers repeat in the middle of a file) are skipped."""
    with open(path, newline="", encoding="utf-8-sig") as f:
        reader = csv.reader(f)
        header = [h.strip() for h in next(reader)]
        lower = [h.lower() for h in header]
        seq_col = next((i for i, h in enumerate(lower) if h in ("src_seq", "seq")), None)
        t_col = next((i for i, h in enumerate(lower) if h == "t_ms"), None)
        names = [h for i, h in enumerate(lower) if i not in (seq_col, t_col)]
        for need in ("roll", "pitch", "yaw"):
            if need not in names:
                raise SystemExit(f"{path}: column '{need}' not found (header: {','.join(header)})")
        rows = []
        for cells in reader:
            try:
                values = [float(c) for c in cells]
            except ValueError:
                continue
            if len(values) != len(header):
                continue
            if any(c is not None and not math.isfinite(values[c]) for c in (seq_col, t_col)):
                continue
            fields = {h: v for i, (h, v) in enumerate(zip(lower, values, strict=True)) if i not in (seq_col, t_col)}
            rows.append((None if seq_col is None else values[seq_col],
                         None if t_col is None else values[t_col], fields))
    if not rows:
        raise SystemExit(f"{path}: no numeric data rows")
    return names, rows


def csv_replay(rows: list, rate: float, speed: float, loop: bool) -> Iterator[Sample]:
    """Replay rows paced by their t_ms column (or at --rate when there is none).
    seq and t_ms keep increasing across loops so the receiver sees one continuous stream."""
    step = 1.0 / rate
    first_seq = int(rows[0][0]) if rows[0][0] is not None else 1
    first_t = rows[0][1] if rows[0][1] is not None else 0.0
    t_rel = 0.0
    seq_base, t_base = 0, 0.0
    started = False
    while True:
        prev_t = None
        last_seq, last_t = 0, 0.0
        for i, (seq, t_ms, fields) in enumerate(rows):
            if not started:
                delta, started = 0.0, True
            elif i == 0 or t_ms is None:                 # loop boundary / no time column
                delta = step
            else:
                delta = (t_ms - prev_t) / 1000.0
                if not 0.0 < delta <= 5.0:               # clock went backwards or a long gap
                    delta = step
            prev_t = t_ms
            t_rel += delta / speed
            last_seq = seq_base + (int(seq) if seq is not None else i + 1)
            last_t = t_base + (t_ms if t_ms is not None else i * step * 1000.0)
            yield Sample(t_rel, last_seq, last_t, fields)
        if not loop:
            return
        seq_base = last_seq - first_seq + 1
        t_base = last_t - first_t + step * 1000.0


# --------------------------------------------------------------------------- encoders
def fmt_num(value: float) -> str:
    return f"{value:.3f}"


def encode_sample(fmt: str, s: Sample, names: list[str], osc_address: str, bundle: bool) -> bytes:
    f = s.fields
    if fmt == "dat":
        return ",".join(["DAT", str(s.seq), str(int(round(s.t_ms)))] + [fmt_num(f[n]) for n in names]).encode()
    if fmt == "json":
        obj = {"seq": s.seq, "t_ms": int(round(s.t_ms))}
        obj.update((n, round(f[n], 3)) for n in names)
        return json.dumps(obj, separators=(",", ":")).encode()   # like any Python sender: nan -> NaN
    if fmt == "csv":
        return ",".join(fmt_num(f[n]) for n in ("roll", "pitch", "yaw")).encode()
    if fmt == "osc":
        args = [float(f["roll"]), float(f["pitch"]), float(f["yaw"])]
        for a, b in (("sv1", "s0"), ("sv3", "s2")):      # same aliasing as viewer_serialsend.py
            v = f.get(a, f.get(b))
            if v is not None:
                args.append(float(v))
        msg = osc_message(osc_address, args)
        return osc_bundle([msg]) if bundle else msg
    raise ValueError(fmt)


def encode_log(fmt: str, text: str) -> bytes | None:
    if fmt == "dat":
        return f"LOG,{text}".encode()
    if fmt == "json":
        return json.dumps({"log": text}, separators=(",", ":")).encode()
    return None                                          # csv / osc carry no log channel


# --------------------------------------------------------------------------- link
class Link:
    """UDP socket with optional impairments: random loss, random delay, back-to-back bursts."""

    def __init__(self, host: str, port: int, loss: float, jitter_ms: float, burst: int, rng: random.Random):
        family, _, _, _, self.dest = socket.getaddrinfo(host, port, type=socket.SOCK_DGRAM)[0]
        self.sock = socket.socket(family, socket.SOCK_DGRAM)
        if family == socket.AF_INET:
            self.sock.setsockopt(socket.SOL_SOCKET, socket.SO_BROADCAST, 1)   # allow x.x.x.255
        self.loss, self.jitter_ms, self.burst, self.rng = loss, jitter_ms, max(1, burst), rng
        self.pending: list[bytes] = []
        self.sent = self.dropped = 0
        self.last = b""

    def send(self, payload: bytes | None) -> None:
        if payload is None:
            return
        if self.loss > 0.0 and self.rng.random() < self.loss:
            self.dropped += 1
            return
        self.pending.append(payload)
        if len(self.pending) >= self.burst:
            self.flush()

    def flush(self) -> None:
        if self.pending and self.jitter_ms > 0.0:
            time.sleep(self.rng.uniform(0.0, self.jitter_ms) / 1000.0)
        for payload in self.pending:
            try:
                self.sock.sendto(payload, self.dest)
                self.sent += 1
                self.last = payload
            except OSError as exc:                      # e.g. network unreachable
                print(f"[send error] {exc}", file=sys.stderr)
        self.pending.clear()


def sleep_until(deadline: float) -> None:
    """Sleep most of the way, then yield in a tight loop for the last few ms (accurate pacing)."""
    while True:
        remaining = deadline - time.perf_counter()
        if remaining <= 0.0:
            return
        time.sleep(remaining - 0.002 if remaining > 0.004 else 0.0)


def use_fine_timer() -> None:
    """Before Python 3.11, time.sleep() on Windows moves in 15.6 ms steps; ask for 1 ms steps."""
    if sys.platform == "win32" and sys.version_info < (3, 11):
        try:
            import ctypes
            ctypes.windll.winmm.timeBeginPeriod(1)
        except (ImportError, AttributeError, OSError):
            pass


def show(payload: bytes) -> str:
    if payload[:1] in (b"/", b"#"):
        return payload.hex()
    return payload.decode("utf-8", errors="backslashreplace").rstrip("\n")


# --------------------------------------------------------------------------- main
class HelpFormatter(argparse.ArgumentDefaultsHelpFormatter, argparse.RawDescriptionHelpFormatter):
    """Keep the layout of the module docstring and show default values."""


def parse_args(argv: list[str] | None = None) -> argparse.Namespace:
    p = argparse.ArgumentParser(description=__doc__, formatter_class=HelpFormatter)
    p.add_argument("--host", default="127.0.0.1",
                   help="receiver address (the PC running Unity); 'localhost' may resolve to IPv6 ::1")
    p.add_argument("--port", type=int, default=9000, help="receiver UDP port")
    p.add_argument("--format", choices=("dat", "json", "osc", "csv"), default="dat", help="payload format")
    p.add_argument("--rate", type=float, default=30.0, help="sample rate in Hz (synthetic / CSV without t_ms)")
    p.add_argument("--csv", metavar="FILE", help="replay this CSV (needs roll,pitch,yaw; uses t_ms, src_seq)")
    p.add_argument("--loop", action="store_true", help="repeat the CSV forever")
    p.add_argument("--speed", type=float, default=1.0, help="CSV replay speed factor")
    p.add_argument("--duration", type=float, default=0.0, help="stop after this many seconds (0 = never)")
    p.add_argument("--count", type=int, default=0, help="stop after this many samples (0 = never)")
    p.add_argument("--hdr-interval", type=float, default=2.0,
                   help="dat: seconds between HDR lines (0 = once at start, negative = never)")
    p.add_argument("--log-interval", type=float, default=2.0, help="dat/json: seconds between LOG lines (0 = off)")
    p.add_argument("--newline", action="store_true", help="append \\n to text payloads")
    p.add_argument("--osc-address", default="/plane/data", help="osc: address pattern")
    p.add_argument("--osc-bundle", action="store_true", help="osc: wrap every message in a #bundle")
    p.add_argument("--loss", type=float, default=0.0, help="probability (0..1) of dropping a datagram")
    p.add_argument("--jitter-ms", type=float, default=0.0, help="random extra delay per send, 0..N ms")
    p.add_argument("--burst", type=int, default=1, help="hold N datagrams and send them back to back")
    p.add_argument("--seed", type=int, default=None, help="random seed for --loss / --jitter-ms")
    p.add_argument("--print", dest="echo", action="store_true", help="print every datagram")
    p.add_argument("--quiet", action="store_true", help="no once-per-second status line")
    args = p.parse_args(argv)
    if args.rate <= 0 or args.speed <= 0:
        p.error("--rate and --speed must be > 0")
    if not 0.0 <= args.loss <= 1.0:
        p.error("--loss must be between 0 and 1")
    return args


def main(argv: list[str] | None = None) -> int:
    args = parse_args(argv)
    for stream in (sys.stdout, sys.stderr):              # never die on a console that is not UTF-8
        if hasattr(stream, "reconfigure"):
            stream.reconfigure(errors="backslashreplace")
    use_fine_timer()
    if args.csv:
        names, rows = load_csv(args.csv)
        source = csv_replay(rows, args.rate, args.speed, args.loop)
        what = f"{args.csv} ({len(rows)} rows{', looping' if args.loop else ''})"
    else:
        names, source = FIRMWARE_FIELDS, synthetic(args.rate)
        what = f"synthetic motion at {args.rate:g} Hz"
    try:
        link = Link(args.host, args.port, args.loss, args.jitter_ms, args.burst, random.Random(args.seed))
    except OSError as exc:
        raise SystemExit(f"cannot use {args.host}:{args.port}: {exc}") from exc
    tail = b"\n" if args.newline and args.format != "osc" else b""
    hdr = ("HDR,1,GLDR,fields=" + ",".join(names)).encode() if args.format == "dat" else None
    print(f"sending {what} as '{args.format}' to {link.dest[0]}:{link.dest[1]}  (Ctrl+C to stop)")

    def emit(payload: bytes | None) -> None:
        if payload is not None:
            if args.echo:
                print(show(payload))
            link.send(payload + tail)

    start = time.perf_counter()
    next_hdr = next_log = next_status = 0.0
    samples = 0
    if hdr and args.hdr_interval >= 0:
        emit(hdr)
        next_hdr = args.hdr_interval
    if args.log_interval > 0:
        emit(encode_log(args.format, "mode:Manual"))
    try:
        for sample in source:
            sleep_until(start + sample.t_rel)
            now = time.perf_counter() - start
            if args.duration > 0 and now >= args.duration:
                break
            if hdr and args.hdr_interval > 0 and now >= next_hdr:
                emit(hdr)
                next_hdr = now + args.hdr_interval
            if args.log_interval > 0 and now >= next_log:
                emit(encode_log(args.format, PARAM_LOG))
                next_log = now + args.log_interval
            emit(encode_sample(args.format, sample, names, args.osc_address, args.osc_bundle))
            samples += 1
            if not args.quiet and not args.echo and now >= next_status:
                print(f"[{now:6.1f}s] samples={samples} sent={link.sent} dropped={link.dropped}  last: {show(link.last)[:90]}")
                next_status = now + 1.0
            if args.count and samples >= args.count:
                break
    except KeyboardInterrupt:
        print("stopped")
    link.flush()
    elapsed = time.perf_counter() - start
    print(f"done: {samples} samples, {link.sent} datagrams sent, {link.dropped} dropped, "
          f"{elapsed:.1f} s ({samples / elapsed if elapsed > 0 else 0:.1f} samples/s)")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
