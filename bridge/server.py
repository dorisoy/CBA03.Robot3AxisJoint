"""版本化 JSON Lines 进程桥：stdout 仅协议，输入/输出都有界。"""

import json
import queue
import sys
import threading
import time

from engine import Engine
from can_endpoint import PositionPreparation

MAX_LINE = 262144


def reject_constant(value):
    raise ValueError("不允许 NaN/Infinity")


class Service:
    def __init__(self, reader=None, writer=None):
        self.reader = reader or sys.stdin
        self.writer = writer or sys.stdout
        self.commands = queue.Queue(64)
        self.samples = queue.Queue(1)
        self.leases = queue.Queue(4)
        self.urgent = queue.Queue(8)
        self.replies = queue.Queue(64)
        self.states = queue.Queue(1)
        self.output_ready = threading.Event()
        self.cancel = threading.Event()
        self.eof = threading.Event()
        self.broken = threading.Event()
        self.engine = Engine(cancelled=self.cancel.is_set)
        self.running = True
        self.deferred = None
        self.axis_cancel_at = {}

    def enqueue_reply(self, reply):
        self.replies.put_nowait(reply)
        self.output_ready.set()

    def input_loop(self):
        try:
            while True:
                line = self.reader.readline(MAX_LINE + 1)
                if not line:
                    break
                if len(line) > MAX_LINE or not line.endswith("\n"):
                    raise ValueError("输入消息过长或未完整结束")
                msg = json.loads(line, parse_constant=reject_constant)
                if (
                    not isinstance(msg, dict)
                    or not isinstance(msg.get("requestId"), str)
                    or len(msg["requestId"]) > 80
                ):
                    raise ValueError("请求结构或 requestId 非法")
                msg["receivedAt"] = time.monotonic()
                if msg.get("action") in ("stop", "safety_stop", "stop_unowned", "estop", "disconnect", "pause", "stream_end"):
                    self.cancel.set()
                    self.urgent.put_nowait(msg)
                elif msg.get("action") in ("stop_axis", "hold_axis", "hold"):
                    self.urgent.put_nowait(msg)
                elif msg.get("action") == "lease":
                    # 租约独立优先，但绝不设置停止取消标记；仍由 Engine 核验会话和版本。
                    self.leases.put_nowait(msg)
                elif msg.get("action") == "stream_sample":
                    try:
                        stale = self.samples.get_nowait()
                        self.enqueue_reply({"version": 1, "kind": "reply", "requestId": stale["requestId"],
                                               "ok": False, "error": "旧目标被更新样本取代"})
                    except queue.Empty:
                        pass
                    self.samples.put_nowait(msg)
                else:
                    self.commands.put_nowait(msg)
        except Exception as exc:
            print(f"输入终止：{exc}", file=sys.stderr)
        finally:
            self.eof.set()
            self.cancel.set()

    def output_loop(self):
        try:
            while self.running or not self.replies.empty():
                # 先清通知再读有界槽，避免应答进入后还等待下一次状态轮询。
                self.output_ready.clear()
                try:
                    obj = self.replies.get_nowait()
                except queue.Empty:
                    try:
                        obj = self.states.get_nowait()
                    except queue.Empty:
                        self.output_ready.wait(.02)
                        continue
                line = json.dumps(
                    obj,
                    ensure_ascii=False,
                    allow_nan=False,
                    separators=(",", ":"),
                )
                if len(line.encode("utf-8")) > MAX_LINE:
                    raise ValueError("输出消息超出协议上限")
                self.writer.write(line + "\n")
                self.writer.flush()
        except Exception as exc:
            print(f"输出终止：{exc}", file=sys.stderr)
            self.broken.set()
            self.cancel.set()

    def publish(self):
        try:
            self.states.get_nowait()
        except queue.Empty:
            pass
        self.states.put_nowait(self.engine.snapshot())
        self.output_ready.set()

    def handle(self, msg):
        try:
            limit = 0.15 if msg.get("action") == "lease" else 0.2
            if time.monotonic() - msg["receivedAt"] > limit and msg.get(
                "action"
            ) not in ("stop", "stop_axis", "hold", "hold_axis", "safety_stop", "estop", "disconnect", "pause", "stream_end", "parameters", "connect"):
                raise TimeoutError("命令已过期")
            if msg.get("action") == "stream_sample" and isinstance(msg.get("data", {}).get("sample"), dict):
                sample = msg["data"]["sample"]
                sample["ageMs"] = sample.get("ageMs", 1e9) + (time.monotonic() - msg["receivedAt"]) * 1000
            action = msg.get("action")
            data = msg.get("data") or {}
            nodes = data.get("nodes", [1, 2, 3])
            if action not in ("lease", "stream_sample", "stop", "stop_axis", "hold", "hold_axis", "safety_stop", "estop", "disconnect", "parameters", "ports", "connect"):
                if any(msg["receivedAt"] <= self.axis_cancel_at.get((msg.get("sessionId"), n), -1) for n in nodes):
                    raise InterruptedError("本轴排队操作已被局部停止取消")
                # 仅承接局部操作前已经排队的其他轴请求；全局来源/会话屏障仍拒绝旧版本。
                revision = msg.get("revision")
                if (type(revision) is int and self.engine.local_revision_floor <= revision < self.engine.revision):
                    msg = {**msg, "revision": self.engine.revision + 1}
            result = self.engine.command(msg)
            if isinstance(result, PositionPreparation):
                self.deferred = (msg, result)
                return
            if action in ("stop_axis", "hold_axis", "hold"):
                for n in nodes:
                    self.axis_cancel_at[(msg["sessionId"], n)] = msg["receivedAt"]
            if action not in ("lease", "parameters", "ports", "connect", "disconnect"):
                result = {**(result or {}), "revision": self.engine.revision}
            reply = {
                "version": 1,
                "kind": "reply",
                "requestId": msg["requestId"],
                "ok": True,
                "sessionId": self.engine.session,
                "revision": self.engine.revision,
                "data": result,
            }
        except Exception as exc:
            reply = {
                "version": 1,
                "kind": "reply",
                "requestId": msg["requestId"],
                "ok": False,
                "sessionId": self.engine.session,
                "revision": self.engine.revision,
                "error": str(exc),
                "time": time.time(),
                "fault": self.engine.latched,
                "firstProtection": self.engine.first_protection,
                "streamGeneration": self.engine.streams.generation,
            }
            self.engine.log("请求拒绝：" + str(exc))
        try:
            self.enqueue_reply(reply)
        except queue.Full:
            self.broken.set()

    def finish_deferred(self):
        if self.deferred is None:
            return
        msg, task = self.deferred
        if not task.done:
            return
        self.deferred = None
        error = task.error
        if error is None and (task.session != self.engine.session or task.token != task.axis.control_token):
            error = InterruptedError("位置准备应答已被停止或旧会话取消")
        reply = {"version": 1, "kind": "reply", "requestId": msg["requestId"],
                 "sessionId": task.session, "revision": self.engine.revision,
                 "ok": error is None}
        if error is not None:
            reply["error"] = str(error)
        else:
            reply["data"] = {"node": task.axis.node, "stage": task.stage,
                             "controlToken": task.axis.control_token, "revision": self.engine.revision}
        try:
            self.enqueue_reply(reply)
        except queue.Full:
            self.broken.set()

    def run(self):
        threads = [
            threading.Thread(target=self.input_loop, daemon=True),
            threading.Thread(target=self.output_loop, daemon=True),
        ]
        for thread in threads:
            thread.start()
        next_tick = next_state = time.monotonic()
        try:
            while not self.eof.is_set() and not self.broken.is_set():
                try:
                    urgent = self.urgent.get_nowait()
                except queue.Empty:
                    urgent = None
                if urgent is not None:
                    # 停止意图抢占并废弃尚未处理的旧目标/使能。
                    while urgent.get("action") not in ("stop_axis", "hold_axis", "hold"):
                        try:
                            try:
                                stale = self.samples.get_nowait()
                            except queue.Empty:
                                try:
                                    stale = self.leases.get_nowait()
                                except queue.Empty:
                                    stale = self.commands.get_nowait()
                            self.enqueue_reply(
                                {
                                    "version": 1,
                                    "kind": "reply",
                                    "requestId": stale["requestId"],
                                    "ok": False,
                                    "error": "已被停止意图取消",
                                }
                            )
                        except queue.Empty:
                            break
                    self.handle(urgent)
                    if self.urgent.empty():
                        self.cancel.clear()
                else:
                    # 每次只处理一条，保证 CAN tick 不被普通请求队列饿死。
                    try:
                        try:
                            message = self.leases.get_nowait()
                        except queue.Empty:
                            try:
                                message = self.commands.get_nowait()
                            except queue.Empty:
                                message = self.samples.get_nowait()
                        self.handle(message)
                    except queue.Empty:
                        pass
                self.finish_deferred()
                now = time.monotonic()
                if now >= next_tick:
                    self.engine.tick()
                    self.finish_deferred()
                    next_tick = now + 0.02
                if now >= next_state:
                    self.publish()
                    next_state = now + 0.05
                time.sleep(0.001)
        except Exception as exc:
            self.engine.trip(f"桥接异常：{exc}")
            print(str(exc), file=sys.stderr)
        finally:
            if self.engine.active:
                self.engine.trip("UI EOF 或输出通道失效")
            self.engine.disconnect()
            self.finish_deferred()
            self.publish()
            self.running = False
            self.output_ready.set()
            threads[1].join(0.3)


if __name__ == "__main__":
    sys.stdin.reconfigure(encoding="utf-8")
    sys.stdout.reconfigure(encoding="utf-8")
    sys.stderr.reconfigure(encoding="utf-8")
    Service().run()
