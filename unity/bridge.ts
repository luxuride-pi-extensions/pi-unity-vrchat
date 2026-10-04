/**
 * Request/response transport to the live Unity Editor.
 *
 * Protocol, mirroring the Blender extension so the two bridges behave the same
 * way for a model: pi writes <bridgeDir>/requests/<uuid>.json (tmp + rename, so
 * the Editor never reads a partial file), the C# plugin answers with
 * <bridgeDir>/responses/<uuid>.json, and <bridgeDir>/heartbeat proves the plugin
 * compiled and is still polling.
 *
 * Unity-specific wrinkle: the plugin does not exist until it has been compiled
 * inside a project, and a *freshly generated* project must resolve packages and
 * import assets before the Editor is usable. Cold start therefore gets a much
 * longer budget than a warm request, and the timeout message says what to do
 * rather than just failing.
 */

import { randomUUID } from "node:crypto";
import { existsSync, mkdirSync, readFileSync, readdirSync, renameSync, rmSync, statSync, writeFileSync } from "node:fs";
import { join } from "node:path";

import { getUnityBridgeDir } from "../config.js";
import {
	ensureUnityLaunched,
	isLiveBridgeAlive,
	LIVE_BRIDGE_HEARTBEAT_FILE,
} from "../launch.js";
import type { UnityBridgeRequest } from "./types.js";
import {
	acquireBridgeLock,
	delay,
	LIVE_BRIDGE_COLD_START_TIMEOUT_MS,
	LIVE_BRIDGE_POLL_MS,
	LIVE_BRIDGE_TIMEOUT_MS,
} from "./utils.js";

export interface LiveBridgePaths {
	bridgeDir: string;
	requestsDir: string;
	responsesDir: string;
}

export function getLiveBridgePaths(): LiveBridgePaths {
	const bridgeDir = getUnityBridgeDir();
	return {
		bridgeDir,
		requestsDir: join(bridgeDir, "requests"),
		responsesDir: join(bridgeDir, "responses"),
	};
}

export interface LiveBridgeRequestOptions {
	payload: UnityBridgeRequest;
	signal?: AbortSignal;
	timeoutMs?: number;
	/** Project to open if the Editor is not up yet; enables a true cold start. */
	launchProjectPath?: string;
}

/**
 * A bridge request that failed but still returned a payload.
 *
 * `partialResult` is whatever the Editor managed to produce: for
 * unity_execute_csharp that is `stdout`, `stderr`, `stackTrace`, `logs` and
 * `diagnostics`. Callers read it off the error rather than having it
 * stringified into a message.
 */
export class UnityBridgeFailureError<T = unknown> extends Error {
	readonly partialResult: T;

	constructor(message: string, partialResult: T) {
		super(message);
		this.name = "UnityBridgeFailureError";
		this.partialResult = partialResult;
	}
}

export async function requestLiveUnityBridge<T>(options: LiveBridgeRequestOptions): Promise<T> {
	const { requestsDir, responsesDir } = getLiveBridgePaths();
	mkdirSync(requestsDir, { recursive: true });
	mkdirSync(responsesDir, { recursive: true });

	// One request at a time: the live Editor is a single mutable session.
	const lock = await acquireBridgeLock({
		signal: options.signal,
		label: options.payload.label || String(options.payload.type ?? "request"),
	});
	try {
		return await runLockedBridgeRequest<T>(options);
	} finally {
		lock.release();
	}
}

async function runLockedBridgeRequest<T>(options: LiveBridgeRequestOptions): Promise<T> {
	const coldStart = !isLiveBridgeAlive();
	await ensureUnityLaunched(options.launchProjectPath);

	const { requestsDir, responsesDir } = getLiveBridgePaths();
	const requestId = randomUUID();
	const requestPath = join(requestsDir, `${requestId}.json`);
	const responsePath = join(responsesDir, `${requestId}.json`);

	// A leftover response from a recycled id would otherwise be read as ours.
	rmSync(responsePath, { force: true });

	const tempPath = `${requestPath}.tmp`;
	writeFileSync(tempPath, JSON.stringify(options.payload), "utf8");
	// Atomic publish: the Editor scans the directory continuously.
	renameSync(tempPath, requestPath);

	const timeoutMs = options.timeoutMs ?? (coldStart ? LIVE_BRIDGE_COLD_START_TIMEOUT_MS : LIVE_BRIDGE_TIMEOUT_MS);
	const deadline = Date.now() + timeoutMs;

	while (Date.now() < deadline) {
		if (options.signal?.aborted) {
			rmSync(requestPath, { force: true });
			throw new Error("Unity bridge request aborted.");
		}
		if (existsSync(responsePath)) {
			const raw = readFileSync(responsePath, "utf8");
			rmSync(responsePath, { force: true });
			let parsed: { ok?: boolean; result?: T; error?: string };
			try {
				parsed = JSON.parse(raw) as typeof parsed;
			} catch (error) {
				// Name the request and the size. A single-line payload means
				// JSON.parse's offset is meaningless on its own: "Expected
				// double-quoted property name in JSON at position 49883" of a
				// 50KB scene dump says neither which request failed nor that the
				// payload was near a whole scene's worth of objects. The bridge
				// self-checks payloads before publishing them (see
				// WriteResponse in PiUnityBridge.Polling.cs), so reaching this
				// branch at all means the response was already malformed
				// upstream -- or the file was read mid-write.
				const label = options.payload.label || String(options.payload.type ?? "unknown");
				throw new Error(
					`The Unity bridge wrote an unreadable response for request '${label}' ` +
						`(${raw.length} bytes on disk): ${error instanceof Error ? error.message : String(error)}. ` +
						`Narrow the request (fewer categories, a filtered query) to get a readable payload; ` +
						`check unity_bridge_status for the plugin compile state.`,
				);
			}
			if (parsed.ok) return parsed.result as T;
			// A failure may still carry a payload: a snippet that threw, or one
			// that did not compile, has usually learned something worth returning
			// (the Debug.Log lines it emitted, the Roslyn diagnostics, the stack
			// trace). Discarding `result` here threw that away and the caller
			// re-ran the snippet to get output it had already paid for.
			//
			// Rethrown as a typed error carrying `partialResult`, NOT folded into
			// the message: a caller that wants the logs needs them as fields, and
			// pasting a JSON blob into an error string is how they get lost again.
			if (parsed.result !== undefined && parsed.result !== null) {
				throw new UnityBridgeFailureError(parsed.error ?? "Live Unity bridge request failed.", parsed.result as T);
			}
			throw new Error(parsed.error ?? "Live Unity bridge request failed.");
		}
		await delay(LIVE_BRIDGE_POLL_MS, options.signal);
	}

	// Withdraw the request so the Editor does not execute it after we gave up.
	rmSync(requestPath, { force: true });
	const waitedSeconds = Math.round(timeoutMs / 1000);
	throw new Error(
		`Timed out after ${waitedSeconds}s waiting for the live Unity bridge (request: ${String(options.payload.type ?? "unknown")}). ` +
			`The request was withdrawn, but a long-running script may still be finishing inside Unity. ` +
			`Check unity_bridge_status, confirm the bridge plugin compiled without errors and the project is open, then retry.`,
	);
}

export const requestLiveUnitySessionContext = () =>
	requestLiveUnityBridge<import("./types.js").UnitySessionContext>({ payload: { type: "get-session-context" } });

export const requestLiveUnitySceneInfo = (categories: string[], label?: string) =>
	requestLiveUnityBridge<import("./types.js").UnitySceneInfo>({ payload: { type: "get-scene-info", categories, label } });

export const requestLiveUnityBones = (options: {
	gameObject?: string;
	bones?: string[];
	limit?: number;
	timeoutMs?: number;
}) =>
	requestLiveUnityBridge<import("./types.js").UnityBonesResult>({
		timeoutMs: options.timeoutMs,
		payload: {
			type: "get-bones",
			gameObject: options.gameObject,
			bones: options.bones,
			limit: options.limit,
		},
	});

export const requestLiveUnityInspect = (options: {
	gameObject: string;
	component?: string;
	includeChildComponents?: boolean;
	timeoutMs?: number;
}) =>
	requestLiveUnityBridge<import("./types.js").UnityInspectResult>({
		timeoutMs: options.timeoutMs,
		payload: {
			type: "inspect",
			gameObject: options.gameObject,
			component: options.component,
			includeChildComponents: options.includeChildComponents,
		},
	});

export const requestLiveUnitySetProperty = (options: {
	gameObject: string;
	component: string;
	property: string;
	value: string;
	space?: "scene" | "asset" | "global";
	timeoutMs?: number;
}) =>
	requestLiveUnityBridge<import("./types.js").UnitySetPropertyResult>({
		timeoutMs: options.timeoutMs,
		payload: {
			type: "set-property",
			gameObject: options.gameObject,
			component: options.component,
			property: options.property,
			value: options.value,
			space: options.space,
		},
	});

export const requestLiveUnitySetTransform = (options: {
	gameObject: string;
	position?: string;
	rotationEuler?: string;
	scale?: string;
	space?: "world" | "local";
	timeoutMs?: number;
}) =>
	requestLiveUnityBridge<import("./types.js").UnitySetTransformResult>({
		timeoutMs: options.timeoutMs,
		payload: {
			type: "set-transform",
			gameObject: options.gameObject,
			position: options.position,
			rotationEuler: options.rotationEuler,
			scale: options.scale,
			space: options.space,
		},
	});

/**
 * VRChat avatar-side helpers. Actions are strings because these helpers grow
 * with package versions; an unrecognised action returns the list of known ones,
 * which turns a typo into a self-correcting call rather than a dead end.
 */
export const requestLiveUnityVrc = (options: {
	action: string;
	gameObject?: string;
	element?: string;
	bone?: string;
	materialPath?: string;
	child?: string;
	timeoutMs?: number;
}) =>
	requestLiveUnityBridge<import("./types.js").UnityVrcResult>({
		timeoutMs: options.timeoutMs,
		payload: {
			type: "vrc",
			action: options.action,
			gameObject: options.gameObject,
			element: options.element,
			bone: options.bone,
			materialPath: options.materialPath,
			child: options.child,
		},
	});

/**
 * VRChat world-side helpers (light volumes, Udon, scene descriptor).
 *
 * A separate request type from the avatar `vrc` one, deliberately: a world
 * project has no avatar, so the two share almost no resolution logic, and a
 * world call in an avatar project says so instead of returning an empty report.
 */
export const requestLiveUnityWorld = (options: {
	action: string;
	gameObject?: string;
	behaviour?: string;
	lightType?: string;
	range?: number;
	intensity?: number;
	color?: string;
	tag?: string;
	inactive?: boolean;
	object?: string;
	component?: string;
	path?: string;
	target?: string;
	property?: string;
	value?: string;
	fields?: string;
	keepOriginal?: string;
	material?: string;
	shader?: string;
	position?: string;
	rotation?: string;
	shape?: string;
	size?: string;
	isTrigger?: string;
	clip?: string;
	volume?: string;
	spatial?: string;
	loop?: string;
	loadType?: string;
	timeoutMs?: number;
}) =>
	requestLiveUnityBridge<import("./types.js").UnityVrcResult>({
		timeoutMs: options.timeoutMs,
		payload: {
			type: "world",
			action: options.action,
			gameObject: options.gameObject,
			behaviour: options.behaviour,
			lightType: options.lightType,
			range: options.range,
			intensity: options.intensity,
			// The declared defaults are RE-APPLIED here rather than trusted to
			// the schema. TypeBox `default` is metadata: it is shown to the model
			// and is not applied on the way in, so an omitted colour crossed the
			// bridge as undefined and the C# side's own fallback decided it. That
			// is two places deciding what "default" means, and they are already
			// kept in step by hand -- which is how they drift. Same idiom as
			// unity/execute.ts's `saveAfter: options.saveAfter ?? true`.
			//
			// `size` has no default to re-apply: an omitted size means "keep the
			// size the collider already had", and inventing one would change what
			// an omitted argument does.
			color: options.color ?? "ffffff",
			tag: options.tag,
			inactive: options.inactive,
			object: options.object,
			component: options.component,
			path: options.path,
			target: options.target,
			// Names the world-package set actions: the field to write, the
			// value to write, and the comma-separated Field=Value list that
			// create-behaviour applies in one call.
			property: options.property,
			value: options.value,
			fields: options.fields,
			keepOriginal: options.keepOriginal,
			material: options.material,
			shader: options.shader,
			position: options.position,
			rotation: options.rotation,
			shape: options.shape,
			size: options.size,
			isTrigger: options.isTrigger,
			clip: options.clip,
			// Same reasoning as color above; these two are declared with a
			// default in index.ts and re-applied here.
			volume: options.volume ?? "1",
			spatial: options.spatial ?? "0",
			loop: options.loop,
			loadType: options.loadType,
		},
	});

export const requestLiveUnityOpenScene = (scenePath: string, projectPath?: string) =>
	requestLiveUnityBridge<import("./types.js").UnityOpenSceneResult>({
		// The one request that always has a project: workspace_init just
		// generated or adopted it. This is what makes a cold start possible
		// without opening the gate to every other call site.
		launchProjectPath: projectPath,
		payload: { type: "open-scene", scenePath },
	});

export const requestLiveUnityReloadScene = () =>
	requestLiveUnityBridge<import("./types.js").UnityReloadSceneResult>({ payload: { type: "reload-scene" } });

export const requestLiveUnityExecuteCSharp = (options: {
	code: string;
	saveAfter: boolean;
	label: string;
	saveBeforeScenePath?: string;
	/** Include the tail of the Editor log. Off unless asked: it also carries package noise from before the snippet ran. */
	logTail?: boolean;
	timeoutMs?: number;
	launchProjectPath?: string;
}) =>
	requestLiveUnityBridge<import("./types.js").UnityExecuteResult>({
		timeoutMs: options.timeoutMs,
		launchProjectPath: options.launchProjectPath,
		payload: {
			type: "execute-csharp",
			code: options.code,
			saveAfter: options.saveAfter,
			label: options.label,
			logTail: options.logTail === true,
		},
	});

export const requestLiveUnityRender = (options: {
	outputPath: string;
	width: number;
	height: number;
	cameraName?: string;
	timeoutMs?: number;
}) =>
	requestLiveUnityBridge<import("./types.js").UnityRenderResult>({
		timeoutMs: options.timeoutMs,
		payload: {
			type: "render",
			outputPath: options.outputPath,
			width: options.width,
			height: options.height,
			cameraName: options.cameraName ?? "",
		},
	});

export const requestLiveUnityCaptureView = (options: {
	outputPath: string;
	width: number;
	height: number;
	timeoutMs?: number;
}) =>
	requestLiveUnityBridge<import("./types.js").UnityCaptureResult>({
		timeoutMs: options.timeoutMs,
		payload: { type: "capture-view", outputPath: options.outputPath, width: options.width, height: options.height },
	});

export const requestLiveUnitySetTool = (tool: string) =>
	requestLiveUnityBridge<import("./types.js").UnityToolResult>({ payload: { type: "set-tool", tool } });

export const requestLiveUnityImportFbx = (options: {
	fbxPath: string;
	destination: string;
	name?: string;
	timeoutMs?: number;
}) =>
	requestLiveUnityBridge<import("./types.js").UnityImportFbxResult>({
		timeoutMs: options.timeoutMs,
		payload: {
			type: "import-fbx",
			fbxPath: options.fbxPath,
			destination: options.destination,
			name: options.name ?? "",
		},
	});

export const requestLiveUnityExportObj = (options: {
	outputPath: string;
	gameObjectNames: string[];
	timeoutMs?: number;
}) =>
	requestLiveUnityBridge<import("./types.js").UnityExportObjResult>({
		timeoutMs: options.timeoutMs,
		payload: { type: "export-obj", outputPath: options.outputPath, gameObjectNames: options.gameObjectNames },
	});

/**
 * Read-only: which real model files does this selection resolve to?
 *
 * Both renderer kinds must be walked to get a real answer. A MeshFilter-only
 * walk MISSES THE ENTIRE AVATAR, because the body is a SkinnedMeshRenderer and
 * never appears in a MeshFilter enumeration -- so "no source models" can be
 * true of a fully file-backed avatar purely because the wrong walk was used.
 *
 * Call this before export-source-models to see what will travel, or to explain
 * why a mesh cannot be handed over as a file at all.
 */
export const requestLiveUnityFindSourceModels = (options: {
	gameObjectNames?: string[];
	timeoutMs?: number;
}) =>
	requestLiveUnityBridge<import("./types.js").UnitySourceModelsResult>({
		timeoutMs: options.timeoutMs,
		payload: {
			type: "find-source-models",
			// An empty list means every scene root, which is the C# side's
			// documented behaviour, not an absent argument.
			gameObjectNames: options.gameObjectNames ?? [],
		},
	});

/**
 * Copies the resolved source model FILES into a staging directory.
 *
 * Copying rather than re-exporting is the point: the FBX Unity itself imported
 * is exactly what Blender then imports, so no rig or material description is
 * lost in translation. A source referenced by the scene but absent from disk is
 * reported in `missingSources` rather than thrown, because a library-only
 * checkout is a normal state, not an error.
 */
export const requestLiveUnityExportSourceModels = (options: {
	outputDir: string;
	gameObjectNames?: string[];
	timeoutMs?: number;
}) =>
	requestLiveUnityBridge<import("./types.js").UnitySourceModelsResult>({
		timeoutMs: options.timeoutMs,
		payload: {
			type: "export-source-models",
			outputDir: options.outputDir,
			gameObjectNames: options.gameObjectNames ?? [],
		},
	});

/**
 * Places a prefab asset into the active scene, in one call.
 *
 * Instantiate and place TOGETHER on purpose. Split across two calls, the caller
 * has to name the object it just created, and that is the step that cannot be
 * done reliably: a prefab's children repeat their names in every instance, so
 * once the asset is placed twice a bare name is ambiguous -- and the resolver
 * refuses ambiguity rather than guessing. The returned `path` is the stable
 * handle for everything after this call.
 */
export const requestLiveUnityInstantiatePrefab = (options: {
	prefabPath: string;
	parent?: string;
	name?: string;
	position?: string;
	rotation?: string;
	scale?: string;
	timeoutMs?: number;
}) =>
	requestLiveUnityBridge<import("./types.js").UnityPrefabInstanceResult>({
		timeoutMs: options.timeoutMs,
		payload: {
			type: "instantiate-prefab",
			prefabPath: options.prefabPath,
			parent: options.parent ?? "",
			name: options.name ?? "",
			position: options.position ?? "",
			rotation: options.rotation ?? "",
			scale: options.scale ?? "",
		},
	});

/**
 * Reads a prefab asset WITHOUT instantiating it.
 *
 * The point is to know what placing it will do before the scene gains an object
 * you then have to delete. `materials` here deliberately includes materials
 * embedded in the source FBX, which `unity_scene_info { categories: ["materials"] }`
 * cannot see -- for a model that arrived from Blender, that listing reporting
 * nothing is not evidence the materials were lost.
 */
export const requestLiveUnityPrefabInfo = (options: {
	prefabPath: string;
	timeoutMs?: number;
}) =>
	requestLiveUnityBridge<import("./types.js").UnityPrefabInfo>({
		timeoutMs: options.timeoutMs,
		payload: { type: "prefab-info", prefabPath: options.prefabPath },
	});

/**
 * Builds ONE composite prefab whose members are NESTED PREFAB INSTANCES.
 *
 * The member list is forwarded whole rather than parsed here, so the C# side is
 * the only place that decides what a member is -- and it refuses a member
 * before building anything, which is why a bad path in the last entry does not
 * leave a half-written asset behind.
 */
export const requestLiveUnityComposePrefab = (options: {
	destination: string;
	name: string;
	members: import("./types.js").UnityComposePrefabMember[];
	timeoutMs?: number;
}) =>
	requestLiveUnityBridge<import("./types.js").UnityComposePrefabResult>({
		timeoutMs: options.timeoutMs,
		payload: {
			type: "compose-prefab",
			destination: options.destination,
			name: options.name,
			members: options.members,
		},
	});

/**
 * Measures mesh/transform relationships and returns numbers.
 *
 * `timeoutMs` is a caller-supplied override rather than a raised default,
 * because a probe scales with the size of the meshes it is pointed at: a
 * `topology` pass walks every triangle of every named mesh, and only the caller
 * knows whether the object list is one prop or a whole avatar.
 */
export const requestLiveUnityGeometryProbe = (options: {
	objects?: string[];
	pairs?: string[][];
	checks?: import("./types.js").UnityGeometryProbeCheck[];
	tolerance?: number;
	expectedAxis?: "X" | "Y" | "Z";
	timeoutMs?: number;
}) =>
	requestLiveUnityBridge<import("./types.js").UnityGeometryProbeResult>({
		timeoutMs: options.timeoutMs,
		payload: {
			type: "geometry-probe",
			// An empty list is the C# side's documented "probe everything with
			// a mesh" behaviour, not an absent argument.
			objects: options.objects ?? [],
			pairs: options.pairs ?? [],
			checks: options.checks ?? [],
			tolerance: options.tolerance,
			expectedAxis: options.expectedAxis,
		},
	});

/**
 * Where the Editor's logs actually are.
 *
 * Asked of the live Editor rather than derived from a path, because the Editor
 * writes to the XDG config home and which file that is depends on how it was
 * launched. The C# side probes each candidate and reports `exists`, so a caller
 * is never handed a path that does not exist.
 */
export const requestLiveUnityLog = (options: { timeoutMs?: number } = {}) =>
	requestLiveUnityBridge<import("./types.js").UnityLogResult>({
		timeoutMs: options.timeoutMs,
		payload: { type: "get-log" },
	});

/** The plugin's captured compiler output, named in Core.Configure. */
const COMPILER_MESSAGES_FILE = "compile-messages.json";

/**
 * What one of the plugin's compile handlers wrote, read without throwing.
 *
 * The plugin's OWN finishedAt is preferred over the file's mtime, because a
 * copy or a backup can move the file without anything having compiled.
 *
 * `completed` is what makes this usable as a verdict rather than a ping: the
 * cycle-start marker is written the moment a compile begins, so a file that
 * exists and is newer than the wait says nothing except that a compile started.
 */
const readCapture = (
	path: string,
): {
	finishedAtMs: number;
	completed: boolean;
	assembly: string;
	errors: string[];
	warnings: string[];
} | null => {
	try {
		const parsed = JSON.parse(readFileSync(path, "utf8")) as {
			finishedAt?: string;
			completed?: unknown;
			assembly?: unknown;
			errors?: unknown;
			warnings?: unknown;
		};
		const finishedAt = Date.parse(parsed.finishedAt ?? "");
		return {
			finishedAtMs: Number.isNaN(finishedAt) ? mtimeOf(path) ?? 0 : finishedAt,
			completed: parsed.completed === true,
			assembly: typeof parsed.assembly === "string" ? parsed.assembly : "",
			errors: Array.isArray(parsed.errors) ? (parsed.errors as string[]) : [],
			warnings: Array.isArray(parsed.warnings) ? (parsed.warnings as string[]) : [],
		};
	} catch {
		return null;
	}
};

/** How often the plugin re-touches its heartbeat: PollIntervalSeconds in Core.cs. */
const PLUGIN_POLL_SECONDS = 0.5;

export interface AwaitCompileOptions {
	/** Budget for the compile. A full script compile takes 4-6 minutes. */
	timeoutMs?: number;
	signal?: AbortSignal;
}

/** Default budget: a slow full compile is 4-6 min, so 10 leaves room over it. */
export const LIVE_BRIDGE_AWAIT_COMPILE_TIMEOUT_MS = 600_000;

const mtimeOf = (path: string): number | null => {
	try {
		return statSync(path).mtimeMs;
	} catch {
		return null;
	}
};

/**
 * Block until the live Editor's script compilation has settled, then report the
 * compiler's verdict. This exists because a compile outlasts any single bridge
 * request (4-6 minutes against a 30s transport timeout), and because sleeping
 * then re-reading status is guesswork: the caller has to invent a duration, and
 * a duration that is too short returns a report that looks identical to a clean
 * one.
 *
 * It waits by WATCHING FILES, not by polling compile-status. Polling would take
 * the single bridge lock on every tick and contend with whatever else is
 * running; a compile also ends in a domain reload, during which the plugin
 * cannot answer anything at all. The heartbeat and the captured compiler output
 * are both files, so both survive a reload.
 *
 * "Settled" means a capture the plugin COMPLETED was written after this call
 * began. Deliberately not the heartbeat: the plugin touches it before it tests
 * isCompiling and then returns, so it ticks through a whole compile, and during
 * the compile itself the main thread is held and it cannot move at all. It is
 * only reported afterwards, as `editorResponding`.
 *
 * There is deliberately no bridge request for this. A handler cannot wait: Poll
 * runs on the Editor's main thread, which is the thread the compile needs in
 * order to finish, so a blocking handler would deadlock the thing it is waiting
 * for.
 *
 * The hard part is the race at entry. This can be called before Unity has even
 * noticed the edit, and at that moment "no compile is running" is
 * indistinguishable from "the compile already finished", so a wait keyed to the
 * compile flag reports a clean build for a broken one. What closes it is the
 * capture's own completion marker plus its timestamp: a compile Unity ran has
 * finished writing one, and a compile that has not STARTED cannot have written
 * anything newer than the moment this call began. A capture that merely records
 * a cycle starting is not a verdict -- on a 4-6 minute compile that marker is
 * most of the wait -- so only a completed one settles.
 */
export async function requestLiveUnityAwaitCompile(
	options: AwaitCompileOptions = {},
): Promise<import("./types.js").UnityAwaitCompileResult> {
	const timeoutMs = options.timeoutMs ?? LIVE_BRIDGE_AWAIT_COMPILE_TIMEOUT_MS;
	const { bridgeDir } = getLiveBridgePaths();
	const heartbeatPath = join(bridgeDir, LIVE_BRIDGE_HEARTBEAT_FILE);
	const messagesPath = join(bridgeDir, COMPILER_MESSAGES_FILE);

	if (!isLiveBridgeAlive()) {
		throw new Error(
			"The Unity Editor is not responding: its bridge heartbeat is stale, so there is nothing to wait for. " +
				"Run unity_bridge_status to see why, and confirm the project is open, before awaiting a compile.",
		);
	}

	if (mtimeOf(heartbeatPath) === null) {
		throw new Error(`The bridge heartbeat file is missing (${heartbeatPath}), so a compile cannot be awaited.`);
	}
	const started = Date.now();
	const deadline = started + timeoutMs;

	// A capture already on disk describes code as it was BEFORE this call, so it
	// cannot be the verdict for a compile that is about to run. Its CONTENT is
	// still worth having -- a project whose every script is broken keeps
	// reporting needsCompile=true forever, and waiting for a capture from a
	// compile Unity never starts would burn the whole budget -- so it is
	// remembered as the fallback answer.
	//
	// Its TIMESTAMP is what decides settle, and that is read from the file at
	// every tick rather than snapshotted here: the compare is against when the
	// wait STARTED, not against a fresh read, because the first tick runs a
	// moment later and a baseline taken inside the loop would accept a capture
	// that was already on disk.
	const stale = readCapture(messagesPath);
	// The plugin polls on the main thread and touches the heartbeat once per
	// tick, so polling faster than this only spins. It also cannot detect a
	// compile faster than this, so it would gain nothing.
	const tickMs = Math.ceil(PLUGIN_POLL_SECONDS * 1000);

	// Liveness is NOT checked during the wait, and that is deliberate.
	// LIVE_BRIDGE_HEARTBEAT_STALE_MS is 10s, because that is how long an IDLE
	// Editor's silence means it is gone. During a compile it means nothing of
	// the sort: the domain reload at the end measured 6-8s on its own, and the
	// compile itself holds the main thread for minutes, so Poll cannot run and
	// the heartbeat cannot move. Treating that silence as death aborted the
	// wait 8s into every real compile -- observed live, before this was removed.
	//
	// So the answer is reported rather than guessed at: a wait that runs out
	// says whether the heartbeat moved at all, and a caller that cares about the
	// difference runs unity_bridge_status.
	//
	// Whether the Editor's heartbeat ADVANCED during the wait. The entry mtime is
	// not a fallback: a plugin that has simply stopped polling looks identical
	// to one that has gone away, and reusing entry would report the first as
	// the second.
	const beatAtEntry = mtimeOf(heartbeatPath);
	let beatMoved = false;

	for (;;) {
		await delay(tickMs, options.signal);

		if (Date.now() >= deadline) break;

		// Only a COMPLETED capture settles the wait. The plugin also writes a
		// marker the moment a cycle starts, which on a 4-6 minute compile is
		// most of the wait, so settling on any new file would answer while the
		// compiler was still running.
		const capture = readCapture(messagesPath);
		if (capture && capture.completed && capture.finishedAtMs > started) break;

		const beat = mtimeOf(heartbeatPath);
		if (beat !== null && beatAtEntry !== null && beat > beatAtEntry) beatMoved = true;
	}

	const waitedMs = Date.now() - started;
	// Re-read rather than reuse the loop's last value, which is scoped to it.
	const settledCapture = readCapture(messagesPath);

	// The last thing known about an unsettled wait is a compile that has NOT
	// finished, which is not a status. "timeout" is returned rather than thrown
	// so the caller reads the absence of a verdict as a result rather than as
	// a failure to ask.
	if (!settledCapture || settledCapture.finishedAtMs <= started || !settledCapture.completed) {
		return {
			status: "timeout",
			errors: stale?.errors ?? [],
			warnings: stale?.warnings ?? [],
			source: "none",
			waitedMs,
			editorResponding: beatMoved,
			// A capture that is newer than the wait but not completed is the one
			// case where more is known than "nothing happened": the plugin
			// recorded that a cycle began and the budget ran out before it ended.
			// That is a compile still running, so it is said rather than left to
			// be read as a hang.
			compileInFlight: settledCapture !== null && settledCapture.finishedAtMs > started,
		};
	}

	const status = await readCompileStatusBestEffort();
	if (!status) {
		throw new Error(
			`The Editor settled after ${Math.round(waitedMs / 1000)}s but would not report its compile status, ` +
				"so no verdict can be given. Retry once the Editor is responsive.",
		);
	}

	// The compiler is only said to have spoken when the capture names the
	// assembly it built; that is what makes its verdict the final one.
	if (settledCapture.assembly.length > 0) {
		return {
			status: status.errorCount > 0 ? "failed" : "compiled",
			errors: [...status.errors],
			warnings: [],
			source: status.source,
			waitedMs,
			editorResponding: beatMoved,
		};
	}

	// It did not speak -- a no-op compile, or a build Unity chose not to run --
	// so needsCompile is the only evidence there is, and it is kept. That is the
	// case it was written for: a project whose every script is broken logs its
	// errors once and then stops, so an empty error list would otherwise report
	// a broken project as clean.
	const errors = [...status.errors];
	if (status.needsCompile) {
		for (const script of status.scriptsNeedingCompile) {
			errors.push(`${script}  (never compiled)`);
		}
	}

	return {
		status: status.errorCount > 0 || status.needsCompile ? "failed" : "compiled",
		errors,
		warnings: [],
		source: status.source,
		waitedMs,
		editorResponding: beatMoved,
	};
}

/**
 * compile-status as evidence for the caller.
 *
 * Null rather than a throw: it is consulted while reporting a timeout, where the
 * useful message is what the Editor last said, and a failure to read it must not
 * replace that with a second, less useful failure.
 */
async function readCompileStatusBestEffort(): Promise<import("./types.js").UnityCompileStatus | null> {
	try {
		return await requestLiveUnityBridge<import("./types.js").UnityCompileStatus>({
			payload: { type: "compile-status" },
			timeoutMs: LIVE_BRIDGE_COLD_START_TIMEOUT_MS,
		});
	} catch {
		return null;
	}
}

export function listStaleResponseFiles(): string[] {
	const { responsesDir } = getLiveBridgePaths();
	try {
		return readdirSync(responsesDir).filter((name) => name.endsWith(".json"));
	} catch {
		return [];
	}
}
