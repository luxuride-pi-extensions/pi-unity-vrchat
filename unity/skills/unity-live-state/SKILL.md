---
name: unity-live-state
description: The single source of truth for using the live Unity UI state in this workspace. Read before acting on "it", "this", the selected object, or the current view.
---

# Unity Live State

The authoritative live Unity state is injected automatically before every turn
while the extension is engaged. Read it. Do not reconstruct it from memory.

## When the injected block is missing or stale

Call `unity_session_context { workspace }`. It reports:

- open project and whether it matches the workspace
- open scene, and whether it is loaded
- selection and active object
- active transform tool
- Scene view perspective and camera

Treat this as the *only* source of truth for the current turn. Selection
mentions from earlier turns, and your own prior inferences, are stale by
definition — a user clicking a cube changes the answer.

## The injected block is deliberately small

It carries the cheap, always-changing facts: scene, selection, tool, viewport.
It does **not** carry transforms or component fields, because those are large and
mostly irrelevant to a turn. When you need them, ask:

| Question | Call |
| --- | --- |
| Where is this object? | `unity_scene_info { categories: ["objects"] }` — world and local position, rotation, scale, tag, layer, prefab |
| Where is this **bone**? | `unity_bones { gameObject, bones: [...] }` — a bone is not a GameObject; the rig lives inside the prefab |
| What is on this component? | `unity_inspect { gameObject, component }` — every serialized property, like the Inspector |
| Where is this bone relative to its parent? | `unity_set_transform` reads back both spaces; the `head` of an arm is a local offset, not a world point |
| What is in the selected prefab before placing it? | `unity_prefab_info { prefabPath }` — read-only, adds nothing to the scene |

"it" and "this" name an object by what the state block says, but you still have
to hand the tool something it can resolve. A **scene path**
(`"Chair (2)/Leg"`) is always safe; a bare name is refused when two scene roots
contain it, which is the normal state for a prefab placed more than once.

Do not infer a position from a previous turn's `unity_scene_info` either. If
something was moved, read it again.

## When it says UNAVAILABLE

The Editor may be closed, still importing, or mid-compile. Do **not** guess and
do **not** act. Check `unity_bridge_status`, and if the Editor is not running,
open the project. If the project path is not the one you expected, the state
block will say so — a mismatch means you are about to edit the wrong project.

The live Editor is a **single global session**, not a per-workspace one, so
the state block reports the real open project even when the current directory
is not a pi workspace. If it says there is no `unity-workspace.json` at the
cwd, that is a true statement about *workspace management*, not a warning
about the bridge: the live state above it is still authoritative. Adopt or
create a workspace with `unity_workspace_init` when you need one to iterate on,
then pass an explicit `workspace` to every tool call.

## Phrasing

"it", "this", "the selected object", and "the current view" all mean *whatever
this turn's state block says*, not whatever a previous turn said.
