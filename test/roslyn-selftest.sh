#!/usr/bin/env bash
# Run the Roslyn snippet compiler's runtime self-test.
#
# Why this exists: the snippet compiler shipped broken and every static check
# passed anyway. It compiled clean with the offline plugin compile, because the
# failure was at RUNTIME -- Unity removed the Mono C# compiler in 2021.2, so
# Mono.CSharp.AssemblyBuilder does not exist on 2022.3, and the first
# unity_execute_csharp call returned:
#
#   Value cannot be null.  Parameter name: type
#
# Compiling proves nothing about this path. So this compiles SnippetCompiler
# (which has no Unity dependency, by design) plus a self-test program, then
# RUNS it under Unity's bundled mono against Unity's own Roslyn, with no Editor
# open and in about two seconds.
#
# SnippetCompiler being Unity-free is the whole point: it is what makes the part
# most likely to break silently testable without a running Editor.

set -euo pipefail

HERE="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
PLUGIN_DIR="${HERE}/../unity/plugin"
OUT="${TMPDIR:-/tmp}/pi-roslyn-selftest.exe"

# ---- locate a Unity editor (same discovery as compile-plugin.sh) -----------
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
# A minimal self-contained one: the self-test only needs the framework facades.
# Note .../Data/MonoBleedingEdge/bin/csc is a broken wrapper on some installs
# (it hardcodes a macOS CI build path), so csc.exe is run through mono.
REF_DIR="$DATA_DIR/MonoBleedingEdge/lib/mono/4.8-api"
RSP="${TMPDIR:-/tmp}/pi-roslyn-selftest.rsp"
{
  printf -- '-nologo\n'
  printf -- '-nostdlib+\n'
  printf -- '-out:"%s"\n' "$OUT"
  for name in mscorlib System System.Core; do
    printf -- '-r:"%s"\n' "$REF_DIR/$name.dll"
  done
  # A response file has no trailing newline: each source needs a leading \n or
  # it is concatenated onto the previous argument and silently ignored. That
  # produced a false "0 errors" pass once already.
  printf '\n"%s"\n' "$PLUGIN_DIR/PiUnityBridge.Compiler.cs"
  printf '\n"%s"\n' "$HERE/roslyn-selftest.cs"
} > "$RSP"

# ---- compile and run -------------------------------------------------------
rm -f "$OUT"
set +e
"$MONO" "$CSC" "@$RSP" 2>&1 | grep -E 'error CS' || true
set -e
if [[ ! -f "$OUT" ]]; then
  echo "FAILED: self-test did not compile" >&2
  exit 1
fi

"$MONO" "$OUT" "$DATA_DIR"
