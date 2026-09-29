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
	unityCaptureView,
	requestLiveUnityVrc,
	requestLiveUnityWorld,
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
 * Manager mode, entered ONLY by the /unity command.
 *
 * The mechanism — the engaged flag, the tool_call gate, and the exemption for a
 * subagent inside a manager session — is the shared core's, because all three
 * manager extensions had a private copy of it and they drifted. The refusals
 * are attached here as a policy callback: they name a Unity worker and a Unity
 * critic, so they cannot live in a file that does not know about Unity.
 */
const managerMode = core.createManagerMode({
	allowedTools: [
		// Delegation and supervision: without these the manager cannot do the
		// one thing its prompt tells it to do.
		"subagent",
		"subagents_enable",
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
	],
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
		execute: async (_toolCallId, params, signal, _onUpdate, ctx) => {
			// Engagement is a statement of INTENT, not of success: it is set
			// before any work, so a tool that throws (bad argument, no editor)
			// still leaves the session engaged and the guidance injected.
			managerMode.markEngaged();
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
			// Engagement is a statement of INTENT, not of success: it is set
			// before any work, so a tool that throws (bad argument, no editor)
			// still leaves the session engaged and the guidance injected.
			managerMode.markEngaged();
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
			// Engagement is a statement of INTENT, not of success: it is set
			// before any work, so a tool that throws (bad argument, no editor)
			// still leaves the session engaged and the guidance injected.
			managerMode.markEngaged();
			const result = await unitySessionContext({ cwd: ctx.cwd, workspace: params.workspace });
			return { content: [{ type: "text", text: formatJsonResult(result) }], details: result };
		},
	});

	pi.registerTool({
		name: "unity_scene_info",
		label: "Unity Scene Info",
		description:
			"Inspect the live Unity scene and project. Narrow it with `categories` instead of dumping everything: " +
			`${UNITY_SCENE_INFO_CATEGORIES.join(", ")}. The default omits packages and assets, which are slow and large.`,
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
			// Engagement is a statement of INTENT, not of success: it is set
			// before any work, so a tool that throws (bad argument, no editor)
			// still leaves the session engaged and the guidance injected.
			managerMode.markEngaged();
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
			// Engagement is a statement of INTENT, not of success: it is set
			// before any work, so a tool that throws (bad argument, no editor)
			// still leaves the session engaged and the guidance injected.
			managerMode.markEngaged();
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
			// Engagement is a statement of INTENT, not of success: it is set
			// before any work, so a tool that throws (bad argument, no editor)
			// still leaves the session engaged and the guidance injected.
			managerMode.markEngaged();
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
			gameObject: Type.String({ description: "GameObject name in the active scene. Call unity_scene_info for the list of roots." }),
			component: Type.Optional(
				Type.String({ description: "Component short or full type name to dump in full, e.g. \"VRC_AvatarDescriptor\"." }),
			),
			includeChildren: Type.Optional(
				Type.Boolean({ description: "Also list immediate children with their local positions. Defaults to false." }),
			),
		}),
		execute: async (_toolCallId, params, _signal, _onUpdate, _ctx) => {
			// Engagement is a statement of INTENT, not of success: it is set
			// before any work, so a tool that throws (bad argument, no editor)
			// still leaves the session engaged and the guidance injected.
			managerMode.markEngaged();
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
			// Engagement is a statement of INTENT, not of success: it is set
			// before any work, so a tool that throws (bad argument, no editor)
			// still leaves the session engaged and the guidance injected.
			managerMode.markEngaged();
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
			gameObject: Type.String({ description: "GameObject name in the active scene." }),
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
			// Engagement is a statement of INTENT, not of success: it is set
			// before any work, so a tool that throws (bad argument, no editor)
			// still leaves the session engaged and the guidance injected.
			managerMode.markEngaged();
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
			// Engagement is a statement of INTENT, not of success: it is set
			// before any work, so a tool that throws (bad argument, no editor)
			// still leaves the session engaged and the guidance injected.
			managerMode.markEngaged();
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
			"VRChat WORLD project helpers: light volumes, Udon, and the scene descriptor. Start with action \"scene\", which reports " +
			"packages, the VRC_SceneDescriptor, Udon counts, world behaviours, and the light volume setup in one call. " +
			"Actions: scene, descriptor, light-volumes, create-light-volume, udon, find-behaviour, actions. " +
			"Light volumes bake at BUILD time, so a new one changes nothing on screen until you build. " +
			"Not for avatar projects; use unity_vrc there.",
		parameters: Type.Object({
			workspace: Type.Optional(
				Type.String({ description: "Explicit workspace path. Relative paths resolve from the current working directory." }),
			),
			action: Type.String({
				description:
					"scene = the full picture (packages, descriptor, Udon, behaviours, light volumes). descriptor = the VRC_SceneDescriptor. " +
					"light-volumes = every point light volume with its range, intensity, colour and shape, plus the bake settings. " +
					"create-light-volume = add one. udon = UdonBehaviour counts and why script creation is not automated. " +
					"find-behaviour = resolve a world behaviour by name and report the real type. actions = the list.",
			}),
			gameObject: Type.Optional(
				Type.String({ description: "Target GameObject for create-light-volume or find-behaviour. Omit to create a new object." }),
			),
			behaviour: Type.Optional(
				Type.String({
					description:
						"find-behaviour only: a world behaviour name such as VRC_SceneDescriptor, VRCObjectSync, VRC_Pickup. " +
						"Resolves by simple name, so it reports the real type rather than assuming one.",
				}),
			),
			lightType: Type.Optional(
				Type.Union([Type.Literal("PointLight"), Type.Literal("SpotLight"), Type.Literal("AreaLight")], {
					description: "create-light-volume only. Defaults to PointLight.",
				}),
			),
			range: Type.Optional(Type.Number({ description: "create-light-volume only. Metres. Defaults to 10.", minimum: 0 })),
			intensity: Type.Optional(Type.Number({ description: "create-light-volume only. Defaults to 1." })),
			color: Type.Optional(
				Type.String({ description: "create-light-volume only: hex colour, e.g. \"ff8844\". Alpha is always 1.", default: "ffffff" }),
			),
		}),
		execute: async (_toolCallId, params, _signal, _onUpdate, _ctx) => {
			// Engagement is a statement of INTENT, not of success: it is set
			// before any work, so a tool that throws (bad argument, no editor)
			// still leaves the session engaged and the guidance injected.
			managerMode.markEngaged();
			const result = await requestLiveUnityWorld({
				action: params.action,
				gameObject: params.gameObject,
				behaviour: params.behaviour,
				lightType: params.lightType,
				range: params.range,
				intensity: params.intensity,
				color: params.color,
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
			// Engagement is a statement of INTENT, not of success: it is set
			// before any work, so a tool that throws (bad argument, no editor)
			// still leaves the session engaged and the guidance injected.
			managerMode.markEngaged();
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
			// Engagement is a statement of INTENT, not of success: it is set
			// before any work, so a tool that throws (bad argument, no editor)
			// still leaves the session engaged and the guidance injected.
			managerMode.markEngaged();
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
			// Engagement is a statement of INTENT, not of success: it is set
			// before any work, so a tool that throws (bad argument, no editor)
			// still leaves the session engaged and the guidance injected.
			managerMode.markEngaged();
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
			// Engagement is a statement of INTENT, not of success: it is set
			// before any work, so a tool that throws (bad argument, no editor)
			// still leaves the session engaged and the guidance injected.
			managerMode.markEngaged();
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
			// Engagement is a statement of INTENT, not of success: it is set
			// before any work, so a tool that throws (bad argument, no editor)
			// still leaves the session engaged and the guidance injected.
			managerMode.markEngaged();
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
			// Engagement is a statement of INTENT, not of success: it is set
			// before any work, so a tool that throws (bad argument, no editor)
			// still leaves the session engaged and the guidance injected.
			managerMode.markEngaged();
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
			// Engagement is a statement of INTENT, not of success: it is set
			// before any work, so a tool that throws (bad argument, no editor)
			// still leaves the session engaged and the guidance injected.
			managerMode.markEngaged();
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
			// Engagement is a statement of INTENT, not of success: it is set
			// before any work, so a tool that throws (bad argument, no editor)
			// still leaves the session engaged and the guidance injected.
			managerMode.markEngaged();
			const result = await unityBridgeStatus();
			const details = { ...result, requestedWorkspace: params.workspace };
			return { content: [{ type: "text", text: formatJsonResult(details) }], details };
		},
	});

	pi.on("resources_discover", () => ({
		skillPaths: [getBundledUnitySkillsDir()],
	}));

	// Engagement is per-session: a fresh session must not inherit the
	// guidance-injection flag from a previous one.
	pi.on("session_start", () => {
		managerMode.reset();
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
