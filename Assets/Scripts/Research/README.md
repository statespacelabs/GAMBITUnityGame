# Optional research sources

These legacy experiment, telemetry, training, and evaluation sources are not
part of the release runtime. The `Gambit.Research` assembly is compiled only
when the `GAMBIT_RESEARCH` scripting define is enabled.

The release demo must build and run without this define. Reconnecting the
legacy research workflows to the release composition root is intentionally
outside the release path and should be done through explicit adapters rather
than direct dependencies from `Gambit.Runtime`.
