"""只读 CAN 端点适配；复用原厂 Device 的索引、类型和 CRC，不发送 USB 包。"""

import importlib.util
import json
from pathlib import Path
import struct
import time

from protocol import HOST, NODES

# 显式位置准备仅允许这两个固定 RAM 值；不开放通用写入或保存动作。
POSITION_SETUP = {
    "config.enable_vel_limit": ("bool", True),
    "config.input_mode": ("int32", 1),
}

ROOT = Path(__file__).resolve().parent.parent
VENDOR = ROOT / "vendor" / "mc_ep.py"
if not VENDOR.exists():
    VENDOR = ROOT.parent / "robot_mc-CBA03_v4.2.89" / "tools" / "mc_ep.py"
spec = importlib.util.spec_from_file_location("cba03_vendor_mc_ep", VENDOR)
mc_ep = importlib.util.module_from_spec(spec)
spec.loader.exec_module(mc_ep)


class NodeReadTimeout(TimeoutError):
    """单节点应答超时；与串口写超时等总线故障区分。"""


class Descriptor:
    """不把缺片补零；完整位图及重复分片一致性检查。"""

    def __init__(self):
        self.total = None
        self.crc = None
        self.parts = {}

    def feed(self, data):
        if len(data) != 8:
            raise ValueError("描述符必须是 Classic 8 字节帧")
        off = int.from_bytes(data[:2], "little")
        if self.total is None:
            total, crc = struct.unpack("<IH", data[2:])
            if off != 0 or not 2 <= total <= 65535:
                raise ValueError("描述符元数据非法或缺失")
            self.total, self.crc = total, crc
            return
        if off >= self.total or off % 6:
            raise ValueError("描述符偏移非法")
        part = data[2 : 2 + min(6, self.total - off)]
        if off in self.parts and self.parts[off] != part:
            raise ValueError("描述符分片冲突")
        self.parts[off] = part

    def finish(self):
        if self.total is None or any(
            i not in self.parts for i in range(0, self.total, 6)
        ):
            raise ValueError("描述符缺片")
        raw = b"".join(self.parts[i] for i in range(0, self.total, 6))
        if mc_ep.crc16(raw) != self.crc:
            raise ValueError("描述符 CRC16 错误")
        result = json.loads(raw.decode("ascii"))
        if not isinstance(result, list) or not result:
            raise ValueError("描述符结构非法")
        names, ids = set(), set()
        for ep in result:
            if (
                not isinstance(ep, dict)
                or not isinstance(ep.get("name"), str)
                or type(ep.get("id")) is not int
                or not 1 <= ep["id"] <= 65535
                or ep["name"] in names
                or ep["id"] in ids
            ):
                raise ValueError("描述符索引非法或重复")
            names.add(ep["name"])
            ids.add(ep["id"])
        return result


class PositionPreparation:
    """Engine tick 驱动的单轴 RAM 任务；每次至多一个端点事务在途。"""

    def __init__(self, engine, axis):
        from motion import REQUIRED
        self.engine, self.axis = engine, axis
        self.session, self.token = engine.session, axis.control_token
        self.started = engine.clock()
        self.stage = "核验必需参数"
        self.required = [name for name in REQUIRED if not name.startswith("telemetry.")]
        self.operations = [("read", name) for name in self.required]
        self.waiting = None
        self.raw = bytearray()
        self.offset = 0
        self.cancel_reason = ""
        self.done, self.error = False, None
        self.written = []
        self.completed_transactions = 0
        self.terminal_diagnostics = None

    def diagnostics(self):
        if self.terminal_diagnostics is not None:
            return dict(self.terminal_diagnostics)
        now, w = self.engine.clock(), self.waiting
        kind, name = self.operations[0] if self.operations else (None, None)
        return {"stage": self.stage, "elapsedMs": max(0, now - self.started) * 1000,
                "completedTransactions": self.completed_transactions,
                "pendingOperations": len(self.operations), "operation": kind, "endpoint": name,
                "offset": self.offset, "waiting": w is not None,
                "transactionAgeMs": max(0, now - w["issued"]) * 1000 if w else None,
                "writtenEndpoints": list(self.written), "done": self.done}

    def cancel(self, reason):
        self.cancel_reason = reason
        if self.waiting is None:
            self.finish(InterruptedError(reason))

    def finish(self, error=None):
        if self.done:
            return
        terminal = self.diagnostics()
        self.done, self.error = True, error
        self.waiting = None
        self.operations.clear()
        self.raw, self.offset = bytearray(), 0
        a = self.axis
        if error:
            a.errors = ["位置准备未完成，须重新核验：" + str(error)]
            self.stage = "已取消" if isinstance(error, InterruptedError) else "准备失败"
        else:
            from motion import validate_parameters
            a.errors = validate_parameters(a.params)
            a.reset_stationary(self.engine.clock())
            self.stage = "准备完成"
        a.preparation_stage = self.stage
        a.local_cancel_reason = str(error) if error else ""
        terminal.update(done=True, result=self.stage, error=str(error) if error else "")
        self.terminal_diagnostics = a.preparation_diagnostics = terminal

    def identity_guard(self):
        e, a = self.engine, self.axis
        if (e.session != self.session or a.control_token != self.token or e.cancelled()):
            self.cancel("位置准备被停止或旧会话取消")
        return not self.cancel_reason

    def guard(self):
        if not self.identity_guard():
            return False
        self.engine.check_preparation(self.axis)
        return True

    def tick(self):
        if self.done:
            return
        e, a, now = self.engine, self.axis, self.engine.clock()
        try:
            if now - self.started >= 3:
                raise NodeReadTimeout("位置准备超过 3 秒；RAM 状态须重新核对")
            if self.waiting is not None:
                if now - self.waiting["issued"] > .25:
                    raise NodeReadTimeout("位置准备端点事务超时；须重连防止迟到串位")
                return
            if not self.guard() or self.done:
                return
            if not self.operations:
                if self.stage == "核验必需参数":
                    issues = e.preparation_issues(a)
                    if issues:
                        raise ValueError("；".join(issues))
                    self.stage = "RAM 白名单写入"
                    self.operations = [("write", name) for name, (_, value) in POSITION_SETUP.items()
                                       if a.params.get(name) != value]
                elif self.stage == "RAM 白名单写入":
                    self.stage = "最终读回"
                    self.operations = [("read", name) for name in POSITION_SETUP]
                else:
                    from motion import validate_parameters
                    issues = validate_parameters(a.params)
                    if issues:
                        raise ValueError("；".join(issues))
                    self.finish()
                    e.log("位置准备完成；仅 RAM、未使能、未保存 Flash", a.node)
                    return
                a.preparation_stage = self.stage
                return
            kind, name = self.operations[0]
            ep = next((row for row in a.descriptor if row["name"] == name), None)
            if not ep or not CanDevice.readable(ep):
                raise PermissionError(name + " 缺少在线可读描述符")
            fmt, size = mc_ep.TYPE_CODES[ep["type"]]
            # 只等待同一节点/端点的旧事务；其他门极遥测不能阻止本任务推进。
            if (a.node, ep["id"]) in e.pending:
                return
            if kind == "write":
                CanDevice.position_setup_entries(a.descriptor)
                typ, value = POSITION_SETUP[name]
                raw = struct.pack(mc_ep.TYPE_CODES[typ][0], value)
                a.params[name] = None
                a.parameter_times.pop(name, None)
                payload = struct.pack(">BHB", 0, ep["id"], len(raw)) + raw
                seq = e.bus.send(0x21, a.node, payload)
                self.written.append(name)
            else:
                want = min(4, size - self.offset)
                seq = e.bus.send(0x20, a.node, struct.pack(">BHBI", 0, ep["id"], want, self.offset))
            self.waiting = {"kind": kind, "ep": ep, "seq": seq, "issued": now,
                            "want": min(4, size - self.offset)}
        except Exception as exc:
            self.finish(exc)
            if isinstance(exc, (NodeReadTimeout, ConnectionError, OSError)):
                e.trip(f"ID{a.node} 准备通信故障：{exc}")
                e.lose_node(a, str(exc))

    def receive(self, frame):
        w = self.waiting
        if (self.done or w is None or frame.src != self.axis.node or frame.dest != HOST
                or frame.mt != (0x21 if w["kind"] == "write" else 0x20)):
            return False
        d, ep = frame.data, w["ep"]
        if len(d) < 4 or int.from_bytes(d[1:3], "big") != ep["id"]:
            return False
        if frame.seq != (w["seq"] if w["kind"] == "read" else 0):
            return False
        if self.engine.session != self.session:
            return True
        try:
            if self.engine.clock() - w["issued"] > .25:
                raise NodeReadTimeout("准备收到迟到端点应答")
            if w["kind"] == "write":
                # 固件 ACK 序号固定为 0；只匹配节点/端点，成功必须读回。
                if len(d) != 8 or d[:4] != struct.pack(">BHB", 0, ep["id"], 0):
                    raise ValueError("RAM ACK 结构不符")
            elif d[0] & 0x60 or d[3] != w["want"] or len(d) != 4 + w["want"]:
                raise ValueError("准备 SDO 长度/错误标志不符")
            self.waiting = None
            self.completed_transactions += 1
            if not self.identity_guard():
                self.finish(InterruptedError(self.cancel_reason))
                return True
            # 只收割本事务，不在帧内发令；同批后续门极应答尚未收割时，
            # 旧门极年龄不能否定当前合法读回。下一次 tick 发任何事务前仍完整核验。
            # 已明确观察到的故障/门极开启仍立即拒绝，不能用后续正常帧覆盖。
            if self.axis.has_fault or self.axis.gate is True:
                self.engine.check_preparation(self.axis)
            if w["kind"] == "write":
                self.operations[0] = ("read", ep["name"])
            else:
                self.raw.extend(d[4:])
                self.offset += w["want"]
                fmt, size = mc_ep.TYPE_CODES[ep["type"]]
                if self.offset == size:
                    value = struct.unpack(fmt, self.raw)[0]
                    if ep["type"] == "bool":
                        value = bool(value)
                    name = ep["name"]
                    if (name in POSITION_SETUP and self.stage != "核验必需参数"
                            and value != POSITION_SETUP[name][1]):
                        raise ValueError(name + " 写后/最终读回不符")
                    self.axis.params[name] = value
                    self.axis.parameter_times[name] = (w["issued"], time.time())
                    self.raw, self.offset = bytearray(), 0
                    self.operations.pop(0)
        except Exception as exc:
            self.finish(exc)
            if isinstance(exc, NodeReadTimeout):
                self.engine.trip(str(exc))
                self.engine.lose_node(self.axis, str(exc))
        return True


class ReadLink:
    """仅在所有轴无授权时使用同步事务，运动阶段由调度器直接处理帧。"""

    def __init__(self, bus, dispatch=lambda f: None, cancelled=lambda: False):
        self.bus, self.dispatch, self.cancelled = bus, dispatch, cancelled
        self.poisoned = False

    def poll(self):
        if self.cancelled():
            raise InterruptedError("连接/读取已取消")
        frames = self.bus.poll()
        for frame in frames:
            self.dispatch(frame)
        return frames

    def close(self):
        # 节点 Device 不拥有共享串口。
        pass


class CanDevice(mc_ep.Device):
    def __init__(self, link, node):
        if node not in NODES:
            raise ValueError("仅支持 ID1/2/3")
        super().__init__(io=link)
        self.link, self.node = link, node

    def matches(self, f, mt):
        return f.src == self.node and f.dest == HOST and f.mt == mt

    def fetch_descriptor(self):
        last = None
        for _ in range(3):
            assembly = Descriptor()
            # 取消上一轮可能仍在发送的流，然后有界排空。
            self.link.bus.send(0x24, self.node, struct.pack("<I", 0xFFFFFFFF))
            end = time.monotonic() + 0.04
            while time.monotonic() < end:
                self.link.poll()
                time.sleep(0.001)
            self.link.bus.send(0x24, self.node, struct.pack("<I", 0))
            started = time.monotonic()
            end = started + 8
            try:
                while time.monotonic() < end:
                    for f in self.link.poll():
                        if self.matches(f, 0x25):
                            try:
                                assembly.feed(f.data)
                            except ValueError as exc:
                                raise mc_ep.EpError(str(exc)) from exc
                    if (
                        assembly.total is not None
                        and len(assembly.parts) == (assembly.total + 5) // 6
                    ):
                        try:
                            self.descriptor = assembly.finish()
                        except ValueError as exc:
                            raise mc_ep.EpError(str(exc)) from exc
                        self.by_name = {
                            ep["name"]: ep for ep in self.descriptor
                        }
                        self.by_id = {ep["id"]: ep for ep in self.descriptor}
                        self.json_crc = assembly.crc
                        return self.descriptor
                    if (
                        assembly.total is None
                        and time.monotonic() - started >= 0.5
                    ):
                        raise NodeReadTimeout("未收到描述符应答")
                    time.sleep(0.001)
                raise NodeReadTimeout("描述符下载超时/缺片")
            except (mc_ep.EpError, NodeReadTimeout) as exc:
                last = exc
        self.link.bus.send(0x24, self.node, struct.pack("<I", 0xFFFFFFFF))
        raise mc_ep.EpError(f"ID{self.node} 描述符失败：{last}")

    def _transact(self, endpoint_id, tx_data, rx_len, trailer, write=False):
        ep = self.by_id.get(endpoint_id)
        if write or tx_data or not ep or not self.readable(ep):
            raise PermissionError("仅允许描述符中的可读标量")
        if self.link.poisoned:
            raise ConnectionError(
                "上次 SDO 超时，必须断开重连以免迟到响应串位"
            )
        raw = bytearray()
        for offset in range(0, rx_len, 4):
            want = min(4, rx_len - offset)
            self.link.poll()
            seq = self.link.bus.send(
                0x20,
                self.node,
                struct.pack(">BHBI", 0, endpoint_id, want, offset),
            )
            end = time.monotonic() + 0.25
            found = False
            while time.monotonic() < end:
                for f in self.link.poll():
                    if not self.matches(f, 0x20) or f.seq != seq:
                        continue
                    d = f.data
                    if (
                        len(d) < 4
                        or int.from_bytes(d[1:3], "big") != endpoint_id
                    ):
                        continue
                    if d[0] & 0x60 or d[3] != want or len(d) != 4 + want:
                        raise mc_ep.EpError("SDO 长度/错误标志不符")
                    raw.extend(d[4:])
                    found = True
                if found:
                    break
                time.sleep(0.001)
            if not found:
                self.link.poisoned = True
                raise NodeReadTimeout(f"ID{self.node} {ep['name']} 读取超时")
        return bytes(raw)

    @staticmethod
    def readable(ep):
        return (
            not ep.get("name", "action.").startswith("action.")
            and ep.get("type") in mc_ep.TYPE_CODES
            and ep.get("access") in ("r", "rw")
        )

    def read(self, name):
        ep = self._entry(name)
        if not self.readable(ep):
            raise PermissionError("动作、只写或未知类型不能读取")
        # 新旧 SDK 的 Device.read 传输接口不同；只复用类型，始终走本适配器的 CAN 事务。
        fmt, size = mc_ep.TYPE_CODES[ep["type"]]
        raw = self._transact(ep["id"], b"", size, self.json_crc)
        value = struct.unpack(fmt, raw)[0]
        return bool(value) if ep["type"] == "bool" else value

    @staticmethod
    def position_setup_entries(descriptor):
        entries = {ep["name"]: ep for ep in descriptor}
        for name, (typ, _) in POSITION_SETUP.items():
            ep = entries.get(name)
            if not ep or ep.get("type") != typ or ep.get("access") != "rw":
                raise PermissionError(f"{name} 须有在线 {typ}/rw 描述符；禁止猜测端点")
        return [entries[name] for name in POSITION_SETUP]

    def prepare_position_value(self, name, guard):
        """专用固定值事务；ACK 无成败状态且 seq 固定为 0，成功只认读回。"""
        self.position_setup_entries(self.descriptor)
        if name not in POSITION_SETUP:
            raise PermissionError("不在位置准备白名单")
        if self.link.poisoned:
            raise ConnectionError("SDO 状态不明，必须断开重连")
        ep = self.by_name[name]
        typ, expected = POSITION_SETUP[name]
        raw = struct.pack(mc_ep.TYPE_CODES[typ][0], expected)
        self.link.poll()
        guard()
        self.link.bus.send(
            0x21, self.node,
            struct.pack(">BHB", 0, ep["id"], len(raw)) + raw,
        )
        end = time.monotonic() + 0.25
        try:
            while time.monotonic() < end:
                for f in self.link.poll():
                    if (self.matches(f, 0x21) and len(f.data) == 8
                            and f.data[:4] == struct.pack(
                                ">BHB", 0, ep["id"], 0)):
                        guard()
                        actual = self.read(name)
                        guard()
                        if actual != expected:
                            raise ValueError(
                                f"{name} 写后读回不符：期望 {expected}，读回 {actual}"
                            )
                        return actual
                time.sleep(0.001)
            raise NodeReadTimeout(f"ID{self.node} {name} RAM 写入确认超时")
        except Exception:
            self.link.poisoned = True
            raise

    def write(self, name, value):
        raise PermissionError("禁止通用固件参数写入；只能显式执行固定的 RAM 位置准备")

    def call(self, name, via_read=False):
        raise PermissionError("首版不允许调用动作端点")
