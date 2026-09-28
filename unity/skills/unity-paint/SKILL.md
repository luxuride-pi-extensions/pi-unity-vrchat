---
name: unity-paint
description: Paint textures, author UVs, write vertex colors, add blend shapes, and set up skinning in a Unity project using C# data plus the correct viewport paint modes. Use for texture paint, UV layout, vertex/weight paint, face paint, and sculpt requests.
---

# Unity Paint

Use this skill when the user wants texture paint, UV authoring or layout, vertex
paint, weight paint, face paint, sculpting, blendshape work, or skinning
setup in a Unity project.

Core idea: Unity's paint and UV tools are **interactive UI state**, not
scriptable operations. Reflected over UnityEditor and UnityEngine on 2022.3 and
there is no API for it: no `UnityEditor.PaintMode` enum, no mesh-paint type, and
`UnityEditorInternal.EditMode` exposes only `SceneViewEditMode` (None, Collider,
Cloth, GridPainting) plus an `ownerID`. So **write the result as project data
through C#** — `Texture2D` assets, `Mesh.uv*`, `Mesh.colors`,
`Mesh.AddBlendShapeFrame`, `BoneWeight` — and then **tell the user which mode to
switch to by hand**, because you cannot switch it for them.

Do not promise a mode switch and then silently not do it. The one thing pi can
change in the Editor's tools is the transform tool (`unity_set_tool`: move,
rotate, scale, rect), and that is a different thing entirely.

## Workflow

1. Open or adopt the project with `unity_workspace_init` (`sourceProject` for an
   existing project, which is the normal case) and keep the `workspace`
   argument explicit in every subsequent call. If the project is already open
   in the Editor, `unity_workspace_init { continueExisting: true }` adopts it in
   place.
2. **Budget the first request.** The bridge shares the Editor's main thread with
   asset import and script compilation, so the first call after a launch can
   take 1-3 minutes. Check `unity_bridge_status` first if you are unsure, and
   raise `timeoutSeconds` rather than assuming a hang.
3. Inspect before you touch anything, with `unity_scene_info`:
   - `uv` — per-mesh `uv`/`uv2`/`uv3`/`uv4` presence and counts, blendshape
     names, bone weights.
   - `textures` — every material's texture properties with their asset paths.
   - `materials` — which shader each material uses, and its texture slots.
   - `armatures` — the bone hierarchy with head/tail positions and
     `useDeform`.
   - `paintSettings` — active object, current mode, active material.
4. You are the MANAGER here. Delegate the write to a `unity-worker`; it owns
   one `modules/<name>/script.cs` and runs it with `unity_module_run`, which
   checkpoints the scene, executes, saves, and can write
   `modules/<name>/render.png`. Hand it the real mesh, material, bone, and
   blendshape names from step 3 — never a guessed name.
5. Spawn a **fresh** `unity-critic` on the render. Paint is hard to judge from
   one angle, so a pass that needs several angles costs several critics. The
   critic gets ONE render plus at most ONE reference, so budget accordingly.

## What the worker actually writes

**Texture paint.** A texture in a Unity project is an asset on disk, not an
in-memory buffer you can stroke into. So paint means: get a `Texture2D`,
`SetPixels32` / `SetPixel` / `LoadImage` on it, then encode and write it back
to a real file under `Assets/`, and reimport it.
- Create: `new Texture2D(w, h, TextureFormat.RGBA32, mipmap: true)` →
  `AssetDatabase.CreateAsset(tex, path)` → write → `EditorUtility.SetDirty(tex)` →
  `AssetDatabase.SaveAssets()`.
- Overwrite an existing PNG: read bytes → `LoadImage` → write → encode to PNG
  → `File.WriteAllBytes` → `AssetDatabase.ImportAsset(path,
  ImportAssetOptions.ForceUpdate)`. A `Texture2D` that came from
  `AssetDatabase.LoadAssetAtPath` is **read-only** for pixel writes; you must go
  through a fresh `Texture2D` and write the file, or you get a silent no-op or
  an exception.
- `EncodeToJPG` is lossy and does not carry alpha. Paint to PNG.
- A bulk `SetPixels` is noticeably faster than per-pixel `SetPixel`, and for a
  large texture the difference decides whether your request times out. Prefer
  the array form.
- Mipmaps and `isReadable` matter: a texture the shader reads on the GPU with
  no CPU copy is not readable, and reading it returns garbage. Set
  `isReadable` deliberately and say whether you left it on.

**UV authoring and layout.** UVs are four `Vector2[]` arrays on the mesh:
`uv`, `uv2`, `uv3`, `uv4`. Read `mesh.uv.Length` before writing — a zero-length
array means the mesh has no UV channel at all, and assigning into it does
nothing.
- Unwrap: `Unwrapping.GenerateSecondaryUVSet(mesh)` fills `uv2` and is the
  reliable built-in. It is automatic-layout, not an atlas packer, so it
  overlaps; say so rather than presenting it as a finished layout.
- A second UV set is what lightmaps and the `Secondary UV` slot read. A mesh
  with only `uv` will not receive baked lighting.
- **Keep the mesh marked dirty.** `mesh.MarkDynamic()` is wrong for authored
  data; after editing call `EditorUtility.SetDirty(mesh)` and
  `AssetDatabase.SaveAssets()`, or the edit exists only in memory and vanishes
  on the next domain reload. This is the single most common way painted or
  unwrapped work silently disappears.
- `mesh.uv` writes are an all-or-nothing array swap. Keep the original array,
  mutate, assign back.

**Vertex paint / vertex colors.** `mesh.colors` is `Color[]` and
`mesh.colors32` is `Color32[]` (gamma-safe integers; prefer this for authored
data). Writing `colors` does not populate `colors32` or the reverse — pick one
and say which. Vertex colors do nothing visually unless the **shader** reads
them: lilToon needs its vertex color setting enabled, and a URP/HDRP Lit
material needs a real vertex-color input wired. A mesh with correct color data
and a shader that ignores it looks exactly like a failed paint job — check the
shader before blaming the data.

**Weight paint / skinning.** There is no weight-paint data structure you can
write directly on a `SkinnedMeshRenderer`; weights live per-vertex on the mesh
as a `BoneWeight[]` (`boneIndex0-3` plus `weight0-3`) and are bound to bones
through `skinnedMeshRenderer.bones`, whose order must match `mesh.bindposes`.
The honest scriptable path is a **rebind**: build the `BoneWeight[]` yourself,
assign it, and make sure `bones` and `bindposes` are consistent. Attempting to
"weight paint" a specific vertex by hand through a bridge request is slow and
fragile — if the request is interactive weight painting, say so and let the
user drive it in the viewport you just switched them into.

**Face paint.** `mesh.SetTriangles(submeshIndex, triangles)` writes submesh
assignment. Check `mesh.subMeshCount` first: assigning to an index that does
not exist is an error, and assigning a different triangle count to an existing
submesh silently rewrites what that submesh is. Report the submesh count you
started from.

**Blendshapes.** `mesh.AddBlendShapeFrame(name, frameCount, deltaVertices,
deltaNormals, deltaTangents)`. Frames for one shape are consecutive calls with
the same name, so a 3-frame shape is 3 calls. After adding, confirm with
`unity_scene_info { categories: ["uv"] }` that the name and frame count are
what you intended. A blendshape added to the mesh is not weighted on the
renderer until `SkinnedMeshRenderer.SetBlendShapeWeight(index, value)`.

**Sculpt.** There is no scriptable sculpt brush. Sculpting is interactive
vertex displacement in the viewport. Do not fake it with a code-generated
displacement unless the user asked for a procedural shape, and say which one
you did.

## Tell the user which mode to switch to

Paint modes cannot be set from script on 2022.3 (see the premise above), so the
last step of a paint job is a sentence, not a tool call. Say which mode the user
should select, and what will be visible when they do:

| The user asked for | They switch to | What they then see |
| --- | --- | --- |
| texture paint | Texture Paint | the texture in the inspector, brushable |
| vertex paint | Vertex Paint | per-vertex colour on the mesh |
| weight paint | Weight Paint | bone weights, but only meaningful if the mesh's `BoneWeight[]` order matches the renderer's `bones` — interactive weight painting is a user job |
| face paint | Face Paint | per-face selection in edit mode |
| sculpt | Sculpt | the sculpt brush; there is no scriptable brush, so code cannot stroke it |
| UV work | UV | edit mode with the UV editor, to inspect what you wrote |
| posing / rigging | Pose | the rig, if an ARMATURE object is active |
| rig structure editing | Armature Edit | the bone structure |

Name the object too, when it is not the only candidate. And say plainly if the
result is already complete without them touching a brush — a script that wrote
the UVs does not need a human to lay them out again, and inviting pointless
manual work is its own kind of waste.

## Rules

- **Do not simulate interactive brush strokes.** Write the data, say which
  mode to switch to, and let the user refine.
- **Create or verify the target before writing into it.** A missing texture
  asset, a zero-length UV array, or an absent submesh is the most common cause
  of a paint operation that reports success and does nothing.
- **Save your work explicitly.** `EditorUtility.SetDirty` +
  `AssetDatabase.SaveAssets` after every mesh or texture edit. A paint pass
  that is not saved is a paint pass that did not happen.
- **`AssetDatabase.Refresh()` is required** after writing a file from code
  (`File.WriteAllBytes`, `Texture2D.EncodeToPNG`) that the AssetDatabase has
  never seen. Without it Unity does not import the file and the material keeps
  sampling the old texture with no error.
- **Delete assets with `AssetDatabase.DeleteAsset`, never `Directory.Delete` or
  `File.Delete`.** Removing a file behind Unity's back orphans the `.meta` and
  leaves a broken GUID in every reference to it.
- **Do not add packages to an adopted project.** A paint request does not
  justify a `Packages/manifest.json` edit; if a workflow needs a package, say
  so and let the user decide.
- **One module at a time.** The paint worker, any other module, and the
  critic's render all share one Editor and one scene file behind a
  cross-process lock. Run them in sequence; a parallel spawn queues and then
  fails on its own timeout.
- If the user says painted work "looks wrong", suspect the viewpoint and the
  shader before the pixels. A white or flat-shaded object is very often a
  shader-pipeline mismatch, not a bad texture — see the `unity-edit` skill.
