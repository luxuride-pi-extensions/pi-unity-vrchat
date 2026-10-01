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
		// dir is handed to the callback so a test can write a file the fake Editor
		// claims to have produced, rather than asserting against a path nothing wrote.
		return await fn({ dir, bridgeDir, setControl: (o) => writeFileSync(join(bridgeDir, "control.json"), JSON.stringify(o)) });
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

	// A fractional range is the case that silently broke: the C# side read it
	// with an int parser, which rejects "12.5" and returns the fallback, so the
	// volume came back at the default while the caller was told it had been set.
	const fractional = await bridge.requestLiveUnityWorld({
		action: "create-light-volume",
		lightType: "SpotLight",
		range: 12.5,
		intensity: 3.5,
		color: "ff8844",
	});
	check("world create-light-volume keeps a fractional range", fractional?.range === "12.5", JSON.stringify(fractional));
	check("world create-light-volume keeps a fractional intensity", fractional?.intensity === "3.5", JSON.stringify(fractional));

	// find-behaviour with no gameObject searches the whole scene and says so,
	// rather than reporting a miss under a GameObject that was never named.
	const sceneWide = await bridge.requestLiveUnityWorld({
		action: "find-behaviour",
		behaviour: "VRC.SDK3.Components.VRCSceneDescriptor",
	});
	check("world find-behaviour finds scene-wide by full name", sceneWide?.found === true, JSON.stringify(sceneWide));
	check(
		"world find-behaviour reports the type it resolved to",
		sceneWide?.resolvedType === "VRC.SDK3.Components.VRCSceneDescriptor",
		JSON.stringify(sceneWide),
	);
	check("world find-behaviour says it searched the whole scene", sceneWide?.searched === "the whole scene", JSON.stringify(sceneWide));

	const scoped = await bridge.requestLiveUnityWorld({
		action: "find-behaviour",
		behaviour: "VRC.SDK3.Components.VRCObjectSync",
		gameObject: "VRCWorld",
	});
	check("world find-behaviour reports the scope it searched", scoped?.searched === '"VRCWorld" and its children', JSON.stringify(scoped));

	// The toolkit and quest-switch parameters reach the bridge as named keys
	// rather than a positional blob, so a dropped field here is what makes a
	// mutating world action quietly act on the wrong thing.
	const questAdd = await bridge.requestLiveUnityWorld({ action: "quest-add", object: "Directional Light" });
	check("world quest-add carries the object name and tags its report", questAdd?.action === "quest-add", JSON.stringify(questAdd));

	const questApply = await bridge.requestLiveUnityWorld({ action: "quest-apply", target: "android" });
	check("world quest-apply carries the target platform", questApply?.action === "quest-apply", JSON.stringify(questApply));

	const toggle = await bridge.requestLiveUnityWorld({ action: "toggle-tag", tag: "Interactable", inactive: true });
	check("world toggle-tag carries the tag and the inactive flag", toggle?.action === "toggle-tag", JSON.stringify(toggle));

	// The new world actions each carry parameters that existed for exactly one
	// action, so a key dropped in the bridge payload is silent: the C# side
	// reads an absent field and falls back to a default that looks plausible.
	const createBehaviour = await bridge.requestLiveUnityWorld({
		action: "create-behaviour",
		behaviour: "VRC.SDK3.Components.VRCObjectSync",
		gameObject: "Pi_Sync",
		fields: "allowCollisionOwnershipTransfer=false",
	});
	check("world create-behaviour carries the behaviour and field list", createBehaviour?.action === "create-behaviour", JSON.stringify(createBehaviour));

	const convert = await bridge.requestLiveUnityWorld({
		action: "convert-light",
		gameObject: "Directional Light",
		lightType: "PointLight",
		range: 8.5,
		keepOriginal: "false",
	});
	check("world convert-light carries the range and the keep-original choice", convert?.action === "convert-light", JSON.stringify(convert));

	// The <package>-set actions are the one place a FIELD NAME and its value
	// travel as strings, so both keys are asserted: losing value while keeping
	// property writes an empty string into the field the user asked about.
	const audioSet = await bridge.requestLiveUnityWorld({
		action: "audio-link-set",
		gameObject: "Pi_AudioLink",
		property: "gain",
		value: "0.35",
	});
	check("world audio-link-set carries the field and its value", audioSet?.action === "audio-link-set", JSON.stringify(audioSet));

	// The authoring actions each added their own parameter keys, and a key the
	// payload drops is silent: the C# side reads an absent field and falls back
	// to a default that looks plausible in the report.
	const shaderFix = await bridge.requestLiveUnityWorld({
		action: "fix-android-shaders",
		material: "GridFloor_udon",
		shader: "VRChat/Mobile/Standard Lite",
	});
	check("world fix-android-shaders carries the material and target shader", shaderFix?.action === "fix-android-shaders", JSON.stringify(shaderFix));

	const spawn = await bridge.requestLiveUnityWorld({
		action: "add-spawn-point",
		position: "1,2,3",
		rotation: "0,90,0",
	});
	check("world add-spawn-point carries its position and rotation", spawn?.action === "add-spawn-point", JSON.stringify(spawn));

	const collider = await bridge.requestLiveUnityWorld({
		action: "add-collider",
		gameObject: "Floor",
		shape: "Box",
		size: "4",
		isTrigger: "false",
	});
	check("world add-collider carries the shape, size and trigger flag", collider?.action === "add-collider", JSON.stringify(collider));

	const audioSource = await bridge.requestLiveUnityWorld({
		action: "add-audio-source",
		clip: "SampleAudio",
		volume: "0.8",
		spatial: "0",
		loop: "true",
	});
	check("world add-audio-source carries the clip and its audio settings", audioSource?.action === "add-audio-source", JSON.stringify(audioSource));

	const loadType = await bridge.requestLiveUnityWorld({ action: "set-clip-load-type", clip: "SampleAudio", loadType: "Streaming" });
	check("world set-clip-load-type carries the clip and the load type", loadType?.action === "set-clip-load-type", JSON.stringify(loadType));
});

console.log("compile status: the compiler's own errors");

// compile-status is the one request whose absence made build-checks pass a
// project whose scripts had never compiled, so its shape is asserted field by
// field rather than by "it returned something".
await withEditor(async () => {
	const live = await loadBridge();
	const status = await live.requestLiveUnityBridge({ payload: { type: "compile-status" }, timeoutMs: 5_000 });
	check("compile-status reports the compiler errors, not just isCompiling", Array.isArray(status?.errors) && status.errors.length > 0, JSON.stringify(status));
	check(
		"a compile error keeps the compiler's own wording and position",
		/\(\d+,\d+\): error CS\d+:/.test(status?.errors?.[0] ?? ""),
		status?.errors?.[0],
	);
	// errorCount is a SEPARATE number in the C# because the Json writer's item
	// count is 0 for a flat array of strings -- deriving it from the array here
	// would make this check pass on the very bug the field exists to catch.
	check("compile-status errorCount agrees with the errors array", status?.errorCount === status?.errors?.length, JSON.stringify(status));
	check("compile-status lists the scripts that are not compiled yet", Array.isArray(status?.scriptsNeedingCompile) && status.scriptsNeedingCompile.length > 0, JSON.stringify(status));
	check("compile-status sets needsCompile from those scripts", status?.needsCompile === true, JSON.stringify(status));
	check("compile-status names the log it read, so a reader can go and look", typeof status?.logPath === "string" && status.logPath.length > 0, JSON.stringify(status));
	check("compile-status names the project it compiled", status?.projectPath === "/tmp/fake-project", JSON.stringify(status));
});

console.log("the non-world request types: every payload key survives the round trip");

// These wrappers had no handler and no test at all, which meant a key dropped
// while the payload grew was invisible: the C# side reads an absent field and
// falls back to a default that reads back as plausible. Each check below names
// the field whose loss would be silent.
await withEditor(async ({ dir }) => {
	const bridge = await loadBridge();

	// The three that MUTATE. A dropped key here is not a missing report, it is a
	// write to the wrong thing or an edit that says it happened when it did not.
	const inspect = await bridge.requestLiveUnityInspect({
		gameObject: "Floor",
		component: "MeshRenderer",
		includeChildComponents: true,
	});
	check(
		"inspect carries the object, the component and the child flag",
		inspect?.gameObject === "Floor" && inspect?.component === "MeshRenderer" && inspect?.includeChildComponents === true,
		JSON.stringify(inspect),
	);

	// The real bridge REFUSES an ambiguous bare name and names the candidates.
	// A double that answered anyway would let a wrapper that lost the gameObject
	// pass this suite, so the refusal is exercised instead.
	let ambiguous;
	try {
		ambiguous = await bridge.requestLiveUnityInspect({ gameObject: "Ambiguous" });
	} catch (error) {
		ambiguous = { error: String(error?.message ?? error) };
	}
	check(
		"inspect surfaces the refusal to guess at an ambiguous name",
		String(ambiguous?.error ?? "").includes("not unique"),
		JSON.stringify(ambiguous),
	);

	const setProperty = await bridge.requestLiveUnitySetProperty({
		gameObject: "Floor",
		component: "MeshRenderer",
		property: "m_Materials",
		value: "GridFloor_udon",
		space: "scene",
	});
	check(
		"set-property carries the property path and its value",
		setProperty?.property === "m_Materials" && setProperty?.value === "GridFloor_udon",
		JSON.stringify(setProperty),
	);
	check("set-property reports the space it wrote into", setProperty?.space === "scene", JSON.stringify(setProperty));
	check("set-property marks the scene dirty rather than claiming a save", setProperty?.dirty === true && setProperty?.changed === true, JSON.stringify(setProperty));

	const setTransform = await bridge.requestLiveUnitySetTransform({
		gameObject: "Pi_SpawnPoint",
		position: "1,2,3",
		rotationEuler: "0,90,0",
		scale: "2,2,2",
		space: "world",
	});
	check(
		"set-transform carries position, rotation and scale",
		setTransform?.position === "1,2,3" && setTransform?.rotationEuler === "0,90,0" && setTransform?.scale === "2,2,2",
		JSON.stringify(setTransform),
	);
	check("set-transform carries the space, so local and world stay distinct", setTransform?.space === "world", JSON.stringify(setTransform));

	// get-bones with a filter: an avatar has 180+ bones, so a payload that lost
	// the array returns everything and a test asserting "has bones" still passes.
	const bones = await bridge.requestLiveUnityBones({
		gameObject: "Avatar",
		bones: ["LeftHand", "Head"],
		limit: 200,
	});
	check(
		"get-bones carries the name filter through to the bridge",
		Object.keys(bones?.humanoidBones ?? {}).length === 2 && "LeftHand" in (bones?.humanoidBones ?? {}),
		JSON.stringify(bones),
	);

	// geometry-probe and prefab-info answer questions a render cannot, so the
	// shape is what matters: an empty list where objects were named would read
	// as "these objects have no mesh" rather than as a dropped key.
	const probe = await bridge.requestLiveUnityGeometryProbe({ objects: ["Floor", "Main Camera"] });
	check(
		"geometry-probe returns one entry per object it was asked about",
		Array.isArray(probe?.objects) && probe.objects.length === 2,
		JSON.stringify(probe),
	);

	const prefab = await bridge.requestLiveUnityPrefabInfo({ prefabPath: "Assets/PiImported/Chair.prefab" });
	check("prefab-info answers for the asset path given", prefab?.prefabPath === "Assets/PiImported/Chair.prefab", JSON.stringify(prefab));
	check(
		"prefab-info reports the bounds STATE, not just a size",
		typeof prefab?.bounds?.state === "string",
		JSON.stringify(prefab),
	);

	// instantiate-prefab places and names in ONE call, so the returned path is
	// the only stable handle for everything after it.
	const placed = await bridge.requestLiveUnityInstantiatePrefab({
		prefabPath: "Assets/PiImported/Chair.prefab",
		position: "2,0,3",
		rotation: "0,90,0",
		scale: "1,1,1",
	});
	check(
		"instantiate-prefab places it and returns the instance path to address it by",
		placed?.instancePath === "Chair (2)/Seat" && placed?.position === "2,0,3",
		JSON.stringify(placed),
	);

	const find = await bridge.requestLiveUnityFindSourceModels({ gameObjectNames: ["Floor", "Chair"] });
	check(
		"find-source-models resolves every named object",
		(find?.models ?? []).length === 2 && find.models[0].assetPath === "Assets/Models/Floor.fbx",
		JSON.stringify(find),
	);

	const exported = await bridge.requestLiveUnityExportSourceModels({
		gameObjectNames: ["Floor"],
		outputDir: "/tmp/fake-export",
	});
	check(
		"export-source-models carries the destination it copied into",
		String(exported?.exported?.[0]?.copiedTo ?? "").startsWith("/tmp/fake-export/"),
		JSON.stringify(exported),
	);

	// capture-view and set-tool are the two that write into the Editor's own UI
	// state, which is invisible to every other check here.
	const capturePath = join(dir, "capture.png");
	const capture = await bridge.requestLiveUnityCaptureView({ outputPath: capturePath, width: 800, height: 600 });
	check("capture-view carries the requested size", capture?.width === 800 && capture?.height === 600, JSON.stringify(capture));
	check(
		"capture-view reports the path it WROTE, and the file is there",
		capture?.imagePath === capturePath && existsSync(capturePath),
		JSON.stringify(capture),
	);

	const renderPath = join(dir, "render.png");
	const render = await bridge.requestLiveUnityRender({ outputPath: renderPath, width: 640, height: 480, cameraName: "Main Camera" });
	check(
		"render reports the path it wrote, the size it used and the camera it chose",
		render?.imagePath === renderPath && render.width === 640 && render.cameraName === "Main Camera" && existsSync(renderPath),
		JSON.stringify(render),
	);

	const tool = await bridge.requestLiveUnitySetTool("Rotate");
	check("set-tool carries the tool name", tool?.tool === "Rotate" && tool?.activeTransformTool === "Rotate", JSON.stringify(tool));

	// Negative cases, so the checks above are not theatre: each double REFUSES
	// the payload a dropped key would produce. Without these, a handler that
	// answered everything would pass the positive checks just as happily.
	const noValue = await bridge
		.requestLiveUnityBridge({ payload: { type: "set-property", gameObject: "Floor", component: "MeshRenderer", property: "m_Materials" }, timeoutMs: 5_000 })
		.then((r) => ({ error: `resolved instead of refusing: ${JSON.stringify(r)}` }))
		.catch((e) => ({ error: String(e?.message ?? e) }));
	check("set-property refuses a payload with no value at all", noValue.error.includes("what to write"), JSON.stringify(noValue));

	const nothingToDo = await bridge
		.requestLiveUnityBridge({ payload: { type: "set-transform", gameObject: "Floor" }, timeoutMs: 5_000 })
		.then((r) => ({ error: `resolved instead of refusing: ${JSON.stringify(r)}` }))
		.catch((e) => ({ error: String(e?.message ?? e) }));
	check("set-transform refuses a payload that changes nothing", nothingToDo.error.includes("nothing to change"), JSON.stringify(nothingToDo));

	const noObject = await bridge
		.requestLiveUnityBridge({ payload: { type: "inspect" }, timeoutMs: 5_000 })
		.then((r) => ({ error: `resolved instead of refusing: ${JSON.stringify(r)}` }))
		.catch((e) => ({ error: String(e?.message ?? e) }));
	check("inspect refuses a payload with no object named", noObject.error.includes("gameObject"), JSON.stringify(noObject));

	// scene-info and session-context are read-only, so their coverage is only
	// about not being empty.
	const info = await bridge.requestLiveUnitySceneInfo(["objects", "cameras"]);
	check("scene-info returns the categories it was asked for", Array.isArray(info?.objects) && Array.isArray(info?.cameras), JSON.stringify(info));

	const context = await bridge.requestLiveUnitySessionContext();
	check("session-context reports the open scene", context?.scene?.name === "FakeScene", JSON.stringify(context));
});

console.log("bridge status: logs and a corrupt lock");

await withEditor(async ({ bridgeDir }) => {
	const status = await jiti.import(join(EXT, "unity", "status.ts"), { default: false });

	// get-log was implemented in C# with no TS wrapper at all: the one place a
	// caller is told where to look when a request failed was unreachable. It is
	// also the only request that can REPORT a path that does not exist, so the
	// wrapper must pass the probe flag through rather than assume the file is
	// there.
	const logs = await (await loadBridge()).requestLiveUnityLog({ timeoutMs: 5_000 });
	check("get-log returns candidates", Array.isArray(logs?.logs) && logs.logs.length === 3, JSON.stringify(logs));
	check(
		"each candidate is probed, not asserted",
		logs.logs.every((l) => typeof l.exists === "boolean" && typeof l.role === "string"),
		JSON.stringify(logs.logs),
	);
	check(
		"the Editor's own log is distinguished from the player log",
		logs.logs.some((l) => l.exists === true && l.role.includes("Editor log")) &&
			logs.logs.some((l) => l.exists === false && l.role === "player log"),
		JSON.stringify(logs.logs),
	);

	// Idle + alive: the log lookup runs.
	const healthy = await status.unityBridgeStatus({ isAlive: () => true });
	check("status carries the log section", Array.isArray(healthy.logs?.logs));
	check("an idle reachable Editor is not an error", healthy.logError === null, String(healthy.logError));
	check("idle Editor reports ready", healthy.recommendation.includes("idle and ready"), healthy.recommendation);

	// A failing log lookup must not fail the status call. This is the one tool
	// that has to stay safe to call exactly when things are broken, so a throw
	// here would be worse than an empty log section.
	const degraded = await status.unityBridgeStatus({
		isAlive: () => true,
		readLogs: async () => {
			throw new Error("bridge went away mid-lookup");
		},
	});
	check("a failed log lookup does not throw", degraded.logs === null);
	check("the failure is reported, not swallowed", degraded.logError?.includes("mid-lookup"), String(degraded.logError));
	check("the Editor is still described as reachable", degraded.recommendation.includes("reachable"), degraded.recommendation);

	// Dead Editor: no log lookup at all, because it cannot succeed.
	const dead = await status.unityBridgeStatus({ isAlive: () => false });
	check("a dead Editor is not asked for logs", dead.logs === null && dead.logError === null);

	// Busy Editor: deliberately NOT asked, because the lookup takes the bridge
	// lock and would queue behind the very request being reported on. A real
	// lock file is what makes it busy — stubbing a field would test the stub.
	writeFileSync(
		join(bridgeDir, "bridge.lock"),
		JSON.stringify({ pid: 4242, label: "in-flight", acquiredAt: new Date().toISOString() }),
	);
	await sleep(50);
	let lookedWhileBusy = false;
	const busy = await status.unityBridgeStatus({
		isAlive: () => true,
		readLogs: async () => {
			lookedWhileBusy = true;
			return { logs: [] };
		},
	});
	check("a busy Editor does not get asked for logs", lookedWhileBusy === false);
	check("a held lock is busy", busy.busy === true, JSON.stringify(busy.lock));
	check("a held lock is not flagged unreadable", busy.lock?.unreadable === false, JSON.stringify(busy.lock));
	rmSync(join(bridgeDir, "bridge.lock"), { force: true });
	await sleep(50);

	// A lock file that exists but does not parse is still a HELD lock. Reporting
	// it as absent made the status tool say "idle" while a request was in
	// flight, which invites exactly the concurrent mutation the lock prevents.
	writeFileSync(join(bridgeDir, "bridge.lock"), "{corrupt");
	await sleep(50);
	const corrupt = await status.unityBridgeStatus({ isAlive: () => true });
	check("a corrupt lock still counts as busy", corrupt.busy === true, JSON.stringify(corrupt.lock));
	check("the lock is flagged unreadable", corrupt.lock?.unreadable === true, JSON.stringify(corrupt.lock));
	check(
		"a corrupt lock is explained, not reported as idle",
		corrupt.recommendation.includes("did not") && corrupt.recommendation.includes("parse"),
		corrupt.recommendation,
	);
	rmSync(join(bridgeDir, "bridge.lock"), { force: true });
	await sleep(50);
	const cleared = await status.unityBridgeStatus({ isAlive: () => true });
	check("removing the lock clears busy", cleared.busy === false, JSON.stringify(cleared.lock));
});

console.log(`\n${passed} passed, ${failures.length} failed`);
if (failures.length > 0) {
	for (const failure of failures) console.log(`  - ${failure}`);
	process.exit(1);
}
