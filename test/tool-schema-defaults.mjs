/**
 * Declared defaults must mean the same thing in the schema and in the payload.
 *
 * Finding I. TypeBox's `default` is METADATA: it is shown to the model in the
 * tool description and it is not applied on the way in. index.ts declared
 * `default: "ffffff"` for color, and described defaults 1 and 0 for
 * volume/spatial, while unity/bridge.ts forwarded `options.color`,
 * `options.volume` and `options.spatial` straight through -- so an omitted
 * argument crossed the bridge as `undefined` and the C# side's own fallback
 * decided what it meant. Two places deciding what "default" means, kept in
 * step by hand, which is how they drift apart.
 *
 * The fake Editor is what proves the crossing: it REFUSES a payload carrying
 * the wrong value, so a check that only asserted "the call succeeded" would
 * pass against the old forwarding. These assertions are therefore about the
 * value the bridge actually put on the wire.
 */

import assert from "node:assert/strict";
import { existsSync, mkdirSync, mkdtempSync, rmSync, writeFileSync } from "node:fs";
import { tmpdir } from "node:os";
import { dirname, join } from "node:path";
import { fileURLToPath } from "node:url";
import { spawn } from "node:child_process";

import { JITI } from "./paths.mjs";

const HERE = dirname(fileURLToPath(import.meta.url));
const EXT = join(HERE, "..");
const sleep = (ms) => new Promise((r) => setTimeout(r, ms));

const { createJiti } = await import(JITI);
const jiti = createJiti(import.meta.url, { interopDefault: true, moduleCache: false });

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

/**
 * Drives the real bridge.ts against the fake Editor, and hands the callback
 * the request payload the double received.
 *
 * The double is the point: it REFUSES a create-light-volume whose colour is
 * not the one the caller asked for, so an assertion that the default reached
 * the wire is not the same assertion as "the call did not throw".
 */
async function withEditor(fn) {
	const dir = mkdtempSync(join(tmpdir(), "unity-vrchat-defaults-"));
	const bridgeDir = join(dir, "bridge");
	for (const sub of ["requests", "responses"]) mkdirSync(join(bridgeDir, sub), { recursive: true });
	writeFileSync(join(bridgeDir, "control.json"), "{}");

	const child = spawn(process.execPath, [join(HERE, "fake-editor.mjs"), bridgeDir], {
		stdio: ["ignore", "inherit", "inherit"],
	});
	for (let i = 0; i < 100 && !existsSync(join(bridgeDir, "heartbeat")); i += 1) await sleep(50);

	const previous = process.env.PI_UNITY_BRIDGE_DIR;
	process.env.PI_UNITY_BRIDGE_DIR = bridgeDir;
	try {
		const bridge = await jiti.import(join(EXT, "unity", "bridge.ts"), { default: false });
		return await fn(bridge);
	} finally {
		if (previous === undefined) delete process.env.PI_UNITY_BRIDGE_DIR;
		else process.env.PI_UNITY_BRIDGE_DIR = previous;
		child.kill("SIGTERM");
		await sleep(100);
		rmSync(dir, { recursive: true, force: true });
	}
}

console.log("declared defaults are applied where the payload is built");

await withEditor(async (bridge) => {
	// The declared default, reached by OMITTING the argument -- the case the
	// bug is about. The double refuses anything but "ffffff" for this action.
	let omitted = null;
	try {
		omitted = await bridge.requestLiveUnityWorld({ action: "create-light-volume", gameObject: "Pi_Light" });
	} catch (error) {
		omitted = { error: String(error?.message ?? error) };
	}
	check(
		"an omitted color reaches the bridge as the declared default",
		omitted?.color === "#ffffff",
		JSON.stringify(omitted),
	);

	// An explicit value still wins, or the default would be a second bug.
	const explicit = await bridge.requestLiveUnityWorld({ action: "create-light-volume", color: "ff8844" });
	check("an explicit color is still forwarded", explicit?.color === "#ff8844", JSON.stringify(explicit));

	// volume/spatial: the same shape, on the action whose description names
	// "Defaults to 1" and "\"0\" (default)". The double tags the action back,
	// so a dropped key is visible rather than inferred.
	const audio = await bridge.requestLiveUnityWorld({ action: "add-audio-source", clip: "Ambience" });
	check(
		"an omitted volume/spatial reaches the bridge as the declared defaults (1 and 0)",
		audio?.volume === "1" && audio?.spatial === "0",
		JSON.stringify(audio),
	);

	const audioExplicit = await bridge.requestLiveUnityWorld({ action: "add-audio-source", volume: "0.35", spatial: "1" });
	check(
		"an explicit volume/spatial still wins over the default",
		audioExplicit?.volume === "0.35" && audioExplicit?.spatial === "1",
		JSON.stringify(audioExplicit),
	);

	// `size` must NOT acquire a default: an omitted size means "keep the size
	// the collider already has", and inventing one would change behaviour. The
	// double echoes size ?? "" so "omitted" is distinguishable from "0".
	const collider = await bridge.requestLiveUnityWorld({ action: "add-collider", gameObject: "Floor", shape: "Box" });
	check("an omitted collider size is left absent (not defaulted)", collider?.size === "", JSON.stringify(collider));

	const sized = await bridge.requestLiveUnityWorld({ action: "add-collider", size: "4" });
	check("an explicit collider size is still forwarded", sized?.size === "4", JSON.stringify(sized));
});

// ---- the schema gap the C# side already refused ---------------------------
{
	const tools = [];
	const pi = {
		registerTool: (tool) => tools.push(tool),
		registerCommand: () => {},
		on: () => {},
		sendUserMessage: () => {},
	};
	const extension = await jiti.import(join(EXT, "index.ts"), { default: false });
	await extension.default(pi);

	const compose = tools.find((tool) => tool.name === "unity_compose_prefab");
	check("unity_compose_prefab is registered", compose !== undefined);

	// Finding I, third part: `members` had no minItems, so `params.members ?? []`
	// forwarded an empty array that the C# side then refused with "compose-prefab
	// requires at least one member". The schema now carries the constraint, so
	// the pair of `pairs` (minItems 2) and `members` cannot drift apart.
	const members = compose?.parameters?.properties?.members;
	check("compose-prefab members carries minItems: 1", members?.minItems === 1, JSON.stringify(members?.minItems));

	const pairs = tools.find((tool) => tool.name === "unity_geometry_probe")?.parameters?.properties?.pairs;
	check(
		"the sibling pairs constraint is unchanged (minItems 2, maxItems 2)",
		pairs?.items?.minItems === 2 && pairs?.items?.maxItems === 2,
		JSON.stringify(pairs?.items),
	);
}

console.log(`\n${passed} passed, ${failures.length} failed`);
if (failures.length > 0) {
	for (const failure of failures) console.log(`  - ${failure}`);
	process.exit(1);
}