/**
 * unity_execute_csharp and unity_scene_info.
 *
 * Both run *through* the live bridge rather than headless, because a Unity
 * scene only exists inside an Editor process. (Blender could fall back to
 * `blender --background`; Unity cannot run a project without an Editor, so a
 * disconnected bridge is a hard error here rather than a slower path.)
 */

import { copyFileSync, mkdirSync, readFileSync, writeFileSync } from "node:fs";
import { isAbsolute, join, resolve } from "node:path";

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

/**
 * Where a relative `scriptPath` belongs, and how to say so when it is missing.
 *
 * The documented layout is `<workspace>/modules/<name>/script.cs`
 * (unity/skills/unity-modules/SKILL.md, unity/agents/unity-worker.md), and
 * unity_module_run reads its script from exactly there. So a relative
 * `scriptPath` is a path *inside the workspace*, and resolving it against the
 * session cwd was simply the wrong root: it broke the one case the feature
 * exists for -- adopting a project elsewhere on disk and running its modules
 * from an unrelated session directory.
 *
 * The session cwd is still tried, but only AFTER the workspace and only when
 * the file is really there, so a workspace that happens to sit inside the cwd
 * still works. That fallback also keeps a genuine cwd-relative script usable
 * for a session that never adopted anything.
 *
 * Absolute paths are used as given, still checked for existence: an absolute
 * path that does not exist is a typo, not a root-selection question.
 *
 * On failure every candidate absolute path is named in the error. The old
 * message named exactly one path and never said which root produced it, which
 * is why the bug in #3 was hard to diagnose from the tool output alone.
 */
export function resolveScriptPath(scriptPath: string, workspacePath: string, cwd: string): string {
	const trimmed = scriptPath.trim();
	const notFound = (candidates: string[]): Error =>
		new Error(
			`C# script not found: ${trimmed}. ` +
				`Tried: ${candidates.join(", ")}. ` +
				`A relative scriptPath resolves against the workspace root first ` +
				`(${workspacePath}), then the session cwd (${cwd}). ` +
				`Pass an absolute path if the script lives somewhere else, or write the script first. ` +
				`You can also pass inlineCode instead.`,
		);

	if (isAbsolute(trimmed)) {
		if (!fileExists(trimmed)) throw notFound([trimmed]);
		return trimmed;
	}

	// Order matters and is the fix: workspace, then cwd. Deduped because a
	// workspace is often a subdirectory of the cwd, and naming the same path
	// twice reads as "two roots tried" when only one was.
	const roots = [workspacePath, cwd].filter((root, index, all) => all.indexOf(root) === index);
	for (const root of roots) {
		const candidate = resolve(root, trimmed);
		if (fileExists(candidate)) return candidate;
	}
	throw notFound(roots.map((root) => resolve(root, trimmed)));
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
			? resolveScriptPath(options.scriptPath, workspacePath, options.cwd)
			: ensureWorkspaceScriptExists(workspacePath);
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
