---
name: unity-edit
description: Edit an existing Unity scene or project through the live Editor, with checkpointed, revertible changes.
---

# Unity Edit

Use this skill when modifying a scene or project that already exists.

## Inspect first

Never mutate before you know what is there:

```
unity_scene_info { workspace, categories: ["objects", "materials", "cameras", "lights"] }
```

The `objects` category includes world and local position, rotation, and scale,
so "where is this object" needs no second call.

Add `"compileStatus"` when something is missing that you expect to exist — a
missing type is a compile error far more often than it is an empty scene.

### Component properties

For anything on a component, use the Inspector rather than reflection:

```
unity_inspect { workspace, gameObject: "Chiffon", component: "VRC_AvatarDescriptor" }
unity_set_property { workspace, gameObject: "Chiffon", component: "VRC_AvatarDescriptor",
                     property: "ViewPosition.y", value: "1.5" }
```

`unity_inspect` walks `SerializedObject`, so it sees exactly what a human sees —
serialized private fields and `[HideInInspector]` ones included. That is the
only way to read or write a package field (`VRC_AvatarDescriptor`, lilToon,
VRCFury) whose shape you do not know. Both writes are undoable; a scene write
marks the scene dirty without saving to disk.

Pass `space: "asset"` when the target is a prefab **asset** rather than a scene
instance — otherwise the edit lands on the instance and is lost on reload. For
an object inside a prefab instance, `space: "asset"` writes through to the source
asset, so every instance of that prefab sees the change; use the default `scene`
space when you mean only this one.

### Prefabs

`unity_set_property` and `unity_set_transform` work on an instantiated prefab,
and writing to one is a prefab **override** — recorded, so it survives, but
revertible only by reloading the prefab. Use these two tools rather than a
snippet:

```
unity_prefab_info      { workspace, prefabPath: "Assets/PiImported/Chair.prefab" }
unity_instantiate_prefab { workspace, prefabPath: "Assets/PiImported/Chair.prefab",
                           position: "2,0,0", rotation: "0,90,0" }
```

`unity_instantiate_prefab` returns the instance's scene `path` and its child
inventory. **Use that `path`** for any follow-up call: a prefab's children repeat
their names in every instance, so `gameObject: "Leg"` is refused as ambiguous the
moment the asset is placed twice, while `gameObject: "Chair (2)/Leg"` resolves
exactly.

### Rigs and bones

A bone's position is not a GameObject's position. The rig lives inside the
prefab and is addressed by Humanoid bone name:

```
unity_bones { workspace, gameObject: "Chiffon", bones: ["Head", "LeftHand"] }
unity_set_transform { workspace, gameObject: "Head", position: "0,0.05,0", space: "local" }
```

Use `space: "local"` to place a bone relative to its parent. A typical avatar
has 180+ bones, so always pass `bones` to filter rather than reading the whole
rig.

## The edit loop

1. Write C# to the workspace root `script.cs` (or pass a short `inlineCode`).
2. `unity_execute_csharp { workspace, scriptPath, saveAfter: true }`.
3. `unity_render { workspace }` to see it.
4. `unity_log_critique` with concrete issues.
5. Repeat up to 5 times, then stop and report.

## Checkpoints

For a risky change, `saveBefore: true` copies the scene file to
`<workspace>/pre-execute.unity` first. For multi-part work use modules instead —
see the `unity-modules` skill; `unity_module_revert` gives you a real undo.

## Writing scene code

```csharp
using UnityEditor;
using UnityEngine;

public static class PiScript
{
    public static void Run()
    {
        var existing = GameObject.Find("Pi_Table");
        if (existing == null)
        {
            var go = GameObject.CreatePrimitive(PrimitiveType.Cube);
            go.name = "Pi_Table";
        }
    }
}
```

- Entry point: `PiScript.Run` (also accepts `PiEntryPoint.Run` or any static
  `Run`/`Main`/`Execute`).
- **Re-runnable by construction**: look up with `GameObject.Find` before
  creating, and mutate the found object. A module run that duplicates its own
  GameObjects on the second run is a bug.
- `Undo.RegisterCreatedObjectUndo` is unnecessary; the module checkpoint is the
  undo mechanism.

## Materials

Do not hand-pick a shader. `PiUnity.PiMaterial` already knows which pipeline the
project is on, and the failure it prevents is silent:

```csharp
var material = PiMaterial.Create("Assets/Materials/Bone.mat", Color.grey);
PiMaterial.Assign(someGameObject, material);
```

The trap: a URP shader (lilToon) in a built-in pipeline project resolves via
`Shader.Find`, assigns without error, passes `HasProperty("_BaseColor")` — and
renders **pure white**. It reads as "the renderer is broken" rather than "wrong
shader". `PiMaterial.Describe()` prints the pipeline, which shaders are
installed, and which one it will pick.

Two related traps `PiMaterial` also handles:
- Assigning a new shader **resets** the material's properties, so a colour set
  before the swap is silently lost. `Create` sets the colour after.
- `Shader.Find` returns `null` for a shader that is not installed, and a
  null-assignment "succeeds" and leaves a magenta object.

Check `unity_scene_info { categories: ["packages"] }` to see which render
pipeline the project actually uses.

## Rules

- Never edit a `.unity` or `.asset` file as text.
- Never delete a GameObject you did not create.
- One `workspace` for the whole task.
- If a script throws, read the error rather than retrying the same call.
- Read `unity_inspect` before `unity_set_property`: the property path must match
  exactly, and the error message lists the real paths when it does not.
