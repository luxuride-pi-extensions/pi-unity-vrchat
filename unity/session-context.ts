/**
 * unity_session_context.
 *
 * The read-only "what is true right now" call, deliberately cheap: it answers
 * from a single bridge round-trip with no scene writes. Every skill that acts
 * on "this", "the selected object", or "the current view" resolves it here
 * first rather than assuming.
 *
 * The Open-file is not in play for Unity the way it is for Blender: there is no
 * per-workspace live session to guess at, so this reports what the single live
 * Editor has open, and whether that project matches the requested workspace.
 */

import { resolve } from "node:path";

import { requestLiveUnitySessionContext } from "./bridge.js";
import { fileExists, getManifestPath, loadWorkspaceManifest } from "./workspace.js";
import type { UnitySessionContext } from "./types.js";

export interface SessionContextOptions {
	cwd: string;
	workspace?: string;
	workspacePath?: string;
}

export interface SessionContextResult {
	workspacePath: string;
	/** What the live Editor has open. Falls back to the manifest, then to "". */
	projectPath: string;
	/** False when no unity-workspace.json was found. NOT an error by itself. */
	workspaceExists: boolean;
	/**
	 * True when the open project IS the workspace project. False when the two
	 * genuinely differ, and also when either side is unknown.
	 */
	matchesOpenProject: boolean;
	context: UnitySessionContext | null;
	note: string;
}

/**
 * Ask the bridge FIRST, and treat the manifest as optional.
 *
 * The live Editor is a single global session, not a per-workspace one: it can
 * answer authoritatively whether or not any unity-workspace.json exists. The
 * previous order checked the manifest first and threw when it was missing, so
 * asking from a directory that is not a pi workspace reported a "No pi Unity
 * workspace at <cwd>" non-answer on every turn -- even with a fully reachable
 * Editor and a correct answer available. That was a wrong signal dressed as a
 * helpful one, and it was telling the agent to call unity_session_context
 * (which then repeated the same non-answer).
 */
export async function unitySessionContext(options: SessionContextOptions): Promise<SessionContextResult> {
	const workspacePath = options.workspacePath ?? resolve(options.cwd, options.workspace ?? ".");
	const manifestPath = getManifestPath(workspacePath);
	// Load the manifest only if it exists. Null is a normal state here, not a
	// failure: the live Editor answers without it, and the note below explains
	// the consequence (live state describes an unmanaged project).
	const manifest = fileExists(manifestPath) ? loadWorkspaceManifest(workspacePath) : null;
	const manifestProject = manifest ? resolve(manifest.projectPath) : "";

	try {
		const context = await requestLiveUnitySessionContext();
		const openProject = resolve(context.projectPath);
		const matches = manifest !== null && openProject === manifestProject;

		let note = "";
		if (manifest === null) {
			// The state above is AUTHORITATIVE for the project the Editor has
			// open -- it is read from that live session, not guessed. What is
			// missing is a manifest, which is a narrower fact than "this state is
			// untrustworthy", and an earlier wording blurred the two by saying the
			// state "describes whatever project the Editor has open" as if that
			// were a caveat about the state. It is not: it is the state.
			//
			// The real consequence of having no manifest is that the
			// WORKSPACE-SCOPED tools (unity_module_run, unity_module_revert,
			// unity_scene_info's iteration folder, saved views) have nowhere to
			// record their results. Say exactly that.
			note =
				`This project is NOT managed by pi (no unity-workspace.json at ${workspacePath}). The live state ` +
				`above is still authoritative for it, and unity_execute_csharp / unity_inspect / unity_bones / ` +
				`unity_render all work normally. What is unavailable without a manifest: unity_module_run and ` +
				`unity_module_revert (they checkpoint into the workspace), and iteration folders, renders and saved ` +
				`views. Run unity_workspace_init to adopt it if you need those.`;
		} else if (!matches) {
			note =
				`The live Unity Editor has ${context.projectPath} open, but this workspace is ${manifest.projectPath}. ` +
				`Open the workspace project in Unity, or run unity_workspace_init again to switch to it.`;
		}

		return {
			workspacePath,
			projectPath: manifestProject || openProject,
			workspaceExists: manifest !== null,
			matchesOpenProject: matches,
			context,
			note,
		};
	} catch (error) {
		// A missing Editor is a normal state, not a failure: the tool still
		// reports the workspace it knows about so the caller can decide.
		return {
			workspacePath,
			projectPath: manifestProject,
			workspaceExists: manifest !== null,
			matchesOpenProject: false,
			context: null,
			note: manifestProject
				? `The live Unity Editor is not reachable, so live state is unknown. ` +
					`Open ${manifestProject} in Unity (the pi bridge plugin compiles on first open). ` +
					`Bridge said: ${error instanceof Error ? error.message : String(error)}`
				: `The live Unity Editor is not reachable, so live state is UNKNOWN -- do not assume any ` +
					`selection, active object, open scene or camera. There is also no pi Unity workspace at ` +
					`${workspacePath} to open instead. ` +
					`Bridge said: ${error instanceof Error ? error.message : String(error)}`,
		};
	}
}
