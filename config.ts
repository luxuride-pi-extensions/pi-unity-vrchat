/**
 * Path resolution for the pi Unity extension.
 *
 * Unlike Blender there is no single `UNITY_PATH` equivalent that every
 * install provides, so resolution is layered:
 *   1. $UNITY_PATH        — explicit editor binary (whitespace-split argv, so
 *                           wrapper commands work too)
 *   2. Unity Hub layout   — $UNITY_HUB_PATH / ~/Unity/Hub/Editor/<version>/
 *                           Editor/Unity, newest version wins
 *   3. `unity` on PATH    — covers a manually symlinked or flatpak-wrapped one
 *
 * Cache dirs follow the XDG convention the Blender extension uses, so a
 * sandboxed or immutable $HOME does not break bridge IO.
 */

import { mkdirSync } from "node:fs";
import { homedir } from "node:os";
import { dirname, isAbsolute, join, resolve } from "node:path";
import { fileURLToPath } from "node:url";

const EXTENSION_DIR = resolve(dirname(fileURLToPath(import.meta.url)));

export const APP_NAME = "vibe-unity";

export const ENV_UNITY_BRIDGE_DIR = "PI_UNITY_BRIDGE_DIR";
export const ENV_UNITY_TMP_DIR = "PI_UNITY_TMP_DIR";

function xdgCacheHome(): string {
	return process.env.XDG_CACHE_HOME?.trim() || join(homedir(), ".cache");
}

/** Expand a leading `~` and resolve relative paths against the cwd. */
function expandUserPath(raw: string, cwd: string = process.cwd()): string {
	const value = raw.trim();
	if (value === "~") return homedir();
	if (value.startsWith("~/")) return homedir() + value.slice(1);
	return isAbsolute(value) ? value : resolve(cwd, value);
}

export function getUnityAssetsDir(): string {
	return join(EXTENSION_DIR, "unity");
}

/**
 * Bundled C# editor plugin, as a *directory* of partial-class files. pi copies
 * the whole directory into a project's Assets/Editor/PiUnityBridge/ so it
 * compiles into the predefined Assembly-CSharp-Editor with no asmdef and no
 * package dependencies, which is what lets it drop into any Unity 2021+
 * project (a configured VRChat world, an existing game) that pi was pointed at
 * rather than generated.
 */
export function getBundledUnityBridgeDir(): string {
	return resolve(join(getUnityAssetsDir(), "plugin"));
}

/** Directory the generated-project template is copied from. */
export function getUnityProjectTemplateDir(): string {
	return resolve(join(getUnityAssetsDir(), "project-template"));
}

export function getBundledUnitySkillsDir(): string {
	return join(getUnityAssetsDir(), "skills");
}

export function getBundledUnityAgentsDir(): string {
	return join(getUnityAssetsDir(), "agents");
}

/** Cross-process bridge queue directory, polled by the unity/plugin/ C# code. */
export function getUnityBridgeDir(): string {
	const envDir = process.env[ENV_UNITY_BRIDGE_DIR]?.trim();
	if (envDir) {
		return expandUserPath(envDir);
	}
	return join(xdgCacheHome(), "vibe-unity", "bridge");
}

/**
 * Scratch dir for staging transfer payloads (FBX, textures, generated C#).
 * Kept out of the workspace so a failed import never leaves junk in Assets/.
 */
export function getUnityTempDir(): string {
	const dir = expandUserPath(process.env[ENV_UNITY_TMP_DIR]?.trim() || join(xdgCacheHome(), "vibe-unity", "tmp"));
	mkdirSync(dir, { recursive: true });
	return dir;
}

export function getUnityHubPath(): string {
	return expandUserPath(process.env.UNITY_HUB_PATH?.trim() || join(homedir(), "Unity", "Hub"));
}

export function getUnityHubEditorRoot(): string {
	return join(getUnityHubPath(), "Editor");
}
