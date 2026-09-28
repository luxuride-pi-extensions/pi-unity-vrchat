/**
 * Engagement-gating test.
 *
 * Both extensions must stay silent in a fresh session and speak only after the
 * user engages (a slash command, or any of their tools). This asserts that
 * behaviourally rather than by inspection, because a gate that looks right but
 * still returns a message is exactly the bug this guards.
 */

import { join } from "node:path";

import { INSTALLED_EXTENSIONS, JITI } from "./paths.mjs";

// jiti lives in the pi install, not in this project, so it is imported through an
// absolute resolved path rather than a bare specifier. See test/paths.mjs.
const { createJiti } = await import(JITI);

const jiti = createJiti(import.meta.url, { interopDefault: true, moduleCache: false });

// Built from INSTALLED_EXTENSIONS rather than hardcoded, so this test still
// points at the real symlinks after the extensions move. If a name is missing
// the failure names the path it looked at, instead of a module-not-found.
const EXTENSIONS = [
	{
		file: join(INSTALLED_EXTENSIONS, "vibe-unity", "index.ts"),
		command: "unity",
		tool: "unity_bridge_status",
		label: "pi-vibe-unity",
		task: "add a cube to the scene",
	},
	{
		file: join(INSTALLED_EXTENSIONS, "unity-blender", "index.ts"),
		command: "unity-blender",
		tool: "unity_blender_status",
		label: "pi-unity-blender",
		task: "move the selected meshes into Assets/Art",
	},
];

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

/** A command context with the bits the extensions actually touch. */
function commandContext() {
	return {
		isIdle: () => true,
		ui: { notify: () => {} },
		sendUserMessage: () => {},
	};
}

/** Load an extension and capture the handlers it registers. */
async function loadExtension(spec) {
	const tools = [];
	const commands = new Map();
	const events = [];

	const pi = {
		registerTool: (tool) => tools.push(tool),
		registerCommand: (name, options) => commands.set(name, options),
		registerShortcut: () => {},
		registerFlag: () => {},
		on: (event, handler) => events.push({ event, handler }),
		sendUserMessage: () => {},
	};

	const mod = await jiti.import(spec.file, { default: true });
	await (mod.default ?? mod)(pi);

	const find = (name) => events.find((e) => e.event === name)?.handler;
	return { tools, commands, beforeAgentStart: find("before_agent_start"), sessionStart: find("session_start") };
}

// pi-vibe-unity's before_agent_start returns a Promise (it awaits a live
// context fetch), while the binder's returns a plain object. Normalise both.
async function injected(pi) {
	const result = pi.beforeAgentStart({}, {});
	return (await result) ?? undefined;
}

for (const spec of EXTENSIONS) {
	console.log(spec.label);
	const pi = await loadExtension(spec);
	const { tools, commands, sessionStart } = pi;

	// Registration is the precondition for every behaviour below.
	check(`registers /${spec.command}`, commands.has(spec.command));
	check(`registers ${spec.tool}`, tools.some((t) => t.name === spec.tool));
	check("has a before_agent_start handler", typeof pi.beforeAgentStart === "function");
	check("has a session_start handler", typeof sessionStart === "function");

	// A fresh session injects nothing, and stays quiet across idle turns.
	sessionStart?.();
	check("fresh session: no injection", (await injected(pi)) === undefined);
	check("idle turn: still no injection", (await injected(pi)) === undefined);

	// The slash command turns it on. Both take a task argument: /unity with no
	// argument just prints usage and dispatches nothing, so there is no turn to
	// inject into and the flag stays unset -- which is why this passes a task.
	await commands.get(spec.command)?.handler(spec.task, commandContext());
	const afterCommand = await injected(pi);
	check("after slash command: injects", afterCommand?.message !== undefined);
	check(
		"injected guidance is substantial",
		typeof afterCommand?.message?.content === "string" && afterCommand.message.content.length > 50,
		`${afterCommand?.message?.content?.length ?? 0} chars`,
	);

	// A new session silences it again -- that is the "not in fresh session" part.
	sessionStart?.();
	check("new session: silent again", (await injected(pi)) === undefined);

	// A tool call also engages, and engagement survives idle turns after that.
	const tool = tools.find((t) => t.name === spec.tool);
	try {
		await tool?.execute({}, undefined);
	} catch {
		// A status call may legitimately fail with no editor running. The
		// engagement flag is set before any work, so a throw is fine here.
	}
	check(`after ${spec.tool}: injects`, (await injected(pi))?.message !== undefined);
	check("stays engaged on later turns", (await injected(pi))?.message !== undefined);
}

console.log(`\n${passed} passed, ${failures.length} failed`);
if (failures.length > 0) {
	for (const failure of failures) console.log(`  - ${failure}`);
	process.exit(1);
}
