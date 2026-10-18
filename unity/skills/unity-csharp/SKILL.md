---
name: unity-csharp
description: Write C# that runs inside the live Unity Editor through unity-vrchat — entry points, available APIs, and the traps that fail silently.
---

# Unity C# for the pi Bridge

The bridge compiles your snippet in memory and invokes it. It does not write a
file into `Assets/`, so there is no domain reload and no compile step to wait
for — but there are rules.

## Entry point

Any of these is found automatically:

```csharp
public static class PiScript { public static void Run() { /* ... */ } }
```

Prefer `PiScript.Run`. `PiEntryPoint.Run` and a bare static `Run`/`Main`/
`Execute` also work.

## What is referenced

The compiled snippet gets `UnityEngine`, `UnityEditor`, and the project's own
referenced assemblies (so URP, TMP, lilToon, and VRChat SDK types resolve). It
does **not** get `Mono.CSharp` or arbitrary NuGet packages. If a type is
missing, check `unity_scene_info { categories: ["compileStatus", "packages"] }`
before assuming a bug in your code.

VRChat and lilToon types resolve by full name — verified on a real avatar with
`VRC.SDK3A`, `VRC.SDKBase.Editor`, `lilToon.Editor`, `VRCFury`, and
`nadena.dev.ndmf` all reachable. The reference set is the union of every
assembly loaded in the Editor plus `Library/ScriptAssemblies`, so a snippet
works in a project whose package set pi knows nothing about.

## Implicit usings — do not declare them

These are prepended automatically, so a snippet needs no `using` at all:

`System`, `System.IO`, `System.Collections.Generic`, `System.Linq`, `System.Text`,
`System.Reflection`, `UnityEngine`, `UnityEditor`, `PiUnity`

Declaring one yourself is harmless (it is skipped, not duplicated) and a
conventional script is not warned about it.

`Object` and `Random` are aliased to `UnityEngine.Object` and `UnityEngine.Random`.
With both `System` and `UnityEngine` in scope a bare `Object` or `Random` would
otherwise be CS0104 ambiguous, and both are almost always meant as the Unity
type — `Object.DestroyImmediate(x)`, `Random.Range(0f, 1f)`. Alias them
yourself only if you need the `System` one.

## Error line numbers match your file

Diagnostic line numbers refer to the snippet as you wrote it, not to the
compiled text. So `PiSnippet.cs(12,5)` is line 12 of your `script.cs`.

## Console output

`Debug.Log` from your snippet appears in the Editor console, not in the tool
result. The tool result carries the thrown exception instead. So: log
*decisions*, and let failures throw.

## Failures that are silent

These produce no error and must be checked explicitly:

- **`Shader.Find` returned null** → the material is magenta. Check for `null`
  and `Debug.LogError` before assigning. `PiMaterial.PickShader()` does this for
  the project's actual pipeline.
- **Assigned a URP shader in a built-in pipeline project** → resolves, assigns,
  `HasProperty("_BaseColor)` is true, and renders **pure white** with no error.
  `PiMaterial.Create` and `PiMaterial.Describe` exist to prevent exactly this.
- **Assigned a new shader after setting a colour** → the assignment resets every
  property, so the colour is silently lost. Set properties *after* the shader.
- **Used `SkeletonBone`** → `bone.transform` does not resolve and `bone == null`
  is rejected at compile time on Unity 2022.3. Read bone positions from a
  `SkinnedMeshRenderer`'s `bones` array, or `Animator.GetBoneTransform`.
- **A GameObject was created outside the scene** → it exists in memory and
  vanishes on reload. Create under an explicit parent you can find again.
- **Wrote to `Assets/` and did not `AssetDatabase.Refresh()`** → the file exists
  on disk but not in the Editor.
- **Destroyed an asset without `AssetDatabase.DeleteAsset`** → the file survives
  and reimports on the next refresh.
- **Ran in Play Mode** → changes vanish when play stops. Check
  `unity_session_context`; `isPlaying` is reported.

## Re-runnability

A module script runs more than once during iteration. Every create must be
paired with a lookup:

```csharp
var go = GameObject.Find("Pi_Lamp");
if (go == null)
{
    go = new GameObject("Pi_Lamp");
    var light = go.AddComponent<Light>();
    light.type = LightType.Point;
}
else if (!go.TryGetComponent<Light>(out _))
{
    go.AddComponent<Light>();
}
```

A script that is not idempotent produces a scene with four lamps after three
runs, and the mistake is attributed to Unity.

## Prefabs and composition

A model that arrived from Blender is a **prefab asset**, not scene geometry, and
the transfer deliberately does not place it. Composing a scene from prefabs is
what `unity_prefab_info` and `unity_instantiate_prefab` are for — prefer them over
a snippet:

```csharp
// not this
var asset = AssetDatabase.LoadAssetAtPath<GameObject>("Assets/PiImported/Chair.prefab");
Object.Instantiate(asset);              // detached copy, asset link lost
PrefabUtility.InstantiatePrefab(asset); // linked instance, overrides reportable
```

`Object.Instantiate` on a prefab produces an object that looks identical and is
disconnected: a later edit to the asset does not reach it, and `PrefabUtility`
cannot report its overrides. If a snippet must instantiate, use
`PrefabUtility.InstantiatePrefab`.

Four things bite in a snippet, all silent:

- **Placing one asset twice makes child names ambiguous.** A prefab's children
  repeat their names in every instance, so `GameObject.Find("Leg")` on the second
  instance hits the first. Every name-taking tool *refuses* an ambiguous name
  rather than guessing. Pass a scene path — `"Chair (2)/Leg"` — not a bare name.
  `unity_instantiate_prefab` returns that path for you.
- **A rename or a reparent on a fresh instance is an override**, and an override
  with nothing recorded as its base is discarded by the next Apply. Call
  `PrefabUtility.RecordPrefabInstancePropertyModifications(transform)`.
- **An asset write needs the ASSET, not the instance.** `GetAssetPath(component)`
  is empty for anything in a scene, so `AssetDatabase.SaveAssetIfDirty(instance)`
  is a no-op that reports success. Use `GetCorrespondingObjectFromSource`.
- **`AssetDatabase.FindAssets("t:Material")` cannot see materials inside a
  model.** They are sub-assets. Use `unity_prefab_info`'s `materials`, which does.

`unity_instantiate_prefab` does all four correctly and returns the child's
inventory plus world bounds, which is the only thing that answers "is this the
right size where I put it".

## Naming objects you cannot predict

Prefab-derived names are rarely unique. Do not assume a name you chose will still
be the name after a second instance:

```csharp
string UniqueName(string wanted)
{
    if (GameObject.Find(wanted) == null) return wanted;
    for (int i = 2; i < 1000; i++)
        if (GameObject.Find($"{wanted} ({i})") == null) return $"{wanted} ({i})";
    return wanted + " (" + Guid.NewGuid().ToString("N")[..6] + ")";
}
```

## Long operations

`AssetDatabase.Refresh`, `EditorUtility.DisplayDialog`, and anything that opens a
modal dialog block the Editor's main thread — the same thread the bridge polls
on. The request will time out while the Editor is fine. Never open a dialog from
a snippet. For real progress, `Debug.Log` and check the Editor's console.

## Cleanup

A snippet should not call `EditorApplication.Exit`, `EditorApplication.quit`, or
discard the open scene's unsaved changes. Those destroy the live session the
user is working in.
