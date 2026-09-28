/**
 * Minimal promise wrapper around child_process.spawn.
 *
 * shell:false everywhere: Unity paths and Hub wrapper argv must not go through
 * a shell, and the Blender extension's exec already established this contract.
 */

import { spawn } from "node:child_process";

export interface ExecOptions {
	signal?: AbortSignal;
	timeout?: number;
	cwd?: string;
}

export interface ExecResult {
	stdout: string;
	stderr: string;
	code: number;
	killed: boolean;
}

export async function execCommand(
	command: string,
	args: string[],
	cwd: string,
	options?: ExecOptions,
): Promise<ExecResult> {
	return new Promise<ExecResult>((resolvePromise, rejectPromise) => {
		if (options?.signal?.aborted) {
			rejectPromise(new Error("Command aborted before it started."));
			return;
		}

		const child = spawn(command, args, {
			cwd,
			shell: false,
			stdio: ["ignore", "pipe", "pipe"],
		});

		let stdout = "";
		let stderr = "";
		let settled = false;
		let killed = false;
		let spawnError: Error | undefined;
		let timer: NodeJS.Timeout | undefined;
		let forceTimer: NodeJS.Timeout | undefined;

		const onAbort = (): void => {
			killed = true;
			child.kill("SIGTERM");
		};
		options?.signal?.addEventListener("abort", onAbort, { once: true });

		const cleanup = (): void => {
			if (timer) clearTimeout(timer);
			if (forceTimer) clearTimeout(forceTimer);
			options?.signal?.removeEventListener("abort", onAbort);
		};

		const settle = (code: number): void => {
			if (settled) return;
			settled = true;
			cleanup();
			// 127 is the conventional "command not found"; reporting success
			// for a spawn ENOENT is how the Blender extension used to hide
			// missing binaries until a later step failed confusingly.
			resolvePromise({ stdout, stderr, code: spawnError ? 127 : code, killed });
		};

		child.stdout?.on("data", (chunk: Buffer) => {
			stdout += chunk.toString();
		});
		child.stderr?.on("data", (chunk: Buffer) => {
			stderr += chunk.toString();
		});
		child.on("error", (error: Error) => {
			spawnError = error;
			stderr += error.message;
		});
		child.on("close", (code) => {
			killed = killed || code === null;
			settle(code ?? 0);
		});

		if (options?.timeout) {
			timer = setTimeout(() => {
				killed = true;
				child.kill("SIGTERM");
				forceTimer = setTimeout(() => child.kill("SIGKILL"), 5_000);
				forceTimer.unref?.();
			}, options.timeout);
			timer.unref?.();
		}
	});
}
