/**
 * pi extension: Unity integration.
 *
 * Registers 11 unity_* tools + bundled skills, the /unity slash command, and
 * injects Unity workflow guidance + live Editor state before turns that engage
 * the extension.
 *
 * The Unity Editor is NOT launched at pi/session start, and unlike Blender it
 * cannot be launched to answer a question alone: the bridge is a compiled C#
 * editor plugin (unity/plugin/) that is copied into a workspace's
 * Assets/Editor/ folder, so the first request against a project costs an editor
 * boot plus a compile. The guidance injected below is what keeps that cost from
 * turning into a sequence of blind timeouts.
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
import { ensureUnityLaunchedForce, resetUnityLaunch } from "./launch.js";

const UNITY_WORKFLOW_GUIDANCE = [
	"You are operating inside pi-vibe-unity with Unity-native tools available.",
	"Prefer Unity tools over generic shell/file workflows for scene creation, editing, inspection, and rendering.",
	"Keep the `workspace` argument explicit in every Unity tool call and reuse the same workspace across continuation turns.",
	"Author C# in the workspace root script.cs (a static `PiScript.Run` entry point) using the normal write/edit tools, then call unity_execute_csharp with script_path pointing to it. Entry points PiScript.Run, PiEntryPoint.Run, or any static Run/Main/Execute are accepted.",
	"The live bridge is a compiled C# plugin, so Unity must have the project open and the plugin must have compiled. The first request against a project can take a minute or two while the Editor boots and imports. Check unity_bridge_status rather than assuming a call is stuck.",
	"If an authoritative live Unity state block is present for this turn, treat it as the only current source of truth for present-tense Editor state such as selection, open scene, or active camera.",
	"Ignore older live Unity context summaries, earlier selection mentions in message history, and prior inferences about current Unity state when answering present-tense questions for this turn.",
	'If the user refers to "it", "this", the selected object, or the current view and the authoritative live Unity state block is missing, stale, or insufficient, call unity_session_context before mutating.',
	"Inspect before mutating when editing an existing scene, and render after meaningful scene changes that need visual verification.",
	"Positions: unity_scene_info { categories: [\"objects\"] } carries world AND local position, rotation, and scale per object, so 'where is this object' needs no second call. A bone is NOT a GameObject: a rig lives inside the prefab and is addressed by Humanoid bone name, so use unity_bones, and unity_set_transform with space 'local' to place a bone relative to its parent. Always pass `bones` to filter -- an avatar has 180+.",
	"Component fields: read them with unity_inspect, which walks SerializedObject and therefore sees exactly what the Unity Inspector shows -- including serialized private and [HideInInspector] fields on types you have never heard of (VRC_AvatarDescriptor, lilToon, VRCFury). Write one with unity_set_property using an exact property path from unity_inspect; the value is parsed by the property's own type and every write is undoable.",
	"unity_render renders through a camera with lighting and materials (what the game looks like); unity_capture_view grabs the Scene view (what the artist is looking at). Default to unity_render for judging work, and add a camera GameObject if the scene has none.",
	"A saved view in Unity is a named camera, not a viewport snapshot: use unity_save_view with a camera that exists.",
	"For multi-part tasks (several GameObjects, or several distinct concerns), decompose into modules: one per object or concern, each with its own modules/<name>/script.cs. Stay the manager: give each worker subagent a compact brief (goal, GameObject names, acceptance criteria, workspace + module name) and read only the returned report.",
	"Run modules with unity_module_run and undo with unity_module_revert. Note the scope of the guarantee: a module checkpoint is a copy of the .unity scene file, so a revert restores scene state but does NOT roll back assets written under Assets/.",
	"Before the first worker spawn in the modular flow, call subagents_enable (no arguments) once: fresh sessions do not have the subagent tool active until you enable it.",
	"For create and edit work, use a short ReAct loop: execute, render, critique, log the critique, then either stop or iterate. Do at most 5 iterations for each new user instruction.",
	"Do not restate tool schemas in prose. Use the tool definitions directly.",
].join(" ");

const LIVE_CONTEXT_PROMPT_TIMEOUT_MS = 4_000;

let unityEngaged = false;
function markUnityEngaged(): void {
	unityEngaged = true;
}

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

export default function piVibeUnityExtension(pi: ExtensionAPI) {
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
			const result = await unityWorkspaceInit({
				workspacePath: normalizeWorkspacePath(ctx.cwd, params.workspace),
				template: params.template as "3d" | "blank" | undefined,
				sourceProject: params.sourceProject,
				continueExisting: params.continueExisting,
			});
			markUnityEngaged();
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
			markUnityEngaged();
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
			const result = await unitySessionContext({ cwd: ctx.cwd, workspace: params.workspace });
			markUnityEngaged();
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
			const result = await getSceneInfo({
				cwd: ctx.cwd,
				workspace: params.workspace,
				categories: params.categories as never,
				signal,
			});
			markUnityEngaged();
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
			const result = await requestLiveUnityBones({
				gameObject: params.gameObject,
				bones: params.bones,
				limit: params.limit,
			});
			markUnityEngaged();
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
			const result = await requestLiveUnityInspect({
				gameObject: params.gameObject,
				component: params.component,
				includeChildComponents: params.includeChildren,
			});
			markUnityEngaged();
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
			const result = await requestLiveUnitySetProperty({
				gameObject: params.gameObject,
				component: params.component,
				property: params.property,
				value: params.value,
				space: params.space,
			});
			markUnityEngaged();
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
			const result = await requestLiveUnitySetTransform({
				gameObject: params.gameObject,
				position: params.position,
				rotationEuler: params.rotationEuler,
				scale: params.scale,
				space: params.space,
			});
			markUnityEngaged();
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
			const result = await requestLiveUnityVrc({
				action: params.action,
				gameObject: params.gameObject,
				element: params.element,
				bone: params.bone,
				materialPath: params.materialPath,
				child: params.child,
			});
			markUnityEngaged();
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
			const result = await requestLiveUnityWorld({
				action: params.action,
				gameObject: params.gameObject,
				behaviour: params.behaviour,
				lightType: params.lightType,
				range: params.range,
				intensity: params.intensity,
				color: params.color,
			});
			markUnityEngaged();
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
			const workspacePath = normalizeWorkspacePath(ctx.cwd, params.workspace);
			await assertCameraExists(workspacePath, params.cameraName);
			const result = await unitySaveView({
				cwd: ctx.cwd,
				workspacePath,
				name: params.name,
				cameraName: params.cameraName,
			});
			markUnityEngaged();
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
			markUnityEngaged();
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
			const result = await unityModuleRun({
				cwd: ctx.cwd,
				workspace: params.workspace,
				moduleName: params.moduleName ?? "default",
				render: params.render,
				view: params.view,
				resolution: params.resolution,
				timeoutSeconds: params.timeoutSeconds,
			});
			markUnityEngaged();
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
			const result = await unityModuleRevert({
				cwd: ctx.cwd,
				workspace: params.workspace,
				moduleName: params.moduleName ?? "default",
				iteration: params.iteration,
				force: params.force,
			});
			markUnityEngaged();
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
			const result = await unitySetTool({ tool: params.tool });
			markUnityEngaged();
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
			const result = unityBridgeStatus();
			markUnityEngaged();
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
		unityEngaged = false;
	});

	pi.registerCommand("unity", {
		description: "Send a Unity task — everything after /unity is the prompt",
		handler: async (args, ctx) => {
			const task = args.trim();
			if (!task) {
				ctx.ui.notify("Usage: /unity <what to build or change in Unity>", "info");
				return;
			}
			unityEngaged = true;
			// Force-launch so a project opened and closed moments ago is relaunched
			// rather than treated as already-alive. No project is known here, so a
			// workspace_init call still has to happen before the bridge can serve
			// anything; the launch is best-effort by design.
			resetUnityLaunch();
			ensureUnityLaunchedForce();
			ctx.ui.notify("Unity engaged — routing task to the agent.", "info");
			pi.sendUserMessage(task, { deliverAs: ctx.isIdle() ? undefined : "steer" });
		},
	});

	pi.on("before_agent_start", (_event, ctx) => {
		// Only inject when engaged: the user ran /unity, or a unity_* tool was
		// already used. Injection alone never spawns the Editor.
		if (!unityEngaged) return undefined;

		return (async () => {
			let liveContextPrompt = "";
			try {
				// Unity is slower to answer than Blender: the bridge shares the
				// Editor's main thread with import and compile work, so it gets a
				// wider prompt-timeout budget. If even that fails, say the state is
				// unknown instead of leaving an empty string, which the agent used
				// to read as "nothing to inject" and then assume it knew the
				// selection.
				const live = await withTimeout(
					unitySessionContext({ cwd: ctx.cwd }),
					LIVE_CONTEXT_PROMPT_TIMEOUT_MS,
				);
				const context = live.context;
				liveContextPrompt = context
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
							live.note,
						]
							.filter((line) => line.length > 0)
							.join("\n")
					: [
							"Live Unity state is UNAVAILABLE for this turn.",
							"Do NOT assume any selection, active object, open scene, or camera state from earlier turns.",
							`${live.note}`,
							"Call `unity_session_context` before mutating anything, and ask the user if it also fails.",
						].join("\n");
			} catch (error) {
				liveContextPrompt = [
					`Live Unity state is UNAVAILABLE for this turn (the bridge did not answer within ${LIVE_CONTEXT_PROMPT_TIMEOUT_MS} ms: ${error instanceof Error ? error.message : String(error)}).`,
					"Do NOT assume any selection, active object, open scene, or camera state from earlier turns.",
					"Call `unity_session_context` before mutating anything, and ask the user if it also fails.",
				].join("\n");
			}

			return {
				message: {
					customType: "unity_context",
					content: [UNITY_WORKFLOW_GUIDANCE, liveContextPrompt].filter((part) => part.length > 0).join("\n\n"),
					display: true,
				},
			};
		})();
	});
}
