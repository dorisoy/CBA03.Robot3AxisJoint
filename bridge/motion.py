"""共享的解析梯形轨迹；改变目标先连续制动，不对实测位置做吸附。"""

from collections import deque
import math
from protocol import finite


# 三轴相对软件参考点的保护上限；不改写固件绝对限位。
JOINT_MIN_DEG = -135.0
JOINT_MAX_DEG = 135.0
FAST_ACCELERATION = 360.0


def maximum_response_rpm(p):
    """只取在线只读速度限制内的最大整 RPM；未知或不足1 RPM时闭锁。"""
    try:
        cap = finite(p.get("config.v_max"))
    except ValueError as exc:
        raise ValueError("快速响应速度上限未知：config.v_max 必须为有限正数") from exc
    if cap <= 0:
        raise ValueError("快速响应速度上限非法：config.v_max 必须为正数")
    rpm = math.floor(min(cap, 32767 * math.pi / 30) * 30 / math.pi)
    # 消除浮点换算的边界误差；绝不向上超出读回值。
    while rpm > 0 and rpm * math.pi / 30 > cap:
        rpm -= 1
    while rpm < 32767 and (rpm + 1) * math.pi / 30 <= cap:
        rpm += 1
    if rpm < 1:
        raise ValueError("在线速度限制不足1 RPM，快速响应启动已阻断")
    return rpm


class Trajectory:
    def __init__(self, position=0.0, speed=6.0, acceleration=12.0):
        self.position = finite(position)
        self.velocity = 0.0
        self.speed, self.acceleration = finite(speed), finite(acceleration)
        if self.speed <= 0 or self.acceleration <= 0:
            raise ValueError("速度、加速度必须为正")
        self.target = self.position
        self.segments = deque()

    @property
    def stopping_position(self):
        return self.position + self.velocity * abs(self.velocity) / (
            2 * self.acceleration
        )

    def set_target(self, target, low, high, pause=False):
        target, low, high = map(finite, (target, low, high))
        stop = self.stopping_position
        if (
            not low <= self.position <= high
            or not low - 1e-9 <= stop <= high + 1e-9
        ):
            raise ValueError("当前位置或连续制动终点超出行程")
        if pause:
            target = stop
        if not low <= target <= high:
            raise ValueError("目标超出有效行程")
        segments = deque()
        a = self.acceleration
        if abs(self.velocity) > 1e-12:
            segments.append(
                [abs(self.velocity) / a, -math.copysign(a, self.velocity)]
            )
        distance = abs(target - stop)
        direction = 1 if target >= stop else -1
        peak = min(self.speed, math.sqrt(distance * a))
        if peak > 1e-12:
            ramp = peak / a
            cruise = max(0, (distance - peak * peak / a) / peak)
            segments.extend(
                [[ramp, direction * a], [cruise, 0], [ramp, -direction * a]]
            )
        self.segments, self.target = segments, target

    def advance(self, dt):
        dt = finite(dt)
        if not 0 <= dt <= 0.05:
            raise ValueError("规划周期超出允许范围")
        while self.segments and dt > 1e-12:
            duration, acceleration = self.segments[0]
            t = min(dt, duration)
            self.position += self.velocity * t + acceleration * t * t / 2
            self.velocity += acceleration * t
            self.segments[0][0] -= t
            dt -= t
            if self.segments[0][0] <= 1e-12:
                self.segments.popleft()
        # 仅消除解析积分浮点尾差；不改传感器值、不提前结束制动。
        if (
            not self.segments
            and abs(self.velocity) < 1e-9
            and abs(self.position - self.target) < 1e-8
        ):
            self.position, self.velocity = self.target, 0.0
        return self.position, self.velocity


class ContinuousTrajectory(Trajectory):
    """从当前规划位置和速度续算；同向新目标不先停车。"""

    def set_target(self, target, low, high, pause=False):
        target, low, high = map(finite, (target, low, high))
        stop = self.stopping_position
        if (not low <= self.position <= high or
                not low - 1e-9 <= stop <= high + 1e-9):
            raise ValueError("当前位置或连续制动终点超出行程")
        if pause:
            return super().set_target(target, low, high, pause=True)
        if not low <= target <= high:
            raise ValueError("目标超出有效行程")
        delta = target - self.position
        direction = 1 if delta >= 0 else -1
        velocity = self.velocity * direction
        distance = abs(delta)
        # 反向或目标落在当前制动距离内时，先连续制动再反向。
        if velocity < 0 or distance < velocity * velocity / (2 * self.acceleration):
            return super().set_target(target, low, high)
        a = self.acceleration
        peak = min(self.speed, math.sqrt(a * distance + velocity * velocity / 2))
        acceleration_time = max(0, (peak - velocity) / a)
        deceleration_time = peak / a
        ramp_distance = (2 * peak * peak - velocity * velocity) / (2 * a)
        cruise_time = max(0, (distance - ramp_distance) / peak) if peak else 0
        self.segments = deque(
            [duration, value] for duration, value in (
                (acceleration_time, direction * a),
                (cruise_time, 0),
                (deceleration_time, -direction * a),
            ) if duration > 1e-12
        )
        self.target = target

    @classmethod
    def from_trajectory(cls, previous):
        result = cls(previous.position, previous.speed, previous.acceleration)
        result.velocity = previous.velocity
        result.target = previous.target
        result.segments = deque(segment[:] for segment in previous.segments)
        return result


REQUIRED = (
    "config.gr",
    "config.kt",
    "config.i_max",
    "config.v_max",
    "config.p_min",
    "config.p_max",
    "config.torque_lim",
    "config.pre_calibrated",
    "config.input_mode",
    "config.enable_vel_limit",
    "config.can_timeout",
    "system.can_prot_active",
    "system.can_baud_active",
    "system.can_fd",
    "system.serial_number",
    "system.fw_version_major",
    "system.fw_version_minor",
    "system.fw_version_revision",
    "telemetry.fault",
    "telemetry.gate_drive",
    "telemetry.mode",
)


def validate_parameters(p):
    errors = []
    for name in REQUIRED:
        if name not in p or p[name] is None:
            errors.append(f"缺少 {name}")
        elif not isinstance(p[name], (int, float, bool)) or not math.isfinite(
            p[name]
        ):
            errors.append(f"非法 {name}")
    if errors:
        return errors
    if (
        p["system.can_prot_active"] != 1
        or p["system.can_fd"]
        or p["system.can_baud_active"] != 1000000
    ):
        errors.append(
            "仅支持 CyberBeast / Classic CAN / 1Mbit/s；读回 "
            f"can_prot_active={p['system.can_prot_active']}、"
            f"can_fd={p['system.can_fd']}、"
            f"can_baud_active={p['system.can_baud_active']}"
        )
    for name in ("config.gr", "config.kt", "config.i_max", "config.v_max"):
        if p[name] <= 0:
            errors.append(f"{name}={p[name]}；必须为正")
    if p["config.p_min"] > p["config.p_max"] or p["config.torque_lim"] < 0:
        errors.append(
            f"固件位置/力矩限制非法：p_min={p['config.p_min']}、"
            f"p_max={p['config.p_max']}、torque_lim={p['config.torque_lim']}"
        )
    if not 100 <= p["config.can_timeout"] <= 500:
        errors.append(
            f"config.can_timeout={p['config.can_timeout']}ms；"
            "必须为 100～500ms；不自动改写"
        )
    if p["config.input_mode"] not in (1, 2, 3, 5):
        errors.append(
            f"config.input_mode={p['config.input_mode']}；"
            "不属于支持的位置路径（1/2/3/5）"
        )
    if p["config.enable_vel_limit"] is not True:
        errors.append("config.enable_vel_limit 未开启；帧内 RPM 限速不能保证生效，请显式准备位置控制")
    if not p["config.pre_calibrated"]:
        errors.append("未标定：config.pre_calibrated 未置位；不自动标定")
    return errors


def runtime_limits(p, zero, direction, low, high, rpm, amps, acceleration):
    low, high, rpm, amps, acceleration = map(
        finite, (low, high, rpm, amps, acceleration)
    )
    if (direction not in (-1, 1)
            or not JOINT_MIN_DEG <= low < high <= JOINT_MAX_DEG):
        raise ValueError(f"台架方向或行程非法（相对范围 {JOINT_MIN_DEG:g}～{JOINT_MAX_DEG:g}°）")
    if (
        not rpm.is_integer()
        or not 1 <= rpm <= 32767
        or rpm * math.pi / 30 > p["config.v_max"] + 1e-8
    ):
        raise ValueError("帧限速必须为正整数 RPM，且不能超过在线限制")
    cap = p["config.i_max"]
    if p["config.torque_lim"] > 0:
        cap = min(
            cap, p["config.torque_lim"] / (p["config.kt"] * p["config.gr"])
        )
    amps = math.floor(amps * 10 + 1e-9) / 10
    if not 0.1 <= amps <= cap or not 0 < acceleration <= FAST_ACCELERATION:
        raise ValueError("临时电流或加速度超出允许范围")
    ends = sorted((zero + direction * low, zero + direction * high))
    if p["config.p_min"] != 0 or p["config.p_max"] != 0:
        ends = [
            max(ends[0], math.degrees(p["config.p_min"])),
            min(ends[1], math.degrees(p["config.p_max"])),
        ]
    if ends[0] >= ends[1]:
        raise ValueError("台架与固件位置限制没有有效交集")
    return ends[0], ends[1], amps
