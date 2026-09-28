---
name: unity-create
description: Create a new Unity project workspace, or adopt an existing project (e.g. a configured VRChat world or an existing game) so pi can work in it.
---

# Unity Create

Use this skill when the user wants a brand-new Unity project, or wants pi to work
in a project they already have.

## Two ways in

**Generate** — pi copies its bundled 3D template and installs the bridge plugin:

```
unity_workspace_init { workspace: "outputs/my-world", template: "3d" }
```

**Adopt an existing project** — the right choice for a configured VRChat world,
an existing game, or anything with packages, render pipeline settings, and
scene setup you must not disturb:

```
unity_workspace_init { workspace: "~/Projects/MyVRChatWorld", sourceProject: "~/Projects/MyVRChatWorld" }
```

When the workspace path *is* the source project, pi adopts it **in place**: it
writes `unity-workspace.json` and `Assets/Editor/PiUnityBridge/` into the real
project and never copies or rewrites its existing assets. That is deliberate —
adopting must not become a migration.

To copy a project instead, pass a `sourceProject` different from `workspace`.

## After init

The first bridge request is slow: Unity has to open the project, import it, and
compile the plugin. Expect 1–3 minutes on a real project.

1. `unity_bridge_status` — confirm the Editor is up and the plugin compiled.
   If the plugin has a compile error, the heartbeat never appears; the
   `liveUnityMessage` from init tells you what the bridge said.
2. `unity_scene_info` — confirm you see the scene you expected.
3. Only then start building.

## Fresh projects have no scene yet

A generated project ships an `Assets/Scenes/Main.unity`, but Unity may still be
generating settings on first open. If `unity_scene_info` reports no loaded scene,
create one:

```
unity_execute_csharp { inlineCode: "using UnityEditor; using UnityEditor.SceneManagement; using UnityEngine; public static class PiScript { public static void Run() { var s = EditorSceneManager.NewScene(NewSceneSetup.DefaultGameObjects, NewSceneMode.Single); EditorSceneManager.SaveScene(s, \"Assets/Scenes/Main.unity\"); } }" }
```

## Rules

- Keep one `workspace` argument value for the whole task; re-deriving it loses
  the manifest.
- Never edit a `.unity` file as text. Scenes are Unity's to serialize; use
  `unity_execute_csharp`.
- Do not add packages to an adopted project without asking. The user chose
  those versions deliberately.
