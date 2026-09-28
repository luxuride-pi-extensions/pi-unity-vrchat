/**
 * unity_bridge_status.
 *
 * "busy" is a first-class state here, not an error: the live Unity Editor is one
 * shared mutable session, so a worker about to mutate the scene needs to know
 * whether another request is mid-flight. Without this, the model either guesses
 * or burns a 30-120 s timeout waiting on a lock it could have seen coming.
 */

import { readdirSync } from "node:fs";
import { join } from "node:path";

import { getLiveBridgePaths } from "./bridge.js";
import { getUnityBridgeDir } from "../config.js";
import { isLiveBridgeAlive } from "../launch.js";
import { readBridgeLock } from "./utils.js";

export interface BridgeStatusDeps {
	isAlive?: () => boolean;
}

export interface BridgeStatusResult {
	alive: boolean;
	busy: boolean;
	pendingRequests: number;
	lock: ReturnType<typeof readBridgeLock>;
	bridgeDir: string;
	requestsDir: string;
	recommendation: string;
}

export function unityBridgeStatus(deps: BridgeStatusDeps = {}): BridgeStatusResult {
	const isAlive = deps.isAlive ?? isLiveBridgeAlive;
	const paths = getLiveBridgePaths();
	const lock = readBridgeLock();

	let pendingRequests = 0;
	try {
		pendingRequests = readdirSync(join(getUnityBridgeDir(), "requests")).filter((name) =>
			name.endsWith(".json"),
		).length;
	} catch {
		pendingRequests = 0;
	}

	const busy = lock !== null || pendingRequests > 0;

	let recommendation: string;
	if (!isAlive()) {
		recommendation =
			"The live Unity Editor is not running (stale or missing heartbeat). The next unity_* call will launch it, " +
			"which needs the project to open and the pi bridge plugin to compile — allow up to ~2 minutes on a first run.";
	} else if (busy) {
		recommendation =
			"The live Unity Editor is busy with another request. Wait for it to finish before mutating the scene; " +
			"concurrent requests interleave in one shared session and the result is undefined.";
	} else {
		recommendation = "The live Unity Editor is idle and ready.";
	}

	return {
		alive: isAlive(),
		busy,
		pendingRequests,
		lock,
		bridgeDir: getUnityBridgeDir(),
		requestsDir: paths.requestsDir,
		recommendation,
	};
}
