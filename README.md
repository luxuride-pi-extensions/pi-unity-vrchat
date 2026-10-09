# pi-unity

Live Unity Editor integration for [pi](https://github.com/badlogic/pi). The agent
gets `unity_*` tools and a `/unity` command that operate on a *real, running*
Unity Editor — the same design as `pi-blender`, adapted to the fact that
Unity has no scripting console.

MIT. Extracted from the `pi-unity` 0.1.0 prototype.

---

## How it differs from pi-blender (read this first)

Blender's bridge is a Python file pi drops into Blender and `exec`s from
`bpy.app.timers`. Unity has **no editor scripting console**, so the live side
here is a **C# editor plugin**, `unity/plugin/`, which pi copies into
`Assets/Editor/PiUnityBridge/` of the project you point it at. It polls the
bridge directory from `EditorApplication.update` and writes responses.

Three consequences you will feel:

1. **The plugin must compile before anything works.** The first time a bridge
   file lands in a project, Unity domain-reloads — budget 10–60 s. Opening an
   existing project for the first time is slower still.
2. **You may need to open the project once by hand.** On a project you have
   never opened, Unity generates `Library/`, resolves packages, and compiles.
   pi can launch the Editor for you, but it cannot skip that.
3. **Executing C# does not use a domain reload.** Snippets are compiled at
   runtime through `Mono.CSharp.AssemblyBuilder` and invoked in a throwaway
   in-memory assembly. This is deliberate: a `PendingFile → AssetDatabase.Refresh`
   trick would either force a slow reload per call or — because the pending file
   lives in the cache dir, not `Assets/` — silently never trigger one.

---

## Install

```bash
ln -sfn "$PWD" ~/.pi/agent/extensions/pi-unity
```

Two things the installer cannot do for you:

- **`node_modules/typebox` is a symlink into pi's own install.** pi bundles
  typebox and `jiti` resolves it from there. If pi's layout changes, repoint:
  ```bash
  ln -sfn "$(dirname "$(readlink -f "$(which pi)")")/../node_modules/typebox" node_modules/typebox
  ```
- **Agent templates are not auto-registered.** `resources_discover` only carries
  `skillPaths` — there is no hook for agent definitions. Copy them yourself:
  ```bash
  cp unity/agents/unity-worker.md ~/.pi/agent/agents/
  cp unity/agents/unity-critic.md ~/.pi/agent/agents/
  ```
  Then set the frontmatter `extensions:` field to the **absolute** path of this
  directory. No `~`, no env vars — that field is not expanded. This is not
  optional: child agents do not inherit ambient extensions, and it is exactly
  what makes `unity_*` available to them.

---

## Configuration

| Variable | Default | Purpose |
| --- | --- | --- |
| `UNITY_PATH` | auto | Editor binary. **Whitespace-split into argv**, so `UNITY_PATH="flatpak run org.unity.Unity"` works without a shell. |
| `UNITY_HUB_PATH` | `~/Unity/Hub` | Hub install root. |
| `PI_UNITY_BRIDGE_DIR` | `$XDG_CACHE_HOME/pi-unity/bridge` | Request/response queue shared with the C# plugin. |
| `PI_UNITY_TMP_DIR` | `$XDG_CACHE_HOME/pi-unity/tmp` | Staging for transfer payloads and generated C# snippets. Kept out of the workspace so a failed import never leaves junk in `Assets/`. |
| `PI_UNITY_NO_UI` | unset | `1` opts out of auto-launching the Editor entirely. Distinct from the no-project gate, which is on by default and needs no env var. |
| `PI_TODO_ENTRY` | `$PI_AGENT_DIR/npm/node_modules/@juicesharp/rpiv-todo/index.ts` | Install-time only. Pin the todo package when the agent templates name `todo` in `tools:` and the package is not in that npm tree; without it a worker is handed a `todo` it can never call. |
| `XDG_CONFIG_HOME` | auto | **Read, not set** — see below. Only consulted when it is unset. |

Editor resolution order: `$UNITY_PATH` → macOS `/Applications/Unity/Hub/Editor`
→ the newest version under `$UNITY_HUB_PATH/Editor/*` → `unity` on `PATH`.

**Why `XDG_CONFIG_HOME` appears in that table.** The Editor is normally launched
by Unity Hub, which on a flatpak install is itself sandboxed, so the Editor
inherits the *sandbox's* config root and finds the licence there
(`~/.var/app/com.unity.UnityHub/config/unity3d/Unity/licenses/`). pi launches the
same binary with the host's config root instead, where there is no licence file,
and the Editor starts with `License is not active (com.unity.editor.ui)`.
`launch.ts` therefore points `XDG_CONFIG_HOME` at the Hub's sandbox config when
the caller has not set it themselves — setting it yourself is respected, because
you knowing better is the more likely case.

**The Editor is never launched at session start, and never without a project.**
`/unity` no longer forces a launch: engaging a session says nothing about which
project to open, so a launch there could only ever open an empty Editor, whose
bridge plugin is not compiled, which never heartbeats and only produces timeouts.
Any `unity_*` call does a lazy launch, but only when a project is KNOWN — a
workspace manifest, which means `unity_workspace_init` has run and pi has a
project path to hand the Editor. With no project, the call reports that fact and
launches nothing; it does not wait out a timeout against an Editor that was never
going to answer. If you never mention Unity, you never get a Unity window, and if
you never open a project, you never get an empty one either.

An Editor **you** launched yourself is always attached to, and never replaced:
the live-bridge check happens before the gate, so a hand-opened project works with
no `unity_workspace_init` at all.

### Agent templates

Six agent templates ship in `unity/agents/`, and one more file is a prompt rather than a template:

| Agent | Role | Has |
| --- | --- | --- |
| `unity-worker` | authors and runs one `modules/<name>/script.cs` | write/edit |
| `unity-critic` | judges how a render LOOKS against the brief, scores it | vision, read-only |
| `unity-diagnostician` | measures live state, returns ranked fixes with exact values | read-only, no vision |
| `unity-finalizer` | one-shot handoff: views, renders, honest completeness check | write, vision |
| `vrchat-unity-avatar` | avatar packages: blendshapes, physbones, VRCFury, upload budget | write/edit |
| `vrchat-unity-world` | world packages: light volumes, Udon, scene descriptor, bake reality | write/edit |

`unity/agents/unity-manager.md` is the eighth file and is **not a template**.
It is a PROMPT, read at runtime by `getManagerPrompt()` in `config.ts` and
injected as a system-prompt section when `/unity` is entered. The `/unity`
command session IS the manager: there is no manager subagent and no router hop,
so the build's task list and progress are visible in the session the user typed
the command into, rather than inside a child they would have to open to watch.
Installing the file would leave a spawnable `unity-manager` whose prompt drifts
from the one the user is actually watching, so `install-agents.mjs` excludes it
and deletes any already-installed copy.

`unity-manager` and `blender-manager` (in the Blender repo) are the two
top-level PROMPTS of their own sides, each entered from its own command
(`/unity`, `/blender`). Each gates itself the way a manager must: no `write` or
`edit` tool, `acceptanceRole: read-only`, and an `allowedAgents` list.

`unity-blender-manager` is a **sibling top-level prompt**, not a parent of
`unity-manager`: `/unity-blender` enters it from the binder repo, it holds both
toolchains and the transfer boundary, and it spawns the Unity and Blender
subagents directly. A task that only touches Unity goes through `/unity`; a
task that crosses between the two applications goes through `/unity-blender`.

**None of them are loaded from this checkout.** An agent template's
`extensions:` field must be an absolute path, and pi-subagents resolves only
`.`/`..` segments in it — never `~`, never an env var. So the committed
templates carry the placeholder `__PI_AGENT_EXTENSION_DIR__` and a script stamps
it at install time:

```bash
node scripts/install-agents.mjs            # dry run: print resolved paths
node scripts/install-agents.mjs --write    # install into $PI_AGENT_DIR/agents
```

The script warns if the extension is not installed under
`$PI_AGENT_DIR/extensions`, because an agent pointing at a missing directory
loads with **no tools at all** and fails in a way that looks like a bug in the
agent rather than a missing extension. `resources_discover` cannot register
agents, so copying them is mandatory, not optional.

The Unity-Blender manager is installed by the **binder's** own
`scripts/install-agents.mjs`, not this one. It needs both extensions on its
`extensions:` line, and two installers racing over one destination file is a
worse failure than a file nobody owns.

### Skills

Eight skills in `unity/skills/`, published through the `resources_discover`
hook: `unity-create`, `unity-edit`, `unity-analyze`, `unity-live-state`,
`unity-csharp`, `unity-modules`, `unity-with-reference`, and `unity-paint`
(textures, UVs, vertex colours, blendshapes, and the honest limits of weight
paint — the mesh's `BoneWeight[]` must match the renderer's bone order, so
interactive weight painting is a user job). The binder ships one more,
`unity-blender-transfer`.

## Development

| Variable | Default | Purpose |
| --- | --- | --- |
| `PI_HOME` | `~/.pi` | Root of the pi installation. |
| `PI_AGENT_DIR` | `$PI_HOME/agent` | Where extensions and agents are installed. |
| `PI_INSTALL_DIR` | `$PI_AGENT_DIR/install` | Used to find the newest pi release, and therefore jiti. |
| `PI_JITI_PATH` | auto | Explicit jiti entry, if the lookup cannot find it. |

The test harnesses need **jiti**, which lives in pi's own install rather than in
this repo, and they load the extensions from their *installed* symlinks on
purpose — the engagement test asserts what a real session loads, not what the
source tree says. `test/paths.mjs` resolves all of the above, preferring the
newest release so a pi upgrade does not silently break the suite. Note that
`node --experimental-strip-types` cannot run these tests at all: the sources use
NodeNext `.js` specifiers, which type stripping cannot resolve.

```bash
./node_modules/typescript/bin/tsc --noEmit   # NOT `npx tsc` — see below
npm test                                     # bridge + engagement suites
bash test/compile-plugin.sh <unity-project>  # offline plugin compile
bash test/json-selftest.sh                  # JSON writer
bash test/roslyn-selftest.sh                # snippet compiler
```

`npx tsc` is a **false pass**: it downloads a decoy `tsc@2.0.4` and reports
success without checking anything. Always use the local binary.

`compile-plugin.sh` needs a real project because it borrows that project's
`Library/Bee/artifacts/*.dag/Assembly-CSharp-Editor.rsp` for its authoritative
reference set. It has four guards against silently compiling the wrong file
list — the failure mode it exists to catch is printing `OK: 0 errors` for a
build that skipped your code.

---

## Tools

| Tool | Does |
| --- | --- |
| `unity_workspace_init` | Create a generated project, or **adopt an existing project in place**. Opens it in the Editor. |
| `unity_execute_csharp` | Run C# in the live Editor from `script.cs` or inline. `saveBefore` / `saveAfter`, `logTail`. Returns `saveRequested` (what you asked for) beside `saved` (whether unsaved scene work actually reached disk), `logs: [{level, message, stack}]` for anything the snippet `Debug.Log`ged, plus `diagnostics` and `stackTrace` when it fails. |
| `unity_session_context` | Live state: open scene, selection, active transform tool, viewport. |
| `unity_scene_info` | Structured read-only inspection by category. Objects carry world + local position, rotation, and scale. |
| `unity_bones` | Where a **bone** is. `humanoidBones` by `HumanBodyBones` name, plus the full rig by transform path. |
| `unity_inspect` | Read a component's **serialized properties** — what the Inspector shows, for any component including VRChat/lilToon/VRCFury. |
| `unity_set_property` | Write one serialized property, parsed by the property's own type. Undoable. |
| `unity_set_transform` | Move / rotate / scale a GameObject or a rig bone, in world or local space. Undoable. |
| `unity_source_models` | Which real model **files** a selection resolves to. `find` reports, `export` copies them out for another application to open verbatim — refusing an `outputDir` inside `Assets/`, and disambiguating two sources whose files share a name. |
| `unity_prefab_info` | What a prefab **asset** contains — children, materials, bounds — read without instantiating it. |
| `unity_instantiate_prefab` | Place a prefab asset into the scene, positioned, in one call. Returns the instance's scene path. |
| `unity_compose_prefab` | Build ONE part-of-the-world prefab whose members are **nested prefab instances** — never merged geometry. Members may themselves be composites, to any depth. Refuses to overwrite an existing composite. |
| `unity_geometry_probe` | **Measure** the scene: world bounds, thin/long axis vs an expected axis, closest-surface distance and interpenetration between a pair, non-manifold/loose/duplicate counts. Read-only, and the Unity counterpart to `blender_geometry_probe`. Read every `state`: a skinned mesh is measured in **bind pose**, `closestSurfaceDistance` is absent rather than `0` when nothing could be measured, and the `topology` walk is exact and uncapped. |
| `unity_vrc` | **VRChat avatar** helpers: package/optimizer readiness, VRCFury, lilToon, bone paths. |
| `unity_world` | **VRChat world** helpers: scene descriptor, Udon inventory, light volumes, build/upload checks, AudioLink / Yama Stream / VRChat Hand Menu, VRWorld Toolkit, EasyQuestSwitch, plus authoring for materials, spawn points, colliders/triggers and audio. 37 actions — `world action=actions` lists them. |
| `unity_render` | Render through a **camera** with the real lighting and materials. |
| `unity_capture_view` | Capture the **Scene view** — the iteration loop's feedback. |
| `unity_save_view` | Save a named view. A saved view *is* a named camera. |
| `unity_set_tool` | Move / Rotate / Scale / Rect transform tool. |
| `unity_module_run` | Run one module's `script.cs` with a per-run checkpoint. |
| `unity_module_revert` | Roll a module back to its checkpoint. |
| `unity_log_critique` | 0–2 × 5 rubric into `critique.log`, plus a view-adequacy judgement. |
| `unity_bridge_status` | Is the bridge alive, busy, or locked, and on what. |
| `unity_await_compile` | **Block until the Editor's script compile settles**, then return the compiler's verdict. Replaces sleeping and re-reading compile status, which is a guess about a 4–6 minute wait. |

**A bone's position is not a GameObject's position.** A VRChat avatar is not
scene geometry: the descriptor sits in a prefab, the rig lives inside it, and
bones are addressed by `HumanBodyBones` name rather than by object path. A
typical avatar has 180+ bones. Use `unity_bones` for the rig and
`unity_set_transform` with `space: "local"` to place a bone relative to its
parent.

**`unity_inspect` uses `SerializedObject`, not field reflection.** That is what
lets it read `VRC_AvatarDescriptor`, lilToon, VRCFury, or a user's own
`MonoBehaviour` — pi cannot know a package's field names ahead of time, and
reflection would miss serialized private fields and `[HideInInspector]` ones. The
walk sees exactly what a human sees in the Inspector.

**A model transferred from Blender is a prefab asset, not scene geometry.** The
transfer deliberately leaves it out of the scene, so composition is an explicit
step: `unity_prefab_info` to see what placing it will add, then
`unity_instantiate_prefab` to place it. Use the returned scene `path` afterwards —
a prefab's children repeat their names in every instance, so a bare name is
refused as ambiguous the moment the asset is placed twice, while `"Chair (2)/Leg"`
resolves exactly. Every tool that takes `gameObject` accepts either form.

**A part-of-the-world is a prefab OF prefabs.** `unity_compose_prefab` builds
one — a house, an island, a furnished room — out of existing prefabs as nested
references, so editing the chair prefab changes the chair in the house in the
island. Nesting is recursive with no depth limit: a member may itself be a
composite. The result re-reads the saved asset and reports `nestedCount` and
`isPrefabOfPrefabs`, so a member that did not land as a reference is named rather
than discovered later — a merged composite looks identical in a render and
silently stops following the models it claims to be built from. Furniture is
never bundled as furniture: a prefab fusing 25 chairs is 25 items nobody can
place separately. To change a composite, delete it and compose it again from the
current members; to change a member, `unity_set_property { space: "asset" }` and
every composite that references it follows.

**`unity_render` vs `unity_capture_view` is the single most important
distinction.** `unity_render` tells you how the game will look from a camera.
`unity_capture_view` tells you what you built. Criticising lighting from a Scene
view, or judging composition from a camera render, both produce confidently
wrong feedback.

---

## Workspaces

A workspace is a **Unity project directory** plus a `unity-workspace.json`
manifest beside it.

```
<workspace>/
  Assets/  Packages/  ProjectSettings/     ← a real Unity project
  unity-workspace.json                    ← manifest: iterations, modules, views
  script.cs                               ← the C# you edit
  critique.log
  iteration_01/  iteration_02/ …          ← renders, scene-info.json, checkpoints
  modules/<name>/script.cs
```

Two ways in:

- **Generate** — a fresh 3D project from `unity/project-template/`. Shipped with
  a `Main.unity` scene and a 3D-capable `manifest.json`. Note the template
  contains **no** `ProjectSettings/`: hand-writing those YAML files is a trap,
  and Unity generates valid defaults on first open.
- **Adopt** — point at a project you already have, including a configured VRChat
  world or a real game. pi installs `Assets/Editor/PiUnityBridge/` and a
  `unity-bridge-dir.txt` marker, and **never writes scene YAML**. In an adopted
  project, do not add packages: it is your project, and a resolver failure there
  is your outage.

The `unity-bridge-dir.txt` marker is what lets a **hand-launched** Editor find
the bridge. Without it, the plugin is inert: it resolves its directory from
`$PI_UNITY_BRIDGE_DIR` or that marker, and returns without polling if neither
works. The Editor logging to stdout will not break your launch.

---

## Modular work

```
unity_create ... → call subagents_enable first
  Manager
    ├─ unity-worker (module "body")
    └─ unity-worker (module "rig")
  critic
```

Each module owns its objects, writes an idempotent-by-construction script, and
never touches another module's files. `unity_module_run` checkpoints the scene
to `modules/<name>/runs/NN/pre-NN.unity` **before** running.

**What a revert actually restores:** the *scene file*. Writes under `Assets/`
are not rolled back, and `unity_module_revert` says so in its result. This is
inherent — Unity scenes and assets are separate concerns, and a text-level
rollback of `Assets/` would be worse than useless. If a module created assets,
its script must clean them up itself.

---

## Known limits

- **Verified live** for: `unity_workspace_init` (adopt + generate),
  `unity_execute_csharp`, `unity_scene_info`, `unity_bones`, `unity_inspect`,
  `unity_set_property`, `unity_set_transform`, `unity_session_context`, the
  read-only `vrc` / `world` actions, and these mutating `world` actions:
  - `create-light-volume` (including that a fractional `range` / `intensity`
    survives the round trip, and that it reuses an existing component instead
    of stacking a second one, and that a second create gets a unique name
    rather than silently shadowing the first under the same bare name);
  - `convert-light` (a Unity light becomes a light volume, keeping its own
    colour, range and intensity, refusing an area light, and disabling rather
    than destroying the original);
  - `create-behaviour` (adds a verified world behaviour, sets fields in the
    same call, reports a bad field without losing the good ones, and refuses to
    add a second copy of a behaviour already on the object);
  - `build-checks` / `upload-risk` / `performance` / `environment` /
    `udon-inventory` (the scene-review reports);
  - `audio-link-set` (end to end on a freshly created AudioLink, including the
    proxy→Udon copy — see the UdonSharp note below);
  - `toolkit-state`, `setup-layers`, `setup-post-processing`, `toggle-tag`,
    `quest-switch`, `quest-add`, `quest-apply` — the last exercised against a
    scene where the entry had to be created, since the package's own window is
    the only other thing that can create it.
  - `materials` / `fix-android-shaders` (a material swapped onto an
    Android-safe shader with its colour, texture, tiling, metallic and
    glossiness carried across — **the swap is written to the `.mat` asset on
    disk**, so it is not undone by a scene revert);
  - `spawn-points` / `add-spawn-point` (growing `descriptor.spawns` and
    reading the array back to confirm the count);
  - `colliders` / `add-collider` / `add-trigger` (including that the action
    refuses rather than crashing on an abstract trigger type — see the
    UdonSharp/abstract note below);
  - `audio` / `add-audio-source` / `set-clip-load-type` (the last verified by
    re-reading the importer after `ImportAsset`, which is the only way to tell a
    real persistence from a reflected write that looks successful).
  Offline suites cover the request framing, the cross-process lock, timeouts,
  and the JSON writer (106 assertions in the bridge suite).
- **`unity_await_compile` is verified live in all three compile shapes**, and
  the three are different code paths in the plugin:
  - a compile the Editor builds no assembly for (nothing changed on disk) →
    `compiled`;
  - a compile that fails → `failed` with the compiler's own
    `file(line,col): error CSxxxx:` lines;
  - a real build that succeeds and reloads the domain → `compiled`.
  The wait settles on a capture file the plugin writes, because
  `assemblyCompilationFinished` fires **only for an assembly Unity actually
  builds** — a no-op compile raises neither assembly event, so a wait keyed to
  one always times out in a clean project. `compilationStarted` /
  `compilationFinished` both carry a `Bee.BeeDriver.ActiveBuild` and no
  messages, and `compilationFinished` is itself lost to the domain reload of a
  successful build, which is why the plugin's poll closes the cycle instead of
  trusting the event.
- **The Editor's heartbeat cannot be used to detect a compile.** `Bridge.Poll`
  touches it on every tick, so it moves straight through a compile; and during
  a compile the poll does not run at all, so the heartbeat goes silent for
  minutes. Both were measured. The wait therefore does not use it — it is only
  *reported*, as `editorResponding`, so a caller can tell a settled wait from an
  Editor that has gone away.
- **`scriptsNeedingCompile` is a TIMESTAMP proxy, and cannot be a compiler
  statement.** It used to compare each script against its output assembly's
  mtime, which is unsound on 2022.3: compilation goes through Bee, which keys
  the build on **content hashes**, so touching a file without changing a byte
  rebuilds no assembly and leaves the output's mtime where it was — measured
  live as a script reported as "never compiled" after a build that had just
  succeeded with no errors, and would stay that way forever. The reference is
  now when the last compile cycle the plugin watched ENDED; output timestamps
  remain only as a fallback for a project the plugin has not seen compile. The
  proxy is also not consulted once the compiler has spoken: a capture naming
  the assembly it built means the compiler ran and had its say, and a stale
  `needsCompile` no longer turns a green build into `failed`.
- **Never exercised live:** `unity_module_run` / `unity_module_revert` with a
  real revert, `unity_capture_view` against a non-trivial Scene view, and the
  mutating `vrc` actions (`ensure-trace-and-optimize`, `vrcfury-add`). They
  compile and their read paths are tested; treat the first real use as the
  integration test.
- **A UdonSharp component added by plain `AddComponent` is a half-built
  object.** UdonSharp keeps a proxy MonoBehaviour paired with the
  `UdonBehaviour` its program actually compiles to, and only the package's own
  editor extension creates that pair — the Inspector's "Add Component" and
  `UdonSharpComponentExtensions.AddUdonSharpComponent` both call
  `RunBehaviourSetup`; nothing else does. A proxy with no backing
  `UdonBehaviour` makes `UdonSharpEditorUtility.CopyProxyToUdon` throw
  `ArgumentNullException` from its formatter, and **every field write to it is
  then discarded at build time**, silently. `create-behaviour` routes through
  the package's add path and reports when it did; the `-set` actions check for
  the backing behaviour first and say so when it is missing rather than
  claiming the value was copied. `CreateBehaviourForProxy` is not a
  workaround — it just calls the same copy.
- **`build-checks` never invokes the SDK's build callbacks.** VRChat's own
  `VRCBuildPipelineCallbacks.OnVRCSDKBuildRequested` is public and callable,
  and VRWorld Toolkit's callback opens a modal
  `EditorUtility.DisplayDialogComplex` when the scene descriptor has no spawn
  points or holds duplicate `PipelineManager` components — a bridge request
  would hang the Editor on a dialog nobody can click. So `build-checks` drives
  `VRC.SDKBase.Validation.ValidationUtils` / `WorldValidation` directly and
  reimplements those two checks read-only.
- **A whitelist read needs the non-public binding flags.** The SDK's
  component-type whitelists are non-public static `string[]` fields on
  `VRC.SDKBase.Validation.WorldValidation` (`ComponentTypeWhiteListCommon` and
  friends), and `ValidationUtils.GetCachedWhitelistedTypes` only binds with a
  by-ref `out HashSet<Type>` and returns false for every name the SDK knows —
  the names come from an SDK config resource, not from string literals. A
  public-only read degrades the illegal-component check to "not available",
  which is the honest outcome rather than a false pass.
- **`Enum.ToObject` does not take a name.** It accepts a numeric value or an
  enum value and throws `ArgumentException: The value passed in must be an enum
  base or an underlying type for an enum` for a string, which reads like a type
  mismatch rather than the wrong overload. `Enum.Parse` is the one that takes a
  name. Anything driven by a caller-supplied enum name goes through
  `PiAudio.ParseEnum`, which matches case-insensitively and lists the real
  members when the name is wrong.
- **`VRC_Trigger` is abstract in the current SDK.** `VRC.SDKBase.VRC_Trigger`
  and both `VRCInteractable` types are abstract, and
  `GameObject.AddComponent(<abstract type>)` returns null *silently* instead of
  throwing — which is how `add-trigger` first failed with a bare
  `NullReferenceException`. The action now checks `IsAbstract` and says that the
  trigger behaviour is UdonSharp in this SDK (with the collider as its volume)
  rather than crashing.
- **`Undo.RecordObject` after an `AddComponent` is not an undo step.** Measured
  on Unity 2022.3, reverting with `Undo.RevertAllDownToGroup`:

  | pattern | Ctrl-Z removes it? |
  | --- | --- |
  | `Undo.AddComponent(go, typeof(C))` | **yes** |
  | `AddComponent(C)` then `Undo.RecordObject(go)` | no |
  | `AddComponent(C)` then `Undo.RegisterCompleteObjectUndo(go)` | no |
  | `Undo.RecordObject(go)` then `AddComponent(C)` | no |

  `RecordObject` snapshots state as it is *now*, so every after-the-fact variant
  records a state that already contains the component; and a plain-`AddComponent`'d
  component is not part of the GameObject's undo state at all. Every `add-` action
  therefore adds through `Undo.AddComponent`, and records the *component* before
  its field writes, because a record on the GameObject captures none of them.
- **A `Undo` claim in a report is only worth what was measured.** The undo line
  each action prints used to be the *intent* ("recorded as one undo step")
  rather than the outcome, which is why a whole set of them was wrong. If you
  see `undo: NOT registered`, the operation happened but nothing will take it
  back for you.
- **Euler angles wrap at 360.** Asking for a rotation of `0,-45,0` puts it back
  on the transform as `0,315,0`, and a raw triple comparison calls a correctly
  placed object a mismatch. `SameRotation` compares with `Mathf.DeltaAngle` and
  the report says so in words when the two triples are one rotation.
- **A clone written to `Assets/` is not imported yet.** Dropping a `.wav` into
  the project directory does not make it findable: `FindAssets("t:AudioClip")`
  and `LoadAssetAtPath` both miss it until an
  `AssetDatabase.Refresh(ForceSynchronousImport)` has run. An action that
  reports "was not found as a clip asset" right after a file appeared is
  usually this, not a bad name.
- **Compile errors are read from the Editor log, not from the compilation
  event.** `CompilationPipeline.compilationFinished` hands over a plain
  `object` -- in 2022.3.22f1 it is the Bee build driver -- and Unity exposes no
  `CompilationContext.GetMessages`, so an event-driven reader cannot see a
  single diagnostic. `compile-status` and `unity_world action=build-checks`
  instead parse the tail of `Editor.log` for `error CS####` lines, and
  `build-checks` **fails** on any of them. Three details make that honest:
  - Unity writes each error to the log several times, so lines are deduped to
    the newest per source file.
  - The log survives the fix, so a stale error would be reported against code
    that now compiles. An error is therefore dropped when its file is OLDER
    than the assembly that would contain it (Unity only rewrites an assembly
    when its compile succeeds) or when the file no longer exists. A missing
    assembly keeps the error live, which is the safe direction.
  - A project whose EVERY script is broken never rewrites its assemblies and
    then goes quiet, so an empty error list would read as success.
    `scriptsNeedingCompile` lists any `Assets/**/*.cs` newer than
    `Assembly-CSharp.dll`, and `build-checks` fails on that too.
- **A UdonSharp script needs a program asset, and writing the `.cs` is not
  enough.** `UdonSharpEditorUtility.GetUdonSharpProgramAsset` builds its lookup
  by *scanning program assets that already exist*, so a script you just wrote
  has no program until a `.asset` sits beside it — and
  `UdonSharpComponentExtensions.AddUdonSharpComponent` then throws a bare
  `NullReferenceException` from inside the package, several frames down and
  naming nothing. `create-behaviour` now creates that asset
  (`PiUdon.EnsureProgramAsset`) and names it in its report. The asset type is
  **`UdonSharp.UdonSharpProgramAsset`**, not the `UdonSharpEditor.*` namespace
  its own source file suggests. Watch for a stale `ZoneAudioVolume.asset`-style
  orphan in `git status` after deleting a script: the `.cs` and the `.asset`
  are two files and deleting one leaves the other.
- **Writing a UdonSharp proxy's field is not the same as setting the value the
  program runs with.** The proxy carries its own copy of every field, so
  `create-behaviour { fields: "taps=7" }` used to write the proxy and nothing
  else — and since `udon-inventory` reads the *UdonBehaviour's* variable table,
  the value read back as absent, which looks exactly like a lost write.
  `create-behaviour` now copies the proxy across after writing fields, and
  `PiPackages.CopyProxyToUdon` is shared with the `audio-link-set` / `yama-set` /
  `hand-menu-set` actions rather than duplicated. Saving the scene in the Editor
  normally does the same job, so if you see this warning the copy failed for a
  reason UdonSharp chose not to explain.
- **A full script compile in this project takes 4-6 minutes**, so a bridge call
  that asks for a compile and then waits will time out. Request the compile in
  one short call, then check `compile-status` in another; do not poll from
  inside a single C# snippet, because the Editor cannot answer you mid-compile
  and the call simply dies at the tool timeout.
- **Licensing.** Unity is installed under a **flatpak Hub** on this machine, so
  the licence lives in the sandbox at
  `~/.var/app/com.unity.UnityHub/config/unity3d/Unity/licenses/`, not in the
  host `~/.config/unity3d/`. pi must spawn the Editor with that `XDG_CONFIG_HOME`
  or it starts unlicensed (`License is not active (com.unity.editor.ui)`);
  `launch.ts` does this automatically. The legacy Pro entitlement is *not*
  granted, so Pro features may fail while Personal works.
- **Snippets compile with Roslyn, not Mono.CSharp.** Unity removed the Mono C#
  compiler in 2021.2, so the old `Mono.CSharp.AssemblyBuilder` path throws
  `Value cannot be null.  Parameter name: type` on 2022.3. The plugin now loads
  `Data/Tools/ScriptUpdater/Microsoft.CodeAnalysis.CSharp.dll` (3.11) by
  reflection — the plugin compiles into the predefined `Assembly-CSharp-Editor`,
  which has no Roslyn reference, and adding one would force an asmdef into the
  user's project. References are the union of every assembly loaded in the
  current AppDomain plus `Library/ScriptAssemblies/*.dll`, which is how
  VRCFury, lilToon, and VRChat SDK types stay visible to a snippet.
- **The plugin must be compiled, not just present.** Merely opening a project
  never installs it — only `unity_workspace_init` does. The plugin also stays
  deliberately inert unless it can resolve a bridge directory (from
  `PI_UNITY_BRIDGE_DIR`, else the project's `unity-bridge-dir.txt` marker).
  After a source change you must wait for a real recompile; a DLL that still
  shows its old timestamp means the Editor has not rebuilt, so any "test" run
  against it is invalid.
- **`SceneView.RenderAndCapture` is the capture path.** It is solid but
  deprecated-ish; a real game camera is a better final check.
- **Long operations block the Editor's main thread**, and therefore the bridge.
  A 60 s bake looks identical to a dead Editor from the outside. Bridge
  timeouts are deliberately generous (`300 s` for transfer work) and the
  timeout message says to check status rather than to blindly retry.
- **One live session, no ordering guarantee.** Concurrent `unity_*` calls
  interleave mutations. There is a cross-process disk lock before each request
  precisely because of this.
- **Some projects crash the Editor on this machine, and it is not the plugin.**
  The ALCOM `My World` project SIGSEGVs in
  `CustomRenderTextureManager::InitCustomRenderTextures`, backtraced through
  Mesa `dri_invalidate_drawable` / `dri2_destroy_image` → `ApiGLES::CreateFramebuffer`
  → `RenderTexture::SetActive`. That is a DRI3 driver path (AMD RX 7900 XTX,
  radeonsi/navi31) triggered by the project's `CustomRenderTexture` assets
  (`Assets/USharpVideo/Scripts/Utility/RenderTextureOutput.asset`,
  `Assets/_pi_/_LTCGI-Adapters/LTCGI_USharpVideoAdapter.asset`). Chiffon runs for
  hours with the identical plugin and has zero CRT assets. The real log is at
  `~/.var/app/com.unity.UnityHub/config/unity3d/Editor.log` — the host
  `~/.config/unity3d/Editor.log` is a months-old decoy. Changing the graphics API
  or disabling the LTCGI/USharpVideo CRT components is the user’s call.
- **A missing workspace is not a missing bridge.** The live Editor is a single
  global session, so `unity_session_context` answers from the bridge even when
  the current directory has no `unity-workspace.json`. It reports that state as a
  workspace-management fact, not as a bridge failure.

## Releases

Every release is a **git tag**, not a published npm package. All four packages
in this organization are `"private": true`, so `npm publish` refuses them, and
unsetting that flag to get a publish through would trade a correct manifest for
a working script.

pi installs this extension from the **clone**, not from a tarball: `installGit()`
runs `git clone`, an optional `git checkout <ref>`, then
`npm install --legacy-peer-deps` inside it, and pi loads the extension's
TypeScript straight from source through jiti. So a tag *is* the artifact — there
is no build step and nothing to compile.

### Install a release

```bash
pi install git:github.com/luxuride-pi-extensions/pi-unity@v<version>
```

The `@ref` is what pins the version; without it pi tracks the default branch, so
an unpinned spec is not a release, it is a moving target.

### Cut a release

1. Bump `version` in `package.json` and commit that on its own.
2. Tag the commit that contains the bump — the tag and the version must agree:
   ```bash
   git tag v<version> && git push origin master --tags
   ```
3. `.github/workflows/release.yml` packs the tarball, checks it, and publishes
   the release with generated notes.

Step 2 is the one that is easy to get wrong, so the workflow refuses to guess:
it fails when the tag is not on the commit being released, and again when the
tag does not match `package.json`'s `version`. Bump the version *before*
tagging; the tag is not the place to bump.

The `npm pack --dry-run` check in the release workflow is deliberately a copy of
what `pi-manager-core`'s `test/package-contents.mjs` proves on every CI run. A
release workflow that borrowed the check from another repository would depend on
that repository being checked out at the right ref, which is a moving part a
release should not have.
