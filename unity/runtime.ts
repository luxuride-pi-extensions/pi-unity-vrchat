/**
 * Single import surface for index.ts, mirroring how the Blender extension
 * re-exports its blender/ internals.
 */

export {
	unityWorkspaceInit,
	ensureWorkspaceInitialized,
	openWorkspaceInLiveUnity,
	looksLikeUnityProject,
	type WorkspaceInitOptions,
	type WorkspaceInitResult,
	type UnityTemplate,
} from "./init.js";

export {
	getSceneInfo,
	unityExecuteCSharp,
	resolveSceneInfoCategories,
	type SceneInfoOptions,
	type SceneInfoResult,
	type ExecuteCSharpOptions,
	type ExecuteCSharpResult,
} from "./execute.js";

export { unityCaptureView, unityRender, type CaptureViewOptions, type RenderOptions } from "./render.js";
export { assertCameraExists, unitySaveView, unitySetTool, type SetToolOptions } from "./save-view.js";
export {
	unitySessionContext,
	type SessionContextOptions,
	type SessionContextResult,
} from "./session-context.js";
export { unityBridgeStatus, type BridgeStatusDeps, type BridgeStatusResult } from "./status.js";
export { unityLogCritique, type CritiqueEntry } from "./critique.js";
export {
	unityModuleRevert,
	unityModuleRun,
	type ModuleRevertOptions,
	type ModuleRevertResult,
	type ModuleRunOptions,
	type ModuleRunResult,
} from "./module.js";

export {
	requestLiveUnityBones,
	requestLiveUnityImportFbx,
	requestLiveUnityExportObj,
	requestLiveUnityFindSourceModels,
	requestLiveUnityExportSourceModels,
	requestLiveUnityGeometryProbe,
	requestLiveUnityInstantiatePrefab,
	requestLiveUnityPrefabInfo,
	requestLiveUnityInspect,
	requestLiveUnitySceneInfo,
	requestLiveUnitySessionContext,
	requestLiveUnitySetProperty,
	requestLiveUnitySetTransform,
	requestLiveUnityVrc,
	requestLiveUnityWorld,
	getLiveBridgePaths,
} from "./bridge.js";

export {
	UNITY_SCENE_INFO_CATEGORIES,
	UNITY_SCENE_INFO_DEFAULT_CATEGORIES,
	UNITY_TOOLS,
	type UnityBone,
	type UnityBonesResult,
	type UnityComponentInfo,
	type UnityInspectResult,
	type UnitySceneInfo,
	type UnitySceneInfoCategory,
	type UnitySerializedProperty,
	type UnitySetPropertyResult,
	type UnitySetTransformResult,
	type UnityVrcResult,
	type UnitySessionContext,
	type UnityImportFbxResult,
	type UnityExportObjResult,
	type UnitySourceModel,
	type UnitySourceModelsResult,
	type UnityGeometryProbeCheck,
	type UnityGeometryProbeResult,
	type UnityProbeAxis,
	type UnityProbeBounds,
	type UnityProbeObject,
	type UnityProbePair,
	type UnityProbeSelfSymmetry,
	type UnityProbeShape,
	type UnityProbeTopology,
	type UnityProbeTransform,
} from "./types.js";

export {
	CRITIQUE_LOG_FILE,
	MANIFEST_FILE,
	WORKSPACE_SCRIPT_FILE,
	normalizeWorkspacePath,
	type UnityWorkspaceManifest,
	type ModuleRunRecord,
	type SavedView,
} from "./workspace.js";

export { getBundledUnitySkillsDir, getBundledUnityAgentsDir } from "../config.js";
