/**
 * Scratch staging: the two ways a staged file becomes the user's problem.
 *
 * Both findings here are about a file written OUTSIDE the workspace, which is
 * exactly why nothing complained:
 *
 *   Finding J (config.ts) — PI_UNITY_TMP_DIR was unvalidated. Pointed at
 *   <project>/Assets/, every inlineCode call dropped a real .cs into the user's
 *   project: Unity imports it, and the snippet compiles a SECOND time into
 *   Assembly-CSharp on the next refresh. The default cache dir cannot do this,
 *   so the one configuration that causes the damage was the one accepted.
 *
 *   Finding J (workspace.ts) — stageScratchFile wrote and nothing removed it,
 *   so one file accumulated per inlineCode call, forever.
 *
 * The check is structural (a path SEGMENT named Assets) rather than a list of
 * known projects, because a list is incomplete by construction. The refusals
 * are asserted as refusals WITH THEIR REASON: a check that only asserted
 * "throws" would still pass if the guard had been replaced by an unrelated
 * error, and that reason text is the tool's UX.
 */

import assert from "node:assert/strict";
import { mkdirSync, mkdtempSync, readdirSync, rmSync, utimesSync, writeFileSync } from "node:fs";
import { tmpdir } from "node:os";
import { join } from "node:path";

import { JITI } from "./paths.mjs";

const { createJiti } = await import(JITI);
const jiti = createJiti(import.meta.url, { isolated: true, interopDefault: true });

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

const tmpRoot = mkdtempSync(join(tmpdir(), "unity-vrchat-scratch-"));

/**
 * Calls `fn` with PI_UNITY_TMP_DIR set to `dir` and the modules loaded FRESH.
 *
 * Fresh every time on purpose: a cached module would keep the first env value
 * and every later check would be vacuous. `isolated: true` gives each load its
 * own module registry, and `moduleCache: false` defeats jiti's own cache.
 */
async function withTmpDir(dir, fn) {
	const previous = process.env.PI_UNITY_TMP_DIR;
	process.env.PI_UNITY_TMP_DIR = dir;
	try {
		const config = await jiti.import("../config.ts", { default: false });
		const workspace = await jiti.import("../unity/workspace.ts", { default: false });
		return await fn({ config, workspace });
	} finally {
		if (previous === undefined) delete process.env.PI_UNITY_TMP_DIR;
		else process.env.PI_UNITY_TMP_DIR = previous;
	}
}

/** getUnityTempDir()'s refusal text for `dir`, or null when it was accepted. */
async function tmpDirRefusal(dir) {
	let refusal = null;
	try {
		await withTmpDir(dir, async ({ config }) => config.getUnityTempDir());
	} catch (error) {
		refusal = error instanceof Error ? error.message : String(error);
	}
	return refusal;
}

// ---- a refused tmp dir ---------------------------------------------------
{
	const project = join(tmpRoot, "Chiffon");
	const inAssets = join(project, "Assets");
	mkdirSync(inAssets, { recursive: true });

	const refusal = await tmpDirRefusal(inAssets);
	check("a tmp dir inside Assets/ is refused", refusal !== null, "getUnityTempDir() accepted a path inside Assets/");
	check(
		"the refusal names the offending path",
		typeof refusal === "string" && refusal.includes(inAssets),
		String(refusal).slice(0, 200),
	);
	check(
		"the refusal says why (it would compile into the user's Assembly-CSharp)",
		typeof refusal === "string" && refusal.includes("Assembly-CSharp"),
		String(refusal).slice(0, 200),
	);
	check(
		"the refusal says what to do instead",
		typeof refusal === "string" && (refusal.includes("scratch") || refusal.includes("cache")),
		String(refusal).slice(0, 200),
	);
}

// A subfolder of Assets/ is the same hazard, and a path that merely CONTAINS
// the word is not. Both halves are asserted, because a substring test passes the
// first and fails the second -- and a substring test is the obvious wrong
// implementation here.
{
	const refusal = await tmpDirRefusal(join(tmpRoot, "Chiffon", "Assets", "PiImported"));
	check("a tmp dir in a SUBDIRECTORY of Assets/ is refused too", refusal !== null, String(refusal).slice(0, 200));

	const sneaky = await tmpDirRefusal(join(tmpRoot, "Chiffon", "Assets", "..", "Assets", "staging"));
	check("a tmp dir that walks back into Assets/ via .. is refused", sneaky !== null, String(sneaky).slice(0, 200));

	const sibling = await tmpDirRefusal(join(tmpRoot, "Chiffon", "AssetsEvil"));
	check(
		"a directory merely NAMED AssetsEvil is allowed (whole-segment match, not substring)",
		sibling === null,
		String(sibling).slice(0, 200),
	);

	// A real scratch dir outside any project is the normal case and must work.
	const scratch = join(tmpRoot, "scratch");
	let resolved = "";
	let thrown = null;
	try {
		await withTmpDir(scratch, async ({ config }) => {
			resolved = config.getUnityTempDir();
		});
	} catch (error) {
		thrown = error instanceof Error ? error.message : String(error);
	}
	check(
		"a scratch dir outside the project still works",
		thrown === null && resolved === scratch,
		String(thrown).slice(0, 200),
	);
}

// ---- scratch snippets are pruned on a stated bound ------------------------
{
	const scratch = join(tmpRoot, "prune-age");
	mkdirSync(scratch, { recursive: true });

	const old = join(scratch, "script-1-old.cs");
	const fresh = join(scratch, "script-2-fresh.cs");
	writeFileSync(old, "// old\n", "utf8");
	writeFileSync(fresh, "// fresh\n", "utf8");
	writeFileSync(join(scratch, "notes.txt"), "keep me\n", "utf8");
	// Well past the 24 h age bound the implementation states.
	const longAgo = new Date(Date.now() - 48 * 60 * 60 * 1000);
	utimesSync(old, longAgo, longAgo);

	let staged = "";
	await withTmpDir(scratch, async ({ workspace }) => {
		staged = workspace.stageScratchFile("script", "// inline\n");
	});
	check("stageScratchFile still writes the snippet", staged.endsWith(".cs") && staged.startsWith(scratch), staged);

	const remaining = readdirSync(scratch);
	check("an over-age scratch snippet is pruned on the next stage", !remaining.includes("script-1-old.cs"), remaining.join(", "));
	check(
		"a snippet inside the age bound is KEPT (the Editor may not have read it yet)",
		remaining.includes("script-2-fresh.cs"),
		remaining.join(", "),
	);
	check("a non-.cs file in the scratch dir is never touched", remaining.includes("notes.txt"), remaining.join(", "));
	check(
		"the snippet just staged is still there",
		remaining.some((name) => name.startsWith("script-") && name.endsWith(".cs") && name !== "script-1-old.cs" && name !== "script-2-fresh.cs"),
		remaining.join(", "),
	);
}

// ---- the count bound -----------------------------------------------------
{
	const scratch = join(tmpRoot, "prune-count");
	mkdirSync(scratch, { recursive: true });

	// 250 fresh files: past the stated 200-file bound but all inside the age
	// bound, so only the COUNT rule can prune them. Without a count bound the
	// directory grows without limit inside a single long session.
	for (let i = 0; i < 250; i++) {
		writeFileSync(join(scratch, `script-bulk-${String(i).padStart(3, "0")}.cs`), "// bulk\n", "utf8");
	}
	writeFileSync(join(scratch, "notes.txt"), "keep\n", "utf8");

	await withTmpDir(scratch, async ({ workspace }) => {
		workspace.stageScratchFile("script", "// inline\n");
	});

	const remaining = readdirSync(scratch);
	const snippets = remaining.filter((name) => name.endsWith(".cs"));
	check(
		"the count bound prunes a scratch dir that grew past it in one session",
		snippets.length <= 210 && snippets.length > 0,
		`${snippets.length} .cs files left`,
	);
	check("the count bound never deletes a non-.cs file", remaining.includes("notes.txt"), remaining.join(", "));
}

rmSync(tmpRoot, { recursive: true, force: true });

console.log(`\n${passed} passed, ${failures.length} failed`);
if (failures.length > 0) {
	for (const failure of failures) console.log(`  - ${failure}`);
	process.exit(1);
}