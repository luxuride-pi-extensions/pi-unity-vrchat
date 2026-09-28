/**
 * unity_render and unity_capture_view.
 *
 * Two different pictures, and the distinction matters when critiquing work:
 *   - render: through a camera, with lighting and materials (what the game
 *     actually looks like). Needs a camera; falls back to the MainCamera tag.
 *   - capture-view: straight off the Scene view, i.e. what the artist is
 *     looking at right now. No camera required.
 */

import { mkdirSync } from "node:fs";
import { join, resolve } from "node:path";

import { requestLiveUnityCaptureView, requestLiveUnityRender } from "./bridge.js";
import { getIterationPaths, loadWorkspaceManifest, updateManifest, getManifestPath } from "./workspace.js";
import { normalizeRenderOutputName } from "./utils.js";

export interface RenderOptions {
	cwd: string;
	workspace?: string;
	workspacePath?: string;
	outputName?: string;
	width?: number;
	height?: number;
	cameraName?: string;
	timeoutSeconds?: number;
	signal?: AbortSignal;
}

export interface RenderResult {
	workspacePath: string;
	iteration: number;
	outputPath: string;
	cameraName: string;
	usedActiveCamera: boolean;
	width: number;
	height: number;
}

export async function unityRender(options: RenderOptions): Promise<RenderResult> {
	const workspacePath = options.workspacePath ?? resolve(options.cwd, options.workspace ?? ".");
	// The manifest is read for validation and project path only; the iteration
	// number itself comes from the locked read-modify-write below.
	loadWorkspaceManifest(workspacePath);

	// Claim the iteration under the manifest lock so two renders cannot land in
	// the same folder, and publish it before the slow work starts.
	const iteration = await updateManifest<number>(getManifestPath(workspacePath), (state) => {
		const next = state.latestIteration + 1;
		state.latestIteration = next;
		return next;
	});

	const { rendersDir } = getIterationPaths(workspacePath, iteration);
	mkdirSync(rendersDir, { recursive: true });
	const outputName = normalizeRenderOutputName(options.outputName, ".png");
	const outputPath = join(rendersDir, outputName);

	const result = await requestLiveUnityRender({
		outputPath,
		width: options.width ?? 960,
		height: options.height ?? 540,
		cameraName: options.cameraName,
		timeoutMs: (options.timeoutSeconds ?? 180) * 1000,
	});

	await updateManifest(getManifestPath(workspacePath), (state) => {
		state.renderOutputs.push(outputPath);
	});

	return {
		workspacePath,
		iteration,
		outputPath,
		cameraName: result.cameraName,
		usedActiveCamera: result.usedActiveCamera,
		width: result.width,
		height: result.height,
	};
}

export interface CaptureViewOptions {
	cwd: string;
	workspace?: string;
	workspacePath?: string;
	outputName?: string;
	width?: number;
	height?: number;
	timeoutSeconds?: number;
	signal?: AbortSignal;
}

export interface CaptureViewResult {
	workspacePath: string;
	iteration: number;
	outputPath: string;
	perspective: string;
	sceneViewCamera: string;
}

export async function unityCaptureView(options: CaptureViewOptions): Promise<CaptureViewResult> {
	const workspacePath = options.workspacePath ?? resolve(options.cwd, options.workspace ?? ".");
	loadWorkspaceManifest(workspacePath);

	const iteration = await updateManifest<number>(getManifestPath(workspacePath), (state) => {
		const next = state.latestIteration + 1;
		state.latestIteration = next;
		return next;
	});

	const { rendersDir } = getIterationPaths(workspacePath, iteration);
	mkdirSync(rendersDir, { recursive: true });
	const outputPath = join(rendersDir, normalizeRenderOutputName(options.outputName, ".png"));

	const result = await requestLiveUnityCaptureView({
		outputPath,
		width: options.width ?? 960,
		height: options.height ?? 540,
		timeoutMs: (options.timeoutSeconds ?? 180) * 1000,
	});

	await updateManifest(getManifestPath(workspacePath), (state) => {
		state.renderOutputs.push(outputPath);
	});

	return {
		workspacePath,
		iteration,
		outputPath,
		perspective: result.perspective,
		sceneViewCamera: result.sceneViewCamera,
	};
}
