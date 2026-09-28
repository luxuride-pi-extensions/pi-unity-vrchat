#!/usr/bin/env bash
# Compile the C# bridge plugin with Unity's own reference set -- no Editor launch.
#
# Why this exists: `tsc --noEmit` cannot see C# at all, so every one of the
# plugin's original 29 compile errors sat latent until a user opened a project.
# This compiles the plugin offline instead, in seconds, using the exact
# reference set and defines Unity itself uses.
#
# It borrows Unity's own response file from any project that has been opened
# once, because hand-assembling the reference set does not work: Unity mixes
# facades in ways that produce hundreds of bogus errors (CS0518 from two corlib
# models, CS0433 from duplicate facade assemblies).
#
# Usage:
#   bash test/compile-plugin.sh <project-with-Library>
#   bash test/compile-plugin.sh            # auto-detects from ~/Unity + ~/Projects
#
# Note: `.../Data/MonoBleedingEdge/bin/csc` is a broken wrapper on this machine
# (it hardcodes a macOS build path from Unity's CI), so csc.exe is invoked
# through the bundled mono instead.

set -euo pipefail

HERE="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
PLUGIN_DIR="${HERE}/../unity/plugin"
OUT="${TMPDIR:-/tmp}/pi-bridge-verify.dll"
RSP="${TMPDIR:-/tmp}/pi-bridge-verify.rsp"

# ---- locate a Unity editor -------------------------------------------------
EDITOR_BIN="${UNITY_EDITOR_BIN:-}"
versions=()
if [[ -z "$EDITOR_BIN" ]]; then
  HUB_ROOT="${UNITY_HUB_PATH:-$HOME/Unity/Hub/Editor}"
  if [[ -d "$HUB_ROOT" ]]; then
    # Read the version directories through a glob rather than `ls | sort`: it
    # needs no subprocess and cannot mangle a name containing spaces.
    shopt -s nullglob
    for dir in "$HUB_ROOT"/*/; do
      versions+=("$(basename "$dir")")
    done
    # Newest version wins, so sort descending version-aware.
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
  echo "       mono: $MONO" >&2
  echo "       csc:  $CSC" >&2
  exit 2
fi
echo "editor: $EDITOR_BIN"

# ---- locate Unity's own compile response file ------------------------------
# Every project that has been opened once leaves its predefined-assembly
# response file under Library/Bee/artifacts/*.dag/.
PROJECT_ARG="${1:-}"
RSP_SRC=""
if [[ -n "$PROJECT_ARG" && -d "$PROJECT_ARG" ]]; then
  RSP_SRC="$(find "$PROJECT_ARG/Library/Bee/artifacts" -name 'Assembly-CSharp-Editor.rsp' 2>/dev/null | head -1 || true)"
fi
if [[ -z "$RSP_SRC" ]]; then
  for base in "$HOME/Projects" "$HOME/Unity"; do
    [[ -d "$base" ]] || continue
    RSP_SRC="$(find "$base" -path '*/Library/Bee/artifacts/*' -name 'Assembly-CSharp-Editor.rsp' 2>/dev/null | head -1 || true)"
    [[ -n "$RSP_SRC" ]] && break
  done
fi
if [[ -z "$RSP_SRC" ]]; then
  cat >&2 <<'EOF'
error: could not find Unity's Assembly-CSharp-Editor.rsp.

That file is produced when a project is opened in Unity once. Open any Unity
project (or pass one as the first argument) and re-run.
EOF
  exit 2
fi
echo "rsp:    $RSP_SRC"

# Unity's .rsp lists *relative* source paths, resolved against the project root.
# Invoking csc from anywhere else yields CS2001 "source file could not be found"
# for every one of them. So the compile runs from the project directory.
PROJECT_ROOT="$(cd "$(dirname "$RSP_SRC")/../../../.." && pwd)"
echo "root:   $PROJECT_ROOT"

# ---- build the verification response file ----------------------------------
# Three details that silently produce a FALSE PASS if you get them wrong:
#   1. Unity's .rsp has NO trailing newline, so a naive `echo "$src"` append
#      concatenates onto the last argument and the source is never compiled.
#   2. -out/-refout must be redirected, or we overwrite the project's real
#      Assembly-CSharp-Editor.dll.
#   3. The source list must RECURSE. A plain glob ("$PLUGIN_DIR"/*.cs) silently
#      skipped unity/plugin/vrchat/*.cs -- 4 files that were never compiled, and
#      the run still reported "OK: 0 errors" with an identical output size. The
#      guard below is what makes that visible.
#   4. The INSTALLED copy must be filtered OUT of Unity's response file. Once the
#      plugin lives in a project's Assets/Editor/PiUnityBridge/, Unity's own .rsp
#      lists those files, so a harness that appends the canonical source tree as
#      well compiles every type twice: 211 duplicate-definition errors that look
#      like code bugs but are the harness listing the same source twice.
{
  sed -e "s#-out:\"[^\"]*\"#-out:\"$OUT\"#" \
      -e "s#-refout:\"[^\"]*\"#-refout:\"${OUT%.dll}.ref.dll\"#" \
      "$RSP_SRC" \
  | grep -v 'Assets/Editor/PiUnityBridge'
  # A response file has no trailing newline, so each source needs a leading \n.
  find "$PLUGIN_DIR" -name '*.cs' -type f | sort | while IFS= read -r f; do
    printf '\n"%s"\n' "$f"
  done
} > "$RSP"

# Every one of OUR .cs files must appear in the response file. Without this
# check a non-recursive glob reports a clean compile of a subset, which is how the
# vrchat/ subdirectory went uncompiled for one run.
#
# Count only our own sources: Unity's response file also lists the PROJECT's
# sources (poiyomi, PCS, VEUT), so a bare count of .cs lines is 8 higher and
# would report a failure that is not one.
ON_DISK=$(find "$PLUGIN_DIR" -name '*.cs' -type f | wc -l)
IN_RSP=$(grep -F -c "$PLUGIN_DIR/" "$RSP" || true)
if [ "$ON_DISK" != "$IN_RSP" ]; then
  echo "FAILED: $ON_DISK .cs files on disk but $IN_RSP in the compile list." >&2
  echo "       A source file would be silently skipped and the compile would still pass." >&2
  find "$PLUGIN_DIR" -name '*.cs' -type f | sort | while IFS= read -r f; do
    grep -qF "$f" "$RSP" || echo "       missing: $f" >&2
  done
  exit 1
fi
echo "sources: $IN_RSP of our .cs files (+ Unity's own project sources)"

# ---- compile ---------------------------------------------------------------
# Run from the project root (see PROJECT_ROOT above) and pass absolute source
# paths, so the result does not depend on where this script was invoked.
rm -f "$OUT"
set +e
( cd "$PROJECT_ROOT" && "$MONO" "$CSC" "@$RSP" 2>&1 ) | grep -E 'error CS' || true
ERRORS=$( cd "$PROJECT_ROOT" && "$MONO" "$CSC" "@$RSP" 2>&1 | grep -cE 'error CS' || true )
set -e

if [[ "$ERRORS" != "0" ]]; then
  echo "FAILED: $ERRORS compile error(s)" >&2
  exit 1
fi
if [[ ! -f "$OUT" ]]; then
  echo "FAILED: no assembly produced -- the sources were not actually compiled." >&2
  exit 1
fi

# Guard against the false pass in (1) above: prove our type is in the output.
if ! "$DATA_DIR/MonoBleedingEdge/bin/monodis" --typedef "$OUT" 2>/dev/null | grep -q 'PiUnity'; then
  echo "FAILED: compiled clean but PiUnity is absent from the assembly." >&2
  echo "        The sources were not actually part of the compile." >&2
  exit 1
fi

echo "OK: 0 errors, PiUnity types present ($(stat -c%s "$OUT") bytes)"
