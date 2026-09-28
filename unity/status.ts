/**
 * unity_bridge_status.
 *
 * "busy" is a first-class state here, not an error: the live Unity Editor is one
 * shared mutable session, so a worker about to mutate the scene needs to know
 * whether another request is mid-flight. Without this, the model either guesses
 * or burns a 30-120 s timeout waiting on a lock it could have seen coming.
 *
 * This is the one tool that must never be the thing that hangs, so the log
 * lookup below is deliberately conservative: it runs only when the bridge is
 * alive AND idle, is given a short timeout, and its failure is folded into the
 * report rather than thrown. A status call that can block behind the request it
 * is reporting on, or that throws because the Editor died mid-call, would be
 * worse than a status call with no log section at all.
 */

import { getLiveBridgePaths, requestLiveUnityLog } from "./bridge.js";
import { isLiveBridgeAlive } from "../launch.js";
import { countPendingRequests, LIVE_BRIDGE_LOCK_STALE_MS, readBridgeLock } from "./utils.js";

/** Short on purpose: a log path is a nicety, not the reason for the call. */
const LOG_LOOKUP_TIMEOUT_MS = 5_000;

export interface BridgeStatusDeps {
	isAlive?: () => boolean;
	/** Injected in tests; defaults to a real bridge call. */
	readLogs?: () => Promise<UnityLogResult>;
}

export interface UnityLogCandidate {
	path: string;
	role: string;
	exists: boolean;
}

export interface UnityLogResult {
	logs: UnityLogCandidate[];
}

export interface BridgeStatusResult {
	alive: boolean;
	busy: boolean;
	pendingRequests: number;
	lock: ReturnType<typeof readBridgeLock>;
	bridgeDir: string;
	requestsDir: string;
	/** Null when the Editor is down or busy, or the lookup failed. */
	logs: UnityLogResult | null;
	logError: string | null;
	recommendation: string;
}

export async function unityBridgeStatus(deps: BridgeStatusDeps = {}): Promise<BridgeStatusResult> {
	const isAlive = deps.isAlive ?? isLiveBridgeAlive;
	const paths = getLiveBridgePaths();
	const lock = readBridgeLock();
	const pendingRequests = countPendingRequests(paths.requestsDir);

	const busy = lock !== null || pendingRequests > 0;

	// Only when idle: this acquires the bridge lock, so asking while a request is
	// in flight would queue behind the very thing being reported on.
	const readLogs = deps.readLogs ?? defaultReadLogs;
	let logs: UnityLogResult | null = null;
	let logError: string | null = null;
	if (isAlive() && !busy) {
		try {
			logs = await readLogs();
		} catch (error) {
			logError = error instanceof Error ? error.message : String(error);
		}
	}

	let recommendation: string;
	if (!isAlive()) {
		recommendation =
			"The live Unity Editor is not running (stale or missing heartbeat). The next unity_* call will launch it, " +
			"which needs the project to open and the pi bridge plugin to compile — allow up to ~2 minutes on a first run.";
	} else if (busy) {
		recommendation = lock?.unreadable === true
			? `The live Unity Editor is busy: a lock file exists at ${paths.bridgeDir}/bridge.lock but its contents did not ` +
				`parse, so the request holding it cannot be identified. A truncated lock normally means a crashed process; it ` +
				`is reclaimed automatically once it is ${Math.round(LIVE_BRIDGE_LOCK_STALE_MS / 1000)}s old. Do not mutate the scene until it ` +
				`clears.`
			: "The live Unity Editor is busy with another request. Wait for it to finish before mutating the scene; " +
				"concurrent requests interleave in one shared session and the result is undefined.";
	} else if (logError) {
		recommendation =
			`The live Unity Editor is idle, but the log lookup failed: ${logError}. The Editor itself is reachable.`;
	} else {
		recommendation = "The live Unity Editor is idle and ready.";
	}

	return {
		alive: isAlive(),
		busy,
		pendingRequests,
		lock,
		bridgeDir: paths.bridgeDir,
		requestsDir: paths.requestsDir,
		logs,
		logError,
		recommendation,
	};
}

async function defaultReadLogs(): Promise<UnityLogResult> {
	const result = await requestLiveUnityLog({ timeoutMs: LOG_LOOKUP_TIMEOUT_MS });
	return { logs: Array.isArray(result?.logs) ? result.logs : [] };
}
