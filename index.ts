/**
 * pi extension: Unity integration.
 *
 * Registers 19 unity_* tools + bundled skills, the /unity slash command, and
 * injects the manager prompt + live Editor state before turns that engage
 * the extension.
 *
 * The /unity command session IS the manager: there is no manager subagent, so
 * the user watches the build's todos and progress in the session they typed the
 * command into. The manager prompt lives in unity/agents/unity-manager.md and
 * is read at runtime (config.ts getManagerPrompt) rather than installed as a
 * spawnable agent, so a second, drifting copy cannot exist.
 *
 * The Unity Editor is NOT launched at pi/session start, and unlike Blender it
 * cannot be launched to answer a question alone: the bridge is a compiled C#
 * editor plugin (unity/plugin/) that is copied into a workspace's
 * Assets/Editor/ folder, so the first request against a project costs an editor
 * boot plus a compile. The live-state block injected below is what keeps that
 * cost from turning into a sequence of blind timeouts.
 */

import { Type } from "typebox";
import type { ExtensionAPI } from "@earendil-works/pi-coding-agent";
import {
	assertCameraExists,
	getBundledUnitySkillsDir,
	normalizeWorkspacePath,
	requestLiveUnityBones,
	requestLiveUnityInspect,
	requestLiveUnitySetProperty,
	requestLiveUnitySetTransform,
	requestLiveUnityFindSourceModels,
	requestLiveUnityExportSourceModels,
	requestLiveUnityGeometryProbe,
	requestLiveUnityInstantiatePrefab,
	requestLiveUnityPrefabInfo,
	unityCaptureView,
	requestLiveUnityVrc,
	requestLiveUnityWorld,
	requestLiveUnityAwaitCompile,
	unityBridgeStatus,
	unityExecuteCSharp,
	unityLogCritique,
	unityModuleRevert,
	unityModuleRun,
	unityRender,
	unitySaveView,
	getSceneInfo,
	unitySessionContext,
	unitySetTool,
	unityWorkspaceInit,
	UNITY_SCENE_INFO_CATEGORIES,
	UNITY_TOOLS,
} from "./unity/runtime.js";
import { getManagerPrompt } from "./config.js";
import { loadManagerCore } from "./manager-core.js";

/**
 * The shared manager core, or this extension's own copy of it. See
 * manager-core.ts for the resolution order and why it is a runtime lookup.
 */
const core = loadManagerCore();

const LIVE_CONTEXT_PROMPT_TIMEOUT_MS = 4_000;

/**
 * The agents this manager may spawn.
 *
 * These are the names in `allowedAgents:` in unity/agents/unity-manager.md
 * (line 49) minus the manager itself, which is the one asking. That line is
 * what pi offers the session; this set is what the gate enforces, so the two
 * must be edited together — test/agent-rosters.mjs compares them.
 *
 * `vrchat-unity-avatar` and `vrchat-unity-world` are in the list because for a
 * VRChat world this manager delegates to a world specialist rather than the
 * generic worker, and a roster that omitted them would block the only correct
 * delegation available.
 */
const UNITY_MANAGER_ROSTER = new Set([
	"unity-worker",
	"unity-critic",
	"unity-diagnostician",
	"unity-finalizer",
	"unity-reference-describer",
	"vrchat-unity-avatar",
	"vrchat-unity-world",
]);

/**
 * Manager mode, entered ONLY by the /unity command.
 *
 * A unity_* tool call does NOT enter it. `markUsed()` turns on the per-turn
 * guidance injection, which is right for any turn that touches Unity; the gate
 * stays down, because a session that was never handed a task is not a manager
 * and must keep its `write` and `edit`. Before the split, probing
 * `unity_bridge_status` silently cost a session both for the rest of it, with
 * no way back but a slash command.
 *
 * The mechanism — the engaged flag, the tool_call gate, and the exemption for a
 * subagent inside a manager session — is the shared core's, because all three
 * manager extensions had a private copy of it and they drifted. The refusals
 * are attached here as a policy callback: they name a Unity worker and a Unity
 * critic, so they cannot live in a file that does not know about Unity.
 */
const managerMode = core.createManagerMode({
	allowedTools: [
		// Delegation, supervision, and the two halves of waiting: bg_wait
		// blocks on this session's own async runs, subagent_supervisor answers
		// a child that needs a decision. Both belong to pi-subagents.
		"subagent",
		"subagents_enable",
		"bg_wait",
		"subagent_supervisor",
		"contact_supervisor",
		// Planning and reading agent definitions to write a brief with.
		"todo",
		"read",
		"grep",
		"find",
		"ls",
		"ask_user_question",
		// Unity inspection. Read-only: the manager may look, not author.
		"unity_workspace_init",
		"unity_scene_info",
		"unity_session_context",
		"unity_bridge_status",
		"unity_bones",
		"unity_inspect",
		"unity_vrc",
		"unity_world",
		"unity_source_models",
		"unity_module_revert",
		"unity_log_critique",
		"unity_prefab_info",
		"unity_geometry_probe",
	],
	// The roster's RULE is the core's; only the names are ours. This manager had
	// no roster at all, so it could spawn any agent in the session — including a
	// writer that walks straight past the write/edit refusal above.
	agentRoster: [...UNITY_MANAGER_ROSTER],
	onGateMessage: (event) => {
		if (event.toolName === "write" || event.toolName === "edit") {
			return {
				block: true,
				reason:
					"Manager role: do not author files. This session IS the manager — you were not handed a job to relay — and the subagents you spawn are the ones that do the work. Authoring Unity C# is `unity-worker`'s job (or `vrchat-unity-avatar` / `vrchat-unity-world` for those projects): plan the modules, then spawn one per module with a compact brief (goal, module name, GameObject names, acceptance criteria) and read only the reports that come back. A direct approach is a dead end: your `write`/`edit` is blocked for the rest of this manager mode. If the file is unrelated to Unity — project files, a config, a script of your own — say so and ask the user to run /unity-exit, which lifts this gate.",
			};
		}
		return null;
	},
});

/**
 * The per-turn injection: the constant manager prompt as a system section, and
 * the live snapshot as a message.
 *
 * The ordering and the three rules are the shared core's — why a constant prompt
 * belongs in a section, why a child must not receive the parent's snapshot, and
 * why a failed fetch must say so loudly instead of injecting nothing. What stays
 * here is the Unity half: the 4s budget, the state categories, and the fact
 * that Unity has a second unavailable branch the core has no word for — a
 * snapshot that comes back successfully but with no context in it.
 */
const injectUnityContext = core.createContextInjection({
	section: "unity",
	customType: "unity_context",
	timeoutMs: LIVE_CONTEXT_PROMPT_TIMEOUT_MS,
	shouldInject: () => managerMode.isEngaged(),
	getPrompt: getManagerPrompt,
	// Unity is slower to answer than Blender: the bridge shares the Editor's
	// main thread with import and compile work, so it gets a wider prompt-timeout
	// budget. The explicit withTimeout is kept as well as the core's own: this
	// one rejects a HUNG bridge, and the core's message reports it.
	fetchState: (ctx) => withTimeout(unitySessionContext({ cwd: (ctx as { cwd: string }).cwd }), LIVE_CONTEXT_PROMPT_TIMEOUT_MS),
	formatState: (live) => {
		const result = live as Awaited<ReturnType<typeof unitySessionContext>>;
		const context = result.context;
		// A snapshot with no context in it is not a failure the core can see:
		// the call resolved, and the shape is an absent field. The agent used to
		// read an empty string as "nothing to inject" and then assume it knew
		// the selection, so the unavailable notice is built here instead.
		return context
			? [
					"Authoritative live Unity state for this turn:",
					`- project: ${context.projectPath}`,
					`- unity: ${context.unityVersion}${context.isCompiling ? " (COMPILING)" : ""}${context.isPlaying ? " (PLAY MODE)" : ""}`,
					`- open scene: ${context.scene.name} (${context.scene.isLoaded ? "loaded" : "not loaded"})`,
					`- available scenes: ${context.scene.availableScenes.join(", ") || "(none)"}`,
					`- selection: ${context.selection.count > 0 ? context.selection.gameObjects.join(", ") : "(nothing selected)"}`,
					`- active object: ${context.selection.activeGameObject || "(none)"}`,
					`- active transform tool: ${context.tools.activeTransformTool}`,
					`- scene view: ${context.viewport.hasSceneView ? `${context.viewport.perspective ?? "?"} (camera ${context.viewport.cameraName ?? "?"})` : "NOT VISIBLE"}`,
					result.note,
				]
					.filter((line) => line.length > 0)
					.join("\n")
			: [
					"Live Unity state is UNAVAILABLE for this turn.",
					"Do NOT assume any selection, active object, open scene, or camera state from earlier turns.",
					`${result.note}`,
					"Call `unity_session_context` before mutating anything, and ask the user if it also fails.",
				].join("\n");
	},
	// "open scene" is not one of the core's four defaults, so the list of what
	// must not be assumed is stated per app rather than fixed in the core.
	assumeNothingAbout: "selection, active object, open scene, or camera state",
	failureGuidance: () => "Call `unity_session_context` before mutating anything, and ask the user if it also fails.",
});

function formatJsonResult(value: unknown): string {
	return JSON.stringify(value, null, 2);
}

/**
 * Reject with a clear message instead of leaving a hanging promise, so a stuck
 * Editor shows up as "did not answer in N ms" rather than as a stalled turn.
 */
function withTimeout<T>(promise: Promise<T>, timeoutMs: number): Promise<T> {
	return new Promise<T>((resolvePromise, rejectPromise) => {
		const timer = setTimeout(
			() => rejectPromise(new Error(`no response within ${timeoutMs} ms`)),
			timeoutMs,
		);
		promise
			.then((value) => {
				clearTimeout(timer);
				resolvePromise(value);
			})
			.catch((error: unknown) => {
				clearTimeout(timer);
				rejectPromise(error);
			});
	});
}

export default function piUnityExtension(pi: ExtensionAPI) {
	pi.registerTool({
		name: "unity_workspace_init",
		label: "Unity Workspace Init",
		description:
			"Create or adopt a managed Unity workspace: either generate a fresh project from the bundled 3D template, copy an existing project in, or adopt a project in place (e.g. a configured VRChat world or an existing game). Installs the pi bridge plugin into Assets/Editor/ and auto-opens the project in the live Unity Editor when available.",
		parameters: Type.Object({
			workspace: Type.Optional(
				Type.String({
					description: "Explicit workspace path. Relative paths resolve from the current working directory.",
				}),
			),
			template: Type.Optional(
				Type.String({
					description: 'Workspace template for generated projects: "3d" (default) or "blank" (minimal package set).',
					default: "3d",
				}),
			),
			sourceProject: Type.Optional(
				Type.String({
					description:
						"Path to an existing Unity project. When the workspace already is that project, it is adopted in place instead of copied.",
				}),
			),
			continueExisting: Type.Optional(
				Type.Boolean({
					description: "When true, require the workspace to exist already instead of creating a new one.",
					default: false,
				}),
			),
		}),
		execute: async (_toolCallId, params, _signal, _onUpdate, ctx) => {
			// Injection is a statement of INTENT, not of success: it is set
			// before any work, so a tool that throws (bad argument, no editor)
			// still leaves the guidance flowing for the next turn. It does NOT
			// raise the manager gate -- only /unity does that, because calling a
			// tool is not a delegation.
			managerMode.markUsed();
			const result = await unityWorkspaceInit({
				workspacePath: normalizeWorkspacePath(ctx.cwd, params.workspace),
				template: params.template as "3d" | "blank" | undefined,
				sourceProject: params.sourceProject,
				continueExisting: params.continueExisting,
			});
			return { content: [{ type: "text", text: formatJsonResult(result) }], details: result };
		},
	});

	pi.registerTool({
		name: "unity_execute_csharp",
		label: "Unity Execute C#",
		description:
			"Run a C# snippet inside the live Unity Editor. The code is compiled in-memory and invoked (no domain reload, no recompile), so it can create GameObjects, materials, lights, and assets, then optionally save the scene. Use script_path for the workspace script, or inlineCode for a short snippet.",
		parameters: Type.Object({
			workspace: Type.Optional(
				Type.String({ description: "Explicit workspace path. Relative paths resolve from the current working directory." }),
			),
			scriptPath: Type.Optional(
				Type.String({
					description: "Path to a .cs file. Defaults to the workspace root script.cs when omitted.",
				}),
			),
			inlineCode: Type.Optional(
				Type.String({
					description: "Inline C# source, used instead of scriptPath. Must expose a static entry point (PiScript.Run, PiEntryPoint.Run, or any static Run/Main/Execute).",
				}),
			),
			saveAfter: Type.Optional(
				Type.Boolean({
					description: "Save the open scene after the code runs successfully.",
					default: true,
				}),
			),
			saveBefore: Type.Optional(
				Type.Boolean({
					description: "Snapshot the scene file to <workspace>/pre-execute.unity before running, so a bad script can be undone.",
					default: false,
				}),
			),
			timeoutSeconds: Type.Optional(
				Type.Number({ description: "Explicit timeout in seconds. Defaults to 120.", default: 120 }),
			),
		}),
		execute: async (_toolCallId, params, signal, _onUpdate, ctx) => {
			// Injection is a statement of INTENT, not of success: it is set
			// before any work, so a tool that throws (bad argument, no editor)
			// still leaves the guidance flowing for the next turn. It does NOT
			// raise the manager gate -- only /unity does that, because calling a
			// tool is not a delegation.
			managerMode.markUsed();
			const result = await unityExecuteCSharp({
				cwd: ctx.cwd,
				workspace: params.workspace,
				scriptPath: params.scriptPath,
				inlineCode: params.inlineCode,
				saveAfter: params.saveAfter,
				saveBefore: params.saveBefore,
				timeoutSeconds: params.timeoutSeconds,
				signal,
			});
			return { content: [{ type: "text", text: formatJsonResult(result) }], details: result };
		},
	});

	pi.registerTool({
		name: "unity_session_context",
		label: "Unity Session Context",
		description:
			"Read the live Unity Editor state: open project and scene, selection, active object, active camera, and the current scene view. Call this before acting on 'it', 'this', the selected object, or the current view.",
		parameters: Type.Object({
			workspace: Type.Optional(
				Type.String({ description: "Explicit workspace path. Relative paths resolve from the current working directory." }),
			),
		}),
		execute: async (_toolCallId, params, _signal, _onUpdate, ctx) => {
			// Injection is a statement of INTENT, not of success: it is set
			// before any work, so a tool that throws (bad argument, no editor)
			// still leaves the guidance flowing for the next turn. It does NOT
			// raise the manager gate -- only /unity does that, because calling a
			// tool is not a delegation.
			managerMode.markUsed();
			const result = await unitySessionContext({ cwd: ctx.cwd, workspace: params.workspace });
			return { content: [{ type: "text", text: formatJsonResult(result) }], details: result };
		},
	});

	pi.registerTool({
		name: "unity_scene_info",
		label: "Unity Scene Info",
		description:
			"Inspect the live Unity scene and project. Narrow it with `categories` instead of dumping everything: " +
			`${UNITY_SCENE_INFO_CATEGORIES.join(", ")}. The default omits packages and assets, which are slow and large. ` +
			"`compileStatus` is the one worth reading before you believe a build: `errors` carries the compiler's own `error CS…` lines, " +
			"and `scriptsNeedingCompile` lists scripts written after the last compile the Editor finished -- both empty means the code on disk is compiled. " +
			"Do not sleep and re-read this to wait out a compile; unity_await_compile blocks until the Editor settles and returns the verdict.",
		parameters: Type.Object({
			workspace: Type.Optional(
				Type.String({ description: "Explicit workspace path. Relative paths resolve from the current working directory." }),
			),
			categories: Type.Optional(
				Type.Array(Type.Union(UNITY_SCENE_INFO_CATEGORIES.map((category) => Type.Literal(category))), {
					description: "Sections to inspect. Omit for the default set.",
				}),
			),
		}),
		execute: async (_toolCallId, params, signal, _onUpdate, ctx) => {
			// Injection is a statement of INTENT, not of success: it is set
			// before any work, so a tool that throws (bad argument, no editor)
			// still leaves the guidance flowing for the next turn. It does NOT
			// raise the manager gate -- only /unity does that, because calling a
			// tool is not a delegation.
			managerMode.markUsed();
			const result = await getSceneInfo({
				cwd: ctx.cwd,
				workspace: params.workspace,
				categories: params.categories as never,
				signal,
			});
			return { content: [{ type: "text", text: formatJsonResult(result) }], details: result };
		},
	});

	pi.registerTool({
		name: "unity_bones",
		label: "Unity Bones",
		description:
			"Where is a bone? For a rigged avatar, a bone's position is not the GameObject's: the rig lives inside the prefab and is addressed by Humanoid bone name. " +
			"Returns humanoidBones (keyed by HumanBodyBones name: Head, Hips, LeftHand, ...) and the complete rig keyed by transform path, each with world and local position. " +
			"A typical avatar has 180+ bones, so pass `bones` to filter instead of dumping the whole rig. " +
			"Omit gameObject to auto-detect the avatar root from the scene.",
		parameters: Type.Object({
			workspace: Type.Optional(
				Type.String({ description: "Explicit workspace path. Relative paths resolve from the current working directory." }),
			),
			gameObject: Type.Optional(
				Type.String({
					description:
						"Avatar root GameObject name. Omit to use the first object in the scene with an Animator or SkinnedMeshRenderer.",
				}),
			),
			bones: Type.Optional(
				Type.Array(Type.String(), {
					description:
						"Case-insensitive substrings to keep, e.g. [\"LeftHand\"] or [\"Head\",\"Hips\"]. Matches both the Humanoid enum name and the transform name. Omit for the full rig.",
				}),
			),
			limit: Type.Optional(
				Type.Number({ description: "Maximum bones to return from the full rig list. Defaults to 200.", minimum: 1 }),
			),
		}),
		execute: async (_toolCallId, params, _signal, _onUpdate, _ctx) => {
			// Injection is a statement of INTENT, not of success: it is set
			// before any work, so a tool that throws (bad argument, no editor)
			// still leaves the guidance flowing for the next turn. It does NOT
			// raise the manager gate -- only /unity does that, because calling a
			// tool is not a delegation.
			managerMode.markUsed();
			const result = await requestLiveUnityBones({
				gameObject: params.gameObject,
				bones: params.bones,
				limit: params.limit,
			});
			return { content: [{ type: "text", text: formatJsonResult(result) }], details: result };
		},
	});

	pi.registerTool({
		name: "unity_source_models",
		label: "Unity Source Models",
		description:
			"Which real model FILES on disk does this selection resolve to? This is the read-only half of the Unity->Blender handoff. " +
			"mode:\"find\" (default) reports without copying anything; mode:\"export\" copies each resolved file into outputDir so another application can open it verbatim. " +
			"Copying beats re-exporting: an FBX carries the rig and the material description that a re-exported OBJ flattens away, and a file has no sub-mesh selection, so a file with 6 sub-meshes travels whole even if you named one. " +
			"Meshes with NO file behind them (Unity's built-in primitives, procedural geometry) come back under unhandoffableNames -- and unhandoffableCounts says how many objects share each name, because a name is not a unique identifier and exporting one of five identically-named objects while reporting all five is a lie. " +
			"Omit gameObjectNames to cover every scene root.",
		parameters: Type.Object({
			workspace: Type.Optional(
				Type.String({ description: "Explicit workspace path. Relative paths resolve from the current working directory." }),
			),
			gameObjectNames: Type.Optional(
				Type.Array(Type.String(), {
					description:
						"GameObject names to resolve. Omit or pass [] to cover every scene root, which is how an entire avatar is resolved at once.",
				}),
			),
			mode: Type.Optional(
				Type.Union([Type.Literal("find"), Type.Literal("export")], {
					description:
						"\"find\" (default) reports what would travel. \"export\" copies the files to outputDir.",
					default: "find",
				}),
			),
			outputDir: Type.Optional(
				Type.String({
					description:
						'Required for mode:"export". The destination directory for the copied model files. Put it in a scratch/cache location, never inside the project.',
				}),
			),
		}),
		execute: async (_toolCallId, params, _signal, _onUpdate, _ctx) => {
			// Injection is a statement of INTENT, not of success: it is set
			// before any work, so a tool that throws (bad argument, no editor)
			// still leaves the guidance flowing for the next turn. It does NOT
			// raise the manager gate -- only /unity does that, because calling a
			// tool is not a delegation.
			managerMode.markUsed();
			const names = params.gameObjectNames ?? [];
			const result =
				params.mode === "export"
					? await requestLiveUnityExportSourceModels({
							outputDir: params.outputDir ?? "",
							gameObjectNames: names,
						})
					: await requestLiveUnityFindSourceModels({ gameObjectNames: names });
			return { content: [{ type: "text", text: formatJsonResult(result) }], details: result };
		},
	});

	pi.registerTool({
		name: "unity_prefab_info",
		label: "Unity Prefab Info",
		description:
			"What does this prefab ASSET contain, read WITHOUT instantiating it? Call this BEFORE placing one: it is the difference between knowing a prefab's contents and discovering them in a scene you then have to clean up. " +
			"Returns the child hierarchy (names, scene paths, components, mesh vertex counts), every material the prefab references, and the bounds its geometry occupies. " +
			"CRITICAL for transferred models: `materials` includes materials EMBEDDED in the source FBX, which unity_scene_info { categories: [\"materials\"] } cannot see -- FindAssets(\"t:Material\") skips material sub-assets, so for a model that arrived from Blender an empty materials list there is NOT evidence the materials were lost. " +
			"Read `bounds.state` before trusting `bounds.size`: a skinned renderer's bounds volume can read far larger than the mesh at rest, and the state says so rather than leaving you to measure it yourself. " +
			"prefabPath is ASSET-RELATIVE (\"Assets/...\", not absolute) -- get real paths from unity_scene_info { categories: [\"assets\"] }.",
		parameters: Type.Object({
			workspace: Type.Optional(
				Type.String({ description: "Explicit workspace path. Relative paths resolve from the current working directory." }),
			),
			prefabPath: Type.String({
				description: 'Asset-relative prefab path, e.g. "Assets/PiImported/Chair.prefab". Not an absolute path.',
			}),
		}),
		execute: async (_toolCallId, params, _signal, _onUpdate, _ctx) => {
			const result = await requestLiveUnityPrefabInfo({ prefabPath: params.prefabPath });
			return { content: [{ type: "text", text: formatJsonResult(result) }], details: result };
		},
	});

	pi.registerTool({
		name: "unity_instantiate_prefab",
		label: "Unity Instantiate Prefab",
		description:
			"Place a prefab asset into the active scene, positioned, in ONE call. This is the composition step: a transferred model arrives as a prefab asset (unity_blender_transfer creates it but deliberately does NOT add it to the scene), and this is what actually puts it in. " +
			"Instantiate and place are one call on purpose. Split across two, you would have to name the object you just created -- and a prefab's children repeat their names in every instance, so the moment you place one twice a bare name is ambiguous and every name-based tool refuses it rather than guessing. " +
			"The returned `path` is the stable handle: pass it to unity_inspect / unity_set_transform / unity_set_property, which accept a scene path (\"Parent/Child\") as well as a bare name. " +
			"Use PrefabUtility.InstantiatePrefab semantics, so the instance stays LINKED to the asset: later edits to the prefab propagate, and PrefabUtility can report overrides. The name is made unique automatically (Chair, Chair (2), ...) when the asset is placed more than once. " +
			"Pass `parent` to nest under an existing object. Writes to an instance become prefab OVERRIDES -- recorded via RecordPrefabInstancePropertyModifications, so they survive, but an override has no recorded base if you edit a component this tool did not touch.",
		parameters: Type.Object({
			workspace: Type.Optional(
				Type.String({ description: "Explicit workspace path. Relative paths resolve from the current working directory." }),
			),
			prefabPath: Type.String({
				description: 'Asset-relative prefab path, e.g. "Assets/PiImported/Chair.prefab". Not an absolute path.',
			}),
			parent: Type.Optional(
				Type.String({
					description:
						"Scene object to parent under, by name or scene path. Omit to place at the scene root.",
				}),
			),
			name: Type.Optional(
				Type.String({
					description:
						"Rename the instance. Omit to keep the prefab's name, made unique if the scene already has one.",
				}),
			),
			position: Type.Optional(Type.String({ description: 'World position as "x,y,z". Default "0,0,0".' })),
			rotation: Type.Optional(Type.String({ description: 'World rotation in Euler DEGREES as "x,y,z". Default "0,0,0".' })),
			scale: Type.Optional(Type.String({ description: 'LOCAL scale as "x,y,z". Default "1,1,1".' })),
		}),
		execute: async (_toolCallId, params, _signal, _onUpdate, _ctx) => {
			const result = await requestLiveUnityInstantiatePrefab({
				prefabPath: params.prefabPath,
				parent: params.parent,
				name: params.name,
				position: params.position,
				rotation: params.rotation,
				scale: params.scale,
			});
			return { content: [{ type: "text", text: formatJsonResult(result) }], details: result };
		},
	});

	pi.registerTool({
		name: "unity_geometry_probe",
		label: "Unity Geometry Probe",
		description:
			"Measure mesh/transform relationships in the live scene and return NUMBERS instead of guesses: world-space bounds, the object's own thin/long axis and how far it sits off an expected one, closest-surface distance between two placed objects with an interpenetrating/in-contact/separated verdict, and non-manifold/loose/duplicate geometry counts. " +
			"READ-ONLY: it never mutates, never dirties or saves the scene, never writes an asset, and claims no iteration, so it is safe to call while a worker has a module mid-flight. " +
			"What unity_scene_info cannot tell you: it reports only hasMesh/hasRenderer booleans per object, so a MIRRORED object (a negative scale component, which looks correct and has inverted normals and winding) is invisible there and is reported here as transform.mirrorScale. " +
			"READ EVERY `state` FIELD before trusting a number -- several figures are approximations and say which: a skinned renderer's bounds volume can read much larger than the mesh at rest, the axis is derived from the mesh's LOCAL bounding box (exact only for an axis-aligned mesh), and a pair's 'inside' test is only meaningful on a CLOSED mesh. " +
			"Omit `objects` to probe every active mesh root, which is usually right: a misplaced part is found by its relationship to the rest, and the culprit is often not yet known. `pairs` takes precedence over `objects` for that one family. " +
			"An object name is resolved exactly -- a bare name two scene roots both contain is REFUSED, and a scene path (\"Chair (2)/Leg\") always works. A missing mesh, missing renderer or empty mesh is reported as a number with a `state`, never as an error.",
		parameters: Type.Object({
			workspace: Type.Optional(
				Type.String({ description: "Explicit workspace path. Relative paths resolve from the current working directory." }),
			),
			objects: Type.Optional(
				Type.Array(Type.String(), {
					description:
						'Object names or scene paths to measure. Omit to probe every active mesh root in the scene. A bare name that is ambiguous is refused rather than guessed.',
				}),
			),
			pairs: Type.Optional(
				Type.Array(Type.Array(Type.String(), { minItems: 2, maxItems: 2 }), {
					description:
						'Object-name pairs to measure against each other, e.g. [["Mug", "MugHandle"]]. Each pair yields the closest surface distance, penetration depth, per-object vertex-inside counts, bounding-box overlap and a verdict. Takes precedence over `objects` for this family; omit to auto-pair when exactly two objects are given.',
				}),
			),
			checks: Type.Optional(
				Type.Array(
					Type.Union([
						Type.Literal("transform"),
						Type.Literal("bounds"),
						Type.Literal("axis"),
						Type.Literal("pair"),
						Type.Literal("shape"),
						Type.Literal("topology"),
					]),
					{
						description:
							"Measurement families to run. Omit for all. `transform` catches unapplied and MIRROR scale; `bounds` is the world-space volume each object occupies; `axis` is the object's own thin/long axis and its angle to `expectedAxis`; `shape` reports self-symmetry, convexity and open edges -- the defects a render cannot show; `pair` is the gap or overlap between two placed objects; `topology` is the expensive one (a pass over every triangle of every named mesh) and is only needed when the shape itself is suspect.",
						uniqueItems: true,
					},
				),
			),
			tolerance: Type.Optional(
				Type.Number({
					description:
						"Contact distance in metres (default 0.002, i.e. 2 mm). A pair whose closest sampled surfaces are within tolerance reports as `in-contact` rather than `separated`.",
					minimum: 0,
				}),
			),
			expectedAxis: Type.Optional(
				Type.Union([Type.Literal("X"), Type.Literal("Y"), Type.Literal("Z")], {
					description:
						"The world axis a part's thin direction should align with -- `Y` for a mug handle's ring axis, `Z` for a vertical stem. Defaults to Z; the returned angles are against this axis.",
				}),
			),
			timeoutMs: Type.Optional(
				Type.Number({
					description:
						"Bridge timeout override in ms. Raise it for a `topology` check on a heavy scene -- a probe scales with mesh size -- and lower it when probing a small object list and you want a dead bridge to report sooner.",
				}),
			),
		}),
		execute: async (_toolCallId, params, _signal, _onUpdate, _ctx) => {
			// Injection is a statement of INTENT, not of success: it is set
			// before any work, so a tool that throws (bad argument, no editor)
			// still leaves the guidance flowing for the next turn. It does NOT
			// raise the manager gate -- only /unity does that, because calling a
			// tool is not a delegation.
			managerMode.markUsed();
			const result = await requestLiveUnityGeometryProbe({
				objects: params.objects,
				pairs: params.pairs,
				checks: params.checks,
				tolerance: params.tolerance,
				expectedAxis: params.expectedAxis,
				timeoutMs: params.timeoutMs,
			});
			return { content: [{ type: "text", text: formatJsonResult(result) }], details: result };
		},
	});

	pi.registerTool({
		name: "unity_inspect",
		label: "Unity Inspect",
		description:
			"Read a GameObject's transform, components, and serialized properties -- what the Unity Inspector shows. " +
			"Works for any component, including ones from packages pi has never heard of (VRC_AvatarDescriptor, lilToon, VRCFury, a user's own MonoBehaviour), because it walks SerializedObject rather than reading fields by name. " +
			"Pass `component` to dump one component's properties in full; the error message lists the real property paths when a name does not match.",
		parameters: Type.Object({
			workspace: Type.Optional(
				Type.String({ description: "Explicit workspace path. Relative paths resolve from the current working directory." }),
			),
			gameObject: Type.String({
				description:
					'GameObject name in the active scene, or a scene path ("Chair (2)/Leg") when the name alone is ambiguous -- which is the normal state for a prefab placed more than once, since a prefab\'s children repeat their names in every instance. A bare name that two scene roots both contain is REFUSED rather than guessed. Call unity_scene_info for the list of roots.',
			}),
			component: Type.Optional(
				Type.String({ description: "Component short or full type name to dump in full, e.g. \"VRC_AvatarDescriptor\"." }),
			),
			includeChildren: Type.Optional(
				Type.Boolean({ description: "Also list immediate children with their local positions. Defaults to false." }),
			),
		}),
		execute: async (_toolCallId, params, _signal, _onUpdate, _ctx) => {
			// Injection is a statement of INTENT, not of success: it is set
			// before any work, so a tool that throws (bad argument, no editor)
			// still leaves the guidance flowing for the next turn. It does NOT
			// raise the manager gate -- only /unity does that, because calling a
			// tool is not a delegation.
			managerMode.markUsed();
			const result = await requestLiveUnityInspect({
				gameObject: params.gameObject,
				component: params.component,
				includeChildComponents: params.includeChildren,
			});
			return { content: [{ type: "text", text: formatJsonResult(result) }], details: result };
		},
	});

	pi.registerTool({
		name: "unity_set_property",
		label: "Unity Set Property",
		description:
			"Write one serialized property on a component, exactly as the Inspector would. The value is parsed according to the property's own type, so \"1\" writes a number to a Float and text to a String. " +
			"Use a property path from unity_inspect. Vectors accept \"1,2,3\"; enums accept the display name or the index; object references accept \"Type:Name\" or \"none\" to clear. " +
			"Every write is undoable and, for a scene object, marks the scene dirty without saving to disk.",
		parameters: Type.Object({
			workspace: Type.Optional(
				Type.String({ description: "Explicit workspace path. Relative paths resolve from the current working directory." }),
			),
			gameObject: Type.String({ description: "GameObject name in the active scene." }),
			component: Type.String({ description: "Component short or full type name, e.g. \"VRC_AvatarDescriptor\"." }),
			property: Type.String({ description: "Serialized property path from unity_inspect, e.g. \"m_Name\" or \"m_CustomRenderQueue\"." }),
			value: Type.String({ description: "New value, as a string. Parsed according to the property type." }),
			space: Type.Optional(
				Type.Union([Type.Literal("scene"), Type.Literal("asset"), Type.Literal("global")], {
					description:
						"Where the write lands. \"scene\" (default) marks the scene dirty without touching the .unity file. \"asset\"/\"global\" saves the asset immediately. Use \"asset\" for an edit to a prefab asset rather than a scene instance.",
					default: "scene",
				}),
			),
		}),
		execute: async (_toolCallId, params, _signal, _onUpdate, _ctx) => {
			// Injection is a statement of INTENT, not of success: it is set
			// before any work, so a tool that throws (bad argument, no editor)
			// still leaves the guidance flowing for the next turn. It does NOT
			// raise the manager gate -- only /unity does that, because calling a
			// tool is not a delegation.
			managerMode.markUsed();
			const result = await requestLiveUnitySetProperty({
				gameObject: params.gameObject,
				component: params.component,
				property: params.property,
				value: params.value,
				space: params.space,
			});
			return { content: [{ type: "text", text: formatJsonResult(result) }], details: result };
		},
	});

	pi.registerTool({
		name: "unity_set_transform",
		label: "Unity Set Transform",
		description:
			"Move, rotate, or scale a GameObject. Use space:\"local\" for a parented object or to move a rig bone relative to its parent; space:\"world\" (default) for absolute placement. " +
			"Undoable, and marks the scene dirty without writing to disk. Reads as the companion to the transform data unity_scene_info returns.",
		parameters: Type.Object({
			workspace: Type.Optional(
				Type.String({ description: "Explicit workspace path. Relative paths resolve from the current working directory." }),
			),
			gameObject: Type.String({
				description:
					'GameObject name in the active scene, or a scene path ("Chair (2)/Leg") when the name alone is ambiguous -- which is the normal state for a prefab placed more than once, since a prefab\'s children repeat their names in every instance. A bare name that two scene roots both contain is REFUSED rather than guessed, because silently inspecting the wrong object is worse than being told to be specific. Every tool that takes gameObject accepts both forms.',
			}),
			position: Type.Optional(Type.String({ description: "New position as \"x,y,z\"." })),
			rotationEuler: Type.Optional(Type.String({ description: "New rotation as euler degrees \"x,y,z\"." })),
			scale: Type.Optional(Type.String({ description: "New local scale as \"x,y,z\"." })),
			space: Type.Optional(
				Type.Union([Type.Literal("world"), Type.Literal("local")], {
					description: "Interpret position/rotation in world or local space. Defaults to \"world\".",
					default: "world",
				}),
			),
		}),
		execute: async (_toolCallId, params, _signal, _onUpdate, _ctx) => {
			// Injection is a statement of INTENT, not of success: it is set
			// before any work, so a tool that throws (bad argument, no editor)
			// still leaves the guidance flowing for the next turn. It does NOT
			// raise the manager gate -- only /unity does that, because calling a
			// tool is not a delegation.
			managerMode.markUsed();
			const result = await requestLiveUnitySetTransform({
				gameObject: params.gameObject,
				position: params.position,
				rotationEuler: params.rotationEuler,
				scale: params.scale,
				space: params.space,
			});
			return { content: [{ type: "text", text: formatJsonResult(result) }], details: result };
		},
	});

	pi.registerTool({
		name: "unity_vrc",
		label: "Unity VRChat (avatar)",
		description:
			"VRChat AVATAR project helpers. Start with action \"project\": it reports which packages are installed, which avatars are in the scene, " +
			"and whether the avatar is upload-ready. Actions: project, avatar, optimizer, ensure-trace-and-optimize, vrcfury-list, vrcfury-add, " +
			"liltoon-usage, liltoon-material, path, actions. Reports are prose, because the findings that matter are ones like " +
			"\"TraceAndOptimize is MISSING -- this avatar uploads unoptimized\". Not for world projects; use unity_world there.",
		parameters: Type.Object({
			workspace: Type.Optional(
				Type.String({ description: "Explicit workspace path. Relative paths resolve from the current working directory." }),
			),
			action: Type.String({
				description:
					"Which helper to run. project = packages + avatars + optimizer readiness. avatar = one avatar in detail. " +
					"optimizer = vertex counts against VRChat's 70000 limit and which optimizer components are present. " +
					"vrcfury-list = addable VRCFury elements. vrcfury-add = add one (e.g. armature-link, toggle, socket, full-controller). " +
					"liltoon-usage / liltoon-material = lilToon shader inspection. path = a bone's transform path. " +
					"actions = the list. An unknown action returns the known ones, so a typo self-corrects.",
			}),
			gameObject: Type.Optional(Type.String({ description: "Avatar root GameObject name. Omit to use the scene's only avatar." })),
			element: Type.Optional(
				Type.String({
					description:
						"vrcfury-add only: which element to add -- armature-link, full-controller, toggle, or socket.",
				}),
			),
			bone: Type.Optional(
				Type.String({
					description:
						"vrcfury-add only: bone name. A bone targets the BONE, not the avatar root, which is the most common VRCFury mistake.",
				}),
			),
			materialPath: Type.Optional(Type.String({ description: "liltoon-material only: material asset path." })),
			child: Type.Optional(Type.String({ description: "path only: a bone or child name to resolve under the avatar." })),
		}),
		execute: async (_toolCallId, params, _signal, _onUpdate, _ctx) => {
			// Injection is a statement of INTENT, not of success: it is set
			// before any work, so a tool that throws (bad argument, no editor)
			// still leaves the guidance flowing for the next turn. It does NOT
			// raise the manager gate -- only /unity does that, because calling a
			// tool is not a delegation.
			managerMode.markUsed();
			const result = await requestLiveUnityVrc({
				action: params.action,
				gameObject: params.gameObject,
				element: params.element,
				bone: params.bone,
				materialPath: params.materialPath,
				child: params.child,
			});
			return { content: [{ type: "text", text: formatJsonResult(result) }], details: result };
		},
	});

	pi.registerTool({
		name: "unity_world",
		label: "Unity VRChat (world)",
		description:
			"VRChat WORLD project helpers: light volumes, Udon, the scene descriptor, upload validation, VRWorld Toolkit, " +
			"EasyQuestSwitch, AudioLink, YamaPlayer and VRCHandMenu. " +
			"Start with action \"scene\", which reports " +
			"packages, the scene descriptor (by its real resolved type), Udon counts, world behaviours, and the light volume setup in one call. " +
			"Actions: scene, descriptor, light-volumes, create-light-volume, convert-light, udon, udon-inventory, find-behaviour, create-behaviour, " +
			"toolkit-state, setup-layers, setup-post-processing, toggle-tag, " +
			"quest-switch, quest-add, quest-apply, " +
			"build-checks, upload-risk, performance, environment, " +
			"audio-link, audio-link-set, yama, yama-set, hand-menu, hand-menu-set, " +
			"materials, fix-android-shaders, spawn-points, add-spawn-point, " +
			"colliders, add-collider, add-trigger, audio, add-audio-source, set-clip-load-type, actions. " +
			"Light volumes bake at BUILD time, so a new one changes nothing on screen until you build. " +
			"build-checks runs VRChat's own validators WITHOUT uploading, and never triggers the modal dialogs the SDK's build callbacks open. " +
			"quest-apply switches a world's PC/Quest values WITHOUT changing the project's active build target, and leaves the scene unsaved. " +
			"Not for avatar projects; use unity_vrc there.",
		parameters: Type.Object({
			workspace: Type.Optional(
				Type.String({ description: "Explicit workspace path. Relative paths resolve from the current working directory." }),
			),
			action: Type.String({
				description:
					"scene = the full picture (packages, descriptor, Udon, behaviours, light volumes). descriptor = the VRC_SceneDescriptor. " +
					"light-volumes = every point light volume with its range, intensity, colour and shape, plus the bake settings. " +
					"create-light-volume = add one. udon = UdonBehaviour counts, and why writing new UdonSharp SOURCE is a human job. " +
					"find-behaviour = resolve a world behaviour by name and report the real type. " +
					"toolkit-state = VRWorld Toolkit's own project facts: VRChat layers and collision matrix, " +
					"post-processing wiring on the reference camera, and every scene material on a shader that is not Android-safe. " +
					"setup-layers = set up the VRChat layers and collision matrix (the SDK's own Builder setup). " +
					"setup-post-processing = add the post process layer, profile and volume to the reference camera. " +
					"toggle-tag = enable or disable every object carrying a tag. " +
					"quest-switch = EasyQuestSwitch state: the switch set, and per entry which PC/Quest values differ. " +
					"quest-add = add a scene object or asset to the switch set, creating the EQS_DATA holder if the scene has none. " +
					"Add a component (component: \"Light\") when you want that component's per-platform values, not just the GameObject's. " +
					"quest-apply = apply pc or android values without switching the project's build target. " +
					"build-checks = run VRChat's own upload validation without uploading: descriptor and spawn points, illegal " +
					"components and shaders against the SDK whitelists, EditorOnly-tagged objects, Udon compile errors. " +
					"upload-risk = what makes an upload REJECTED or force a rebuild: asset sizes against VRChat's limits, audio " +
					"clip load types and lengths, texture sizes and Android compression, unconfigured world behaviours. " +
					"udon-inventory = every UdonBehaviour with its proxy script, sync method, program size and public variables. " +
					"performance = a scene census that PREDICTS the build rating (objects, renderers, meshes, materials, " +
					"lights, particles, textures, audio, lightmaps) and names the biggest levers. Every figure is a proxy; " +
					"VRChat computes the rank at build time. " +
					"environment = how the world looks: skybox, ambient, fog, directional light, the reference camera and " +
					"its post-processing layer, and what this scene does NOT configure. " +
					"convert-light = turn a Unity Light into a light volume, reading the light's own type, range, intensity and colour. " +
					"create-behaviour = add a VRChat world behaviour to an object, optionally setting fields in the same call. " +
					"audio-link = every AudioLink component with its fields; audio-link-set = set one field on one. " +
					"yama = every YamaPlayer component; yama-set = set one field on one. " +
					"hand-menu = every VRCHandMenu component; hand-menu-set = set one field on one. " +
					"materials = every material the scene's renderers use, with its shader, the Quest verdict against the " +
					"whitelist, every texture slot (including slots with nothing assigned), and its colour. " +
					"fix-android-shaders = swap one material to a Quest-safe shader, carrying the main colour, main texture, " +
					"tiling/offset and metallic/glossiness across, and reporting every property that could NOT be carried. " +
					"spawn-points = every spawn with its world position, plus the descriptor's spawn settings. " +
					"add-spawn-point = append one to the descriptor's spawns array. " +
					"colliders = every collider with its shape numbers, plus meshes with no collider or a null one. " +
					"add-collider = add or configure one. add-trigger = that, plus the reason the current SDK has no trigger behaviour to add (VRC_Trigger is abstract, so a script reacts to the collider instead). " +
					"audio = every AudioSource (2D or 3D, audibility, clip length, load type) and the clips the scene uses. " +
					"add-audio-source = add one with a clip. set-clip-load-type = set a clip's importer load type. " +
					"actions = the list.",
			}),
			property: Type.Optional(
				Type.String({
					description:
						"The <package>-set actions and create-behaviour's field list: the FIELD NAME to write. On these " +
						"packages the user-facing fields are private, so an unknown name comes back with the list of fields " +
						"that DO exist rather than a silent no-op.",
				}),
			),
			value: Type.Optional(
				Type.String({
					description:
						"The <package>-set actions: the value, coerced by the field's own type -- bool, number, " +
						"\"#rrggbb\" colour, enum member name, or another scene object's name. Read the current value with the " +
						"matching describe action first; a UdonSharp field is copied back into the Udon program after the write.",
				}),
			),
			fields: Type.Optional(
				Type.String({
					description:
						"create-behaviour only: comma-separated Field=Value pairs applied after the component is added. " +
						"Field names are the SDK's own, and they are PascalCase: \"RespawnHeightY=-100,ForbidUserPortals=true\". " +
						"An unknown name is reported with the fields that do exist, and the rest of the pairs still apply. " +
						"For a UdonSharp script the values are copied into the compiled program, not just the proxy.",
				}),
			),
			keepOriginal: Type.Optional(
				Type.Union([Type.Literal("true"), Type.Literal("false")], {
					description:
						"convert-light only. \"true\" (default) keeps the Unity Light, \"false\" disables it -- disabled, not " +
						"destroyed, so it can be switched back on.",
				}),
			),
			material: Type.Optional(
				Type.String({
					description:
						"fix-android-shaders only: the material's name. Ambiguous if several assets share it -- that is " +
						"refused with the candidate paths rather than guessed.",
				}),
			),
			shader: Type.Optional(
				Type.String({
					description:
						"fix-android-shaders only: the Quest-safe shader to swap to, default VRChat/Mobile/Standard Lite. " +
						"The VRChat/ prefix may be omitted.",
				}),
			),
			position: Type.Optional(
				Type.String({ description: 'add-spawn-point only: world position as "x,y,z". Defaults to 0,0,0.' }),
			),
			rotation: Type.Optional(
				Type.String({ description: 'add-spawn-point only: Euler rotation in degrees as "x,y,z". Defaults to 0,0,0.' }),
			),
			shape: Type.Optional(
				Type.String({ description: "add-collider and add-trigger only: Box, Sphere, Capsule or Mesh. Defaults to Box." }),
			),
			size: Type.Optional(
				Type.String({
					description:
						"add-collider and add-trigger only: one number. Box uses it as the edge length, Sphere as the radius, " +
						"Capsule as the radius with height set to twice that. Ignored for Mesh, which takes its geometry from " +
						"the object's mesh.",
				}),
			),
			isTrigger: Type.Optional(
				Type.Union([Type.Literal("true"), Type.Literal("false")], {
					description:
						"add-collider only: default true, which is what VRChat interaction uses. False makes the object BLOCK " +
						"avatars instead of triggering.",
				}),
			),
			clip: Type.Optional(
				Type.String({
					description:
						"add-audio-source and set-clip-load-type: the AudioClip asset's name or its Assets path. " +
						"An ambiguous name is refused with the matching paths.",
				}),
			),
			volume: Type.Optional(
				Type.String({ description: 'add-audio-source only: the source\'s volume, as a number such as "0.8". Defaults to 1.' }),
			),
			spatial: Type.Optional(
				Type.String({
					description:
						'add-audio-source only: "0" (default) for 2D, non-zero for 3D. A 3D source also gets sensible ' +
						"min/max distances when they are still Unity's 1/1 default.",
				}),
			),
			loop: Type.Optional(
				Type.Union([Type.Literal("true"), Type.Literal("false")], {
					description: "add-audio-source only: loop the clip. Defaults to false.",
				}),
			),
			loadType: Type.Optional(
				Type.String({
					description:
						"set-clip-load-type only: DecompressOnLoad, CompressedInMemory or Streaming. The value is read back " +
						"from the importer after the reimport and a mismatch is reported rather than assumed.",
				}),
			),
			gameObject: Type.Optional(
				Type.String({
					description:
						"Target GameObject for create-light-volume, find-behaviour, convert-light, create-behaviour and the " +
						"<package>-set actions. Omit to create a new object (create-light-volume), or to search the whole " +
						"scene (find-behaviour).",
				}),
			),
			behaviour: Type.Optional(
				Type.String({
					description:
						"find-behaviour and create-behaviour: a world behaviour type name. Prefer the full name, e.g. " +
						"VRC.SDK3.Components.VRCObjectSync; a bare simple name such as VRCObjectSync also resolves, " +
						"and the result reports the real type it landed on.",
				}),
			),
			tag: Type.Optional(Type.String({ description: "toggle-tag only: the Unity tag whose objects get toggled." })),
			inactive: Type.Optional(
				Type.Boolean({ description: "toggle-tag only: set objects with that tag INACTIVE instead of active. Defaults to false." }),
			),
			object: Type.Optional(
				Type.String({
					description:
						"quest-add only: the scene object's name. Ambiguous if two objects share it -- that is refused, not guessed. " +
						"The <package>-set actions take their target in gameObject.",
				}),
			),
			component: Type.Optional(
				Type.String({
					description:
						"quest-add only: a component type on that object, e.g. \"Light\". Without it the entry targets the " +
						"GameObject, which can only switch Active, Tag, Layer and static flags. Naming a component is what " +
						"gives the entry that component's per-platform values.",
				}),
			),
			path: Type.Optional(Type.String({ description: "quest-add only: an asset path, e.g. \"Assets/Materials/Wood.mat\", instead of a scene object." })),
			target: Type.Optional(
				Type.Union([Type.Literal("pc"), Type.Literal("android")], {
					description: "quest-apply only. \"pc\" or \"android\" (quest). Defaults to pc.",
				}),
			),
			lightType: Type.Optional(
				Type.Union([Type.Literal("PointLight"), Type.Literal("SpotLight"), Type.Literal("AreaLight")], {
					description: "create-light-volume only. Defaults to PointLight.",
				}),
			),
			range: Type.Optional(
				Type.Number({ description: "create-light-volume and convert-light. Metres, fractional allowed. 0 or omitted on convert-light means use the light's own range.", minimum: 0 }),
			),
			intensity: Type.Optional(
				Type.Number({ description: "create-light-volume and convert-light. Fractional allowed. 0 or omitted on convert-light means use the light's own intensity." }),
			),
			color: Type.Optional(
				Type.String({ description: "create-light-volume and convert-light: hex colour, e.g. \"ff8844\". Alpha is always 1.", default: "ffffff" }),
			),
		}),
		execute: async (_toolCallId, params, _signal, _onUpdate, _ctx) => {
			// Injection is a statement of INTENT, not of success: it is set
			// before any work, so a tool that throws (bad argument, no editor)
			// still leaves the guidance flowing for the next turn. It does NOT
			// raise the manager gate -- only /unity does that, because calling a
			// tool is not a delegation.
			managerMode.markUsed();
			const result = await requestLiveUnityWorld({
				action: params.action,
				gameObject: params.gameObject,
				behaviour: params.behaviour,
				lightType: params.lightType,
				range: params.range,
				intensity: params.intensity,
				color: params.color,
				tag: params.tag,
				inactive: params.inactive,
				object: params.object,
				component: params.component,
				path: params.path,
				target: params.target,
				property: params.property,
				value: params.value,
				fields: params.fields,
				keepOriginal: params.keepOriginal,
				material: params.material,
				shader: params.shader,
				position: params.position,
				rotation: params.rotation,
				shape: params.shape,
				size: params.size,
				isTrigger: params.isTrigger,
				clip: params.clip,
				volume: params.volume,
				spatial: params.spatial,
				loop: params.loop,
				loadType: params.loadType,
			});
			return { content: [{ type: "text", text: formatJsonResult(result) }], details: result };
		},
	});

	pi.registerTool({
		name: "unity_save_view",
		label: "Unity Save View",
		description:
			"Record a named view in the workspace manifest. In Unity a saved view is a named camera, so the camera must exist in the scene; the tool verifies that first.",
		parameters: Type.Object({
			workspace: Type.Optional(
				Type.String({ description: "Explicit workspace path. Relative paths resolve from the current working directory." }),
			),
			name: Type.String({ description: "Short view name, e.g. \"hero-angle\"." }),
			cameraName: Type.String({ description: "Name of the camera GameObject this view uses." }),
		}),
		execute: async (_toolCallId, params, _signal, _onUpdate, ctx) => {
			// Injection is a statement of INTENT, not of success: it is set
			// before any work, so a tool that throws (bad argument, no editor)
			// still leaves the guidance flowing for the next turn. It does NOT
			// raise the manager gate -- only /unity does that, because calling a
			// tool is not a delegation.
			managerMode.markUsed();
			const workspacePath = normalizeWorkspacePath(ctx.cwd, params.workspace);
			await assertCameraExists(workspacePath, params.cameraName);
			const result = await unitySaveView({
				cwd: ctx.cwd,
				workspacePath,
				name: params.name,
				cameraName: params.cameraName,
			});
			return { content: [{ type: "text", text: formatJsonResult(result) }], details: result };
		},
	});

	pi.registerTool({
		name: "unity_render",
		label: "Unity Render",
		description:
			"Render the live Unity scene through a camera into the current iteration folder. Use this to judge what the game actually looks like (lighting, materials, post-processing). If no camera is given the MainCamera-tagged camera is used, falling back to any camera in the scene.",
		parameters: Type.Object({
			workspace: Type.Optional(
				Type.String({ description: "Explicit workspace path. Relative paths resolve from the current working directory." }),
			),
			outputName: Type.Optional(
				Type.String({ description: "File name inside the iteration renders folder. A .png extension is added if missing.", default: "render.png" }),
			),
			cameraName: Type.Optional(
				Type.String({ description: "Camera GameObject to render from. Defaults to the active camera." }),
			),
			width: Type.Optional(Type.Number({ description: "Output width in pixels.", default: 960 })),
			height: Type.Optional(Type.Number({ description: "Output height in pixels.", default: 540 })),
			timeoutSeconds: Type.Optional(Type.Number({ description: "Explicit timeout in seconds.", default: 180 })),
		}),
		execute: async (_toolCallId, params, signal, _onUpdate, ctx) => {
			// Injection is a statement of INTENT, not of success: it is set
			// before any work, so a tool that throws (bad argument, no editor)
			// still leaves the guidance flowing for the next turn. It does NOT
			// raise the manager gate -- only /unity does that, because calling a
			// tool is not a delegation.
			managerMode.markUsed();
			const result = await unityRender({
				cwd: ctx.cwd,
				workspace: params.workspace,
				outputName: params.outputName,
				cameraName: params.cameraName,
				width: params.width,
				height: params.height,
				timeoutSeconds: params.timeoutSeconds,
				signal,
			});
			return { content: [{ type: "text", text: formatJsonResult(result) }], details: result };
		},
	});

	pi.registerTool({
		name: "unity_capture_view",
		label: "Unity Capture View",
		description:
			"Capture the live Scene view into the current iteration folder -- what the artist is looking at, with overlays and the Scene camera's own framing. " +
			"Use unity_render instead to judge lighting, materials, and post-processing; a Scene view has neither, so a material that renders white can look fine here. " +
			"This is the one to use while iterating on layout, scale, and composition. Needs a pi workspace: it claims an iteration slot and writes into it.",
		parameters: Type.Object({
			workspace: Type.Optional(
				Type.String({ description: "Explicit workspace path. Relative paths resolve from the current working directory." }),
			),
			outputName: Type.Optional(
				Type.String({ description: "File name inside the iteration renders folder. A .png extension is added if missing.", default: "render.png" }),
			),
			width: Type.Optional(Type.Number({ description: "Output width in pixels.", default: 960 })),
			height: Type.Optional(Type.Number({ description: "Output height in pixels.", default: 540 })),
			timeoutSeconds: Type.Optional(Type.Number({ description: "Explicit timeout in seconds.", default: 180 })),
		}),
		execute: async (_toolCallId, params, signal, _onUpdate, ctx) => {
			// Injection is a statement of INTENT, not of success: it is set
			// before any work, so a tool that throws (bad argument, no editor)
			// still leaves the guidance flowing for the next turn. It does NOT
			// raise the manager gate -- only /unity does that, because calling a
			// tool is not a delegation.
			managerMode.markUsed();
			const result = await unityCaptureView({
				cwd: ctx.cwd,
				workspace: params.workspace,
				outputName: params.outputName,
				width: params.width,
				height: params.height,
				timeoutSeconds: params.timeoutSeconds,
				signal,
			});
			return { content: [{ type: "text", text: formatJsonResult(result) }], details: result };
		},
	});

	pi.registerTool({
		name: "unity_module_run",
		label: "Unity Module Run",
		description:
			"Run one module of a multi-part task: checkpoints the .unity scene file into modules/<name>/runs/NN/, executes modules/<name>/script.cs in the live Editor, saves the scene, and optionally renders to modules/<name>/render.png. Each module reverts independently.",
		parameters: Type.Object({
			workspace: Type.Optional(
				Type.String({ description: "Explicit workspace path. Relative paths resolve from the current working directory." }),
			),
			moduleName: Type.Optional(
				Type.String({ description: 'Module name: lowercase letters, digits, dashes. Defaults to "default".', default: "default" }),
			),
			render: Type.Optional(
				Type.Boolean({ description: "Render modules/<name>/render.png after a successful run.", default: true }),
			),
			view: Type.Optional(
				Type.String({ description: "Camera name to render from. Defaults to the active camera." }),
			),
			resolution: Type.Optional(
				Type.Object({ x: Type.Number(), y: Type.Number() }, { description: "Render size for the module render." }),
			),
			timeoutSeconds: Type.Optional(Type.Number({ description: "Explicit timeout in seconds.", default: 180 })),
		}),
		execute: async (_toolCallId, params, _signal, _onUpdate, ctx) => {
			// Injection is a statement of INTENT, not of success: it is set
			// before any work, so a tool that throws (bad argument, no editor)
			// still leaves the guidance flowing for the next turn. It does NOT
			// raise the manager gate -- only /unity does that, because calling a
			// tool is not a delegation.
			managerMode.markUsed();
			const result = await unityModuleRun({
				cwd: ctx.cwd,
				workspace: params.workspace,
				moduleName: params.moduleName ?? "default",
				render: params.render,
				view: params.view,
				resolution: params.resolution,
				timeoutSeconds: params.timeoutSeconds,
			});
			return { content: [{ type: "text", text: formatJsonResult(result) }], details: result };
		},
	});

	pi.registerTool({
		name: "unity_module_revert",
		label: "Unity Module Revert",
		description:
			"Restore the .unity scene file from a module run's checkpoint, discarding that run and everything after it. Refuses by default when other modules' later runs would be lost. Note the scope: scene state is restored, assets written under Assets/ are not.",
		parameters: Type.Object({
			workspace: Type.Optional(
				Type.String({ description: "Explicit workspace path. Relative paths resolve from the current working directory." }),
			),
			moduleName: Type.Optional(
				Type.String({ description: 'Module name to revert. Defaults to "default".', default: "default" }),
			),
			iteration: Type.Optional(
				Type.Number({ description: "Specific recorded iteration to revert to. Defaults to the module's latest run." }),
			),
			force: Type.Optional(
				Type.Boolean({
					description: "Revert even though later module runs modified the scene. Accepts discarding their work.",
					default: false,
				}),
			),
		}),
		execute: async (_toolCallId, params, _signal, _onUpdate, ctx) => {
			// Injection is a statement of INTENT, not of success: it is set
			// before any work, so a tool that throws (bad argument, no editor)
			// still leaves the guidance flowing for the next turn. It does NOT
			// raise the manager gate -- only /unity does that, because calling a
			// tool is not a delegation.
			managerMode.markUsed();
			const result = await unityModuleRevert({
				cwd: ctx.cwd,
				workspace: params.workspace,
				moduleName: params.moduleName ?? "default",
				iteration: params.iteration,
				force: params.force,
			});
			return { content: [{ type: "text", text: formatJsonResult(result) }], details: result };
		},
	});

	pi.registerTool({
		name: "unity_set_tool",
		label: "Unity Set Tool",
		description: `Set the active transform tool in the live Unity Editor: ${UNITY_TOOLS.join(", ")}.`,
		parameters: Type.Object({
			tool: Type.String({ description: "Transform tool to activate." }),
		}),
		execute: async (_toolCallId, params, _signal, _onUpdate, _ctx) => {
			// Injection is a statement of INTENT, not of success: it is set
			// before any work, so a tool that throws (bad argument, no editor)
			// still leaves the guidance flowing for the next turn. It does NOT
			// raise the manager gate -- only /unity does that, because calling a
			// tool is not a delegation.
			managerMode.markUsed();
			const result = await unitySetTool({ tool: params.tool });
			return { content: [{ type: "text", text: formatJsonResult(result) }], details: result };
		},
	});

	pi.registerTool({
		name: "unity_log_critique",
		label: "Unity Log Critique",
		description:
			"Append an iteration critique to the workspace critique log: five 0-2 scores, concrete issues, a judgment of whether the current render view is adequate for evaluating the work, and the next action.",
		parameters: Type.Object({
			workspace: Type.Optional(
				Type.String({ description: "Explicit workspace path. Relative paths resolve from the current working directory." }),
			),
			accuracy: Type.Number({ description: "How well the result matches the brief (0-2)." }),
			geometry: Type.Number({ description: "Geometry and proportions (0-2)." }),
			materials: Type.Number({ description: "Materials and appearance (0-2)." }),
			completeness: Type.Number({ description: "How much of the brief is done (0-2)." }),
			quality: Type.Number({ description: "Overall visual quality (0-2)." }),
			issues: Type.Optional(
				Type.Array(Type.String(), { description: "Concrete problems found in the render or scene." }),
			),
			viewAdequacy: Type.String({
				description: "Non-scored judgment of whether the current render view is good enough for evaluation, including framing, coverage, or perspective problems.",
			}),
			nextAction: Type.String({
				description: 'A fix to attempt next, or "present to user" when the result is good enough.',
			}),
		}),
		execute: async (_toolCallId, params, _signal, _onUpdate, ctx) => {
			// Injection is a statement of INTENT, not of success: it is set
			// before any work, so a tool that throws (bad argument, no editor)
			// still leaves the guidance flowing for the next turn. It does NOT
			// raise the manager gate -- only /unity does that, because calling a
			// tool is not a delegation.
			managerMode.markUsed();
			const result = unityLogCritique({
				cwd: ctx.cwd,
				workspace: params.workspace,
				accuracy: params.accuracy,
				geometry: params.geometry,
				materials: params.materials,
				completeness: params.completeness,
				quality: params.quality,
				issues: params.issues ?? [],
				viewAdequacy: params.viewAdequacy,
				nextAction: params.nextAction,
			});
			return { content: [{ type: "text", text: formatJsonResult(result) }], details: result };
		},
	});

	pi.registerTool({
		name: "unity_bridge_status",
		label: "Unity Bridge Status",
		description:
			"Check whether the live Unity bridge can serve a request right now: is the Editor running, is another request holding the single live slot, and how many requests are pending. Use it before a long or mutating batch, or when a bridge call timed out, instead of guessing or burning another timeout.",
		parameters: Type.Object({
			workspace: Type.Optional(
				Type.String({ description: "Optional workspace path to report alongside the bridge state." }),
			),
		}),
		execute: async (_toolCallId, params, _signal, _onUpdate, _ctx) => {
			// Injection is a statement of INTENT, not of success: it is set
			// before any work, so a tool that throws (bad argument, no editor)
			// still leaves the guidance flowing for the next turn. It does NOT
			// raise the manager gate -- only /unity does that, because calling a
			// tool is not a delegation.
			managerMode.markUsed();
			const result = await unityBridgeStatus();
			const details = { ...result, requestedWorkspace: params.workspace };
			return { content: [{ type: "text", text: formatJsonResult(details) }], details };
		},
	});

	pi.registerTool({
		name: "unity_await_compile",
		label: "Unity Await Compile",
		description:
			"Block until the live Editor's script compilation has FINISHED, then return the compiler's verdict. " +
			"Call this after you change or create a C# script instead of sleeping and re-reading compile status: " +
			"a script compile takes 4-6 minutes, which outlasts any single bridge request, and a sleep long enough " +
			"to be safe is a guess -- a sleep too short returns a status that looks identical to a clean one. " +
			"Returns status compiled | failed | timeout. `timeout` is the ABSENCE of a verdict, not a build " +
			"result: `source` is then \"none\" and nothing is reported as compiled -- call it again with a " +
			"larger timeoutMs rather than treating it as a build. `compileInFlight` distinguishes a compile " +
			"still running from one that never started. " +
			"A dead or unresponsive Editor raises instead, rather than being reported as a clean build.",
		parameters: Type.Object({
			timeoutMs: Type.Optional(
				Type.Number({
					description: "Budget for the compile in ms. Default 600000 (10 min), which covers a 4-6 min full compile.",
				}),
			),
		}),
		execute: async (_toolCallId, params, signal, _onUpdate, _ctx) => {
			managerMode.markUsed();
			const result = await requestLiveUnityAwaitCompile({
				timeoutMs: params.timeoutMs,
				signal,
			});
			return { content: [{ type: "text", text: formatJsonResult(result) }], details: result };
		},
	});

	pi.on("resources_discover", () => ({
		skillPaths: [getBundledUnitySkillsDir()],
	}));

	// Engagement is per-session: a fresh session must not inherit the
	// guidance-injection flag from a previous one. A /reload is not a fresh
	// session and keeps it — see createManagerMode's reset().
	pi.on("session_start", (event) => {
		managerMode.reset(event?.reason);
	});

	// The gate itself. This line was MISSING while the extension already
	// carried the refusals, the allowlist and a /unity-exit that advertises
	// lifting the gate — so the policy was written and never installed, and
	// nothing in this repo tested for it. See pi-manager-core's
	// test/extension-registration.mjs, which now checks that every manager
	// actually registers a tool_call handler.
	managerMode.install(pi);

	// The command registration, the argument handling and the deliverAs choice
	// are the shared core's: all three manager extensions send the user's text
	// the same way, and a `steer` sent into a non-idle session can interleave
	// ahead of queued work. What stays here is the launch, which is Unity's.
	core.registerEngageCommand(pi, {
		name: "unity",
		usage: "<what to build or change in Unity>",
		engagedMessage: "Unity engaged — this session is the manager.",
		onEngage: () => {
			managerMode.markEngaged();
			// Deliberately NO launch here. Engaging a session says nothing about
			// which project to open — there is no project, no manifest and no
			// workspace yet — so the only thing a launch could do is open an
			// Editor with nothing in it, which never heartbeats and only
			// produces timeouts. unity_workspace_init launches the Editor with
			// the project it just made; until then pi attaches to whatever Editor
			// the user has open, and launches nothing. See
			// noProjectKnownMessage in launch.ts.
		},
	});

	// /unity-exit lifts the gate without ending the session or touching the
	// project, because the gate otherwise lasts until /new.
	core.registerExitCommand(pi, {
		name: "unity-exit",
		description: "Leave Unity manager mode — lift the write/edit gate (keeps the session and the project)",
		onExit: () => managerMode.clearGate(),
		notEngagedMessage: "Not in Unity manager mode — nothing to exit.",
		exitedMessage:
			"Left Unity manager mode. The unity_* tools, /unity and the skills are still available; run /unity to re-enter.",
	});

	pi.on("before_agent_start", injectUnityContext);
}
