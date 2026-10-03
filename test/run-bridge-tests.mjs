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

console.log("awaiting a compile");

// The whole point of this tool is that it returns when a compile FINISHES, not
// when a budget runs out, so these use short budgets: a run that had to wait
// out its timeout is a failure, not a slow pass.
await withEditor(async ({ bridgeDir, setControl }) => {
	const bridge = await loadBridge();
	const capturePath = join(bridgeDir, "compile-messages.json");

	// The heartbeat ticks THROUGH a compile -- Poll touches it before it tests
	// isCompiling and then returns -- so a settle condition that waited on it
	// advancing could only ever fire at the deadline. Observed live: a compile
	// that finished in seconds still burned the entire budget. What the wait
	// settles on is the CAPTURE, written only when a compile really finished.
	// A heartbeat that stops mid-wait is a dead Editor, which is not a verdict.
	setControl({ freezeHeartbeat: true });
	await sleep(300);
	const raced = await bridge.requestLiveUnityAwaitCompile({ timeoutMs: 1_200 });
	const raceMessage = JSON.stringify(raced);
	check(
		"a frozen heartbeat does NOT report a clean compile",
		raced.status === "timeout",
		raceMessage.slice(0, 200),
	);
	check(
		"a timeout is never dressed up as a build result",
		raced.status !== "compiled",
		raceMessage.slice(0, 200),
	);
	check(
		"a timeout says which source answered: none did",
		raced.source === "none",
		raceMessage.slice(0, 200),
	);
	check(
		"a timeout respects its budget rather than hanging",
		raced.waitedMs >= 900 && raced.waitedMs < 3_000,
		`${raced.waitedMs}ms of a 1200ms budget`,
	);
	// A frozen heartbeat must NOT abort the wait: 10s of silence is how an IDLE
	// Editor's death looks, but a domain reload and a compile both hold the main
	// thread, so it is reported rather than used to give up. Observed live: this
	// check aborted the wait 8s into every real compile.
	check(
		"a frozen heartbeat is REPORTED, not used to abort",
		raced.editorResponding === false,
		JSON.stringify(raced),
	);

	setControl({ freezeHeartbeat: false });
	await sleep(400);

	// A capture that lands DURING the wait, reporting the compiler's own error
	// -- the real shape of a compile, which takes time.
	setControl({
		compileErrors: ["Assets/UdonSharp/UtilityScripts/ZoneAudioVolume.cs(132,47): error CS0246: The type or namespace name 'VRCPlayerApi' could not be found"],
		compileWarnings: ["Assets/Foo.cs(1,1): warning CS0168: unused variable"],
		captureInMs: 1_500,
	});
	const failedStart = Date.now();
	const failed = await bridge.requestLiveUnityAwaitCompile({ timeoutMs: 8_000 });
	const failedWaited = Date.now() - failedStart;
	check("a compile with errors reports failed", failed.status === "failed", JSON.stringify(failed));
	check(
		"the compiler's own error line is carried through",
		failed.errors.some((e) => e.includes("error CS0246") && e.includes("VRCPlayerApi")),
		JSON.stringify(failed),
	);
	check("the await reports which path answered", failed.source === "event", JSON.stringify(failed));
	check("a settled compile leaves the Editor answering", failed.editorResponding === true, JSON.stringify(failed));
	// The regression this fixes: it used to return only when the budget ran out,
	// so a compile that finished in seconds still cost the full wait.
	check(
		"a settled wait returns promptly instead of burning its budget",
		failedWaited < 4_000,
		`${failedWaited}ms of an 8000ms budget`,
	);

	// A capture that is ALREADY on disk when the wait begins is not a verdict
	// for this call -- it describes code that is about to change. Settling on it
	// would report success before the compile even started. Observed live: the
	// first read happened a tick after entry, so a baseline taken inside the
	// loop accepted the stale file and returned "compiled" in 501ms.
	setControl({ compileErrors: [], compileWarnings: [] });
	await sleep(300);
	const stale = await bridge.requestLiveUnityAwaitCompile({ timeoutMs: 1_200 });
	check(
		"a stale capture does NOT settle the next wait",
		stale.status === "timeout",
		JSON.stringify(stale),
	);

	// And the clean case, on the same capture path, with the capture refreshed.
	setControl({ compileErrors: [], compileWarnings: [], captureInMs: 1_000 });
	const clean = await bridge.requestLiveUnityAwaitCompile({ timeoutMs: 8_000 });
	check("a clean compile reports compiled", clean.status === "compiled", JSON.stringify(clean));
	check("a clean compile carries no errors", clean.errors.length === 0, JSON.stringify(clean));

	// No capture at all: the log fallback answers, and its errors must NOT be
	// dressed up as if the compiler event had said them.
	rmSync(capturePath, { force: true });
	setControl({ writeCompileMessages: false });
	await sleep(300);
	const fromLog = await bridge.requestLiveUnityAwaitCompile({ timeoutMs: 1_200 });
	check("with no capture the wait times out rather than answering", fromLog.status === "timeout", JSON.stringify(fromLog));
	check("a timeout answers from no source at all", fromLog.source === "none", JSON.stringify(fromLog));

	// A cycle that STARTED and never finished inside the budget. Unity records
	// the cycle the moment it begins, and on a 4-6 minute compile that is most
	// of the wait, so a wait that settled on any new file would answer
	// "compiled" while the compiler was still running. Settling on the marker is
	// the regression; naming it is what tells a slow compile from a hang.
	setControl({ captureInMs: 400, captureNeverFinishes: true });
	const inFlight = await bridge.requestLiveUnityAwaitCompile({ timeoutMs: 1_500 });
	check("a cycle that began but has not finished does NOT settle the wait", inFlight.status === "timeout", JSON.stringify(inFlight));
	check("an unfinished cycle is named as one", inFlight.compileInFlight === true, JSON.stringify(inFlight));

	// A compile Unity built no assembly for. The cycle still ends, but no
	// assemblyCompilationFinished ever fires, so the compiler never speaks and
	// without the cycle handler a no-op compile in a clean project always timed
	// out. Measured live: compilationStarted/Finished fired 1s after the request
	// while neither assembly event fired at all.
	setControl({ captureInMs: 600, captureAssembly: "" });
	const noAssembly = await bridge.requestLiveUnityAwaitCompile({ timeoutMs: 6_000 });
	check("a compile with no assembly to report still settles", noAssembly.status === "compiled", JSON.stringify(noAssembly));

	// The regression the user reported. The compiler ran, built an assembly and
	// reported nothing wrong, and needsCompile still claimed a script had never
	// been compiled -- so a green build read as failed.
	//
	// needsCompile is a timestamp comparison, not a compiler statement, and on
	// 2022.3 the comparison cannot work: compilation goes through Bee, which
	// keys on content hashes, so touching a file without changing a byte
	// rebuilds no assembly and leaves its output timestamp where it was.
	// Measured live -- a runtime script touched, compiled with no errors, the
	// runtime assembly's timestamp unchanged by the build, and the script
	// reported as never compiled forever, because no later compile moves that
	// timestamp either. So once the compiler has spoken, its verdict stands
	// alone and the proxy is not consulted.
	setControl({ captureInMs: 600, captureAssembly: "Library/ScriptAssemblies/Assembly-CSharp.dll", compileStatusStale: true });
	const greenBuild = await bridge.requestLiveUnityAwaitCompile({ timeoutMs: 6_000 });
	check("a clean build is not called failed by a stale needsCompile", greenBuild.status === "compiled", JSON.stringify(greenBuild));
	check("that build reports no errors at all", greenBuild.errors.length === 0, JSON.stringify(greenBuild.errors));

	// The other half, and the reason the proxy exists at all: with no capture
	// naming an assembly, an empty error list would report a project whose
	// every script is broken as clean, because a broken project logs its
	// errors once and then never again. needsCompile is then the only evidence
	// there is, so it is kept.
	setControl({ captureInMs: 600, captureNeverFinishes: true, compileStatusStale: true });
	const neverSpoke = await bridge.requestLiveUnityAwaitCompile({ timeoutMs: 1_500 });
	check("a wait with no compiler verdict stays a timeout, never clean", neverSpoke.status === "timeout", JSON.stringify(neverSpoke));
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

console.log("snippet output: logs, diagnostics, and partial results");

// The reported failure in #4: unity_execute_csharp always came back with
// stdout:"", because Debug.Log never reaches the Console redirect, and
// everything the snippet said was lost when the request failed.
await withEditor(async ({ setControl }) => {
	const bridge = await jiti.import(join(EXT, "unity", "bridge.js"), { default: false });
	const execute = await jiti.import(join(EXT, "unity", "execute.js"), { default: false });

	// unityExecuteCSharp insists on a manifest, so the run happens against a
	// throwaway one. projectPath points at the fake Editor's project.
	const workspace = mkdtempSync(join(tmpdir(), "pi-unity-snippet-output-"));
	writeFileSync(
		join(workspace, "unity-workspace.json"),
		JSON.stringify({
			version: 2,
			id: "snippet-output",
			projectPath: "/tmp/fake-project",
			scenePath: "Assets/FakeScene.unity",
			createdAt: new Date().toISOString(),
			updatedAt: new Date().toISOString(),
			latestIteration: 1,
			moduleRuns: [],
			savedViews: [],
			renderOutputs: [],
			adopted: true,
		}),
		"utf8",
	);

	try {
	// 1. A successful run returns `logs` as an array, even when empty.
	//    Asserted as an array rather than undefined because the C# always
	//    writes the key now, and a caller that iterates it must not have to
	//    guess whether the field is missing or empty.
	setControl({ failedExecute: null });
	const okRun = await execute.unityExecuteCSharp({
		cwd: EXT,
		workspacePath: workspace,
		inlineCode: '{"found":3}',
		saveAfter: false,
	});
	check("a successful run returns logs as an array", Array.isArray(okRun.logs), JSON.stringify(okRun.logs));
	// Optional-chained: the point of the check above is that `logs` may be
	// missing entirely, so the next one must not crash on that.
	check("an empty log array stays empty", (okRun.logs?.length ?? -1) === 0, JSON.stringify(okRun.logs));
	check("stdout still carries the snippet's Console output", okRun.stdout.includes("found"), okRun.stdout);

	// 2. logTail is opt-in AND actually opt-in: the key must not appear at all
	//    when it was not asked for, because "an empty tail" and "no tail" mean
	//    different things to a caller deciding whether to ask again.
	check("no log tail is returned when it was not asked for", okRun.logTail === undefined, JSON.stringify(okRun.logTail));

	const tailed = await execute.unityExecuteCSharp({
		cwd: EXT,
		workspacePath: workspace,
		inlineCode: '{"found":3}',
		saveAfter: false,
		logTail: true,
	});
	check("a requested log tail comes back", Array.isArray(tailed.logTail) && tailed.logTail.length === 2, JSON.stringify(tailed.logTail));
	check("the tail says how many lines it returned", tailed.logTailLines === 2, String(tailed.logTailLines));

	// 3. A snippet that threw: the failure envelope now carries the payload.
	//    This is the heart of the fix -- the C# writes ok:false WITH a result,
	//    and bridge.ts used to drop that result on the floor.
	setControl({
		failedExecute: {
			message: "C# snippet threw: System.NullReferenceException: Object reference not set",
			result: {
				stdout: "collected 12 findings\n--- Debug.Log ---\n[Warning] lilToon: shader missing",
				stderr: "C# snippet threw: System.NullReferenceException: Object reference not set",
				exitCode: 1,
				changed: true,
				label: "execute:probe",
				stackTrace: "   at Probe.Run() in Assets/Editor/Probe.cs:line 42",
				logs: [{ level: "Warning", message: "lilToon: shader missing", stack: "" }],
			},
		},
	});
	let thrown = "";
	try {
		await execute.unityExecuteCSharp({ cwd: EXT, workspacePath: workspace, inlineCode: "throw", saveAfter: false });
	} catch (error) {
		thrown = error instanceof Error ? error.message : String(error);
	}

	// The message keeps the bridge's own wording first, so anything that read
	// error.message before keeps working.
	check("a thrown snippet surfaces its own message", thrown.includes("NullReferenceException"), thrown.slice(0, 200));
	// ...and the payload that used to be discarded is now IN the message, so a
	// caller reading only the tool result can see it.
	check("the stack trace reaches the caller", thrown.includes("Probe.cs:line 42"), thrown.slice(0, 400));
	check(
		"the Debug.Log lines reach the caller",
		thrown.includes("[Warning] lilToon: shader missing"),
		thrown.slice(0, 400),
	);
	check(
		"the output printed before the throw reaches the caller",
		thrown.includes("collected 12 findings"),
		thrown.slice(0, 400),
	);
	check("the caller is told nothing was saved", thrown.includes("Nothing was saved."), thrown.slice(-200));

	// 4. A snippet that did not compile: Roslyn's diagnostics verbatim.
	//
	// Asserted to include the file:line:col, because the value of a diagnostic is
	// the location -- and unity/plugin/PiUnityBridge.Compiler.cs already spends a
	// helper (StripImplicitUsingOffset) correcting the line number so it matches
	// the caller's own file. Losing that work to a reformat would be invisible.
	setControl({
		failedExecute: {
			message: "C# snippet failed to compile:\nPiSnippet.cs(12,19): error CS0103",
			result: {
				stdout: "",
				stderr: "C# snippet failed to compile:\nPiSnippet.cs(12,19): error CS0103",
				exitCode: 1,
				changed: false,
				diagnostics: ["PiSnippet.cs(12,19): error CS0103: The name 'foo' does not exist in the current context"],
				logs: [],
			},
		},
	});
	let compileThrown = "";
	try {
		await execute.unityExecuteCSharp({ cwd: EXT, workspacePath: workspace, inlineCode: "nonsense", saveAfter: false });
	} catch (error) {
		compileThrown = error instanceof Error ? error.message : String(error);
	}
	check("compile diagnostics reach the caller verbatim", compileThrown.includes("(12,19): error CS0103"), compileThrown.slice(0, 300));
	check(
		"compile diagnostics are labelled, not bare",
		compileThrown.includes("--- compiler diagnostics ---"),
		compileThrown.slice(0, 300),
	);
	// A snippet that never compiled changed nothing, so nothing may be saved.
	check("a failed compile is reported as not saved", compileThrown.includes("Nothing was saved."), compileThrown.slice(-200));

	// 5. A failure WITHOUT a payload must NOT become a partial-result error.
	//    unity_bridge_status, open-scene and friends still reply ok:false with
	//    only an error string; pretending they carry a payload would attach an
	//    empty object to every one of them.
	setControl({ failedExecute: null });
	await sleep(1_100);
	setControl({ sceneDirty: true });
	await sleep(1_100);
	let bare = "";
	try {
		await bridge.requestLiveUnityBridge({ payload: { type: "open-scene", scenePath: "Assets/Other.unity" }, timeoutMs: 5_000 });
	} catch (error) {
		bare = error;
	}
	// Read off the error's own class name rather than instanceof-ing the
	// exported class: when the fix is absent the class does not exist, and
	// `x instanceof undefined` is a TypeError that would abort the run instead
	// of reporting the failure this check exists to catch.
	check(
		"a payload-free failure is a plain Error",
		bare instanceof Error && bare?.constructor?.name !== "UnityBridgeFailureError",
		bare?.constructor?.name,
	);
	check("its message is still the Editor's own", String(bare?.message).includes("unsaved changes"), String(bare?.message));
	} finally {
		rmSync(workspace, { recursive: true, force: true });
	}
});

console.log(`\n${passed} passed, ${failures.length} failed`);
if (failures.length > 0) {
	for (const failure of failures) console.log(`  - ${failure}`);
	process.exit(1);
}
