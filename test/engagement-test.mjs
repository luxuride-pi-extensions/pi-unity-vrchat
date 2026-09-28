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

	const findAll = (name) => events.filter((e) => e.event === name).map((e) => e.handler);
	return { tools, commands, beforeAgentStarts: findAll("before_agent_start"), sessionStart: findAll("session_start")[0] };
}

/**
 * Run every before_agent_start handler against one realistic event, and report
 * the first message any of them returned.
 *
 * Two details the previous version got wrong, both of which hid a real crash:
 *
 *   - It passed `{}` as the event. A handler that injects a system-prompt
 *     section writes to `event.systemPromptOptions.sections`, so the bare
 *     object made that a TypeError instead of an assertion failure. The event
 *     now carries the mutable options and the systemPrompt string the handlers
 *     actually read, and the sections a handler writes are returned alongside
 *     the message so the injection is observable.
 *
 *   - It kept only the FIRST handler for an event name. pi dispatches to every
 *     registered handler in order, and these extensions register more than one
 *     (the binder injects its per-turn context message and, separately, its
 *     manager prompt), so taking the first tested a handler that may not be the
 *     one under change and skipped the others entirely.
 *
 * pi-vibe-unity's handler returns a Promise (it awaits a live context fetch),
 * while the binder's returns a plain object, so the results are awaited
 * together. Later handlers must observe earlier mutations, so the event is
 * shared rather than rebuilt per handler.
 */
async function injected(pi) {
	const event = { systemPrompt: "", systemPromptOptions: { sections: {} } };
	let message;
	for (const handler of pi.beforeAgentStarts) {
		const result = await handler(event, { getSystemPrompt: () => event.systemPrompt });
		if (result?.message !== undefined && message === undefined) message = result.message;
	}
	return { message, sections: event.systemPromptOptions.sections };
}

for (const spec of EXTENSIONS) {
	console.log(spec.label);
	const pi = await loadExtension(spec);
	const { tools, commands, sessionStart } = pi;

	// Registration is the precondition for every behaviour below.
	check(`registers /${spec.command}`, commands.has(spec.command));
	check(`registers ${spec.tool}`, tools.some((t) => t.name === spec.tool));
	check("has a before_agent_start handler", pi.beforeAgentStarts.length > 0);
	check("has a session_start handler", typeof sessionStart === "function");

	// Silence means BOTH halves quiet: no returned message AND no system-prompt
	// section written. A handler that injects a section returns undefined, so
	// testing only the message would report a fresh session as silent while it
	// was in fact being handed a manager prompt.
	const silentResult = async (pi) => {
		const { message, sections } = await injected(pi);
		return message === undefined && Object.keys(sections).length === 0;
	};

	// A fresh session injects nothing, and stays quiet across idle turns.
	sessionStart?.();
	check("fresh session: no injection", await silentResult(pi));
	check("idle turn: still no injection", await silentResult(pi));

	// The slash command turns it on. Both take a task argument: /unity with no
	// argument just prints usage and dispatches nothing, so there is no turn to
	// inject into and the flag stays unset -- which is why this passes a task.
	await commands.get(spec.command)?.handler(spec.task, commandContext());
	const afterCommand = await injected(pi);
	check("after slash command: injects", afterCommand.message !== undefined);
	check(
		"injected guidance is substantial",
		typeof afterCommand.message?.content === "string" && afterCommand.message.content.length > 50,
		`${afterCommand.message?.content?.length ?? 0} chars`,
	);
	// The manager prompt travels as a system-prompt section, not as a message:
	// it is constant, so pi diffs it per run and never re-sends it on steady
	// turns. Asserting only the message would let the prompt go missing without
	// a single failure.
	const sectionNames = Object.keys(afterCommand.sections);
	check("after slash command: injects a system-prompt section", sectionNames.length > 0, sectionNames.join(", "));
	check(
		"injected section is substantial",
		sectionNames.some((name) => typeof afterCommand.sections[name] === "string" && afterCommand.sections[name].length > 50),
		sectionNames.map((name) => `${name}=${afterCommand.sections[name]?.length ?? 0}`).join(", "),
	);

	// A new session silences it again -- that is the "not in fresh session" part.
	sessionStart?.();
	check("new session: silent again", await silentResult(pi));

	// EVERY tool must engage, not just one. Checking a single tool is what let
	// unity_log_critique ship without markUnityEngaged(): 18 of 19 tools engaged
	// and the suite was green, because the tool it happened to exercise was one
	// of the 18. So each registered tool is called in a FRESH session and must be
	// the thing that turns injection on.
	const silent = [];
	for (const tool of tools) {
		sessionStart?.();
		try {
			await tool.execute({}, undefined);
		} catch {
			// A tool may legitimately fail with no editor or workspace; the
			// engagement flag is set before any work, so a throw is fine.
		}
		if (await silentResult(pi)) continue;
		const engaged = await injected(pi);
		if (engaged.message === undefined) silent.push(tool.name);
	}
	check(
		`every one of ${tools.length} tools engages the session`,
		silent.length === 0,
		silent.join(", "),
	);

	// Engagement survives idle turns once any tool has fired.
	check("stays engaged on later turns", (await silentResult(pi)) === false);
}

console.log(`\n${passed} passed, ${failures.length} failed`);
if (failures.length > 0) {
	for (const failure of failures) console.log(`  - ${failure}`);
	process.exit(1);
}
