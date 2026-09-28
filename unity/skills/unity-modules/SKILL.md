---
name: unity-modules
description: Multi-part Unity work structured as manager, per-module worker agents, and a separate critic. Use when a task has several objects or concerns and the user wants modular, revertible runs.
---

# Unity Modules

Use this when a task has several distinct parts — several objects, or several
concerns like geometry + materials + lighting. Each part becomes a module that
reverts independently.

## Layout

```
<workspace>/
  unity-workspace.json          manifest: iteration counter, module runs, views
  script.cs                     single-part work
  modules/<name>/script.cs      one module
  modules/<name>/render.png     its render
  modules/<name>/runs/NN/       checkpoint + the exact script that ran
```

## Flow

1. `subagents_enable` once — fresh sessions do not have the subagent tool
   active until you enable it.
2. Delegate one worker per module with a compact brief: goal, GameObject names
   it owns, acceptance criteria, workspace + module name.
3. Each worker runs `unity_module_run { workspace, moduleName, render: true }`
   and reports back. It may attempt 3 self-fixes, then report the failure
   verbatim.
4. Spawn a **fresh** critic per module iteration. It judges
   `modules/<name>/render.png` against the brief, fixes view/camera problems,
   and logs with `unity_log_critique`.

## Naming

Lowercase letters, digits, dashes (`table-top`). Names are contract: the
GameObject names in the brief must be the names in the script.

## What a revert actually restores

`unity_module_run` checkpoints the `.unity` **scene file** before running.
`unity_module_revert` restores that file. It does **not** roll back assets
written under `Assets/` — a module that created a material, a texture, or a
prefab leaves those behind. This is a narrower guarantee than the Blender
extension's whole-file checkpoint, and the tool says so in its result.

A revert also refuses by default when other modules ran after the target, since
it would discard their work. Revert those first, or pass `force: true`.

## Rules for the manager

- Never run two modules in parallel. One scene, one live Editor, one session.
- Never paste a module's source into your own context. Read its report.
- Never edit `unity-workspace.json` by hand.
- Stay the manager: decide, delegate, judge. Do not write module scripts
  yourself once workers are running.
