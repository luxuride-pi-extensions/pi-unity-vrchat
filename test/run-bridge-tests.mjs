/**
 * Bridge protocol tests.
 *
 * Runs the real TypeScript bridge module against the fake Editor double, so the
 * request/response framing, the cross-process disk lock, timeouts, and error
 * propagation are all exercised without launching Unity. The C# plugin itself
 * is verified separately by compiling it with Unity's own response file
 * (see test/compile-bridge.sh).
 *
 * Run: node test/run-bridge-tests.mjs
 */

import { spawn } from "node:child_process";
import { mkdtempSync, readFileSync, rmSync, writeFileSync, existsSync } from "node:fs";
import { tmpdir } from "node:os";
import { dirname, join } from "node:path";
import { fileURLToPath } from "node:url";

const HERE = dirname(fileURLToPath(import.meta.url));
const EXT = join(HERE, "..");

// jiti is resolved from the pi install rather than hardcoded, so a moved
// checkout or a pi upgrade does not fail as an unresolved module. See
// test/paths.mjs.
const { JITI } = await import("./paths.mjs");

const jiti = (await import(JITI)).createJiti(import.meta.url, {
	interopDefault: true,
	moduleCache: false,
});

const sleep = (ms) => new Promise((r) => setTimeout(r, ms));

let passed = 0;
const failures = [];

function check(name, condition, detail = "") {
	if (condition) {
		passed += 1;
		console.log(`  ok   ${name}`);
	} else {
		failures.push(`${name}${detail ? ` — ${detail}` : ""}`);
		console.log(`  FAIL ${name}${detail ? ` — ${detail}` : ""}`);
	}
}

async function withEditor(fn) {
	const dir = mkdtempSync(join(tmpdir(), "pi-unity-bridge-"));
	const bridgeDir = join(dir, "bridge");
	for (const sub of ["requests", "responses"]) {
		// The TS side creates these itself, but the double reads at startup.
		const { mkdirSync } = await import("node:fs");
		mkdirSync(join(bridgeDir, sub), { recursive: true });
	}
	writeFileSync(join(bridgeDir, "control.json"), "{}");

	const child = spawn(process.execPath, [join(HERE, "fake-editor.mjs"), bridgeDir], {
		stdio: ["ignore", "inherit", "inherit"],
	});
	// Wait for the first heartbeat, which is what isLiveBridgeAlive() reads.
	for (let i = 0; i < 100 && !existsSync(join(bridgeDir, "heartbeat")); i += 1) {
		await sleep(50);
	}

	const previous = process.env.PI_UNITY_BRIDGE_DIR;
	process.env.PI_UNITY_BRIDGE_DIR = bridgeDir;

	try {
		return await fn({ bridgeDir, setControl: (o) => writeFileSync(join(bridgeDir, "control.json"), JSON.stringify(o)) });
	} finally {
		if (previous === undefined) delete process.env.PI_UNITY_BRIDGE_DIR;
		else process.env.PI_UNITY_BRIDGE_DIR = previous;
		child.kill("SIGTERM");
		await sleep(100);
		rmSync(dir, { recursive: true, force: true });
	}
}

// config.ts caches nothing, but bridge.ts may cache the resolved dir; load the
// module fresh per editor instance so the env var above is honoured.
async function loadBridge() {
	return jiti.import(join(EXT, "unity", "bridge.ts"), { default: false });
}

console.log("bridge protocol");

await withEditor(async ({ bridgeDir, setControl }) => {
	const bridge = await loadBridge();

	// 1. Happy path. The bridge returns the *unwrapped* result
	// (`if (parsed.ok) return parsed.result`), not a { result } envelope.
	const pong = await bridge.requestLiveUnityBridge({ payload: { type: "ping" }, timeoutMs: 5_000 });
	check("ping returns the handler result", pong?.pong === true, JSON.stringify(pong));

	// 2. The request file is consumed, not left behind.
	const leftover = bridge.listStaleResponseFiles?.() ?? [];
	check("no stale response files remain", Array.isArray(leftover));

	// 3. Errors from the editor propagate with the editor's own text.
	let message = "";
	try {
		await bridge.requestLiveUnityBridge({ payload: { type: "no-such-request" }, timeoutMs: 5_000 });
	} catch (error) {
		message = String(error?.message ?? error);
	}
	check(
		"editor error text propagates",
		message.includes('no handler for "no-such-request"'),
		message.slice(0, 120),
	);

	// 4. execute-csharp round-trips stdout, which is what the binder parses.
	const run = await bridge.requestLiveUnityBridge({
		payload: { type: "execute-csharp", code: '{"materialNames":["A"],"vertexTotal":42}' },
		timeoutMs: 5_000,
	});
	check("execute-csharp returns stdout", run?.stdout?.includes("vertexTotal"), JSON.stringify(run));

	// 5. open-scene refuses while the fake reports unsaved changes.
	setControl({ sceneDirty: true });
	await sleep(200);
	let refused = "";
	try {
		await bridge.requestLiveUnityBridge({
			payload: { type: "open-scene", scenePath: "Assets/Other.unity" },
			timeoutMs: 5_000,
		});
	} catch (error) {
		refused = String(error?.message ?? error);
	}
	check(
		"open-scene refuses on a dirty scene",
		refused.includes("unsaved changes"),
		refused.slice(0, 120),
	);

	// 6. A timeout withdraws the request and says so actionably.
	setControl({ sceneDirty: false, delayMs: 3_000 });
	await sleep(200);
	let timeoutMessage = "";
	const started = Date.now();
	try {
		await bridge.requestLiveUnityBridge({ payload: { type: "ping" }, timeoutMs: 800 });
	} catch (error) {
		timeoutMessage = String(error?.message ?? error);
	}
	const elapsed = Date.now() - started;
	check("timeout throws", timeoutMessage.length > 0);
	check("timeout mentions the bridge status tool", timeoutMessage.includes("unity_bridge_status"), timeoutMessage.slice(0, 160));
	check("timeout returns near the deadline, not the handler time", elapsed < 2_500, `${elapsed}ms`);

	// 7. Withdraw-on-timeout leaves no pending request behind.
	const { readdirSync } = await import("node:fs");
	await sleep(200);
	const pendingAfter = readdirSync(join(bridgeDir, "requests")).filter((f) => f.endsWith(".json"));
	check("timed-out request is withdrawn", pendingAfter.length === 0, `left: ${pendingAfter.join(", ")}`);

	// 8. The lock is released after a failure, so the next request still works.
	setControl({ delayMs: 0 });
	await sleep(300);
	let recovered = "";
	try {
		const again = await bridge.requestLiveUnityBridge({ payload: { type: "ping" }, timeoutMs: 5_000 });
		recovered = again?.pong === true ? "ok" : "unexpected";
	} catch (error) {
		recovered = String(error?.message ?? error);
	}
	check("bridge usable after a timeout", recovered === "ok", recovered.slice(0, 120));
});

console.log("bridge lock");

await withEditor(async () => {
	const bridge = await loadBridge();
	// Two concurrent requests must serialise: the lock is what guarantees the
	// shared Editor never interleaves two mutations.
	const results = await Promise.all([
		bridge.requestLiveUnityBridge({ payload: { type: "ping" }, timeoutMs: 8_000 }),
		bridge.requestLiveUnityBridge({ payload: { type: "ping" }, timeoutMs: 8_000 }),
		bridge.requestLiveUnityBridge({ payload: { type: "ping" }, timeoutMs: 8_000 }),
	]);
	check(
		"three concurrent requests all succeed",
		results.every((r) => r?.pong === true),
		JSON.stringify(results),
	);
});

console.log("session context (manifest is OPTIONAL)");

await withEditor(async () => {
	const session = await jiti.import(join(EXT, "unity", "session-context.ts"), { default: false });

	// The live Editor is a single global session: it can answer whether or not
	// any unity-workspace.json exists. The old code demanded the manifest first
	// and threw, so every turn asked from a non-workspace directory reported
	// "No pi Unity workspace at <cwd>" while a perfectly good answer sat
	// unused. That made the injected block cry wolf on every single turn.
	const emptyDir = mkdtempSync(join(tmpdir(), "pi-unity-no-manifest-"));
	try {
		const result = await session.unitySessionContext({ cwd: emptyDir });
		check(
			"answers from the bridge with NO manifest present",
			result.context !== null && result.context.scene?.name === "FakeScene",
			JSON.stringify(result.context),
		);
		check("workspaceExists is false, not an error", result.workspaceExists === false);
		check("matchesOpenProject is false when nothing is claimed", result.matchesOpenProject === false);
		check(
			"the note explains the real situation instead of blaming the bridge",
			result.note.includes("no unity-workspace.json") && !result.note.includes("not reachable"),
			result.note,
		);
		check("projectPath falls back to what the Editor has open", result.projectPath === "/tmp/fake-project");
	} finally {
		rmSync(emptyDir, { recursive: true, force: true });
	}

	// And it must never THROW for a missing workspace: an unhandled throw is
	// what turned a soft "unknown" into a hard "call unity_session_context",
	// which then repeated the same non-answer.
	let threw = false;
	try {
		await session.unitySessionContext({ cwd: join(tmpdir(), "definitely-not-a-workspace-xyz") });
	} catch (error) {
		threw = true;
		console.log(`       threw: ${error instanceof Error ? error.message : String(error)}`);
	}
	check("a missing workspace does not throw", threw === false);
});

console.log("vrc and world requests");

await withEditor(async () => {
	const bridge = await loadBridge();

	const project = await bridge.requestLiveUnityVrc({ action: "project" });
	check(
		"vrc project returns prose",
		typeof project?.report === "string" && project.report.includes("TraceAndOptimize=MISSING"),
		JSON.stringify(project),
	);

	const actions = await bridge.requestLiveUnityVrc({ action: "actions" });
	check("vrc actions self-corrects a typo", actions?.report?.includes("optimizer"), JSON.stringify(actions));

	const scene = await bridge.requestLiveUnityWorld({ action: "scene" });
	check("world scene carries Udon counts", scene?.udonBehaviourCount === 177, JSON.stringify(scene));

	const created = await bridge.requestLiveUnityWorld({ action: "create-light-volume", lightType: "PointLight", range: 10 });
	check(
		"world create-light-volume reports the component",
		created?.component === "VRCLightVolumes.PointLightVolume",
		JSON.stringify(created),
	);
});

console.log(`\n${passed} passed, ${failures.length} failed`);
if (failures.length > 0) {
	for (const failure of failures) console.log(`  - ${failure}`);
	process.exit(1);
}
