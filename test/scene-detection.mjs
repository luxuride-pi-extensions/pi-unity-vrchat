/**
 * Scene detection for adopted projects.
 *
 * The manifest's scenePath is load-bearing, not documentation: open-scene
 * refuses a path that does not exist, and every module checkpoint copies that
 * same file. So the generated default (Assets/Scenes/Main.unity) was a trap
 * for anyone adopting a real project — it initialised fine and then failed on
 * the first scene operation, naming a scene the user never had.
 *
 * The fixtures below are the three shapes that actually occur: a VRChat-style
 * project with an unconventional scene name and a populated build-settings
 * file, a plain project that happens to have Main.unity, and a project whose
 * only scene is somewhere odd.
 */

import assert from "node:assert/strict";
import { mkdirSync, mkdtempSync, rmSync, writeFileSync } from "node:fs";
import { tmpdir } from "node:os";
import { join } from "node:path";

import { JITI } from "./paths.mjs";

const { createJiti } = await import(JITI);
const jiti = createJiti(import.meta.url, { isolated: true, interopDefault: true });
const { findProjectScene } = await jiti.import(join(import.meta.dirname, "..", "unity", "init.ts"));

const tmp = mkdtempSync(join(tmpdir(), "unity-vrchat-scene-"));

let passed = 0;
const failures = [];
const check = (name, fn) => {
	try {
		fn();
		passed += 1;
	} catch (error) {
		failures.push(`${name}: ${error instanceof Error ? error.message : String(error)}`);
	}
};

/** A minimal project skeleton; scenes are added per test. */
function makeProject(name) {
	const root = join(tmp, name);
	mkdirSync(join(root, "Assets"), { recursive: true });
	mkdirSync(join(root, "ProjectSettings"), { recursive: true });
	writeFileSync(join(root, "ProjectSettings", "ProjectVersion.txt"), "m_EditorVersion: 2022.3.22f1\n");
	return root;
}

function addScene(root, relative, guid) {
	const file = join(root, relative);
	mkdirSync(join(file, ".."), { recursive: true });
	writeFileSync(file, "%YAML 1.1\n");
	if (guid) {
		writeFileSync(`${file}.meta`, `fileFormatVersion: 2\nguid: ${guid}\n`);
	}
}

function setBuildScenes(root, guids) {
	const lines = guids.map((g) => `  - {fileID: 102900000, guid: ${g}, type: 3}`).join("\n");
	writeFileSync(join(root, "ProjectSettings", "EditorBuildSettings.asset"), `EditorBuildSettings:\n  m_Scenes:\n${lines}\n`);
}

// --- the case that was broken: a real project, unconventional scene name ---

{
	const root = makeProject("vrchat-world");
	addScene(root, "Assets/Scene.unity", "a".repeat(32));
	addScene(root, "Assets/Scenes/Main.unity", "b".repeat(32));
	setBuildScenes(root, ["a".repeat(32)]);
	check("build settings win over the conventional name", () => {
		assert.equal(findProjectScene(root), "Assets/Scene.unity");
	});
}

{
	// A build-settings entry pointing at a scene that is not on disk must not
	// produce a path that does not exist, which is the bug in another form.
	const root = makeProject("stale-build-settings");
	addScene(root, "Assets/Scenes/Main.unity", "b".repeat(32));
	setBuildScenes(root, ["c".repeat(32)]);
	check("a build-settings entry with no scene on disk falls through", () => {
		assert.equal(findProjectScene(root), "Assets/Scenes/Main.unity");
	});
}

{
	const root = makeProject("plain");
	addScene(root, "Assets/Scenes/Main.unity");
	check("a conventional project keeps Main.unity", () => {
		assert.equal(findProjectScene(root), "Assets/Scenes/Main.unity");
	});
}

{
	const root = makeProject("odd-location");
	addScene(root, "Assets/Level01/Midway.unity");
	addScene(root, "Assets/Level02/End.unity");
	check("an unusual layout still yields a scene", () => {
		assert.equal(findProjectScene(root), "Assets/Level01/Midway.unity");
	});
}

{
	const root = makeProject("no-scenes");
	check("a project with no scene reports null, not a made-up path", () => {
		assert.equal(findProjectScene(root), null);
	});
}

{
	// Packages/ ships sample scenes. Adopting a project must not adopt one of
	// those: it is read-only and not the user's work.
	const root = makeProject("with-packages");
	addScene(root, "Packages/com.example/Samples/Demo.unity");
	addScene(root, "Assets/Scenes/Main.unity");
	check("a Packages sample scene is never chosen", () => {
		assert.equal(findProjectScene(root), "Assets/Scenes/Main.unity");
	});
}

{
	// Paths come out of readdir with platform separators; the manifest is
	// compared against resolved paths elsewhere, so the shape must be stable.
	const root = makeProject("separators");
	addScene(root, "Assets/Deep/Nested/Scene.unity");
	const found = findProjectScene(root);
	check("the returned path uses forward slashes", () => {
		assert.ok(found && !found.includes("\\"), `got ${found}`);
		assert.equal(found, "Assets/Deep/Nested/Scene.unity");
	});
}

check("a directory that is not a project does not throw", () => {
	assert.equal(findProjectScene(join(tmp, "nope")), null);
});

rmSync(tmp, { recursive: true, force: true });

console.log(`${passed} passed, ${failures.length} failed`);
if (failures.length) {
	for (const f of failures) console.error(`FAIL ${f}`);
	process.exit(1);
}
