# unity-vrchat

Live Unity Editor integration for [pi](https://github.com/badlogic/pi), built
for **VRChat projects on Unity 2022.3 LTS** (the version the VRChat SDK pins).
The agent gets `unity_*` tools and a `/unity` command that operate on a *real,
running* Unity Editor — the same design as `pi-blender`, adapted to the fact
that Unity has no scripting console.

This is not a general-purpose Unity extension for current Unity releases: the
bridge and its workarounds are measured against 2022.3, and a future Unity 6
extension would be a separate package.

MIT. Extracted from the `unity-vrchat` 0.1.0 prototype.

---

## Tools

Registered tools (`unity_*`, 24). The full inventory with parameters and
behaviour notes lives in [docs/DESIGN.md](docs/DESIGN.md).

| Tool | What it does |
|---|---|
| `unity_workspace_init` | generate a fresh 3D project or adopt an existing one; opens the Editor |
| `unity_execute_csharp` | run C# live in the Editor from `script.cs` or inline |
| `unity_session_context` | current workspace, selection, mode, last render |
| `unity_scene_info` | scene graph with world+local pos/rot/scale |
| `unity_bones` | humanoid bones by `HumanBodyBones` name + full rig by transform path |
| `unity_inspect` | serialized properties exactly as the Inspector shows them |
| `unity_set_property` | write one serialized property (parsed by type, undoable) |
| `unity_set_transform` | set a GameObject's or rig bone's transform, world/local, undoable |
| `unity_source_models` | find/export real model files from the project |
| `unity_prefab_info` | children/materials/bounds without instantiating |
| `unity_instantiate_prefab` | place a prefab in the scene; returns its scene path |
| `unity_compose_prefab` | build a prefab of prefabs (nested references, never merged) |
| `unity_geometry_probe` | bounds/axis/interpenetration/topology, read-only |
| `unity_vrc` | avatar helpers: package/optimizer readiness, VRCFury, lilToon, bone paths |
| `unity_world` | world helpers (light volumes, Udon, AudioLink, toolkit): 37 actions |
| `unity_render` | render from the camera — how the game looks |
| `unity_capture_view` | capture the Scene view — what you built |
| `unity_save_view` | save a named camera/view |
| `unity_set_tool` | select the active Editor tool |
| `unity_module_run` | run one module's script against a checkpointed scene |
| `unity_module_revert` | restore the scene checkpoint from before a module run |
| `unity_log_critique` | append a scored critique to `critique.log` |
| `unity_bridge_status` | bridge alive/busy/locked and on what |
| `unity_await_compile` | block until a script compile settles, return its verdict |

The `/unity` command engages manager mode over these tools; child agents
(`unity-worker`, `unity-critic`, `unity-diagnostician`, `unity-finalizer`,
`vrchat-unity-avatar`, `vrchat-unity-world`) do the tool-level work.

---

## Install

The released package installs with pi itself:

```bash
pi install npm:@luxuride-pi/unity-vrchat
```

For development from a clone (symlink install):

```bash
ln -sfn "$PWD" ~/.pi/agent/extensions/unity-vrchat
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

## Releases

Every release is both a **git tag** and an npm package. The package publishes
as `@luxuride-pi/unity-vrchat` from the release workflow via npm trusted publishing (OIDC from
GitHub Actions — no publish token exists anywhere).

pi can also install this extension from the **clone**: `installGit()` runs
`git clone`, an optional `git checkout <ref>`, then
`npm install --legacy-peer-deps` inside it, and pi loads the extension's
TypeScript straight from source through jiti. So a tag *is* the artifact for a
git install — there is no build step and nothing to compile.

### Install a release

```bash
pi install npm:@luxuride-pi/unity-vrchat@^<version>
# or, from the clone:
pi install git:github.com/luxuride-pi-extensions/pi-unity-vrchat@v<version>
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
