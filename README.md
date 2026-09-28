# pi-vibe-unity

Live Unity Editor integration for [pi](https://github.com/badlogic/pi). The agent
gets `unity_*` tools and a `/unity` command that operate on a *real, running*
Unity Editor — the same design as `pi-vibe-blender`, adapted to the fact that
Unity has no scripting console.

MIT. Extracted from the `vibe-unity` 0.1.0 prototype.

---

## How it differs from pi-vibe-blender (read this first)

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
ln -sfn "$PWD" ~/.pi/agent/extensions/vibe-unity
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
| `PI_UNITY_BRIDGE_DIR` | `$XDG_CACHE_HOME/vibe-unity/bridge` | Request/response queue shared with the C# plugin. |
| `PI_UNITY_TMP_DIR` | `$XDG_CACHE_HOME/vibe-unity/tmp` | Staging for transfer payloads and generated C# snippets. Kept out of the workspace so a failed import never leaves junk in `Assets/`. |
| `VIBE_UNITY_NO_UNITY` | unset | `1` opts out of auto-launching the Editor. |
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

**The Editor is never launched at session start.** Like the Blender extension,
Unity launches lazily on first engagement: `/unity` forces a launch, any
`unity_*` call does a lazy launch. If you never mention Unity, you never get a
Unity window.

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
| `unity_execute_csharp` | Run C# in the live Editor from `script.cs` or inline. `saveBefore` / `saveAfter`. |
| `unity_session_context` | Live state: open scene, selection, active transform tool, viewport. |
| `unity_scene_info` | Structured read-only inspection by category. Objects carry world + local position, rotation, and scale. |
| `unity_bones` | Where a **bone** is. `humanoidBones` by `HumanBodyBones` name, plus the full rig by transform path. |
| `unity_inspect` | Read a component's **serialized properties** — what the Inspector shows, for any component including VRChat/lilToon/VRCFury. |
| `unity_set_property` | Write one serialized property, parsed by the property's own type. Undoable. |
| `unity_set_transform` | Move / rotate / scale a GameObject or a rig bone, in world or local space. Undoable. |
| `unity_source_models` | Which real model **files** a selection resolves to. `find` reports, `export` copies them out for another application to open verbatim. |
| `unity_vrc` | **VRChat avatar** helpers: package/optimizer readiness, VRCFury, lilToon, bone paths. |
| `unity_world` | **VRChat world** helpers: light volumes, Udon, scene descriptor. |
| `unity_render` | Render through a **camera** with the real lighting and materials. |
| `unity_capture_view` | Capture the **Scene view** — the iteration loop's feedback. |
| `unity_save_view` | Save a named view. A saved view *is* a named camera. |
| `unity_set_tool` | Move / Rotate / Scale / Rect transform tool. |
| `unity_module_run` | Run one module's `script.cs` with a per-run checkpoint. |
| `unity_module_revert` | Roll a module back to its checkpoint. |
| `unity_log_critique` | 0–2 × 5 rubric into `critique.log`, plus a view-adequacy judgement. |
| `unity_bridge_status` | Is the bridge alive, busy, or locked, and on what. |

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
  `unity_set_property`, `unity_set_transform`, `unity_session_context`, and the
  read-only `vrc` / `world` actions. Offline suites cover the request framing,
  the cross-process lock, timeouts, and the JSON writer (43 assertions/tests).
- **Never exercised live:** `unity_module_run` / `unity_module_revert` with a
  real revert, `unity_capture_view` against a non-trivial Scene view, and every
  mutating `vrc` / `world` action (`ensure-trace-and-optimize`, `vrcfury-add`,
  `create-light-volume`). They compile and their read paths are tested; treat
  the first real use as the integration test.
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
