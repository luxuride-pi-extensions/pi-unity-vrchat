/**
 * Agent template tool/provider consistency.
 *
 * The defect this exists for: `unity-manager` and `unity-blender-manager` both
 * named `todo` in their `tools:` list, and both `extensions:` lines listed only
 * their own extension. A foreground child does NOT load ambient parent
 * extensions, so a tool named in `tools:` that no listed extension registers is
 * requested and never provided -- the agent is handed a `todo` it can never
 * call, and the failure looks like the agent ignoring its own instructions.
 * blender-manager had already documented this in its own frontmatter, and both
 * new managers shipped without it. Nothing failed: the files were valid, the
 * installers ran clean, and the agent would have degraded silently at runtime.
 *
 * So the check is: every tool a template names must be provided by an extension
 * that template actually lists.
 *
 * HONEST LIMITS, because a test that overstates what it proves is worse than no
 * test:
 *  - Tools that come from pi-subagents or the todo package (`subagent`,
 *    `contact_supervisor`, `subagents_enable`, `todo`) are not registered by
 *    either extension we can load, so they cannot be verified by SET membership.
 *    They are whitelisted as AMBIENT, and `todo` additionally gets a dedicated
 *    structural check, because that is the one whose provider is a host path
 *    that silently goes missing.
 *  - This reads the TEMPLATES, which carry the `__PI_AGENT_EXTENSION_DIR__`
 *    placeholder. It verifies what the installer is asked to produce, not the
 *    installed result; `test/install-agents.mjs` covers the substitution.
 */

import { existsSync, readFileSync, readdirSync } from "node:fs";
import { join } from "node:path";

import { INSTALLED_EXTENSIONS, JITI } from "./paths.mjs";

/** Tools that come from pi-subagents, the todo package, or pi's own built-ins. */
const AMBIENT_TOOLS = new Set([
	"subagent",
	"contact_supervisor",
	"subagents_enable",
	"todo",
	"read",
	"write",
	"edit",
	"grep",
	"find",
	"ls",
]);

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

/** Tools each installed extension registers, keyed by its directory name. */
async function toolsOf(extensionName) {
	const tools = [];
	const pi = {
		registerTool: (tool) => tools.push(tool.name),
		registerCommand: () => {},
		on: () => {},
		sendUserMessage: () => {},
	};
	const indexPath = join(INSTALLED_EXTENSIONS, extensionName, "index.ts");
	const extension = await jiti.import(indexPath);
	await extension.default(pi);
	return new Set(tools);
}

function frontmatter(source, key) {
	const line = new RegExp(`^${key}:(.*)$`, "m").exec(source);
	if (!line) return null;
	return line[1]
		.split(",")
		.map((entry) => entry.trim())
		.filter(Boolean);
}

const EXTENSION_TEMPLATES = {
	"vibe-unity": "unity/agents",
	"unity-blender": "agents",
};

/** Every agent template across both repos, as {name, extensionName, tools, extensions}. */
function collectTemplates() {
	const templates = [];
	for (const [extensionName, relative] of Object.entries(EXTENSION_TEMPLATES)) {
		const agentsDir = join(INSTALLED_EXTENSIONS, extensionName, relative);
		if (!existsSync(agentsDir)) {
			failures.push(`no template dir for ${extensionName} at ${agentsDir}`);
			continue;
		}
		for (const file of readdirSync(agentsDir).sort()) {
			if (!file.endsWith(".md")) continue;
			const source = readFileSync(join(agentsDir, file), "utf8");
			templates.push({
				name: file.replace(/\.md$/, ""),
				extensionName,
				tools: frontmatter(source, "tools") ?? [],
				extensions: frontmatter(source, "extensions") ?? [],
				source,
			});
		}
	}
	return templates;
}

const providers = new Map([
	["vibe-unity", await toolsOf("vibe-unity")],
	["unity-blender", await toolsOf("unity-blender")],
]);
const allProviderTools = new Set([...providers.values()].flatMap((set) => [...set]));
const templates = collectTemplates();

check("found agent templates", templates.length > 0, `found ${templates.length}`);
check(
	"both extension dirs were readable",
	providers.size === 2,
	`read ${[...providers.keys()].join(", ")}`,
);

// --- every named tool is provided by an extension the template lists -----
// Only checked for tools some extension here registers; ambient tools are
// whitelisted above and cannot be resolved to a provider by this test.
const unresolvable = [];
const wronglyProvided = [];

for (const template of templates) {
	// The template's OWN extension is always listed after the installer stamps
	// it, but in the committed template that entry is still the placeholder --
	// one placeholder per template, so it names exactly one extension and cannot
	// be attributed. Fall back to the repo the template lives in, which is what
	// the installer resolves the placeholder to.
	const listedDirs = new Set(
		template.extensions
			.flatMap((entry) => entry.split(",").map((part) => part.trim()))
			.flatMap((entry) =>
				entry.includes("__PI_AGENT_EXTENSION_DIR__")
					? [template.extensionName]
					: [entry.replace(/\/index\.ts$/, "").split("/").filter(Boolean).pop() ?? ""],
			),
	);

	for (const tool of template.tools) {
		if (AMBIENT_TOOLS.has(tool)) continue;
		if (!allProviderTools.has(tool)) {
			unresolvable.push(`${template.name}: ${tool}`);
			continue;
		}
		const providing = [...providers.entries()].filter(([, set]) => set.has(tool));
		const ok = providing.some(([dir]) => listedDirs.has(dir));
		if (!ok) {
			wronglyProvided.push(
				`${template.name}: ${tool} (provided by ${providing.map(([d]) => d).join("/")}, ` +
					`but extensions: names ${[...listedDirs].join("/") || "nothing"})`,
			);
		}
	}
}

check(
	"every named tool that an extension provides is provided by one this template lists",
	wronglyProvided.length === 0,
	wronglyProvided.join("; "),
);
check(
	"every named tool is either ambient or registered by a known extension",
	unresolvable.length === 0,
	`unresolvable (neither ambient nor registered): ${unresolvable.join("; ")}`,
);

// --- the todo provider, structurally ------------------------------------
// `todo` is the one ambient tool whose provider is a HOST PATH, so it is the
// one that goes missing without an error. It must never be served by the
// extension dirs, and a template naming it must not list only its own
// extension.
const todoTemplates = templates.filter((template) => template.tools.includes("todo"));
check("at least one template names todo", todoTemplates.length > 0, `found ${todoTemplates.length}`);

const todoBad = [];
for (const template of todoTemplates) {
	const hostsItsProvider = template.extensions.some(
		(entry) =>
			entry.includes("rpiv-todo") || entry.includes("todo") || entry.includes("PI_TODO"),
	);
	if (!hostsItsProvider) todoBad.push(template.name);
}
check(
	"a template naming todo also names a todo provider on its extensions: line",
	todoBad.length === 0,
	`missing provider: ${todoBad.join(", ")}`,
);

// --- ambient tools on a template that does not name them -----------------
// The reverse drift: adding a provider for a tool the template never calls
// widens the agent's toolset for nothing.
const unusedAmbient = templates.filter((template) =>
	template.extensions.some((entry) => entry.includes("rpiv-todo") || entry.includes("PI_TODO")),
).filter((template) => !template.tools.includes("todo"));
check(
	"no template lists a todo provider without using todo",
	unusedAmbient.length === 0,
	`provider listed but todo unused: ${unusedAmbient.map((t) => t.name).join(", ")}`,
);

// --- templates keep their placeholder ------------------------------------
const stamped = templates.filter((template) => !template.source.includes("__PI_AGENT_EXTENSION_DIR__"));
check(
	"every committed template keeps the placeholder (not a host path)",
	stamped.length === 0,
	`stamped in the repo: ${stamped.map((t) => t.name).join(", ")}`,
);

console.log(`\n${passed} passed, ${failures.length} failed  (${templates.length} agent templates)`);
if (failures.length > 0) process.exitCode = 1;
