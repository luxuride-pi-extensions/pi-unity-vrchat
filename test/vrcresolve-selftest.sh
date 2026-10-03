#!/usr/bin/env bash
# Run the VRChat reflection-resolver self-test.
#
# Why this is a separate harness, in the same shape as json-selftest.sh and
# roslyn-selftest.sh: the bug under test is a RUNTIME behaviour that compiles
# clean. VrcResolve.Type's simple-name fallback resolved the moved VRCFury type
# names to plain data objects (com.vrcfury.api.Components.FuryToggle &c., which
# derive from System.Object), and ComponentsOn then handed that type to
# GameObject.GetComponents, which throws:
#
#   GetComponent requires that the requested component 'FuryArmatureLink'
#   derives from MonoBehaviour or Component or is an interface.
#
# That took down `unity_vrc { action: "project" }` on every project with a
# recent VRCFury. The offline plugin compile cannot see it, and the TS test
# suite cannot either -- the resolution needs a live AppDomain with real
# assemblies. So the harness fakes the minimum: it compiles
# plugin/vrchat/PiVRChat.Types.cs together with a stub that declares the same
# full names the real packages declare, in the same shapes (one Component, one
# plain class), then asserts IsComponentType/ComponentsOn/ComponentOn refuse the
# plain one.
#
# Compiled and run under Unity's bundled mono. Requires a Unity editor for its
# reference set, like the other self-tests.

set -euo pipefail

HERE="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
PLUGIN_DIR="${HERE}/../unity/plugin"
OUT="${TMPDIR:-/tmp}/pi-vrcresolve-selftest.exe"

# ---- locate a Unity editor (same logic as the other self-tests) ------------
EDITOR_BIN="${UNITY_EDITOR_BIN:-}"
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
# Deliberately a MINIMAL one: only the framework facades plus Unity's own
# engine/editor modules. The other self-tests reuse Unity's
# Assembly-CSharp-Editor.rsp, and that is wrong here: it carries every
# PACKAGE reference assembly in the project (Poi.Tools.ref.dll,
# AnimationViewer.ref.dll, ...). VrcResolve calls GetExportedTypes(), so any
# reference assembly that transitively mentions an editor type makes the
# reflection throw ReflectionTypeLoadException -- which VrcResolve catches and
# treats as "no types here", silently disabling the simple-name fallback the
# whole test is about. UnityEngine.CoreModule.dll is enough for
# MonoBehaviour/Component, and there are no facades in play here, so this does
# not run into the CS0518/CS0433 trap compile-plugin.sh documents.
REF_DIR="$DATA_DIR/MonoBleedingEdge/lib/mono/4.8-api"
RSP="${TMPDIR:-/tmp}/pi-vrcresolve-selftest.rsp"
{
  printf -- '-nologo\n'
  printf -- '-nostdlib+\n'
  printf -- '-out:"%s"\n' "$OUT"
  # netstandard ALONE, no 4.8 facades alongside it: Unity's modules are built
  # against netstandard 2.1, and mixing the facades in produces a facade pair --
  # "The type 'Type' exists in both mscorlib and netstandard" (CS0433) plus a
  # cascade of CS0518 "predefined type not defined". That is the same trap
  # compile-plugin.sh works around by borrowing Unity's own response file; here
  # netstandard 2.1 on its own is the clean set.
  NETSTANDARD="$DATA_DIR/NetStandard/ref/2.1.0/netstandard.dll"
  if [[ ! -f "$NETSTANDARD" ]]; then
    echo "error: Unity's netstandard 2.1 reference assembly not found at $NETSTANDARD" >&2
    exit 2
  fi
  printf -- '-r:"%s"\n' "$NETSTANDARD"
  for dll in "$DATA_DIR"/Managed/UnityEngine/*.dll; do
    printf -- '-r:"%s"\n' "$dll"
  done
  # A response file has no trailing newline: each source needs a leading \n or
  # it is concatenated onto the previous argument and silently ignored (this
  # produced a false "0 errors" pass once already, in roslyn-selftest.sh).
  printf '\n"%s"\n' "$PLUGIN_DIR/vrchat/PiVRChat.Types.cs"
  printf '\n"%s"\n' "$HERE/vrcresolve-selftest.cs"
} > "$RSP"

# ---- compile and run -------------------------------------------------------
rm -f "$OUT"
set +e
( cd "$DATA_DIR/Managed" && "$MONO" "$CSC" "@$RSP" 2>&1 ) | grep -E 'error CS' || true
set -e
if [[ ! -f "$OUT" ]]; then
  echo "FAILED: self-test did not compile" >&2
  exit 1
fi

# GameObject construction needs a Unity runtime, so the self-test exercises the
# TYPE-level guard (IsComponentType) rather than building a scene. That is the
# guard that decides whether GetComponents is ever reached, and the only part
# that is environment-independent.
set +e
# Runtime assembly resolution.
#
# Compilation alone is not enough: VrcResolve.Type reflects over
# AppDomain.CurrentDomain.GetAssemblies(), so the self-test's own stand-in types
# must actually LOAD. Two traps, both hit while writing this:
#
# 1. mono resolves Unity's modules from MONO_PATH, not next to the exe. Without
#    it the first `typeof(Component)` dies with "Could not load file or
#    assembly 'UnityEngine.CoreModule'".
# 2. Unity's response file references .ref.dll (reference assemblies, meant
#    for compiling only). One of those transitively pulls in UnityEditor
#    types, so GetExportedTypes() on this assembly throws
#    ReflectionTypeLoadException -- which VrcResolve catches and moves on, so
#    every simple-name fallback lookup silently returns null and the test
#    passes for the wrong reason. UnityEditor.CoreModule.dll must therefore be
#    resolvable at runtime too.
export MONO_PATH="$DATA_DIR/Managed:$DATA_DIR/Managed/UnityEngine${MONO_PATH:+:$MONO_PATH}"

"$MONO" "$OUT"
status=$?
set -e
exit "$status"