/**
 * unity_log_critique — the iteration rubric.
 *
 * Scoring exists to force an explicit judgment, not to be a metric. The
 * viewAdequacy field is kept separate from the numeric scores on purpose: a
 * render that cannot show the thing being judged should not be able to produce
 * confident scores, and the two questions ("how good is it" and "can I even
 * see it") have different answers.
 */

import { appendFileSync } from "node:fs";
import { resolve } from "node:path";

import { ensureWorkspaceCritiqueLogExists, loadWorkspaceManifest } from "./workspace.js";

export interface CritiqueEntry {
	accuracy: number;
	geometry: number;
	materials: number;
	completeness: number;
	quality: number;
	issues: string[];
	viewAdequacy: string;
	nextAction: string;
}

export interface CritiqueLogResult {
	workspacePath: string;
	logPath: string;
	entry: CritiqueEntry;
}

const SCORE_FIELDS = ["accuracy", "geometry", "materials", "completeness", "quality"] as const;

function validateScore(name: string, value: number): void {
	if (!Number.isFinite(value) || value < 0 || value > 2) {
		throw new Error(`${name} must be between 0 and 2, got: ${value}`);
	}
}

export function unityLogCritique(
	options: { cwd: string; workspace?: string; workspacePath?: string } & CritiqueEntry,
): CritiqueLogResult {
	const workspacePath = options.workspacePath ?? resolve(options.cwd, options.workspace ?? ".");
	loadWorkspaceManifest(workspacePath);

	for (const field of SCORE_FIELDS) {
		validateScore(field, options[field]);
	}
	if (!options.nextAction.trim()) {
		throw new Error("nextAction is required: say what to attempt next, or \"present to user\".");
	}
	if (!options.viewAdequacy.trim()) {
		throw new Error(
			"viewAdequacy is required: judge whether the current render can actually show what you are evaluating.",
		);
	}

	const entry: CritiqueEntry = {
		accuracy: options.accuracy,
		geometry: options.geometry,
		materials: options.materials,
		completeness: options.completeness,
		quality: options.quality,
		issues: options.issues ?? [],
		viewAdequacy: options.viewAdequacy,
		nextAction: options.nextAction.trim(),
	};

	const logPath = ensureWorkspaceCritiqueLogExists(workspacePath);
	const total = SCORE_FIELDS.reduce((sum, field) => sum + entry[field], 0);
	appendFileSync(
		logPath,
		[
			"",
			`## ${new Date().toISOString()}`,
			`score: ${total}/10 (accuracy ${entry.accuracy}/2, geometry ${entry.geometry}/2, materials ${entry.materials}/2, completeness ${entry.completeness}/2, quality ${entry.quality}/2)`,
			`view adequacy: ${entry.viewAdequacy}`,
			`issues:`,
			...(entry.issues.length > 0 ? entry.issues.map((issue) => `- ${issue}`) : ["- (none recorded)"]),
			`next: ${entry.nextAction}`,
			"",
		].join("\n"),
		"utf8",
	);

	return { workspacePath, logPath, entry };
}
