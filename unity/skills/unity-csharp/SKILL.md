---
name: unity-csharp
description: Write C# that runs inside the live Unity Editor through pi-vibe-unity — entry points, available APIs, and the traps that fail silently.
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

## Long operations

`AssetDatabase.Refresh`, `EditorUtility.DisplayDialog`, and anything that opens a
modal dialog block the Editor's main thread — the same thread the bridge polls
on. The request will time out while the Editor is fine. Never open a dialog from
a snippet. For real progress, `Debug.Log` and check the Editor's console.

## Cleanup

A snippet should not call `EditorApplication.Exit`, `EditorApplication.quit`, or
discard the open scene's unsaved changes. Those destroy the live session the
user is working in.
