#!/usr/bin/env bash
set -euo pipefail

PROJECT_ROOT="$(cd "$(dirname "$0")/.." && pwd)"
UNITY_EDITOR_ROOT="/Applications/Unity/Hub/Editor/2022.3.62f3/Unity.app/Contents"
CHECK_OUTPUT_ROOT="${TMPDIR:-/tmp}/gambit-static-compile"
COMPILER="$UNITY_EDITOR_ROOT/MonoBleedingEdge/lib/mono/msbuild/Current/bin/Roslyn/csc.exe"
MONO="$UNITY_EDITOR_ROOT/MonoBleedingEdge/bin/mono"
FRAMEWORK="$UNITY_EDITOR_ROOT/NetStandard/ref/2.1.0/netstandard.dll"

mkdir -p "$CHECK_OUTPUT_ROOT"
cd "$PROJECT_ROOT"

unity_refs=()
for dll in "$UNITY_EDITOR_ROOT"/Managed/UnityEngine/*.dll; do unity_refs+=("-r:$dll"); done

compile_sources=()
while IFS= read -r -d '' source; do compile_sources+=("$source"); done < <(find Assets/Scripts/Runtime -name '*.cs' -print0)
"$MONO" "$COMPILER" -nologo -noconfig -nostdlib -langversion:9.0 -target:library \
  "-out:$CHECK_OUTPUT_ROOT/Gambit.Runtime.dll" "-r:$FRAMEWORK" \
  "-r:Library/ScriptAssemblies/Unity.ML-Agents.dll" "${unity_refs[@]}" "${compile_sources[@]}"

compile_sources=()
while IFS= read -r -d '' source; do compile_sources+=("$source"); done < <(find Assets/Scripts/Training -name '*.cs' -print0)
"$MONO" "$COMPILER" -nologo -noconfig -nostdlib -langversion:9.0 -target:library \
  "-out:$CHECK_OUTPUT_ROOT/Gambit.Training.dll" "-r:$FRAMEWORK" \
  "-r:$CHECK_OUTPUT_ROOT/Gambit.Runtime.dll" \
  "-r:Library/ScriptAssemblies/Unity.ML-Agents.dll" \
  "${unity_refs[@]}" "${compile_sources[@]}"

compile_sources=()
while IFS= read -r -d '' source; do compile_sources+=("$source"); done < <(find Assets/Scripts/Tournament -name '*.cs' -print0)
"$MONO" "$COMPILER" -nologo -noconfig -nostdlib -langversion:9.0 -target:library \
  "-out:$CHECK_OUTPUT_ROOT/Gambit.Tournament.dll" "-r:$FRAMEWORK" \
  "-r:$CHECK_OUTPUT_ROOT/Gambit.Runtime.dll" "${unity_refs[@]}" "${compile_sources[@]}"

editor_refs=("-r:$UNITY_EDITOR_ROOT/Managed/Newtonsoft.Json.dll")
compile_sources=()
while IFS= read -r -d '' source; do compile_sources+=("$source"); done < <(find Assets/Scripts/Inference -name '*.cs' -print0)
"$MONO" "$COMPILER" -nologo -noconfig -nostdlib -langversion:9.0 -target:library \
  "-out:$CHECK_OUTPUT_ROOT/Gambit.Inference.dll" "-r:$FRAMEWORK" \
  "-r:$CHECK_OUTPUT_ROOT/Gambit.Runtime.dll" "-r:$CHECK_OUTPUT_ROOT/Gambit.Training.dll" \
  "-r:$CHECK_OUTPUT_ROOT/Gambit.Tournament.dll" \
  "-r:Library/ScriptAssemblies/Unity.ML-Agents.dll" \
  "-r:Library/ScriptAssemblies/Unity.Barracuda.dll" \
  "${unity_refs[@]}" "${editor_refs[@]}" "${compile_sources[@]}"

compile_sources=()
while IFS= read -r -d '' source; do compile_sources+=("$source"); done < <(find Assets/Scripts/Research -name '*.cs' -print0)
"$MONO" "$COMPILER" -nologo -noconfig -nostdlib -define:GAMBIT_RESEARCH \
  -langversion:9.0 -target:library "-out:$CHECK_OUTPUT_ROOT/Gambit.Research.dll" \
  "-r:$FRAMEWORK" "-r:$CHECK_OUTPUT_ROOT/Gambit.Runtime.dll" \
  "-r:$CHECK_OUTPUT_ROOT/Gambit.Training.dll" "-r:$CHECK_OUTPUT_ROOT/Gambit.Tournament.dll" \
  "-r:$CHECK_OUTPUT_ROOT/Gambit.Inference.dll" \
  "-r:Library/ScriptAssemblies/Unity.ML-Agents.dll" \
  "-r:Library/ScriptAssemblies/UnityEngine.UI.dll" \
  "${unity_refs[@]}" "${compile_sources[@]}"

echo "Static compilation passed: Runtime, Training, Tournament, Inference, Research"
