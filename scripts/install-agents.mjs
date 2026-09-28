#!/usr/bin/env node
/**
 * Install the agent templates into pi's agents directory, stamping the
 * `extensions:` path for THIS machine.
 *
 * Why a script rather than a committed path: an agent template's `extensions:`
 * field takes an absolute path, and pi-subagents does not expand `~` or read
 * env vars for it (it only resolves `.`/`..` relative segments). So the tracked
 * file cannot be correct for two machines at once, and editing the tracked copy
 * to fix it puts a host path in version control.
 *
 * The tracked templates therefore keep a placeholder, and this resolves it.
 *
 *   node scripts/install-agents.mjs            # print resolved paths
 *   node scripts/install-agents.mjs --write    # actually install
 *
 * Overrides:
 *   PI_HOME         default ~/.pi
 *   PI_AGENT_DIR    default $PI_HOME/agent
 */

import { existsSync, mkdirSync, readFileSync, writeFileSync } from "node:fs";
import { homedir } from "node:os";
import { dirname, join, resolve } from "node:path";
import { fileURLToPath } from "node:url";

const HERE = dirname(fileURLToPath(import.meta.url));
const EXT_ROOT = resolve(HERE, "..");
const TEMPLATES = join(EXT_ROOT, "unity", "agents");

const PI_HOME = process.env.PI_HOME ?? join(homedir(), ".pi");
const PI_AGENT_DIR = process.env.PI_AGENT_DIR ?? join(PI_HOME, "agent");

/**
 * The extension directory pi LOADS, which is not necessarily the checkout. The
 * installed location is under $PI_AGENT_DIR/extensions; if this repo is not
 * installed there, say so plainly, because an agent template pointing at an
 * uninstalled directory loads with no tools and fails confusingly.
 */
function resolveInstalledExtensionDir(name) {
	const installed = join(PI_AGENT_DIR, "extensions", name);
	if (existsSync(installed)) return installed;
	if (resolve(installed) === EXT_ROOT) return EXT_ROOT;
	return installed;
}

const VIBE_UNITY = resolveInstalledExtensionDir("vibe-unity");

// The Unity-Blender manager is NOT listed here. It lives in the binder repo
// (pi-unity-blender/agents/) because it is the root of the pair, not a Unity
// subagent, and it needs BOTH extensions on its `extensions:` line. The binder's
// own scripts/install-agents.mjs installs it, and having two installers race
// over one destination file is a worse failure than a file nobody owns.
const targets = [
	{ file: "unity-manager.md", extensionDir: VIBE_UNITY },
	{ file: "unity-worker.md", extensionDir: VIBE_UNITY },
	{ file: "unity-critic.md", extensionDir: VIBE_UNITY },
	{ file: "unity-diagnostician.md", extensionDir: VIBE_UNITY },
	{ file: "unity-finalizer.md", extensionDir: VIBE_UNITY },
	{ file: "vrchat-unity-avatar.md", extensionDir: VIBE_UNITY },
	{ file: "vrchat-unity-world.md", extensionDir: VIBE_UNITY },
];

const write = process.argv.includes("--write");
const agentDir = join(PI_AGENT_DIR, "agents");
let problems = 0;

for (const { file, extensionDir } of targets) {
	const source = join(TEMPLATES, file);
	if (!existsSync(source)) {
		console.error(`missing template: ${source}`);
		problems += 1;
		continue;
	}

	if (!existsSync(extensionDir)) {
		console.error(
			`WARNING ${file}: ${extensionDir} does not exist, so the installed agent would load with no ` +
				`tools. Install the extension first (symlink it into ${PI_AGENT_DIR}/extensions).`,
		);
		problems += 1;
	}

	// Replace the placeholder rather than a literal path, so a template
	// installed on this machine never claims to be installed on another.
	const stamped = readFileSync(source, "utf8").replace(
		/^extensions:.*$/m,
		`extensions: ${extensionDir}`,
	);

	const destination = join(agentDir, file);
	if (write) {
		mkdirSync(agentDir, { recursive: true });
		writeFileSync(destination, stamped);
		console.log(`wrote ${destination}  (extensions: ${extensionDir})`);
	} else {
		console.log(`${file} -> ${destination}\n  extensions: ${extensionDir}`);
	}
}

if (!write) {
	console.log("\ndry run. re-run with --write to install.");
}
if (problems > 0) {
	console.error(`\n${problems} problem(s) above.`);
	process.exitCode = 1;
}
