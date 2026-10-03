/**
 * Fake Unity Editor — a test double for PiUnityBridge.cs.
 *
 * Implements the same file-queue protocol the real C# plugin does: watch
 * <bridgeDir>/requests for *.json, write <bridgeDir>/responses/<id>.json, keep
 * <bridgeDir>/heartbeat fresh. That is enough to exercise the whole TypeScript
 * side — request framing, response parsing, the cross-process disk lock,
 * timeouts, withdraw-on-timeout, error propagation — without launching Unity,
 * which needs a licence handshake, a domain reload, and a real project.
 *
 * Request types listed in HANDLERS are faked. Anything else gets a real error
 * response, deliberately: a test double that silently accepts an unimplemented
 * request would prove nothing.
 *
 * The harness steers behaviour through <bridgeDir>/control.json, e.g.
 *   { "sceneDirty": true, "delayMs": 0 }
 */

import { existsSync, readdirSync, readFileSync, unlinkSync, utimesSync, writeFileSync } from "node:fs";
import { join } from "node:path";

const bridgeDir = process.argv[2];
if (!bridgeDir) {
	console.error("usage: fake-editor.mjs <bridgeDir>");
	process.exit(2);
}

const requestsDir = join(bridgeDir, "requests");
const responsesDir = join(bridgeDir, "responses");
const heartbeatPath = join(bridgeDir, "heartbeat");
const controlPath = join(bridgeDir, "control.json");
const compileMessagesPath = join(bridgeDir, "compile-messages.json");
const captureTimers = [];

/**
 * What OnAssemblyCompilationFinished writes: the compiler's own output, to a
 * file, because the domain reload at the end of a compile discards statics.
 */
function writeCompileMessages(completed = true, assembly = "Library/ScriptAssemblies/Assembly-CSharp-Editor.dll") {
	writeFileSync(
		compileMessagesPath,
		JSON.stringify({
			finishedAt: new Date().toISOString(),
			assembly,
			completed,
			errors: completed ? state.compileErrors : [],
			warnings: completed ? state.compileWarnings : [],
		}),
	);
}

// A real Editor touches its heartbeat every poll; isLiveBridgeAlive() treats a
// heartbeat older than 10 s as "the Editor is gone", so this must stay fresh.
function touchHeartbeat() {
	const now = new Date();
	try {
		utimesSync(heartbeatPath, now, now);
	} catch {
		writeFileSync(heartbeatPath, "");
	}
}
touchHeartbeat();
const heartbeatTimer = setInterval(() => {
	if (state.freezeHeartbeat) return;
	touchHeartbeat();
}, 300);

const state = {
	/** Behave like an Editor holding unsaved scene changes. */
	sceneDirty: false,
	/** Stall each handler, so timeout and withdraw-on-timeout can be tested. */
	delayMs: 0,
	/**
	 * Freeze the heartbeat, which models an Editor that has stopped replying --
	 * hung, or reloading its domain around a compile. It must never SETTLE a
	 * wait, because the plugin touches it on every poll whether or not a compile
	 * is running; what the await reports from it is `editorResponding`.
	 */
	freezeHeartbeat: false,
	/** Write the plugin's captured compiler output, as OnAssemblyCompilationFinished does. */
	writeCompileMessages: false,
	/** The compiler errors the capture reports; empty means a clean compile. */
	compileErrors: [],
	compileWarnings: [],
	/**
	 * Report needsCompile even though a capture naming an assembly exists,
	 * which is the real shape of the regression: a script touched with
	 * unchanged content never moves its assembly's timestamp, so a
	 * timestamp-based proxy keeps calling it uncompiled.
	 */
	compileStatusStale: false,
};

const sleep = (ms) => new Promise((resolve) => setTimeout(resolve, ms));

const HANDLERS = {
	ping: () => ({ pong: true }),

	// Mirrors the C# side: each candidate is probed and `exists` is the field
	// that stops a caller trusting a path that is not there. unity_bridge_status
	// reads this, so a handler-less "get-log" would surface as logError instead of
	// a report.
	"get-log": () => ({
		logs: [
			{ path: "/tmp/fake-bridge/Editor-csharp.log", role: "pi bridge log", exists: false },
			{ path: "/tmp/fake-unity3d/Editor.log", role: "Unity Editor log for this process", exists: true },
			{ path: "/tmp/fake-project/Logs/Editor.log", role: "player log", exists: false },
		],
	}),

	// The compiler errors from the last finished compile. errorCount is written
	// separately from the array length in the C# (the Json writer's item count is
	// 0 for a flat array of strings), so the double states 1 here instead of
	// deriving it from errors.length -- a derived value would make a test pass
	// on the very C# bug the field exists to catch.
	// Mirrors CompileStatusJson: a readable capture answers, and the log parse
	// only does when there is none. Whether the capture exists is decided by the
	// FILE, because that is what the real plugin decides it by -- and it is what
	// the await path inspects before ever asking.
	"compile-status": () => {
		const captured = existsSync(compileMessagesPath);
		// compileStatusStale models the state the regression lived in: a
		// capture that names an assembly, and a needsCompile that still claims
		// a script has never been built. Real, measured -- a script touched
		// with unchanged content never moves its assembly's timestamp, because
		// Bee keys the build on content hashes.
		const stale = state.compileStatusStale === true;
		return {
			isCompiling: false,
			lastError: "",
			errors: captured ? state.compileErrors : ["Assets/UdonSharp/UtilityScripts/ZoneAudioVolume.cs(132,47): error CS0246: The type or namespace name 'VRCPlayerApi' could not be found"],
			errorCount: captured ? state.compileErrors.length : 1,
			source: captured ? "event" : "log",
			scriptsNeedingCompile: stale || !captured ? ["Assets/UdonSharp/UtilityScripts/ZoneAudioVolume.cs"] : [],
			needsCompile: stale || !captured,
			logPath: "/tmp/fake-unity3d/Editor.log",
			projectPath: "/tmp/fake-project",
		};
	},

	"get-session-context": () => ({
		projectPath: "/tmp/fake-project",
		scene: { name: "FakeScene", path: "/tmp/fake-project/Assets/FakeScene.unity" },
		selection: { gameObjects: ["Cube"], activeGameObject: "Cube" },
		tools: { activeTransformTool: "Move" },
		viewport: { perspective: "Perspective" },
	}),

	// stdout is what the binder parses for the export summary, so mirror the
	// real bridge's shape: whatever the caller sent comes back as stdout.
	"execute-csharp": (request) => ({
		stdout: request.code ?? "",
		stderr: "",
		exitCode: 0,
		changed: true,
	}),

	"open-scene": (request) => {
		if (state.sceneDirty) {
			throw new Error(
				`The open scene "FakeScene" has unsaved changes in the Unity Editor. ` +
					`Save or discard them before pi switches to "${request.scenePath}".`,
			);
		}
		return { opened: request.scenePath, path: request.scenePath };
	},

	"reload-scene": () => ({ reloaded: true, discardedUnsavedChanges: state.sceneDirty }),

	"set-tool": (request) => ({ tool: request.tool, activeTransformTool: request.tool }),

	// The real bridge returns prose for most actions, because the findings that
	// matter are sentences ("TraceAndOptimize is MISSING -- this avatar uploads
	// unoptimized"). Tests assert on that shape, so the double mirrors it.
	vrc: (request) => {
		if (request.action === "project") {
			return { report: "packages: com.vrcfury.vrcfury, com.vrcfury.vrcfury is INSTALLED\nTraceAndOptimize=MISSING" };
		}
		if (request.action === "actions") return { report: "project, avatar, optimizer, actions" };
		return { report: `vrc action=${request.action} ok`, gameObject: request.gameObject };
	},

	world: (request) => {
		if (request.action === "scene") {
			return {
				report: "scene World, worldsSdk=true, udonSharp=true",
				udonBehaviourCount: 177,
				worldsSdk: true,
				udonSharp: true,
			};
		}
		if (request.action === "create-light-volume") {
			// range/intensity come back EXACTLY as sent, fractions included, so a
			// test can catch a side that coerces them to integers.
			return { component: "VRCLightVolumes.PointLightVolume", range: String(request.range), intensity: String(request.intensity), color: "#ffffff" };
		}
		if (request.action === "find-behaviour") {
			return {
				behaviour: request.behaviour,
				found: request.behaviour === "VRC.SDK3.Components.VRCSceneDescriptor",
				resolvedType: "VRC.SDK3.Components.VRCSceneDescriptor",
				assembly: "VRCSDK3",
				gameObject: "VRCWorld",
				searched: request.gameObject ? `"${request.gameObject}" and its children` : "the whole scene",
			};
		}
		if (
			request.action === "quest-add" ||
			request.action === "quest-apply" ||
			request.action === "setup-layers" ||
			request.action === "toggle-tag" ||
			request.action === "create-behaviour" ||
			request.action === "convert-light" ||
			request.action === "audio-link-set" ||
			request.action === "yama-set" ||
			request.action === "hand-menu-set" ||
			request.action === "fix-android-shaders" ||
			request.action === "add-spawn-point" ||
			request.action === "add-collider" ||
			request.action === "add-trigger" ||
			request.action === "add-audio-source" ||
			request.action === "set-clip-load-type"
		) {
			// Mutating actions tag their report with the action, so two in a row
			// are tellable apart without re-reading prose.
			return { action: request.action, report: `world action=${request.action} ok` };
		}
		return { report: `world action=${request.action} ok` };
	},

	// render and capture-view both write the image themselves and report the
	// path back, so the doubles write it: a caller that checked the path existed
	// would otherwise fail against a double that only claimed to.
	render: (request) => {
		writeFileSync(request.outputPath, "fake png\n");
		return { imagePath: request.outputPath, width: request.width, height: request.height, cameraName: request.cameraName ?? "" };
	},

	"capture-view": (request) => {
		writeFileSync(request.outputPath, "fake png\n");
		return { imagePath: request.outputPath, width: request.width, height: request.height, source: "scene view" };
	},

	// inspect and set-property are the pair most likely to be silently wrong:
	// a dropped key here writes an empty string into whatever field the caller
	// named, which looks like a successful edit. The doubles echo every key the
	// wrapper sent, so a payload that loses one fails the assertion below.
	inspect: (request) => {
		if (!request.gameObject) throw new Error("inspect needs a gameObject");
		if (request.gameObject === "Ambiguous") {
			throw new Error('The name "Ambiguous" is not unique in this scene. Pass a scene path instead.');
		}
		return {
			gameObject: request.gameObject,
			component: request.component ?? "",
			includeChildComponents: request.includeChildComponents ?? false,
			transform: { position: "0,0,0", rotation: "0,0,0", scale: "1,1,1" },
			components: [{ type: "Transform" }, { type: request.component ?? "MeshRenderer" }],
			properties: {},
		};
	},

	"set-property": (request) => {
		if (!request.gameObject) throw new Error("set-property needs a gameObject");
		if (!request.component) throw new Error("set-property needs a component");
		if (!request.property) throw new Error("set-property needs a property path");
		// value "" is a legitimate clear for an object reference, so it cannot
		// be rejected here -- but a MISSING value means the wrapper dropped it.
		if (request.value === undefined) {
			throw new Error(`set-property was not told what to write to "${request.property}"`);
		}
		return {
			gameObject: request.gameObject,
			component: request.component,
			property: request.property,
			value: String(request.value),
			space: request.space ?? "scene",
			changed: true,
			dirty: true,
		};
	},

	"set-transform": (request) => {
		if (!request.gameObject) throw new Error("set-transform needs a gameObject");
		if (request.position === undefined && request.rotationEuler === undefined && request.scale === undefined) {
			throw new Error("set-transform was given nothing to change");
		}
		return {
			gameObject: request.gameObject,
			position: request.position ?? "0,0,0",
			rotationEuler: request.rotationEuler ?? "0,0,0",
			scale: request.scale ?? "1,1,1",
			space: request.space ?? "world",
			changed: true,
		};
	},

	"get-bones": (request) => ({
		root: request.gameObject ?? "Avatar",
		// Filtering by the caller's `bones` is the whole point of the filter, so
		// the double honours it: a wrapper that dropped the array would return
		// 180 bones and pass a test that only checked "some bones".
		humanoidBones: Object.fromEntries(
			(request.bones?.length ? request.bones : ["Hips", "Head", "LeftHand"]).map((name) => [
				name,
				{ position: "0,0,0", localPosition: "0,0,0" },
			]),
		),
		rig: Object.fromEntries(
			(request.bones?.length ? request.bones : ["Hips", "Head", "LeftHand"]).map((name) => [
				name,
				{ position: "0,0,0", localPosition: "0,0,0" },
			]),
		),
		truncated: false,
	}),

	"geometry-probe": (request) => ({
		transform: { mirrorScale: false, unappliedScale: false },
		bounds: { state: "exact", size: "1,1,1", center: "0,0,0" },
		objects: (request.objects ?? []).map((name) => ({ name, state: "exact" })),
	}),

	"prefab-info": (request) => ({
		prefabPath: request.prefabPath,
		hierarchy: [{ name: "Root", scenePath: "Root", vertexCount: 8 }],
		materials: [],
		bounds: { state: "at-rest", size: "1,1,1" },
	}),

	"instantiate-prefab": (request) => ({
		instancePath: "Chair (2)/Seat",
		prefabPath: request.prefabPath,
		position: request.position ?? "0,0,0",
		rotation: request.rotation ?? "0,0,0",
		scale: request.scale ?? "1,1,1",
		children: ["Seat", "Back"],
	}),

	// Mirrors the C# handler's contract closely enough to be useful: members come
	// back as REPORTED references, and a member the caller could not resolve is
	// reported as not being one rather than quietly omitted. That asymmetry is
	// what makes `isPrefabOfPrefabs` false rather than the count simply being
	// short, so the fake has to be able to produce it.
	"compose-prefab": (request) => {
		const members = request.members ?? [];
		const reported = members.map((member) => ({
			name: member.name ?? member.prefabPath.split("/").pop().replace(/\.prefab$/, ""),
			path: member.name ?? member.prefabPath,
			// Every third member lands baked instead of referenced, so the test can
			// exercise the partial case as well as the clean one.
			sourceAsset: member.prefabPath.includes("Baked") ? "" : member.prefabPath,
			landedAsReference: member.prefabPath.includes("Baked") ? "NOT a prefab reference" : "",
			localPosition: member.position ?? "0,0,0",
			localScale: member.scale ?? "1,1,1",
		}));
		const nestedCount = reported.filter((member) => member.sourceAsset !== "").length;
		return {
			prefabPath: `${request.destination}/${request.name}.prefab`,
			rootName: request.name,
			memberCount: members.length,
			nestedCount,
			isPrefabOfPrefabs: nestedCount === members.length,
			members: reported,
			bounds: { state: "asset-local renderer bounds, before any placement", size: "2,2,2" },
		};
	},

	"find-source-models": (request) => ({
		models: (request.gameObjectNames ?? []).map((name) => ({ gameObject: name, assetPath: `Assets/Models/${name}.fbx` })),
		unhandoffableNames: [],
		unhandoffableCounts: {},
	}),

	"export-source-models": (request) => ({
		exported: (request.gameObjectNames ?? []).map((name) => ({
			gameObject: name,
			sourcePath: `Assets/Models/${name}.fbx`,
			copiedTo: join(request.outputDir, `${name}.fbx`),
		})),
	}),

	"get-scene-info": () => ({
		objects: [{ name: "Cube", childCount: 0 }],
		materials: [],
		cameras: [{ name: "Main Camera" }],
	}),

	"import-fbx": (request) => ({
		prefabPath: `${request.destination}/${request.name || "transfer"}.prefab`,
		fbxAssetPath: `${request.destination}/transfer.fbx`,
		rootName: request.name || "transfer",
	}),

	// The real bridge writes the file and the transfer layer then verifies it
	// exists, so the double writes it too.
	"export-obj": (request) => {
		writeFileSync(request.outputPath, "# fake obj\nv 0 0 0\n");
		const mtlPath = request.outputPath.replace(/\.obj$/, ".mtl");
		writeFileSync(mtlPath, "# fake mtl\n");
		return {
			objPath: request.outputPath,
			mtlPath,
			meshNames: ["Cube"],
			materialNames: [],
		};
	},
};

let stopping = false;

async function loop() {
	while (!stopping) {
		let pending = [];
		try {
			pending = readdirSync(requestsDir).filter((name) => name.endsWith(".json"));
		} catch {
			pending = [];
		}

		for (const name of pending) {
			const id = name.replace(/\.json$/, "");
			const requestPath = join(requestsDir, name);

			let request;
			try {
				request = JSON.parse(readFileSync(requestPath, "utf8"));
			} catch {
				continue; // partial write; the .tmp+rename publish should prevent this
			}

			// Claim it before doing any work, so a slow handler cannot leave the
			// request visible for the next poll and run twice.
			try {
				unlinkSync(requestPath);
			} catch {
				continue;
			}

			if (state.delayMs > 0) await sleep(state.delayMs);

			const handler = HANDLERS[request.type];
			let body;
			if (!handler) {
				body = { ok: false, error: `Fake editor has no handler for "${request.type}"` };
			} else {
				try {
					body = { ok: true, result: handler(request) };
				} catch (error) {
					body = { ok: false, error: String(error?.message ?? error) };
				}
			}

			try {
				writeFileSync(join(responsesDir, `${id}.json`), JSON.stringify(body));
			} catch {
				// responses dir vanished; nothing useful to do in a test double
			}
		}

		await sleep(50);
	}
}

const controlTimer = setInterval(() => {
	let command;
	try {
		command = JSON.parse(readFileSync(controlPath, "utf8"));
	} catch {
		return;
	}
	Object.assign(state, command);
	// Reset so the harness's next control write is not re-applied forever.
	try {
		writeFileSync(controlPath, "{}");
	} catch {
		// ignore
	}
	// The capture is written out of band, not from a handler: the await path
	// checks the FILE before it asks anything, so a capture that only appeared
	// during the compile-status request would arrive too late to be waited on.
	//
	// Only when the command TURNS IT ON. A real compile writes this file once,
	// when the compiler finishes; re-writing it on every tick would leave its
	// timestamp permanently "just now", which is exactly the thing the await
	// path uses its timestamp to decide, and the fake would then be testing a
	// condition Unity never produces.
	if (command.writeCompileMessages === true) {
		// One-shot, so a later command can turn it on again for a second compile.
		state.writeCompileMessages = false;
		writeCompileMessages();
	} else if (typeof command.captureInMs === "number") {
		// A compile takes time, so the capture lands DURING a wait rather than
		// before it. Writing it eagerly would test a stale file instead -- which
		// the await path is specifically required to refuse.
		//
		// The cycle marker comes first, exactly as the plugin writes it: a
		// compile is recorded the moment it BEGINS, so a wait that settles on
		// any new file would answer while the compiler was still running.
		//
		// Timers from an earlier command are cancelled first. A real compile
		// writes this file once, and a leftover one-shot from the previous test
		// would fire during this one and answer for it.
		for (const pending of captureTimers) clearTimeout(pending);
		captureTimers.length = 0;
		const schedule = (fn, ms) => captureTimers.push(setTimeout(fn, ms));

		const delay = command.captureInMs;
		schedule(() => writeCompileMessages(false, ""), delay);
		if (command.captureNeverFinishes === true) return;
		// A cycle Unity built no assembly for finishes with the compiler's own
		// verdict absent rather than with the file left at its start state:
		// compilationFinished fires for it, and that is the only evidence the
		// wait has.
		schedule(() => writeCompileMessages(true, command.captureAssembly || ""), delay + 200);
	}
}, 100);

function shutdown() {
	stopping = true;
	clearInterval(heartbeatTimer);
	for (const pending of captureTimers) clearTimeout(pending);
	clearInterval(controlTimer);
	process.exit(0);
}

process.on("SIGTERM", shutdown);
process.on("SIGINT", shutdown);

loop().catch((error) => {
	console.error("fake-editor crashed:", error);
	process.exit(1);
});
