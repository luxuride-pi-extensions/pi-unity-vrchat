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

import { readdirSync, readFileSync, unlinkSync, utimesSync, writeFileSync } from "node:fs";
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
const heartbeatTimer = setInterval(touchHeartbeat, 300);

const state = {
	/** Behave like an Editor holding unsaved scene changes. */
	sceneDirty: false,
	/** Stall each handler, so timeout and withdraw-on-timeout can be tested. */
	delayMs: 0,
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
			return { component: "VRCLightVolumes.PointLightVolume", range: "10 m", intensity: "1", color: "#ffffff" };
		}
		return { report: `world action=${request.action} ok` };
	},

	render: () => ({ imagePath: "/tmp/fake-render.png", width: 960, height: 540 }),

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
}, 100);

function shutdown() {
	stopping = true;
	clearInterval(heartbeatTimer);
	clearInterval(controlTimer);
	process.exit(0);
}

process.on("SIGTERM", shutdown);
process.on("SIGINT", shutdown);

loop().catch((error) => {
	console.error("fake-editor crashed:", error);
	process.exit(1);
});
