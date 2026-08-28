# Three-policy PPO presentation demo

This folder runs three small PPO demonstrations against the same
`FaceOpponentAndShoot` scripted opponent on Ascent. Every run stops at 1,000
ML-Agents steps and writes its own checkpoint, trainer log, and TensorBoard
summary beneath `demo/results/`.

These are integration demonstrations. One thousand steps is enough to show
Unity/Python communication, observation and action shapes, reward delivery,
optimization, and ONNX export. It is not enough to compare policy quality or
claim convergence.

## What the three runs demonstrate

| Run | Policy observation | Final action ownership | PPO action space |
|---|---|---|---|
| 1. local45 | 45-value local combat telemetry | PPO owns all actions | 4 continuous plus 4 binary branches |
| 2. local45 + actor231 | PPO sees local45; frozen navigator sees actor231 | Navigator owns movement and hidden turning; PPO owns visible aim/combat | 2 continuous plus 4 binary branches |
| 3. Gen3 unified | 232-value causal token derived from the 217-value fair observation | One PPO policy owns all eight actions | 4 continuous plus 4 binary branches, recurrent memory |

All three use the same map, opponent, health, damage, cooldown, episode
timeout, PPO learning rate, network width, and 1,000-step budget. Their rewards
are still not scientifically comparable because their observations and action
authority differ.

## One-time setup

Run these commands on this Mac from the project root:

```sh
cd /Users/amitsonashree/Projects/Unity/all_unity_projects/01_GAMBIT-Demo
Tools/setup_local45_ppo.sh
```

This creates the project-local `.venv-mlagents20` environment with the Python
ML-Agents version matched to the Unity package. It does not modify the system
Python installation.

Before presenting, open `Assets/Scenes/BotArena.unity` in Unity 2022.3.62f3
and wait for script compilation to finish.

## Recommended presentation workflow: Unity Editor

Use one terminal and Unity. Run the experiments individually so the audience
can see which configuration is selected.

### Run 1: local45 alone

```sh
demo/run_editor.sh 1
```

When the terminal says `Waiting for Unity`:

1. In Unity choose **GAMBIT > Training > Start Training...**.
2. Select `demo/launch/01_local45.training.json`.
3. Unity enters Play Mode and connects to the trainer.
4. Wait for the terminal to print `Step: 1000` and export the ONNX file.
5. Stop Play Mode before starting the next run.

This learner sees only local45 and directly controls movement, yaw, pitch,
shoot, reload, jump, and crouch.

### Run 2: local45 with frozen actor231 navigation

```sh
demo/run_editor.sh 2
```

Select `demo/launch/02_local45_actor231.training.json` in Unity. The Console
should contain:

```text
ML-Agents Local45NavigatorAgent + frozen actor231 navigator
```

The navigator owns movement and hidden-target turning. PPO receives only the
two continuous aim outputs plus the four binary branches, so it is not trained
on discarded movement outputs.

Wait for `Step: 1000`, then stop Play Mode.

### Run 3: Gen3 unified full policy

```sh
demo/run_editor.sh 3
```

Select `demo/launch/03_gen3_unified.training.json` in Unity. The Console should
contain:

```text
unified policy decision clock=30 Hz
ML-Agents Gen3UnifiedAgent unified direct-owner
```

The policy receives one 232-value causal token per decision. Its PPO network
uses a 32-step recurrent sequence and directly controls every action,
including vertical look. No frozen navigator, combat expert, or safety
compositor overwrites the PPO output.

Wait for `Step: 1000`, then stop Play Mode.

## Automated workflow using a built player

For a rehearsal or unattended mechanical check, first build the player in
Unity with **GAMBIT > Build > macOS Native (Universal)**. The default output is:

```text
Builds/macOS-Universal/GAMBIT Demo.app
```

Then run all three sequentially:

```sh
demo/run_all.sh
```

If the application is elsewhere, pass its absolute path:

```sh
demo/run_all.sh "/absolute/path/to/GAMBIT Demo.app"
```

The automated workflow launches a fresh player for each experiment and closes
it when that trainer finishes. It assigns a separate ML-Agents communication
port to each run so macOS socket cleanup cannot interfere with the next one.

## Expected output

Each run creates a timestamped directory such as:

```text
demo/results/presentation-local45-YYYYMMDD-HHMMSS/
demo/results/presentation-local45-actor231-YYYYMMDD-HHMMSS/
demo/results/presentation-gen3-unified-YYYYMMDD-HHMMSS/
```

Inside each directory:

- `<BehaviorName>.onnx` is the exported 1,000-step policy.
- `<BehaviorName>/checkpoint.pt` is the PyTorch checkpoint.
- `events.out.tfevents...` contains TensorBoard summaries.
- `run_logs/Player-0.log` contains Unity initialization and runtime logs.
- `configuration.yaml` is the exact resolved trainer configuration.

The YAML limit is exactly `max_steps: 1000`, and the trainer prints
`Step: 1000` for every run. ML-Agents finishes the active rollout before its
final save, so checkpoint filenames can carry a slightly larger suffix such
as `-1031.onnx`; that does not mean the configured PPO budget was changed.

The recurrent Gen3 export may print PyTorch ONNX shape-inference warnings.
They are expected for this short ML-Agents LSTM export and do not fail the
demo. The exported file is an ML-Agents presentation checkpoint; it is not the
missing qualified 32-token static-window Gen3 deployment model expected by
`Gen3UnifiedOnnxController`.

Inspect summaries with:

```sh
.venv-mlagents20/bin/tensorboard --logdir demo/results
```

Then open the URL printed by TensorBoard.

## Presentation interpretation

- A successful run proves the selected observation/action contract connects to
  PPO and reaches optimization/export.
- Mean reward after only 1,000 steps is noisy and is not a ranking.
- The local45 + actor231 run demonstrates explicit modular ownership.
- The Gen3 run demonstrates a single unified action owner and retained pitch.
- The Gen3 run is a new presentation smoke run. It does not repair, overwrite,
  or invalidate the failed protected V005 transfer evaluation from the `07`
  workspace, and its checkpoint is not a qualified production model.

## Troubleshooting

If the trainer remains on `Listening on port 5004`, Unity has not connected.
Check that Play Mode is active and that the selected JSON matches the current
terminal command.

If Unity reports a behavior-name mismatch, stop Play Mode and verify the pair:

| Command | Required launch JSON |
|---|---|
| `demo/run_editor.sh 1` | `01_local45.training.json` |
| `demo/run_editor.sh 2` | `02_local45_actor231.training.json` |
| `demo/run_editor.sh 3` | `03_gen3_unified.training.json` |

Do not use `--resume` for the presentation runs. Every command creates a new
timestamped namespace so previous evidence remains untouched.

The verified rehearsal results are summarized in [`RUN_RESULTS.md`](RUN_RESULTS.md).
