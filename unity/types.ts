/**
 * Shapes shared across the Unity bridge.
 *
 * These mirror the JSON the C# plugin emits. They are hand-written rather than
 * inferred because the bridge is a wire format: keeping the contract in one
 * file makes a C# change and its TS consequence reviewable together.
 */

export interface UnityBridgeRequest {
	type: string;
	label?: string;
	[key: string]: unknown;
}

export interface UnityBridgeResponse<T> {
	ok: boolean;
	result?: T;
	error?: string;
}

/**
 * One Debug.Log line the Editor emitted while a snippet ran.
 *
 * `level` is Unity's own LogType name -- "Log", "Warning", "Error", "Assert",
 * "Exception" -- so it reads exactly like the Editor console.
 */
export interface UnityExecuteLogEntry {
	level: string;
	message: string;
	/** Present for Error/Exception/Assert; empty for a plain Debug.Log. */
	stack?: string;
}

export interface UnityExecuteResult {
	stdout: string;
	stderr: string;
	exitCode: number;
	changed: boolean;
	/**
	 * Whether a scene save was requested (the resolved `saveAfter`). The
	 * caller's intent, kept separate from the outcome below.
	 */
	saveRequested?: boolean;
	/**
	 * Whether unsaved scene work actually reached disk. False for a read-only
	 * run even when a save was requested, so intent and outcome cannot be read
	 * off the same field.
	 */
	saved?: boolean;
	label: string;
	/** Debug.Log lines captured during the snippet, in order. */
	logs?: UnityExecuteLogEntry[];
	/** Roslyn diagnostics, when the snippet failed to compile. */
	diagnostics?: string[];
	/** The snippet's own stack trace, when it threw. */
	stackTrace?: string;
	/** Present only when logTail was requested: the Editor log's last lines. */
	logTail?: string[];
	/** How many lines logTail actually returned; less than requested if the log is short or unreadable. */
	logTailLines?: number;
}

export interface UnitySessionContext {
	projectPath: string;
	unityVersion: string;
	isPlaying: boolean;
	isCompiling: boolean;
	scene: {
		name: string;
		path: string;
		isLoaded: boolean;
		availableScenes: string[];
	};
	selection: {
		activeGameObject: string;
		gameObjects: string[];
		count: number;
	};
	viewport: {
		hasSceneView: boolean;
		perspective?: string;
		cameraName?: string;
		pivotX?: number;
		pivotY?: number;
		pivotZ?: number;
	};
	tools: {
		activeTransformTool: string;
		activeTool: string;
	};
}

export interface UnitySceneObject {
	name: string;
	parent: string;
	childCount: number;
	active: boolean;
	components: string[];
	hasMesh: boolean;
	hasRenderer: boolean;
	/**
	 * World-space position, rotation (euler degrees) and local-space
	 * position/rotation/scale, as `[x, y, z]` number triples.
	 *
	 * Arrays rather than a joined "x,y,z" string, so a caller can use the
	 * numbers directly instead of parsing. World and local are both present
	 * because they answer different questions: world is where the object sits
	 * in the scene, local is how it sits inside its parent.
	 */
	position?: number[];
	rotationEuler?: number[];
	localPosition?: number[];
	localRotationEuler?: number[];
	localScale?: number[];
	tag?: string;
	layer?: string;
	isPrefab?: boolean;
	prefabAssetPath?: string;
}

/** One row of `unity_scene_info { categories: ["assets"] }`. */
export interface UnityAssetEntry {
	path: string;
	type: string;
	/**
	 * Disambiguates a prefab from a model, because both have main-asset type
	 * GameObject -- so `type` alone cannot tell a prefab worth placing from the
	 * FBX a prefab was already made from.
	 */
	isPrefab?: boolean;
	isModel?: boolean;
}

/** One bone's placement, from unity_bones. */
export interface UnityBone {
	name: string;
	/** Present only for bones reached through HumanBodyBones. */
	humanoidBone?: string;
	/** World space. */
	position: number[];
	/** Relative to the bone's parent. */
	localPosition: number[];
	rotationEuler: number[];
	localRotationEuler: number[];
	localScale: number[];
	/** Path from the scene root, so two bones with the same name are distinct. */
	path: string;
	active: boolean;
}

export interface UnityBonesResult {
	gameObject: string;
	scene: string;
	hasAnimator: boolean;
	hasSkinnedRenderer: boolean;
	isHuman?: boolean;
	avatarMask?: string;
	skinnedRenderer?: string;
	rootBone?: string;
	boneCount?: number;
	/**
	 * Bones addressed by HumanBodyBones name (Head, Hips, LeftHand, ...).
	 * On a humanoid rig this covers 53 of the 55 enum values; the two missing
	 * are `LastBone` and `UpperChest` on rigs that lack one.
	 */
	humanoidBones: Record<string, UnityBone>;
	/**
	 * The complete rig, keyed by transform path relative to the root bone.
	 * Much larger than humanoidBones -- a typical avatar has 180+ bones, one
	 * per finger joint -- so pass `bones` to filter.
	 */
	rig: Record<string, UnityBone>;
	truncated: boolean;
}

export interface UnitySerializedProperty {
	/** e.g. "m_Name", "m_CustomRenderQueue", "baseColor". */
	path: string;
	/** Human-readable label, which is what the Inspector shows. */
	displayName: string;
	/** SerializedPropertyType name: Float, Boolean, String, Vector3, ObjectReference... */
	type: string;
	/**
	 * Value shaped by `type`. Enums come back as `[index, displayName]`; a
	 * ManagedReference or Generic (nested struct/list) is a hint string telling
	 * you to address it by property path instead.
	 */
	value?: unknown;
}

export interface UnityComponentInfo {
	type: string;
	shortName: string;
	assembly: string;
	scriptAssetPath?: string;
	enabled?: boolean;
	missing?: boolean;
	properties?: UnitySerializedProperty[];
}

export interface UnityInspectResult {
	gameObject: string;
	path: string;
	position: number[];
	localPosition: number[];
	tag: string;
	layer: string;
	active: boolean;
	prefabAssetPath: string;
	components: UnityComponentInfo[];
	/** Full property dump, present only when `component` was requested. */
	inspect?: UnityComponentInfo;
	children?: { name: string; path: string; localPosition: number[]; childCount: number }[];
}

export interface UnitySetPropertyResult {
	gameObject: string;
	component: string;
	property: string;
	value: unknown;
	space: string;
	previousTypeAndHint?: string;
}

export interface UnitySetTransformResult {
	gameObject: string;
	space: string;
	position: number[];
	localPosition: number[];
	rotationEuler: number[];
	localScale: number[];
	previousPosition: string;
}

/**
 * Result of a `vrc` (avatar-side) or `world` (world-side) bridge call.
 *
 * Both request types share this shape because most of their actions return a
 * human-readable report, and a report is the useful form for findings like
 * "this avatar uploads unoptimized" or "TraceAndOptimize is MISSING". The
 * structured fields are present only for the actions that return data instead
 * of prose, so every field beyond `report` is optional.
 */
export interface UnityVrcResult {
	/** Prose report, for the actions that summarise rather than enumerate. */
	report?: string;
	/** `world action=descriptor`. */
	gameObject?: string;
	type?: string;
	shortName?: string;
	assembly?: string;
	assetPath?: string;
	scriptAssetPath?: string;
	enabled?: boolean;
	note?: string;
	/** `world action=find-behaviour`. */
	behaviour?: string;
	resolvedType?: string;
	found?: boolean;
	/** `world action=udon`. */
	worldsSdk?: boolean;
	udonSharp?: boolean;
	udonBehaviourCount?: number;
	explanation?: string;
	/** `world action=create-light-volume`. */
	component?: string;
	range?: string;
	intensity?: string;
	color?: string;
	newGameObject?: boolean;
	/**
	 * The mutating world actions (setup-layers, setup-post-processing, toggle-tag, quest-add, quest-apply,
	 * create-behaviour, convert-light, and the <package>-set actions). It is how a caller that ran two in a row
	 * tells the two answers apart, since the payload is otherwise the same `report` string.
	 */
	action?: string;
	/** `world action=find-behaviour`: which scope was searched on a miss. */
	searched?: string;
	/** The descriptor's spawn configuration, `world action=descriptor`. */
	spawnRadius?: string;
	spawnOrder?: string;
	spawnOrientation?: string;
	RespawnHeightY?: string;
	ObjectBehaviourAtRespawnHeight?: string;
	ForbidUserPortals?: string;
	ReferenceCamera?: string;
	DefaultSpawnScale?: string;
	UseCustomSpawnScale?: string;
	unityVersion?: string;
}

export interface UnityMaterialInfo {
	name: string;
	assetPath: string;
	shader: string;
	baseColor?: number[];
	renderQueue: string;
}

/**
 * A material in use that has no .mat file of its own.
 *
 * `assetPath` empty means it is a built-in shader material; otherwise it is the
 * asset the material is embedded in, which for a model from Blender is the FBX.
 */
export interface UnityEmbeddedMaterialInfo {
	name: string;
	assetPath: string;
	kind: "embeddedInModel" | "builtin";
	parentAsset: string;
	shader: string;
	/** Scene object name holding a renderer that uses it. */
	usedBy: string;
	color?: number[];
}

export interface UnityCameraInfo {
	name: string;
	gameObject: string;
	orthographic: boolean;
	fieldOfView: number;
	nearClipPlane: number;
	farClipPlane: number;
	depth: number;
	position: string;
	rotationEuler: string;
}

export interface UnityLightInfo {
	name: string;
	type: string;
	intensity: number;
	color: string;
	shadows: string;
}

export interface UnityRenderSettings {
	renderPipeline: string;
	colorSpace: string;
	antiAliasing: number;
	qualityLevel: number;
}

export interface UnityPackageInfo {
	name: string;
	version: string;
}

export interface UnityAssetInfo extends UnityAssetEntry {}

export interface UnityCompileStatus {
	isCompiling: boolean;
	lastError: string;
	/**
	 * Every `error CS…` line from the last finished compile, filtered to those
	 * that still describe the source on disk. Empty means a clean compile;
	 * `scriptsNeedingCompile` being non-empty is the OTHER failure mode, and the
	 * one an empty `errors` array on its own would hide.
	 */
	errors: string[];
	errorCount: number;
	/**
	 * Which of the two diagnostic paths answered.
	 *
	 * `event` is the compiler's own output, captured from
	 * `assemblyCompilationFinished` -- authoritative, because it belongs to the
	 * compile that actually ran. `log` is a parse of the Editor log tail, used
	 * when no capture exists: a compile that began before this Editor session
	 * could observe it, or a capture that could not be read. `none` means
	 * neither, so an empty `errors` array there is the absence of evidence
	 * rather than evidence of a clean compile.
	 */
	source: "event" | "log" | "none";
	/** Scripts written after the last compile cycle the Editor finished, so their code is not in the build. */
	scriptsNeedingCompile: string[];
	/**
	 * Whether those scripts exist -- NOT a statement by the compiler. It is a
	 * timestamp comparison, and on Unity 2022.3 it cannot be a fact about
	 * compilation: the build keys on content hashes, so touching a file without
	 * changing a byte rebuilds no assembly and leaves its output's mtime where
	 * it was. It is kept because the case it was written for is real -- a
	 * project whose every script is broken logs its errors once and then never
	 * again, so an empty error list would report it as clean.
	 */
	needsCompile: boolean;
	/** Where the compiler's messages were read from, so a reader can go and look. */
	logPath: string;
	projectPath: string;
}

export interface UnitySceneInfo {
	projectPath: string;
	activeScene: string;
	unityVersion: string;
	objects?: UnitySceneObject[];
	materials?: UnityMaterialInfo[];
	/**
	 * Materials a scene object actually uses that `materials` cannot contain:
	 * one embedded in a model file, or a built-in.
	 *
	 * `materials` is FindAssets("t:Material"), which only sees standalone .mat
	 * files. A model transferred from Blender carries its materials inside the
	 * FBX, so an empty `materials` list is not evidence they were lost --
	 * `embeddedMaterials` is where they are.
	 */
	embeddedMaterials?: UnityEmbeddedMaterialInfo[];
	cameras?: UnityCameraInfo[];
	lights?: UnityLightInfo[];
	renderSettings?: UnityRenderSettings;
	packages?: UnityPackageInfo[];
	assets?: UnityAssetInfo[];
	compileStatus?: UnityCompileStatus;
}

export interface UnityAwaitCompileResult {
	/**
	 * `compiled` only when the compiler reported no errors. `failed` when it
	 * reported some. `timeout` when the Editor never finished a compile inside
	 * the budget, which is NOT a pass and NOT a build result: it is the absence
	 * of a verdict, and `source` is `none` to say so.
	 */
	status: "compiled" | "failed" | "timeout";
	errors: string[];
	warnings: string[];
	/**
	 * Where the diagnostics came from, for the same reason compile-status reports
	 * it. `event` is the compiler's own output; `log` is the Editor.log parse,
	 * used when no compile was captured; `none` means nothing answered and the
	 * arrays carry the last compile's output rather than a verdict.
	 */
	source: "event" | "log" | "none";
	waitedMs: number;
	/**
	 * Whether the Editor's heartbeat moved during the wait. Deliberately NOT
	 * checked while waiting -- a domain reload and a compile both hold the main
	 * thread, so silence there is expected -- which is why it is reported as a
	 * fact instead of being used to abort. False means the Editor looks stuck or
	 * gone; run unity_bridge_status to tell those apart.
	 */
	editorResponding: boolean;
	/**
	 * True when the plugin recorded a compile CYCLE START after this call began
	 * and the budget ran out before it finished. Distinguishes "Unity is still
	 * compiling, just slower than the budget" from "nothing is happening", which
	 * are the same result otherwise. Always false when status is not "timeout".
	 */
	compileInFlight?: boolean;
}

export interface UnityRenderResult {
	outputPath: string;
	width: number;
	height: number;
	cameraName: string;
	usedActiveCamera: boolean;
}

export interface UnityCaptureResult {
	outputPath: string;
	perspective: string;
	sceneViewCamera: string;
}

export interface UnityToolResult {
	tool: string;
	activeTransformTool: string;
	selectedGameObjects: string[];
}

export interface UnityImportFbxResult {
	prefabPath: string;
	fbxAssetPath: string;
	rootName: string;
}

export interface UnityExportObjResult {
	objPath: string;
	mtlPath: string;
	meshNames: string[];
	materialNames: string[];
}

/**
 * One source model file a selection resolves to.
 *
 * `objects` lists which meshes were asked for; the FILE is the unit of
 * handoff, so a file with 6 sub-meshes travels whole even if you named one.
 */
export interface UnitySourceModel {
	assetPath: string;
	absolutePath?: string;
	fileName?: string;
	/** Export request only: where the file was copied to. */
	stagedPath?: string;
	/** Export request only: false when the source is absent from disk. */
	copied?: boolean;
	bytes?: number;
	error?: string;
	objects: string[];
	vertexTotal: number;
	boneTotal: number;
	exists?: boolean;
}

/**
 * Result of find-source-models / export-source-models.
 *
 * The three unhandoffable fields answer three different questions, and
 * conflating them is how a transfer reports success while dropping objects:
 *   unhandoffableObjects - "name -> path", for a human reading why it stayed
 *   unhandoffableNames   - bare GameObject names, usable as export-obj input
 *   unhandoffableCounts  - how many objects share each of those names
 *
 * The count exists because export-obj resolves a NAME to exactly ONE
 * GameObject. Five objects called "sphere" cannot all be exported by passing
 * that one name, so the binder reports them as not-transferred rather than
 * moving one and reporting all five.
 */
export interface UnitySourceModelsResult {
	models: UnitySourceModel[];
	/** Export request only. */
	stagedPaths?: string[];
	/** Export request only: sources referenced by the scene but absent on disk. */
	missingSources?: string[];
	unhandoffableObjects: string[];
	unhandoffableNames: string[];
	unhandoffableCounts: Record<string, number>;
	modelCount?: number;
	copiedCount?: number;
	missingCount?: number;
	unhandoffableCount: number;
	scope?: string;
}

export interface UnityOpenSceneResult {
	switched: boolean;
	scenePath: string;
}

/** One node of a prefab hierarchy, as the bridge reports it. */
export interface UnityPrefabChild {
	name: string;
	path: string;
	localPosition: number[];
	localScale: number[];
	childCount: number;
	components: string[];
	/** Present only when the node carries a MeshFilter with a mesh. */
	vertexCount?: number;
	children?: UnityPrefabChild[];
	/** Set when the listing stopped at the depth limit instead of at a leaf. */
	childrenTruncatedAt?: string;
}

/**
 * Bounds, with the caveat that produced them attached.
 *
 * `state` is load-bearing: a skinned renderer's bounds volume can read much
 * larger than the mesh at rest, so a size reported without this reads as a
 * measurement when it is really an upper bound.
 */
export interface UnityPrefabBounds {
	state: string;
	size?: number[];
	center?: number[];
}

export interface UnityPrefabMaterial {
	name: string;
	/** Empty when the material is embedded in the model asset. */
	assetPath: string;
	parentAsset: string;
	shader: string;
	color?: number[];
}

/** What a prefab ASSET contains, read without instantiating it. */
export interface UnityPrefabInfo {
	prefabPath: string;
	rootName: string;
	meshCount: number;
	childCount: number;
	localScale: number[];
	hasAnimator: boolean;
	bounds: UnityPrefabBounds;
	materials: UnityPrefabMaterial[];
	children: UnityPrefabChild[];
}

/** A prefab placed into the active scene. */
export interface UnityPrefabInstanceResult {
	gameObject: string;
	path: string;
	prefabAssetPath: string;
	scene: string;
	position: number[];
	rotationEuler: number[];
	localScale: number[];
	children: UnityPrefabChild[];
	bounds: UnityPrefabBounds;
}

/** One member requested for a composite prefab. */
export interface UnityComposePrefabMember {
	/** Asset-relative PREFAB path. An FBX/OBJ model asset is refused. */
	prefabPath: string;
	/** Name for this member inside the composite. Defaults to the prefab's own. */
	name?: string;
	/** LOCAL position as "x,y,z". Default "0,0,0". */
	position?: string;
	/** LOCAL rotation in Euler DEGREES as "x,y,z". Default "0,0,0". */
	rotation?: string;
	/** LOCAL scale as "x,y,z". Default "1,1,1". */
	scale?: string;
}

/**
 * One member of the composite, as it landed in the SAVED asset.
 *
 * `landedAsReference` is the field that carries the tool's central claim: it is
 * set only when the member is a prefab reference, so a composite that quietly
 * baked a copy names the member here rather than looking correct in a render.
 */
export interface UnityComposePrefabMemberReport {
	name: string;
	path: string;
	/** Asset path of the prefab this member references. Empty when it is not one. */
	sourceAsset: string;
	/** Diagnostic text when the member did NOT land as a reference. Empty otherwise. */
	landedAsReference: string;
	localPosition: number[];
	localScale: number[];
}

/**
 * The composite prefab that was written.
 *
 * `isPrefabOfPrefabs` is `nestedCount === memberCount`: true only when every
 * requested member survived as a reference, which is the difference between a
 * composite that follows its sources and one that looks the same and does not.
 */
export interface UnityComposePrefabResult {
	prefabPath: string;
	rootName: string;
	memberCount: number;
	nestedCount: number;
	isPrefabOfPrefabs: boolean;
	members: UnityComposePrefabMemberReport[];
	bounds: UnityPrefabBounds;
}

/**
 * One candidate log location, probed rather than asserted.
 *
 * `exists` is the load-bearing field: the Editor's log is NOT at
 * `<project>/Logs/Editor.log` (that is the *player* log), it goes to the XDG
 * config home, and which file that is depends on whether the Editor was launched
 * by the Hub (sandbox) or directly. Reporting a path without checking it would
 * send a reader to a file that is not there.
 */
/**
 * One measurement family `unity_geometry_probe` can run.
 *
 * `transform` catches unapplied/mirror scale, `bounds` the world-space volume
 * each object occupies, `axis` the object's own thin/long axis and how far it
 * sits off an expected one, `pair` the gap or overlap between two placed
 * objects, `shape` the defects a render cannot show, and `topology` the
 * expensive per-mesh defect counts.
 */
export type UnityGeometryProbeCheck = "transform" | "bounds" | "axis" | "pair" | "shape" | "topology";

/**
 * Bounds, with the caveat that produced them attached.
 *
 * `state` is load-bearing: a skinned renderer's bounds volume can read much
 * larger than the mesh at rest, and `meshBounds.state` says the same of a
 * transformed local box. A size reported without them reads as a measurement
 * when it is really an upper bound.
 */
export interface UnityProbeBounds {
	state: string;
	size?: number[];
	center?: number[];
	min?: number[];
	max?: number[];
	/**
	 * The mesh's own local box carried into world space. Absent when the mesh
	 * is not readable, which is a different answer from a zero-size box.
	 */
	meshBounds?: {
		state: string;
		size: number[];
		center: number[];
		min: number[];
		max: number[];
		localSizeX: number;
		localSizeY: number;
		localSizeZ: number;
	};
}

/**
 * `transform.mirrorScale` is the one figure here nothing else reports: an
 * object with a negative scale component looks correct and has inverted
 * normals and winding, and `unity_scene_info` carries only `localScale`.
 */
export interface UnityProbeTransform {
	position: number[];
	rotationEuler: number[];
	localScale: number[];
	/** Accumulated scale through the parent chain, which can differ from localScale. */
	lossyScale: number[];
	/** Empty when the object is a scene root. */
	parent: string;
	unappliedScale: boolean;
	nonUniformScale: boolean;
	mirrorScale: boolean;
	/** The single flipped component, or "" when the flip cannot be attributed to one. */
	mirroredAxis: string;
	state: string;
}

export interface UnityProbeAxis {
	expectedAxis: string;
	/** Present only when no mesh was found; the absence says why. */
	state?: string;
	measured?: {
		state: string;
		thinAxisLocal: number[];
		thinAxisWorld: number[];
		longAxisLocal: number[];
		longAxisWorld: number[];
		thinExtent: number;
		longExtent: number;
		/** Degrees between the object's thin axis and `expectedAxis`. */
		thinAxisAngleToExpected: number;
		longAxisAngleToExpected: number;
		/** thinExtent / longExtent. Under 0.05 is ring- or tube-like. */
		axisRatio: number;
		isRingLike: boolean;
		center: number[];
		mirrorScale: boolean;
		centerNote: string;
	};
}

export interface UnityProbeSelfSymmetry {
	/** The world axis the mesh is symmetric about, or "" when none qualifies. */
	mirrorAxis: string;
	deviationRatio?: number;
	/** Explains an absent `deviationRatio`/`perAxis` on a mesh too small to test. */
	state?: string;
	perAxis?: Array<{
		axis: string;
		meanDeviation: number;
		maxDeviation: number;
		deviationRatio: number;
	}>;
}

export interface UnityProbeShape {
	vertices: number;
	triangles?: number;
	dimensions?: number[];
	selfSymmetry?: UnityProbeSelfSymmetry;
	convexity?: {
		available: boolean;
		/** Fraction of sampled vertices sitting on the mesh's own convex hull. */
		convexityRatio: number;
		sampledVertices?: number;
		/** How far the worst interior vertex sits below the hull. */
		deepestInsideHull?: number;
		state: string;
	};
	openEdges?: number;
	looseVertices?: number;
	degenerateFaces?: number;
	/** Present when symmetry and convexity ran on a subsample of the mesh. */
	sampleNote?: string;
	state?: string;
}

export interface UnityProbeTopology {
	vertices: number;
	triangles: number;
	/** Edges shared by MORE than two faces. */
	nonManifoldEdges: number;
	/** Edges with exactly one face, i.e. the mesh is not closed there. */
	boundaryEdges: number;
	looseVertices: number;
	degenerateFaces: number;
	/** Vertices sharing a position to within 1e-5. Absent when not counted. */
	duplicateVertices?: number;
	/** Says whether the count is real, skipped for size, or a read failure. */
	duplicateState?: string;
	state?: string;
}

export interface UnityProbeObject {
	name: string;
	/** Scene path, the unambiguous handle for everything that follows. */
	path: string;
	/** The object the mesh came from, when it was not the named object itself. */
	meshOwner?: string;
	/** Why the mesh was found where it was, or why there is none. */
	meshState?: string;
	transform?: UnityProbeTransform;
	bounds?: UnityProbeBounds;
	axis?: UnityProbeAxis;
	shape?: UnityProbeShape;
	topology?: UnityProbeTopology;
}

export interface UnityProbePair {
	/** Scene path of the first object. */
	a: string;
	/** Scene path of the second object. */
	b: string;
	verdict: "interpenetrating" | "in-contact" | "separated" | "unknown";
	reason?: string;
	tolerance: number;
	closestSurfaceDistance?: number;
	/** Positive depth of the deepest overlap, 0 when they do not overlap. */
	deepestPenetration?: number;
	/** Keyed by scene path, since two placed prefabs can share a name. */
	verticesInsideTarget?: Record<string, number>;
	sampledVertexCount?: Record<string, number>;
	/** Signed per-axis overlap; a negative component means separated on that axis. */
	boundingBoxOverlap?: { x: number; y: number; z: number };
	state?: string;
}

export interface UnityGeometryProbeResult {
	scene: string;
	checks: UnityGeometryProbeCheck[];
	tolerance: number;
	expectedAxis: string;
	unitNote: string;
	objects: UnityProbeObject[];
	pairs: UnityProbePair[];
}

export interface UnityLogCandidate {
	path: string;
	role: string;
	exists: boolean;
}

export interface UnityLogResult {
	logs: UnityLogCandidate[];
}

export interface UnityReloadSceneResult {
	scenePath: string;
	discardedUnsavedChanges: boolean;
}

/** Which sections a scene_info response should carry. */
export const UNITY_SCENE_INFO_CATEGORIES = [
	"objects",
	"materials",
	"cameras",
	"lights",
	"renderSettings",
	"packages",
	"assets",
	"compileStatus",
] as const;

export type UnitySceneInfoCategory = (typeof UNITY_SCENE_INFO_CATEGORIES)[number];

/**
 * Default set for a plain `unity_scene_info` call. Packages/assets are left
 * out because they are large and rarely what the caller wants unprompted.
 */
export const UNITY_SCENE_INFO_DEFAULT_CATEGORIES: UnitySceneInfoCategory[] = [
	"objects",
	"materials",
	"cameras",
	"lights",
	"renderSettings",
	"compileStatus",
];

export const UNITY_TOOLS = [
	"move",
	"rotate",
	"scale",
	"rect",
] as const;

export type UnityTool = (typeof UNITY_TOOLS)[number];
