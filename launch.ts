/**
 * Locating and (optionally) launching the Unity Editor.
 *
 * The important difference from the Blender extension: pi cannot cold-launch
 * a usable Unity session on its own. Unity needs a project path, a real window
 * manager, and a licensing handshake, and on a first run the generated bridge
 * C# has to compile (a domain reload). So `ensureUnityLaunched` only opens a
 * project if the user is actually engaged, and it never claims success until
 * the bridge heartbeat file proves the plugin compiled and is polling.
 */

import { spawn } from "node:child_process";
import { existsSync, mkdirSync, readdirSync, statSync } from "node:fs";
import { homedir } from "node:os";
import { join } from "node:path";

import { execCommand } from "./core/exec.js";
import { getUnityBridgeDir, getUnityHubEditorRoot } from "./config.js";

export interface UnityArgv {
	command: string;
	args: string[];
}

/** 10s of silence from the bridge means the Editor is gone. */
export const LIVE_BRIDGE_HEARTBEAT_FILE = "heartbeat";
export const LIVE_BRIDGE_HEARTBEAT_STALE_MS = 10_000;
/** Spawned but never heartbeating: 180s covers first-run package resolve + compile. */
export const UNITY_LAUNCH_WATCHDOG_MS = 180_000;

const MAC_UNITY_PATH = "/Applications/Unity/Hub/Editor";

/**
 * Where the Hub's flatpak sandbox keeps its config, including the entitlement
 * license.
 */
const HUB_FLATPAK_CONFIG_HOME = join(homedir(), ".var", "app", "com.unity.UnityHub", "config");

/**
 * Licensing is the one thing pi cannot work around by retrying.
 *
 * Unity Hub is installed as a flatpak, so the entitlement license it obtained
 * lives inside its sandbox at
 * `~/.var/app/com.unity.UnityHub/config/unity3d/Unity/licenses/`. The Hub
 * launches the Editor as a child process, so the Editor inherits that sandbox's
 * `XDG_CONFIG_HOME` and finds the license. pi launches the same binary directly
 * with the host's `XDG_CONFIG_HOME`, so the Editor looks in `~/.config/unity3d`
 * instead, finds nothing, and starts up unlicensed:
 *
 *   [Licensing::Client] Error: Code 500 ... No ULF license found., Token not found in cache
 *   [Licensing::Module] Error: License is not active (com.unity.editor.ui).
 *
 * Pointing XDG_CONFIG_HOME at the sandbox reproduces exactly what the Hub hands
 * its own child, which is why the identical binary works from one launch path
 * and not the other. This is not a Linux Unity bug -- it is two different
 * config roots.
 *
 * Returns undefined when the caller already set XDG_CONFIG_HOME (they know
 * something we do not) or when no sandbox license directory exists.
 */
export function findLicensedConfigHome(): string | undefined {
	if (process.env.XDG_CONFIG_HOME?.trim()) return undefined;
	const licenseDir = join(HUB_FLATPAK_CONFIG_HOME, "unity3d", "Unity", "licenses");
	return existsSync(licenseDir) ? HUB_FLATPAK_CONFIG_HOME : undefined;
}

let cachedArgv: UnityArgv | undefined;
let unityLaunchStarted = false;
let unitySpawnInFlight = false;
let unityWatchdog: NodeJS.Timeout | undefined;

function warn(message: string): void {
	console.error(`[pi-vibe-unity] warning: ${message}`);
}

function fromEnv(): UnityArgv | undefined {
	const raw = process.env.UNITY_PATH?.trim();
	if (!raw) return undefined;
	const parts = raw.split(/\s+/).filter(Boolean);
	if (parts.length === 0) return undefined;
	// No shell: the value may legitimately be a wrapper argv such as
	// "flatpak run --command=Unity com.unity.Unity".
	return { command: parts[0]!, args: parts.slice(1) };
}

/** Newest version directory wins, so an updated Hub install is picked up. */
function fromHub(): UnityArgv | undefined {
	const root = getUnityHubEditorRoot();
	if (!existsSync(root)) return undefined;
	let versions: string[];
	try {
		versions = readdirSync(root);
	} catch {
		return undefined;
	}
	const sorted = versions
		.filter((name) => existsSync(join(root, name, "Editor", "Unity")))
		.sort()
		.reverse();
	const version = sorted[0];
	if (!version) return undefined;
	return { command: join(root, version, "Editor", "Unity"), args: [] };
}

function fromMacHub(): UnityArgv | undefined {
	if (process.platform !== "darwin") return undefined;
	if (!existsSync(MAC_UNITY_PATH)) return undefined;
	try {
		const versions = readdirSync(MAC_UNITY_PATH).sort().reverse();
		for (const version of versions) {
			const candidate = join(MAC_UNITY_PATH, version, "Unity.app", "Contents", "MacOS", "Unity");
			if (existsSync(candidate)) return { command: candidate, args: [] };
		}
	} catch {
		/* fall through to PATH lookup */
	}
	return undefined;
}

async function fromPathLookup(): Promise<UnityArgv | undefined> {
	const result = await execCommand("unity", ["-version"], process.cwd(), { timeout: 10_000 });
	if (result.code === 0) return { command: "unity", args: [] };
	return undefined;
}

export async function resolveUnityArgv(): Promise<UnityArgv> {
	if (cachedArgv) return cachedArgv;
	const candidates: (UnityArgv | undefined)[] = [fromEnv(), fromMacHub(), fromHub()];
	for (const candidate of candidates) {
		if (candidate) {
			cachedArgv = candidate;
			return candidate;
		}
	}
	const onPath = await fromPathLookup();
	if (onPath) {
		cachedArgv = onPath;
		return onPath;
	}
	throw new Error(
		"Could not find a Unity Editor installation. Set UNITY_PATH to the editor binary " +
			`(for example ${getUnityHubEditorRoot()}/2022.3.22f1/Editor/Unity), or install an editor ` +
			"through Unity Hub. UNITY_PATH is whitespace-split, so quoting is not needed.",
	);
}

export function isLiveBridgeAlive(): boolean {
	try {
		const stat = statSync(join(getUnityBridgeDir(), LIVE_BRIDGE_HEARTBEAT_FILE));
		return Date.now() - stat.mtimeMs < LIVE_BRIDGE_HEARTBEAT_STALE_MS;
	} catch {
		return false;
	}
}

export function resetUnityLaunch(): void {
	unityLaunchStarted = false;
}

function clearUnityWatchdog(): void {
	if (unityWatchdog) {
		clearTimeout(unityWatchdog);
		unityWatchdog = undefined;
	}
}

function armUnityWatchdog(): void {
	clearUnityWatchdog();
	unityWatchdog = setTimeout(() => {
		unityWatchdog = undefined;
		if (isLiveBridgeAlive()) return;
		unityLaunchStarted = false;
		warn(
			"the Unity Editor did not start a bridge heartbeat. Check that the project compiled without errors " +
				"and that the editor finished its licensing handshake. If the log shows " +
				"'License is not active (com.unity.editor.ui)' and you installed Unity Hub as a flatpak, " +
				"launch the project from the Hub once so the entitlement license is refreshed.",
		);
	}, UNITY_LAUNCH_WATCHDOG_MS);
	unityWatchdog.unref?.();
}

export async function ensureUnityLaunchedForce(projectPath?: string): Promise<void> {
	if (process.env.VIBE_UNITY_NO_UNITY?.trim() === "1") return;
	if (isLiveBridgeAlive()) return;
	if (unityLaunchStarted && !unitySpawnInFlight) return;

	unityLaunchStarted = true;
	unitySpawnInFlight = true;
	try {
		const argv = await resolveUnityArgv();
		const bridgeDir = getUnityBridgeDir();
		mkdirSync(bridgeDir, { recursive: true });
		mkdirSync(join(bridgeDir, "requests"), { recursive: true });
		mkdirSync(join(bridgeDir, "responses"), { recursive: true });

		const args = [...argv.args];
		if (projectPath) args.push("-projectPath", projectPath);
		// Keep the editor out of batch mode: the whole point of the live
		// bridge is a visible, interactive session the user can also drive.
		args.push("-logFile", join(bridgeDir, "Editor.log"));

		// -logFile above is what keeps this side effect contained: Unity's own
		// Editor.log would otherwise follow XDG_CONFIG_HOME into the sandbox.
		const configHome = findLicensedConfigHome();
		const child = spawn(argv.command, args, {
			detached: true,
			stdio: "ignore",
			env: {
				...process.env,
				PI_UNITY_BRIDGE_DIR: bridgeDir,
				...(configHome ? { XDG_CONFIG_HOME: configHome } : {}),
			},
		});
		// With stdio ignored a failed exec is otherwise silent.
		child.on("error", (error) => {
			unityLaunchStarted = false;
			warn(`failed to launch Unity: ${error.message}`);
		});
		child.unref();
		armUnityWatchdog();
	} finally {
		unitySpawnInFlight = false;
	}
}

/** Lazy variant used inside tool paths; never throws on launch failure. */
export async function ensureUnityLaunched(projectPath?: string): Promise<void> {
	if (isLiveBridgeAlive()) return;
	try {
		await ensureUnityLaunchedForce(projectPath);
	} catch (error) {
		unityLaunchStarted = false;
		warn(error instanceof Error ? error.message : String(error));
	}
}
