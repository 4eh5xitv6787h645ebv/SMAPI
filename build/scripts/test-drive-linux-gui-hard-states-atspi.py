#!/usr/bin/env python3
"""Synthetic tests for the external Linux GUI hard-state AT-SPI operator."""

from __future__ import annotations

import importlib.util
import json
import os
from pathlib import Path
import stat
import subprocess
import sys
import tempfile
from typing import Any, Callable, Iterable


ROOT = Path(__file__).resolve().parents[2]
TOOL = ROOT / "build/scripts/drive-linux-gui-hard-states-atspi.py"


def load_tool():
    spec = importlib.util.spec_from_file_location("hard_state_atspi", TOOL)
    if spec is None or spec.loader is None:
        raise AssertionError("operator could not be loaded")
    module = importlib.util.module_from_spec(spec)
    sys.modules[spec.name] = module
    spec.loader.exec_module(module)
    return module


tool = load_tool()
TOKEN = bytes.fromhex("12" * 32)
SESSION = "qualification_session_0001"
GUI_PID = 4242
GUI_HASH = "a" * 64
NONCE = "b" * 32


class FakeNode:
    def __init__(
        self,
        name: str,
        role: str,
        *,
        pid: int | None = None,
        visible: bool = True,
        enabled: bool = True,
        actions: tuple[str, ...] = (),
        children: Iterable["FakeNode"] = (),
        callback: Callable[[], bool] | None = None,
        select_callback: Callable[[], bool] | None = None,
        selected: bool = False,
    ):
        self.name = name
        self.role = role
        self.pid = pid
        self.visible = visible
        self.enabled = enabled
        self.action_names = actions
        self.children = tuple(children)
        self.callback = callback
        self.select_callback = select_callback
        self.invocations = 0
        self.selected = selected

    def invoke_action(self, index: int) -> bool:
        if index < 0 or index >= len(self.action_names):
            return False
        self.invocations += 1
        return True if self.callback is None else self.callback()

    def select(self) -> bool:
        accepted = True if self.select_callback is None else self.select_callback()
        if accepted:
            self.selected = True
        return accepted


class FakeBackend:
    def __init__(
        self,
        route: str,
        *,
        window_pid: int = GUI_PID,
        disabled_at: int | None = None,
        duplicate_action_at: int | None = None,
        wrong_action_interface_at: int | None = None,
        missing_action_at: int | None = None,
        duplicate_picker: bool = False,
        duplicate_window_at: int | None = None,
        wrong_window_at: int | None = None,
    ):
        self.milestones = [tool.MILESTONES[name] for name in tool.ROUTES[route]]
        self.index = 0
        self.window_pid = window_pid
        self.disabled_at = disabled_at
        self.duplicate_action_at = duplicate_action_at
        self.wrong_action_interface_at = wrong_action_interface_at
        self.missing_action_at = missing_action_at
        self.duplicate_picker = duplicate_picker
        self.duplicate_window_at = duplicate_window_at
        self.wrong_window_at = wrong_window_at
        self.picker_open = False
        self.invoked: list[str] = []
        self.picker_paths: list[str] = []
        self.operation_selected = False

    def roots(self) -> Iterable[FakeNode]:
        if self.index >= len(self.milestones):
            return ()
        milestone = self.milestones[self.index]
        if self.picker_open:
            picker = FakeNode(milestone.picker_title or "wrong picker", "dialog", pid=7001)
            return (picker, FakeNode(picker.name, "dialog", pid=7002)) if self.duplicate_picker else (picker,)
        title = "Unexpected isolated window" if self.wrong_window_at == self.index else milestone.window_title
        nodes: list[FakeNode] = []
        if self.missing_action_at != self.index:
            action_name = milestone.action_names[0]

            def invoke() -> bool:
                self.invoked.append(milestone.name)
                if milestone.picker_title:
                    self.picker_open = True
                else:
                    self.index += 1
                return True

            nodes.append(FakeNode(
                action_name,
                "push button",
                enabled=self.disabled_at != self.index,
                actions=("toggle",) if self.wrong_action_interface_at == self.index else ("click",),
                callback=invoke,
            ))
            if self.duplicate_action_at == self.index:
                nodes.append(FakeNode(action_name, "push button", actions=("press",)))
        if milestone.requires_operation:
            def select_operation() -> bool:
                self.operation_selected = True
                return True

            nodes.append(FakeNode(
                tool.OPERATION_ACCESSIBLE_NAMES["install"], "list item",
                select_callback=select_operation,
                selected=self.operation_selected,
            ))
        window = FakeNode(title, "frame", pid=self.window_pid, children=nodes)
        if self.duplicate_window_at == self.index:
            return window, FakeNode(title, "frame", pid=self.window_pid, children=nodes)
        return (window,)

    def choose_folder_with_fixed_keys(self, path: str) -> None:
        if not self.picker_open:
            raise AssertionError("fixed picker keys used without a verified picker")
        self.picker_paths.append(path)
        self.picker_open = False
        self.index += 1


class MemoryTransport:
    def __init__(self, incoming: Iterable[dict[str, Any]]):
        self.incoming = list(incoming)
        self.outgoing: list[dict[str, Any]] = []

    def send(self, message: dict[str, Any]) -> None:
        self.outgoing.append(message)

    def receive(self) -> dict[str, Any]:
        if not self.incoming:
            raise tool.QualificationError("test-eof")
        return self.incoming.pop(0)


class FakeBinder:
    def __init__(self, *, rebound_after: int | None = None):
        self.calls = 0
        self.rebound_after = rebound_after

    def bind(self, pid: int, expected_sha256: str) -> tuple[int, int, int, str]:
        if pid != GUI_PID or expected_sha256 != GUI_HASH:
            raise tool.QualificationError("process-identity")
        self.calls += 1
        inode = 99 if self.rebound_after is not None and self.calls > self.rebound_after else 42
        return 7, inode, 12345, expected_sha256


class FakeTrace:
    def __init__(self):
        self.events: list[tuple[str, int | None, str | None]] = []

    def event(self, code: str, sequence: int | None = None, milestone: str | None = None) -> None:
        self.events.append((code, sequence, milestone))


class FastClock:
    def __init__(self):
        self.value = 0.0

    def __call__(self) -> float:
        return self.value

    def sleep(self, seconds: float) -> None:
        self.value += max(seconds, tool.ACTION_TIMEOUT_SECONDS + 1)


def admission(route: str, **changes: Any) -> dict[str, Any]:
    message: dict[str, Any] = {
        "type": "admit",
        "version": tool.PROTOCOL_VERSION,
        "session": SESSION,
        "nonce": NONCE,
        "route": route,
        "gui_pid": GUI_PID,
        "gui_sha256": GUI_HASH,
    }
    message.update(changes)
    return tool.signed(TOKEN, message)


def advance(sequence: int, milestone: Any, picker_path: str | None = None, **changes: Any) -> dict[str, Any]:
    message: dict[str, Any] = {
        "type": "advance",
        "version": tool.PROTOCOL_VERSION,
        "session": SESSION,
        "sequence": sequence,
        "milestone": milestone.name,
    }
    if milestone.picker_field:
        message[milestone.picker_field] = picker_path
    if milestone.requires_operation:
        message["operation"] = "install"
    message.update(changes)
    return tool.signed(TOKEN, message)


def completion(count: int) -> dict[str, Any]:
    return tool.signed(TOKEN, {
        "type": "complete", "version": tool.PROTOCOL_VERSION,
        "session": SESSION, "sequence": count,
    })


def make_operator(
    route: str,
    backend: FakeBackend,
    folders: tuple[str, ...],
    *,
    mutate_messages: Callable[[list[dict[str, Any]]], None] | None = None,
    binder: FakeBinder | None = None,
    clock: FastClock | None = None,
) -> tuple[Any, MemoryTransport, FakeTrace]:
    milestones = [tool.MILESTONES[name] for name in tool.ROUTES[route]]
    folder_index = 0
    incoming = [admission(route)]
    for sequence, milestone in enumerate(milestones):
        picker_path = None
        if milestone.picker_field:
            picker_path = folders[folder_index]
            folder_index += 1
        incoming.append(advance(sequence, milestone, picker_path))
    incoming.append(completion(len(milestones)))
    if mutate_messages:
        mutate_messages(incoming)
    transport = MemoryTransport(incoming)
    protocol = tool.AuthenticatedProtocol(transport, TOKEN, SESSION, nonce_factory=lambda: NONCE)
    trace = FakeTrace()
    fast = clock or FastClock()
    operator = tool.HardStateOperator(
        backend, protocol, binder or FakeBinder(), trace,
        clock=fast, sleeper=fast.sleep,
    )
    return operator, transport, trace


def expect_error(name: str, expected: str, action: Callable[[], None]) -> None:
    try:
        action()
    except tool.QualificationError as exc:
        if exc.code != expected:
            raise AssertionError(f"{name}: expected {expected!r}, got {exc.code!r}") from exc
    else:
        raise AssertionError(f"{name}: expected a qualification refusal")


def test_success_routes(root: Path) -> int:
    release = root / "release"
    game = root / "game"
    release.mkdir(exist_ok=True)
    game.mkdir(exist_ok=True)
    count = 0
    for route, folders in (
        ("operation-local-cancel", (str(release), str(game))),
        ("operation-download-run", (str(game),)),
        ("recovery", ()),
    ):
        backend = FakeBackend(route)
        operator, transport, trace = make_operator(route, backend, folders)
        operator.run()
        expected = list(tool.ROUTES[route])
        if backend.invoked != expected:
            raise AssertionError(f"{route}: wrong accessible action sequence {backend.invoked!r}")
        types = [message["type"] for message in transport.outgoing]
        if types != ["hello"] + ["reached"] * len(expected) + ["completed"]:
            raise AssertionError(f"{route}: wrong supervisor response sequence {types!r}")
        if trace.events[-1][0] != "completed" or transport.incoming:
            raise AssertionError(f"{route}: did not settle its exact route")
        serialized = json.dumps(transport.outgoing, sort_keys=True)
        if str(root) in serialized or TOKEN.hex() in serialized:
            raise AssertionError(f"{route}: outbound protocol leaked picker paths or token")
        count += 1
    return count


def main() -> int:
    cases = 0
    with tempfile.TemporaryDirectory(prefix="smapi-hard-state-atspi-test.") as temporary:
        root = Path(temporary)
        release = root / "release"
        game = root / "game"
        release.mkdir()
        game.mkdir()
        folders = (str(release), str(game))
        cases += test_success_routes(root)

        def run_backend(**options: Any) -> None:
            backend = FakeBackend("operation-local-run", **options)
            operator, _, _ = make_operator("operation-local-run", backend, folders)
            operator.run()

        expect_error("ambiguous action", "action-ambiguous", lambda: run_backend(duplicate_action_at=0))
        expect_error("disabled action", "action-disabled", lambda: run_backend(disabled_at=0))
        expect_error("wrong GUI PID", "window-pid", lambda: run_backend(window_pid=GUI_PID + 1))
        expect_error("ambiguous app window", "window-ambiguous", lambda: run_backend(duplicate_window_at=0))
        expect_error("wrong window", "window-timeout", lambda: run_backend(wrong_window_at=0))
        expect_error("missing exact action", "action-missing", lambda: run_backend(missing_action_at=0))
        expect_error("wrong action interface", "action-interface", lambda: run_backend(wrong_action_interface_at=0))
        cases += 7

        picker_backend = FakeBackend("operation-local-run", duplicate_picker=True)
        picker_operator, _, _ = make_operator("operation-local-run", picker_backend, folders)
        expect_error("ambiguous native picker", "window-ambiguous", picker_operator.run)
        if picker_backend.picker_paths:
            raise AssertionError("ambiguous native picker received keyboard input")
        cases += 1

        order_backend = FakeBackend("operation-local-run")

        def wrong_order(messages: list[dict[str, Any]]) -> None:
            milestone = tool.MILESTONES["release.local-folder"]
            messages[1] = advance(1, milestone, str(release))

        order_operator, _, _ = make_operator(
            "operation-local-run", order_backend, folders, mutate_messages=wrong_order,
        )
        expect_error("wrong milestone order", "protocol-order", order_operator.run)
        if order_backend.invoked:
            raise AssertionError("out-of-order protocol invoked an accessible action")
        cases += 1

        auth_backend = FakeBackend("operation-local-run")

        def wrong_auth(messages: list[dict[str, Any]]) -> None:
            messages[0]["proof"] = "0" * 64

        auth_operator, _, _ = make_operator(
            "operation-local-run", auth_backend, folders, mutate_messages=wrong_auth,
        )
        expect_error("wrong supervisor proof", "protocol-authentication", auth_operator.run)
        cases += 1

        timeout_backend = FakeBackend("operation-local-run", wrong_window_at=0)
        clock = FastClock()
        timeout_operator, _, _ = make_operator(
            "operation-local-run", timeout_backend, folders, clock=clock,
        )
        expect_error("window timeout", "window-timeout", timeout_operator.run)
        cases += 1

        rebound_backend = FakeBackend("operation-local-run")
        rebound_operator, _, _ = make_operator(
            "operation-local-run", rebound_backend, folders,
            binder=FakeBinder(rebound_after=1),
        )
        expect_error("process identity changed", "process-rebound", rebound_operator.run)
        cases += 1

        unsafe_backend = FakeBackend("operation-local-run")

        def unsafe_picker(messages: list[dict[str, Any]]) -> None:
            messages[1] = advance(0, tool.MILESTONES["release.local-folder"], "/does/not/exist/private")

        unsafe_operator, _, _ = make_operator(
            "operation-local-run", unsafe_backend, folders, mutate_messages=unsafe_picker,
        )
        expect_error("unsafe picker path", "picker-path", unsafe_operator.run)
        if unsafe_backend.invoked:
            raise AssertionError("invalid picker path invoked an accessible action")
        cases += 1

    secret = "/private/operator-argument-that-must-not-echo"
    for arguments in (("--supervisor-socket", secret), ("--help",)):
        result = subprocess.run(
            [sys.executable, str(TOOL), *arguments],
            check=False,
            capture_output=True,
            text=True,
        )
        if (
            result.returncode != 2 or result.stdout != ""
            or result.stderr != "Linux GUI hard-state AT-SPI operator failed\n"
            or secret in result.stdout + result.stderr
        ):
            raise AssertionError("fixed command-line refusal leaked private argument text")
        cases += 1

    with tempfile.TemporaryDirectory(prefix="smapi-hard-state-trace.") as temporary:
        private_root = Path(temporary)
        os.chmod(private_root, 0o700)
        trace_path = private_root / "trace.jsonl"
        trace = tool.PrivateTrace.create(trace_path)
        try:
            trace.event("starting")
            trace.event("milestone-reached", 0, "release.download")
        finally:
            trace.close()
        content = trace_path.read_text(encoding="ascii")
        if stat.S_IMODE(trace_path.stat().st_mode) != 0o600 or "/" in content or TOKEN.hex() in content:
            raise AssertionError("private trace mode or path/token exclusion failed")
    cases += 1

    deep = FakeNode("leaf", "label")
    for index in range(tool.MAX_TREE_DEPTH + 2):
        deep = FakeNode(f"node-{index}", "panel", children=(deep,))
    expect_error("tree depth bound", "accessibility-tree-bound", lambda: tuple(tool.walk_nodes((deep,))))
    cases += 1

    print(f"Linux GUI hard-state AT-SPI operator tests passed ({cases} cases).")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
