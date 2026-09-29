/**
 * The module run's sequence must be the claimed iteration, not the length of
 * the history.
 *
 * The history only ever records SUCCESSFUL runs, so `moduleRuns.length + 1` is
 * derived from a base that changes while a run is inside Unity. Two runs
 * overlapping both read the same length and are handed the same sequence — and
 * here the sequence is also the run DIRECTORY name (modules/<name>/runs/NN), so
 * the second run overwrites the first's script, log and checkpoint. The
 * sequence is then what unityModuleRevert compares to decide what "later" means,
 * so its guard against discarding another module's work stops working too.
 *
 * Two runs genuinely inside Unity at once cannot be reproduced here, so what is
 * asserted is the decision itself, factored out of the live path: the sequence
 * is strictly increasing across claims even when the history does not change,
 * which is exactly the case the length-derived version got wrong.
 */

import assert from "node:assert/strict";
import { JITI } from "./paths.mjs";

const { createJiti } = await import(JITI);
const jiti = createJiti(import.meta.url, { isolated: true, interopDefault: true });
const { claimModuleRun } = await jiti.import("../unity/module.ts");

/** A manifest state holding only what the claim touches. */
const state = (latestIteration, recordedRuns = 0) => ({
	latestIteration,
	// History is an array of run RECORDS. A run in flight has claimed its number
	// but has not appended a record yet, so its absence here is the normal case
	// and not a missing fixture.
	moduleRuns: new Array(recordedRuns).fill({}),
});

function check(label, fn) {
	try {
		fn();
		console.log(`  ok  ${label}`);
	} catch (error) {
		console.error(`FAIL ${label}\n     ${error.message}`);
		process.exitCode = 1;
	}
}

check("a fresh workspace claims 1", () => {
	assert.deepEqual(claimModuleRun(state(0)), { iteration: 1, sequence: 1 });
});

check("the claim advances the counter and returns it", () => {
	const s = state(7);
	const { iteration, sequence } = claimModuleRun(s);
	assert.equal(iteration, 8);
	assert.equal(sequence, 8);
	assert.equal(s.latestIteration, 8, "updateManifest persists the state, not the return value");
});

check("two runs in flight get different sequences, with no history between them", () => {
	// The bug, in the shape it occurs: run A claims 1 and does not record itself;
	// run B claims 2, also unrecorded. The length of moduleRuns is 0 for both
	// moments, so the old code handed 1 to each.
	const a = claimModuleRun(state(0, 0));
	const b = claimModuleRun(state(a.iteration, 0));
	assert.deepEqual(
		[a.sequence, b.sequence],
		[1, 2],
		"an in-flight run must not be handed out twice",
	);
});

check("sequences stay strictly increasing while the history stays empty", () => {
	// The invariant the length-derived version violated: with nothing recorded,
	// a length-based sequence repeats. Ten claims, no records.
	const sequences = [];
	let s = state(0, 0);
	for (let i = 0; i < 10; i++) {
		s = state(s.latestIteration, 0);
		sequences.push(claimModuleRun(s).sequence);
	}
	assert.deepEqual(sequences, [1, 2, 3, 4, 5, 6, 7, 8, 9, 10]);
	assert.equal(new Set(sequences).size, sequences.length, "sequences must be unique");
});

check("a recorded run does not shift a later claim", () => {
	// The direction that would break monotonic ordering if the two numbers were
	// related wrongly: a legacy manifest whose records carry sequences at or
	// below their iterations must still compare correctly against new claims.
	const legacy = { latestIteration: 5, moduleRuns: [{ sequence: 1, iteration: 1 }, { sequence: 3, iteration: 3 }] };
	const { iteration, sequence } = claimModuleRun(legacy);
	assert.equal(iteration, 6);
	assert.ok(sequence > 3, "a new claim sorts after every legacy sequence");
});

if (process.exitCode) {
	console.error("\nmodule-sequence: FAILED");
} else {
	console.log("\nmodule-sequence: 5 passed, 0 failed");
}
