#!/usr/bin/env bash
# Run the JSON writer's runtime self-test.
#
# Why this is a separate harness: PiUnity.Json had a bug no compiler catches.
# OpenObject(string key) built the nested object in a throwaway Json, so the
# parent's stack never recorded the nesting and the matching CloseObject() threw
# "Index was out of range" at runtime. It compiled clean, passed the offline
# plugin compile, and only failed once a real Editor was polling the bridge.
#
# So this compiles the writer plus a self-test program, then RUNS it under
# Unity's bundled mono. Behaviour, not compilation, is the thing under test.

set -euo pipefail

HERE="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
PLUGIN_DIR="${HERE}/../unity/plugin"
OUT="${TMPDIR:-/tmp}/pi-json-selftest.exe"

# ---- locate a Unity editor (same logic as compile-plugin.sh) ----------------
EDITOR_BIN="${UNITY_EDITOR_BIN:-}"
versions=()
if [[ -z "$EDITOR_BIN" ]]; then
  HUB_ROOT="${UNITY_HUB_PATH:-$HOME/Unity/Hub/Editor}"
  if [[ -d "$HUB_ROOT" ]]; then
    shopt -s nullglob
    for dir in "$HUB_ROOT"/*/; do versions+=("$(basename "$dir")"); done
    mapfile -t versions < <(printf '%s\n' "${versions[@]}" | sort -rV)
    for v in "${versions[@]}"; do
      candidate="$HUB_ROOT/$v/Editor/Unity"
      if [[ -x "$candidate" ]]; then EDITOR_BIN="$candidate"; break; fi
    done
  fi
fi
if [[ -z "$EDITOR_BIN" ]]; then
  echo "error: no Unity editor found. Set UNITY_EDITOR_BIN or UNITY_HUB_PATH." >&2
  exit 2
fi
DATA_DIR="$(dirname "$EDITOR_BIN")/Data"
MONO="$DATA_DIR/MonoBleedingEdge/bin/mono"
CSC="$DATA_DIR/MonoBleedingEdge/lib/mono/msbuild/Current/bin/Roslyn/csc.exe"
if [[ ! -x "$MONO" || ! -f "$CSC" ]]; then
  echo "error: compiler not found under $DATA_DIR" >&2
  exit 2
fi
echo "editor: $EDITOR_BIN"

# ---- reference set ---------------------------------------------------------
# PiUnity.Json.cs has Unity using-directives, so it needs the same references
# the plugin compiles against. Reuse Unity's own response file when available.
RSP_SRC=""
for base in "$HOME/Projects" "$HOME/Unity"; do
  [[ -d "$base" ]] || continue
  RSP_SRC="$(find "$base" -path '*/Library/Bee/artifacts/*' -name 'Assembly-CSharp-Editor.rsp' 2>/dev/null | head -1 || true)"
  [[ -n "$RSP_SRC" ]] && break
done
if [[ -z "$RSP_SRC" ]]; then
  echo "error: could not find Unity's Assembly-CSharp-Editor.rsp for a reference set." >&2
  echo "       Open any Unity project once, then re-run." >&2
  exit 2
fi
PROJECT_ROOT="$(cd "$(dirname "$RSP_SRC")/../../../.." && pwd)"
RSP="${TMPDIR:-/tmp}/pi-json-selftest.rsp"
# A response file has no trailing newline, so each source needs a leading one.
{
  # Exclude the project's own plugin source: once the plugin is installed in
  # Assets/Editor/PiUnityBridge/, Unity's response file lists it, so keeping it
  # here would declare every type twice (CS0111 and friends) and the "failure"
  # would be the harness listing the same file two ways. This has bitten
  # compile-plugin.sh and then both selftests.
  grep -vE '^-out:|^-refout:|^-target:' "$RSP_SRC" | grep -v 'Assets/Editor/PiUnityBridge'
  printf '\n-out:"%s"\n' "$OUT"
  printf '\n"%s"\n' "$PLUGIN_DIR/PiUnity.Json.cs"
  printf '\n"%s"\n' "$HERE/json-selftest.cs"
} > "$RSP"

# ---- compile and run -------------------------------------------------------
rm -f "$OUT"
set +e
( cd "$PROJECT_ROOT" && "$MONO" "$CSC" "@$RSP" 2>&1 ) | grep -E 'error CS' || true
set -e
if [[ ! -f "$OUT" ]]; then
  echo "FAILED: self-test did not compile" >&2
  exit 1
fi

"$MONO" "$OUT"
