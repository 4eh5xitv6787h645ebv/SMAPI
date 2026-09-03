#!/usr/bin/env python3
"""Pure, closed capture contract for Linux GUI hard-state evidence."""

from __future__ import annotations

from dataclasses import dataclass
from enum import Enum
from types import MappingProxyType
from typing import Final, Mapping, TypeVar
import re


class Scenario(str, Enum):
    E2_PERMISSION = "E2-permission"
    E2_READ_ONLY = "E2-read-only"
    E2_DISK_FULL = "E2-disk-full"
    E2_CROSS_DEVICE = "E2-cross-device"
    C2 = "C2"
    C3 = "C3"
    E5 = "E5"
    E6 = "E6"


class EvidenceId(str, Enum):
    E2 = "E2"
    C2 = "C2"
    C3 = "C3"
    E5 = "E5"
    E6 = "E6"


class Fault(str, Enum):
    PERMISSION = "permission"
    READ_ONLY = "read-only"
    DISK_FULL = "disk-full"
    CROSS_DEVICE = "cross-device"


class AtspiRoute(str, Enum):
    E2_PERMISSION = "e2-permission"
    E2_READ_ONLY = "e2-read-only"
    E2_DISK_FULL = "e2-disk-full"
    E2_CROSS_DEVICE = "e2-cross-device"
    C3_TERMINAL = "c3-terminal"
    E5_BACKEND_LOSS = "e5-backend-loss"
    E6_AUTOMATIC_RECOVERY = "e6-automatic-recovery"


class Milestone(str, Enum):
    E2_PERMISSION = "state.e2-permission"
    E2_READ_ONLY = "state.e2-read-only"
    E2_DISK_FULL = "state.e2-disk-full"
    E2_CROSS_DEVICE = "state.e2-cross-device"
    C2 = "state.c2"
    C3 = "terminal.c3"
    E5 = "state.e5"
    E6 = "terminal.e6"


class Operation(str, Enum):
    INSTALL = "install"
    AUTOMATIC_RECOVERY = "automatic-recovery"


class BoundaryTrigger(str, Enum):
    FILESYSTEM_EACCES = "filesystem-eacces"
    FILESYSTEM_EROFS = "filesystem-erofs"
    FILESYSTEM_ENOSPC = "filesystem-enospc"
    FILESYSTEM_EXDEV = "filesystem-exdev"
    POST_APPLIED_CANCEL = "post-applied-cancel"
    POST_APPLIED_BACKEND_SIGKILL = "post-applied-backend-sigkill"
    FRESH_SESSION_AUTOMATIC_RECOVERY = "fresh-session-automatic-recovery"


class VisibleState(str, Enum):
    INSTALL_FAILED_UNCHANGED = "install-failed-unchanged"
    INSTALL_FAILED_ROLLED_BACK = "install-failed-rolled-back"
    CANCELLATION_FINISHING_SAFELY = "cancellation-finishing-safely"
    CANCELLED_AND_ROLLED_BACK = "cancelled-and-rolled-back"
    BACKEND_STATE_UNKNOWN_RECOVERY_REQUIRED = "backend-state-unknown-recovery-required"
    AUTOMATIC_RECOVERY_COMPLETED_FRESH_INSPECTION_REQUIRED = (
        "automatic-recovery-completed-fresh-inspection-required"
    )


class DurableState(str, Enum):
    UNCHANGED = "unchanged"
    APPLIED = "applied"
    ROLLED_BACK = "rolled-back"
    RECOVERY_REQUIRED = "recovery-required"
    RECOVERY_COMPLETED = "recovery-completed"


class EnvironmentId(str, Enum):
    UBUNTU_GNOME_X11 = "ubuntu-24.04-gnome-x11"
    UBUNTU_GNOME_XWAYLAND = "ubuntu-24.04-gnome-xwayland"
    UBUNTU_KDE_X11 = "ubuntu-24.04-kde-x11"
    UBUNTU_KDE_XWAYLAND = "ubuntu-24.04-kde-xwayland"


class Distribution(str, Enum):
    UBUNTU = "Ubuntu"


class Desktop(str, Enum):
    GNOME = "GNOME"
    KDE = "KDE"


class SessionType(str, Enum):
    X11 = "x11"
    WAYLAND = "wayland"


class WindowBackend(str, Enum):
    X11 = "x11"
    XWAYLAND = "xwayland"


class Architecture(str, Enum):
    AMD64 = "amd64"


class Theme(str, Enum):
    LIGHT = "light"


@dataclass(frozen=True, slots=True)
class CaptureSpec:
    scenario: Scenario
    evidence_id: EvidenceId
    fault: Fault | None
    atspi_route: AtspiRoute
    capture_milestone: Milestone
    required_terminal_milestone: Milestone
    operation: Operation
    boundary_trigger: BoundaryTrigger
    visible_state: VisibleState
    durable_before: DurableState
    durable_at_capture: DurableState
    durable_after: DurableState
    window_title: str
    output_basename: str
    docs_anchor: str


@dataclass(frozen=True, slots=True)
class EnvironmentProfile:
    profile_id: EnvironmentId
    distribution: Distribution
    distribution_version: str
    desktop: Desktop
    architecture: Architecture
    session: SessionType
    window_backend: WindowBackend
    scale_percent: int
    theme: Theme
    resolution_width: int
    resolution_height: int


EXECUTION_WINDOW_TITLE: Final = "SMAPI Linux Installer — Run operation"
UBUNTU_VERSION: Final = "24.04.4 LTS"
DOCS_FILE: Final = "linux-gui-screenshot-evidence.md"


_CAPTURE_SPECS = (
    CaptureSpec(
        Scenario.E2_PERMISSION, EvidenceId.E2, Fault.PERMISSION, AtspiRoute.E2_PERMISSION,
        Milestone.E2_PERMISSION, Milestone.E2_PERMISSION, Operation.INSTALL,
        BoundaryTrigger.FILESYSTEM_EACCES, VisibleState.INSTALL_FAILED_UNCHANGED,
        DurableState.UNCHANGED, DurableState.UNCHANGED, DurableState.UNCHANGED,
        EXECUTION_WINDOW_TITLE, "e2-permission", f"{DOCS_FILE}#evidence-e2",
    ),
    CaptureSpec(
        Scenario.E2_READ_ONLY, EvidenceId.E2, Fault.READ_ONLY, AtspiRoute.E2_READ_ONLY,
        Milestone.E2_READ_ONLY, Milestone.E2_READ_ONLY, Operation.INSTALL,
        BoundaryTrigger.FILESYSTEM_EROFS, VisibleState.INSTALL_FAILED_UNCHANGED,
        DurableState.UNCHANGED, DurableState.UNCHANGED, DurableState.UNCHANGED,
        EXECUTION_WINDOW_TITLE, "e2-read-only", f"{DOCS_FILE}#evidence-e2",
    ),
    CaptureSpec(
        Scenario.E2_DISK_FULL, EvidenceId.E2, Fault.DISK_FULL, AtspiRoute.E2_DISK_FULL,
        Milestone.E2_DISK_FULL, Milestone.E2_DISK_FULL, Operation.INSTALL,
        BoundaryTrigger.FILESYSTEM_ENOSPC, VisibleState.INSTALL_FAILED_UNCHANGED,
        DurableState.UNCHANGED, DurableState.UNCHANGED, DurableState.UNCHANGED,
        EXECUTION_WINDOW_TITLE, "e2-disk-full", f"{DOCS_FILE}#evidence-e2",
    ),
    CaptureSpec(
        Scenario.E2_CROSS_DEVICE, EvidenceId.E2, Fault.CROSS_DEVICE, AtspiRoute.E2_CROSS_DEVICE,
        Milestone.E2_CROSS_DEVICE, Milestone.E2_CROSS_DEVICE, Operation.INSTALL,
        BoundaryTrigger.FILESYSTEM_EXDEV, VisibleState.INSTALL_FAILED_ROLLED_BACK,
        DurableState.UNCHANGED, DurableState.ROLLED_BACK, DurableState.ROLLED_BACK,
        EXECUTION_WINDOW_TITLE, "e2-cross-device", f"{DOCS_FILE}#evidence-e2",
    ),
    CaptureSpec(
        Scenario.C2, EvidenceId.C2, None, AtspiRoute.C3_TERMINAL,
        Milestone.C2, Milestone.C3, Operation.INSTALL, BoundaryTrigger.POST_APPLIED_CANCEL,
        VisibleState.CANCELLATION_FINISHING_SAFELY, DurableState.UNCHANGED,
        DurableState.APPLIED, DurableState.ROLLED_BACK, EXECUTION_WINDOW_TITLE,
        "c2-finishing-safely", f"{DOCS_FILE}#evidence-c2",
    ),
    CaptureSpec(
        Scenario.C3, EvidenceId.C3, None, AtspiRoute.C3_TERMINAL,
        Milestone.C3, Milestone.C3, Operation.INSTALL, BoundaryTrigger.POST_APPLIED_CANCEL,
        VisibleState.CANCELLED_AND_ROLLED_BACK, DurableState.UNCHANGED,
        DurableState.ROLLED_BACK, DurableState.ROLLED_BACK, EXECUTION_WINDOW_TITLE,
        "c3-cancelled-rolled-back", f"{DOCS_FILE}#evidence-c3",
    ),
    CaptureSpec(
        Scenario.E5, EvidenceId.E5, None, AtspiRoute.E5_BACKEND_LOSS,
        Milestone.E5, Milestone.E5, Operation.INSTALL,
        BoundaryTrigger.POST_APPLIED_BACKEND_SIGKILL,
        VisibleState.BACKEND_STATE_UNKNOWN_RECOVERY_REQUIRED, DurableState.UNCHANGED,
        DurableState.RECOVERY_REQUIRED, DurableState.RECOVERY_REQUIRED,
        EXECUTION_WINDOW_TITLE, "e5-recovery-required", f"{DOCS_FILE}#evidence-e5",
    ),
    CaptureSpec(
        Scenario.E6, EvidenceId.E6, None, AtspiRoute.E6_AUTOMATIC_RECOVERY,
        Milestone.E6, Milestone.E6, Operation.AUTOMATIC_RECOVERY,
        BoundaryTrigger.FRESH_SESSION_AUTOMATIC_RECOVERY,
        VisibleState.AUTOMATIC_RECOVERY_COMPLETED_FRESH_INSPECTION_REQUIRED,
        DurableState.RECOVERY_REQUIRED, DurableState.RECOVERY_COMPLETED,
        DurableState.RECOVERY_COMPLETED, EXECUTION_WINDOW_TITLE,
        "e6-automatic-recovery", f"{DOCS_FILE}#evidence-e6",
    ),
)

_ENVIRONMENT_PROFILES = (
    EnvironmentProfile(
        EnvironmentId.UBUNTU_GNOME_X11, Distribution.UBUNTU, UBUNTU_VERSION,
        Desktop.GNOME, Architecture.AMD64, SessionType.X11, WindowBackend.X11,
        100, Theme.LIGHT, 1920, 1080,
    ),
    EnvironmentProfile(
        EnvironmentId.UBUNTU_GNOME_XWAYLAND, Distribution.UBUNTU, UBUNTU_VERSION,
        Desktop.GNOME, Architecture.AMD64, SessionType.WAYLAND, WindowBackend.XWAYLAND,
        100, Theme.LIGHT, 1920, 1080,
    ),
    EnvironmentProfile(
        EnvironmentId.UBUNTU_KDE_X11, Distribution.UBUNTU, UBUNTU_VERSION,
        Desktop.KDE, Architecture.AMD64, SessionType.X11, WindowBackend.X11,
        100, Theme.LIGHT, 1920, 1080,
    ),
    EnvironmentProfile(
        EnvironmentId.UBUNTU_KDE_XWAYLAND, Distribution.UBUNTU, UBUNTU_VERSION,
        Desktop.KDE, Architecture.AMD64, SessionType.WAYLAND, WindowBackend.XWAYLAND,
        100, Theme.LIGHT, 1920, 1080,
    ),
)

CAPTURE_SPECS: Final[tuple[CaptureSpec, ...]] = _CAPTURE_SPECS
ENVIRONMENT_PROFILES: Final[tuple[EnvironmentProfile, ...]] = _ENVIRONMENT_PROFILES
CAPTURE_SPEC_BY_SCENARIO: Final[Mapping[Scenario, CaptureSpec]] = MappingProxyType(
    {spec.scenario: spec for spec in CAPTURE_SPECS}
)
ENVIRONMENT_PROFILE_BY_ID: Final[Mapping[EnvironmentId, EnvironmentProfile]] = MappingProxyType(
    {profile.profile_id: profile for profile in ENVIRONMENT_PROFILES}
)


_EnumType = TypeVar("_EnumType", bound=Enum)


def _closed_enum(value: object, enum_type: type[_EnumType], label: str) -> _EnumType:
    if isinstance(value, enum_type):
        return value
    if not isinstance(value, str):
        raise ValueError(f"unknown {label}")
    try:
        return enum_type(value)
    except ValueError:
        raise ValueError(f"unknown {label}: {value}") from None


def capture_spec(scenario: Scenario | str) -> CaptureSpec:
    """Return one exact scenario spec, rejecting every value outside the closed set."""

    return CAPTURE_SPEC_BY_SCENARIO[_closed_enum(scenario, Scenario, "scenario")]


def environment_profile(profile_id: EnvironmentId | str) -> EnvironmentProfile:
    """Return one exact environment profile, rejecting every value outside the closed set."""

    return ENVIRONMENT_PROFILE_BY_ID[_closed_enum(profile_id, EnvironmentId, "environment profile")]


def validate_contract() -> None:
    """Fail if the module's immutable tables are internally inconsistent."""

    if tuple(spec.scenario for spec in CAPTURE_SPECS) != tuple(Scenario):
        raise RuntimeError("capture scenario set or order is not closed")
    if len(CAPTURE_SPEC_BY_SCENARIO) != len(CAPTURE_SPECS):
        raise RuntimeError("capture scenarios are not unique")
    if len({spec.output_basename for spec in CAPTURE_SPECS}) != len(CAPTURE_SPECS):
        raise RuntimeError("capture output basenames are not unique")
    safe_basename = re.compile(r"^[a-z0-9][a-z0-9-]{1,63}$")
    for spec in CAPTURE_SPECS:
        expected_evidence = EvidenceId.E2 if spec.scenario.value.startswith("E2-") else EvidenceId(spec.scenario.value)
        expected_fault = Fault(spec.scenario.value[3:]) if expected_evidence is EvidenceId.E2 else None
        if spec.evidence_id is not expected_evidence or spec.fault is not expected_fault:
            raise RuntimeError(f"scenario derivation mismatch: {spec.scenario.value}")
        if spec.window_title != EXECUTION_WINDOW_TITLE or not safe_basename.fullmatch(spec.output_basename):
            raise RuntimeError(f"unsafe capture identity: {spec.scenario.value}")
        if spec.docs_anchor != f"{DOCS_FILE}#evidence-{spec.evidence_id.value.casefold()}":
            raise RuntimeError(f"documentation anchor mismatch: {spec.scenario.value}")
    if tuple(profile.profile_id for profile in ENVIRONMENT_PROFILES) != tuple(EnvironmentId):
        raise RuntimeError("environment profile set or order is not closed")
    if len(ENVIRONMENT_PROFILE_BY_ID) != len(ENVIRONMENT_PROFILES):
        raise RuntimeError("environment profile IDs are not unique")
    for profile in ENVIRONMENT_PROFILES:
        if (
            profile.distribution_version != UBUNTU_VERSION
            or profile.architecture is not Architecture.AMD64
            or profile.scale_percent != 100
            or profile.theme is not Theme.LIGHT
            or (profile.resolution_width, profile.resolution_height) != (1920, 1080)
        ):
            raise RuntimeError(f"environment constant mismatch: {profile.profile_id.value}")
        expected_backend = WindowBackend.X11 if profile.session is SessionType.X11 else WindowBackend.XWAYLAND
        if profile.window_backend is not expected_backend:
            raise RuntimeError(f"environment backend mismatch: {profile.profile_id.value}")
        if profile.distribution is not Distribution.UBUNTU:
            raise RuntimeError(f"environment desktop mismatch: {profile.profile_id.value}")


validate_contract()
