# pi-unity-vrchat

Pi extension: live Unity Editor integration for **VRChat projects on Unity
2022.3 LTS** (the version the VRChat SDK pins). The agent gets `unity_*` tools
and a `/unity` command that operate on a *real, running* Unity Editor.

This is not a general-purpose Unity extension for current Unity releases: the
bridge and its workarounds are measured against 2022.3, and a future Unity 6
extension would be a separate package.

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

## Install

```bash
pi install npm:@luxuride-pi/unity-vrchat
```

The child agents above must also exist in `$PI_AGENT_DIR/agents`. The
extension ships its agent templates, so install them once from the installed
copy:

```bash
node scripts/install-agents.mjs            # dry run: print resolved paths
node scripts/install-agents.mjs --write    # install into $PI_AGENT_DIR/agents
```

The Unity Editor is autodetected (`UNITY_PATH`, macOS app bundle, newest
install under the Unity Hub, `PATH`). Set `UNITY_PATH` if yours lives
elsewhere.

Usage, workspace semantics, environment variables and per-tool behaviour:
[docs/DESIGN.md](docs/DESIGN.md).

## Releases

Every release is both a **git tag** and an npm package. The package publishes
as `@luxuride-pi/unity-vrchat` from the release workflow via npm trusted
publishing (OIDC from GitHub Actions — no publish token exists anywhere).

pi can also install this extension from a **git ref**: pi clones, checks out
the ref, runs `npm install --legacy-peer-deps`, and loads the extension's
TypeScript straight from source — so a tag *is* the artifact; there is no
build step and nothing to compile.

### Install a release

```bash
pi install npm:@luxuride-pi/unity-vrchat@^<version>
# or, pinned to a git tag:
pi install git:github.com/luxuride-pi-extensions/pi-unity-vrchat@v<version>
```

### Cut a release

1. Bump `version` in `package.json` and commit that on its own.
2. Tag the commit that contains the bump — the tag and the version must agree:
   ```bash
   git tag v<version> && git push origin master --tags
   ```
3. `.github/workflows/release.yml` packs the tarball, checks it, and publishes
   the release with generated notes.

The workflow refuses to guess: it fails when the tag is not on the commit being
released, and again when the tag does not match `package.json`'s `version`.
Bump the version *before* tagging; the tag is not the place to bump.

## License

MIT
