/**
 * Creating or adopting a Unity workspace.
 *
 * Two paths, because the user asked for both:
 *   - generate: copy the bundled template (a real, openable 3D project with a
 *     Main Camera + Directional Light scene) and point a manifest at it.
 *   - adopt:    the user points pi at an existing project (a configured VRChat
 *     world, a game in progress). pi does NOT generate anything; it installs
 *     only the bridge plugin plus the manifest, so a real project is never
 *     scaffolded over.
 *
 * In both cases the scene is created by *Unity* on first open, not by pi. pi
 * never hand-writes scene YAML into a project it did not generate: a subtly
 * wrong .unity file fails to deserialize and looks like data loss to the user.
 */

import { cpSync, existsSync, mkdirSync, readdirSync, readFileSync, writeFileSync, type Dirent } from "node:fs";
import { join, resolve } from "node:path";

import { getUnityBridgeDir, getUnityProjectTemplateDir } from "../config.js";
import { requestLiveUnityOpenScene } from "./bridge.js";
import { LIVE_BRIDGE_INIT_TIMEOUT_MS } from "./utils.js";
import {
	createWorkspaceId,
	DEFAULT_SCENE_NAME,
	fileExists,
	getManifestPath,
	installBridgeIntoProject,
	nowIso,
	readManifest,
	MANIFEST_VERSION,
	type UnityWorkspaceManifest,
} from "./workspace.js";

export type UnityTemplate = "3d" | "blank";

/** A directory counts as a Unity project if it has Assets/ and ProjectSettings/. */
export function looksLikeUnityProject(path: string): boolean {
	return existsSync(join(path, "Assets")) && existsSync(join(path, "ProjectSettings"));
}

export interface WorkspaceInitOptions {
	workspacePath: string;
	template?: UnityTemplate;
	sourceProject?: string;
	continueExisting?: boolean;
}

export interface WorkspaceInitResult {
	workspacePath: string;
	created: boolean;
	adopted: boolean;
	scenePath: string;
	manifestPath: string;
	projectReady: boolean;
	message: string;
	/** The project to launch the Editor with. Never blank: init just made it. */
	projectPath: string;
}

/**
 * Find a real scene in an adopted project, or null when it has none.
 *
 * The manifest's scenePath is not cosmetic: open-scene refuses a path that does
 * not exist, and every module checkpoint copies that same file. So adopting a
 * project whose scenes are not named Main.unity — which is most real projects,
 * and every VRChat world — used to produce a workspace that initialised
 * successfully and then failed on its first scene operation, with a message
 * about a scene the user never had.
 *
 * The order is deliberate. EditorBuildSettings is what the project itself
 * considers its scenes, so honouring it is the most faithful answer. Then the
 * conventional Assets/Scenes/Main.unity, which is what a generated project has
 * and what the manifest still defaults to. Then any .unity under Assets/,
 * excluding Packages/, because an adopted project with scenes in an unusual
 * place is normal and refusing to manage it would be worse than guessing.
 */
export function findProjectScene(projectPath: string): string | null {
	const buildSettings = join(projectPath, "ProjectSettings", "EditorBuildSettings.asset");
	if (existsSync(buildSettings)) {
		const raw = readFileSync(buildSettings, "utf8");
		// Lines look like:  - {fileID: 102900000, guid: <32 hex>, type: 3}
		for (const line of raw.split("\n")) {
			const guid = /guid:\s*([0-9a-f]{32})/i.exec(line)?.[1];
			if (!guid) continue;
			const match = findSceneByGuid(projectPath, guid);
			if (match) return match;
		}
	}
	const conventional = join("Assets", "Scenes", DEFAULT_SCENE_NAME);
	if (existsSync(join(projectPath, conventional))) return conventional;
	return firstSceneUnderAssets(projectPath);
}

/** Resolve a scene GUID through Assets/.meta files, which is where it lives. */
function findSceneByGuid(projectPath: string, guid: string): string | null {
	const assets = join(projectPath, "Assets");
	const stack = [assets];
	while (stack.length > 0) {
		const dir = stack.pop()!;
		let entries: Dirent[];
		try {
			entries = readdirSync(dir, { withFileTypes: true });
		} catch {
			continue;
		}
		for (const entry of entries) {
			const full = join(dir, entry.name);
			if (entry.isDirectory()) {
				stack.push(full);
				continue;
			}
			if (!entry.name.endsWith(".unity.meta")) continue;
			if (!readFileSync(full, "utf8").includes(guid)) continue;
			// The match is on the SIDECAR file, so strip its extension: the
			// manifest must name the scene, and open-scene does File.Exists on
			// exactly that path.
			return toProjectRelative(projectPath, full.slice(0, -".meta".length));
		}
	}
	return null;
}

/** Depth-first, alphabetical, so the answer is stable across runs. */
function firstSceneUnderAssets(projectPath: string): string | null {
	const assets = join(projectPath, "Assets");
	const queue = [assets];
	while (queue.length > 0) {
		const dir = queue.shift()!;
		let entries: Dirent[];
		try {
			entries = readdirSync(dir, { withFileTypes: true });
		} catch {
			continue;
		}
		for (const entry of [...entries].sort((a, b) => a.name.localeCompare(b.name))) {
			const full = join(dir, entry.name);
			if (entry.isDirectory()) {
				queue.push(full);
				continue;
			}
			if (entry.name.endsWith(".unity")) {
				return toProjectRelative(projectPath, full);
			}
		}
	}
	return null;
}

/**
 * A project-relative path with forward slashes.
 *
 * readdir gives platform separators and the walk builds absolute paths, but
 * the manifest stores a Unity-style path that is compared against
 * resolve(projectPath, scenePath) and sent to the Editor verbatim. Normalising
 * here is what keeps the two from disagreeing on a backslash.
 */
function toProjectRelative(projectPath: string, absolute: string): string {
	return absolute.slice(projectPath.length + 1).split("\\").join("/");
}

function writeFreshManifest(
	workspacePath: string,
	options: { adopted: boolean; sourceProject?: string },
): UnityWorkspaceManifest {
	// An adopted project keeps its own scene names, so the generated default is
	// only correct for a project pi actually generated. See findProjectScene.
	const detected = findProjectScene(workspacePath);
	const manifest: UnityWorkspaceManifest = {
		version: MANIFEST_VERSION,
		id: createWorkspaceId(),
		projectPath: workspacePath,
		// Falls back to the documented default so the manifest stays well-formed
		// for a generated project Unity has not created a scene for yet.
		scenePath: detected ?? join("Assets", "Scenes", DEFAULT_SCENE_NAME),
		createdAt: nowIso(),
		updatedAt: nowIso(),
		latestIteration: 0,
		moduleRuns: [],
		savedViews: [],
		renderOutputs: [],
		adopted: options.adopted,
	};
	writeFileSync(getManifestPath(workspacePath), `${JSON.stringify(manifest, undefined, 2)}\n`, "utf8");
	return manifest;
}

export function ensureWorkspaceInitialized(options: WorkspaceInitOptions): WorkspaceInitResult {
	const workspacePath = resolve(options.workspacePath);
	const manifestPath = getManifestPath(workspacePath);

	if (fileExists(manifestPath)) {
		const manifest = readManifest(manifestPath);
		return {
			workspacePath,
			created: false,
			adopted: manifest.adopted,
			scenePath: manifest.scenePath,
			manifestPath,
			projectReady: looksLikeUnityProject(workspacePath),
			projectPath: workspacePath,
			message: `Workspace already exists (adopted: ${manifest.adopted}). Reusing it.`,
		};
	}

	if (options.continueExisting) {
		throw new Error(`Workspace does not exist: ${workspacePath}`);
	}

	if (existsSync(workspacePath)) {
		const entries = readdirSync(workspacePath);
		// An empty dir is fine to adopt; a populated one that is not a Unity
		// project is almost certainly a mistake worth stopping for.
		if (entries.length > 0 && !looksLikeUnityProject(workspacePath)) {
			throw new Error(
				`${workspacePath} already contains files but is not a Unity project (no Assets/ + ProjectSettings/). ` +
					`Point pi at an existing Unity project directory, or choose an empty workspace path.`,
			);
		}
	}

	mkdirSync(workspacePath, { recursive: true });

	let adopted = false;
	let message: string;

	if (options.sourceProject) {
		const source = resolve(options.sourceProject);
		if (!looksLikeUnityProject(source)) {
			throw new Error(
				`--source-project must be a Unity project directory (containing Assets/ and ProjectSettings/): ${source}`,
			);
		}
		if (source === workspacePath) {
			adopted = true;
			message = `Adopting the existing Unity project in place: ${source}. Only the bridge plugin and manifest are added.`;
		} else {
			// Copy the project so pi's work never mutates the user's real one.
			cpSync(source, workspacePath, { recursive: true });
			adopted = true;
			message = `Copied the existing Unity project from ${source}. Edit the copy, not the original.`;
		}
	} else {
		const template = options.template ?? "3d";
		if (template !== "3d" && template !== "blank") {
			throw new Error(`Unsupported Unity template: ${template}. Use "3d" or "blank".`);
		}
		if (template === "3d") {
			const templateDir = getUnityProjectTemplateDir();
			if (!existsSync(templateDir)) {
				throw new Error(`Bundled Unity project template is missing: ${templateDir}`);
			}
			cpSync(templateDir, workspacePath, { recursive: true });
			message = "Generated a new 3D Unity project from the bundled template.";
		} else {
			mkdirSync(join(workspacePath, "Assets"), { recursive: true });
			mkdirSync(join(workspacePath, "ProjectSettings"), { recursive: true });
			message = "Generated a blank Unity project (no scene yet; Unity creates one on first open).";
		}
	}

	// Installing the bridge is what makes the project pi-managed. It is
	// refreshed on every init so an older plugin never survives an upgrade.
	installBridgeIntoProject(workspacePath, getUnityBridgeDir());
	const manifest = writeFreshManifest(workspacePath, { adopted });

	return {
		workspacePath,
		created: true,
		adopted,
		scenePath: manifest.scenePath,
		manifestPath,
		projectReady: looksLikeUnityProject(workspacePath),
		projectPath: workspacePath,
		message,
	};
}

/**
 * Opens the workspace's scene in the live Editor.
 *
 * Failures are reported as a message rather than thrown: a fresh project has
 * no scene until Unity generates one, and that is not an error the caller can
 * do anything about by retrying harder.
 */
export async function openWorkspaceInLiveUnity(
	result: WorkspaceInitResult,
): Promise<{ openedInLiveUnity: boolean; liveUnityMessage: string }> {
	try {
		await requestLiveUnityOpenScene(result.scenePath, result.projectPath);
		return { openedInLiveUnity: true, liveUnityMessage: "" };
	} catch (error) {
		return {
			openedInLiveUnity: false,
			liveUnityMessage:
				`The project is initialized but the scene is not open in Unity yet. ` +
				`Open ${result.workspacePath} in the Unity Editor (it will generate its first scene and compile the pi bridge plugin, which takes a minute). ` +
				`Bridge said: ${error instanceof Error ? error.message : String(error)}`,
		};
	}
}

/**
 * The tool-facing entry point: initialize, then try to surface the scene in the
 * live Editor. Never throws on the live half — a project that has never been
 * opened is a normal intermediate state, not a failure to report as an error.
 */
export async function unityWorkspaceInit(
	options: WorkspaceInitOptions,
): Promise<WorkspaceInitResult & { openedInLiveUnity: boolean; liveUnityMessage: string }> {
	const result = ensureWorkspaceInitialized(options);
	const live = await openWorkspaceInLiveUnity(result);
	return { ...result, ...live };
}

export { LIVE_BRIDGE_INIT_TIMEOUT_MS };
