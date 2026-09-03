#!/usr/bin/env python3
"""Fixture-free tests for the external Linux GUI hard-state durability barrier."""

from __future__ import annotations

import os
from pathlib import Path
import queue
import select
import shutil
import socket
import stat
import subprocess
import tempfile
import threading
import time


SCRIPT_ROOT = Path(__file__).resolve().parent
SOURCE = SCRIPT_ROOT / "linux-gui-hard-state-barrier.c"
MARKER = "SMAPI Linux GUI hard-state disposable root v1\n"
ENV_ROOT = "SMAPI_LINUX_GUI_HARD_STATE_ROOT"
ENV_PID = "SMAPI_LINUX_GUI_HARD_STATE_PID_FILE"
ENV_SOCKET = "SMAPI_LINUX_GUI_HARD_STATE_SOCKET"
ENV_TIMEOUT = "SMAPI_LINUX_GUI_HARD_STATE_TIMEOUT_MS"
TRANSACTION = "0123456789abcdef0123456789abcdef"
DIGEST_A = "a" * 64
DIGEST_B = "b" * 64
DIGEST_C = "c" * 64
APPLIED = (
    '{"schemaVersion":1,"sequence":4,"kind":"Applied","operationIndex":0,'
    f'"planSha256":"{DIGEST_A}","previousEventSha256":"{DIGEST_B}",'
    f'"eventSha256":"{DIGEST_C}"}}\n'
)
INTENT = APPLIED.replace('"kind":"Applied"', '"kind":"Intent"')

WORKER_SOURCE = r"""
#define _GNU_SOURCE
#include <fcntl.h>
#include <string.h>
#include <unistd.h>

int main(int argc, char **argv)
{
    if (argc != 3)
        return 20;
    int descriptor = open(argv[2], O_RDWR | O_CLOEXEC | O_NOFOLLOW);
    if (descriptor < 0)
        return 21;
    char gate;
    if (read(STDIN_FILENO, &gate, 1) != 1)
        return 22;
    if (write(STDOUT_FILENO, "R", 1) != 1)
        return 23;
    int result = strcmp(argv[1], "fdatasync") == 0 ? fdatasync(descriptor) : fsync(descriptor);
    close(descriptor);
    return result == 0 ? 0 : 24;
}
"""


class TestFailure(Exception):
    pass


def require(condition: bool, message: str) -> None:
    if not condition:
        raise TestFailure(message)


def private_write(path: Path, content: str) -> None:
    descriptor = os.open(path, os.O_WRONLY | os.O_CREAT | os.O_EXCL | os.O_CLOEXEC, 0o600)
    try:
        os.write(descriptor, content.encode("ascii"))
        os.fsync(descriptor)
    finally:
        os.close(descriptor)


def compile_tools(root: Path) -> tuple[Path, Path]:
    compiler = shutil.which("cc") or shutil.which("gcc")
    require(compiler is not None, "a C compiler is required")
    library = root / "barrier.so"
    worker_source = root / "worker.c"
    worker = root / "worker"
    worker_source.write_text(WORKER_SOURCE, encoding="utf-8")
    subprocess.run(
        [compiler, "-std=c11", "-O2", "-fPIC", "-shared", "-Wall", "-Wextra", "-Werror", str(SOURCE), "-o", str(library)],
        check=True,
        stdout=subprocess.DEVNULL,
        stderr=subprocess.PIPE,
    )
    subprocess.run(
        [compiler, "-std=c11", "-O2", "-Wall", "-Wextra", "-Werror", str(worker_source), "-o", str(worker)],
        check=True,
        stdout=subprocess.DEVNULL,
        stderr=subprocess.PIPE,
    )
    return library, worker


class ControlServer:
    def __init__(self, control: Path, release: bool, close_after_request: bool = False):
        self.path = control / "barrier.sock"
        self.release = release
        self.close_after_request = close_after_request
        self.requests: queue.Queue[bytes] = queue.Queue()
        self.allow_release = threading.Event()
        self.failure: BaseException | None = None
        self.server = socket.socket(socket.AF_UNIX, socket.SOCK_STREAM)
        old_umask = os.umask(0o177)
        try:
            self.server.bind(os.fspath(self.path))
        finally:
            os.umask(old_umask)
        os.chmod(self.path, 0o600, follow_symlinks=False)
        self.server.listen(1)
        self.server.settimeout(3)
        self.thread = threading.Thread(target=self._run, daemon=True)
        self.thread.start()

    def _run(self) -> None:
        try:
            connection, _ = self.server.accept()
            with connection:
                connection.settimeout(2)
                request = b""
                while not request.endswith(b"\n") and len(request) < 128:
                    chunk = connection.recv(128 - len(request))
                    if not chunk:
                        break
                    request += chunk
                self.requests.put(request)
                if self.close_after_request:
                    return
                if self.release:
                    require(self.allow_release.wait(3), "release was not authorized")
                    connection.sendall(b"release\n")
                else:
                    time.sleep(2)
        except socket.timeout:
            return
        except BaseException as exc:
            self.failure = exc
        finally:
            self.server.close()

    def close(self) -> None:
        self.allow_release.set()
        self.thread.join(timeout=4)
        if self.thread.is_alive():
            raise TestFailure("control server did not settle")
        if self.failure is not None:
            raise TestFailure("control server failed") from self.failure
        try:
            self.path.unlink()
        except FileNotFoundError:
            pass


def prepare_root(base: Path, name: str = "game") -> Path:
    root = base / name
    root.mkdir(mode=0o700)
    private_write(root / ".smapi-hard-state-disposable", MARKER)
    return root.resolve(strict=True)


def events_file(root: Path, content: str = APPLIED) -> Path:
    transaction = root / ".smapi-installer" / "transactions" / TRANSACTION
    transaction.mkdir(parents=True, mode=0o700)
    path = transaction / "events.jsonl"
    private_write(path, content)
    return path


def start_worker(
    worker: Path,
    library: Path,
    target: Path,
    root: Path,
    pid_file: Path,
    socket_path: Path,
    timeout_ms: int = 1000,
    function: str = "fsync",
    configure: bool = True,
) -> subprocess.Popen[bytes]:
    environment = os.environ.copy()
    environment["LD_PRELOAD"] = os.fspath(library)
    if configure:
        environment.update(
            {
                ENV_ROOT: os.fspath(root),
                ENV_PID: os.fspath(pid_file),
                ENV_SOCKET: os.fspath(socket_path),
                ENV_TIMEOUT: str(timeout_ms),
            }
        )
    process = subprocess.Popen(
        [worker, function, target],
        stdin=subprocess.PIPE,
        stdout=subprocess.PIPE,
        stderr=subprocess.PIPE,
        env=environment,
    )
    require(process.stdin is not None and process.stdout is not None, "worker pipes unavailable")
    return process


def admit(process: subprocess.Popen[bytes], pid_file: Path, expected_pid: int | None = None) -> None:
    private_write(pid_file, f"{process.pid if expected_pid is None else expected_pid}\n")
    assert process.stdin is not None
    process.stdin.write(b"G")
    process.stdin.flush()
    assert process.stdout is not None
    ready, _, _ = select.select([process.stdout], [], [], 2)
    require(bool(ready), "worker readiness timed out")
    require(process.stdout.read(1) == b"R", "worker did not reach sync call")


def finish(process: subprocess.Popen[bytes], timeout: float = 3) -> None:
    stdout, stderr = process.communicate(timeout=timeout)
    require(process.returncode == 0, "worker sync failed")
    require(stdout == b"" and stderr == b"", "worker emitted unexpected details")


def assert_no_request(server: ControlServer) -> None:
    time.sleep(0.2)
    require(server.requests.empty(), "an out-of-scope sync reached the barrier")


def test_inert(base: Path, library: Path, worker: Path) -> None:
    root = prepare_root(base, "inert-game")
    target = events_file(root)
    pid_file = base / "inert.pid"
    process = start_worker(worker, library, target, root, pid_file, base / "missing.sock", configure=False)
    assert process.stdin is not None
    process.stdin.write(b"G")
    process.stdin.flush()
    assert process.stdout is not None
    ready, _, _ = select.select([process.stdout], [], [], 2)
    require(bool(ready), "inert worker readiness timed out")
    require(process.stdout.read(1) == b"R", "inert worker did not reach sync")
    finish(process)


def test_ignored_scope(base: Path, library: Path, worker: Path, kind: str) -> None:
    root = prepare_root(base, f"{kind}-game")
    control = base / f"{kind}-control"
    control.mkdir(mode=0o700)
    server = ControlServer(control, release=True)
    pid_file = control / "expected.pid"
    if kind == "wrong-root":
        other = prepare_root(base, "other-game")
        target = events_file(other)
    elif kind == "wrong-file":
        target = root / "ordinary.jsonl"
        private_write(target, APPLIED)
    elif kind == "not-applied":
        target = events_file(root, INTENT)
    else:
        target = events_file(root)
    process = start_worker(worker, library, target, root, pid_file, server.path)
    admit(process, pid_file, process.pid + 1 if kind == "wrong-pid" else None)
    finish(process)
    assert_no_request(server)
    server.close()


def test_release(base: Path, library: Path, worker: Path, function: str) -> None:
    root = prepare_root(base, f"release-{function}-game")
    target = events_file(root)
    control = base / f"release-{function}-control"
    control.mkdir(mode=0o700)
    server = ControlServer(control, release=True)
    pid_file = control / "expected.pid"
    process = start_worker(worker, library, target, root, pid_file, server.path, function=function)
    admit(process, pid_file)
    request = server.requests.get(timeout=2)
    expected = f"SMAPI_HARD_STATE_BARRIER_V1 pid={process.pid} op=0\n".encode("ascii")
    require(request == expected, "barrier request was not the fixed path-free protocol")
    time.sleep(0.15)
    require(process.poll() is None, "worker did not remain at the durable barrier")
    server.allow_release.set()
    finish(process)
    server.close()


def test_timeout_or_peer_death(base: Path, library: Path, worker: Path, peer_death: bool) -> None:
    label = "peer-death" if peer_death else "timeout"
    root = prepare_root(base, f"{label}-game")
    target = events_file(root)
    control = base / f"{label}-control"
    control.mkdir(mode=0o700)
    server = ControlServer(control, release=False, close_after_request=peer_death)
    pid_file = control / "expected.pid"
    process = start_worker(worker, library, target, root, pid_file, server.path, timeout_ms=250)
    started = time.monotonic()
    admit(process, pid_file)
    request = server.requests.get(timeout=2)
    require(request.startswith(b"SMAPI_HARD_STATE_BARRIER_V1 pid="), "barrier did not notify its private peer")
    finish(process)
    elapsed = time.monotonic() - started
    if not peer_death:
        require(elapsed >= 0.20, "barrier timeout returned too early")
        require(elapsed < 2, "barrier timeout was not bounded")
    server.close()


def main() -> int:
    require(os.name == "posix" and Path("/proc/self/fd").is_dir(), "Linux procfs is required")
    with tempfile.TemporaryDirectory(prefix="smapi-hard-state-barrier-test-") as temporary:
        base = Path(temporary)
        os.chmod(base, 0o700)
        library, worker = compile_tools(base)
        test_inert(base, library, worker)
        for case in ("wrong-root", "wrong-file", "not-applied", "wrong-pid"):
            test_ignored_scope(base, library, worker, case)
        test_release(base, library, worker, "fsync")
        test_release(base, library, worker, "fdatasync")
        test_timeout_or_peer_death(base, library, worker, peer_death=False)
        test_timeout_or_peer_death(base, library, worker, peer_death=True)
    print("Linux GUI hard-state barrier tests passed.")
    return 0


if __name__ == "__main__":
    try:
        raise SystemExit(main())
    except (OSError, subprocess.SubprocessError, TestFailure, queue.Empty) as error:
        print(f"Linux GUI hard-state barrier tests failed: {type(error).__name__}", file=os.sys.stderr)
        raise SystemExit(1)
