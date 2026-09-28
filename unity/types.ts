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

export interface UnityExecuteResult {
	stdout: string;
	stderr: string;
	exitCode: number;
	changed: boolean;
	label: string;
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
}

export interface UnityMaterialInfo {
	name: string;
	assetPath: string;
	shader: string;
	baseColor?: number[];
	renderQueue: string;
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

export interface UnityAssetInfo {
	path: string;
	type: string;
}

export interface UnityCompileStatus {
	isCompiling: boolean;
	lastError: string;
	projectPath: string;
}

export interface UnitySceneInfo {
	projectPath: string;
	activeScene: string;
	unityVersion: string;
	objects?: UnitySceneObject[];
	materials?: UnityMaterialInfo[];
	cameras?: UnityCameraInfo[];
	lights?: UnityLightInfo[];
	renderSettings?: UnityRenderSettings;
	packages?: UnityPackageInfo[];
	assets?: UnityAssetInfo[];
	compileStatus?: UnityCompileStatus;
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

export interface UnityOpenSceneResult {
	switched: boolean;
	scenePath: string;
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
