"""单线程真机 CAN 调度与安全会话；不在导入或启动时打开串口。"""

from dataclasses import dataclass, field
from collections import deque
import copy
import math
import struct
import time
import uuid

from protocol import (
    NODES,
    HOST,
    SlcanBus,
    finite,
    position_payload,
    decode_mit,
    decode_pos_vel,
)
from motion import (
    REQUIRED, Trajectory, validate_parameters, runtime_limits,
    JOINT_MIN_DEG, JOINT_MAX_DEG, FAST_ACCELERATION, maximum_response_rpm,
)
from control_stream import MotionStreams
from can_endpoint import (
    CanDevice, ReadLink, NodeReadTimeout, POSITION_SETUP, PositionPreparation, mc_ep,
)


COMMAND_MARGIN_DEG = 0.1


@dataclass
class Axis:
    node: int
    connected: bool = False
    connection_error: str = ""
    params: dict = field(default_factory=dict)
    descriptor: list = field(default_factory=list)
    errors: list = field(default_factory=list)
    samples: dict = field(default_factory=dict)
    parameter_times: dict = field(default_factory=dict)
    position: float = 0.0
    velocity: float = 0.0
    position_at: float = -1e9
    status_at: float = -1e9
    gate_at: float = -1e9
    fault_at: float = -1e9
    gate: bool | None = None
    mode: int = 0
    fault: int = 0
    error: int = 0
    heartbeat_flags: int | None = None
    heartbeat_at: float = -1e9
    heartbeat_fault_flags: int = 0
    heartbeat_fault_at: float = -1e9
    zero: float | None = None
    direction: int = 1
    low: float = JOINT_MIN_DEG
    high: float = JOINT_MAX_DEG
    rpm: float = 100.0
    amps: float = 0.5
    acceleration: float = FAST_ACCELERATION
    owned: bool = False
    holding: bool = False
    hold_at_ms: float = -1e9
    control_token: int = 0
    preparation_stage: str = ""
    local_cancel_reason: str = ""
    local_stop: bool = False
    phase: str = "未连接"
    stage_at: float = 0.0
    sent_at: float = 0.0
    stop_at: float = 0.0
    trajectory: Trajectory | None = None
    error_since: float | None = None
    following_error: float | None = None
    following_reference: float | None = None
    following_reference_at: float = -1e9
    tx_history: deque = field(default_factory=lambda: deque(maxlen=256))
    stationary_since: float | None = None
    stationary_at: float = -1e9
    stationary_reset_at: float = -1e9
    stationary_issue: str = "等待连续位置采样"
    stationary_history: deque = field(default_factory=lambda: deque(maxlen=32))
    stationary_span: float | None = None
    position_resolution: float | None = None
    preparation_diagnostics: dict = field(default_factory=dict)
    stationary_path_velocity: float | None = None
    stationary_drift_velocity: float | None = None
    stationary_sample_at: float = -1e9
    encoder_at: float = -1e9
    encoder_count: int | None = None
    encoder_delta_deg: float = 0.0
    encoder_history: deque = field(default_factory=lambda: deque(maxlen=32))
    position_history: deque = field(default_factory=lambda: deque(maxlen=32))
    tx_count: int = 0
    tx_position: float | None = None
    tx_revision: int = -1
    tx_at: float = -1e9
    stop_tx_count: int = 0
    stop_tx_at: float = -1e9
    stop_tx_revision: int = -1
    stop_tx_kind: str = "未发送"
    stop_tx_error: str = ""
    stop_confirmed_at: float | None = None
    stop_confirmed_tx_count: int = 0

    def bounds(self):
        if self.zero is None:
            raise ValueError(f"ID{self.node} 尚未记录软件参考点")
        return runtime_limits(
            self.params,
            self.zero,
            self.direction,
            self.low,
            self.high,
            self.rpm,
            self.amps,
            self.acceleration,
        )

    def command_bounds(self):
        # 在已排序的绝对保护交集两端内缩；反向不改变裕量方向，不改原设置。
        low, high, amps = self.bounds()
        low, high = low + COMMAND_MARGIN_DEG, high - COMMAND_MARGIN_DEG
        if low >= high:
            raise ValueError(f"ID{self.node} 有效行程过窄，无法预留两端各 {COMMAND_MARGIN_DEG:g}° 命令裕量")
        return low, high, amps

    def relative_bounds(self, command=False):
        low, high, _ = self.bounds()

        def rel(value):
            # 软件配置端点保留原相对值，避免往返坐标转换把命令端点显示/判为越界。
            for configured in (self.low, self.high):
                if value == self.zero + self.direction * configured:
                    return configured
            return (value - self.zero) * self.direction

        low, high = sorted((rel(low), rel(high)))
        if command:
            self.command_bounds()
            low, high = low + COMMAND_MARGIN_DEG, high - COMMAND_MARGIN_DEG
        return low, high

    def validate_target(self, goal):
        goal = finite(goal)
        low, high, _ = self.command_bounds()
        # 仅容纳参考坐标变换的浮点尾差，不钳位目标，不放宽实测保护。
        if not low - 1e-12 <= goal <= high + 1e-12:
            relative = self.relative_bounds(command=True)
            raise ValueError(f"ID{self.node} 目标超出可下发行程 [{relative[0]:.6g}, {relative[1]:.6g}]°；"
                             f"两端预留 {COMMAND_MARGIN_DEG:g}°，未钳位或下发")
        return goal

    def fresh(self, now):
        return (
            self.connected
            and all(
                0 <= now - t <= 0.2
                for t in (self.position_at, self.status_at, self.gate_at)
            )
            and 0 <= now - self.fault_at <= 1
        )

    @property
    def has_fault(self):
        return bool(self.fault or self.error or self.heartbeat_fault_flags)

    def reconcile_heartbeat(self):
        # 心跳零标志不等于故障清除；还需故障心跳之后发起的新查询均无故障。
        # 未核验的分类位不因年龄过期而自动消失，也不写入 MIT 错误码。
        if (self.heartbeat_flags == 0 and not self.fault and not self.error
                and min(self.fault_at, self.status_at)
                > self.heartbeat_fault_at):
            self.heartbeat_fault_flags = 0

    def reset_following(self):
        self.error_since = None
        self.following_error = self.following_reference = None
        self.following_reference_at = -1e9
        self.tx_history.clear()

    @property
    def uses_encoder_counts(self):
        return any(ep.get("name") == "telemetry.count" and ep.get("type") == "int32"
                   and CanDevice.readable(ep) for ep in self.descriptor)

    def stationary_observations(self):
        return self.encoder_history if self.uses_encoder_counts else self.stationary_history

    def observe_encoder_count(self, value, sampled):
        if sampled <= self.encoder_at or sampled <= self.stationary_reset_at:
            return
        if sampled - self.encoder_at > .1 + 1e-9:
            self.reset_stationary(sampled - 1e-9)
        # 厂商 count 是线性化单圈计数（不是文档误标的多圈值），CPR=65536。
        # 只展开相邻计数增量用于静止；绝对角度、参考点和下发坐标仍来自 0x41。
        if self.encoder_count is not None:
            delta = (value - self.encoder_count + 32768) % 65536 - 32768
            self.encoder_delta_deg += delta * 360 / (65536 * self.params["config.gr"])
        self.encoder_count, self.encoder_at = value, sampled
        self.encoder_history.append((sampled, self.encoder_delta_deg, self.velocity, self.position))
        while len(self.encoder_history) > 2 and sampled - self.encoder_history[1][0] >= .5:
            self.encoder_history.popleft()

    def reset_stationary(self, now):
        self.stationary_since = None
        self.stationary_at = -1e9
        self.stationary_reset_at = now
        self.stationary_sample_at = self.encoder_at = -1e9
        self.encoder_count, self.encoder_delta_deg = None, 0.0
        self.encoder_history.clear()
        self.stationary_history.clear()
        self.stationary_span = self.stationary_path_velocity = None
        self.stationary_drift_velocity = None
        self.position_resolution = None
        self.stationary_issue = "等待连续位置采样"

    def update_stationary(self, now):
        # 500ms 位置窗口合格后，再由新采样连续确认 200ms；tick 空转不累计。
        if not self.fresh(now) or self.has_fault or now - self.position_at > .1 + 1e-9:
            self.reset_stationary(now)
            return
        counts = self.uses_encoder_counts
        observations = self.stationary_observations()
        if counts and (not observations or not 0 <= now - self.encoder_at <= .1 + 1e-9):
            self.stationary_since = None
            self.stationary_issue = "等待新鲜整数编码器计数（采样间隔不超过100ms）"
            return
        if not observations:
            return
        sampled = observations[-1][0]
        if sampled <= self.stationary_sample_at:
            return
        self.stationary_at, self.stationary_sample_at = self.position_at, sampled
        h = [(row[0], row[1], row[2]) for row in observations]
        window = h[-1][0] - h[0][0]
        self.stationary_span = max(p for _, p, _ in h) - min(p for _, p, _ in h)
        self.stationary_path_velocity = (
            sum(abs(b[1] - a[1]) for a, b in zip(h, list(h)[1:])) / window
            if window > 0 else None
        )
        # 用相对坐标回归，避免多圈绝对角度造成消减误差；不取模。
        ts = [t - h[0][0] for t, _, _ in h]
        ps = [p - h[0][1] for _, p, _ in h]
        mt, mp = sum(ts) / len(h), sum(ps) / len(h)
        denominator = sum((t - mt) ** 2 for t in ts)
        self.stationary_drift_velocity = (
            sum((t - mt) * (p - mp) for t, p in zip(ts, ps)) / denominator
            if denominator > 0 else None
        )
        gr = self.params["config.gr"]
        turns = abs(self.position * gr / 360)
        resolution = math.ldexp(1.0, math.frexp(turns)[1] - 24) * 360 / gr
        self.position_resolution = resolution
        if window < .5 - 1e-9 or len(h) < 6:
            issue = "等待连续位置观测窗 500ms（至少 6 个新采样）"
        elif counts and 360 / (65536 * gr) > .00375:
            issue = "整数编码器输出轴分辨率不足，不能确认静止"
        elif not counts and resolution > .00375:
            issue = (f"多圈位置 float32 分辨率不足：约 {resolution:.6f}°/LSB > 0.003750°；"
                     "不能确认静止，软件参考点不改变原始精度；须人工核对编码器多圈坐标，不自动置零")
        elif counts and any(abs((row[3] - observations[0][3]) - (row[1] - observations[0][1]))
                            > .015 + 2 * resolution for row in observations):
            issue = "整数编码器计数与0x41位置增量不一致；不能确认静止"
        elif self.stationary_span > .015:
            issue = "位置跨度超过 0.015°，尚未静止"
        elif self.stationary_path_velocity > .2:
            issue = "位置累计路程速度超过 0.2°/s（含往复运动）"
        elif abs(self.stationary_drift_velocity) > .02:
            issue = "位置漂移速度超过 0.02°/s"
        elif all(v > .2 for _, _, v in h) or all(v < -.2 for _, _, v in h):
            issue = "原始速度持续同向超过 0.2°/s，与静止位置不一致"
        else:
            issue = ""
        self.stationary_issue = issue
        if issue:
            self.stationary_since = None
        elif self.stationary_since is None:
            self.stationary_since = sampled

    def stationary_ms(self, now):
        return (max(0, self.stationary_sample_at - self.stationary_since) * 1000
                if self.fresh(now) and not self.has_fault
                and self.stationary_since is not None else 0)

    def stationary(self, now):
        return (self.stationary_at == self.position_at
                and 0 <= now - self.position_at <= .1 + 1e-9
                and not self.stationary_issue
                and (not self.uses_encoder_counts or 0 <= now - self.encoder_at <= .1 + 1e-9)
                and self.stationary_ms(now) >= 200 - 1e-6)

    def stationary_reason(self, now):
        if not self.fresh(now) or self.has_fault:
            return "反馈过期、未知或故障，静止证据无效"
        if self.stationary_issue:
            return self.stationary_issue
        if not self.stationary(now):
            return f"等待连续静止确认 200ms（新采样累计 {self.stationary_ms(now):.0f}ms）"
        return "连续位置窗口静止判定通过"


class Engine:
    def __init__(
        self,
        clock=time.monotonic,
        bus_factory=SlcanBus,
        cancelled=lambda: False,
    ):
        self.clock, self.bus_factory, self.cancelled = (
            clock,
            bus_factory,
            cancelled,
        )
        self.bus = None
        self.mode = "hardware"
        self.connected = False
        self.axes = {n: Axis(n) for n in NODES}
        self.session = str(uuid.uuid4())
        self.revision = 0
        self.lease_at = -1e9
        self.lease_count = 0
        self.last_lease_revision = -1
        self.lease_history = deque(maxlen=16)
        self.last_tick = clock()
        self.period_ms = 0.0
        self.next_fast = 0.0
        self.next_slow = 0.0
        self.slow_index = 0
        self.enable_queue = deque()
        self.pending = {}
        self.feedback_pending = {}
        self.latched = ""
        self.first_protection = None
        self.logs = deque(maxlen=200)
        self.log_seq = 0
        self.estop_recovery = {}
        self.unowned_stop = {}
        self.streams = MotionStreams(self)
        self.preparation = None
        self.local_revision_floor = 0

    def log(self, text, node=0):
        self.log_seq += 1
        self.logs.append(
            {
                "id": self.log_seq,
                "time": time.time(),
                "node": node,
                "text": text,
            }
        )

    @property
    def active(self):
        return any(a.owned for a in self.axes.values()) or bool(
            self.enable_queue
        )

    def connect(self, mode, port=""):
        if (
            self.connected
            or self.bus
            or any(a.phase == "停止待确认" for a in self.axes.values())
        ):
            raise ValueError("请先停止并断开当前会话")
        if mode != "hardware":
            raise ValueError("仅支持真机 hardware 模式；请更新上位机并显式选择串口")
        if not isinstance(port, str) or not port.strip() or len(port) > 128:
            raise ValueError("必须显式选择串口")
        self.mode, self.axes = mode, {n: Axis(n) for n in NODES}
        self.session, self.revision = str(uuid.uuid4()), 0
        self.local_revision_floor = 0
        self.preparation = None
        self.latched, self.pending, self.feedback_pending = "", {}, {}
        self.first_protection = None
        self.estop_recovery = {}
        self.unowned_stop = {}
        self.lease_at = -1e9
        self.lease_count = 0
        self.last_lease_revision = -1
        self.lease_history.clear()
        try:
            self.bus = self.bus_factory(port)
            if self.bus is None:
                raise ConnectionError("未建立真实总线连接")
            for n, a in self.axes.items():
                if self.cancelled():
                    raise InterruptedError("连接/读取已取消")
                # 各节点独立事务链；失败节点本会话不再重试，迟到包按源隔离。
                link = ReadLink(self.bus, self.receive, self.cancelled)
                dev = CanDevice(link, n)
                try:
                    a.descriptor = dev.fetch_descriptor()
                    # 缺少参数只读保留；事务超时后不继续复用同条连接。
                    for ep in a.descriptor:
                        if dev.readable(ep):
                            value = dev.read(ep["name"])
                            a.params[ep["name"]] = (
                                value
                                if not isinstance(value, float)
                                or math.isfinite(value)
                                else None
                            )
                            a.parameter_times[ep["name"]] = (
                                self.clock(),
                                time.time(),
                            )
                    a.errors = validate_parameters(a.params)
                    try:
                        a.rpm = maximum_response_rpm(a.params)
                    except ValueError as exc:
                        a.rpm = 0
                        a.errors.append(str(exc))
                    a.fault = int(a.params.get("telemetry.fault") or 0)
                    a.fault_at = a.parameter_times.get(
                        "telemetry.fault", (-1e9, 0)
                    )[0]
                except (mc_ep.EpError, NodeReadTimeout) as exc:
                    a.connection_error = str(exc)
                    a.params.clear()
                    a.descriptor.clear()
                    a.parameter_times.clear()
                    self.log("未连接：" + str(exc), n)
                    continue
                a.connected, a.phase = True, "只读"
                self.log(
                    "RX 描述符及只读参数完成"
                    + ("；" + "；".join(a.errors) if a.errors else ""),
                    n,
                )
            if not any(a.connected for a in self.axes.values()):
                raise ConnectionError(
                    "ID1/2/3 均未完成只读连接；请检查上电、接线及端口。"
                    + "；".join(
                        a.connection_error for a in self.axes.values()
                    )
                )
            if self.cancelled():
                raise InterruptedError("连接/读取已取消")
            self.connected = True
            self.last_tick = self.clock()
            self.next_fast = self.next_slow = self.slow_index = 0
            self.log(
                "新会话：真实硬件，只读；已连接 "
                + ", ".join(
                    f"ID{a.node}" for a in self.axes.values() if a.connected
                )
                + "；缺席节点不参与轮询，增减节点须停止并断开重连"
            )
        except Exception:
            self.connected = False
            for a in self.axes.values():
                a.connected, a.phase = False, "未连接"
            if self.bus:
                self.bus.close()
                self.bus = None
            raise

    def lose_node(self, a, reason):
        if not a.connected:
            return
        if a.owned or a.node in self.enable_queue or (a.local_stop and self.active):
            self.trip(f"ID{a.node} 通信中断：{reason}")
        a.connected, a.connection_error = False, reason
        a.zero, a.gate, a.trajectory = None, None, None
        a.reset_following()
        a.reset_stationary(self.clock())
        a.phase = (
            "停机未确认"
            if a.phase in ("停止待确认", "停机未确认")
            else "通信中断"
        )
        self.pending = {
            k: v for k, v in self.pending.items() if k[0] != a.node
        }
        self.feedback_pending = {
            k: v for k, v in self.feedback_pending.items() if k[0] != a.node
        }
        self.log("节点已隔离，须断开重连：" + reason, a.node)

    def reject_unsafe_preparation(self, a):
        task = self.preparation
        if task and not task.done and task.axis is a and (a.has_fault or a.gate is True):
            task.cancel(f"ID{a.node} 位置准备过程中门极开启或设备故障；未继续 RAM 事务")

    def receive(self, f):
        if f.src not in self.axes or (f.mt != 0x48 and f.dest != HOST):
            return
        a, now, d = self.axes[f.src], self.clock(), f.data
        if not a.connected:
            return
        if self.preparation and self.preparation.receive(f):
            return
        if f.mt == 0x48 and len(d) == 8:
            a.samples["axisState"] = (d[1] >> 4, now)
            a.heartbeat_flags, a.heartbeat_at = d[0] & 31, now
            if a.heartbeat_flags:
                a.heartbeat_fault_flags |= a.heartbeat_flags
                a.heartbeat_fault_at = now
                a.reset_stationary(now)
                # 按帧撤销活动授权，防止同批次后续正常状态掩盖新故障。
                if a.owned or a.node in self.enable_queue:
                    self.trip(f"ID{a.node} 心跳故障分类 "
                              f"0x{a.heartbeat_flags:02X}；已撤销运动授权")
                if (self.recovering_estop
                        and self.estop_recovery["node"] == a.node
                        and a.heartbeat_fault_flags & ~1):
                    self.finish_estop_recovery(
                        "failed", "心跳报告其他故障分类，禁止继续清除"
                    )
            a.reconcile_heartbeat()
            self.reject_unsafe_preparation(a)
            return
        if f.mt == 0x20 and len(d) >= 4:
            ep = int.from_bytes(d[1:3], "big")
            key = (a.node, ep)
            pending = self.pending.get(key)
            if pending and pending[0] == f.seq:
                _, name, issued, typ = pending
                if (
                    now - issued <= 0.2
                    and d[3] == mc_ep.TYPE_CODES[typ][1]
                    and len(d) == d[3] + 4
                    and not d[0] & 0x60
                ):
                    self.pending.pop(key)
                    value = struct.unpack(mc_ep.TYPE_CODES[typ][0], d[4:])[0]
                    if name == "telemetry.gate_drive":
                        a.gate, a.gate_at = bool(value), issued
                    elif name == "telemetry.fault":
                        a.fault, a.fault_at = value, issued
                    elif name == "telemetry.count" and typ == "int32":
                        a.observe_encoder_count(value, issued)
                    a.params[name] = value
                    a.parameter_times[name] = (
                        issued,
                        time.time() - (now - issued),
                    )
                    a.reconcile_heartbeat()
                    self.reject_unsafe_preparation(a)
            return
        if len(d) != 8 or (f.mt == 0x45 and d[0] != 5):
            return
        pending = self.feedback_pending.get((a.node, f.mt), [])
        match = next((p for p in pending if p[0] == f.seq), None)
        if match is None or now - match[1] > 0.2:
            return
        pending.remove(match)
        sampled = match[1]
        if f.mt == 0:
            # 状态不依赖位置量程；不把量化 MIT 位置当高精度测量。
            a.mode, a.error, a.status_at = d[5] & 15, d[3] & 15, sampled
            if all(
                a.params.get(k) is not None
                for k in (
                    "config.p_max",
                    "config.v_max",
                    "config.i_max",
                    "config.gr",
                )
            ):
                result = decode_mit(d, a.params)
                for k in ("current", "motorTemp", "driverTemp"):
                    a.samples[k] = (result[k], sampled)
        elif (
            f.mt == 0x41
            and len(d) == 8
            and (a.params.get("config.gr") or 0) > 0
        ):
            if sampled <= a.position_at:
                return
            if sampled - a.position_at > 0.2:
                a.position_history.clear()
            if sampled - a.position_at > .1 + 1e-9:
                a.reset_stationary(sampled - 1e-9)
            a.position, a.velocity = decode_pos_vel(d, a.params["config.gr"])
            a.position_at = sampled
            if sampled > a.stationary_reset_at:
                a.stationary_history.append((sampled, a.position, a.velocity))
                while (len(a.stationary_history) > 2
                       and sampled - a.stationary_history[1][0] >= .5):
                    a.stationary_history.popleft()
            a.position_history.append((sampled, a.position))
            while (a.position_history
                   and sampled - a.position_history[0][0] > 1):
                a.position_history.popleft()
            # 按新位置帧检查，避免同批次后续正常位置覆盖瞬时越界证据。
            if a.owned:
                low, high, _ = a.bounds()
                if not low <= a.position <= high:
                    self.fail_position_limit(a, low, high)
                self.check_following(a, sampled)
        elif f.mt in (0x42, 0x43, 0x44) and len(d) == 8:
            values = tuple(map(finite, struct.unpack(">ff", d)))
            names = {
                0x42: ("current", "idCurrent"),
                0x43: ("motorTemp", "driverTemp"),
                0x44: ("voltage", "busCurrentEstimate"),
            }[f.mt]
            for k, value in zip(names, values):
                a.samples[k] = (value, sampled)
        elif f.mt == 0x45:
            a.fault, a.fault_at = int.from_bytes(d[4:], "big"), sampled
        a.reconcile_heartbeat()
        self.reject_unsafe_preparation(a)

    def send_feedback(self, mt, node, data=b"", priority=5):
        if not self.axes[node].connected:
            raise ValueError(f"ID{node} 未连接，禁止发送查询或运动命令")
        response_type = 0 if mt in (0x01, 0x40) else mt
        pending = self.feedback_pending.setdefault((node, response_type), [])
        issued = self.clock()
        if pending and issued - pending[0][1] > 0.2:
            raise NodeReadTimeout(
                f"ID{node} 0x{response_type:02X} 反馈事务超时；须重连"
            )
        if mt != 0x01 and pending:
            return
        # 相同节点/响应类型最多四个未完成事务，禁止覆盖两位序号。
        if len(pending) >= 4:
            raise NodeReadTimeout(f"ID{node} 未完成反馈过多")
        seq = self.bus.send(mt, node, data, priority=priority)
        if any(p[0] == seq for p in pending):
            raise NodeReadTimeout(f"ID{node} 未完成反馈序号回绕；须重连")
        pending.append((seq, issued))

    def query_scalar(self, a, name):
        ep = next(
            (
                e
                for e in a.descriptor
                if e["name"] == name and CanDevice.readable(e)
            ),
            None,
        )
        if not ep or mc_ep.TYPE_CODES[ep["type"]][1] > 4:
            return
        key = (a.node, ep["id"])
        if key in self.pending:
            if self.clock() - self.pending[key][2] > 0.2:
                raise NodeReadTimeout(f"ID{a.node} 状态 SDO 过期")
            return
        if self.preparation and not self.preparation.done:
            # 门极遥测与 RAM 参数按节点/端点/序号独立匹配；不让遥测饿死准备任务。
            # 只放行门极及整数静止采样；准备仍至多一个 RAM 事务在途。
            if name not in ("telemetry.gate_drive", "telemetry.count"):
                return
            w = self.preparation.waiting
            if w and a.node == self.preparation.axis.node and ep["id"] == w["ep"]["id"]:
                return
        size = mc_ep.TYPE_CODES[ep["type"]][1]
        issued = self.clock()
        seq = self.bus.send(
            0x20, a.node, struct.pack(">BHB", 0, ep["id"], size)
        )
        self.pending[key] = (seq, name, issued, ep["type"])

    def preparation_issues(self, a):
        try:
            CanDevice.position_setup_entries(a.descriptor)
        except PermissionError as exc:
            return [str(exc)]
        projected = dict(a.params)
        projected.update({
            name: value for name, (_, value) in POSITION_SETUP.items()
        })
        return validate_parameters(projected)

    def check_preparation(self, a):
        now = self.clock()
        if self.latched or self.bus is None or not self.connected or self.cancelled():
            raise ValueError("位置准备要求无锁存、总线有效且未取消")
        if (not a.connected or a.owned or a.node in self.enable_queue or not a.fresh(now)
                or a.gate is not False or a.has_fault
                or a.phase in ("停止待确认", "停机未确认")):
            raise ValueError(f"ID{a.node} 位置准备要求反馈新鲜、未接管、无故障、门极关闭且停止已确认")

    def prepare_position(self, a):
        self.check_preparation(a)
        if self.preparation and not self.preparation.done:
            raise ValueError("另一个位置准备事务尚未结束")
        if not a.stationary(self.clock()):
            raise ValueError("位置准备要求本轴连续静止：" + a.stationary_reason(self.clock()))
        issues = self.preparation_issues(a)
        if issues:
            raise ValueError("；".join(issues))
        self.streams.remove(a.node)
        a.zero, a.trajectory = None, None
        a.preparation_stage, a.local_cancel_reason = "核验必需参数", ""
        self.preparation = PositionPreparation(self, a)
        return self.preparation

    @property
    def recovering_estop(self):
        return self.estop_recovery.get("pending", False)

    @property
    def unowned_stopping(self):
        return self.unowned_stop.get("pending", False)

    def unowned_stop_reason(self, a, now):
        if not self.connected or not a.connected or self.bus is None:
            return "本轴未连接"
        if self.unowned_stopping or self.recovering_estop:
            return "停止或急停清除核验中"
        if self.active or self.streams.source != "none":
            return "须先停止本会话输出和控制流"
        if not a.fresh(now) or a.gate is not True:
            return "须有新鲜反馈且门极开启；不对未知状态发送人工停止"
        if a.phase in ("停止待确认", "停机未确认"):
            return "停机正在核验或未确认；请核对支撑和硬件急停"
        if a.fault or a.error not in (0, 9) or a.heartbeat_fault_flags:
            return "仅处理无驱动故障的未接管闭环/指令超时；不清其他故障"
        version = tuple(a.params.get("system.fw_version_" + key)
                        for key in ("major", "minor", "revision"))
        if version != (1, 0, 5):
            return "固件版本未核实：人工停止入口仅支持已核对的 1.0.5"
        return ""

    def begin_unowned_stop(self, a, revision):
        reason = self.unowned_stop_reason(a, self.clock())
        if reason:
            raise ValueError(reason)
        self.streams.invalidate(clear_alignment=True)
        self.enable_queue.clear()
        for axis in self.axes.values():
            axis.zero, axis.trajectory = None, None
            axis.reset_stationary(self.clock())
        self.revision = revision
        self.unowned_stop = {
            "pending": True, "node": a.node, "revision": revision,
            "sessionId": self.session, "stage": "verifying", "sent": False,
            "requestedAt": time.time(), "sentAt": None, "completedAt": None,
            "originalFault": a.fault, "originalError": a.error,
            "reason": "人工停止请求已接受；尚未确认门极关闭",
        }
        a.phase, a.stop_at = "停止待确认", self.clock()
        try:
            self.bus.discard_pending_output()
            # 仅本轴普通 STOP，不发送 CLEAR/START/位置帧，不接管运动。
            self.send_stop_frame(a)
            a.stop_at = self.clock()
            self.unowned_stop.update(sent=True, sentAt=time.time())
            self.log("TX 人工 STOP 0x63；等待发送后新门极/故障/状态查询", a.node)
        except Exception as exc:
            self.finish_unowned_stop("failed", "停止帧写入未确认：" + str(exc))
        self._unowned_stop_poll = 0

    def finish_unowned_stop(self, stage, reason):
        if not self.unowned_stopping:
            return
        r = self.unowned_stop
        r.update(pending=False, stage=stage, reason=reason,
                 completedAt=time.time())
        a = self.axes[r["node"]]
        a.phase = "停止输出已确认" if stage == "confirmed" else "停机未确认"
        if stage == "confirmed":
            self.record_stop_confirmation(a)
        a.reset_stationary(self.clock())
        if stage != "confirmed":
            self.latched = self.latched or "人工停止未确认：" + reason
        self.log(reason, a.node)

    def tick_unowned_stop(self, now):
        if not self.unowned_stopping:
            return
        a = self.axes[self.unowned_stop["node"]]
        if self.cancelled():
            self.finish_unowned_stop("cancelled", "核验被取消；不自动继续或启动")
            return
        if not self.connected or not a.connected or self.bus is None:
            self.finish_unowned_stop("failed", "总线或节点失联；请检查硬件急停与支撑")
            return
        if now - a.stop_at >= .3:
            self.finish_unowned_stop("failed", "300ms 内未确认停机；不自动重试")
            return
        # 查询发起时间必须晚于 STOP；旧请求的迟到回复不能确认本次停止。
        if (min(a.gate_at, a.fault_at, a.status_at) > a.stop_at
                and a.fresh(now) and a.gate is False):
            self.finish_unowned_stop("confirmed", "RX 门极已关闭；无保持力矩。"
                                     "未清故障、未启动；须重新确认安全并启动位置环")
            return
        if now >= self._unowned_stop_poll:
            self._unowned_stop_poll = now + .05
            self.query_scalar(a, "telemetry.gate_drive")
            self.send_feedback(0x45, a.node, b"\x05")
            self.send_feedback(0x40, a.node)
            self.send_feedback(0x41, a.node)

    def estop_clear_reason(self, a, now):
        if not self.connected or not a.connected or self.bus is None:
            return "本轴未连接"
        if self.latched:
            return "本地保护已锁存；先检查原因、停止并断开重连"
        if self.unowned_stopping:
            return "人工停止核验中；不会自动使能"
        if self.recovering_estop:
            return "急停清除正在核验；不会自动使能"
        if self.active or self.streams.source != "none":
            return "须先停止所有轴输出和控制流"
        for axis in self.axes.values():
            if axis.connected and (
                not axis.fresh(now) or axis.gate is not False
                or axis.phase in ("停止待确认", "停机未确认")
            ):
                return f"ID{axis.node} 须反馈新鲜、门极关闭且停止已确认"
        if a.heartbeat_fault_flags & ~1:
            return "心跳报告其他故障分类；仅允许清除单独 CAN 急停锁存"
        if a.fault != 4 or a.error != 9:
            return "仅允许清除单独的 CAN 急停锁存 Fault=4 / Error=9；其他故障须排查"
        version = tuple(a.params.get("system.fw_version_" + key)
                        for key in ("major", "minor", "revision"))
        if version != (1, 0, 5):
            return "固件版本未核实：此清除入口仅支持已核对的 1.0.5"
        return ""

    def begin_estop_recovery(self, a, revision):
        reason = self.estop_clear_reason(a, self.clock())
        if reason or self.cancelled():
            raise ValueError(reason or "急停清除已被停止意图取消")
        self.streams.invalidate(clear_alignment=True)
        for axis in self.axes.values():
            axis.zero, axis.trajectory = None, None
            axis.reset_stationary(self.clock())
        self.revision = revision
        self._recovery_started = self._recovery_barrier = self.clock()
        self._recovery_poll = 0
        self.estop_recovery = {
            "pending": True, "node": a.node, "revision": revision,
            "sessionId": self.session, "stage": "checking",
            "requestedAt": time.time(), "sentAt": None,
            "completedAt": None, "originalFault": a.fault,
            "originalError": a.error, "sent": False,
            "reason": "等待操作后新采样再次核对故障和门极；尚未发送清除帧",
        }
        a.phase = "急停清除待确认"
        self.log("用户点击异常请求解锁；清除所有软件参考和旧目标，开始核验", a.node)

    def finish_estop_recovery(self, stage, reason):
        if not self.recovering_estop:
            return
        r = self.estop_recovery
        r.update(pending=False, stage=stage, reason=reason,
                 completedAt=time.time())
        a = self.axes[r["node"]]
        a.reset_stationary(self.clock())
        a.phase = "急停清除已确认" if stage == "confirmed" else "急停清除未确认"
        if stage != "confirmed":
            self.latched = self.latched or "急停清除未确认：" + reason
        self.log(reason, a.node)

    def tick_estop_recovery(self, now):
        if not self.recovering_estop:
            return
        r = self.estop_recovery
        a = self.axes[r["node"]]
        if self.cancelled() or self.latched:
            self.finish_estop_recovery("cancelled", "清除已取消，须重连核对实际状态")
            return
        if now - self._recovery_started >= 1:
            self.finish_estop_recovery("failed", "清除核验超时；不重试、不使能，须重连")
            return
        for axis in self.axes.values():
            if (axis.node == a.node and not axis.connected) or (
                axis.connected and (not axis.fresh(now)
                                    or axis.gate is not False)
            ):
                self.finish_estop_recovery(
                    "failed", f"ID{axis.node} 反馈失效或门极异常，清除结果未确认"
                )
                return
        if (a.fault not in (0, 4) or a.error not in (0, 9)
                or a.heartbeat_fault_flags & ~1):
            self.finish_estop_recovery("failed", "出现其他驱动故障，禁止继续清除")
            return
        # 使用查询发起时间作屏障：旧请求的迟到回复不能确认本次操作。
        if min(a.gate_at, a.fault_at, a.status_at) > self._recovery_barrier:
            if r["stage"] == "checking":
                if a.fault != 4 or a.error != 9:
                    self.finish_estop_recovery("failed", "故障状态已变化；未发送清除帧")
                    return
                if self.cancelled():
                    self.finish_estop_recovery("cancelled", "发送前被停止意图取消")
                    return
                # 0x65 为普通节点寻址，只发送一次，不附带 START 或位置指令。
                self.bus.send(0x65, a.node, priority=3)
                self._recovery_barrier = self.clock()
                r.update(stage="verifying", sent=True, sentAt=time.time(),
                         reason="清除帧已写入转换器，等待新故障/状态/门极反馈")
                self.log("TX CLEAR_ERRORS 0x65；未使能，等待真实反馈", a.node)
            elif not a.has_fault and a.mode == 2:
                self.finish_estop_recovery(
                    "confirmed", "RX 急停0x4已清除、门极关闭且电机空闲；未使能。"
                    "等待连续静止后可显式启动，不恢复旧目标"
                )
                return
        if now >= self._recovery_poll:
            self._recovery_poll = now + .05
            self.query_scalar(a, "telemetry.gate_drive")
            self.send_feedback(0x45, a.node, b"\x05")
            self.send_feedback(0x40, a.node)
            self.send_feedback(0x41, a.node)

    def check_ready(self, a, now):
        if not a.connected:
            raise ValueError(f"ID{a.node} 未连接")
        if self.latched:
            raise ValueError("本地故障已锁存：停止后断开、重新确认连接")
        if a.errors:
            raise ValueError("；".join(a.errors))
        if (
            not a.fresh(now)
            or a.gate is not False
            or a.mode != 2
            or a.has_fault
            or a.owned
        ):
            raise ValueError(
                f"ID{a.node} 要求反馈新鲜、无故障且实际未使能；不接管外部运行"
            )
        lo, hi, amps = a.bounds()
        a.command_bounds()
        if not lo <= a.position <= hi:
            raise ValueError("当前位置不在有效行程内")
        if not a.stationary(now):
            raise ValueError("要求连续静止：" + a.stationary_reason(now))
        return lo, hi, amps

    def command(self, msg):
        if msg.get("version") != 1:
            raise ValueError("协议版本不匹配")
        action = msg.get("action")
        data = msg.get("data") or {}
        if not isinstance(data, dict):
            raise ValueError("data 必须为对象")
        if action == "ports":
            if self.connected or self.bus or self.active:
                raise ValueError("连接或运行时不枚举端口")
            from serial.tools import list_ports

            return [
                {
                    "device": p.device,
                    "description": p.description or "",
                    "manufacturer": p.manufacturer or "",
                    "product": p.product or "",
                    "vid": p.vid, "pid": p.pid,
                    "serialNumber": p.serial_number or "",
                    "candidate": (p.vid, p.pid) == (0x16D0, 0x117E),
                    "evidence": "USB VID:PID 16D0:117E（仅转换器候选）"
                    if (p.vid, p.pid) == (0x16D0, 0x117E)
                    else "未匹配已核实的 USB 标识；可手动选择",
                }
                for p in sorted(list_ports.comports(), key=lambda p: p.device)
            ]
        if action == "connect":
            self.connect(data.get("mode"), data.get("port", ""))
            return self.snapshot()
        if msg.get("sessionId") != self.session:
            raise ValueError("旧会话命令已拒绝")
        revision = msg.get("revision")
        if (
            type(revision) is not int
            or not (self.local_revision_floor if action in ("lease", "stream_sample") else self.revision)
                   <= revision <= 9007199254740991
        ):
            raise ValueError("旧意图版本已拒绝")
        now = self.clock()
        if action == "lease":
            if revision > self.revision:
                raise ValueError("租约与当前意图不符")
            self.lease_history.append({
                "acceptedAt": time.time(), "revision": revision,
                "gapMs": (now - self.lease_at) * 1000
                if self.lease_count else None,
            })
            self.lease_at = now
            self.lease_count += 1
            self.last_lease_revision = revision
            return None
        if action == "disconnect":
            self.disconnect()
            return self.snapshot()
        if not self.connected:
            raise ValueError("尚未连接")
        if action == "stream_sample":
            if revision > self.revision:
                raise ValueError("流样本不能改变授权 revision")
            return self.streams.sample(data)
        if action == "parameters":
            n = data.get("node", 1)
            a = self.axes[n]
            readings = {
                k: {
                    "value": v,
                    "ageMs": (now - a.parameter_times.get(k, (now, 0))[0])
                    * 1000,
                    "acquiredAt": a.parameter_times.get(k, (now, 0))[1],
                    "valid": self.connected
                    and a.connected
                    and v is not None
                    and (
                        not k.startswith("telemetry.")
                        or now - a.parameter_times.get(k, (-1e9, 0))[0] <= 0.2
                    ),
                }
                for k, v in a.params.items()
            }
            return {
                "node": n,
                "connected": a.connected,
                "connectionError": a.connection_error,
                "source": self.mode,
                "values": a.params,
                "readings": readings,
                "descriptor": a.descriptor,
                "note": "连接时逐项读取的参数缓存；遥测过期不可视为当前值",
            }
        if action not in (
            "reference",
            "settings",
            "prepare_position",
            "clear_estop",
            "stop_unowned",
            "enable",
            "target",
            "pause",
            "stop", "stop_axis", "hold", "hold_axis", "resume_axis",
            "safety_stop",
            "estop",
            "align", "stream_begin", "stream_attach", "stream_end",
        ):
            raise ValueError("未知或不允许的动作")
        if revision <= self.revision:
            raise ValueError("新动作必须提高 revision")
        nodes = data.get("nodes", list(NODES))
        if (
            not isinstance(nodes, list)
            or not nodes
            or len(nodes) != len(set(nodes))
            or any(type(n) is not int or n not in NODES for n in nodes)
        ):
            raise ValueError("节点列表非法")
        axes = [self.axes[n] for n in nodes]
        if action in ("hold", "hold_axis"):
            if (action == "hold_axis" and len(nodes) != 1) or set(data) != {"nodes"}:
                raise ValueError("保持仅接受合法节点列表；逐轴保持严格要求一个节点")
            self.revision = revision
            if action == "hold":
                self.local_revision_floor = revision
            return self.hold_axes(axes)
        if action == "resume_axis":
            if len(nodes) != 1 or set(data) != {"nodes", "source", "sample"}:
                raise ValueError("恢复仅接受单轴、显式来源与本次采集")
            result = self.resume_axis(axes[0], data)
            self.revision = revision
            return result
        if action == "stop_axis":
            if len(nodes) != 1 or set(data) != {"nodes"}:
                raise ValueError("逐轴停止严格要求一个合法节点")
            self.revision = revision
            return self.stop_axis(axes[0])
        if action == "estop":
            self.local_revision_floor = self.revision = revision
            self.trip("用户软件急停", all_nodes=True)
            return None
        if action == "stop":
            self.local_revision_floor = self.revision = revision
            return self.stop()
        if action == "safety_stop":
            reason = data.get("reason")
            if (set(data) != {"reason"} or not isinstance(reason, str)
                    or not reason.strip() or len(reason) > 512):
                raise ValueError("异常停止仅接受非空且不超过512字的原因；不接受全节点授权")
            self.local_revision_floor = self.revision = revision
            self.trip("UI 异常保护：" + reason)
            return None
        if action == "stop_unowned":
            if (len(axes) != 1 or data.get("confirmed") is not True
                    or data.get("safetyAcknowledged") is not True
                    or set(data) != {"nodes", "confirmed", "safetyAcknowledged"}):
                raise ValueError("人工停止仅接受单轴、台架安全及停发/支撑的双重确认")
            self.begin_unowned_stop(axes[0], revision)
            return None
        if self.unowned_stopping:
            if action in ("pause", "stream_end"):
                self.revision = revision
                self.finish_unowned_stop("cancelled", "核验被暂停/结束控制流取消")
                return None
            raise ValueError("人工停止核验中，仅允许取消、停止、急停、断开和只读查询")
        if self.recovering_estop:
            if action in ("pause", "stream_end"):
                self.revision = revision
                self.finish_estop_recovery("cancelled", "清除被暂停/结束控制流取消，须重连核对")
                return None
            raise ValueError("急停清除核验中，仅允许取消控制、停止、急停、断开和只读查询")
        if any(not a.connected for a in axes):
            raise ValueError("所选节点未全部连接；请仅选择已连接轴")
        if action == "clear_estop":
            if (len(axes) != 1 or data.get("requestedBy") != "abnormal_button"
                    or set(data) != {"nodes", "requestedBy"}):
                raise ValueError("急停清除仅接受用户点击异常发起的单轴解锁请求")
            self.begin_estop_recovery(axes[0], revision)
            return None
        if action == "stream_begin":
            result = self.streams.begin(nodes, data)
            self.local_revision_floor = self.revision = revision
            return {**result, "revision": self.revision}
        if action == "stream_attach":
            result = self.streams.attach(nodes, data)
            self.revision = revision
            return {**result, "revision": revision}
        if action == "stream_end":
            self.streams.end()
            self.local_revision_floor = self.revision = revision
            return None
        if action == "align":
            self.streams.align(nodes, data)
            self.revision = revision
            return None
        if self.preparation and not self.preparation.done and any(a.node == self.preparation.axis.node for a in axes):
            raise ValueError("本轴位置准备尚未结束")
        if self.streams.source != "none" and action not in ("pause", "prepare_position", "reference", "settings", "enable"): 
            raise ValueError("控制流运行中；请先暂停并等待静止")
        if action == "prepare_position":
            if (len(axes) != 1 or data.get("confirmed") is not True
                    or set(data) != {"nodes", "confirmed"}):
                raise ValueError("位置准备仅接受单轴及显式安全确认，不接受自定义参数")
            task = self.prepare_position(axes[0])
            if not self.active:
                self.local_revision_floor = revision
            self.revision = revision
            return task
        elif action in ("reference", "settings"):
            if any(
                a.owned or a.node in self.enable_queue or a.gate is not False or not a.fresh(now)
                or a.phase in ("停止待确认", "停机未确认") for a in axes
            ):
                raise ValueError("参考点/设置只允许实际未使能且反馈新鲜时调整")
            copies = []
            for a in axes:
                b = copy.copy(a)
                if action == "reference":
                    if self.latched or a.errors or a.has_fault:
                        raise ValueError("参考被安全校验阻断：" + (
                            self.latched or "；".join(a.errors) or "驱动故障/协议错误"
                        ))
                    if not a.stationary(now):
                        raise ValueError("记录参考要求连续静止：" + a.stationary_reason(now))
                    b.zero = b.position
                else:
                    for name in (
                        "low",
                        "high",
                        "rpm",
                        "amps",
                        "acceleration",
                        "direction",
                    ):
                        if name in data:
                            value = finite(data[name])
                            if name == "direction":
                                if value not in (-1, 1):
                                    raise ValueError("方向只允许 +1/-1")
                                value = int(value)
                            setattr(b, name, value)
                if b.zero is not None:
                    b.amps = b.command_bounds()[2]
                elif action == "settings":
                    raise ValueError("请先记录参考点")
                copies.append(b)
            for b in copies:
                self.axes[b.node] = b
            for a in axes:
                self.streams.remove(a.node)
        elif action == "enable":
            if data.get("confirmed") is not True:
                raise ValueError("需要确认固定空载台架、供电及硬件急停")
            if self.enable_queue or any(
                a.phase in ("等待闭环", "确认位置保持", "停止待确认")
                for a in axes
            ):
                raise ValueError("状态转换正在进行")
            for a in axes:
                self.check_ready(a, now)
            # 仅首次从无运动进入使能时建立运动时钟；不继承只读期的调度延迟。
            # 已有活动轴时绝不重置，不续租，也不刷新任何反馈证据。
            if not self.active:
                self.last_tick = now
            for a in axes:
                a.control_token += 1
                a.local_stop = a.holding = False
                a.local_cancel_reason = ""
            self.enable_queue = deque(nodes)
        elif action in ("target", "pause"):
            if action == "pause" and self.streams.source != "none":
                self.streams.end()
                axes = [a for a in self.axes.values() if a.owned]
            planned = []
            for a in axes:
                if (not a.owned or a.phase != "位置控制" or not a.fresh(now)
                        or action == "target" and a.holding
                        or self.latched or a.errors or a.has_fault
                        or a.gate is not True):
                    raise ValueError(f"ID{a.node} 未进入有效位置控制")
                lo, hi, _ = a.bounds()
                p = copy.deepcopy(a.trajectory)
                goal = (
                    p.target
                    if action == "pause"
                    else a.zero
                    + a.direction
                    * finite(data.get("targets", {}).get(str(a.node)))
                )
                if action == "target":
                    a.validate_target(goal)
                p.set_target(goal, lo, hi, pause=action == "pause")
                planned.append((a, p))
            for a, p in planned:
                a.trajectory = p
                a.reset_stationary(now)
                self.log(
                    f"目标已规划：相对 {(p.target - a.zero) * a.direction:.3f}° / "
                    f"绝对 {p.target:.3f}°；等待 TX 0x01", a.node,
                )
        if action in ("target", "pause") or not self.active:
            self.local_revision_floor = revision
        self.revision = revision
        # 意图提交不是租约；UI 必须持续以当前 revision 显式续约。
        self.log(
            f"UI → {action} / revision {revision}",
            nodes[0] if len(nodes) == 1 else 0,
        )
        return None

    def begin_next(self, now):
        if not self.enable_queue or self.pending or (
            self.preparation and not self.preparation.done and self.preparation.waiting
        ) or any(a.phase in ("等待闭环", "确认位置保持") for a in self.axes.values()):
            return
        a = self.axes[self.enable_queue[0]]
        if self.cancelled():
            raise InterruptedError("停止/断开已取消使能")
        self.check_ready(a, now)
        if now - self.lease_at > 0.2:
            self.fail_timing("ui_lease", "UI 租约过期", 200, a.node)
        self.enable_queue.popleft()
        a.reset_following()
        a.owned, a.phase, a.stage_at = True, "等待闭环", now
        a.sent_at = now
        if self.bus is None:
            raise ConnectionError("总线已断开；禁止使能")
        self.bus.send(0x62, a.node, priority=3)
        self.send_feedback(0x41, a.node)
        self.query_scalar(a, "telemetry.gate_drive")
        self.log("TX START；等待真实门极状态，再播种当前位置", a.node)

    def send_stop_frame(self, a, emergency=False):
        # 仅成功写入转换器才累计；故障字段不代表门极已关闭。
        try:
            if self.bus is None:
                raise ConnectionError("总线已断开，停止帧未发送")
            self.bus.send(0xC0 if emergency else 0x63, a.node, priority=1)
        except Exception as exc:
            a.stop_tx_error = str(exc)
            raise
        a.stop_tx_count += 1
        a.stop_tx_at = self.clock()
        a.stop_tx_revision = self.revision
        a.stop_tx_kind = "ESTOP 0xC0" if emergency else "STOP 0x63"
        a.stop_tx_error = ""

    def hold_axes(self, axes):
        self.finish_unowned_stop("cancelled", "日常停止转动取消核验；不自动使能")
        self.finish_estop_recovery("cancelled", "日常停止转动取消清除核验；须核对结果")
        now = self.clock()
        planned = []
        try:
            for a in axes:
                if not a.owned:
                    continue
                if (self.latched or not a.fresh(now) or a.errors or a.has_fault
                        or a.phase not in ("位置控制", "等待闭环", "确认位置保持")):
                    raise ValueError(f"ID{a.node} 当前状态不能安全减速保持")
                p = copy.deepcopy(a.trajectory)
                if p is not None:
                    if a.gate is not True:
                        raise ValueError(f"ID{a.node} 保持门极异常")
                    lo, hi, _ = a.bounds()
                    p.set_target(p.target, lo, hi, pause=True)
                # START 在途时尚无轨迹；tick 仍等本次新反馈后播种当前位置，绝不补发 START。
                planned.append((a, p))
        except Exception as exc:
            if any(a.owned for a in axes):
                self.trip("无法安全减速保持：" + str(exc))
            raise
        for a in axes:
            if self.preparation and not self.preparation.done and self.preparation.axis.node == a.node:
                self.preparation.cancel("本轴停止转动取消位置准备")
            self.enable_queue = deque(n for n in self.enable_queue if n != a.node)
            a.control_token += 1
            a.local_cancel_reason = "停止转动并保持；旧目标与启动代次已取消"
            self.streams.remove(a.node, clear_alignment=False)
        for a, p in planned:
            if not a.holding:
                a.reset_stationary(now)
            a.trajectory, a.holding, a.hold_at_ms = p, True, time.time() * 1000
            self.log("减速至零并保持力矩；继续 TX 0x01，不发送 STOP/ESTOP", a.node)
        return {"holdingNodes": [a.node for a, _ in planned],
                "skippedNodes": [a.node for a in axes if not a.owned],
                "axisTokens": self.streams.tokens(),
                "message": "停止转动已接受；减速并保持力矩，等待新鲜反馈。未发送停止帧；未接管轴不接管，旧目标不恢复。"}

    def resume_axis(self, a, data):
        now = self.clock()
        source = data.get("source")
        if (source not in ("manual", "vision") or self.latched or not a.owned or not a.holding
                or a.phase != "位置控制" or not a.fresh(now) or a.has_fault or a.errors
                or a.gate is not True or a.mode != 5 or a.trajectory is None or a.trajectory.segments
                or not a.stationary(now) or abs(a.position - a.trajectory.target) >= .2):
            raise ValueError("恢复要求本轴已减速保持、反馈新鲜且连续静止到位；不自动使能")
        if self.streams.source not in ("none", source):
            raise ValueError("控制来源互斥；须先停止转动旧来源")
        result = {}
        if source == "vision":
            result = self.streams.resume_alignment(a, data.get("sample"))
        elif data.get("sample") is not None:
            raise ValueError("手动恢复不接受视觉采集")
        a.holding = False
        a.local_cancel_reason = "人工恢复控制；保持原参考与当前目标，不重放旧目标"
        return result

    def stop_axis(self, a):
        a.holding = False
        submitted = a.owned or a.node in self.enable_queue
        if self.preparation and not self.preparation.done and self.preparation.axis.node == a.node:
            self.preparation.cancel("本轴停止取消位置准备")
        a.control_token += 1
        a.local_cancel_reason = "本轴普通停止；旧目标与启动代次已取消"
        self.enable_queue = deque(n for n in self.enable_queue if n != a.node)
        self.streams.remove(a.node)
        a.trajectory = None
        a.reset_following()
        a.reset_stationary(self.clock())
        if not submitted or a.phase in ("停止待确认", "停机未确认"):
            return {"sentNodes": [], "failedNodes": [], "skippedNodes": [a.node],
                    "axisTokens": self.streams.tokens(), "message": f"ID{a.node} 未接管或停止核验中；未重复发送。外部门极开启须专用人工停止入口。"}
        a.owned, a.local_stop, a.phase, a.stop_at = False, True, "停止待确认", self.clock()
        try:
            self.send_stop_frame(a)
            a.stop_at = self.clock()
            self.log("TX 本轴 STOP 0x63；其他轴继续，等待新门极确认", a.node)
            return {"sentNodes": [a.node], "failedNodes": [], "skippedNodes": [],
                    "axisTokens": self.streams.tokens(), "message": f"ID{a.node} 停止帧已写入；等待门极关闭，无保持力矩。其他轴未停止。"}
        except Exception as exc:
            a.phase = "停机未确认"
            self.trip(f"ID{a.node} 局部停止写入失败：{exc}")
            return {"sentNodes": [], "failedNodes": [a.node], "skippedNodes": [],
                    "message": "本轴停止失败，已升级组保护；须检查硬件急停与支撑。"}

    def stop(self, emergency=False, all_nodes=False):
        self.finish_unowned_stop("cancelled", "核验被停止/急停取消；须重新核对反馈")
        self.finish_estop_recovery("cancelled", "清除被停止/急停取消，须重连核对")
        self.streams.invalidate(clear_alignment=True)
        if self.preparation and not self.preparation.done:
            self.preparation.cancel("全局停止取消位置准备")
        self.enable_queue.clear()
        if self.bus:
            try:
                self.bus.discard_pending_output()
            except Exception as exc:
                self.log(f"清发送缓冲失败：{exc}")
        sent, failed, skipped = [], [], []
        for a in self.axes.values():
            a.control_token += 1
            a.local_stop = a.holding = False
            a.reset_stationary(self.clock())
            a.reset_following()
            if (
                not a.owned
                and not all_nodes
                and a.phase not in ("停止待确认", "停机未确认")
            ):
                if a.connected:
                    skipped.append(a.node)
                continue
            a.owned, a.phase, a.stop_at = False, "停止待确认", self.clock()
            a.trajectory = None
            try:
                self.send_stop_frame(a, emergency)
                a.stop_at = self.clock()
                sent.append(a.node)
                kind = "定向掩码 ESTOP 0xC0" if emergency else "STOP 0x63"
                self.log(f"TX {kind}；已写入转换器，等待新门极反馈；无保持力矩", a.node)
            except Exception as exc:
                failed.append(a.node)
                self.log(f"停机帧发送失败：{exc}", a.node)
                a.phase = "停机未确认"
                if self.bus is None:
                    a.gate = None
        message = (
            f"停止帧已写入转换器：ID {sent}；等待新门极反馈，不等于停机已确认。"
            if sent else "本次未发送任何停止帧。"
        )
        if failed:
            message += f" ID {failed} 发送失败，停机未确认；检查硬件急停与支撑。"
        if skipped:
            message += f" ID {skipped} 本会话未接管，未发停止帧；门极开启时须人工双确认停止。"
        self.log(message)
        return {"sentNodes": sent, "failedNodes": failed,
                "skippedNodes": skipped, "message": message}

    def fail_timing(self, code, reason, limit_ms, node=0):
        now = self.clock()
        if self.first_protection is None:
            self.first_protection = {
                "code": code, "reason": reason, "node": node,
                "observedAt": time.time(), "limitMs": limit_ms,
                "periodMs": self.period_ms,
                "leaseAgeMs": max(0, now - self.lease_at) * 1000
                if self.lease_at > -1e9 else None,
                "sendGapMs": max(0, now - self.axes[node].sent_at) * 1000
                if node else None,
                "sessionId": self.session, "revision": self.revision,
                "leaseCount": self.lease_count,
                "lastLeaseRevision": self.last_lease_revision,
                "recentLeases": list(self.lease_history),
            }
        raise TimeoutError(reason)

    def check_following(self, a, sampled):
        # 只用新查询反馈与查询发起前已成功写入的角度配对；
        # 未发送的下一周期规划不代表电机在该采样时刻应该到的位置。
        if a.trajectory is None:
            return
        reference = next((row for row in reversed(a.tx_history)
                          if row[0] <= sampled), None)
        if reference is None:
            return
        a.following_reference_at, a.following_reference, _ = reference
        a.following_error = a.position - a.following_reference
        # 3°仅用于既有诊断持续时间统计；不锁存首保护，也不触发停止。
        # 实测行程、反馈新鲜度、驱动故障及到位判据仍独立核验。
        if abs(a.following_error) <= 3:
            a.error_since = None
        elif a.error_since is None:
            a.error_since = sampled

    def fail_position_limit(self, a, low, high):
        reason = f"ID{a.node} 实测越界"
        if self.first_protection is None:
            now, p = self.clock(), a.trajectory

            def rel(value):
                return (value - a.zero) * a.direction if value is not None else None

            def age(stamp):
                return max(0, now - stamp) * 1000 if stamp > -1e9 else None

            command_low, command_high = low + COMMAND_MARGIN_DEG, high - COMMAND_MARGIN_DEG
            self.first_protection = {
                "code": "position_limit", "reason": reason, "node": a.node,
                "observedAt": time.time(), "sessionId": self.session,
                "revision": self.revision, "phase": a.phase,
                "source": self.streams.source, "periodMs": self.period_ms,
                "reference": a.zero, "direction": a.direction,
                "positionAbsolute": a.position, "positionRelative": rel(a.position),
                "velocityAbsolute": a.velocity, "velocityRelative": a.velocity * a.direction,
                "hardAbsolute": [low, high], "hardRelative": list(a.relative_bounds()),
                "commandAbsolute": [command_low, command_high],
                "commandRelative": list(a.relative_bounds(command=True)),
                "commandMarginDeg": COMMAND_MARGIN_DEG,
                "side": "low" if a.position < low else "high",
                "relativeSide": "low" if rel(a.position) < min(rel(low), rel(high)) else "high",
                "excessDeg": low - a.position if a.position < low else a.position - high,
                "targetAbsolute": p.target if p else None,
                "targetRelative": rel(p.target) if p else None,
                "plannedAbsolute": p.position if p else None,
                "plannedRelative": rel(p.position) if p else None,
                "plannedVelocity": p.velocity if p else None,
                "stoppingAbsolute": p.stopping_position if p else None,
                "txAbsolute": a.tx_position, "txRelative": rel(a.tx_position),
                "txAgeMs": age(a.tx_at), "txRevision": a.tx_revision, "txCount": a.tx_count,
                "positionAgeMs": age(a.position_at), "statusAgeMs": age(a.status_at),
                "gateAgeMs": age(a.gate_at), "faultAgeMs": age(a.fault_at),
                "heartbeatAgeMs": age(a.heartbeat_at), "leaseAgeMs": age(self.lease_at),
                "gate": a.gate, "fault": a.fault, "error": a.error,
                "heartbeatFaultFlags": a.heartbeat_fault_flags,
            }
        raise RuntimeError(reason)

    def trip(self, reason, all_nodes=False):
        if not self.latched:
            self.latched = reason
            self.log("组故障锁存：" + reason)
            self.stop(True, all_nodes)
        elif all_nodes:
            self.stop(True, True)

    def record_stop_confirmation(self, a):
        # 会话内历史证据；失联后保留，但不能代替当前新鲜反馈或下一次停止核验。
        a.stop_confirmed_at = time.time()
        a.stop_confirmed_tx_count = a.stop_tx_count

    def handle_tick_failure(self, exc):
        self.finish_unowned_stop("failed", "停机结果未确认：" + str(exc))
        self.finish_estop_recovery("failed", "清除结果未确认：" + str(exc))
        self.trip(str(exc))

    def tick(self):
        now = self.clock()
        dt, self.last_tick = now - self.last_tick, now
        self.period_ms = dt * 1000
        if not self.connected:
            return
        try:
            if self.bus is None:
                raise ConnectionError("总线已断开；禁止运动，停机状态待实机确认")
            if self.active and dt > .05:
                for a in self.axes.values():
                    a.reset_stationary(now)
            receive_failed = False
            for frame in self.bus.poll():
                try:
                    self.receive(frame)
                except Exception as exc:
                    # 立即撤权停机，但不丢弃同批已读出的后续应答；
                    # 仅按原事务匹配消费，不清空待应答来伪造通信恢复。
                    receive_failed = True
                    self.handle_tick_failure(exc)
            if receive_failed:
                # 异常当周期不再规划、使能或发送运动；后续周期仍只读核验。
                return
            for a in self.axes.values():
                a.update_stationary(now)
            self.tick_estop_recovery(now)
            self.tick_unowned_stop(now)
            if self.active and dt > 0.05:
                self.fail_timing("scheduler", f"运动调度超时：{dt * 1000:.1f}ms > 50ms", 50)
            if self.active and now - self.lease_at > 0.2:
                self.fail_timing("ui_lease", "UI 租约超时：超过 200ms 未收到有效续约", 200)
            # 先统一检查所有已接管轴，后序轴越界时也不先给前序轴发位置帧。
            for a in self.axes.values():
                if not a.owned:
                    continue
                if not a.fresh(now) or a.has_fault:
                    raise RuntimeError(f"ID{a.node} 反馈过期或设备故障")
                lo, hi, _ = a.bounds()
                if not lo <= a.position <= hi:
                    self.fail_position_limit(a, lo, hi)
            # 先核查视觉采集及流授权；采集暂缺先制动，再发送位置保持帧。
            self.streams.tick(now)
            for a in self.axes.values():
                if (a.phase in ("停止待确认", "停机未确认")
                        and not (self.unowned_stopping
                                 and self.unowned_stop["node"] == a.node)):
                    if (
                        a.gate is False
                        and a.gate_at > a.stop_at
                        and now - a.gate_at <= 0.2
                        and a.stop_tx_count > 0
                        and not a.stop_tx_error
                    ):
                        a.phase = "停止输出已确认"
                        self.record_stop_confirmation(a)
                        self.log("RX 门极已关闭；无保持力矩", a.node)
                    elif now - a.stop_at > 0.3:
                        a.phase = "停机未确认"
                        if a.local_stop:
                            a.local_stop = False
                            raise TimeoutError(f"ID{a.node} 本轴停止 300ms 未确认；升级组保护")
                if not a.owned:
                    continue
                lo, hi, amps = a.bounds()
                if a.phase == "等待闭环":
                    if (
                        a.gate is True
                        and a.gate_at >= a.stage_at
                        and a.position_at >= a.stage_at
                    ):
                        a.trajectory = Trajectory(
                            a.position, a.rpm * 6, a.acceleration
                        )
                        a.phase, a.stage_at = "确认位置保持", now
                    elif now - a.stage_at > 0.05:
                        raise TimeoutError(f"ID{a.node} START 未确认")
                    else:
                        continue
                if a.gate is not True:
                    raise RuntimeError(f"ID{a.node} 意外退出闭环")
                gap_limit = min(0.05, a.params["config.can_timeout"] / 2000)
                if self.clock() - a.sent_at > gap_limit:
                    self.fail_timing("send_gap", f"ID{a.node} 发令间隙超时", gap_limit * 1000, a.node)
                if self.clock() - self.lease_at > 0.2:
                    self.fail_timing("ui_lease", f"ID{a.node} UI 租约超时", 200, a.node)
                if a.phase == "确认位置保持":
                    if a.mode == 5 and a.status_at > a.stage_at:
                        a.phase = "位置控制"
                        self.log("RX 位置保持已确认", a.node)
                    elif now - a.stage_at > 0.1:
                        raise TimeoutError(f"ID{a.node} 位置模式未确认")
                a.trajectory.advance(dt)
                if self.cancelled():
                    raise InterruptedError("停止/断开意图已抢占运动授权")
                payload = position_payload(a.trajectory.position, a.rpm, amps)
                self.send_feedback(0x01, a.node, payload, priority=3)
                a.sent_at = a.tx_at = self.clock()
                a.tx_count += 1
                a.tx_position = struct.unpack(">fhh", payload)[0]
                a.tx_history.append((a.tx_at, a.tx_position, self.revision))
                while (len(a.tx_history) > 1
                       and a.tx_at - a.tx_history[1][0] > .5):
                    a.tx_history.popleft()
                if a.tx_revision != self.revision:
                    self.log(
                        f"TX 0x01：绝对 {a.tx_position:.3f}° / "
                        f"{a.rpm:g} RPM / {amps:.1f} A；"
                        "写入转换器成功，到位须看反馈", a.node,
                    )
                a.tx_revision = self.revision
            self.begin_next(now)
            if self.preparation and not self.preparation.done:
                self.preparation.tick()
            if self.bus and now >= self.next_fast:
                self.next_fast = now + 0.05
                for a in self.axes.values():
                    if not a.connected:
                        continue
                    try:
                        self.query_scalar(a, "telemetry.gate_drive")
                        self.send_feedback(0x41, a.node)
                        if a.uses_encoder_counts:
                            self.query_scalar(a, "telemetry.count")
                        if not a.owned:
                            self.send_feedback(0x40, a.node)
                    except NodeReadTimeout as exc:
                        self.lose_node(a, str(exc))
            if self.bus and now >= self.next_slow:
                self.next_slow = now + 0.125
                mt = (0x42, 0x43, 0x44, 0x45)[self.slow_index % 4]
                self.slow_index += 1
                for a in self.axes.values():
                    if not a.connected:
                        continue
                    try:
                        self.send_feedback(
                            mt, a.node, b"\x05" if mt == 0x45 else b""
                        )
                    except NodeReadTimeout as exc:
                        self.lose_node(a, str(exc))
        except Exception as exc:
            self.handle_tick_failure(exc)

    def snapshot(self):
        now = self.clock()
        axes = []
        for a in self.axes.values():
            fresh = self.connected and a.fresh(now)

            def rel(v):
                return (v - a.zero) * a.direction if a.zero is not None else v

            hard_low = hard_high = command_low = command_high = None
            range_issue = "本轴未连接" if not (self.connected and a.connected) else "尚未记录软件参考点"
            if self.connected and a.connected and a.zero is not None:
                try:
                    hard_low, hard_high = a.relative_bounds()
                    command_low, command_high = a.relative_bounds(command=True)
                    range_issue = ""
                except (ValueError, KeyError, TypeError) as exc:
                    range_issue = str(exc)
            p = a.trajectory
            history = list(a.position_history) if fresh else []
            window = history[-1][0] - history[0][0] if len(history) > 1 else 0
            available = self.connected and a.connected
            samples = {
                k: {
                    "value": v if available and now - t < 1 else None,
                    "ageMs": (now - t) * 1000,
                    "valid": available and now - t < 1,
                }
                for k, (v, t) in a.samples.items()
            }

            def estimate(name, keys, compute):
                if all(k in samples and samples[k]["valid"] for k in keys):
                    samples[name] = {
                        "value": compute(),
                        "ageMs": max(samples[k]["ageMs"] for k in keys),
                        "valid": True,
                        "estimated": True,
                    }

            if (a.params.get("config.kt") or 0) > 0 and (
                a.params.get("config.gr") or 0
            ) > 0:
                estimate(
                    "torqueEstimate",
                    ("current",),
                    lambda: samples["current"]["value"]
                    * a.params["config.kt"]
                    * a.params["config.gr"],
                )
            estimate(
                "powerEstimate",
                ("voltage", "busCurrentEstimate"),
                lambda: samples["voltage"]["value"]
                * samples["busCurrentEstimate"]["value"],
            )
            axes.append(
                {
                    "node": a.node,
                    "controlToken": a.control_token,
                    "preparationStage": a.preparation_stage,
                    "preparationDiagnostics": self.preparation.diagnostics()
                    if self.preparation and self.preparation.axis is a else a.preparation_diagnostics,
                    "localCancelReason": a.local_cancel_reason,
                    "holding": a.holding and a.owned,
                    "holdState": ("反馈过期或未知，保持未确认" if not fresh else
                                  "已静止保持（仍有力矩）" if p and not p.segments
                                  and abs(p.target - a.position) < .2 and a.stationary(now)
                                  else "减速保持中，等待实测静止") if a.holding and a.owned else "",
                    "connected": available,
                    "connectionError": a.connection_error,
                    "position": rel(a.position) if fresh else None,
                    "absolute": a.position if fresh else None,
                    "velocity": a.velocity * a.direction if fresh else None,
                    "planned": rel(p.position) if p else None,
                    "target": rel(p.target) if p else None,
                    "complete": bool(
                        p
                        and not p.segments
                        and fresh
                        and abs(p.target - a.position) < 0.2
                        and a.stationary(now)
                    ),
                    "reference": a.zero,
                    "estopClearBlockReason": self.estop_clear_reason(a, now),
                    "unownedStopBlockReason": self.unowned_stop_reason(a, now),
                    "prepareIssues": self.preparation_issues(a)
                    if available else ["本轴未连接"],
                    "positionWindowMs": window * 1000,
                    "positionSpan": (
                        max(v for _, v in history) - min(v for _, v in history)
                    ) if history else None,
                    "positionWindowVelocity": (
                        (history[-1][1] - history[0][1]) / window
                    ) if window > 0 else None,
                    "followingErrorDeg": a.following_error if fresh else None,
                    "followingReferenceAbsolute": a.following_reference if fresh else None,
                    "followingDurationMs": max(0, a.position_at - a.error_since) * 1000
                    if fresh and a.error_since is not None else 0,
                    "txCount": a.tx_count,
                    "txAbsolute": a.tx_position,
                    "txAgeMs": max(0, now - a.tx_at) * 1000
                    if a.tx_count else None,
                    "txRevision": a.tx_revision,
                    "stopTxCount": a.stop_tx_count,
                    "stopTxAgeMs": max(0, now - a.stop_tx_at) * 1000
                    if a.stop_tx_count else None,
                    "stopTxRevision": a.stop_tx_revision,
                    "stopTxKind": a.stop_tx_kind,
                    "stopTxError": a.stop_tx_error,
                    "stopConfirmedAt": a.stop_confirmed_at,
                    "stopConfirmedTxCount": a.stop_confirmed_tx_count,
                    "stationary": fresh and not self.latched and a.stationary(now),
                    "stationaryMs": a.stationary_ms(now) if fresh and not self.latched else 0,
                    "stationaryReason": self.latched or a.stationary_reason(now),
                    "stationaryWindowMs": (a.stationary_observations()[-1][0] - a.stationary_observations()[0][0]) * 1000
                    if a.stationary_observations() else 0,
                    "stationarySamples": len(a.stationary_observations()),
                    "stationarySource": "telemetry.count/int32" if a.uses_encoder_counts else "0x41/float32",
                    "stationaryResolutionDeg": 360 / (65536 * a.params["config.gr"])
                    if a.uses_encoder_counts and (a.params.get("config.gr") or 0) > 0 else a.position_resolution,
                    "encoderCountAgeMs": max(0, now - a.encoder_at) * 1000 if a.encoder_count is not None else None,
                    "stationarySpan": a.stationary_span,
                    "positionResolutionDeg": a.position_resolution,
                    "stationaryPathVelocity": a.stationary_path_velocity,
                    "stationaryDriftVelocity": a.stationary_drift_velocity,
                    "serialNumber": str(
                        a.params.get("system.serial_number", "")
                    ),
                    "enabled": a.owned,
                    "motorMode": a.mode if fresh else None,
                    "gate": a.gate if fresh else None,
                    "phase": a.phase,
                    "valid": fresh,
                    "ageMs": (
                        max(0, now - a.position_at) * 1000
                        if a.position_at > -1e9
                        else None
                    ),
                    "fault": a.fault,
                    "faultAgeMs": (now - a.fault_at) * 1000
                    if a.fault_at > -1e9 else None,
                    "error": a.error,
                    "errorAgeMs": (now - a.status_at) * 1000
                    if a.status_at > -1e9 else None,
                    "heartbeatFlags": a.heartbeat_flags,
                    "heartbeatAgeMs": (now - a.heartbeat_at) * 1000
                    if a.heartbeat_at > -1e9 else None,
                    "heartbeatFaultFlags": a.heartbeat_fault_flags,
                    "issues": a.errors,
                    "samples": samples,
                    "low": a.low,
                    "high": a.high,
                    "hardLow": hard_low, "hardHigh": hard_high,
                    "commandLow": command_low, "commandHigh": command_high,
                    "commandMarginDeg": COMMAND_MARGIN_DEG,
                    "commandRangeIssue": range_issue,
                    "rpm": a.rpm,
                    "amps": a.amps,
                    "direction": a.direction,
                    "acceleration": a.acceleration,
                }
            )
        return {
            "version": 1,
            "kind": "state",
            "sessionId": self.session,
            "revision": self.revision,
            "mode": self.mode,
            "connected": self.connected,
            "active": self.active,
            "stream": self.streams.snapshot(),
            "estopRecovery": dict(self.estop_recovery),
            "unownedStop": dict(self.unowned_stop),
            "fault": self.latched,
            "firstProtection": self.first_protection,
            "periodMs": self.period_ms,
            "leaseCount": self.lease_count,
            "lastLeaseRevision": self.last_lease_revision,
            "leaseAgeMs": (now - self.lease_at) * 1000
            if self.lease_count else None,
            "time": time.time(),
            "axes": axes,
            "logs": list(self.logs),
        }

    def disconnect(self):
        if self.preparation and not self.preparation.done:
            self.preparation.finish(InterruptedError("断开取消位置准备；旧端点会话已关闭"))
        self.finish_unowned_stop("cancelled", "核验被断开取消；须重连核对")
        self.finish_estop_recovery("cancelled", "清除被断开取消，须重连核对")
        self.streams.invalidate(clear_alignment=True)
        if self.active:
            self.stop()
        end = self.clock() + 0.35
        while (
            any(a.phase == "停止待确认" for a in self.axes.values())
            and self.clock() < end
        ):
            self.tick()
            time.sleep(0.01)
        unknown = any(a.phase == "停机未确认" for a in self.axes.values())
        if self.bus:
            try:
                self.bus.close()
            finally:
                self.bus = None
        self.connected = False
        self.pending.clear()
        self.feedback_pending.clear()
        self.session, self.revision = str(uuid.uuid4()), 0
        self.local_revision_floor = 0
        for a in self.axes.values():
            a.zero, a.connected = None, False
            a.reset_following()
            a.reset_stationary(self.clock())
            if a.phase != "停机未确认":
                a.phase = "未连接"
        self.log(
            "已断开；停机未确认，须检查硬件急停和机械支撑"
            if unknown
            else "已断开；参考点和授权清除"
        )
