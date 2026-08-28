# Verified 1,000-step rehearsal

Date: 2026-08-25
Unity: 2022.3.62f3
Unity ML-Agents package: 2.3.0-exp.3
Python ML-Agents: 0.30.0
Communicator API: 1.5.0

The command below completed all three experiments against the same validated
Universal macOS player:

```sh
demo/run_all.sh "/tmp/gambit-gen3-unified-validation.Tdmp0y/build-final/GAMBIT Unified.app"
```

For the presentation, use the normal build path described in `README.md`; the
`/tmp` path above is only the isolated verification build used for this
rehearsal.

## Results

| Run | Trainer reached | Last printed mean reward | Wall time at step 1,000 | Final checkpoint suffix |
|---|---:|---:|---:|---:|
| local45 standalone | 1,000 | -1.901 | 2.694 s | 1,047 |
| local45 + actor231 | 1,000 | -1.938 | 3.340 s | 1,055 |
| Gen3 unified recurrent | 1,000 | -1.848 | 3.314 s | 1,031 |

The checkpoint suffix is slightly above 1,000 because ML-Agents flushes its
active rollout before the final save. Every YAML file has `max_steps: 1000`,
and every trainer printed `Step: 1000`.

These reward values are smoke evidence, not a quality ranking. The policies
have different observation spaces and action authority, and 1,000 steps is far
too short for convergence.

## Verified Unity controller paths

The player logs confirmed:

```text
ML-Agents Local45StandaloneAgent
ML-Agents Local45NavigatorAgent + frozen actor231 navigator
unified policy decision clock=30 Hz
ML-Agents Gen3UnifiedAgent unified direct-owner
```

No Unity exceptions, tensor mismatches, null references, or policy hard stops
were found in the three successful player logs.

## Result directories

```text
demo/results/presentation-local45-20260825-221621/
demo/results/presentation-local45-actor231-20260825-221621/
demo/results/presentation-gen3-unified-20260825-221621/
```

Each contains an exported ONNX policy, PyTorch checkpoint, TensorBoard event,
resolved configuration, timer/status files, and Unity player log.
