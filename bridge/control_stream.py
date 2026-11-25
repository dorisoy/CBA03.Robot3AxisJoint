"""控制流、视觉对齐与失锁归位；只由 Engine 单线程调用。"""

import copy
import math
import time
import uuid
from motion import ContinuousTrajectory
from protocol import finite, NODES


def integer(value, name):
    if type(value) is not int or not 0 <= value <= 9007199254740991:
        raise ValueError(f"{name} 必须为非负安全整数")
    return value


def angles(value):
    if not isinstance(value, list) or len(value) != 3:
        raise ValueError("必须提供三个模型角度")
    result = [finite(v) for v in value]
    if any(not lo <= v <= hi for v, lo, hi in zip(result, (-135, -135, 0), (135, 135, 80))):
        raise ValueError("模型角度越界（J3 仅允许 0～80°）")
    return result


def identity(sample):
    fields = ("publisher", "visionSession", "calibration", "targetKind")
    if any(not isinstance(sample.get(k), str) or not 1 <= len(sample[k]) <= 128 for k in fields):
        raise ValueError("视觉来源身份非法")
    if sample["targetKind"] not in ("face", "mouth"):
        raise ValueError("视觉目标类型非法")
    return tuple(sample[k] for k in fields)


class VisionCaptureExpired(ValueError):
    def __init__(self, age):
        super().__init__("视觉采集超过 300ms 或时间非法")
        self.age = age


def validate_sample(sample):
    if not isinstance(sample, dict) or sample.get("schema") != 1 or sample.get("unit") != "deg":
        raise ValueError("视觉 schema 或角度单位非法")
    identity(sample)
    integer(sample.get("seq"), "发布序号")
    integer(sample.get("captureSeq"), "采集序号")
    captured = finite(sample.get("capturedAtMs"))
    age = max(finite(sample.get("ageMs")), time.time() * 1000 - captured)
    if age < 0 or captured > time.time() * 1000 + 50:
        raise ValueError("视觉采集超过 300ms 或时间非法")
    if sample.get("mode") != "face" or type(sample.get("valid")) is not bool:
        raise ValueError("只允许显式视觉模式")
    count = sample.get("faceCount")
    if type(count) is not int or count not in (0, 1):
        raise ValueError("多人或人数状态非法")
    if sample.get("status") not in ("tracking", "no_face", "lost"):
        raise ValueError("视觉源不可用；停止并重新确认")
    if sample["valid"] and (count != 1 or sample["status"] != "tracking"):
        raise ValueError("有效追踪必须为单人")
    angles(sample.get("angles"))
    angles(sample.get("home"))
    if age > 300:
        raise VisionCaptureExpired(age)
    return age


class MotionStreams:
    def __init__(self, engine):
        self.engine = engine
        self.alignment = None
        self.invalidate()

    def invalidate(self, clear_alignment=False):
        self.generation = ""
        self.source = "none"
        self.nodes = []
        self.seq = -1
        self.capture_seq = -1
        self.publication_seq = -1
        self.capture_time = -1
        self.acquired_at = -1e9
        self.capture_history = []
        self.state = "未开启"
        self.lost_at = self.good_at = self.arrived_at = None
        self.good_frames = 0
        self.deadline = None
        self.latest_targets = None
        self.capture_wait = None
        if clear_alignment:
            self.alignment = None

    def signature(self, nodes):
        return (self.engine.session, tuple(
            (n, (a.params.get("system.serial_number"), a.zero, a.direction, a.low, a.high))
            for n in nodes for a in (self.engine.axes[n],)
        ))

    def selected(self, nodes, enabled=True):
        now = self.engine.clock()
        axes = [self.engine.axes[n] for n in nodes]
        if self.engine.latched:
            raise ValueError("故障锁存；必须断开重新确认")
        for a in axes:
            if not a.fresh(now) or a.has_fault or a.errors or a.zero is None:
                raise ValueError(f"ID{a.node} 未就绪或尚未记录参考")
            a.command_bounds()
            if enabled and (not a.owned or a.holding or a.phase != "位置控制" or a.gate is not True):
                raise ValueError(f"ID{a.node} 尚未显式使能")
        return axes

    def align(self, nodes, data):
        if any(self.engine.axes[n].owned or n in self.nodes for n in nodes):
            raise ValueError("捕获轴必须停止输出")
        if self.source not in ("none", "vision") or (self.source == "vision" and self.state != "跟随"):
            raise ValueError("来源互斥或失锁归位中；禁止新增对齐")
        sample = data.get("sample")
        validate_sample(sample)
        if not sample["valid"]:
            raise ValueError("捕获对齐需要新鲜单人追踪")
        axes = self.selected(nodes, enabled=False)
        confirmations = data.get("confirmations", {})
        references = angles(sample["angles"])
        homes = angles(sample["home"])
        for a in axes:
            row = confirmations.get(str(a.node), {})
            if any(row.get(k) is not True for k in ("direction", "range", "home", "reference")):
                raise ValueError(f"ID{a.node} 方向、行程、归位和参考尚未全部确认")
            if (a.gate is not False or not a.stationary(self.engine.clock())
                    or abs(a.position - a.zero) > .3):
                raise ValueError("捕获对齐要求门极关闭、静止且位于已记录参考姿态")
            home = a.zero + a.direction * (homes[a.node - 1] - references[a.node - 1])
            a.validate_target(home)
        previous = self.alignment
        if self.source == "vision" and (not previous or previous["identity"] != identity(sample)
                                         or previous["home"] != homes):
            raise ValueError("运行中的视觉身份或归位基准改变")
        if previous and previous["identity"] == identity(sample) and previous["home"] == homes:
            merged = sorted(set(previous["nodes"]) | set(nodes))
            rows = dict(previous["signature"][1])
            rows.update(dict(self.signature(nodes)[1]))
            values = previous["reference"][:]
            for n in nodes:
                values[n - 1] = references[n - 1]
            self.alignment = {**previous, "nodes": merged, "reference": values,
                              "signature": (self.engine.session, tuple((n, rows[n]) for n in merged))}
        else:
            self.alignment = {
                "identity": identity(sample), "signature": self.signature(nodes),
                "nodes": nodes[:], "reference": references, "home": homes,
            }

    def resume_alignment(self, a, sample):
        validate_sample(sample)
        if not sample["valid"] or sample["capturedAtMs"] <= a.hold_at_ms:
            raise ValueError("恢复视觉须使用停止转动之后的新鲜单人采集")
        references, homes = angles(sample["angles"]), angles(sample["home"])
        reference = references[a.node - 1] - (a.trajectory.target - a.zero) * a.direction
        home = a.zero + a.direction * (homes[a.node - 1] - reference)
        a.validate_target(home)
        previous = self.alignment
        if self.source == "vision" and (self.state != "跟随" or not previous or
                previous["identity"] != identity(sample) or previous["home"] != homes):
            raise ValueError("其他轴视觉身份或归位基准已变化，不能恢复本轴")
        if previous and previous["identity"] == identity(sample) and previous["home"] == homes:
            nodes = sorted(set(previous["nodes"]) | {a.node})
            rows = dict(previous["signature"][1])
            rows.update(dict(self.signature([a.node])[1]))
            values = previous["reference"][:]
            values[a.node - 1] = reference
            self.alignment = {**previous, "nodes": nodes, "reference": values,
                              "signature": (self.engine.session, tuple((n, rows[n]) for n in nodes))}
        else:
            references[a.node - 1] = reference
            self.alignment = {"identity": identity(sample), "nodes": [a.node],
                              "signature": self.signature([a.node]), "reference": references, "home": homes}
        return {"modelReference": reference}

    def alignment_matches(self, nodes, sample):
        align = self.alignment
        if not align or not set(nodes) <= set(align["nodes"]):
            return False
        rows = dict(align["signature"][1])
        return (align["signature"][0] == self.engine.session
                and all(rows[n] == row for n, row in self.signature(nodes)[1])
                and align["identity"] == identity(sample) and align["home"] == sample["home"])

    def tokens(self):
        return {str(n): self.engine.axes[n].control_token for n in self.nodes}

    def remove(self, node, clear_alignment=True):
        self.nodes = [n for n in self.nodes if n != node]
        if self.latest_targets is not None:
            self.latest_targets.pop(str(node), None)
        self.arrived_at = None
        if clear_alignment and self.alignment and node in self.alignment["nodes"]:
            align = self.alignment
            members = [n for n in align["nodes"] if n != node]
            self.alignment = {**align, "nodes": members, "signature": (
                align["signature"][0], tuple(row for row in align["signature"][1] if row[0] != node)
            )} if members else None
        if not self.nodes and self.source != "none":
            self.invalidate()

    def attach(self, nodes, data):
        if (self.source == "none" or data.get("source") != self.source
                or data.get("generation") != self.generation or any(n in self.nodes for n in nodes)):
            raise ValueError("来源或流代次不符，不能重复加入轴")
        axes = self.selected(nodes)
        if self.source == "vision":
            sample = data.get("sample")
            validate_sample(sample)
            if self.state != "跟随" or not sample["valid"] or not self.alignment_matches(nodes, sample):
                raise ValueError("失锁、归位或对齐无效；禁止新增视觉轴")
        for a in axes:
            if a.trajectory is None or a.trajectory.segments or abs(a.trajectory.velocity) > .2:
                raise ValueError("新增轴须从当前位置保持开始")
        for a in axes:
            a.trajectory = ContinuousTrajectory.from_trajectory(a.trajectory)
            if self.source == "vision" and self.latest_targets is not None:
                self.latest_targets[str(a.node)] = a.trajectory.target
        self.nodes = sorted(self.nodes + nodes)
        return {"generation": self.generation, "axisTokens": self.tokens()}

    def begin(self, nodes, data):
        source = data.get("source")
        if source not in ("manual", "vision") or self.source != "none":
            raise ValueError("控制来源非法或旧流尚未结束")
        axes = self.selected(nodes)
        if any(a.owned and (not a.stationary(self.engine.clock()) or a.trajectory is None or
                           abs(a.trajectory.velocity) > .2 or a.trajectory.segments)
               for a in self.engine.axes.values()):
            raise ValueError("切换控制来源前须减速保持并等待静止")
        if source == "vision":
            sample = data.get("sample")
            validate_sample(sample)
            align = self.alignment
            if not sample["valid"] or not self.alignment_matches(nodes, sample):
                raise ValueError("视觉对齐已失效；请停止输出并重新捕获确认")
        self.invalidate()
        self.source, self.nodes = source, nodes[:]
        self.generation = str(uuid.uuid4())
        self.state = "手动实时" if source == "manual" else "跟随"
        for a in axes:
            a.trajectory = ContinuousTrajectory.from_trajectory(a.trajectory)
        if source == "vision":
            try:
                self.visual(sample)
            except Exception:
                self.engine.trip("视觉起始目标非法；授权已撤销")
                raise
        return {"generation": self.generation, "axisTokens": self.tokens()}

    def targets(self, targets):
        if (not isinstance(targets, dict) or not targets
                or not set(targets) <= {str(n) for n in self.nodes}):
            raise ValueError("目标必须是当前成员的合法非空子集")
        axes = self.selected([n for n in self.nodes if str(n) in targets])
        planned = []
        for a in axes:
            low, high, _ = a.bounds()
            goal = a.validate_target(targets.get(str(a.node)))
            p = copy.deepcopy(a.trajectory)
            p.set_target(goal, low, high)
            planned.append((a, p))
        for a, p in planned:
            a.trajectory = p

    def hold(self):
        planned = []
        for a in self.selected(self.nodes):
            low, high, _ = a.bounds()
            p = copy.deepcopy(a.trajectory)
            p.set_target(p.target, low, high, pause=True)
            planned.append((a, p))
        for a, p in planned:
            a.trajectory = p

    def end(self):
        if self.source != "none":
            self.hold()
        self.invalidate()

    def sample(self, data):
        if not self.generation or data.get("generation") != self.generation:
            raise ValueError("旧流代次已拒绝")
        seq = integer(data.get("seq"), "流序号")
        if seq <= self.seq:
            raise ValueError("重复或倒序流样本已拒绝")
        try:
            tokens = data.get("axisTokens")
            if (not isinstance(tokens, dict) or not tokens or not set(tokens) <= {str(n) for n in NODES}):
                raise ValueError("必须提供合法逐轴控制令牌")
            for key, value in tokens.items():
                integer(value, "轴控制令牌")
                axis = self.engine.axes[int(key)]
                if value > axis.control_token or (value == axis.control_token and int(key) not in self.nodes):
                    raise ValueError("轴令牌未经授权或节点不是当前成员")
            current = [n for n in self.nodes if tokens.get(str(n)) == self.engine.axes[n].control_token]
            cancelled = [int(k) for k in tokens if int(k) not in current]
            if self.source == "manual":
                values = data.get("targets")
                if not isinstance(values, dict) or not values or not set(values) <= set(tokens):
                    raise ValueError("手动目标结构或令牌覆盖非法")
                values = {k: finite(v) for k, v in values.items()}
                if any(not -135 <= v <= 135 for v in values.values()):
                    raise ValueError("手动目标越限")
                goals = {str(n): self.engine.axes[n].zero + self.engine.axes[n].direction * values[str(n)]
                         for n in current if str(n) in values}
                if goals:
                    self.targets(goals)
            else:
                self.visual(data.get("sample"), current)
            self.seq = seq
            return {"cancelledNodes": cancelled, "axisTokens": self.tokens()}
        except VisionCaptureExpired as exc:
            self.wait_for_capture(data.get("sample"), exc.age)
            return {"waitingForCapture": True, "axisTokens": self.tokens()}
        except Exception:
            self.engine.trip("连续目标非法或视觉授权失效")
            raise

    def visual(self, sample, target_nodes=None):
        try:
            age = validate_sample(sample)
        except VisionCaptureExpired:
            if not self.alignment_matches(self.nodes, sample):
                raise ValueError("视觉身份、校准或电机参考改变")
            raise
        align = self.alignment
        if not self.alignment_matches(self.nodes, sample):
            raise ValueError("视觉身份、校准或电机参考改变")
        # 发布时刻和心跳不能刷新采集时钟。
        if (sample["seq"] <= self.publication_seq or sample["captureSeq"] <= self.capture_seq or
                sample["capturedAtMs"] <= self.capture_time):
            raise ValueError("重复或倒序采集帧")
        self.capture_seq, self.capture_time = sample["captureSeq"], sample["capturedAtMs"]
        self.publication_seq = sample["seq"]
        now = self.engine.clock()
        self.acquired_at = now - age / 1000
        self.capture_history.append({"captureSeq": self.capture_seq, "publicationSeq": self.publication_seq,
                                     "acceptedAt": time.time(), "ageMs": age,
                                     "gapMs": (now - self.capture_history[-1]["acceptedMono"]) * 1000 if self.capture_history else None,
                                     "acceptedMono": now})
        self.capture_history = self.capture_history[-16:]
        values = angles(sample["angles"])
        authorized = self.nodes if target_nodes is None else target_nodes
        goals = {str(n): self.engine.axes[n].zero + self.engine.axes[n].direction *
                 (values[n - 1] - align["reference"][n - 1]) for n in authorized}
        # 原始三角结构与范围始终核验；旧令牌不按重启后的参考解算目标。
        # 失锁帧的当前授权目标仍须通过命令限位。
        for n in authorized:
            self.engine.axes[n].validate_target(goals[str(n)])
        goals = {str(n): goals[str(n)] if n in authorized else
                 (self.latest_targets or {}).get(str(n), self.engine.axes[n].trajectory.target)
                 for n in self.nodes}
        self.latest_targets = goals
        if not sample["valid"]:
            self.good_at, self.good_frames = None, 0
            if self.lost_at is None:
                self.lost_at = now
                if self.state == "跟随":
                    self.hold()
                    self.state = "失锁保持"
        else:
            if self.good_at is None:
                self.good_at = now
            self.good_frames += 1
            if self.state == "跟随":
                selected_goals = goals if target_nodes is None else {str(n): goals[str(n)] for n in target_nodes}
                if selected_goals:
                    self.targets(selected_goals)

    def wait_for_capture(self, rejected=None, age=None):
        # 仅视觉采集暂缺：电机反馈、门极、租约和调度保护仍由 Engine 独立核验。
        # 保留本次流代次与身份；不写首故障、不发急停、不允许旧帧恢复运动。
        if self.source == "vision" and self.state != "采集等待保持":
            self.hold()
            self.record_capture_wait(self.engine.clock(), rejected, age)
            self.state = "采集等待保持"
            self.good_at, self.good_frames = None, 0
            self.arrived_at = self.deadline = None
            self.engine.log("视觉采集超过300ms，减速保持等待新帧；不锁存、不重新启动")

    def record_capture_wait(self, now, rejected=None, age=None):
        self.capture_wait = {
            "code": "vision_capture_wait", "reason": "视觉采集超过 300ms；减速保持等待新帧", 
            "observedAt": time.time(), "sessionId": self.engine.session,
            "revision": self.engine.revision, "source": "vision", "nodes": self.nodes[:],
            "generation": self.generation, "captureSeq": self.capture_seq,
            "publicationSeq": self.publication_seq, "capturedAtMs": self.capture_time,
            "captureAgeMs": (now - self.acquired_at) * 1000, "limitMs": 300,
            "periodMs": self.engine.period_ms,
            "leaseAgeMs": (now - self.engine.lease_at) * 1000,
            "trigger": "sample_rejected" if rejected is not None else "source_clock",
            "rejectedCapture": {"captureSeq": rejected["captureSeq"], "publicationSeq": rejected["seq"],
                                "capturedAtMs": rejected["capturedAtMs"], "ageMs": age} if rejected is not None else None,
            "recentCaptures": [{k: v for k, v in row.items() if k != "acceptedMono"}
                               for row in self.capture_history],
        }

    def tick(self, now):
        if self.source != "vision":
            return
        if now - self.acquired_at > .3:
            self.wait_for_capture()
            return
        self.selected(self.nodes)
        stable = self.good_at is not None and now - self.good_at >= .5 and self.good_frames >= 5
        if self.state == "采集等待保持" and stable:
            self.state, self.lost_at = "跟随", None
            self.targets(self.latest_targets)
            self.engine.log("同一视觉身份新鲜采集已稳定，自动恢复本次跟随；未重发START")
        if self.state == "失锁保持":
            if stable:
                self.state, self.lost_at = "跟随", None
                self.targets(self.latest_targets)
            elif now - self.lost_at >= 1:
                goals = {str(n): self.engine.axes[n].zero + self.engine.axes[n].direction *
                         (self.alignment["home"][n - 1] - self.alignment["reference"][n - 1])
                         for n in self.nodes}
                self.targets(goals)
                duration = max(sum(s[0] for s in self.engine.axes[n].trajectory.segments) for n in self.nodes)
                self.deadline, self.state = now + duration + 2, "归位中"
        if self.state == "归位中":
            arrived = all(abs(self.engine.axes[n].position - self.engine.axes[n].trajectory.target) <= .3
                          and not self.engine.axes[n].trajectory.segments
                          and self.engine.axes[n].stationary(now) for n in self.nodes)
            self.arrived_at = (self.arrived_at if self.arrived_at is not None else now) if arrived else None
            if self.arrived_at is not None and now - self.arrived_at >= .2:
                self.state = "归位保持（仍有力矩）"
            elif now > self.deadline:
                raise TimeoutError("视觉归位实测到位超时")
        if self.state == "归位保持（仍有力矩）" and stable:
            self.state, self.lost_at = "跟随", None
            self.targets(self.latest_targets)

    def snapshot(self):
        return {"generation": self.generation, "source": self.source, "nodes": self.nodes,
                "state": self.state, "aligned": self.alignment is not None,
                "axisTokens": self.tokens(), "captureWait": self.capture_wait}
