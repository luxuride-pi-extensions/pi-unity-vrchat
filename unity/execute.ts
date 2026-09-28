/**
 * unity_execute_csharp and unity_scene_info.
 *
 * Both run *through* the live bridge rather than headless, because a Unity
 * scene only exists inside an Editor process. (Blender could fall back to
 * `blender --background`; Unity cannot run a project without an Editor, so a
 * disconnected bridge is a hard error here rather than a slower path.)
 */

import { copyFileSync, mkdirSync, readFileSync, writeFileSync } from "node:fs";
import { join, resolve } from "node:path";

import { requestLiveUnityExecuteCSharp, requestLiveUnitySceneInfo } from "./bridge.js";
import {
	ensureWorkspaceScriptExists,
	fileExists,
	getIterationPaths,
	loadWorkspaceManifest,
	stageScratchFile,
} from "./workspace.js";
import {
	UNITY_SCENE_INFO_CATEGORIES,
	UNITY_SCENE_INFO_DEFAULT_CATEGORIES,
	type UnitySceneInfo,
	type UnitySceneInfoCategory,
} from "./types.js";

export interface ExecuteCSharpOptions {
	cwd: string;
	workspace?: string;
	workspacePath?: string;
	scriptPath?: string;
	inlineCode?: string;
	saveAfter?: boolean;
	timeoutSeconds?: number;
	saveBefore?: boolean;
	signal?: AbortSignal;
}

export interface ExecuteCSharpResult {
	workspacePath: string;
	iteration: number;
	stdout: string;
	stderr: string;
	exitCode: number;
	changed: boolean;
	saved: boolean;
	sourcePath: string;
	checkpointPath?: string;
}

/** Saves the open scene, then copies the resulting .unity file aside. */
const SAVE_SCENE_SNIPPET = [
	"using UnityEditor;",
	"using UnityEditor.SceneManagement;",
	"public static class PiScript",
	"{",
	"\tpublic static void Run() { EditorSceneManager.SaveOpenScenes(); }",
	"}",
].join("\n");

export async function unityExecuteCSharp(options: ExecuteCSharpOptions): Promise<ExecuteCSharpResult> {
	const workspacePath = options.workspacePath ?? resolve(options.cwd, options.workspace ?? ".");
	const manifest = loadWorkspaceManifest(workspacePath);

	let code: string;
	let sourcePath: string;
	if (options.inlineCode?.trim()) {
		sourcePath = stageScratchFile("script", options.inlineCode);
		code = options.inlineCode;
	} else {
		sourcePath = options.scriptPath
			? resolve(options.cwd, options.scriptPath)
			: ensureWorkspaceScriptExists(workspacePath);
		if (!fileExists(sourcePath)) {
			throw new Error(
				`C# script not found: ${sourcePath}. Write the script first, or pass inlineCode.`,
			);
		}
		code = readFileSync(sourcePath, "utf8");
	}

	// saveBefore snapshots the scene on disk, so a bad script can be undone by
	// restoring the checkpoint file. Unity owns scene serialization, so the
	// save has to happen in the Editor before the file can be copied.
	let checkpointPath: string | undefined;
	if (options.saveBefore) {
		const sceneFile = resolve(manifest.projectPath, manifest.scenePath);
		try {
			await requestLiveUnityExecuteCSharp({
				code: SAVE_SCENE_SNIPPET,
				saveAfter: false,
				label: "save-before",
				timeoutMs: (options.timeoutSeconds ?? 120) * 1000,
				launchProjectPath: manifest.projectPath,
			});
		} catch (error) {
			throw new Error(
				`saveBefore failed, so nothing was executed. ${error instanceof Error ? error.message : String(error)}`,
			);
		}
		if (fileExists(sceneFile)) {
			checkpointPath = join(workspacePath, "pre-execute.unity");
			copyFileSync(sceneFile, checkpointPath);
		} else {
			// An unsaved scene has no file to copy; say so instead of claiming
			// a checkpoint exists that cannot restore anything.
			throw new Error(
				`saveBefore could not snapshot the scene: ${sceneFile} does not exist yet. ` +
					`Save the scene in Unity once (File > Save As), then retry.`,
			);
		}
	}

	const result = await requestLiveUnityExecuteCSharp({
		code,
		saveAfter: options.saveAfter ?? true,
		label: `execute:${sourcePath}`,
		timeoutMs: (options.timeoutSeconds ?? 120) * 1000,
		launchProjectPath: manifest.projectPath,
	});

	return {
		workspacePath,
		iteration: manifest.latestIteration,
		stdout: result.stdout,
		stderr: result.stderr,
		exitCode: result.exitCode,
		changed: result.changed,
		saved: options.saveAfter ?? true,
		sourcePath,
		checkpointPath,
	};
}

export interface SceneInfoOptions {
	cwd: string;
	workspace?: string;
	workspacePath?: string;
	categories?: UnitySceneInfoCategory[];
	signal?: AbortSignal;
}

export interface SceneInfoResult {
	workspacePath: string;
	projectPath: string;
	activeScene: string;
	unityVersion: string;
	iteration: number;
	sceneInfoPath: string;
	sceneInfo: UnitySceneInfo;
}

/** Validate the category list once, at the edge, so the C# side stays dumb. */
export function resolveSceneInfoCategories(
	categories: UnitySceneInfoCategory[] | undefined,
): UnitySceneInfoCategory[] {
	if (!categories?.length) return UNITY_SCENE_INFO_DEFAULT_CATEGORIES;
	for (const category of categories) {
		if (!UNITY_SCENE_INFO_CATEGORIES.includes(category)) {
			throw new Error(
				`Unknown scene_info category: ${category}. Valid categories: ${UNITY_SCENE_INFO_CATEGORIES.join(", ")}`,
			);
		}
	}
	return categories;
}

export async function getSceneInfo(
	options: SceneInfoOptions,
): Promise<SceneInfoResult> {
	const workspacePath = options.workspacePath ?? resolve(options.cwd, options.workspace ?? ".");
	const manifest = loadWorkspaceManifest(workspacePath);
	const categories = resolveSceneInfoCategories(options.categories);

	const sceneInfo = await requestLiveUnitySceneInfo(categories, `scene-info:${categories.join(",")}`);

	const iteration = manifest.latestIteration > 0 ? manifest.latestIteration : 1;
	const { iterationDir } = getIterationPaths(workspacePath, iteration);
	mkdirSync(iterationDir, { recursive: true });
	const sceneInfoPath = join(iterationDir, "scene-info.json");
	writeFileSync(sceneInfoPath, `${JSON.stringify(sceneInfo, undefined, 2)}\n`, "utf8");

	return {
		workspacePath,
		projectPath: manifest.projectPath,
		activeScene: sceneInfo.activeScene,
		unityVersion: sceneInfo.unityVersion,
		iteration,
		sceneInfoPath,
		sceneInfo,
	};
}
