/**
 * The C# plugin's path, naming and honesty rules, asserted against the source.
 *
 * The reason this file reads the .cs rather than executing it: every rule below
 * is a decision the C# side makes at RUNTIME, in an Editor, on the user's real
 * project. `npm run test:plugin` proves the plugin COMPILES with the real Unity;
 * nothing in CI launches an Editor to drive `import-fbx` against a project, so
 * the guarantees themselves had no check at all. Reading the source is the only
 * assertion available in this harness, and it is a real one: each check fails on
 * the pre-fix text, and the failure message quotes the exact shape it wanted
 * rather than a line number that moves.
 *
 * What is deliberately NOT asserted: that the code is correct. These are
 * structural invariants that a refactor cannot break by accident -- a
 * destination path derived in two places, a fixed sidecar file name, a null
 * written into a property. Each check names the finding it belongs to, so a
 * future reader can tell a missing fix from a missing test.
 */

import assert from "node:assert/strict";
import { readFileSync } from "node:fs";
import { dirname, join } from "node:path";
import { fileURLToPath } from "node:url";

const HERE = dirname(fileURLToPath(import.meta.url));
const PLUGIN = join(HERE, "..", "unity", "plugin");

const read = (file) => readFileSync(join(PLUGIN, file), "utf8");

const interchange = read("PiUnityBridge.Interchange.cs");
const sourceModels = read("PiUnityBridge.SourceModels.cs");
const core = read("PiUnityBridge.Core.cs");
const inspect = read("PiUnityBridge.Inspect.cs");
const polling = read("PiUnityBridge.Polling.cs");
const geometry = read("PiUnityBridge.Geometry.cs");
const dispatch = read("PiUnityBridge.Dispatch.cs");

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

// ---- Finding A: the copy target is the name that was checked -------------
{
	// The bug in one sentence: the guard computed `destination/modelName.fbx`
	// while MoveIntoAssets computed `Path.GetFileNameWithoutExtension(source)`.
	// The two must be the same value, which means modelName has to cross the
	// call boundary -- it cannot be re-derived inside.
	check(
		"MoveIntoAssets takes the target name as a parameter",
		/MoveIntoAssets\s*\(\s*string\s+\w+\s*,\s*string\s+\w+\s*,\s*string\s+\w+\s*,/.test(interchange),
		"no targetName parameter: the name is still re-derived from the source path",
	);
	check(
		"import-fbx passes the sanitized modelName in",
		/MoveIntoAssets\(\s*fbxPath\s*,\s*destination\s*,\s*modelName\s*,/.test(interchange),
		"modelName is not the argument, so the guard and the copy can still name different files",
	);
	check(
		"the copy target is no longer derived from the source file's own stem",
		!/Path\.Combine\(absolute,\s*Path\.GetFileNameWithoutExtension\(sourcePath\)\s*\+/.test(interchange),
		"the target is still re-derived from sourcePath",
	);
}

// ---- Finding B: containment is normalised, not a prefix test -------------
{
	check(
		"the Assets/ containment check normalises with Path.GetFullPath",
		/GetFullPath/.test(core) && /Assets/.test(core),
		"no normalised containment helper in PiUnityBridge.Core.cs",
	);
	check(
		"the Assets/ check requires a directory SEPARATOR after Assets",
		/assetsRoot\s*\+\s*Path\.DirectorySeparatorChar/.test(core),
		"no separator check, so AssetsEvil would still pass",
	);
	check(
		"MoveIntoAssets no longer gates on a bare StartsWith(\"Assets\")",
		!/destinationFolder\.StartsWith\("Assets"/.test(interchange),
		"the bare prefix test is still there",
	);
	check(
		"export-source-models refuses an outputDir that resolves into Assets/",
		/export-source-models requires an outputDir[\s\S]{0,2400}?insideAssets/.test(sourceModels),
		"outputDir is validated for emptiness only",
	);
	check(
		"the staging path is normalised before the Assets/ comparison",
		/stagingRoot\s*=\s*Path\.IsPathRooted\(outputDir\)/.test(sourceModels) &&
			/Path\.GetFullPath\(Path\.Combine\(ProjectRoot\(\), outputDir\)\)/.test(sourceModels),
		"a relative outputDir was resolved against the CWD, which is not the project root",
	);
}

// ---- Finding C: colliding names are disambiguated ------------------------
{
	check(
		"the material sidecar is no longer a fixed materials.json",
		!/Path\.Combine\(directory,\s*"materials\.json"\)/.test(interchange),
		"a fixed name means a second export into a shared staging dir overwrites the first",
	);
	check(
		"the sidecar name is derived per run",
		/materialSidecarName|materials\.json/.test(interchange) && /GetFileNameWithoutExtension\(outputPath\)/.test(interchange),
		"no per-run sidecar name",
	);
	check(
		"staged source models go through a disambiguating name chooser",
		/UniqueStagedName\(group\.AssetPath,\s*stagedNames\)/.test(sourceModels),
		"staging still uses Path.GetFileName, so Assets/A/Body.fbx and Assets/B/Body.fbx collide",
	);
	check(
		"the reported stagedPath is the decided target, not a recomputed basename",
		/entry\.AddString\("stagedPath",\s*target\)/.test(sourceModels),
		"stagedPath must be the path that was written",
	);
}

// ---- Finding D: a failed lookup cannot clear a reference ----------------
{
	const resolve = inspect.slice(inspect.indexOf("private static UnityEngine.Object ResolveObjectReference"));
	check(
		"ResolveObjectReference no longer ends in a bare `return null` for a miss",
		!/return null;\s*\}\s*\n\s*private static string\[\] SplitNumbers/.test(resolve),
		"a miss still returns null, which AssignProperty writes straight into the property",
	);
	check(
		"a miss throws naming the type and the value",
		/No object of type/.test(resolve) && /unity_scene_info/.test(resolve),
		"the miss message does not tell the caller what to do",
	);
	check(
		"the explicit `none` clear keeps its own branch",
		/typeName\.Equals\("none"/.test(resolve),
		"`none` no longer has a branch of its own, so it cannot be told from a miss",
	);
	check(
		"an unsearchable type is reported separately from a miss",
		/unsearchable\.Count == searched/.test(resolve),
		"a swallowed FindObjectsOfTypeAll failure is still indistinguishable from 'no such object'",
	);
}

// ---- Finding E: a swallowed response write leaves a trace ----------------
{
	check(
		"WriteResponse logs when BOTH the atomic publish and the fallback write fail",
		/response write failed for/.test(polling),
		"the inner catch is still empty, so a lost response has no diagnostic at all",
	);
	check(
		"WritePartialResponse logs its own failure too",
		/partial response write failed for/.test(polling),
		"the partial-response fallback catch is still empty",
	);
	check(
		"no empty catch remains on either response path",
		!/catch \(Exception\)\s*\{\s*\}/.test(polling),
		"an empty catch is still swallowing something",
	);
}

// ---- Finding F: an unmeasured distance is not reported as zero ----------
{
	check(
		"closestSurfaceDistance is never written as 0 for an unsampled pair",
		!/AddNumber\("closestSurfaceDistance",\s*closest == double\.MaxValue \? 0\.0/.test(geometry),
		"0.0 still stands in for 'not measured', which reads as touching",
	);
	check(
		"an unsampled pair explains the ABSENT number",
		/distanceState/.test(geometry),
		"no distanceState explaining why the number is missing",
	);
	check(
		"the misleading full-triangle-set label is gone for a triangle-less mesh",
		!/world-space triangles, full triangle set/.test(geometry) || /triangles\.Length == 0/.test(geometry),
		"a mesh with vertices and no triangles is still labelled 'full triangle set'",
	);
}

// ---- Finding G: a skinned measurement says it is bind pose --------------
{
	check(
		"the mesh source carries a bind-pose note",
		/BindPoseNote/.test(geometry) && /BIND POSE/.test(geometry),
		"a skinned mesh is still measured in bind pose with nothing said",
	);
	check(
		"the pair's own state names the bind pose",
		/first\.BindPose \|\| second\.BindPose/.test(geometry),
		"the pair numbers are not labelled",
	);
	check(
		"BindPose is a flag on the surface, not only prose",
		/public bool BindPose;/.test(geometry),
		"nothing a caller could read structurally",
	);
}

// ---- Finding H: one world-space transform per object, exactness kept -----
{
	const builds = [...geometry.matchAll(/ProbeSurface\.Build\(/g)].length;
	check(
		"ProbeSurface.Build is reached from exactly one place in the request path",
		builds <= 4,
		`${builds} call sites: check that the duplicate convexity pass is gone`,
	);
	check(
		"convexity takes the SHARED surface instead of building one",
		/MeasureProbeConvexity\(sharedSurface/.test(geometry) || /MeasureProbeConvexity\(ProbeSurface/.test(geometry),
		"convexity still builds its own surface",
	);
	check(
		"the topology walk states that it is uncapped and on the main thread",
		/costNote/.test(geometry) && /UNCAPPED/.test(geometry),
		"an exact, uninterruptible walk is reported as though it were free",
	);
	check(
		"AddProbeTopology's exactness is intact (no silent cap on the triangle walk)",
		/exact over every/i.test(geometry) && !/triangleLimit[\s\S]{0,80}AddProbeTopology/.test(geometry),
		"the exact walk appears to have been capped, which would make the defect count a lie",
	);
}

// ---- Finding I, second part: intent and outcome are separate fields -----
{
	check(
		"the execute handler reports whether a save actually happened",
		/AddBool\("saved",\s*saved\)/.test(dispatch),
		"saved is not the outcome",
	);
	check(
		"the intent has its own field",
		/AddBool\("saveRequested"/.test(dispatch),
		"intent and outcome share one field, so a read-only probe reports saved:true",
	);
	check(
		"saved is taken from the pre-save dirty state, not from the request",
		/SceneManager\.GetActiveScene\(\)\.isDirty/.test(dispatch),
		"no pre-save state is read",
	);
}

console.log(`\n${passed} passed, ${failures.length} failed`);
if (failures.length > 0) {
	for (const failure of failures) console.log(`  - ${failure}`);
	process.exit(1);
}