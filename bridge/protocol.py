"""CyberBeast Classic CAN 编解码及有界 SLCAN 传输；导入不打开设备。"""

from dataclasses import dataclass
import math
import struct
import time

HOST = 127
NODES = (1, 2, 3)


def finite(value):
    if (
        isinstance(value, bool)
        or not isinstance(value, (float, int))
        or not math.isfinite(value)
    ):
        raise ValueError("必须输入有限数值")
    return float(value)


def make_id(mt, dest, src=HOST, seq=0, priority=5):
    if not (
        0 <= mt <= 255
        and 0 <= dest <= 255
        and 0 <= src <= 255
        and 0 <= seq < 4
    ):
        raise ValueError("CAN ID 字段越界")
    return (
        ((priority & 7) << 26) | (mt << 18) | (dest << 10) | (src << 2) | seq
    )


@dataclass(frozen=True)
class Frame:
    can_id: int
    data: bytes

    @property
    def mt(self):
        return (self.can_id >> 18) & 255

    @property
    def src(self):
        return (self.can_id >> 2) & 255

    @property
    def dest(self):
        return (self.can_id >> 10) & 255

    @property
    def seq(self):
        return self.can_id & 3


def position_payload(degrees, rpm, amps):
    degrees, rpm, amps = map(finite, (degrees, rpm, amps))
    if not (1 <= rpm <= 32767 and rpm.is_integer()):
        raise ValueError("Classic CAN 限速须为正整数 RPM，0 不是停止")
    deci = math.floor(amps * 10 + 1e-9)
    if not (1 <= deci <= 32767):
        raise ValueError("电流限值至少 0.1A，按 0.1A 向下量化")
    return struct.pack(">fhh", degrees, int(rpm), deci)


def estop_destination(node):
    if node not in NODES:
        raise ValueError("只允许 ID1/2/3")
    # cb_is_broadcast 对 >=0x80 的所有消息按 Dest 位掩码匹配。
    return 1 << node


def decode_mit(data, params):
    if len(data) != 8:
        raise ValueError("MIT 响应长度应为8")
    pmax = max(0, params["config.p_max"]) or 1.0
    vmax = max(0, params["config.v_max"]) or 1.0
    imax = max(0, params["config.i_max"] * params["config.gr"]) or 1.0
    p = int.from_bytes(data[:2], "big")
    v = (data[2] << 4) | (data[3] >> 4)
    i = (data[4] << 4) | (data[5] >> 4)
    return {
        "position": math.degrees(p * 2 * pmax / 65535 - pmax),
        "velocity": math.degrees(v * 2 * vmax / 4095 - vmax),
        "current": i * 2 * imax / 4095 - imax,
        "error": data[3] & 15,
        "mode": data[5] & 15,
        "motorTemp": data[6] - 50,
        "driverTemp": data[7] - 50,
    }


def decode_pos_vel(data, gr):
    if len(data) != 8 or finite(gr) <= 0:
        raise ValueError("位置反馈长度或减速比非法")
    p, v = struct.unpack(">ff", data)
    return finite(p) * 360 / gr, finite(v) * 360 / gr


class SlcanParser:
    def __init__(self):
        self.buffer = bytearray()
        self.bad = 0

    def feed(self, chunk):
        self.buffer.extend(chunk)
        if len(self.buffer) > 65536:
            self.buffer.clear()
            raise ValueError("SLCAN 接收缓冲溢出")
        frames = []
        while b"\r" in self.buffer:
            raw, _, rest = self.buffer.partition(b"\r")
            self.buffer = bytearray(rest)
            raw = raw.strip()
            if not raw:
                continue
            try:
                if raw[:1] != b"T":
                    self.bad += 1
                    continue
                if len(raw) < 10:
                    raise ValueError()
                can_id, dlc = int(raw[1:9], 16), int(raw[9:10], 16)
                if can_id > 0x1FFFFFFF or dlc > 8 or len(raw) != 10 + 2 * dlc:
                    raise ValueError()
                data = bytes.fromhex(raw[10:].decode("ascii"))
                frames.append(Frame(can_id, data))
            except (ValueError, UnicodeError):
                self.bad += 1
        return frames


class SlcanBus:
    def __init__(self, port, serial_factory=None):
        if serial_factory is None:
            import serial

            serial_factory = serial.Serial
        self.ser = serial_factory(port, 115200, timeout=0, write_timeout=0.015)
        self.parser = SlcanParser()
        self.seq = 0
        self.rx = 0
        self.tx = 0
        self.closed = False
        try:
            # 配置期间无已使能节点，不发送任何运动帧。
            for command in ("C", "S8", "M0", "A0", "O"):
                self._write((command + "\r").encode("ascii"))
                time.sleep(0.025)
            self.ser.reset_input_buffer()
        except Exception:
            self.ser.close()
            raise

    def _write(self, data):
        if self.ser.write(data) != len(data):
            raise OSError("串口写入不完整")

    def send(self, mt, node, data=b"", priority=5):
        if self.closed or node not in NODES or len(data) > 8:
            raise ValueError("总线关闭或帧参数非法")
        dest = estop_destination(node) if mt == 0xC0 else node
        seq = self.seq
        self.seq = (seq + 1) & 3
        ident = make_id(mt, dest, seq=seq, priority=priority)
        self._write(
            f"T{ident:08X}{len(data):X}{data.hex().upper()}\r".encode("ascii")
        )
        self.tx += 1
        return seq

    def poll(self):
        count = min(self.ser.in_waiting, 65536)
        frames = self.parser.feed(self.ser.read(count)) if count else []
        self.rx += len(frames)
        return frames

    def discard_pending_output(self):
        self.ser.reset_output_buffer()

    def close(self):
        if not self.closed:
            self.closed = True
            try:
                self._write(b"C\r")
            finally:
                self.ser.close()
