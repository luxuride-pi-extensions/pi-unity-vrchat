/**
 * unity_save_view and unity_set_tool.
 *
 * save_view records a named view by *camera*: unlike Blender, Unity has no
 * viewport state to snapshot that survives a reopen, and a camera is what an
 * artist actually means by "that angle". So a saved view is a named camera, and
 * saving requires one to exist.
 */

import { resolve } from "node:path";

import { requestLiveUnitySetTool } from "./bridge.js";
import { getManifestPath, loadWorkspaceManifest, updateManifest } from "./workspace.js";
import { UNITY_TOOLS, type UnityTool } from "./types.js";
import { requestLiveUnitySceneInfo } from "./bridge.js";

export interface SaveViewOptions {
	cwd: string;
	workspace?: string;
	workspacePath?: string;
	name: string;
	cameraName: string;
}

export interface SaveViewResult {
	workspacePath: string;
	name: string;
	cameraName: string;
	savedViews: { name: string; cameraName: string }[];
}

export async function unitySaveView(options: SaveViewOptions): Promise<SaveViewResult> {
	const workspacePath = options.workspacePath ?? resolve(options.cwd, options.workspace ?? ".");
	loadWorkspaceManifest(workspacePath);
	const name = options.name.trim();
	if (!name) throw new Error("A saved view needs a name.");

	const savedViews = await updateManifest<{ name: string; cameraName: string }[]>(
		getManifestPath(workspacePath),
		(manifest) => {
			const existing = manifest.savedViews.findIndex((view) => view.name === name);
			const entry = { name, cameraName: options.cameraName };
			if (existing >= 0) manifest.savedViews[existing] = entry;
			else manifest.savedViews.push(entry);
			return manifest.savedViews;
		},
	);

	return { workspacePath, name, cameraName: options.cameraName, savedViews };
}

/**
 * Read-only scene pass used to validate that a camera exists before binding a
 * saved view to it. Without this the manifest would happily record a name that
 * no camera can ever match.
 */
export async function assertCameraExists(
	workspacePath: string,
	cameraName: string,
): Promise<void> {
	const info = await requestLiveUnitySceneInfo(["cameras"], "save-view:cameras");
	const cameras = info.cameras ?? [];
	if (!cameras.some((camera) => camera.name === cameraName || camera.gameObject === cameraName)) {
		throw new Error(
			`No camera named "${cameraName}" in the scene. Available cameras: ` +
				`${cameras.map((camera) => camera.name).join(", ") || "(none)"}. ` +
				`Add a camera GameObject first, or point the view at an existing camera.`,
		);
	}
}

export interface SetToolOptions {
	tool: string;
}

export interface SetToolResult {
	tool: string;
	activeTransformTool: string;
	selectedGameObjects: string[];
}

export async function unitySetTool(options: SetToolOptions): Promise<SetToolResult> {
	const tool = options.tool.trim().toLowerCase() as UnityTool;
	if (!UNITY_TOOLS.includes(tool)) {
		throw new Error(`Unknown tool: ${options.tool}. Valid tools: ${UNITY_TOOLS.join(", ")}`);
	}
	return requestLiveUnitySetTool(tool);
}
