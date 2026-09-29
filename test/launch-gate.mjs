/**
 * The no-project launch gate.
 *
 * Why this needs a test: the failure it prevents looks like nothing at all. An
 * Editor launched with no `-projectPath` opens empty, the bridge plugin is not
 * there, so it never heartbeats, so the request times out — and because the
 * context injection asks for a session context on every turn of an engaged
 * session, each turn relaunches the Editor. The user sees a stream of Unity
 * windows and timeout errors and no cause.
 *
 * So the gate is the whole behaviour, and it has two halves that must be tested
 * together: nothing spawns without a project, and something DOES spawn with
 * one. Testing only the negative half would pass against a gate that never
 * launches anything, which is a different bug with the same shape.
 *
 * The editor is faked with a script that records that it ran, so the test never
 * needs a real Unity install and a regression cannot spawn one by accident.
 */

import assert from "node:assert/strict";
import { existsSync, mkdtempSync, readFileSync, rmSync, writeFileSync } from "node:fs";
import { tmpdir } from "node:os";
import { join } from "node:path";

import { JITI } from "./paths.mjs";

const tmp = mkdtempSync(join(tmpdir(), "pi-unity-launch-gate-"));
const bridgeDir = join(tmp, "bridge");
const launchMarker = join(tmp, "editor-launched.txt");

// A fake "editor": records the argv it was given, so the test can assert the
// project actually reached it. Nothing here needs Unity to be installed.
const fakeEditor = join(tmp, "fake-editor.sh");
writeFileSync(fakeEditor, `#!/bin/sh\nprintf '%s\\n' "$@" > ${JSON.stringify(launchMarker)}\n`, { mode: 0o755 });

process.env.PI_UNITY_BRIDGE_DIR = bridgeDir;
process.env.UNITY_PATH = fakeEditor;
delete process.env.PI_UNITY_NO_UI;

const { createJiti } = await import(JITI);
const jiti = createJiti(import.meta.url, { isolated: true, interopDefault: true });
const launch = await jiti.import(join(import.meta.dirname, "..", "launch.ts"));

let passed = 0;
const failures = [];
const check = (name, fn) => {
	try {
		fn();
		passed += 1;
	} catch (error) {
		failures.push(`${name}: ${error instanceof Error ? error.message : String(error)}`);
	}
};

const waitFor = async (predicate, ms = 5_000) => {
	const deadline = Date.now() + ms;
	while (Date.now() < deadline) {
		if (predicate()) return true;
		await new Promise((r) => setTimeout(r, 25));
	}
	return predicate();
};

// A stale heartbeat would make every "no project" case look like a live Editor.
assert.equal(existsSync(join(bridgeDir, "heartbeat")), false, "the fixture must start with no live Editor");

// --- no project: nothing spawns, and the caller is told why -----------------

launch.resetUnityLaunch();
await launch.ensureUnityLaunchedForce();
check("ensureUnityLaunchedForce with no project spawns nothing", () => {
	assert.equal(existsSync(launchMarker), false, "an Editor was launched with no project to open");
});

let thrown = null;
try {
	await launch.ensureUnityLaunched();
} catch (error) {
	thrown = error;
}
check("ensureUnityLaunched with no project throws instead of timing out", () => {
	assert.ok(thrown, "expected a thrown error, not a silent skip");
	assert.match(thrown.message, /did not launch/);
	// The message has to be actionable, or the agent retries the same thing.
	assert.match(thrown.message, /unity_workspace_init/);
	assert.equal(existsSync(launchMarker), false, "an Editor was launched with no project to open");
});

// The opt-out still short-circuits before anything else, including a project.
process.env.PI_UNITY_NO_UI = "1";
launch.resetUnityLaunch();
await launch.ensureUnityLaunchedForce("/some/project");
check("PI_UNITY_NO_UI=1 suppresses the launch even with a project", () => {
	assert.equal(existsSync(launchMarker), false, "the documented opt-out did not hold");
});
delete process.env.PI_UNITY_NO_UI;

// --- a project: it does launch, and the project reaches the editor -----------

launch.resetUnityLaunch();
await launch.ensureUnityLaunchedForce("/some/project");
const launched = await waitFor(() => existsSync(launchMarker));
check("the launched editor received the project path", () => {
	assert.ok(launched, "no editor was launched even though a project was known");
	const argv = readFileSync(launchMarker, "utf8");
	assert.match(argv, /-projectPath\s+\/some\/project/);
});

rmSync(tmp, { recursive: true, force: true });

console.log(`${passed} passed, ${failures.length} failed`);
if (failures.length) {
	for (const f of failures) console.error(`FAIL ${f}`);
	process.exit(1);
}
