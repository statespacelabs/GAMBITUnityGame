# GAMBIT platform architecture

GAMBIT is one Unity simulation with interchangeable inputs and orchestration.
Demo, training, tournament, and replay are launch reasons—not separate games.

```text
GambitLaunchConfig
  MatchSpec
    MapId + Seed + MatchRulesSpec
    PlayerA: PolicySpec
    PlayerB: PolicySpec
    ExecutionProfile
  TrainingSpec?       -> TrainingSessionCoordinator
  TournamentSpec?     -> TournamentRunner
  OutputSpec          -> RunArtifactStore -> JSON artifacts
```

## Assembly boundaries

| Assembly | Stable responsibility | May depend on |
|---|---|---|
| `Gambit.Runtime` | deterministic players, combat, maps, match state, contracts, artifact I/O | Unity, ML-Agents contracts |
| `Gambit.Training` | supported episode lifecycle and result capture | Runtime |
| `Gambit.Tournament` | matchup expansion, side swaps, result ledger | Runtime |
| `Gambit.Inference` | composition root, UI, human/scripted/ONNX/ML-Agents policy installation | Runtime, Training, Tournament |
| `Gambit.Research` | privileged teachers, probes, procedural experiments, experiment audits | stable assemblies; only with `GAMBIT_RESEARCH` |

External Python owns PPO, PSRO, population management, checkpoint selection,
and large experiment databases. Unity receives a versioned JSON launch spec and
emits versioned per-match results. This keeps game simulation authoritative and
prevents algorithm experiments from accumulating inside `MatchManager`.

## Where to make changes

- Add or modify a controller: `Assets/Scripts/Inference/Policies/PolicyInstaller.cs`
- Add a reusable policy or match field: `Assets/Scripts/Runtime/Core/PlatformSpecs.cs`
- Change combat rules or state: `Assets/Scripts/Runtime/Match/MatchManager.cs`
- Change supported episode bookkeeping: `Assets/Scripts/Training/Core/TrainingSessionCoordinator.cs`
- Change matchup scheduling: `Assets/Scripts/Tournament/Core/TournamentRunner.cs`
- Add a speculative teacher, probe, or arena experiment: `Assets/Scripts/Research/`
- Change the interactive selector only: `Assets/Scripts/Inference/GambitDemo/`

## Migration rule

Research is not deprecated. A research feature moves into Training only after it
has a stable contract, is useful across experiments, and has tests. Experimental
code can depend inward on supported assemblies; supported assemblies must never
depend outward on Research.

## Non-Unity compile check

While another Unity Editor owns the project, run `Tools/static_compile_check.sh`.
It invokes the pinned editor's Roslyn compiler but does not launch Unity, import
assets, enter Play Mode, or write into `Library/`.
