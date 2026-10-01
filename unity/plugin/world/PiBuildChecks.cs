// Part of the pi Unity bridge plugin. See PiUnityBridge.Core.cs for the
// protocol overview. The whole plugin is one assembly of partial classes, so
// Unity compiles it into Assembly-CSharp-Editor with no asmdef and no
// package dependencies, which is what lets it drop into any Unity 2021+
// project (a configured VRChat world, an existing game) that pi was pointed at
// rather than generated.
//
// VRChat's own upload validation, run WITHOUT uploading, plus a second opinion
// on what would make the upload fail.
//
// WHY THE SDK SURFACE IS REACHED BY REFLECTION
// --------------------------------------------
// Same reason as every other file here: a `using VRC.SDKBase.Validation;` would
// stop the plugin compiling in every project without the VRChat SDK, which is
// the one property the no-assembly-definition design exists to preserve. So each
// SDK member is resolved at run time and a missing one is a line in a report --
// never an exception out of a bridge request.
//
// WHY THE SDK's BUILD CALLBACK IS NOT CALLED
// ------------------------------------------
// VRC.SDKBase.Editor.BuildPipeline.VRCBuildPipelineCallbacks
//     .OnVRCSDKBuildRequested(...) must NOT be invoked from a bridge request.
// It is the intended API for "run the pre-build checks", and it is a trap here:
// VRWorldToolkit.Editor.BuildChecksManager implements that same callback and
// opens a MODAL EditorUtility.DisplayDialogComplex when spawn points are missing
// or when the scene carries more than one PipelineManager. A modal dialog with
// no one to click it blocks the Editor for the rest of the session, and the
// bridge request never returns. So the validators are driven directly below and
// VRWorld Toolkit's two checks are reimplemented read-only.
//
// Names verified against com.vrchat.worlds 3.10.1 / VRCSDKBase.dll, VRCCore-Editor
// and VRWorldToolkit 3.4.1. A member this SDK version does not carry is reported
// as "not available: <reason>" and is never counted as a pass.

using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using UnityEditor;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace PiUnity
{
	public static class PiBuildChecks
	{
		private const string ValidationUtilsType = "VRC.SDKBase.Validation.ValidationUtils";
		private const string WorldValidationType = "VRC.SDKBase.Validation.WorldValidation";
		private const string PipelineManagerType = "VRC.Core.PipelineManager";
		private const string PostBuildValidatorType = "VRC.SDK3.Editor.VRCWorldPostBuildValidator";
		private const string UdonSharpBuildChecksType = "UdonSharpEditor.UdonSharpBuildChecks";
		private const string UdonSharpBuildCompileType = "UdonSharpEditor.UdonSharpBuildCompile";
		private const string ValidationHelpersType = "VRC.ValidationHelpers";
		private const string ContentTypeEnum = "VRC.ContentType";
		private const string ToolkitValidationType = "VRWorldToolkit.Editor.Validation";
		private const string StationType = "VRC.SDK3.Components.VRCStation";
		private const string PickupType = "VRC.SDK3.Components.VRCPickup";
		private const string ObjectSyncType = "VRC.SDK3.Components.VRCObjectSync";

		/// The VRChat SDK's own whitelist identifiers. Probed rather than assumed:
		/// which of the three this SDK version serves is a fact about the
		/// project, and guessing wrong means every component looks illegal.
		private static readonly string[] WhitelistNames = { "Common", "Sdk2", "Sdk3" };

		/// The SDK names its whitelists by the ASSEMBLY they came from, and the
		/// names have changed across SDK versions. Each is probed with
		/// every spelling rather than assumed, because a name that returns
		/// nothing is indistinguishable from a whitelist that does not exist.
		private static readonly string[][] WhitelistNameSpellings =
		{
			new[] { "Common", "SDK2Common", "CommonSDK2", "SDKCommon", "WhitelistCommon" },
			new[] { "Sdk2", "SDK2", "Sdk2Common", "WhitelistSDK2" },
			new[] { "Sdk3", "SDK3", "Sdk3Common", "WhitelistSDK3" },
		};

		private const int MaxListLines = 12;

		// =========================================================== build-checks

		/// VRChat's pre-upload validation, driven validator by validator, with
		/// nothing built and nothing uploaded.
		public static string RunBuildChecks()
		{
			var checks = new Checklist();
			Scene scene = SceneManager.GetActiveScene();
			var report = new StringBuilder();
			report.AppendLine("--- VRChat build checks (no build run, nothing uploaded) ---");
			report.AppendLine("scene=" + (scene.IsValid() ? scene.name : "<none open>")
				+ "  path=" + (scene.IsValid() && !string.IsNullOrEmpty(scene.path) ? scene.path : "<unsaved>"));

			Component descriptor = PiUdon.SceneDescriptor();
			CheckSceneDescriptor(checks, descriptor);
			CheckSpawnPoints(checks, descriptor);
			CheckPipelineManagers(checks, scene);
			CheckIllegalComponents(checks, scene);
			CheckIllegalShaders(checks, scene);
			CheckEditorOnly(checks, scene);
			CheckEditorCompileState(checks);
			CheckUdonCompileErrors(checks);
			ReportBuildCallbacks(checks);

			report.Append(checks.Render());
			report.AppendLine(checks.Verdict("safe to upload", "not safe to upload"));
			report.AppendLine("This ran the validators only. It does not build the world, does not run the Udon");
			report.AppendLine("compiler, and uploads nothing -- a real check that only a real build can run is");
			report.AppendLine("listed above as not available.");
			return report.ToString();
		}

		private static void CheckSceneDescriptor(Checklist checks, Component descriptor)
		{
			if (descriptor == null)
			{
				checks.Fail("scene descriptor", "no VRCSceneDescriptor in the open scene",
					"add the VRCWorld prefab to the scene; without a descriptor the scene is not a world");
				return;
			}
			checks.Pass("scene descriptor", descriptor.GetType().FullName + " on \"" + descriptor.gameObject.name + "\"");
		}

		/// Reimplemented read-only from VRWorldToolkit's BuildChecksManager: its
		/// version of this check sits behind the modal dialog above.
		private static void CheckSpawnPoints(Checklist checks, Component descriptor)
		{
			if (descriptor == null)
			{
				checks.Skip("spawn points", "no scene descriptor to read spawns from");
				return;
			}
			Array spawns = PiWorldToolkit.ReadFieldByName(descriptor, "spawns") as Array;
			if (spawns == null && !HasField(descriptor, "spawns"))
			{
				checks.Skip("spawn points", "this descriptor type has no spawns field (looked for \"spawns\" on "
					+ descriptor.GetType().FullName + " and its base types)");
				return;
			}
			int count = spawns == null ? 0 : spawns.Length;
			if (count == 0)
			{
				checks.Fail("spawn points", "descriptor.spawns is " + (spawns == null ? "null" : "empty")
					+ " -- this world has nowhere to place a player",
					"add at least one child Transform under Spawn Points on the VRCWorld prefab");
			}
			else
			{
				checks.Pass("spawn points", count + " configured");
			}
			var detail = new StringBuilder();
			AppendField(detail, descriptor, "spawnRadius");
			AppendField(detail, descriptor, "spawnOrder");
			AppendField(detail, descriptor, "spawnOrientation");
			AppendField(detail, descriptor, "RespawnHeightY");
			if (detail.Length > 0) checks.Detail("  " + detail.ToString().TrimEnd());

			// A spawn list that holds only null entries passes the length test and
			// still places nobody, which is the same bug by another route.
			if (spawns != null && count > 0)
			{
				int assigned = 0;
				foreach (object entry in spawns) if (entry is Transform) assigned++;
				if (assigned < count)
				{
					checks.Warn("spawn points", count - assigned + " of " + count + " spawn entries are unassigned",
						"fill the empty slots in Spawn Points or delete them; an unassigned entry is dropped at upload");
				}
			}
		}

		/// The second VRWorld Toolkit check, also read-only: more than one
		/// PipelineManager means the scene carries two build pipelines' state.
		private static void CheckPipelineManagers(Checklist checks, Scene scene)
		{
			Type type = VrcResolve.Type(PipelineManagerType);
			if (type == null)
			{
				checks.Skip("PipelineManager", PipelineManagerType + " does not resolve; VRCCore-Editor is not loaded");
				return;
			}
			var managers = new List<Component>();
			foreach (GameObject root in SceneRoots(scene))
			{
				managers.AddRange(VrcResolve.ComponentsOn(root, PipelineManagerType));
			}
			// GetComponentsInChildren returns a derived type under a base name, so
			// two names for one class would otherwise count one manager twice.
			var unique = new List<Component>();
			var seen = new HashSet<Component>();
			foreach (Component manager in managers) if (seen.Add(manager)) unique.Add(manager);

			if (unique.Count == 0)
			{
				checks.Warn("PipelineManager", "none in the scene",
					"VRChat adds one at upload time; a scene without one has not been through the SDK pipeline");
			}
			else if (unique.Count > 1)
			{
				var names = new List<string>();
				foreach (Component manager in unique) names.Add(manager.gameObject.name);
				checks.Fail("PipelineManager", unique.Count + " in the scene: " + string.Join(", ", names.ToArray())
					+ " [" + type.FullName + ", assembly " + type.Assembly.GetName().Name + "]",
					"delete every PipelineManager but one; VRChat's own build check refuses the scene otherwise");
			}
			else
			{
				checks.Pass("PipelineManager", "1 on \"" + unique[0].gameObject.name + "\"");
			}
			foreach (Component manager in unique)
			{
				var line = new StringBuilder();
				AppendField(line, manager, "launchedFromSDKPipeline");
				AppendField(line, manager, "completedSDKPipeline");
				AppendField(line, manager, "blueprintId");
				AppendField(line, manager, "contentType");
				if (line.Length > 0) checks.Detail("    " + manager.gameObject.name + ": " + line.ToString().TrimEnd());
			}
		}

		/// ValidationUtils.FindIllegalComponents per scene root, grouped by root,
		/// with the whitelist this SDK actually served rather than an assumed one.
		private static void CheckIllegalComponents(Checklist checks, Scene scene)
		{
			Type utils = VrcResolve.Type(ValidationUtilsType);
			if (utils == null)
			{
				checks.Skip("illegal components", ValidationUtilsType + " does not resolve; com.vrchat.base is not loaded");
				return;
			}
			MethodInfo finder = VrcResolve.StaticMethod(ValidationUtilsType, "FindIllegalComponents",
				typeof(GameObject), typeof(HashSet<>).MakeGenericType(typeof(Type)), typeof(bool));
			if (finder == null)
			{
				checks.Skip("illegal components", ValidationUtilsType
					+ ".FindIllegalComponents(GameObject, HashSet<Type>, bool) is absent in this SDK version");
				return;
			}
			// A whitelist of TYPES, not of names. The SDK's own lists are
			// HashSet<Type>, and a HashSet<string> built from them would hold
			// System.Type objects that never equal a type name, so the check
			// would pass while reporting nothing.
			Type[] whitelist = ResolveWhitelist(checks);
			if (whitelist == null)
			{
				checks.Skip("illegal components", "no SDK component whitelist resolved (tried the configured"
					+ " names and " + WorldValidationType + ".ComponentTypeWhiteListCommon / Sdk2 / Sdk3)");
				return;
			}

			// The third argument is the SDK's own excludeEditorOnly flag; true
			// matches what the SDK does at build time, and EditorOnly-tagged
			// objects are reported on their own by CheckEditorOnly.
			int total = 0;
			var offenders = new List<string>();
			var matched = new HashSet<Type>();
			foreach (GameObject root in SceneRoots(scene))
			{
				if (!Invoke(finder, null, new object[] { root, whitelist, true }, out object result)) continue;
				var onRoot = new List<string>();
				foreach (object entry in Enumerate(result))
				{
					Component component = entry as Component;
					if (component == null) continue;
					if (matched.Add(component.GetType())) continue;
					onRoot.Add(component.GetType().Name + " on \"" + component.gameObject.name + "\"");
				}
				if (onRoot.Count == 0) continue;
				total += onRoot.Count;
				offenders.Add("  " + root.name + " (" + onRoot.Count + ")");
				for (int i = 0; i < onRoot.Count && i < MaxListLines; i++) offenders.Add("      " + onRoot[i]);
				if (onRoot.Count > MaxListLines) offenders.Add("      ... " + (onRoot.Count - MaxListLines) + " more");
			}
			if (total == 0)
			{
				checks.Pass("illegal components", "none, against " + whitelist.Length + " whitelisted types");
				return;
			}
			checks.Fail("illegal components", total + " illegal component(s)",
				"remove them, or move them onto a Quest-only layer/tag exclusion; the SDK refuses the build otherwise");
			foreach (string line in offenders) checks.Detail(line);
		}

		/// Whitelisted component TYPES, built from the SDK's own string lists.
		///
		/// ValidationUtils.GetCachedWhitelistedTypes reads a configured whitelist
		/// by NAME, and in this SDK no name resolves -- the lists live in
		/// WorldValidation as ComponentTypeWhiteListCommon / Sdk2 / Sdk3 and the
		/// names it wants are its own resource keys. The named call is tried
		/// first because a project that HAS configured names gets the SDK's exact
		/// set; the string lists are the fallback that always works.
		///
		/// A list entry is a type NAME, not a type, so each is resolved across
		/// the loaded assemblies and an entry that does not resolve is counted
		/// and reported: a whitelist the SDK ships for components this project
		/// does not have is normal, and silently dropping the entry would hide a
		/// whitelist that failed to load at all.
		private static Type[] ResolveWhitelist(Checklist checks)
		{
			Type[] configured = WhitelistFromNamedLookup(checks);
			if (configured != null) return configured;

			var merged = new HashSet<Type>();
			var resolved = new List<string>();
			int unresolved = 0;
			foreach (string field in new[] { "ComponentTypeWhiteListCommon",
				"ComponentTypeWhiteListSdk2", "ComponentTypeWhiteListSdk3" })
			{
				string[] names = StaticStringArray(WorldValidationType, field);
				if (names == null)
				{
					checks.Detail("  (" + WorldValidationType + "." + field + " did not read)");
					continue;
				}
				int found = 0;
				foreach (string name in names)
				{
					if (string.IsNullOrEmpty(name)) continue;
					Type type = VrcResolve.Type(name);
					if (type == null) { unresolved++; continue; }
					if (merged.Add(type)) found++;
				}
				resolved.Add(field.Replace("ComponentTypeWhiteList", "") + " (" + found + " of " + names.Length + ")");
			}
			if (merged.Count == 0)
			{
				checks.Detail("  (no component whitelist resolved from " + WorldValidationType
					+ ".ComponentTypeWhiteListCommon / Sdk2 / Sdk3)");
				return null;
			}
			checks.Detail("  whitelist from " + WorldValidationType + ": "
				+ string.Join(", ", resolved.ToArray()) + "  union=" + merged.Count + " types"
				+ (unresolved > 0 ? "  (" + unresolved + " listed type(s) are not loaded in this project)" : ""));
			return merged.ToArray();
		}

		/// The SDK's configured whitelist, when this project's SDK serves one by
		/// name. Returns null rather than an empty set so the caller can tell
		/// "no configuration answered" from "the configuration is empty".
		private static Type[] WhitelistFromNamedLookup(Checklist checks)
		{
			Type hashSetOfType = typeof(HashSet<>).MakeGenericType(typeof(Type));
			MethodInfo getter = VrcResolve.StaticMethod(ValidationUtilsType, "GetCachedWhitelistedTypes",
				typeof(string), hashSetOfType.MakeByRefType());
			if (getter == null)
			{
				checks.Detail("  (this SDK version has no " + ValidationUtilsType + ".GetCachedWhitelistedTypes)");
				return null;
			}
			var merged = new HashSet<Type>();
			var resolved = new List<string>();
			foreach (string[] spellings in WhitelistNameSpellings)
			{
				foreach (string name in spellings)
				{
					object[] args = { name, null };
					if (!Invoke(getter, null, args, out object ignored)) continue;
					if (!(args[1] is IEnumerable set)) continue;
					int count = 0;
					foreach (object entry in set)
					{
						if (entry is Type type) { merged.Add(type); count++; }
					}
					if (count == 0) continue;
					resolved.Add(name + " (" + count + ")");
					break;
				}
			}
			if (merged.Count == 0) return null;
			checks.Detail("  whitelist from " + ValidationHelpersType + " configured names: "
				+ string.Join(", ", resolved.ToArray()) + "  union=" + merged.Count + " types");
			return merged.ToArray();
		}

		/// A public static string[] field read by name, or null.
		/// The SDK's whitelist lists are internal statics, so NonPublic is part of
		/// the lookup: a Public-only read returns null for every name and the check
		/// silently degrades to "not available".
		private static string[] StaticStringArray(string typeName, string fieldName)
		{
			Type type = VrcResolve.Type(typeName);
			if (type == null) return null;
			try
			{
				FieldInfo field = type.GetField(fieldName,
					BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static);
				return field == null ? null : field.GetValue(null) as string[];
			}
			catch (Exception)
			{
				return null;
			}
		}

		private static void CheckIllegalShaders(Checklist checks, Scene scene)
		{
			Type utils = VrcResolve.Type(ValidationUtilsType);
			if (utils == null)
			{
				checks.Skip("illegal shaders", ValidationUtilsType + " does not resolve; com.vrchat.base is not loaded");
				return;
			}
			string[] whitelist = ShaderWhiteList(out string source);
			if (whitelist == null)
			{
				checks.Skip("illegal shaders", "no shader whitelist resolved from " + WorldValidationType
					+ ".ShaderWhiteList or " + ToolkitValidationType + ".WorldShaderWhiteList");
				return;
			}
			MethodInfo finder = VrcResolve.StaticMethod(ValidationUtilsType, "FindIllegalShaders",
				typeof(GameObject), typeof(string[]));
			if (finder == null)
			{
				checks.Skip("illegal shaders", ValidationUtilsType
					+ ".FindIllegalShaders(GameObject, string[]) is absent in this SDK version");
				return;
			}
			checks.Detail("  whitelist from " + source + " (" + whitelist.Length + " entries)");

			var all = new List<string>();
			var byRoot = new List<string>();
			foreach (GameObject root in SceneRoots(scene))
			{
				if (!Invoke(finder, null, new object[] { root, whitelist }, out object result)) continue;
				var onRoot = new List<string>();
				foreach (object entry in Enumerate(result))
				{
					string shader = entry as string ?? entry?.ToString();
					if (string.IsNullOrEmpty(shader)) continue;
					onRoot.Add(shader);
				}
				if (onRoot.Count == 0) continue;
				all.AddRange(onRoot);
				byRoot.Add("  " + root.name + ": " + string.Join(", ", onRoot.Distinct().ToArray()));
			}
			if (all.Count == 0)
			{
				checks.Pass("illegal shaders", "none");
				return;
			}
			checks.Fail("illegal shaders", string.Join(", ", all.Distinct().Take(MaxListLines).ToArray())
				+ " (" + all.Count + " occurrence(s))",
				"swap the material to a whitelisted shader, or add the shader to the project's own whitelist");
			foreach (string line in byRoot.Take(MaxListLines)) checks.Detail(line);
		}

		/// Objects tagged EditorOnly are STRIPPED at build, so anything wanted in
		/// the world that carries the tag is a real finding, not a footnote.
		private static void CheckEditorOnly(Checklist checks, Scene scene)
		{
			string tag = ReadStaticField(ValidationUtilsType, "EDITOR_ONLY_TAG") as string;
			if (string.IsNullOrEmpty(tag)) tag = "EditorOnly";
			var tagged = new List<string>();
			foreach (GameObject root in SceneRoots(scene))
			{
				foreach (Transform child in root.GetComponentsInChildren<Transform>(true))
				{
					bool matches;
					try { matches = string.Equals(child.gameObject.tag, tag, StringComparison.Ordinal); }
					catch (Exception) { continue; }
					if (!matches) continue;
					tagged.Add(DescribePath(child));
				}
			}
			if (tagged.Count == 0)
			{
				checks.Pass("EditorOnly objects", "none");
				return;
			}
			checks.Warn("EditorOnly objects", tagged.Count + " tagged \"" + tag + "\" -- stripped from the build",
				"if the object is meant to be in the world, remove the EditorOnly tag; it will not be there in-game");
			for (int i = 0; i < tagged.Count && i < MaxListLines; i++) checks.Detail("    " + tagged[i]);
			if (tagged.Count > MaxListLines) checks.Detail("    ... " + (tagged.Count - MaxListLines) + " more");
		}

		private static void CheckEditorCompileState(Checklist checks)
		{
			if (EditorApplication.isCompiling)
			{
				checks.Fail("editor compile state", "the Editor is compiling scripts right now",
					"wait for the compile to finish; a build started mid-compile uses stale assemblies");
				return;
			}

			// "Idle" is what isCompiling reports whether the last compile
			// succeeded or failed outright, so on its own it passes a project
			// whose scripts never compiled. The compiler's own messages are
			// the real state.
			var errors = PiUnity.Bridge.CompileErrorMessages();
			if (errors.Count > 0)
			{
				checks.Fail("editor compile state", errors.Count + " script compile error(s); the last one says: " + errors[0],
					"fix the script errors before building -- every type below is from the LAST compile that succeeded, "
					+ "so this scene's scripts may not match what is on disk");
				for (int i = 0; i < errors.Count && i < 8; i++) checks.Detail("    " + errors[i]);
				if (errors.Count > 8) checks.Detail("    ... " + (errors.Count - 8) + " more");
				return;
			}

			var stale = PiUnity.Bridge.ScriptsNeedingCompile();
			if (stale.Count > 0)
			{
				// No errors, yet the scripts are newer than the assemblies. The
				// log went quiet because a project whose every script is broken
				// stops logging, so silence here means nothing.
				checks.Fail("editor compile state", stale.Count + " script file(s) newer than the last successful compile",
					"these have never built; the log has no error for them, which means the last compile aborted before it reported -- "
					+ "read \"compile-status\", or open the Console");
				for (int i = 0; i < stale.Count && i < 6; i++) checks.Detail("    " + stale[i]);
				if (stale.Count > 6) checks.Detail("    ... " + (stale.Count - 6) + " more");
				return;
			}

			checks.Pass("editor compile state", "idle");
		}

		/// A UdonBehaviour whose program carries a compile error uploads as a
		/// broken behaviour: the object exists and does nothing.
		private static void CheckUdonCompileErrors(Checklist checks)
		{
			List<Component> behaviours = PiUdon.FindUdonBehaviours();
			if (behaviours.Count == 0)
			{
				checks.Pass("Udon programs", "no UdonBehaviour in the scene");
				return;
			}
			var broken = new List<string>();
			foreach (Component behaviour in behaviours)
			{
				object hasError = ReadProperty(behaviour, "HasError");
				if (!(hasError is bool flag) || !flag) continue;
				var line = new StringBuilder();
				line.Append("HasError=true on \"").Append(behaviour.gameObject.name).Append("\"");
				AppendProperty(line, behaviour, "ProgramId");
				AppendProperty(line, behaviour, "ProgramSize");
				AppendProperty(line, behaviour, "SyncMethod");
				AppendProperty(line, behaviour, "InteractionText");
				broken.Add(line.ToString());
			}
			if (broken.Count == 0)
			{
				checks.Pass("Udon programs", behaviours.Count + " UdonBehaviour(s), none reporting HasError");
			}
			else
			{
				checks.Fail("Udon programs", broken.Count + " of " + behaviours.Count + " UdonBehaviour(s) report HasError",
					"open the UdonSharp script and fix the compile error; UdonSharpBuildCompile is what recompiles it");
				for (int i = 0; i < broken.Count && i < MaxListLines; i++) checks.Detail("    " + broken[i]);
				if (broken.Count > MaxListLines) checks.Detail("    ... " + (broken.Count - MaxListLines) + " more");
			}
		}

		/// The SDK's own post-build validator only runs inside a real build, and
		/// UdonSharp's two callbacks are named so the reader knows which of them
		/// checks versus compiles.
		private static void ReportBuildCallbacks(Checklist checks)
		{
			checks.Note("not run by this action:");
			checks.Detail("    " + PostBuildValidatorType
				+ ".OnProcessScene runs only during a real build; it is not callable stand-alone");
			checks.Detail("    " + UdonSharpBuildChecksType
				+ " is the safe build callback (it refreshes the AssetDatabase and reports whether the Editor is compiling)");
			checks.Detail("    " + UdonSharpBuildCompileType + " is the one that actually compiles Udon");
		}

		// ============================================================ upload-risk

		/// A second opinion, aimed at what makes VRChat REJECT a world or force a
		/// rebuild, at the level of assets rather than scene structure.
		public static string RunUploadRisk()
		{
			Scene scene = SceneManager.GetActiveScene();
			var report = new StringBuilder();
			var risks = new List<Risk>();
			report.AppendLine("--- VRChat upload risk (second opinion, no build, nothing uploaded) ---");
			report.AppendLine("scene=" + (scene.IsValid() ? scene.name : "<none open>")
				+ "  path=" + (scene.IsValid() && !string.IsNullOrEmpty(scene.path) ? scene.path : "<unsaved>"));
			report.AppendLine("Sizes labelled [ESTIMATE] are computed from an asset's on-disk length or from");
			report.AppendLine("declared audio parameters, not from the built AssetBundle VRChat actually measures.");

			CheckSceneSizeCensus(report, risks, scene);
			CheckAudioClips(report, risks);
			CheckTextures(report, risks);
			CheckAndroidShaders(report, risks);
			CheckMaterials(report, risks);
			CheckBehaviourConfiguration(report, risks, scene);
			CheckAudioSources(report, risks, scene);

			report.AppendLine("--- top risks, ranked by (certainty x cost of the upload problem) ---");
			if (risks.Count == 0)
			{
				report.AppendLine("  nothing scored above the noise floor in this scene.");
			}
			else
			{
				int index = 0;
				foreach (Risk risk in risks.OrderByDescending(item => item.Score).ThenBy(item => item.Finding).Take(5))
				{
					report.AppendLine("  " + (++index) + ". [score " + risk.Score + "] " + risk.Finding);
					report.AppendLine("     fix: " + risk.Fix);
				}
			}
			return report.ToString();
		}

		private static void CheckSceneSizeCensus(StringBuilder report, List<Risk> risks, Scene scene)
		{
			report.AppendLine("--- scene size census ---");
			if (!scene.IsValid() || string.IsNullOrEmpty(scene.path))
			{
				report.AppendLine("  not available: the scene has never been saved, so it has no asset path to measure from.");
				report.AppendLine("  save the scene, then run this action again.");
				return;
			}
			string[] dependencies;
			try
			{
				dependencies = AssetDatabase.GetDependencies(scene.path, true);
			}
			catch (Exception error)
			{
				report.AppendLine("  not available: AssetDatabase.GetDependencies failed (" + error.Message + ")");
				return;
			}
			var byExtension = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
			long total = 0;
			int unmeasured = 0;
			var biggest = new List<KeyValuePair<string, long>>();
			foreach (string dependency in dependencies)
			{
				if (string.Equals(dependency, scene.path, StringComparison.OrdinalIgnoreCase)) continue;
				long size = AssetFileSize(dependency);
				if (size < 0) { unmeasured++; continue; }
				total += size;
				string extension = Path.GetExtension(dependency).ToLowerInvariant();
				if (!byExtension.TryGetValue(extension, out long bucket)) bucket = 0;
				byExtension[extension] = bucket + size;
				biggest.Add(new KeyValuePair<string, long>(dependency, size));
			}

			report.AppendLine("  assets measured: " + biggest.Count + "   not measured: " + unmeasured
				+ (unmeasured > 0 ? " (package or non-Assets paths, which carry no file size here)" : ""));
			foreach (string kind in new[] { ".png", ".jpg", ".jpeg", ".tga", ".psd", ".bmp", ".exr", ".tif",
				".wav", ".mp3", ".ogg", ".aif", ".aiff", ".fbx", ".obj", ".dae", ".mat" })
			{
				if (!byExtension.TryGetValue(kind, out long size) || size == 0) continue;
				report.AppendLine("    " + kind + " [ESTIMATE] " + Bytes(size) + " (" + size + " bytes on disk)");
			}
			var other = byExtension.Keys.Where(key => !KnownExtensions.Contains(key)).OrderBy(key => key).ToArray();
			if (other.Length > 0)
			{
				long rest = other.Sum(key => byExtension[key]);
				report.AppendLine("    other [ESTIMATE] " + Bytes(rest) + " across " + other.Length + " extension(s): "
					+ string.Join(" ", other.Take(MaxListLines).ToArray()));
			}
			report.AppendLine("  total [ESTIMATE] " + Bytes(total) + " (" + total + " bytes on disk)");

			report.AppendLine("  VRChat's own limit, from " + ValidationHelpersType + ".GetAssetBundleSizeLimit:");
			AppendSizeLimit(report, "PC", false);
			AppendSizeLimit(report, "PC uncompressed", false, false);
			long androidLimit = AppendSizeLimit(report, "Android", true);
			AppendSizeLimit(report, "Android uncompressed", true, false);
			if (androidLimit > 0 && total > androidLimit)
			{
				risks.Add(new Risk(95, "the scene's own assets [ESTIMATE] " + Bytes(total)
					+ " already exceed the Android limit of " + Bytes(androidLimit),
					"cut assets or move them off this scene; the world is over budget before VRChat adds anything of its own"));
			}

			if (total > 0 && biggest.Count > 0)
			{
				foreach (KeyValuePair<string, long> entry in biggest.OrderByDescending(item => item.Value).Take(5))
				{
					report.AppendLine("    largest: " + Bytes(entry.Value) + "  " + entry.Key);
				}
			}
		}

		/// Returns the limit in bytes, or -1 when it could not be read.
		private static long AppendSizeLimit(StringBuilder report, string label, bool mobilePlatform, bool compressed = true)
		{
			Type contentType = VrcResolve.Type(ContentTypeEnum);
			if (contentType == null)
			{
				report.AppendLine("    " + label + ": not available: " + ContentTypeEnum + " does not resolve");
				return -1;
			}
			// Enum.Parse on the FIELD INFO rather than the field's boxed value
			// throws InvalidCastException, so the World member is read by name
			// through GetField and its value taken from there.
			string world = Enum.GetNames(contentType).FirstOrDefault(name =>
				string.Equals(name, "World", StringComparison.OrdinalIgnoreCase));
			if (world == null)
			{
				report.AppendLine("    " + label + ": not available: " + ContentTypeEnum + " has no World member (has "
					+ string.Join(", ", Enum.GetNames(contentType)) + ")");
				return -1;
			}
			FieldInfo field = contentType.GetField(world, BindingFlags.Public | BindingFlags.Static);
			object worldValue = field == null ? null : field.GetValue(null);
			if (worldValue == null)
			{
				report.AppendLine("    " + label + ": not available: " + ContentTypeEnum + "." + world + " has no value");
				return -1;
			}
			MethodInfo method = VrcResolve.StaticMethod(ValidationHelpersType, "GetAssetBundleSizeLimit",
				contentType, typeof(bool), typeof(bool));
			if (method == null)
			{
				report.AppendLine("    " + label + ": not available: " + ValidationHelpersType
					+ ".GetAssetBundleSizeLimit(ContentType, bool, bool) is absent in this SDK version");
				return -1;
			}
			if (!Invoke(method, null, new object[] { worldValue, mobilePlatform, compressed }, out object result))
			{
				report.AppendLine("    " + label + ": not available: GetAssetBundleSizeLimit threw for "
					+ ContentTypeEnum + "." + world);
				return -1;
			}
			// The SDK returns a NEGATIVE number where the platform has no size
			// limit, and a limit of 0 would read as "every world is over budget",
			// so a value below 1 is reported as the absence of a limit. Those
			// constants really do exist with those values in VRC.ValidationHelpers
			// (CONTENT_WORLD_ASSET_BUNDLE_SIZE_LIMIT_PC = -1).
			try
			{
				long bytes = Convert.ToInt64(result, CultureInfo.InvariantCulture);
				if (bytes < 1)
				{
					report.AppendLine("    " + label + ": no limit enforced by the SDK for this case (it returned "
						+ bytes + ")");
					return -1;
				}
				report.AppendLine("    " + label + ": " + Bytes(bytes) + " (" + bytes + " bytes)");
				return bytes;
			}
			catch (Exception error)
			{
				report.AppendLine("    " + label + ": not available: GetAssetBundleSizeLimit returned "
					+ (result == null ? "null" : result.GetType().FullName) + " (" + error.Message + ")");
				return -1;
			}
		}

		private static void CheckAudioClips(StringBuilder report, List<Risk> risks)
		{
			report.AppendLine("--- audio clips ---");
			AudioClip[] clips = UnityEngine.Object.FindObjectsOfType<AudioClip>(true);
			if (clips.Length == 0)
			{
				report.AppendLine("  no AudioClip referenced by anything in the scene.");
				return;
			}
			var longClips = new List<string>();
			var wrongLoadType = new List<string>();
			long total = 0;
			foreach (AudioClip clip in clips)
			{
				// samples x channels x 16 bits: what a decompressed clip occupies
				// in the bundle, not what the .wav on disk happens to weigh.
				long estimate = (long)Math.Round(clip.samples * clip.channels * 2.0);
				total += estimate;
				report.AppendLine("    " + clip.name + "  " + clip.length.ToString("F1", CultureInfo.InvariantCulture)
					+ "s  loadType=" + clip.loadType + "  channels=" + clip.channels
					+ "  [ESTIMATE] " + Bytes(estimate));
				if (clip.length > 30f) longClips.Add(clip.name + " (" + clip.length.ToString("F1", CultureInfo.InvariantCulture) + "s)");
				if (clip.loadType != AudioClipLoadType.DecompressOnLoad
					&& clip.loadType != AudioClipLoadType.CompressedInMemory)
				{
					wrongLoadType.Add(clip.name + " (" + clip.loadType + ")");
				}
			}
			report.AppendLine("  total [ESTIMATE] " + Bytes(total) + " decompressed, " + clips.Length + " clip(s)");
			if (wrongLoadType.Count > 0)
			{
				report.AppendLine("  loadType outside DecompressOnLoad / CompressedInMemory: "
					+ string.Join(", ", wrongLoadType.Take(MaxListLines).ToArray()));
				risks.Add(new Risk(70, "audio clip(s) on a streaming load type: "
					+ string.Join(", ", wrongLoadType.Take(3).ToArray()),
					"set Load Type to DecompressOnLoad (PC) or CompressedInMemory (Quest); VRChat warns on the others"));
			}
			if (longClips.Count > 0)
			{
				report.AppendLine("  clips over 30s: " + string.Join(", ", longClips.Take(MaxListLines).ToArray()));
				risks.Add(new Risk(55, longClips.Count + " audio clip(s) over 30s: "
					+ string.Join(", ", longClips.Take(3).ToArray()),
					"shorten to under 30s or split it; long clips are the most common cause of a PC-only world failing Quest"));
			}
		}

		private static void CheckTextures(StringBuilder report, List<Risk> risks)
		{
			report.AppendLine("--- textures ---");
			// Walked through the scene's MATERIALS, not with
			// FindObjectsOfType<Texture>: that call returns every texture the
			// Editor happens to have loaded, including UdonSharp's own and the
			// VRChat SDK's, so this report claimed 26 textures and a largest
			// dimension of 16 for a scene that references one. The scene's
			// textures are the ones its materials point at.
			Texture[] textures = SceneTextures();
			if (textures.Length == 0)
			{
				report.AppendLine("  no texture referenced by any material the scene's renderers use.");
				return;
			}
			int maxSize = 0;
			var unconfigured = new List<string>();
			var oversized = new List<string>();
			foreach (Texture texture in textures)
			{
				if (texture.width > maxSize) maxSize = texture.width;
				if (texture.height > maxSize) maxSize = texture.height;
				if (Math.Max(texture.width, texture.height) <= 2048) continue;
				oversized.Add(texture.name + " (" + texture.width + "x" + texture.height + ")");
				// Only a Texture2D has an importer, and the missing one is the
				// reason the platform cannot be configured, so it is counted as
				// unconfigured rather than skipped.
				TextureImporterPlatformSettings android = texture is Texture2D
					? PlatformSettings((Texture2D)texture, "Android")
					: null;
				if (android == null || !android.overridden)
				{
					unconfigured.Add(texture.name + " (" + texture.width + "x" + texture.height + ")");
				}
			}
			report.AppendLine("  textures the scene's materials reference: " + textures.Length
				+ "   largest dimension: " + maxSize);
			report.AppendLine("  (every Texture subclass counts, not just Texture2D: a RenderTexture or Cubemap a");
			report.AppendLine("  scene material points at is real upload cost.)");
			report.AppendLine("  over 2048: " + oversized.Count + "   of those, no Android platform override: " + unconfigured.Count);
			for (int i = 0; i < unconfigured.Count && i < MaxListLines; i++) report.AppendLine("    " + unconfigured[i]);
			if (unconfigured.Count > MaxListLines) report.AppendLine("    ... " + (unconfigured.Count - MaxListLines) + " more");
			if (unconfigured.Count > 0)
			{
				risks.Add(new Risk(80, unconfigured.Count + " texture(s) over 2048 with no Android compression override",
					"select each one and set the Android platform override (usually ASTC); without it Quest gets the full-size source"));
			}
		}

		/// The Android whitelist, preferring VRWorld Toolkit's list and falling
		/// back to the SDK's own, and saying which one answered.
		private static void CheckAndroidShaders(StringBuilder report, List<Risk> risks)
		{
			report.AppendLine("--- Android shaders ---");
			string[] whitelist = ShaderWhiteList(out string source);
			if (whitelist == null)
			{
				report.AppendLine("  not available: no Android shader whitelist resolved from "
					+ ToolkitValidationType + ".WorldShaderWhiteList or " + WorldValidationType + ".ShaderWhiteList");
				return;
			}
			var allowed = new HashSet<string>(whitelist, StringComparer.Ordinal);
			report.AppendLine("  whitelist from " + source + " (" + allowed.Count + " entries)");
			var materials = SceneMaterials();
			var offenders = new List<string>();
			foreach (Material material in materials)
			{
				if (material.shader == null) continue;
				if (allowed.Contains(material.shader.name)) continue;
				offenders.Add(material.name + " -> " + material.shader.name);
			}
			report.AppendLine("  materials in the scene: " + materials.Count
				+ "   on a NON-whitelisted shader: " + offenders.Count);
			for (int i = 0; i < offenders.Count && i < MaxListLines; i++) report.AppendLine("    " + offenders[i]);
			if (offenders.Count > MaxListLines) report.AppendLine("    ... " + (offenders.Count - MaxListLines) + " more");
			if (offenders.Count > 0)
			{
				risks.Add(new Risk(75, offenders.Count + " scene material(s) on a shader that is not Android-whitelisted: "
					+ string.Join(", ", offenders.Take(3).ToArray()),
					"swap to VRChat/Mobile/Standard Lite or Standard for Quest, or restrict those objects to PC-only"));
			}
		}

		private static void CheckMaterials(StringBuilder report, List<Risk> risks)
		{
			report.AppendLine("--- materials ---");
			List<Material> materials = SceneMaterials();
			if (materials.Count == 0)
			{
				report.AppendLine("  no material referenced by anything in the scene.");
				return;
			}
			var missing = new List<string>();
			foreach (Material material in materials)
			{
				if (material.shader != null) continue;
				missing.Add(material.name + " [" + AssetDatabase.GetAssetPath(material) + "]");
			}
			report.AppendLine("  materials in the scene: " + materials.Count + "   with a missing shader: " + missing.Count);
			for (int i = 0; i < missing.Count && i < MaxListLines; i++) report.AppendLine("    " + missing[i]);
			if (missing.Count > 0)
			{
				risks.Add(new Risk(90, missing.Count + " material(s) with no shader assigned (pink in the scene)",
					"assign a shader to each one; a null shader cannot be built and shows as magenta in-game"));
			}
		}

		/// World behaviours that need configuration before they do anything. Each
		/// sub-check reports which field it could not read rather than guessing.
		private static void CheckBehaviourConfiguration(StringBuilder report, List<Risk> risks, Scene scene)
		{
			report.AppendLine("--- world behaviour configuration ---");

			List<Component> stations = ComponentsInScene(scene, StationType);
			int unusableStations = 0;
			foreach (Component station in stations)
			{
				var empty = new List<string>();
				foreach (string field in new[] { "OnPreloadStation", "OnPostExitStation" })
				{
					object value = PiWorldToolkit.ReadFieldByName(station, field);
					if (value is string text && text.Length > 0) continue;
					if (!HasField(station, field))
					{
						empty.Add(field + "=<absent in this SDK version>");
						continue;
					}
					empty.Add(field + "=<empty>");
				}
				if (empty.Count == 0) continue;
				unusableStations++;
				report.AppendLine("    VRCStation \"" + station.gameObject.name + "\": " + string.Join(" ", empty.ToArray()));
			}
			if (stations.Count == 0) report.AppendLine("  VRCStation: none in the scene.");
			else if (unusableStations == 0) report.AppendLine("  VRCStation: " + stations.Count + ", all with player controllers set.");
			else
			{
				risks.Add(new Risk(40, unusableStations + " VRCStation(s) with an empty OnPreloadStation / OnPostExitStation",
					"pick a player prefab for each station; the station has nothing to put the player in"));
			}

			List<Component> pickups = ComponentsInScene(scene, PickupType);
			int childless = 0;
			foreach (Component pickup in pickups)
			{
				if (HasChildPickup(pickup)) continue;
				childless++;
				report.AppendLine("    VRCPickup \"" + pickup.gameObject.name + "\": no child pickup component");
			}
			if (pickups.Count == 0) report.AppendLine("  VRCPickup: none in the scene.");
			else if (childless == 0) report.AppendLine("  VRCPickup: " + pickups.Count + ", all with a child pickup.");
			else
			{
				risks.Add(new Risk(35, childless + " VRCPickup(s) with no child pickup",
					"the SDK3 pickup requires a child VRCPickup carrying the settings; without it nothing is pickable"));
			}

			List<Component> syncs = ComponentsInScene(scene, ObjectSyncType);
			int mismatched = 0;
			foreach (Component sync in syncs)
			{
				Rigidbody body = sync.gameObject.GetComponent<Rigidbody>();
				if (body == null) continue;
				object wantsKinematic = PiWorldToolkit.ReadFieldByName(sync, "rigidbodyIsKinematic");
				object assumesKinematic = PiWorldToolkit.ReadFieldByName(sync, "assumeKinematic");
				object wants = wantsKinematic ?? assumesKinematic;
				if (wants == null)
				{
					report.AppendLine("    VRCObjectSync \"" + sync.gameObject.name + "\": not available: this SDK version "
						+ "has neither a rigidbodyIsKinematic nor an assumeKinematic field to compare the Rigidbody against");
					continue;
				}
				if (!(wants is bool kinematic) || kinematic == body.isKinematic) continue;
				mismatched++;
				report.AppendLine("    VRCObjectSync \"" + sync.gameObject.name + "\": wants isKinematic=" + kinematic
					+ " but the Rigidbody is " + body.isKinematic);
			}
			if (syncs.Count == 0) report.AppendLine("  VRCObjectSync: none in the scene.");
			else if (mismatched == 0) report.AppendLine("  VRCObjectSync: " + syncs.Count + ", Rigidbody ownership consistent.");
			else
			{
				risks.Add(new Risk(65, mismatched + " VRCObjectSync(s) whose Rigidbody is not in the state the sync expects",
					"set the Rigidbody's Is Kinematic to match the sync's setting; ObjectSync and physics then fight over the object"));
			}
		}

		private static void CheckAudioSources(StringBuilder report, List<Risk> risks, Scene scene)
		{
			report.AppendLine("--- audio sources ---");
			AudioSource[] sources = UnityEngine.Object.FindObjectsOfType<AudioSource>(true);
			var silent = new List<string>();
			foreach (AudioSource source in sources)
			{
				if (source.clip != null) continue;
				silent.Add(source.gameObject.name + " (on " + DescribePath(source.transform) + ")");
			}
			report.AppendLine("  audio sources: " + sources.Length + "   with no clip: " + silent.Count);
			for (int i = 0; i < silent.Count && i < MaxListLines; i++) report.AppendLine("    " + silent[i]);
			if (silent.Count > 0)
			{
				report.AppendLine("  A source with no clip may be filled at runtime, so an empty one is only a");
				report.AppendLine("  finding if nothing ever assigns to it. Check the Udon that drives it.");
			}

			int reactive = 0;
			foreach (string typeName in new[] { "AudioLink.AudioReactiveLight", "AudioLink.AudioReactiveSurface",
				"AudioLink.AudioReactiveBlendshapes" })
			{
				reactive += ComponentsInScene(scene, typeName).Count;
			}
			if (reactive == 0) return;
			bool hasAudioLink = ComponentsInScene(scene, "AudioLink.AudioLink").Count > 0;
			if (hasAudioLink)
			{
				report.AppendLine("  AudioReactive components: " + reactive + ", with an AudioLink in the scene.");
				return;
			}
			report.AppendLine("  AudioReactive components: " + reactive + ", with NO AudioLink in the scene.");
			risks.Add(new Risk(85, reactive + " AudioReactive component(s) and no AudioLink instance in the scene",
				"add an AudioLink object; without it every AudioReactive light and surface stays dark"));
		}

		// =========================================================== reflection

		private static string[] ShaderWhiteList(out string source)
		{
			var allowed = new HashSet<string>(StringComparer.Ordinal);
			source = null;
			// VRWorld Toolkit's list is the Android one; the SDK's is the union
			// including PC-only shaders, so the toolkit's answer is preferred.
			if (VrcResolve.Has(ToolkitValidationType))
			{
				object raw = ReadStaticField(ToolkitValidationType, "WorldShaderWhiteList");
				if (Collect(raw, allowed)) source = ToolkitValidationType + ".WorldShaderWhiteList";
			}
			if (source == null && VrcResolve.Has(WorldValidationType))
			{
				object raw = ReadStaticField(WorldValidationType, "ShaderWhiteList");
				if (Collect(raw, allowed)) source = WorldValidationType + ".ShaderWhiteList";
			}
			if (source == null) return null;
			return new List<string>(allowed).ToArray();
		}

		private static bool Collect(object raw, HashSet<string> target)
		{
			if (raw is string[] names)
			{
				foreach (string name in names) target.Add(name);
				return names.Length > 0;
			}
			if (raw is IEnumerable sequence)
			{
				foreach (object entry in sequence)
				{
					string name = entry as string ?? entry?.ToString();
					if (!string.IsNullOrEmpty(name)) target.Add(name);
				}
				return target.Count > 0;
			}
			return false;
		}

		/// A public or private instance field's value, walking the base chain, by
		/// name so two SDK generations that spell a field differently both work.
		private static bool HasField(Component component, string fieldName)
		{
			if (component == null) return false;
			Type type = component.GetType();
			while (type != null)
			{
				if (type.GetField(fieldName, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance) != null)
					return true;
				type = type.BaseType;
			}
			return false;
		}

		private static object ReadStaticField(string typeName, string fieldName)
		{
			Type type = VrcResolve.Type(typeName);
			if (type == null) return null;
			try
			{
				FieldInfo field = type.GetField(fieldName, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static);
				return field == null ? null : field.GetValue(null);
			}
			catch (Exception)
			{
				return null;
			}
		}

		private static object ReadProperty(object target, string propertyName)
		{
			if (target == null) return null;
			Type type = target.GetType();
			while (type != null)
			{
				PropertyInfo property = type.GetProperty(propertyName, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
				if (property != null)
				{
					try { return property.GetValue(target); } catch (Exception) { return null; }
				}
				type = type.BaseType;
			}
			return null;
		}

		/// Invokes a resolved method and reports success separately from a null
		/// result: "returned null" and "did not run" have opposite meanings for a
		/// validator that returns an empty collection on success.
		private static bool Invoke(MethodInfo method, object target, object[] args, out object result)
		{
			result = null;
			if (method == null) return false;
			try
			{
				result = method.Invoke(target, args);
				return true;
			}
			catch (Exception error)
			{
				Debug.LogWarning("[pi] " + method.DeclaringType?.Name + "." + method.Name + " failed: "
					+ (error.InnerException ?? error).Message);
				return false;
			}
		}

		/// A validator's result as a sequence. Null is not a normal return, so it
		/// degrades to empty rather than throwing in the middle of a report.
		private static IEnumerable Enumerate(object value)
		{
			return value as IEnumerable ?? new object[0];
		}

		// ============================================================== scanning

		/// The open scene's roots, empty when there is no open scene. Every scan
		/// goes through here because GetRootGameObjects throws on an invalid Scene
		/// rather than returning nothing.
		private static List<GameObject> SceneRoots(Scene scene)
		{
			if (!scene.IsValid() || !scene.isLoaded) return new List<GameObject>();
			return new List<GameObject>(scene.GetRootGameObjects());
		}

		private static List<Component> ComponentsInScene(Scene scene, string typeName)
		{
			var found = new List<Component>();
			if (!VrcResolve.Has(typeName)) return found;
			var seen = new HashSet<Component>();
			foreach (GameObject root in SceneRoots(scene))
			{
				foreach (Component component in VrcResolve.ComponentsOn(root, typeName))
				{
					if (seen.Add(component)) found.Add(component);
				}
			}
			return found;
		}

		private static bool HasChildPickup(Component pickup)
		{
			Type type = pickup.GetType();
			foreach (Transform child in pickup.transform.GetComponentsInChildren<Transform>(true))
			{
				if (child.gameObject == pickup.gameObject) continue;
				if (child.GetComponent(type) != null) return true;
			}
			return false;
		}

		/// Materials the SCENE references, through its renderers. A project-wide
		/// material listing would report assets this world never uses.
		private static List<Material> SceneMaterials()
		{
			var materials = new List<Material>();
			var seen = new HashSet<Material>();
			foreach (Renderer renderer in UnityEngine.Object.FindObjectsOfType<Renderer>(true))
			{
				foreach (Material material in renderer.sharedMaterials)
				{
					if (material == null) continue;
					if (seen.Add(material)) materials.Add(material);
				}
			}
			return materials;
		}

		/// The textures those materials point at, deduplicated.
		///
		/// The same walk as PiCensus.SceneTextures, and for the same reason: the
		/// two reports have to agree about what the scene contains, and a
		/// project-wide Texture search does not answer that question.
		private static Texture[] SceneTextures()
		{
			var textures = new List<Texture>();
			var seen = new HashSet<Texture>();
			foreach (Material material in SceneMaterials())
			{
				foreach (string slot in material.GetTexturePropertyNames())
				{
					Texture value = material.GetTexture(slot);
					if (value == null || !seen.Add(value)) continue;
					textures.Add(value);
				}
			}
			return textures.ToArray();
		}

		private static TextureImporterPlatformSettings PlatformSettings(Texture2D texture, string platform)
		{
			try
			{
				TextureImporter importer = AssetImporter.GetAtPath(AssetDatabase.GetAssetPath(texture)) as TextureImporter;
				return importer == null ? null : importer.GetPlatformTextureSettings(platform);
			}
			catch (Exception)
			{
				return null;
			}
		}

		/// An asset's on-disk length, or -1 when the path is not a measurable file
		/// under Assets/ (a package asset, or a path Unity does not expose).
		private static long AssetFileSize(string assetPath)
		{
			if (string.IsNullOrEmpty(assetPath)) return -1;
			if (!assetPath.StartsWith("Assets/", StringComparison.OrdinalIgnoreCase)) return -1;
			try
			{
				string root = Directory.GetParent(Application.dataPath).FullName;
				FileInfo info = new FileInfo(Path.Combine(root, assetPath));
				if (!info.Exists) return -1;
				return info.Length;
			}
			catch (Exception)
			{
				return -1;
			}
		}

		private static readonly HashSet<string> KnownExtensions = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
		{
			".png", ".jpg", ".jpeg", ".tga", ".psd", ".bmp", ".exr", ".tif",
			".wav", ".mp3", ".ogg", ".aif", ".aiff",
			".fbx", ".obj", ".dae", ".mat",
		};

		private static string Bytes(long value)
		{
			double size = value;
			string[] units = { "B", "KB", "MB", "GB" };
			int unit = 0;
			while (size >= 1024 && unit < units.Length - 1) { size /= 1024; unit++; }
			return size.ToString(unit == 0 ? "F0" : "F1", CultureInfo.InvariantCulture) + " " + units[unit];
		}

		private static string DescribePath(Transform transform)
		{
			var names = new List<string>();
			for (Transform current = transform; current != null; current = current.parent) names.Insert(0, current.name);
			return string.Join("/", names.ToArray());
		}

		private static void AppendField(StringBuilder line, Component component, string fieldName)
		{
			object value = PiWorldToolkit.ReadFieldByName(component, fieldName);
			if (value == null && !HasField(component, fieldName)) return;
			if (line.Length > 0) line.Append("  ");
			line.Append(fieldName).Append('=').Append(value == null ? "<null>" : value.ToString());
		}

		private static void AppendProperty(StringBuilder line, object target, string propertyName)
		{
			object value = ReadProperty(target, propertyName);
			if (value == null) return;
			if (line.Length > 0) line.Append("  ");
			line.Append(propertyName).Append('=').Append(value);
		}

		// ============================================================== reporting

		private sealed class Risk
		{
			public readonly int Score;
			public readonly string Finding;
			public readonly string Fix;

			public Risk(int score, string finding, string fix)
			{
				Score = score;
				Finding = finding;
				Fix = fix;
			}
		}
	}
}