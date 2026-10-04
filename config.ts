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
import { dirname, join, resolve, sep } from "node:path";
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

/**
 * Path resolution comes from the shared core rather than a local copy.
 *
 * These two were defined here AND exported by the core this file already loads,
 * so the question of which definition a caller used was decided by import order
 * rather than by intent. The core's is the one pi-manager-core tests, and a
 * fourth copy of a five-line function is exactly the kind of drift the core
 * exists to stop.
 */
const xdgCacheHome = core.xdgCacheHome;
const expandUserPath = (raw: string, cwd: string = process.cwd()): string => core.expandUserPath(raw, cwd);

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
 *
 * A PI_UNITY_TMP_DIR pointing into a Unity project's Assets/ is REFUSED, and
 * the refusal is loud. The reason is not tidiness: every staged file here is a
 * real asset the moment it lands under Assets/ -- Unity imports it, and an
 * inlineCode snippet compiles a SECOND time into the user's Assembly-CSharp on
 * the next refresh, silently. The default (~/.cache/pi-unity/tmp) cannot do
 * this, so the check is exactly about the override: without it the one
 * configuration that causes the damage is the one that is accepted.
 *
 * Detection is deliberately structural rather than "is this a known project":
 * a path is refused when it is, or is under, any directory segment named
 * `Assets`. That catches the project itself, a subfolder of it, and the
 * symlink-resolved form, without needing a list of project roots that would be
 * incomplete by construction.
 */
export function getUnityTempDir(): string {
	const configured = process.env[ENV_UNITY_TMP_DIR]?.trim();
	const dir = expandUserPath(configured || join(xdgCacheHome(), "pi-unity", "tmp"));
	if (configured) assertNotInsideAssets(dir, ENV_UNITY_TMP_DIR);
	mkdirSync(dir, { recursive: true });
	return dir;
}

/**
 * Refuses a scratch path that is inside a Unity project's Assets/.
 *
 * The check is on RESOLVED segments, so `Assets/../Assets/x` and
 * `AssetsEvil/x` are told apart correctly: only a whole segment named `Assets`
 * counts, which is what makes a sibling directory with the same prefix safe to
 * use and the folder itself not.
 */
export function assertNotInsideAssets(dir: string, source: string): void {
	const segments = resolve(dir).split(sep).filter((segment) => segment.length > 0);
	const at = segments.indexOf("Assets");
	if (at < 0) return;
	throw new Error(
		`${source} is set to ${dir}, which is inside a Unity project's Assets/ folder (at "${segments.slice(0, at + 1).join(sep)}"). ` +
			"Every file staged there is imported by Unity as a project asset, and a staged C# snippet compiles a second " +
			"time into your Assembly-CSharp on the next refresh. Point it at a scratch or cache directory outside " +
			"any project (unset it to use the default cache dir).",
	);
}

export function getUnityHubPath(): string {
	return expandUserPath(process.env.UNITY_HUB_PATH?.trim() || join(homedir(), "Unity", "Hub"));
}

export function getUnityHubEditorRoot(): string {
	return join(getUnityHubPath(), "Editor");
}
