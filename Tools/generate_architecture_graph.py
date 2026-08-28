#!/usr/bin/env python3
"""Generate a zoomable SVG architecture/call graph from the GAMBIT C# sources."""

from __future__ import annotations

import html
import math
import re
from collections import defaultdict
from dataclasses import dataclass, field
from pathlib import Path


ROOT = Path(__file__).resolve().parents[1]
SOURCE_ROOTS = (ROOT / "Assets" / "Scripts", ROOT / "Assets" / "Editor", ROOT / "Assets" / "Tests")
OUTPUT = ROOT / "Documentation" / "GAMBIT_ARCHITECTURE.svg"


@dataclass
class MethodInfo:
    name: str
    start: int
    end: int
    parameters: str


@dataclass
class TypeInfo:
    node_id: str
    name: str
    full_name: str
    kind: str
    file: Path
    relative_file: str
    start: int
    body_start: int
    end: int
    inheritance: list[str]
    purpose: str
    modify_for: str
    methods: list[MethodInfo] = field(default_factory=list)
    internal_calls: list[str] = field(default_factory=list)
    group: str = ""
    x: float = 0
    y: float = 0
    width: float = 1240
    height: float = 240


GROUPS = [
    "Editor tools",
    "Demo startup & replay",
    "Bootstrap & presentation",
    "Runtime controllers",
    "Match & maps",
    "Player simulation",
    "Core contracts",
    "Inference & sensors",
    "Training & tournaments",
    "Research sessions & teachers",
    "Research navigation & arenas",
    "Research telemetry & trials",
    "Recording & UI",
    "Tests",
]

GROUP_CLASS = {
    "Editor tools": "editor",
    "Demo startup & replay": "demo",
    "Bootstrap & presentation": "bootstrap",
    "Runtime controllers": "controller",
    "Match & maps": "match",
    "Player simulation": "player",
    "Core contracts": "core",
    "Inference & sensors": "inference",
    "Training & tournaments": "bootstrap",
    "Research sessions & teachers": "research",
    "Research navigation & arenas": "research",
    "Research telemetry & trials": "research",
    "Recording & UI": "support",
    "Tests": "tests",
}


# Several source comments deliberately retain historical experiment context. These
# overrides describe the current architectural responsibility instead, so the map
# remains useful as a release/refactoring guide.
PURPOSE_OVERRIDES = {
    "GambitPlayModeSceneGuard": "Forces Editor Play mode to start from the release BotArena scene.",
    "GambitReleaseBuilder": "Builds and validates the reproducible Universal macOS release player.",
    "GambitTrajectoryReplayWindow": "Provides the Editor UI for previewing and rendering trajectory replays.",
    "WeaponResetTortureHarness": "Stress-tests player and weapon reset invariants across hostile states.",
    "GameModeBootstrapper": "Composes arenas, players, controllers, camera, HUD, and match services for the selected mode.",
    "MapIndependentSensorComponent": "Installs the actor231 ML-Agents sensor on a player.",
    "MapIndependentSensor": "Publishes cached actor231 observations to ML-Agents.",
    "MapIndependentTelemetry": "Builds the map-independent actor231 navigation observation and memory cues.",
    "OnnxFrameMonitor": "Tracks frame-time performance and writes ONNX capture readiness and summary evidence.",
    "OnnxFrameSummary": "Serializes frame-time statistics for ONNX inference runs.",
    "OnnxPolicyController": "Runs the bundled navigator and combat ONNX policies and converts outputs to PlayerCommand.",
    "BarracudaPolicy": "Loads and executes navigator and combat Barracuda workers with recurrent state and safety handling.",
    "NormalizerPayload": "Deserializes the bundled local45 normalization parameters.",
    "LocalObservationNormalizer": "Normalizes local45 observations before combat inference.",
    "MapIndependentSafetyLayer": "Overrides navigator movement when hidden-target navigation is stuck near obstacles.",
    "OnnxParityRecord": "Serializes actor, local45, and action snapshots for inference parity checks.",
    "HUDManager": "Renders match state, scores, health, heat, and focused-player telemetry.",
    "AutonomousTrainingSession": "Runs long-lived autonomous research sessions, opponent schedules, resets, and audits.",
    "HeadlessTrainingRuntime": "Validates and audits opt-in headless research launches.",
    "LiveTelemetryBridge": "Reads live trial configuration and publishes human-trial telemetry and completion.",
    "RuntimeNavMesh": "Builds and audits optional runtime NavMeshes for research route sensors.",
    "NavMeshRouteTelemetry": "Builds the 32-value route observation using runtime NavMesh queries.",
    "PrivilegedCriticTelemetry": "Builds the critic-only privileged observation without exposing it to the actor.",
    "PrivilegedTeacher": "Generates DAgger navigation labels using privileged world geometry.",
    "ProceduralArenaRuntime": "Loads, builds, validates, and audits opt-in procedural training arenas.",
    "RenderedSmokeRuntime": "Enables the attended rendered research smoke mode under an explicit launch flag.",
    "SpawnBucketController": "Selects deterministic spawn plans satisfying distance, line-of-sight, and bucket constraints.",
    "TrainingRuntimeMetrics": "Buffers per-step training parity metrics and writes a final runtime summary.",
    "TrialTimeoutController": "Restarts or terminates controlled research trials when timeout rules fire.",
    "PolicyInstaller": "Turns a typed PolicySpec into the exact Unity controller and sensor components for one player.",
    "TrainingSessionCoordinator": "Owns supported training episodes, resets, metrics, and stable match-result artifacts.",
    "TournamentRunner": "Expands deterministic matchups and records results while an external host advances matches.",
    "TournamentSchedule": "Expands repetitions and side swaps into reproducible MatchSpec instances.",
    "RunArtifactStore": "Writes JSON and text artifacts beneath one validated run directory.",
    "GambitAgentController": "Adapts ML-Agents actions and local45 telemetry to the shared PlayerCommand interface.",
    "GambitTelemetrySensorComponent": "Installs the frozen local45 ML-Agents sensor.",
    "GambitTelemetrySensor": "Caches and publishes local45 observations to ML-Agents.",
    "HumanController": "Converts keyboard and mouse input into PlayerCommand.",
    "RLAgentController": "Implements the simple 12-observation discrete ML-Agents controller.",
    "ScriptedBotController": "Produces deterministic scripted combat and movement PlayerCommands.",
    "DemoMapRuntime": "Loads, validates, and exposes the selected baked demo map and spawn surfaces.",
    "MatchManager": "Owns authoritative match state, scoring, rewards, round transitions, and reset coordination.",
    "LearnerShootGeometry": "Computes weapon-ray versus hurtbox geometry and optional aim-ray correction.",
    "PlayerBody": "Coordinates a player's controller, motor, weapon, health, match reference, and camera attachment.",
    "PlayerWeapon": "Runs hitscan firing, cooldown and reload state, aim gates, damage, and fire diagnostics.",
    "FireBlockedReason": "Enumerates why a requested shot was blocked.",
    "FireAttemptResult": "Returns the outcome and reason for a fire attempt.",
    "ShotGeometryLogger": "Writes detailed ray and hurtbox shot-geometry diagnostics when enabled.",
    "WeaponStateDebugLogger": "Writes weapon and reset-state diagnostics for blocked-fire investigation.",
    "PolicyContractGoldenVectorTests": "Verifies frozen observation and action contracts against golden vectors and bundled-model metadata.",
}


def mask_noncode(source: str) -> str:
    """Replace comments and string/char contents with spaces, retaining newlines."""
    chars = list(source)
    index = 0
    state = "code"
    verbatim = False
    while index < len(chars):
        ch = chars[index]
        nxt = chars[index + 1] if index + 1 < len(chars) else ""
        if state == "code":
            if ch == "/" and nxt == "/":
                chars[index] = chars[index + 1] = " "
                index += 2
                state = "line"
                continue
            if ch == "/" and nxt == "*":
                chars[index] = chars[index + 1] = " "
                index += 2
                state = "block"
                continue
            if ch == '@' and nxt == '"':
                chars[index] = chars[index + 1] = " "
                index += 2
                state = "string"
                verbatim = True
                continue
            if ch == '"':
                chars[index] = " "
                index += 1
                state = "string"
                verbatim = False
                continue
            if ch == "'":
                chars[index] = " "
                index += 1
                state = "char"
                continue
        elif state == "line":
            if ch == "\n":
                state = "code"
            else:
                chars[index] = " "
            index += 1
            continue
        elif state == "block":
            if ch == "*" and nxt == "/":
                chars[index] = chars[index + 1] = " "
                index += 2
                state = "code"
                continue
            if ch != "\n":
                chars[index] = " "
            index += 1
            continue
        elif state == "string":
            if verbatim and ch == '"' and nxt == '"':
                chars[index] = chars[index + 1] = " "
                index += 2
                continue
            if ch == '"':
                chars[index] = " "
                index += 1
                state = "code"
                continue
            if not verbatim and ch == "\\":
                chars[index] = " "
                if index + 1 < len(chars):
                    if chars[index + 1] != "\n":
                        chars[index + 1] = " "
                    index += 2
                    continue
            if ch != "\n":
                chars[index] = " "
            index += 1
            continue
        elif state == "char":
            if ch == "\\":
                chars[index] = " "
                if index + 1 < len(chars):
                    chars[index + 1] = " "
                    index += 2
                    continue
            if ch == "'":
                chars[index] = " "
                index += 1
                state = "code"
                continue
            if ch != "\n":
                chars[index] = " "
            index += 1
            continue
        index += 1
    return "".join(chars)


def brace_pairs(masked: str) -> tuple[dict[int, int], list[int]]:
    stack: list[int] = []
    pairs: dict[int, int] = {}
    depth: list[int] = [0] * (len(masked) + 1)
    current = 0
    for index, ch in enumerate(masked):
        depth[index] = current
        if ch == "{":
            stack.append(index)
            current += 1
        elif ch == "}":
            current = max(0, current - 1)
            if stack:
                pairs[stack.pop()] = index
    depth[len(masked)] = current
    return pairs, depth


def clean_summary(text: str) -> str:
    text = re.sub(r"<[^>]+>", " ", text)
    text = re.sub(r"\s+", " ", text).strip(" -—.\n\t")
    if not text:
        return ""
    sentence = re.split(r"(?<=[.!?])\s+", text, maxsplit=1)[0]
    return sentence[:180].rstrip() + ("…" if len(sentence) > 180 else "")


def preceding_summary(source: str, start: int) -> str:
    prefix = source[:start]
    lines = prefix.splitlines()
    collected: list[str] = []
    index = len(lines) - 1
    while index >= 0 and (not lines[index].strip() or lines[index].lstrip().startswith("[")):
        index -= 1
    while index >= 0:
        stripped = lines[index].strip()
        if stripped.startswith("///"):
            collected.append(stripped[3:].strip())
            index -= 1
            continue
        break
    collected.reverse()
    return clean_summary(" ".join(collected))


def purpose_fallback(name: str, kind: str) -> str:
    lower = name.lower()
    if kind == "enum":
        return "Defines the allowed named states or modes used by its owning subsystem."
    if kind == "struct":
        return "Carries a compact value or serialized record between collaborating systems."
    if kind == "interface":
        return "Defines the behavior contract implemented by interchangeable runtime components."
    patterns = [
        ("bootstrap", "Composes and starts the objects required for this runtime path."),
        ("controller", "Turns input, policy output, or scripted logic into player commands."),
        ("telemetry", "Builds, stores, or exports observations and runtime measurements."),
        ("sensor", "Exposes a fixed observation stream to ML-Agents or inference code."),
        ("contract", "Defines frozen sizes, indexes, and compatibility rules shared across assemblies."),
        ("manager", "Coordinates lifecycle, state, and events for its subsystem."),
        ("runtime", "Owns opt-in runtime behavior and its launch-time validation."),
        ("replay", "Loads, plays, renders, or exports deterministic trajectory replays."),
        ("trajectory", "Represents or processes trajectory data used by replay tooling."),
        ("weapon", "Implements weapon state, firing, aiming, cooldown, or diagnostics."),
        ("health", "Owns player health, damage, and death transitions."),
        ("motor", "Applies player movement, turning, crouching, and collision response."),
        ("map", "Loads, validates, or represents playable map geometry."),
        ("arena", "Creates or validates arena geometry and per-arena state."),
        ("record", "Captures or serializes runtime evidence and match data."),
        ("hud", "Renders match and player state to the screen."),
        ("overlay", "Renders developer-facing runtime diagnostics."),
        ("policy", "Loads, validates, or executes the bundled learned policy."),
    ]
    for token, description in patterns:
        if token in lower:
            return description
    return "Provides the " + re.sub(r"(?<!^)([A-Z])", r" \1", name).lower() + " responsibility."


def modify_guidance(name: str, relative_file: str, kind: str) -> str:
    lower = name.lower()
    path = relative_file.lower()
    if kind in {"enum", "struct"} or any(word in lower for word in ("row", "payload", "summary", "descriptor", "plan", "config")):
        return "Modify when this serialized/value schema or the data passed between systems changes."
    if "contract" in lower or "layout" in lower:
        return "Modify only for a versioned schema change; retrain or re-export dependent models."
    if "bootstrap" in lower or "startup" in lower or "factory" in lower:
        return "Modify when startup composition, game modes, or component wiring changes."
    if "controller" in lower:
        return "Modify when this actor's decision logic, input mapping, or command output changes."
    if "telemetry" in lower or "sensor" in lower:
        return "Modify when observation fields, measurement cadence, or telemetry export changes."
    if "match" in lower or "trial" in lower:
        return "Modify when match rules, scoring, round transitions, reset, or timeout behavior changes."
    if any(word in lower for word in ("weapon", "shoot", "geometry")):
        return "Modify when firing, aim geometry, cooldown, hit detection, or weapon diagnostics change."
    if any(word in lower for word in ("motor", "body", "identity", "health")):
        return "Modify when player simulation state, movement, damage, or ownership changes."
    if any(word in lower for word in ("map", "arena", "spawn", "navmesh")):
        return "Modify when maps, navigation, spawn constraints, or arena construction changes."
    if any(word in lower for word in ("replay", "trajectory", "exporter")):
        return "Modify when replay input, playback, rendering, or exported evidence changes."
    if "editor" in path or any(word in lower for word in ("window", "builder", "harness", "guard")):
        return "Modify when the corresponding Unity Editor workflow or release QA tooling changes."
    if any(word in lower for word in ("hud", "overlay", "presentation")):
        return "Modify when on-screen presentation, camera focus, or developer diagnostics change."
    if "research" in path:
        return "Modify for research/training experiments; keep release runtime independent of it."
    return "Modify when this subsystem's responsibility or collaboration contract changes."


def classify(relative_file: str, name: str) -> str:
    path = relative_file.replace("\\", "/")
    if path.startswith("Assets/Tests/"):
        return "Tests"
    if path.startswith("Assets/Editor/"):
        return "Editor tools"
    if "/Research/" in path:
        if name in {
            "NavMeshRouteTelemetry", "RuntimeNavMesh", "ProceduralArenaRuntime",
            "SpawnBucketController", "SpawnPlan", "SpawnPointDescriptor",
        }:
            return "Research navigation & arenas"
        if name in {
            "LiveTelemetryBridge", "SpawnTelemetryWriter", "TelemetryLeakageProbe",
            "TrainingRuntimeMetrics", "TrialTimeoutController", "PrivilegedCriticTelemetry",
        }:
            return "Research telemetry & trials"
        return "Research sessions & teachers"
    if "/Training/" in path or "/Tournament/" in path:
        return "Training & tournaments"
    if "/GambitDemo/Replay/" in path or "Trajectory" in name:
        return "Demo startup & replay"
    if "/GambitDemo/" in path:
        return "Demo startup & replay"
    if "/Bootstrap/" in path or "/Presentation/" in path:
        return "Bootstrap & presentation"
    if "/Runtime/Controllers/" in path:
        return "Runtime controllers"
    if "/Runtime/Match/" in path:
        return "Match & maps"
    if "/Runtime/Player/" in path:
        return "Player simulation"
    if "/Runtime/Core/" in path or "/Runtime/Artifacts/" in path:
        return "Core contracts"
    if "/Inference/Controllers/" in path:
        return "Inference & sensors"
    return "Recording & UI"


TYPE_PATTERN = re.compile(
    r"(?m)^[ \t]*(?:(?:public|internal|private|protected|static|sealed|abstract|partial|readonly|new)\s+)*"
    r"(?P<kind>class|struct|interface|enum)\s+(?P<name>[A-Za-z_][A-Za-z0-9_]*)"
    r"(?P<tail>[^\n{]*)\s*\{"
)

METHOD_PATTERN = re.compile(
    r"(?m)^[ \t]*(?:\[[^\]\n]+\][ \t]*)*"
    r"(?P<mods>(?:(?:public|private|protected|internal|static|virtual|override|abstract|sealed|async|extern|new|unsafe|partial)\s+)+)"
    r"(?:(?:[A-Za-z_][A-Za-z0-9_<>,\.\[\]?]*|operator\s+[^\s]+)\s+)?"
    r"(?P<name>[A-Za-z_][A-Za-z0-9_]*)\s*\((?P<params>[^;{}]*)\)\s*(?:where\s+[^\n{=>]+\s*)?(?P<terminator>\{|=>|;)"
)

# Interface members and legal default-private class methods can omit an access
# modifier. Keep this separate so constructors remain handled by METHOD_PATTERN
# without making ordinary call expressions look like declarations.
BARE_METHOD_PATTERN = re.compile(
    r"(?m)^[ \t]*(?:\[[^\]\n]+\][ \t]*)*"
    r"(?:[A-Za-z_][A-Za-z0-9_<>,\.\[\]?]*|\([^\n()]+\))\s+"
    r"(?P<name>[A-Za-z_][A-Za-z0-9_]*)\s*\((?P<params>[^;{}]*)\)\s*"
    r"(?:where\s+[^\n{=>]+\s*)?(?P<terminator>\{|=>|;)"
)


def parse_types() -> tuple[list[TypeInfo], dict[Path, tuple[str, str, dict[int, int], list[int]]]]:
    files = sorted(path for root in SOURCE_ROOTS for path in root.rglob("*.cs"))
    types: list[TypeInfo] = []
    file_data: dict[Path, tuple[str, str, dict[int, int], list[int]]] = {}
    serial = 0
    for file in files:
        source = file.read_text(encoding="utf-8")
        masked = mask_noncode(source)
        pairs, depth = brace_pairs(masked)
        file_data[file] = (source, masked, pairs, depth)
        raw: list[tuple[re.Match[str], int, int]] = []
        for match in TYPE_PATTERN.finditer(masked):
            opening = masked.find("{", match.start(), match.end())
            closing = pairs.get(opening)
            if closing is None:
                continue
            raw.append((match, opening, closing))
        for match, opening, closing in raw:
            parents = [item for item in raw if item[1] < opening < item[2]]
            parent = min(parents, key=lambda item: item[2] - item[1]) if parents else None
            name = match.group("name")
            parent_name = parent[0].group("name") if parent else ""
            full_name = parent_name + "." + name if parent_name else name
            tail = match.group("tail")
            inheritance: list[str] = []
            if ":" in tail:
                inheritance = [
                    re.sub(r"<.*", "", token).strip()
                    for token in tail.split(":", 1)[1].split(",")
                    if token.strip()
                ]
            relative = file.relative_to(ROOT).as_posix()
            purpose = (
                PURPOSE_OVERRIDES.get(full_name)
                or PURPOSE_OVERRIDES.get(name)
                or preceding_summary(source, match.start())
                or purpose_fallback(name, match.group("kind"))
            )
            info = TypeInfo(
                node_id=f"n{serial}",
                name=name,
                full_name=full_name,
                kind=match.group("kind"),
                file=file,
                relative_file=relative,
                start=match.start(),
                body_start=opening,
                end=closing,
                inheritance=inheritance,
                purpose=purpose,
                modify_for=modify_guidance(name, relative, match.group("kind")),
            )
            info.group = classify(relative, name)
            types.append(info)
            serial += 1

    for info in types:
        source, masked, pairs, depth = file_data[info.file]
        expected_depth = depth[info.body_start] + 1
        candidates = list(METHOD_PATTERN.finditer(masked, info.body_start + 1, info.end))
        candidates.extend(BARE_METHOD_PATTERN.finditer(masked, info.body_start + 1, info.end))
        for match in sorted(candidates, key=lambda item: item.start()):
            if depth[match.start()] != expected_depth:
                continue
            name = match.group("name")
            if name in {"if", "for", "foreach", "while", "switch", "catch", "using", "lock"}:
                continue
            terminator = match.group("terminator")
            method_end = match.end()
            if terminator == "{":
                opening = masked.rfind("{", match.start(), match.end())
                method_end = pairs.get(opening, method_end)
            elif terminator == "=>":
                semicolon = masked.find(";", match.end(), info.end)
                method_end = semicolon if semicolon >= 0 else match.end()
            info.methods.append(MethodInfo(name, match.start(), method_end, match.group("params")))
        unique: dict[tuple[str, int], MethodInfo] = {}
        for method in info.methods:
            unique[(method.name, method.start)] = method
        info.methods = sorted(unique.values(), key=lambda method: method.start)
    return types, file_data


def resolve_type(name: str, source: TypeInfo, by_name: dict[str, list[TypeInfo]]) -> TypeInfo | None:
    name = re.sub(r"<.*", "", name).replace("[]", "").replace("?", "").strip()
    candidates = by_name.get(name, [])
    if not candidates:
        return None
    same_file = [candidate for candidate in candidates if candidate.file == source.file]
    if len(same_file) == 1:
        return same_file[0]
    return candidates[0] if len(candidates) == 1 else None


def build_edges(types: list[TypeInfo], file_data):
    by_name: dict[str, list[TypeInfo]] = defaultdict(list)
    for info in types:
        by_name[info.name].append(info)

    edge_labels: dict[tuple[str, str, str], set[str]] = defaultdict(set)

    def add(source: TypeInfo, target: TypeInfo | None, kind: str, label: str):
        if target is None or source.node_id == target.node_id:
            return
        labels = edge_labels[(source.node_id, target.node_id, kind)]
        if len(labels) < 5:
            labels.add(label)

    for info in types:
        source, masked, _, _ = file_data[info.file]
        body = masked[info.body_start + 1 : info.end]
        for base in info.inheritance:
            add(info, resolve_type(base, info, by_name), "inherits", "inherits/implements")

        type_names = sorted(by_name, key=len, reverse=True)
        type_union = "|".join(re.escape(name) for name in type_names)
        variable_types: dict[str, str] = {}
        declaration_pattern = re.compile(
            rf"\b(?P<type>{type_union})(?:\s*<[^;=()]+>)?(?:\[\])?\s+(?P<var>[a-zA-Z_][A-Za-z0-9_]*)"
        )
        for declaration in declaration_pattern.finditer(body):
            variable_types[declaration.group("var")] = declaration.group("type")

        method_names = {method.name for method in info.methods}
        internal_pairs: set[str] = set()
        for method in info.methods:
            method_body = masked[method.start : min(method.end + 1, info.end)]
            caller = method.name

            for target_name in method_names:
                if target_name == caller:
                    continue
                if re.search(rf"(?<![.\w]){re.escape(target_name)}\s*\(", method_body):
                    internal_pairs.add(caller + "() → " + target_name + "()")

            for direct in re.finditer(rf"\b(?P<type>{type_union})\s*\.\s*(?P<method>[A-Za-z_]\w*)\s*\(", method_body):
                target = resolve_type(direct.group("type"), info, by_name)
                add(info, target, "call", caller + "() → " + direct.group("method") + "()")

            for call in re.finditer(r"\b(?P<var>[a-zA-Z_]\w*)\s*\.\s*(?P<method>[A-Za-z_]\w*)\s*\(", method_body):
                target_name = variable_types.get(call.group("var"))
                if target_name:
                    add(info, resolve_type(target_name, info, by_name), "call",
                        caller + "() → " + call.group("method") + "()")

            for generic in re.finditer(
                rf"\b(?P<op>AddComponent|GetComponent|GetComponentInParent|GetComponentsInChildren|FindObjectOfType|FindObjectsOfType)\s*<(?P<type>{type_union})>",
                method_body,
            ):
                kind = "construct" if generic.group("op") == "AddComponent" else "component"
                add(info, resolve_type(generic.group("type"), info, by_name), kind,
                    caller + "() → " + generic.group("op"))

            for creation in re.finditer(rf"\bnew\s+(?P<type>{type_union})\b", method_body):
                add(info, resolve_type(creation.group("type"), info, by_name), "construct",
                    caller + "() → new")

        info.internal_calls = sorted(internal_pairs)

        referenced_names = set(re.findall(rf"\b({type_union})\b", body)) if type_union else set()
        existing_targets = {target for source_id, target, _ in edge_labels if source_id == info.node_id}
        for referenced in referenced_names:
            target = resolve_type(referenced, info, by_name)
            if target and target.node_id not in existing_targets and target.node_id != info.node_id:
                add(info, target, "uses", "type/reference")

    return edge_labels


def wrap(text: str, width: int) -> list[str]:
    words = text.split()
    lines: list[str] = []
    current = ""
    for word in words:
        proposed = word if not current else current + " " + word
        if len(proposed) <= width:
            current = proposed
        else:
            if current:
                lines.append(current)
            current = word
    if current:
        lines.append(current)
    return lines or [""]


def method_lines(methods: list[MethodInfo]) -> list[str]:
    labels = [method.name + "()" for method in methods]
    lines: list[str] = []
    current = ""
    for label in labels:
        proposed = label if not current else current + " · " + label
        if len(proposed) <= 105:
            current = proposed
        else:
            lines.append(current)
            current = label
    if current:
        lines.append(current)
    return lines or ["(no methods declared)"]


def layout(types: list[TypeInfo]):
    by_group: dict[str, list[TypeInfo]] = defaultdict(list)
    for info in types:
        by_group[info.group].append(info)
    column_gap = 1500
    top = 310
    for column, group in enumerate(GROUPS):
        y = top
        items = sorted(by_group[group], key=lambda item: (item.relative_file, item.start))
        for info in items:
            purpose_rows = wrap(info.purpose, 105)
            modify_rows = wrap(info.modify_for, 105)
            methods = method_lines(info.methods)
            calls = []
            for relation in info.internal_calls:
                calls.extend(wrap(relation, 105))
            base_rows = 7 + len(purpose_rows) + len(modify_rows) + len(methods) + len(calls)
            info.height = max(250, 38 + base_rows * 22)
            info.x = 180 + column * column_gap
            info.y = y
            y += info.height + 72
    width = 180 + len(GROUPS) * column_gap
    height = max((info.y + info.height for info in types), default=1000) + 220
    return width, height


def svg_text(x, y, text, css_class, anchor="start"):
    return (
        f'<text x="{x:.1f}" y="{y:.1f}" class="{css_class}" text-anchor="{anchor}">'
        + html.escape(text)
        + "</text>"
    )


def render(types: list[TypeInfo], edge_labels, width: float, height: float) -> str:
    by_id = {info.node_id: info for info in types}
    outgoing: dict[str, set[str]] = defaultdict(set)
    incoming: dict[str, set[str]] = defaultdict(set)
    for source, target, _ in edge_labels:
        outgoing[source].add(target)
        incoming[target].add(source)

    parts = [
        '<?xml version="1.0" encoding="UTF-8"?>',
        f'<svg xmlns="http://www.w3.org/2000/svg" xmlns:xlink="http://www.w3.org/1999/xlink" viewBox="0 0 {width:.0f} {height:.0f}" width="{width:.0f}" height="{height:.0f}" role="img" aria-labelledby="title desc">',
        '<title id="title">GAMBIT Unity project architecture and dependency graph</title>',
        '<desc id="desc">Every declared C# type grouped by subsystem, with methods, internal calls, purposes, modification guidance, and cross-type dependencies.</desc>',
        """<style>
        :root { --bg:#071018; --fg:#e6edf3; --muted:#9fb0bf; --line:#658096; --node:#101d29; --border:#385166;
          --editor:#a78bfa; --demo:#f59e0b; --bootstrap:#38bdf8; --controller:#22c55e; --match:#fb7185;
          --player:#14b8a6; --core:#c084fc; --inference:#60a5fa; --research:#f97316; --support:#94a3b8; --tests:#ec4899;
          --call:#5aa7ff; --construct:#4ade80; --component:#fbbf24; --inherits:#c084fc; --uses:#71879a; }
        @media (prefers-color-scheme: light) { :root { --bg:#f8fafc; --fg:#10202d; --muted:#526778; --line:#7d93a5; --node:#ffffff; --border:#aec0ce;
          --editor:#6d28d9; --demo:#b45309; --bootstrap:#0369a1; --controller:#15803d; --match:#be123c;
          --player:#0f766e; --core:#7e22ce; --inference:#1d4ed8; --research:#c2410c; --support:#475569; --tests:#be185d;
          --call:#2563eb; --construct:#15803d; --component:#b45309; --inherits:#7e22ce; --uses:#64748b; } }
        .canvas { fill:var(--bg); }
        .heading { fill:var(--fg); font:500 36px system-ui,sans-serif; }
        .subheading { fill:var(--muted); font:400 18px system-ui,sans-serif; }
        .group-title { fill:var(--fg); font:500 22px system-ui,sans-serif; }
        .group-rule { stroke:var(--border); stroke-width:2; }
        .node rect { fill:var(--node); stroke:var(--border); stroke-width:2; rx:12; }
        .node .accent { stroke:none; }
        .node text { pointer-events:none; }
        .node-title { fill:var(--fg); font:500 19px ui-monospace,SFMono-Regular,Menlo,monospace; }
        .kind { fill:var(--muted); font:400 14px system-ui,sans-serif; }
        .file { fill:var(--muted); font:400 13px ui-monospace,SFMono-Regular,Menlo,monospace; }
        .label { fill:var(--muted); font:500 13px system-ui,sans-serif; }
        .body { fill:var(--fg); font:400 14px system-ui,sans-serif; }
        .methods { fill:var(--fg); font:400 13px ui-monospace,SFMono-Regular,Menlo,monospace; }
        .internal { fill:var(--muted); font:400 12px ui-monospace,SFMono-Regular,Menlo,monospace; }
        .edge { fill:none; stroke-width:2; opacity:.18; transition:opacity .12s,stroke-width .12s; }
        .edge.call { stroke:var(--call); marker-end:url(#arrow-call); }
        .edge.construct { stroke:var(--construct); marker-end:url(#arrow-construct); }
        .edge.component { stroke:var(--component); marker-end:url(#arrow-component); }
        .edge.inherits { stroke:var(--inherits); marker-end:url(#arrow-inherits); }
        .edge.uses { stroke:var(--uses); marker-end:url(#arrow-uses); stroke-dasharray:8 7; }
        .edge-label { fill:var(--muted); font:400 11px ui-monospace,SFMono-Regular,Menlo,monospace; opacity:0; pointer-events:none; }
        .node.dim { opacity:.15; }
        .node.active rect { stroke-width:5; }
        .edge.dim { opacity:.025; }
        .edge.active { opacity:.95; stroke-width:4; }
        .edge-label.active { opacity:1; }
        .legend-text { fill:var(--fg); font:400 14px system-ui,sans-serif; }
        .legend-muted { fill:var(--muted); font:400 13px system-ui,sans-serif; }
        </style>""",
        """<defs>
          <marker id="arrow-call" markerWidth="10" markerHeight="10" refX="9" refY="3" orient="auto" markerUnits="strokeWidth"><path d="M0,0 L0,6 L9,3 z" fill="var(--call)"/></marker>
          <marker id="arrow-construct" markerWidth="10" markerHeight="10" refX="9" refY="3" orient="auto" markerUnits="strokeWidth"><path d="M0,0 L0,6 L9,3 z" fill="var(--construct)"/></marker>
          <marker id="arrow-component" markerWidth="10" markerHeight="10" refX="9" refY="3" orient="auto" markerUnits="strokeWidth"><path d="M0,0 L0,6 L9,3 z" fill="var(--component)"/></marker>
          <marker id="arrow-inherits" markerWidth="10" markerHeight="10" refX="9" refY="3" orient="auto" markerUnits="strokeWidth"><path d="M0,0 L0,6 L9,3 z" fill="var(--inherits)"/></marker>
          <marker id="arrow-uses" markerWidth="10" markerHeight="10" refX="9" refY="3" orient="auto" markerUnits="strokeWidth"><path d="M0,0 L0,6 L9,3 z" fill="var(--uses)"/></marker>
        </defs>""",
        f'<rect class="canvas" width="{width:.0f}" height="{height:.0f}"/>',
        svg_text(180, 68, "GAMBIT Unity architecture — types, methods, calls, and change map", "heading"),
        svg_text(180, 102, f"Generated from {len(set(info.file for info in types))} C# files · {len(types)} types · {sum(len(info.methods) for info in types)} methods · {len(edge_labels)} dependency edges", "subheading"),
        svg_text(180, 134, "Click a class to isolate callers and callees. Click empty space to reset. Open in a browser and zoom freely.", "subheading"),
    ]

    legend = [("call", "method call"), ("construct", "constructs/adds"), ("component", "gets/finds component"), ("inherits", "inherits/implements"), ("uses", "type/reference")]
    lx = 180
    for kind, label in legend:
        color = f"var(--{kind})"
        parts.append(f'<line x1="{lx}" y1="174" x2="{lx + 64}" y2="174" stroke="{color}" stroke-width="4"/>')
        parts.append(svg_text(lx + 76, 180, label, "legend-text"))
        lx += 270

    for column, group in enumerate(GROUPS):
        x = 180 + column * 1500
        parts.append(svg_text(x, 250, group, "group-title"))
        parts.append(f'<line class="group-rule" x1="{x}" y1="270" x2="{x + 1240}" y2="270"/>')

    edge_index = 0
    for (source_id, target_id, kind), labels in sorted(edge_labels.items()):
        source = by_id[source_id]
        target = by_id[target_id]
        forward = target.x >= source.x
        sx = source.x + source.width if forward else source.x
        tx = target.x if forward else target.x + target.width
        sy = source.y + min(source.height - 35, 78 + (edge_index % 7) * 19)
        ty = target.y + min(target.height - 35, 78 + ((edge_index * 3) % 7) * 19)
        bend = max(120, abs(tx - sx) * 0.46)
        c1 = sx + bend if forward else sx - bend
        c2 = tx - bend if forward else tx + bend
        path_id = f"e{edge_index}"
        label = " · ".join(sorted(labels))
        parts.append(
            f'<path id="{path_id}" class="edge {kind}" data-source="{source_id}" data-target="{target_id}" d="M {sx:.1f},{sy:.1f} C {c1:.1f},{sy:.1f} {c2:.1f},{ty:.1f} {tx:.1f},{ty:.1f}"><title>{html.escape(source.full_name + " → " + target.full_name + ": " + label)}</title></path>'
        )
        mx = (sx + tx) / 2
        my = (sy + ty) / 2
        parts.append(f'<text class="edge-label" data-edge="{path_id}" x="{mx:.1f}" y="{my:.1f}">{html.escape(label)}</text>')
        edge_index += 1

    for info in types:
        css = GROUP_CLASS[info.group]
        linked = sorted(outgoing[info.node_id] | incoming[info.node_id])
        link_data = " ".join(linked)
        title = info.full_name + " — " + info.purpose + " Modify for: " + info.modify_for
        parts.append(f'<g id="{info.node_id}" class="node" data-links="{link_data}" transform="translate({info.x:.1f},{info.y:.1f})"><title>{html.escape(title)}</title>')
        parts.append(f'<rect width="{info.width:.1f}" height="{info.height:.1f}"/>')
        parts.append(f'<rect class="accent" width="10" height="{info.height:.1f}" rx="5" fill="var(--{css})"/>')
        parts.append(svg_text(28, 34, info.full_name, "node-title"))
        parts.append(svg_text(info.width - 24, 34, info.kind, "kind", "end"))
        parts.append(f'<a xlink:href="../{html.escape(info.relative_file)}">{svg_text(28, 58, info.relative_file, "file")}</a>')
        y = 88
        parts.append(svg_text(28, y, "DOES", "label")); y += 22
        for line in wrap(info.purpose, 105):
            parts.append(svg_text(28, y, line, "body")); y += 20
        y += 5
        parts.append(svg_text(28, y, "MODIFY FOR", "label")); y += 22
        for line in wrap(info.modify_for, 105):
            parts.append(svg_text(28, y, line, "body")); y += 20
        y += 5
        parts.append(svg_text(28, y, f"METHODS ({len(info.methods)})", "label")); y += 22
        for line in method_lines(info.methods):
            parts.append(svg_text(28, y, line, "methods")); y += 19
        if info.internal_calls:
            y += 5
            parts.append(svg_text(28, y, f"INTERNAL CALLS ({len(info.internal_calls)})", "label")); y += 20
            for relation in info.internal_calls:
                for line in wrap(relation, 105):
                    parts.append(svg_text(28, y, line, "internal")); y += 18
        parts.append("</g>")

    parts.append("""<script><![CDATA[
      const root = document.documentElement;
      const nodes = [...root.querySelectorAll('.node')];
      const edges = [...root.querySelectorAll('.edge')];
      const labels = [...root.querySelectorAll('.edge-label')];
      function resetGraph() {
        nodes.forEach(n => n.classList.remove('active','dim'));
        edges.forEach(e => e.classList.remove('active','dim'));
        labels.forEach(l => l.classList.remove('active'));
      }
      nodes.forEach(node => node.addEventListener('click', event => {
        event.stopPropagation();
        const id = node.id;
        const linked = new Set((node.dataset.links || '').split(' ').filter(Boolean));
        linked.add(id);
        nodes.forEach(n => { n.classList.toggle('active', linked.has(n.id)); n.classList.toggle('dim', !linked.has(n.id)); });
        edges.forEach(e => {
          const active = e.dataset.source === id || e.dataset.target === id;
          e.classList.toggle('active', active); e.classList.toggle('dim', !active);
          const label = root.querySelector('[data-edge="' + e.id + '"]');
          if (label) label.classList.toggle('active', active);
        });
      }));
      root.addEventListener('click', resetGraph);
    ]]></script>""")
    parts.append("</svg>")
    return "\n".join(parts)


def main():
    types, file_data = parse_types()
    edges = build_edges(types, file_data)
    width, height = layout(types)
    OUTPUT.parent.mkdir(parents=True, exist_ok=True)
    OUTPUT.write_text(render(types, edges, width, height), encoding="utf-8")
    print(f"wrote {OUTPUT}")
    print(f"files={len(set(info.file for info in types))} types={len(types)} methods={sum(len(info.methods) for info in types)} edges={len(edges)} size={OUTPUT.stat().st_size}")


if __name__ == "__main__":
    main()
