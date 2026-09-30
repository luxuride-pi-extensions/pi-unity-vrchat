---
name: unity-analyze
description: Inspect and render Unity workspaces without mutating them unless the user explicitly asks for changes.
---

# Unity Analyze

Use this skill when the user wants to look at Unity — understand what is in the
scene, how a project is set up, or get a picture of something — not change it.

## Narrow the read

`unity_scene_info` without `categories` is already lean (objects, materials,
cameras, lights, renderSettings). Add sections deliberately:

| Question | Categories |
| --- | --- |
| What's in the scene? | `objects` (includes world + local transform, tag, layer, prefab) |
| Where is this bone? | `unity_bones` — a bone is not a GameObject |
| What's on this object? | `unity_inspect` — every serialized property |
| Why does it look wrong? | `materials`, `embeddedMaterials`, `renderSettings` |
| What render pipeline / packages? | `packages` |
| Why is my type missing? | `compileStatus` |
| What's actually in the project folder? | `assets` — each row says `isPrefab` / `isModel` |
| What's IN this prefab, before placing it? | `unity_prefab_info` — read-only, instantiates nothing |

`packages` and `assets` are the slow ones. Everything else is fast.

`unity_prefab_info` is how you answer "what will this prefab actually add to my
scene" without adding it. It reports the child hierarchy, every material, and
the bounds — which matters for a model that came from Blender, because the
`materials` category lists standalone `.mat` files only and a transferred model's
materials are sub-assets of the FBX. An empty `materials` there is not evidence
they were lost; `embeddedMaterials`, or `unity_prefab_info`, is where they are.

`unity_bones` and `unity_inspect` are read-only. `unity_bones` is how you answer
"where is the left hand" on a rigged avatar — a VRChat avatar is not scene
geometry, the rig lives inside the prefab and is addressed by Humanoid bone name.
`unity_inspect` walks `SerializedObject`, so it reads a component exactly the way
the Inspector displays it, which is the only way to inspect a package type
(`VRC_AvatarDescriptor`, lilToon, VRCFury) whose fields you cannot know in
advance.

### Naming an object reliably

Every tool that takes an object also accepts a **scene path** —
`"Chair (2)/Leg"`, not a bare `Leg`. A bare name is refused when it is ambiguous
across scene roots, and a prefab placed more than once guarantees that ambiguity
for its children. Reach for the path whenever you have one; the child listing
`unity_prefab_info` and `unity_instantiate_prefab` return gives you every path
you need.

### VRChat projects

`unity_vrc` (avatars) and `unity_world` (worlds) answer package questions that
neither `unity_scene_info` nor `unity_inspect` can, because the answer lives in
the package set rather than in the scene.

**Read-only actions** — safe to call while analysing:

| Action | Answers |
| --- | --- |
| `vrc action=project` | Which packages are installed, which avatars are in the scene, and whether the avatar is upload-ready. **Start here.** |
| `vrc action=avatar` / `optimizer` | One avatar in detail; vertex counts against VRChat's 70000 limit and which optimizer components are present. |
| `vrc action=vrcfury-list` | Which VRCFury elements can be added. |
| `vrc action=liltoon-usage` / `liltoon-material` | Which materials use lilToon, and how one is configured. |
| `vrc action=path` | A bone's transform path under the avatar. |
| `world action=scene` | Packages, `VRC_SceneDescriptor`, Udon counts, world behaviours, light volumes — the whole picture in one call. |
| `world action=descriptor` / `light-volumes` / `udon` / `find-behaviour` | Each of those in detail. |

**Mutating actions** — never call these while analysing: `vrc action=
ensure-trace-and-optimize` and `vrc action=vrcfury-add` add components, and
`world action=create-light-volume` creates objects. Report that one is missing
and let the user decide; adding `TraceAndOptimize` to someone's avatar is a
build-affecting change, not an observation.

Two findings worth surfacing rather than fixing silently:

- `TraceAndOptimize` **missing** means the avatar uploads unoptimized. Say so.
- A light volume with a `LightVolumeSetup` present but **no volumes** means the
  world has nothing to bake. Volumes only affect the build, so the Scene view
  will look unchanged until the user builds.

An unknown action returns the list of known ones, so a typo self-corrects.

## Getting a picture

- `unity_render` — through a camera, with lighting and materials. Use for
  "what does this look like".
- `unity_capture_view` — the Scene view, as the artist left it. Use for "what
  am I looking at".

If the scene has no camera, a render fails. That is a fact about the scene, not
an error to route around: add a camera or ask the user where to look from.

## Judging the view itself

A render that cannot show the thing you are judging cannot produce a score. Say
so explicitly when reporting: "the camera is inside the geometry, so the
silhouette is unverifiable" is more useful than a confident bad score.

## Read-only contract

Everything here is non-mutating — including the read-only `vrc` / `world`
actions above, and the mutating ones named there. If the analysis reveals a
problem, report it and propose a fix — do not apply it unless asked.
