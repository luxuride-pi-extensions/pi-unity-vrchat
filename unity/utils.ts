/**
 * Shared helpers: the cross-process bridge lock, small path/record utilities,
 * and the render-output name normalizer.
 *
 * The bridge lock is separate from the manifest lock on purpose. The manifest
 * lock makes read-modify-write atomic and is never held across real work. The
 * bridge lock exists because the live Unity Editor is ONE mutable session:
 * concurrent requests would interleave their mutations with no ordering
 * guarantee, so exactly one request may be in flight at a time.
 */

import { readFileSync, readdirSync, statSync, writeFileSync, rmSync } from "node:fs";
import { join } from "node:path";

import { getUnityBridgeDir } from "../config.js";

export const LIVE_BRIDGE_TIMEOUT_MS = 30_000;
export const LIVE_BRIDGE_COLD_START_TIMEOUT_MS = 120_000;
export const LIVE_BRIDGE_INIT_TIMEOUT_MS = 5_000;
export const LIVE_BRIDGE_POLL_MS = 200;
/** A lock older than this belongs to a crashed process, not a live request. */
export const LIVE_BRIDGE_LOCK_STALE_MS = 600_000;

export function delay(ms: number, signal?: AbortSignal): Promise<void> {
	return new Promise((resolvePromise, rejectPromise) => {
		if (signal?.aborted) {
			rejectPromise(new Error("Aborted before delay started."));
			return;
		}
		const onAbort = (): void => {
			clearTimeout(timer);
			rejectPromise(new Error("Aborted."));
		};
		const timer = setTimeout(() => {
			signal?.removeEventListener("abort", onAbort);
			resolvePromise();
		}, ms);
		signal?.addEventListener("abort", onAbort, { once: true });
	});
}

export interface BridgeLockInfo {
	pid: number;
	label: string;
	acquiredAt: string;
	ageMs: number;
	/** The lock file exists but its contents did not parse. See readBridgeLock. */
	unreadable: boolean;
}

/**
 * Reads the bridge lock without mutating it. Used by the status tool.
 *
 * A lock file that exists but does not parse is still a HELD lock, so it must
 * not collapse to null: the status tool derives "busy" from this, and reporting
 * an idle Editor while a request is in flight is exactly the wrong answer — the
 * caller would then mutate the scene concurrently and get undefined behaviour.
 * The lock is reclaimed by acquireBridgeLock's stale check once it ages out, so
 * all this can safely do is report it honestly with unreadable: true.
 */
export function readBridgeLock(): BridgeLockInfo | null {
	const lockPath = join(getUnityBridgeDir(), "bridge.lock");
	let mtimeMs: number;
	try {
		mtimeMs = statSync(lockPath).mtimeMs;
	} catch {
		return null;
	}
	const ageMs = Date.now() - mtimeMs;
	try {
		const raw = JSON.parse(readFileSync(lockPath, "utf8")) as {
			pid?: number;
			label?: string;
			acquiredAt?: string;
		};
		return {
			pid: raw.pid ?? 0,
			label: raw.label ?? "",
			acquiredAt: raw.acquiredAt ?? "",
			ageMs,
			unreadable: false,
		};
	} catch {
		return {
			pid: 0,
			label: "",
			acquiredAt: "",
			ageMs,
			unreadable: true,
		};
	}
}

export interface BridgeLock {
	release: () => void;
	waitedMs: number;
}

export async function acquireBridgeLock(options?: {
	signal?: AbortSignal;
	timeoutMs?: number;
	label?: string;
}): Promise<BridgeLock> {
	const bridgeDir = getUnityBridgeDir();
	const lockPath = join(bridgeDir, "bridge.lock");
	const started = Date.now();
	const deadline = started + (options?.timeoutMs ?? LIVE_BRIDGE_LOCK_STALE_MS);

	for (;;) {
		try {
			writeFileSync(
				lockPath,
				JSON.stringify({ pid: process.pid, label: options?.label ?? "", acquiredAt: new Date().toISOString() }),
				{ flag: "wx" },
			);
			return {
				release: () => {
					try {
						rmSync(lockPath, { force: true });
					} catch {
						/* already released */
					}
				},
				waitedMs: Date.now() - started,
			};
		} catch (error) {
			if ((error as NodeJS.ErrnoException).code !== "EEXIST") throw error;
			try {
				const age = Date.now() - statSync(lockPath).mtimeMs;
				if (age > LIVE_BRIDGE_LOCK_STALE_MS) {
					rmSync(lockPath, { force: true });
					continue;
				}
			} catch {
				continue;
			}
			if (Date.now() > deadline) {
				throw new Error(
					`Another Unity bridge request is still running (lock: ${lockPath}). ` +
						`The live Unity Editor serializes requests; retry when it finishes.`,
				);
			}
			await delay(25, options?.signal);
		}
	}
}

/**
 * How many requests are queued and not yet answered. The status tool reads this
 * so a worker can see a request coming before it spends a timeout on the lock.
 */
export function countPendingRequests(requestsDir: string): number {
	try {
		return readdirSync(requestsDir).filter((name) => name.endsWith(".json")).length;
	} catch {
		return 0;
	}
}

export function normalizeRenderOutputName(name: string | undefined, extension: string): string {
	const trimmed = (name ?? "").trim();
	if (!trimmed) return `render${extension}`;
	if (trimmed.includes("/") || trimmed.includes("\\") || trimmed.includes("..")) {
		// Renders belong in the iteration's renders/ dir; a caller-supplied
		// "../../etc/passwd" must never be able to escape it.
		throw new Error(`Render output name must be a plain file name, got: ${name}`);
	}
	return trimmed.toLowerCase().endsWith(extension) ? trimmed : `${trimmed}${extension}`;
}
