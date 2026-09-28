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

export function fileExists(path: string): boolean {
	try {
		return statSync(path).isFile();
	} catch {
		return false;
	}
}

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
}

/** Reads the bridge lock without mutating it. Used by the status tool. */
export function readBridgeLock(): BridgeLockInfo | null {
	const lockPath = join(getUnityBridgeDir(), "bridge.lock");
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
			ageMs: Date.now() - statSync(lockPath).mtimeMs,
		};
	} catch {
		return null;
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

export function countPendingRequests(requestsDir: string): number {
	try {
		return readdirSync(requestsDir).filter((name) => name.endsWith(".json")).length;
	} catch {
		return 0;
	}
}

/** Pull a string out of an unknown record without asserting its shape. */
export function getStringRecordValue(value: unknown, key: string): string | undefined {
	if (value && typeof value === "object" && key in value) {
		const found = (value as Record<string, unknown>)[key];
		if (typeof found === "string") return found;
	}
	return undefined;
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
