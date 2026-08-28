# GAMBIT DEMO

Standalone Unity 2022.3 LTS 1v1 arena demo.

A significant portion of this code and documentation was generated with assistance from ChatGPT, building on my earlier Unity research code for simple gameplay PPO experiments and data rendering.

Open this folder in Unity Hub, open `Assets/Scenes/BotArena.unity`, and press
Play. The build begins at **GAMBIT DEMO** with **Start** and **Settings**.
Settings selects game mode, map (Ascent, Breeze, or Bind), scripted bot
behaviours, HUD visibility, enemy-distance heat bar, and target frame rate.
Standard RL modes run the bundled ONNX policy locally. Only modes marked
**Training** expose live Unity ML-Agents and require an external trainer.

## Minimal local45 PPO smoke

The smoke trains one `phase3v2_c_local45` `GambitAgent` against the
`FaceOpponentAndShoot` scripted bot. The bundled frozen actor231 navigator
supplies map navigation while PPO learns the local45 combat action. It is
intentionally small and isolated from other training pipelines.

First-time setup:

```sh
Tools/setup_local45_ppo.sh
```

Start the trainer:

```sh
Tools/run_local45_ppo_smoke.sh
```

When the trainer says it is listening, use **GAMBIT > Training > Start
Training...** in Unity and select `Training/local45_vs_scripted.training.json`.
The editor enters Play Mode and bypasses the interactive start screen. Unity
treats it as a normal training session; the external YAML and shell command
make this particular run a short smoke test. Stop Play Mode after the trainer
reaches 10,000 steps. Trainer checkpoints and summaries are written under
`Training/results/`.

## Gen3 unified policy integration

`Training/gen3_unified_vs_scripted.training.json` selects a separate full
policy that consumes the causal `gen3_champion_v2_token_v001` observation and
directly owns all eight final actions, including vertical look. The imported
contract runs at 30 Hz and does not compose navigator and combat outputs.

The isolated `07_GAMBIT-Gen3-V2-Transfer` project contains no qualified Gen3
ONNX model: its V005 protected evaluation failed and export was not performed.
This repository therefore includes the runtime/training contract and an ONNX
loader that fails clearly when the model resource is absent, but it does not
claim that a deployable Gen3 checkpoint exists or start Gen3 PPO.

## Source layout

* `Assets/Scripts/Runtime/` — reusable gameplay, player, match, controller,
  recording, and runtime UI code (`Gambit.Runtime`).
* `Assets/Scripts/Inference/` — bundled ONNX inference and interactive demo
  composition (`Gambit.Inference`).
* `Assets/Scripts/Research/` — optional legacy training and evaluation tools,
  compiled only with `GAMBIT_RESEARCH` (`Gambit.Research`).
* `Assets/Editor/` — Unity Editor-only build, replay, QA, and Play Mode tools
  (`Gambit.Editor`).

The frozen `local45`, `actor231`, and eight-value action layouts are documented
in [`OBSERVATION_CONTRACTS.md`](OBSERVATION_CONTRACTS.md). Persisted schema IDs
are compatibility identifiers and must change if a layout changes.

For a zoomable project-wide map of types, methods, calls, dependencies, and
change guidance, open [`Documentation/GAMBIT_ARCHITECTURE.svg`](Documentation/GAMBIT_ARCHITECTURE.svg)
in a browser. Regenerate it after source changes with
`python3 Tools/generate_architecture_graph.py`.

## Bundled runtime assets

* `Assets/Maps/` — source map meshes used by the scene and provenance registry.
* `Assets/Resources/Maps/` — build-included Breeze and Bind map prefabs.
* `Assets/Resources/MLModels/` — frozen ONNX navigator/combat policies and
  their normalizer, used by the integrated RL bot controller.

The interactive demo takes settings from `GambitDemoRuntimeSettings`; it does
not use environment variables for game mode, bot behaviour, map selection,
HUD, or frame rate. Legacy experiment systems remain source-only and
are disabled unless explicitly launched through their legacy contracts.

## Known trajectory replay

Use **GAMBIT > Replay Trajectory...** in the Unity editor to choose a
trajectory JSON, map, and camera. **Preview** opens the arena interactively;
press Space to cycle Player, Observer, and Top Down cameras. **Render WebM**
creates `trajectory.webm`, frame-aligned `telemetry.json`, and a provenance
`manifest.json` containing hashes of the input trajectory and active map.

The accepted JSON schema is `gambit_trajectory_v1`: a `duration_seconds`, one
player with time/value series for position and rotation (optional FOV), plus
optional targets and events. The importer also accepts the existing Data
Pipeline `play_analytics.json` fields and ignores unrelated analytics fields.

Batch rendering uses the same deterministic replay path:

```sh
/Applications/Unity/Hub/Editor/2022.3.62f3/Unity.app/Contents/MacOS/Unity \
  -projectPath /absolute/path/to/01_GAMBIT-Demo -batchmode \
  -executeMethod GambitTrajectoryReplayBootstrap.ExecuteReplay \
  --gambit-replay /absolute/path/to/trajectory.json \
  --gambit-map arena_ascent_v1 --gambit-camera Observer \
  --gambit-output /absolute/path/to/render --gambit-export
```

Optional render controls are `--gambit-fps`, `--gambit-width`,
`--gambit-height`, and `--gambit-speed`.

## Build and QA

`BotArena.unity` is the sole enabled player scene. Use **GAMBIT > Build >
macOS Native (Universal)** to create an application containing native ARM64
and Intel executables; Apple-Silicon Macs do not require Rosetta. Automated
builds may call `GambitReleaseBuilder.BuildMacUniversal`; pass
`--gambit-build-output` to override the default path beneath `Builds/`.

Run **GAMBIT > QA > Run Weapon Reset Stress Test** before a release. The same
test is available to batch Unity through
`-executeMethod WeaponResetTortureHarness.Run`.

Policy contract and bundled-model parity tests can be run in EditMode:

```sh
/Applications/Unity/Hub/Editor/2022.3.62f3/Unity.app/Contents/MacOS/Unity \
  -projectPath /absolute/path/to/01_GAMBIT-Demo -batchmode -nographics \
  -runTests -testPlatform editmode \
  -testFilter PolicyContractGoldenVectorTests \
  -testResults /tmp/gambit-contract-tests.xml
```
