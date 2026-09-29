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
 *    any extension we can load, so they cannot be verified by SET membership.
 *    They are whitelisted as AMBIENT, and `todo` additionally gets a dedicated
 *    structural check, because that is the one whose provider is a host path
 *    that silently goes missing.
 *  - This reads the TEMPLATES, which carry the `__PI_*__` placeholders. It
 *    verifies what the installer is asked to produce, not the installed
 *    result; `test/install-agents.mjs` covers the substitution.
 *
 * It also loads pi-blender as a TOOL PROVIDER even though it contributes no
 * templates to this check. `unity-blender-manager` lives in the binder repo and
 * names both sides' tools, so without pi-blender in the provider set every
 * `blender_*` tool it lists would read as unresolvable — a false failure that
 * trains you to ignore this suite. Its OWN templates are deliberately not
 * walked: they carry a documented `/path/to/...` placeholder form rather than
 * the `__PI_*__` placeholders checked below, so judging them here would assert
 * a contract that repo does not have and it checks its templates itself.
 */

import { existsSync, readFileSync, readdirSync } from "node:fs";
import { join } from "node:path";

import { INSTALLED_EXTENSIONS, JITI } from "./paths.mjs";

/**
 * Tools that come from pi-subagents, the todo package, pi's own built-ins, or
 * an npm `packages:` entry rather than a local extension.
 *
 * `ask_user_question` is the third kind: it is registered by
 * `@juicesharp/rpiv-ask-user-question` in settings.json, so the resolver below
 * never sees a local extension providing it. It was missing here and the suite
 * reported the two manager templates as shipping an unresolvable tool, which is
 * the cry-wolf failure this file exists to prevent.
 */
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
	"ask_user_question",
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

const TEMPLATE_DIRS = {
	"pi-unity": "unity/agents",
	"unity-blender": "agents",
};

/** Every extension whose tools a template may name, including ones with no templates here. */
const PROVIDER_DIRS = ["pi-unity", "pi-blender", "unity-blender"];

/**
 * Which extension dir each placeholder the installers substitute resolves to.
 *
 * A template that has not been stamped yet names extensions by PLACEHOLDER, and
 * a placeholder matches no tool provider by string equality — so resolving them
 * any other way reports a merged template as if it listed none of its
 * providers. `__PI_AGENT_EXTENSION_DIR__` is deliberately absent: it is the
 * template's OWN extension, which is per-template and therefore handled at the
 * call site. `__PI_TODO_ENTRY__` is a host path outside every extension dir, so
 * it resolves to nothing here and the todo checks below cover it instead.
 */
const PLACEHOLDER_OWNERS = new Map([
	["__PI_PI_UNITY_EXT_DIR__", "pi-unity"],
	["__PI_BLENDER_DIR__", "pi-blender"],
]);

/**
 * The extension dir a single `extensions:` entry names, or "" when it names
 * no extension at all (the todo host path, a relative or missing path).
 */
function providerDirOf(entry, ownExtensionName) {
	if (entry.includes("__PI_AGENT_EXTENSION_DIR__")) return ownExtensionName;
	const owner = PLACEHOLDER_OWNERS.get(entry);
	if (owner) return owner;
	// An unstamped or already-stamped path: its last real segment is the dir.
	return entry.replace(/\/index\.ts$/, "").split("/").filter(Boolean).pop() ?? "";
}

/** Every agent template across both repos, as {name, extensionName, tools, extensions}. */
function collectTemplates() {
	const templates = [];
	for (const [extensionName, relative] of Object.entries(TEMPLATE_DIRS)) {
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

/**
 * Tools each template's own extension registers, keyed by directory name.
 *
 * Loading all three matters: the binder's `unity-blender-manager` names tools
 * from BOTH sides, so without `pi-blender` in here every `blender_*` tool it
 * lists would read as unresolvable and the suite would cry wolf.
 */
const providers = new Map();
for (const extensionName of PROVIDER_DIRS) {
	providers.set(extensionName, await toolsOf(extensionName));
}
const allProviderTools = new Set([...providers.values()].flatMap((set) => [...set]));
const templates = collectTemplates();

check("found agent templates", templates.length > 0, `found ${templates.length}`);
check(
	"every provider extension dir was readable",
	providers.size === PROVIDER_DIRS.length,
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
	// be attributed by string. providerDirOf maps it back to the repo the
	// template lives in, which is what the installer resolves it to.
	const listedDirs = new Set(
		template.extensions
			.flatMap((entry) => entry.split(",").map((part) => part.trim()))
			.map((entry) => providerDirOf(entry, template.extensionName))
			.filter(Boolean),
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
// A committed template that lists extensions must list them as placeholders.
// A host path baked into the repo is the failure: it names one developer's
// machine, and the installer then resolves to nothing on anyone else's.
//
// A template with NO `extensions:` line at all is exempt, because that is a
// legitimate and deliberate state rather than a stamped one: an agent that
// needs no extension tool should not carry a provider it never calls, and
// `unity-blender-reference-describer` is exactly that (it holds `read`, which is
// ambient). Requiring the placeholder there would push it toward naming a
// directory it does not need.
const declaresExtensions = (template) => /^extensions:/m.test(template.source);
const stamped = templates.filter(
	(template) => declaresExtensions(template) && !template.source.includes("__PI_AGENT_EXTENSION_DIR__"),
);
check(
	"every committed template that lists extensions keeps the placeholder (not a host path)",
	stamped.length === 0,
	`stamped in the repo: ${stamped.map((t) => t.name).join(", ")}`,
);

console.log(`\n${passed} passed, ${failures.length} failed  (${templates.length} agent templates)`);
if (failures.length > 0) process.exitCode = 1;
