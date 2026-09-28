/**
 * Host paths the test harnesses need, resolved instead of hardcoded.
 *
 * These two paths used to be literals in the harness files, which meant the
 * tests only ran on the machine that wrote them -- and, worse, that a moved
 * checkout failed with a module-not-found error that looks like a broken
 * extension rather than a stale path.
 *
 * Overridable, because the "obvious" location is not always the real one:
 *   PI_HOME          defaults to ~/.pi
 *   PI_AGENT_DIR     defaults to $PI_HOME/agent
 *   PI_INSTALL_DIR   defaults to $PI_AGENT_DIR/install
 *
 * The jiti path is resolved by picking the NEWEST release directory rather than
 * a pinned version, so upgrading pi does not silently break the suite. If that
 * ever resolves to something without jiti, the loader below says so by name
 * instead of failing as an unresolved module.
 */

import { existsSync, readdirSync } from "node:fs";
import { homedir } from "node:os";
import { join } from "node:path";

export const PI_HOME = process.env.PI_HOME ?? join(homedir(), ".pi");
export const PI_AGENT_DIR = process.env.PI_AGENT_DIR ?? join(PI_HOME, "agent");
export const PI_INSTALL_DIR = process.env.PI_INSTALL_DIR ?? join(PI_AGENT_DIR, "install");

/** Newest pi release directory by name (versions sort correctly as strings). */
export function newestPiRelease() {
	const releases = join(PI_INSTALL_DIR, "releases");
	if (!existsSync(releases)) {
		throw new Error(
			`No pi install directory at ${releases}. Set PI_INSTALL_DIR, or PI_HOME if your pi ` +
				`directory is not at ~/.pi.`,
		);
	}
	const names = readdirSync(releases).filter((name) => existsSync(join(releases, name, "node_modules")));
	if (names.length === 0) {
		throw new Error(`No pi release under ${releases} contains a node_modules directory.`);
	}
	names.sort();
	const newest = names[names.length - 1];
	return join(releases, newest);
}

function resolveJiti() {
	const explicit = process.env.PI_JITI_PATH;
	if (explicit) {
		if (!existsSync(explicit)) throw new Error(`PI_JITI_PATH is set to ${explicit}, which does not exist.`);
		return explicit;
	}
	// The harness is loaded through jiti, so jiti is either next to the release we
	// just found or one directory up from this repo's node_modules.
	const candidates = [
		join(newestPiRelease(), "node_modules", "jiti", "lib", "jiti.mjs"),
		join(import.meta.dirname, "..", "node_modules", "jiti", "lib", "jiti.mjs"),
	];
	const found = candidates.find((path) => existsSync(path));
	if (!found) {
		throw new Error(
			`Could not find jiti. Looked in:\n  ${candidates.join("\n  ")}\nSet PI_JITI_PATH to point ` +
				`at it explicitly. The harnesses cannot load TypeScript without it (node --strip-types ` +
				`cannot resolve this project's NodeNext ".js" specifiers).`,
		);
	}
	return found;
}

export const JITI = resolveJiti();

/**
 * Where the extensions are INSTALLED, which is deliberately not where they are
 * developed. The engagement test runs against the installed symlinks on
 * purpose: it asserts what a real session loads, not what the source tree says.
 */
export const INSTALLED_EXTENSIONS = join(PI_AGENT_DIR, "extensions");
