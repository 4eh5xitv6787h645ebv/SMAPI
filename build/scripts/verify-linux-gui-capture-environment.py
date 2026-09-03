#!/usr/bin/env python3
"""Fail-closed verification of a Linux GUI screenshot capture environment.

The release contract names an environment; this module proves the observable
facts in the running desktop session instead of copying those names into an
evidence record.  It deliberately rejects ambiguous scale and theme settings.
"""

from __future__ import annotations

from dataclasses import dataclass
import importlib.util
import os
from pathlib import Path
import re
import resource
import stat
import subprocess
import sys
import tempfile
from types import MappingProxyType
from typing import Callable, Final, Mapping, Sequence


SCRIPT_DIRECTORY: Final = Path(__file__).resolve().parent
CONTRACT_PATH: Final = SCRIPT_DIRECTORY / "linux_gui_hard_state_capture_contract.py"
_SPEC = importlib.util.spec_from_file_location("linux_gui_hard_state_capture_contract", CONTRACT_PATH)
if _SPEC is None or _SPEC.loader is None:
    raise RuntimeError("capture contract could not be loaded")
contract = importlib.util.module_from_spec(_SPEC)
sys.modules[_SPEC.name] = contract
_SPEC.loader.exec_module(contract)


OS_RELEASE_PATH: Final = "/usr/lib/os-release"
MAX_OS_RELEASE_BYTES: Final = 65_536
MAX_COMMAND_BYTES: Final = 131_072
MAX_ENV_VALUE_BYTES: Final = 4_096
MAX_ENVIRONMENT_BYTES: Final = 16_384
COMMAND_TIMEOUT_SECONDS: Final = 8.0

XRANDR: Final = "/usr/bin/xrandr"
XDPYINFO: Final = "/usr/bin/xdpyinfo"
GSETTINGS: Final = "/usr/bin/gsettings"
GDBUS: Final = "/usr/bin/gdbus"
KSCREEN_DOCTOR: Final = "/usr/bin/kscreen-doctor"
KREADCONFIG5: Final = "/usr/bin/kreadconfig5"

_PASSTHROUGH_ENV: Final = (
    "DBUS_SESSION_BUS_ADDRESS",
    "DISPLAY",
    "GDK_DPI_SCALE",
    "GDK_SCALE",
    "GTK_THEME",
    "HOME",
    "KDE_FULL_SESSION",
    "QT_SCALE_FACTOR",
    "QT_AUTO_SCREEN_SCALE_FACTOR",
    "QT_SCREEN_SCALE_FACTORS",
    "QT_STYLE_OVERRIDE",
    "WAYLAND_DISPLAY",
    "XAUTHORITY",
    "XDG_CONFIG_HOME",
    "XDG_CURRENT_DESKTOP",
    "XDG_RUNTIME_DIR",
    "XDG_SESSION_DESKTOP",
    "XDG_SESSION_TYPE",
)


class CaptureEnvironmentError(RuntimeError):
    """The running session does not prove the requested closed profile."""


@dataclass(frozen=True, slots=True)
class CaptureEnvironmentFacts:
    schema_version: int
    profile_id: str
    distribution: str
    distribution_version: str
    architecture: str
    desktop: str
    session: str
    window_backend: str
    display_present: bool
    resolution_width: int
    resolution_height: int
    scale_percent: int
    theme: str

    def as_dict(self) -> dict[str, object]:
        """Return the exact public evidence shape (never host/user/path data)."""

        return {
            "schemaVersion": self.schema_version,
            "profileId": self.profile_id,
            "distribution": self.distribution,
            "distributionVersion": self.distribution_version,
            "architecture": self.architecture,
            "desktop": self.desktop,
            "session": self.session,
            "windowBackend": self.window_backend,
            "displayPresent": self.display_present,
            "resolutionWidth": self.resolution_width,
            "resolutionHeight": self.resolution_height,
            "scalePercent": self.scale_percent,
            "theme": self.theme,
        }


@dataclass(frozen=True, slots=True)
class _OsReleaseSnapshot:
    fields: Mapping[str, str]
    identity: tuple[int, int, int, int, int, int]


@dataclass(frozen=True, slots=True)
class _CommandResult:
    returncode: int
    stdout: bytes
    stderr: bytes


@dataclass(frozen=True, slots=True)
class _SessionObservation:
    resolution: tuple[int, int]
    backend: str
    scale_percent: int
    theme: str


def _parse_os_release(data: bytes) -> Mapping[str, str]:
    try:
        text = data.decode("utf-8", "strict")
    except UnicodeDecodeError as ex:
        raise CaptureEnvironmentError("operating-system metadata is not UTF-8") from ex
    fields: dict[str, str] = {}
    key_pattern = re.compile(r"^[A-Z][A-Z0-9_]{0,63}$")
    for raw_line in text.splitlines():
        if not raw_line or raw_line.startswith("#"):
            continue
        if "=" not in raw_line:
            raise CaptureEnvironmentError("malformed operating-system metadata")
        key, raw_value = raw_line.split("=", 1)
        if not key_pattern.fullmatch(key) or key in fields:
            raise CaptureEnvironmentError("malformed operating-system metadata")
        if raw_value.startswith('"'):
            if len(raw_value) < 2 or not raw_value.endswith('"'):
                raise CaptureEnvironmentError("malformed operating-system metadata")
            value = raw_value[1:-1]
            if re.search(r"\\(?![\\\"$`])", value):
                raise CaptureEnvironmentError("unsupported operating-system metadata escape")
            value = re.sub(r"\\([\\\"$`])", r"\1", value)
        else:
            if any(character.isspace() for character in raw_value):
                raise CaptureEnvironmentError("malformed operating-system metadata")
            value = raw_value
        if "\x00" in value or len(value.encode("utf-8")) > 4_096:
            raise CaptureEnvironmentError("malformed operating-system metadata")
        fields[key] = value
    return MappingProxyType(fields)


def _read_os_release() -> _OsReleaseSnapshot:
    flags = os.O_RDONLY | os.O_CLOEXEC
    if hasattr(os, "O_NOFOLLOW"):
        flags |= os.O_NOFOLLOW
    try:
        descriptor = os.open(OS_RELEASE_PATH, flags)
    except OSError as ex:
        raise CaptureEnvironmentError("operating-system metadata is unavailable") from ex
    try:
        before = os.fstat(descriptor)
        if (
            not stat.S_ISREG(before.st_mode)
            or before.st_uid != 0
            or before.st_nlink != 1
            or before.st_mode & (stat.S_IWGRP | stat.S_IWOTH)
            or before.st_size < 1
            or before.st_size > MAX_OS_RELEASE_BYTES
        ):
            raise CaptureEnvironmentError("operating-system metadata is not a stable root-owned file")
        chunks: list[bytes] = []
        remaining = MAX_OS_RELEASE_BYTES + 1
        while remaining:
            chunk = os.read(descriptor, min(16_384, remaining))
            if not chunk:
                break
            chunks.append(chunk)
            remaining -= len(chunk)
        data = b"".join(chunks)
        if len(data) > MAX_OS_RELEASE_BYTES:
            raise CaptureEnvironmentError("operating-system metadata exceeds its bound")
        after = os.fstat(descriptor)
        identity_before = (
            before.st_dev, before.st_ino, before.st_mode, before.st_uid,
            before.st_size, before.st_mtime_ns,
        )
        identity_after = (
            after.st_dev, after.st_ino, after.st_mode, after.st_uid,
            after.st_size, after.st_mtime_ns,
        )
        if identity_before != identity_after or len(data) != before.st_size:
            raise CaptureEnvironmentError("operating-system metadata changed while read")
        return _OsReleaseSnapshot(_parse_os_release(data), identity_after)
    finally:
        os.close(descriptor)


def _bounded_environment(source: Mapping[str, str]) -> dict[str, str]:
    result = {"LANG": "C", "LC_ALL": "C", "PATH": "/usr/bin:/bin"}
    total = sum(len(key) + len(value) for key, value in result.items())
    for key in _PASSTHROUGH_ENV:
        value = source.get(key)
        if value is None:
            continue
        if not isinstance(value, str) or "\x00" in value:
            raise CaptureEnvironmentError("desktop environment contains an invalid value")
        encoded = value.encode("utf-8", "strict")
        if len(encoded) > MAX_ENV_VALUE_BYTES:
            raise CaptureEnvironmentError("desktop environment value exceeds its bound")
        total += len(key) + len(encoded)
        if total > MAX_ENVIRONMENT_BYTES:
            raise CaptureEnvironmentError("desktop environment exceeds its bound")
        result[key] = value
    return result


def _limit_child() -> None:
    resource.setrlimit(resource.RLIMIT_FSIZE, (MAX_COMMAND_BYTES + 1, MAX_COMMAND_BYTES + 1))
    resource.setrlimit(resource.RLIMIT_NOFILE, (32, 32))
    resource.setrlimit(resource.RLIMIT_CORE, (0, 0))


def _secure_executable(path: str) -> tuple[int, int, int, int]:
    try:
        details = os.stat(path, follow_symlinks=False)
    except OSError as ex:
        raise CaptureEnvironmentError("required desktop probe is unavailable") from ex
    if (
        not stat.S_ISREG(details.st_mode)
        or details.st_uid != 0
        or details.st_mode & (stat.S_IWGRP | stat.S_IWOTH)
        or not details.st_mode & stat.S_IXUSR
        or details.st_size < 1
    ):
        raise CaptureEnvironmentError("required desktop probe is not a stable root-owned executable")
    return details.st_dev, details.st_ino, details.st_size, details.st_mtime_ns


def _run_bounded_command(argv: Sequence[str], environment: Mapping[str, str]) -> _CommandResult:
    if not argv or argv[0] not in {XRANDR, XDPYINFO, GSETTINGS, GDBUS, KSCREEN_DOCTOR, KREADCONFIG5}:
        raise CaptureEnvironmentError("unexpected desktop probe command")
    identity = _secure_executable(argv[0])
    with tempfile.TemporaryFile(mode="w+b") as stdout_file, tempfile.TemporaryFile(mode="w+b") as stderr_file:
        try:
            process = subprocess.Popen(
                tuple(argv), stdin=subprocess.DEVNULL, stdout=stdout_file, stderr=stderr_file,
                env=dict(environment), close_fds=True, shell=False, preexec_fn=_limit_child,
            )
            try:
                returncode = process.wait(timeout=COMMAND_TIMEOUT_SECONDS)
            except subprocess.TimeoutExpired:
                process.kill()
                process.wait(timeout=2)
                raise CaptureEnvironmentError("desktop probe timed out") from None
        except OSError as ex:
            raise CaptureEnvironmentError("desktop probe could not execute") from ex
        if _secure_executable(argv[0]) != identity:
            raise CaptureEnvironmentError("desktop probe executable changed during execution")
        stdout_file.seek(0, os.SEEK_END)
        stderr_file.seek(0, os.SEEK_END)
        if stdout_file.tell() > MAX_COMMAND_BYTES or stderr_file.tell() > MAX_COMMAND_BYTES:
            raise CaptureEnvironmentError("desktop probe output exceeds its bound")
        stdout_file.seek(0)
        stderr_file.seek(0)
        return _CommandResult(returncode, stdout_file.read(MAX_COMMAND_BYTES + 1), stderr_file.read(MAX_COMMAND_BYTES + 1))


def _command_text(
    runner: Callable[[Sequence[str], Mapping[str, str]], _CommandResult],
    argv: Sequence[str],
    environment: Mapping[str, str],
) -> str:
    result = runner(tuple(argv), environment)
    if (
        not isinstance(result, _CommandResult)
        or result.returncode != 0
        or len(result.stdout) > MAX_COMMAND_BYTES
        or len(result.stderr) > MAX_COMMAND_BYTES
        or result.stderr.strip()
    ):
        raise CaptureEnvironmentError("desktop probe failed")
    try:
        return result.stdout.decode("utf-8", "strict")
    except UnicodeDecodeError as ex:
        raise CaptureEnvironmentError("desktop probe output is not UTF-8") from ex


def _parse_resolution(xrandr: str, xdpyinfo: str) -> tuple[int, int]:
    current = re.findall(r"\bcurrent ([0-9]{1,5}) x ([0-9]{1,5})\b", xrandr)
    dimensions = re.findall(r"^\s*dimensions:\s*([0-9]{1,5})x([0-9]{1,5}) pixels\b", xdpyinfo, re.MULTILINE)
    if len(current) != 1 or len(dimensions) != 1:
        raise CaptureEnvironmentError("desktop root resolution is ambiguous")
    xrandr_value = tuple(int(value) for value in current[0])
    xdpyinfo_value = tuple(int(value) for value in dimensions[0])
    if xrandr_value != xdpyinfo_value:
        raise CaptureEnvironmentError("desktop root resolution probes disagree")
    return xrandr_value  # type: ignore[return-value]


def _split_top_level(value: str) -> list[str]:
    pieces: list[str] = []
    start = 0
    nesting = 0
    quote: str | None = None
    escaped = False
    for index, character in enumerate(value):
        if quote is not None:
            if escaped:
                escaped = False
            elif character == "\\":
                escaped = True
            elif character == quote:
                quote = None
            continue
        if character in {"'", '"'}:
            quote = character
        elif character in "([{<":
            nesting += 1
        elif character in ")]}>":
            nesting -= 1
            if nesting < 0:
                raise CaptureEnvironmentError("malformed compositor output")
        elif character == "," and nesting == 0:
            pieces.append(value[start:index].strip())
            start = index + 1
    if quote is not None or nesting != 0:
        raise CaptureEnvironmentError("malformed compositor output")
    pieces.append(value[start:].strip())
    return pieces


def _gnome_scale(output: str) -> int:
    stripped = output.strip()
    if not stripped.startswith("(") or not stripped.endswith(")"):
        raise CaptureEnvironmentError("malformed GNOME compositor output")
    top = _split_top_level(stripped[1:-1])
    if len(top) != 4 or not top[2].startswith("[") or not top[2].endswith("]"):
        raise CaptureEnvironmentError("malformed GNOME compositor output")
    logical_text = top[2][1:-1].strip()
    if not logical_text:
        raise CaptureEnvironmentError("GNOME compositor reports no logical monitors")
    logical_monitors = _split_top_level(logical_text)
    scales: list[str] = []
    for monitor in logical_monitors:
        if not monitor.startswith("(") or not monitor.endswith(")"):
            raise CaptureEnvironmentError("malformed GNOME logical monitor")
        fields = _split_top_level(monitor[1:-1])
        if len(fields) < 6 or not re.fullmatch(r"1(?:\.0+)?", fields[2]):
            raise CaptureEnvironmentError("GNOME compositor scale is not exactly 100 percent")
        scales.append(fields[2])
    if not scales:
        raise CaptureEnvironmentError("GNOME compositor scale is ambiguous")
    return 100


def _kde_scale(output: str) -> int:
    scales = re.findall(r"^\s*Scale:\s*([^\s]+)\s*$", output, re.MULTILINE)
    if not scales or any(not re.fullmatch(r"1(?:\.0+)?", value) for value in scales):
        raise CaptureEnvironmentError("KDE compositor scale is not exactly 100 percent")
    return 100


def _light_theme(desktop: str, theme_output: str, app_environment: Mapping[str, str]) -> str:
    # Environment overrides win for the GUI toolkit and could contradict the
    # desktop setting, so anything except an explicit light override is rejected.
    for key in ("GTK_THEME", "QT_STYLE_OVERRIDE"):
        value = app_environment.get(key)
        if value is not None and (not value or "dark" in value.casefold()):
            raise CaptureEnvironmentError("application theme override is not provably light")
    value = theme_output.strip()
    if desktop == "GNOME":
        if value not in {"'default'", "'prefer-light'"}:
            raise CaptureEnvironmentError("GNOME application theme is not explicitly light")
    elif value != "BreezeLight":
        # Arbitrary KDE color-scheme names do not prove luminance.  The closed
        # capture image profile therefore accepts the stock explicit light scheme.
        raise CaptureEnvironmentError("KDE application theme is not explicitly light")
    return "light"


def _observe_session(
    profile: object,
    environment: Mapping[str, str],
    runner: Callable[[Sequence[str], Mapping[str, str]], _CommandResult],
) -> _SessionObservation:
    exact_scale_overrides = {
        "GDK_SCALE": {"1"},
        "GDK_DPI_SCALE": {"1", "1.0"},
        "QT_SCALE_FACTOR": {"1", "1.0"},
        "QT_AUTO_SCREEN_SCALE_FACTOR": {"0"},
    }
    for key, accepted in exact_scale_overrides.items():
        if key in environment and environment[key] not in accepted:
            raise CaptureEnvironmentError("application scale override is not exactly 100 percent")
    if "QT_SCREEN_SCALE_FACTORS" in environment:
        # This syntax permits per-screen values and is intentionally not guessed.
        raise CaptureEnvironmentError("per-screen application scale override is ambiguous")
    xrandr = _command_text(runner, (XRANDR, "--current"), environment)
    xdpyinfo = _command_text(runner, (XDPYINFO, "-queryExtensions"), environment)
    resolution = _parse_resolution(xrandr, xdpyinfo)
    has_xwayland = re.search(r"\bXWAYLAND\b", xdpyinfo) is not None
    backend = "xwayland" if has_xwayland else "x11"
    desktop = profile.desktop.value
    if desktop == "GNOME":
        compositor = _command_text(
            runner,
            (GDBUS, "call", "--session", "--dest", "org.gnome.Mutter.DisplayConfig",
             "--object-path", "/org/gnome/Mutter/DisplayConfig", "--method",
             "org.gnome.Mutter.DisplayConfig.GetCurrentState"),
            environment,
        )
        scale = _gnome_scale(compositor)
        theme_output = _command_text(
            runner, (GSETTINGS, "get", "org.gnome.desktop.interface", "color-scheme"), environment,
        )
    else:
        scale = _kde_scale(_command_text(runner, (KSCREEN_DOCTOR, "-o"), environment))
        theme_output = _command_text(
            runner,
            (KREADCONFIG5, "--file", "kdeglobals", "--group", "General", "--key", "ColorScheme"),
            environment,
        )
    theme = _light_theme(desktop, theme_output, environment)
    return _SessionObservation(resolution, backend, scale, theme)


def _environment_snapshot(reader: Callable[[], Mapping[str, str]]) -> Mapping[str, str]:
    source = reader()
    if not isinstance(source, Mapping):
        raise CaptureEnvironmentError("desktop environment is unavailable")
    selected: dict[str, str] = {}
    total = 0
    for key in _PASSTHROUGH_ENV:
        value = source.get(key)
        if value is None:
            continue
        if not isinstance(value, str) or "\x00" in value:
            raise CaptureEnvironmentError("desktop environment contains an invalid value")
        size = len(value.encode("utf-8", "strict"))
        if size > MAX_ENV_VALUE_BYTES:
            raise CaptureEnvironmentError("desktop environment value exceeds its bound")
        total += len(key) + size
        if total > MAX_ENVIRONMENT_BYTES:
            raise CaptureEnvironmentError("desktop environment exceeds its bound")
        selected[key] = value
    required = ("DISPLAY", "XDG_SESSION_TYPE", "XDG_CURRENT_DESKTOP")
    for key in required:
        value = selected.get(key)
        if not isinstance(value, str) or not value:
            raise CaptureEnvironmentError("required desktop session value is absent")
    display = selected["DISPLAY"]
    if len(display.encode("utf-8")) > 256 or not re.fullmatch(r":[0-9]{1,5}(?:\.[0-9]{1,2})?", display):
        raise CaptureEnvironmentError("DISPLAY is not a bounded local X display")
    return MappingProxyType(selected)


def verify_capture_environment(
    profile_id: str,
    *,
    _os_release_reader: Callable[[], _OsReleaseSnapshot] = _read_os_release,
    _uname_reader: Callable[[], object] = os.uname,
    _environment_reader: Callable[[], Mapping[str, str]] = lambda: os.environ,
    _command_runner: Callable[[Sequence[str], Mapping[str, str]], _CommandResult] = _run_bounded_command,
) -> CaptureEnvironmentFacts:
    """Verify and return bounded facts for one closed capture profile."""

    try:
        profile = contract.environment_profile(profile_id)
    except (KeyError, ValueError) as ex:
        raise CaptureEnvironmentError("unknown capture environment profile") from ex

    os_first = _os_release_reader()
    if not isinstance(os_first, _OsReleaseSnapshot):
        raise CaptureEnvironmentError("invalid operating-system observation")
    fields = os_first.fields
    if (
        fields.get("ID") != "ubuntu"
        or fields.get("NAME") != "Ubuntu"
        or fields.get("VERSION_ID") != "24.04"
        or fields.get("PRETTY_NAME") != "Ubuntu 24.04.4 LTS"
    ):
        raise CaptureEnvironmentError("operating-system release does not match the capture profile")

    uname_first = _uname_reader()
    machine = getattr(uname_first, "machine", None)
    if machine != "x86_64":
        raise CaptureEnvironmentError("machine architecture does not match amd64")

    source_first = _environment_snapshot(_environment_reader)
    session = source_first["XDG_SESSION_TYPE"]
    desktop_value = source_first["XDG_CURRENT_DESKTOP"]
    expected_desktops = {"GNOME", "ubuntu:GNOME"} if profile.desktop.value == "GNOME" else {"KDE"}
    if session != profile.session.value or desktop_value not in expected_desktops:
        raise CaptureEnvironmentError("desktop session does not match the capture profile")
    command_environment = _bounded_environment(source_first)
    observation_first = _observe_session(profile, command_environment, _command_runner)

    # Repeat every mutable observation.  Equality is required; a desktop mode,
    # scale, theme, binary, metadata, or environment transition invalidates capture.
    observation_second = _observe_session(profile, command_environment, _command_runner)
    os_second = _os_release_reader()
    uname_second = _uname_reader()
    source_second = _environment_snapshot(_environment_reader)
    if (
        observation_first != observation_second
        or os_first != os_second
        or getattr(uname_second, "machine", None) != machine
        or dict(source_first) != dict(source_second)
    ):
        raise CaptureEnvironmentError("capture environment changed during verification")

    if observation_first.resolution != (profile.resolution_width, profile.resolution_height):
        raise CaptureEnvironmentError("desktop root resolution does not match the capture profile")
    if observation_first.backend != profile.window_backend.value:
        raise CaptureEnvironmentError("X window backend does not match the capture profile")
    if observation_first.scale_percent != profile.scale_percent or observation_first.theme != profile.theme.value:
        raise CaptureEnvironmentError("desktop appearance does not match the capture profile")

    return CaptureEnvironmentFacts(
        1, profile.profile_id.value, profile.distribution.value,
        profile.distribution_version, profile.architecture.value,
        profile.desktop.value, profile.session.value, profile.window_backend.value,
        True, profile.resolution_width, profile.resolution_height,
        profile.scale_percent, profile.theme.value,
    )


__all__ = (
    "CaptureEnvironmentError",
    "CaptureEnvironmentFacts",
    "verify_capture_environment",
)
