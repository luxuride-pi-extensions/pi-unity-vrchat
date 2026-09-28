/**
 * Documentation/registration consistency.
 *
 * This exists because of a real defect it would have caught: `unity_capture_view`
 * was implemented, handled by the C# dispatch, listed in the README, AND named
 * in the guidance injected before every engaged turn -- but was never passed to
 * pi.registerTool. The agent was being told, authoritatively, to call a tool
 * that did not exist. Nothing failed: tsc was clean, the tests were green, and
 * the docs were all correct about a tool that was simply not there.
 *
 * A count cannot catch that (the total was right by coincidence). The SETS have
 * to be compared, in both directions:
 *   - documented but not registered: advice the agent cannot follow
 *   - registered but not documented: a tool nobody knows to reach for
 *
 * The guidance scan reads index.ts as text because UNITY_WORKFLOW_GUIDANCE is a
 * module-level string literal with no export. A tool name is only counted when
 * it is a whole word, so a tool name appearing inside another tool's name does
 * not register as a mention.
 */

import { readFileSync } from "node:fs";
import { dirname, join } from "node:path";
import { fileURLToPath } from "node:url";

import { INSTALLED_EXTENSIONS, JITI } from "./paths.mjs";

const HERE = dirname(fileURLToPath(import.meta.url));
const REPO = join(HERE, "..");
const INDEX = join(REPO, "index.ts");
const README = join(REPO, "README.md");

const { createJiti } = await import(JITI);
const jiti = createJiti(import.meta.url, { interopDefault: true, moduleCache: false });

let passed = 0;
const failures = [];

function check(name, ok, detail = "") {
	if (ok) {
		passed += 1;
		console.log(`  ok   ${name}`);
	} else {
		failures.push(`${name}${detail ? ` — ${detail}` : ""}`);
		console.log(`  FAIL ${name}${detail ? ` — ${detail}` : ""}`);
	}
}

const tools = [];
const pi = {
	registerTool: (tool) => tools.push(tool.name),
	registerCommand: () => {},
	on: () => {},
	sendUserMessage: () => {},
};

const extension = await jiti.import(join(INSTALLED_EXTENSIONS, "vibe-unity", "index.ts"));
await extension.default(pi);

const registered = new Set(tools);
const source = readFileSync(INDEX, "utf8");
const readme = readFileSync(README, "utf8");

// --- README table vs what is actually registered -------------------------
const documented = new Set([...readme.matchAll(/^\| `(unity_[a-z_]+)`/gm)].map((m) => m[1]));

const documentedNotRegistered = [...documented].filter((name) => !registered.has(name));
check(
	"every documented tool is actually registered",
	documentedNotRegistered.length === 0,
	`documented but missing from registerTool: ${documentedNotRegistered.join(", ")}`,
);

const registeredNotDocumented = [...registered].filter((name) => !documented.has(name));
check(
	"every registered tool is documented",
	registeredNotDocumented.length === 0,
	`registered but absent from the README table: ${registeredNotDocumented.join(", ")}`,
);

// --- the manager prompt vs what is actually registered -------------------
// There is no separate guidance blob any more: the /unity command session IS
// the manager, and its entire prompt is unity-manager.md, read at runtime by
// getManagerPrompt() in config.ts. So the manager file is now the only place
// that can advise a tool, and it is the only place this needs to scan -- which
// is also why a tool named there but not registered is now reachable at all.
const MANAGER = join(REPO, "unity", "agents", "unity-manager.md");
const managerSource = readFileSync(MANAGER, "utf8");
check("found unity-manager.md", managerSource.length > 0);
check("index.ts injects the manager prompt", source.includes("getManagerPrompt()"));

// Whole-word only: \b would split on the underscore, so exclude a longer tool
// name that merely starts with the one being matched.
const mentioned = new Set([...managerSource.matchAll(/(?<![\w])unity_[a-z_]+/g)].map((m) => m[0]));
const advisedButMissing = [...mentioned].filter((name) => !registered.has(name));
check(
	"every tool named in the manager prompt is registered",
	advisedButMissing.length === 0,
	`the prompt advises calling: ${advisedButMissing.join(", ")}`,
);
check("the prompt actually names some tools", mentioned.size > 0, `found ${mentioned.size}`);

// --- the render/capture distinction survives -----------------------------
// It is the most consequential instruction in the prompt, and a tool that does
// not exist makes the instruction impossible to follow. It lives in the
// manager, which is the session that briefs a worker to render.
check(
	"unity-manager distinguishes unity_render from unity_capture_view",
	managerSource.includes("unity_render") && managerSource.includes("unity_capture_view"),
);
check(
	"both render tools are registered",
	registered.has("unity_render") && registered.has("unity_capture_view"),
);

console.log(`\n${passed} passed, ${failures.length} failed  (${registered.size} tools registered)`);
if (failures.length > 0) process.exitCode = 1;
