#!/usr/bin/env python3
"""Drive reviewed Linux GUI installer actions through AT-SPI for private qualification."""

from __future__ import annotations

import argparse
from dataclasses import dataclass
import hashlib
import hmac
import json
import os
from pathlib import Path, PurePosixPath
import re
import socket
import stat
import sys
import time
from typing import Any, Callable, Iterable, Protocol


REPOSITORY_ROOT = Path(__file__).resolve().parents[2]
PROTOCOL_VERSION = 1
MAX_MESSAGE_BYTES = 16 * 1024
MAX_TREE_NODES = 4096
MAX_TREE_DEPTH = 32
MAX_TRACE_EVENTS = 64
ACTION_TIMEOUT_SECONDS = 120.0
PROTOCOL_TIMEOUT_SECONDS = 120.0
MAX_EXECUTABLE_BYTES = 256 * 1024 * 1024
SESSION_RE = re.compile(r"^[a-z0-9][a-z0-9_-]{15,63}$")
SHA256_RE = re.compile(r"^[0-9a-f]{64}$")
TOKEN_RE = re.compile(r"^[0-9a-f]{64}$")
WINDOW_ROLES = frozenset({"frame", "window", "dialog"})
BUTTON_ROLES = frozenset({"push button", "button"})
SAFE_ACTIONS = frozenset({"click", "press", "activate"})


class QualificationError(Exception):
    """A fail-closed qualification refusal whose details stay in the private trace."""

    def __init__(self, code: str):
        super().__init__(code)
        self.code = code


class FixedArgumentParser(argparse.ArgumentParser):
    def error(self, _message: str) -> None:
        raise QualificationError("arguments")


@dataclass(frozen=True)
class Milestone:
    name: str
    window_title: str
    action_names: tuple[str, ...]
    picker_title: str | None = None
    picker_field: str | None = None
    requires_operation: bool = False


RELEASE_TITLE = "SMAPI Linux Installer — Release verification"
GAME_TITLE = "SMAPI Linux Installer — Choose game folder"
PLAN_TITLE = "SMAPI Linux Installer — Plan review"
EXECUTION_TITLE = "SMAPI Linux Installer — Run operation"

MILESTONES = {
    "release.local-folder": Milestone(
        "release.local-folder", RELEASE_TITLE, ("Use local release package folder",),
        "Choose the folder containing all six SMAPI release files", "release_folder",
    ),
    "release.download": Milestone(
        "release.download", RELEASE_TITLE, ("Download and verify selected release",),
    ),
    "release.continue": Milestone(
        "release.continue", RELEASE_TITLE, ("Continue to game folder selection",),
    ),
    "game.choose-folder": Milestone(
        "game.choose-folder", GAME_TITLE, ("Choose a game folder",),
        "Choose the Stardew Valley game folder", "game_folder",
    ),
    "game.continue-valid": Milestone(
        "game.continue-valid", GAME_TITLE, ("Open read-only plan review",),
    ),
    "plan.inspect": Milestone(
        "plan.inspect", PLAN_TITLE, ("Inspect selected plan",), requires_operation=True,
    ),
    "plan.confirm": Milestone(
        "plan.confirm", PLAN_TITLE, ("Confirm this exact reviewed plan",),
    ),
    "execution.run": Milestone(
        "execution.run", EXECUTION_TITLE,
        ("Run the exact confirmed operation", "Run the exact confirmed rollback"),
    ),
    "execution.cancel": Milestone(
        "execution.cancel", EXECUTION_TITLE, ("Request safe operation cancellation",),
    ),
    "execution.recover": Milestone(
        "execution.recover", EXECUTION_TITLE, ("Run interrupted recovery",),
    ),
}

BASE_LOCAL = (
    "release.local-folder", "release.continue", "game.choose-folder", "game.continue-valid",
    "plan.inspect", "plan.confirm", "execution.run",
)
BASE_DOWNLOAD = (
    "release.download", "release.continue", "game.choose-folder", "game.continue-valid",
    "plan.inspect", "plan.confirm", "execution.run",
)
ROUTES = {
    "operation-local-run": BASE_LOCAL,
    "operation-local-cancel": BASE_LOCAL + ("execution.cancel",),
    "operation-download-run": BASE_DOWNLOAD,
    "operation-download-cancel": BASE_DOWNLOAD + ("execution.cancel",),
    "recovery": ("execution.recover",),
}
OPERATION_ACCESSIBLE_NAMES = {
    "install": "Install. Inspect adding the verified release when no managed fork installation is present.",
    "update": "Update. Inspect changing a receipt-authenticated fork installation to the verified release.",
    "repair": "Repair. Inspect restoring managed files for the verified release.",
    "uninstall": "Uninstall. Inspect removing receipt-owned SMAPI files and restoring the observed launcher where applicable.",
    "backup": "Backup. Inspect creating a checkpoint of a receipt-authenticated installation.",
}


class Node(Protocol):
    @property
    def name(self) -> str: ...

    @property
    def role(self) -> str: ...

    @property
    def pid(self) -> int | None: ...

    @property
    def visible(self) -> bool: ...

    @property
    def enabled(self) -> bool: ...

    @property
    def action_names(self) -> tuple[str, ...]: ...

    @property
    def selected(self) -> bool: ...

    @property
    def children(self) -> Iterable["Node"]: ...

    def invoke_action(self, index: int) -> bool: ...

    def select(self) -> bool: ...


class AccessibilityBackend(Protocol):
    def roots(self) -> Iterable[Node]: ...

    def choose_folder_with_fixed_keys(self, path: str) -> None: ...


class Transport(Protocol):
    def send(self, message: dict[str, Any]) -> None: ...

    def receive(self) -> dict[str, Any]: ...


class Trace(Protocol):
    def event(self, code: str, sequence: int | None = None, milestone: str | None = None) -> None: ...


def canonical_message(message: dict[str, Any]) -> bytes:
    return json.dumps(message, sort_keys=True, separators=(",", ":"), ensure_ascii=True).encode("ascii")


def sign_message(token: bytes, message: dict[str, Any]) -> str:
    return hmac.new(token, canonical_message(message), hashlib.sha256).hexdigest()


def signed(token: bytes, message: dict[str, Any]) -> dict[str, Any]:
    result = dict(message)
    result["proof"] = sign_message(token, message)
    return result


def verify_signed(token: bytes, message: dict[str, Any], exact_keys: set[str]) -> dict[str, Any]:
    if set(message) != exact_keys | {"proof"} or not isinstance(message.get("proof"), str):
        raise QualificationError("protocol-shape")
    body = {key: value for key, value in message.items() if key != "proof"}
    if not hmac.compare_digest(message["proof"], sign_message(token, body)):
        raise QualificationError("protocol-authentication")
    return body


class AuthenticatedProtocol:
    def __init__(
        self,
        transport: Transport,
        token: bytes,
        session: str,
        nonce_factory: Callable[[], str] | None = None,
    ):
        self.transport = transport
        self.token = token
        self.session = session
        self.nonce = (nonce_factory or (lambda: os.urandom(16).hex()))()
        if re.fullmatch(r"[0-9a-f]{32}", self.nonce) is None:
            raise QualificationError("nonce")
        self.route: str | None = None
        self.gui_pid: int | None = None
        self.gui_sha256: str | None = None

    def admit(self) -> tuple[str, int, str]:
        hello = {
            "type": "hello", "version": PROTOCOL_VERSION, "session": self.session,
            "nonce": self.nonce,
        }
        self.transport.send(signed(self.token, hello))
        body = verify_signed(
            self.token,
            self.transport.receive(),
            {"type", "version", "session", "nonce", "route", "gui_pid", "gui_sha256"},
        )
        if (
            body["type"] != "admit" or type(body["version"]) is not int
            or body["version"] != PROTOCOL_VERSION
            or body["session"] != self.session or body["nonce"] != self.nonce
            or body["route"] not in ROUTES
            or type(body["gui_pid"]) is not int or body["gui_pid"] <= 1
            or not isinstance(body["gui_sha256"], str)
            or SHA256_RE.fullmatch(body["gui_sha256"]) is None
        ):
            raise QualificationError("protocol-admission")
        self.route = body["route"]
        self.gui_pid = body["gui_pid"]
        self.gui_sha256 = body["gui_sha256"]
        return self.route, self.gui_pid, self.gui_sha256

    def advance(self, sequence: int, milestone: Milestone) -> dict[str, Any]:
        message = self.transport.receive()
        base_keys = {"type", "version", "session", "sequence", "milestone"}
        expected_keys = base_keys | ({milestone.picker_field} if milestone.picker_field else set())
        if milestone.requires_operation:
            expected_keys.add("operation")
        body = verify_signed(self.token, message, expected_keys)
        if (
            body["type"] != "advance" or type(body["version"]) is not int
            or body["version"] != PROTOCOL_VERSION or body["session"] != self.session
            or type(body["sequence"]) is not int or body["sequence"] != sequence
            or body["milestone"] != milestone.name
        ):
            raise QualificationError("protocol-order")
        if milestone.picker_field:
            validate_picker_path(body[milestone.picker_field])
        if milestone.requires_operation and body["operation"] not in OPERATION_ACCESSIBLE_NAMES:
            raise QualificationError("operation")
        return body

    def reached(self, sequence: int, milestone: Milestone) -> None:
        self.transport.send(signed(self.token, {
            "type": "reached", "version": PROTOCOL_VERSION, "session": self.session,
            "sequence": sequence, "milestone": milestone.name,
        }))

    def complete(self, count: int) -> None:
        body = verify_signed(
            self.token,
            self.transport.receive(),
            {"type", "version", "session", "sequence"},
        )
        if (
            body["type"] != "complete" or type(body["version"]) is not int
            or body["version"] != PROTOCOL_VERSION or body["session"] != self.session
            or type(body["sequence"]) is not int or body["sequence"] != count
        ):
            raise QualificationError("protocol-order")
        self.transport.send(signed(self.token, {
            "type": "completed", "version": PROTOCOL_VERSION,
            "session": self.session, "sequence": count,
        }))


def validate_picker_path(value: Any) -> str:
    if not isinstance(value, str) or not value.startswith("/") or len(value.encode("utf-8")) > 4096:
        raise QualificationError("picker-path")
    if any(ord(character) < 32 or ord(character) == 127 for character in value):
        raise QualificationError("picker-path")
    parts = PurePosixPath(value).parts
    if ".." in parts or value != str(PurePosixPath(value)):
        raise QualificationError("picker-path")
    try:
        descriptor = os.open(value, os.O_RDONLY | os.O_CLOEXEC | os.O_NOFOLLOW | os.O_DIRECTORY)
    except OSError:
        raise QualificationError("picker-path") from None
    try:
        status = os.fstat(descriptor)
        resolved = os.readlink(f"/proc/self/fd/{descriptor}")
        if not stat.S_ISDIR(status.st_mode) or status.st_uid != os.geteuid() or resolved != value:
            raise QualificationError("picker-path")
    finally:
        os.close(descriptor)
    return value


def walk_nodes(roots: Iterable[Node]) -> Iterable[Node]:
    stack = [(node, 0) for node in reversed(tuple(roots))]
    count = 0
    while stack:
        node, depth = stack.pop()
        count += 1
        if count > MAX_TREE_NODES or depth > MAX_TREE_DEPTH:
            raise QualificationError("accessibility-tree-bound")
        yield node
        try:
            children = tuple(node.children)
        except Exception:
            raise QualificationError("accessibility-tree") from None
        stack.extend((child, depth + 1) for child in reversed(children))


def exact_window(roots: Iterable[Node], title: str, gui_pid: int | None) -> Node:
    named = [
        node for node in walk_nodes(roots)
        if node.role in WINDOW_ROLES and node.visible and node.name == title
    ]
    if len(named) != 1:
        raise QualificationError("window-ambiguous" if named else "window-missing")
    window = named[0]
    if gui_pid is not None and window.pid != gui_pid:
        raise QualificationError("window-pid")
    return window


def exact_action(window: Node, names: tuple[str, ...]) -> tuple[Node, int]:
    candidates = [node for node in walk_nodes((window,)) if node.name in names]
    if len(candidates) != 1:
        raise QualificationError("action-ambiguous" if candidates else "action-missing")
    node = candidates[0]
    if node.role not in BUTTON_ROLES:
        raise QualificationError("action-role")
    if not node.visible or not node.enabled:
        raise QualificationError("action-disabled")
    safe = [index for index, name in enumerate(node.action_names) if name.casefold() in SAFE_ACTIONS]
    if len(safe) != 1:
        raise QualificationError("action-interface")
    return node, safe[0]


def select_exact_operation(window: Node, operation: str) -> None:
    exact_name = OPERATION_ACCESSIBLE_NAMES[operation]
    candidates = [node for node in walk_nodes((window,)) if node.name == exact_name]
    if len(candidates) != 1:
        raise QualificationError("operation-ambiguous" if candidates else "operation-missing")
    node = candidates[0]
    if node.role != "list item" or not node.visible or not node.enabled:
        raise QualificationError("operation-disabled")
    try:
        accepted = node.select()
    except Exception:
        raise QualificationError("operation-select") from None
    if accepted is not True:
        raise QualificationError("operation-select")


def wait_for_selected_operation(
    backend: AccessibilityBackend,
    window_title: str,
    gui_pid: int,
    operation: str,
    deadline: float,
    clock: Callable[[], float],
    sleeper: Callable[[float], None],
) -> Node:
    exact_name = OPERATION_ACCESSIBLE_NAMES[operation]
    while clock() < deadline:
        window = exact_window(backend.roots(), window_title, gui_pid)
        candidates = [node for node in walk_nodes((window,)) if node.name == exact_name]
        if len(candidates) != 1:
            raise QualificationError("operation-ambiguous" if candidates else "operation-missing")
        if candidates[0].selected:
            return window
        sleeper(0.05)
    raise QualificationError("operation-timeout")


def wait_for_window(
    backend: AccessibilityBackend,
    title: str,
    gui_pid: int | None,
    deadline: float,
    clock: Callable[[], float],
    sleeper: Callable[[float], None],
) -> Node:
    last_missing = False
    while clock() < deadline:
        try:
            return exact_window(backend.roots(), title, gui_pid)
        except QualificationError as exc:
            if exc.code != "window-missing":
                raise
            last_missing = True
            sleeper(0.05)
    raise QualificationError("window-timeout" if last_missing else "window-missing")


class ProcessBinder:
    def bind(self, pid: int, expected_sha256: str) -> tuple[int, int, int, str]:
        if os.geteuid() == 0:
            raise QualificationError("operator-root")
        status_path = Path("/proc") / str(pid) / "status"
        exe_path = Path("/proc") / str(pid) / "exe"
        try:
            status_text = status_path.read_text(encoding="ascii", errors="strict")
            process_stat = (Path("/proc") / str(pid) / "stat").read_text(encoding="ascii", errors="strict")
            stat_before = exe_path.stat()
            if stat_before.st_size <= 0 or stat_before.st_size > MAX_EXECUTABLE_BYTES:
                raise QualificationError("process-executable")
            digest = hashlib.sha256()
            with exe_path.open("rb") as executable:
                while chunk := executable.read(1024 * 1024):
                    digest.update(chunk)
            stat_after = exe_path.stat()
        except QualificationError:
            raise
        except Exception:
            raise QualificationError("process-identity") from None
        uid_line = next((line for line in status_text.splitlines() if line.startswith("Uid:")), None)
        if uid_line is None:
            raise QualificationError("process-identity")
        try:
            real_uid, effective_uid = (int(value) for value in uid_line.split()[1:3])
            close_parenthesis = process_stat.rindex(")")
            stat_fields_from_state = process_stat[close_parenthesis + 2:].split()
            start_time = int(stat_fields_from_state[19])
        except (ValueError, IndexError):
            raise QualificationError("process-identity") from None
        if start_time <= 0:
            raise QualificationError("process-identity")
        if real_uid != os.geteuid() or effective_uid != os.geteuid() or effective_uid == 0:
            raise QualificationError("process-uid")
        identity_before = (stat_before.st_dev, stat_before.st_ino, stat_before.st_size, stat_before.st_ctime_ns)
        identity_after = (stat_after.st_dev, stat_after.st_ino, stat_after.st_size, stat_after.st_ctime_ns)
        actual = digest.hexdigest()
        if identity_before != identity_after or actual != expected_sha256:
            raise QualificationError("process-executable")
        return stat_before.st_dev, stat_before.st_ino, start_time, actual


class HardStateOperator:
    def __init__(
        self,
        backend: AccessibilityBackend,
        protocol: AuthenticatedProtocol,
        binder: ProcessBinder,
        trace: Trace,
        clock: Callable[[], float] = time.monotonic,
        sleeper: Callable[[float], None] = time.sleep,
    ):
        self.backend = backend
        self.protocol = protocol
        self.binder = binder
        self.trace = trace
        self.clock = clock
        self.sleeper = sleeper

    def run(self) -> None:
        route, gui_pid, gui_sha256 = self.protocol.admit()
        bound_identity = self.binder.bind(gui_pid, gui_sha256)
        self.trace.event("admitted")
        milestones = ROUTES[route]
        for sequence, name in enumerate(milestones):
            milestone = MILESTONES[name]
            command = self.protocol.advance(sequence, milestone)
            deadline = self.clock() + ACTION_TIMEOUT_SECONDS
            window = wait_for_window(
                self.backend, milestone.window_title, gui_pid, deadline, self.clock, self.sleeper,
            )
            if milestone.requires_operation:
                select_exact_operation(window, command["operation"])
                window = wait_for_selected_operation(
                    self.backend, milestone.window_title, gui_pid, command["operation"],
                    deadline, self.clock, self.sleeper,
                )
            node, action_index = exact_action(window, milestone.action_names)
            try:
                invoked = node.invoke_action(action_index)
            except Exception:
                raise QualificationError("action-invoke") from None
            if invoked is not True:
                raise QualificationError("action-invoke")
            if milestone.picker_title and milestone.picker_field:
                wait_for_window(
                    self.backend, milestone.picker_title, None, deadline, self.clock, self.sleeper,
                )
                try:
                    self.backend.choose_folder_with_fixed_keys(command[milestone.picker_field])
                except QualificationError:
                    raise
                except Exception:
                    raise QualificationError("picker-keyboard") from None
            if self.binder.bind(gui_pid, gui_sha256) != bound_identity:
                raise QualificationError("process-rebound")
            self.trace.event("milestone-reached", sequence, milestone.name)
            self.protocol.reached(sequence, milestone)
        self.protocol.complete(len(milestones))
        if self.binder.bind(gui_pid, gui_sha256) != bound_identity:
            raise QualificationError("process-rebound")
        self.trace.event("completed")


class JsonSocketTransport:
    def __init__(self, connection: socket.socket):
        self.connection = connection
        self.buffer = bytearray()

    def send(self, message: dict[str, Any]) -> None:
        payload = canonical_message(message) + b"\n"
        if len(payload) > MAX_MESSAGE_BYTES:
            raise QualificationError("protocol-bound")
        try:
            self.connection.sendall(payload)
        except OSError:
            raise QualificationError("protocol-write") from None

    def receive(self) -> dict[str, Any]:
        while b"\n" not in self.buffer:
            if len(self.buffer) >= MAX_MESSAGE_BYTES:
                raise QualificationError("protocol-bound")
            try:
                chunk = self.connection.recv(min(4096, MAX_MESSAGE_BYTES - len(self.buffer)))
            except (OSError, socket.timeout):
                raise QualificationError("protocol-timeout") from None
            if not chunk:
                raise QualificationError("protocol-eof")
            self.buffer.extend(chunk)
        line, separator, remainder = self.buffer.partition(b"\n")
        self.buffer = bytearray(remainder)
        if not separator or not line or len(line) + 1 > MAX_MESSAGE_BYTES:
            raise QualificationError("protocol-framing")
        try:
            message = json.loads(line.decode("utf-8", errors="strict"))
        except (UnicodeError, json.JSONDecodeError):
            raise QualificationError("protocol-json") from None
        if not isinstance(message, dict):
            raise QualificationError("protocol-shape")
        return message


class PrivateTrace:
    def __init__(self, descriptor: int):
        self.descriptor = descriptor
        self.count = 0

    @classmethod
    def create(cls, path: Path) -> "PrivateTrace":
        validate_private_new_path(path, "trace")
        try:
            descriptor = os.open(path, os.O_WRONLY | os.O_CREAT | os.O_EXCL | os.O_CLOEXEC, 0o600)
        except OSError:
            raise QualificationError("trace") from None
        return cls(descriptor)

    def event(self, code: str, sequence: int | None = None, milestone: str | None = None) -> None:
        if self.count >= MAX_TRACE_EVENTS:
            raise QualificationError("trace-bound")
        message: dict[str, Any] = {"event": code}
        if sequence is not None:
            message["sequence"] = sequence
        if milestone is not None:
            message["milestone"] = milestone
        payload = canonical_message(message) + b"\n"
        try:
            os.write(self.descriptor, payload)
            os.fsync(self.descriptor)
        except OSError:
            raise QualificationError("trace") from None
        self.count += 1

    def close(self) -> None:
        try:
            os.close(self.descriptor)
        except OSError:
            pass


class AtspiNode:
    def __init__(self, accessible: Any, pyatspi: Any):
        self.accessible = accessible
        self.pyatspi = pyatspi

    @property
    def name(self) -> str:
        try:
            return self.accessible.name or ""
        except Exception:
            return ""

    @property
    def role(self) -> str:
        try:
            return str(self.accessible.getRoleName()).casefold()
        except Exception:
            return ""

    @property
    def pid(self) -> int | None:
        current = self.accessible
        for _ in range(MAX_TREE_DEPTH + 1):
            try:
                getter = getattr(current, "get_process_id", None)
                if getter is not None:
                    value = int(getter())
                    if value > 0:
                        return value
                current = current.parent
                if current is None:
                    break
            except Exception:
                break
        return None

    def _state(self, *names: str) -> bool:
        try:
            states = self.accessible.getState()
            return all(states.contains(getattr(self.pyatspi, name)) for name in names)
        except Exception:
            return False

    @property
    def visible(self) -> bool:
        return self._state("STATE_VISIBLE", "STATE_SHOWING")

    @property
    def enabled(self) -> bool:
        return self._state("STATE_ENABLED", "STATE_SENSITIVE")

    @property
    def action_names(self) -> tuple[str, ...]:
        try:
            action = self.accessible.queryAction()
            return tuple(str(action.getName(index)) for index in range(action.nActions))
        except Exception:
            return ()

    @property
    def selected(self) -> bool:
        return self._state("STATE_SELECTED")

    @property
    def children(self) -> Iterable[Node]:
        try:
            return tuple(AtspiNode(self.accessible[index], self.pyatspi) for index in range(self.accessible.childCount))
        except Exception:
            raise QualificationError("accessibility-tree") from None

    def invoke_action(self, index: int) -> bool:
        try:
            return bool(self.accessible.queryAction().doAction(index))
        except Exception:
            raise QualificationError("action-invoke") from None

    def select(self) -> bool:
        try:
            parent = self.accessible.parent
            index = int(self.accessible.getIndexInParent())
            accepted = parent.querySelection().selectChild(index)
            return accepted is not False
        except Exception:
            raise QualificationError("operation-select") from None


class AtspiBackend:
    def __init__(self):
        self.pyatspi: Any | None = None

    def _module(self) -> Any:
        if self.pyatspi is not None:
            return self.pyatspi
        try:
            import pyatspi  # type: ignore
        except Exception:
            raise QualificationError("atspi-unavailable") from None
        self.pyatspi = pyatspi
        return pyatspi

    def roots(self) -> Iterable[Node]:
        pyatspi = self._module()
        try:
            desktop = pyatspi.Registry.getDesktop(0)
            return tuple(AtspiNode(desktop[index], pyatspi) for index in range(desktop.childCount))
        except Exception:
            raise QualificationError("accessibility-tree") from None

    def choose_folder_with_fixed_keys(self, path: str) -> None:
        validate_picker_path(path)
        pyatspi = self._module()
        registry = pyatspi.Registry
        try:
            registry.generateKeyboardEvent(65507, None, pyatspi.KEY_PRESS)  # Control_L down.
            registry.generateKeyboardEvent(ord("l"), None, pyatspi.KEY_PRESSRELEASE)
            registry.generateKeyboardEvent(65507, None, pyatspi.KEY_RELEASE)
            registry.generateKeyboardEvent(0, path, pyatspi.KEY_STRING)
            registry.generateKeyboardEvent(65293, None, pyatspi.KEY_PRESSRELEASE)  # Return.
        except Exception:
            raise QualificationError("picker-keyboard") from None


def validate_private_parent(path: Path) -> None:
    try:
        parent = path.parent.resolve(strict=True)
        status = parent.stat()
    except OSError:
        raise QualificationError("private-path") from None
    if status.st_uid != os.geteuid() or stat.S_IMODE(status.st_mode) != 0o700:
        raise QualificationError("private-path")
    try:
        parent.relative_to(REPOSITORY_ROOT)
    except ValueError:
        return
    raise QualificationError("private-path")


def validate_private_new_path(path: Path, _description: str) -> None:
    if not path.is_absolute() or path.exists() or path.is_symlink():
        raise QualificationError("private-path")
    validate_private_parent(path)


def read_private_token(path: Path) -> bytes:
    if not path.is_absolute():
        raise QualificationError("token")
    validate_private_parent(path)
    try:
        descriptor = os.open(path, os.O_RDONLY | os.O_CLOEXEC | os.O_NOFOLLOW)
    except OSError:
        raise QualificationError("token") from None
    try:
        status = os.fstat(descriptor)
        if (
            not stat.S_ISREG(status.st_mode) or status.st_nlink != 1
            or status.st_uid != os.geteuid() or stat.S_IMODE(status.st_mode) != 0o600
            or status.st_size not in (64, 65)
        ):
            raise QualificationError("token")
        data = os.read(descriptor, 66)
        if len(data) != status.st_size or os.read(descriptor, 1):
            raise QualificationError("token")
        final = os.fstat(descriptor)
        fields = ("st_dev", "st_ino", "st_mode", "st_nlink", "st_uid", "st_size", "st_mtime_ns", "st_ctime_ns")
        if any(getattr(status, field) != getattr(final, field) for field in fields):
            raise QualificationError("token")
    finally:
        os.close(descriptor)
    try:
        text = data.decode("ascii", errors="strict").rstrip("\n")
    except UnicodeError:
        raise QualificationError("token") from None
    if TOKEN_RE.fullmatch(text) is None:
        raise QualificationError("token")
    return bytes.fromhex(text)


def connect_private_socket(path: Path) -> socket.socket:
    if not path.is_absolute():
        raise QualificationError("socket")
    validate_private_parent(path)
    try:
        status = path.lstat()
    except OSError:
        raise QualificationError("socket") from None
    if not stat.S_ISSOCK(status.st_mode) or status.st_uid != os.geteuid() or stat.S_IMODE(status.st_mode) != 0o600:
        raise QualificationError("socket")
    connection = socket.socket(socket.AF_UNIX, socket.SOCK_STREAM)
    connection.settimeout(PROTOCOL_TIMEOUT_SECONDS)
    try:
        connection.connect(str(path))
        if hasattr(socket, "SO_PEERCRED"):
            credentials = connection.getsockopt(socket.SOL_SOCKET, socket.SO_PEERCRED, 12)
            peer_pid = int.from_bytes(credentials[0:4], sys.byteorder, signed=True)
            peer_uid = int.from_bytes(credentials[4:8], sys.byteorder, signed=True)
            if peer_pid <= 1 or peer_uid != os.geteuid():
                raise QualificationError("socket-peer")
        return connection
    except QualificationError:
        connection.close()
        raise
    except Exception:
        connection.close()
        raise QualificationError("socket-connect") from None


def parse_arguments(arguments: list[str]) -> argparse.Namespace:
    parser = FixedArgumentParser(add_help=False)
    parser.add_argument("--supervisor-socket", required=True)
    parser.add_argument("--token-file", required=True)
    parser.add_argument("--session-id", required=True)
    parser.add_argument("--trace-file", required=True)
    values = parser.parse_args(arguments)
    if SESSION_RE.fullmatch(values.session_id) is None:
        raise QualificationError("session")
    for field in ("supervisor_socket", "token_file", "trace_file"):
        value = Path(getattr(values, field))
        if not value.is_absolute():
            raise QualificationError("arguments")
        setattr(values, field, value)
    return values


def main(arguments: list[str] | None = None) -> int:
    trace: PrivateTrace | None = None
    connection: socket.socket | None = None
    try:
        values = parse_arguments(sys.argv[1:] if arguments is None else arguments)
        token = read_private_token(values.token_file)
        trace = PrivateTrace.create(values.trace_file)
        trace.event("starting")
        connection = connect_private_socket(values.supervisor_socket)
        protocol = AuthenticatedProtocol(JsonSocketTransport(connection), token, values.session_id)
        HardStateOperator(AtspiBackend(), protocol, ProcessBinder(), trace).run()
        return 0
    except QualificationError as exc:
        if trace is not None:
            try:
                trace.event("failed-" + exc.code)
            except Exception:
                pass
        print("Linux GUI hard-state AT-SPI operator failed", file=sys.stderr)
        return 2
    except Exception:
        if trace is not None:
            try:
                trace.event("failed-unexpected")
            except Exception:
                pass
        print("Linux GUI hard-state AT-SPI operator failed", file=sys.stderr)
        return 1
    finally:
        if connection is not None:
            connection.close()
        if trace is not None:
            trace.close()


if __name__ == "__main__":
    raise SystemExit(main())
