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

import { mkdirSync, readFileSync } from "node:fs";
import { homedir } from "node:os";
import { dirname, isAbsolute, join, resolve } from "node:path";
import { fileURLToPath } from "node:url";

import { loadManagerCore } from "./manager-core.js";

/**
 * The shared manager core, or this extension's own copy of it.
 *
 * Resolved once, at module load, because consumers call it during module
 * evaluation: a lazy resolve would return a different core depending on when
 * the first prompt was read. See manager-core.ts for the resolution order.
 */
const core = loadManagerCore();

const EXTENSION_DIR = resolve(dirname(fileURLToPath(import.meta.url)));

export const APP_NAME = "pi-unity";

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

/**
 * The manager's own prompt, as a system-prompt section.
 *
 * /unity does not spawn a manager: the command session IS the manager, so the
 * user can watch its todos and progress in the session they are typing in. The
 * prompt therefore has to reach the session that ran the command, and
 * `before_agent_start` is the only route -- pi's RegisteredCommand has no way
 * to spawn an agent (types.d.ts: name, sourceInfo, description,
 * getArgumentCompletions, handler).
 *
 * Read from the file rather than inlined, so the manager prompt stays one
 * editable markdown source instead of a ~250-line string literal. The file is
 * deliberately NOT installed as an agent template, so nothing can spawn it and
 * the shadow copy that would drift cannot exist.
 *
 * Frontmatter is stripped: `tools:` and `extensions:` are a spawnable child's
 * contract, and a command session gets its tools from the extension it is
 * already running, so a stray `tools:` line in a system prompt is both inert
 * and misleading. A missing or unreadable file yields a string that says so,
 * because a manager that silently runs on a few lines of generic guidance is
 * worse than one that reports the prompt is gone. Both halves of that -- the
 * strip and the loud failure -- are the shared core's, and identical in the
 * other two manager extensions.
 */
export function getManagerPrompt(): string {
	return core.readPromptFile(join(getUnityAssetsDir(), "agents", "unity-manager.md"), "Unity manager");
}

/** Cross-process bridge queue directory, polled by the unity/plugin/ C# code. */
export function getUnityBridgeDir(): string {
	const envDir = process.env[ENV_UNITY_BRIDGE_DIR]?.trim();
	if (envDir) {
		return expandUserPath(envDir);
	}
	return join(xdgCacheHome(), "pi-unity", "bridge");
}

/**
 * Scratch dir for staging transfer payloads (FBX, textures, generated C#).
 * Kept out of the workspace so a failed import never leaves junk in Assets/.
 */
export function getUnityTempDir(): string {
	const dir = expandUserPath(process.env[ENV_UNITY_TMP_DIR]?.trim() || join(xdgCacheHome(), "pi-unity", "tmp"));
	mkdirSync(dir, { recursive: true });
	return dir;
}

export function getUnityHubPath(): string {
	return expandUserPath(process.env.UNITY_HUB_PATH?.trim() || join(homedir(), "Unity", "Hub"));
}

export function getUnityHubEditorRoot(): string {
	return join(getUnityHubPath(), "Editor");
}
