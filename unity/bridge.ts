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
import { existsSync, mkdirSync, readFileSync, readdirSync, renameSync, rmSync, writeFileSync } from "node:fs";
import { join } from "node:path";

import { getUnityBridgeDir } from "../config.js";
import { ensureUnityLaunched, isLiveBridgeAlive } from "../launch.js";
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
				throw new Error(
					`The Unity bridge wrote an unreadable response: ${error instanceof Error ? error.message : String(error)}`,
				);
			}
			if (parsed.ok) return parsed.result as T;
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
			color: options.color,
		},
	});

export const requestLiveUnityOpenScene = (scenePath: string) =>
	requestLiveUnityBridge<import("./types.js").UnityOpenSceneResult>({ payload: { type: "open-scene", scenePath } });

export const requestLiveUnityReloadScene = () =>
	requestLiveUnityBridge<import("./types.js").UnityReloadSceneResult>({ payload: { type: "reload-scene" } });

export const requestLiveUnityExecuteCSharp = (options: {
	code: string;
	saveAfter: boolean;
	label: string;
	saveBeforeScenePath?: string;
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

export function listStaleResponseFiles(): string[] {
	const { responsesDir } = getLiveBridgePaths();
	try {
		return readdirSync(responsesDir).filter((name) => name.endsWith(".json"));
	} catch {
		return [];
	}
}
