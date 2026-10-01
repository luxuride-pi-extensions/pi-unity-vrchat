// Part of the pi Unity bridge plugin. See PiUnityBridge.Core.cs for the
// protocol overview. The whole plugin is one assembly of partial classes, so
// Unity compiles it into Assembly-CSharp-Editor with no asmdef and no
// package dependencies, which is what lets it drop into any Unity 2021+
// project (a configured VRChat world, an existing game) that pi was pointed at
// rather than generated.

// VRWorld Toolkit helpers.
//
// WHY EVERY CALL HERE IS BY REFLECTION
// -------------------------------------
// VRWorld Toolkit is an optional editor-only package, split across
// assemblies that vary by version, and most of its useful entry points are
// MenuItem methods that are private and validate-gated. A compile-time
// reference would make this plugin fail to compile in every project WITHOUT
// the package -- the exact failure mode the plugin's no-assembly-definition
// design exists to avoid. So the package is probed at runtime and a missing
// package degrades to an explanatory message instead of a build error.
//
// The type and member names below were verified live against VRWorld Toolkit
// 3.4.1 (assembly VRWorldToolkit.Editor). Public members only: a private
// method reached through BindingFlags.NonPublic still exists in 3.4.1 and is
// still gone in the next version, and a call that cannot bind must fail as a
// message rather than as a MissingMethodException from a bridge request.
//
// VRWorld Toolkit's own checkers (its World Debugger) already cover "is this
// world efficient". What it does NOT cover is "is this project SET UP", which
// is the question a world author asks before anything else: are the VRChat
// layers and collision matrix in place, is post-processing wired to the
// reference camera, is the scene's build state coherent.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace PiUnity
{
	public static class PiWorldToolkit
	{
		/// dev.onevr.vrworldtoolkit. Probed by assembly name because the
		/// namespace prefix is the package's own name, which a rename would change.
		public const string Package = "dev.onevr.vrworldtoolkit";

		private const string QuickFunctionsType = "VRWorldToolkit.Editor.QuickFunctions";
		private const string SetupType = "VRWorldToolkit.Editor.PostProcessingTools";
		private const string ScriptingDefineType = "VRWorldToolkit.Editor.ScriptingDefineManager";
		private const string DisableOnBuildType = "VRWorldToolkit.Editor.DisableOnBuildManager";
		private const string ValidationType = "VRWorldToolkit.Editor.Validation";

		/// VRCCore-Editor, not the world SDK. UpdateLayers is the SDK's OWN layer
		/// setup (the "Builder" tab), so it is available whether or not VRWorld
		/// Toolkit is installed -- which is why it is not resolved through it.
		private const string UpdateLayersType = "UpdateLayers";

		/// Calls a package static method, returning false when the package or the
		/// signature is not there. A method whose signature changed across
		/// versions must degrade, not throw: the caller has a report to write
		/// and "could not call it" is one line of that report.
		private static bool CallStatic(string typeName, string methodName, object[] args, Type[] parameterTypes, out object result)
		{
			result = null;
			try
			{
				MethodInfo method = VrcResolve.StaticMethod(typeName, methodName, parameterTypes);
				if (method == null) return false;
				result = method.Invoke(null, args);
				return true;
			}
			catch (Exception error)
			{
				Debug.LogWarning("[pi] " + typeName + "." + methodName + " failed: " + (error.InnerException ?? error).Message);
				return false;
			}
		}

		public static bool IsInstalled()
		{
			return VrcResolve.Type(QuickFunctionsType) != null;
		}

		// ------------------------------------------------------ project state

		/// Layers and collision matrix, by asking the SDK rather than by
		/// inferring from the layer names present.
		///
		/// Inferring is tempting and wrong: a layer name can exist while its
		/// collision row is still the default, and VRChat silently drops
		/// interactions that collide with nothing. The SDK's own predicates are
		/// the only honest source.
		public static string DescribeProjectState()
		{
			var report = new StringBuilder();
			report.AppendLine("--- VRChat project layers ---");
			object layersSetup = null;
			if (!CallStatic(UpdateLayersType, "AreLayersSetup", new object[0], Type.EmptyTypes, out layersSetup))
			{
				report.AppendLine("  " + UpdateLayersType + " not found (com.vrchat.base not loaded?)");
			}
			else
			{
				report.AppendLine("  layers=" + Describe(layersSetup)
					+ "  collision matrix=" + Describe(CallBool("IsCollisionLayerMatrixSetup")));
				if (!IsTrue(layersSetup))
				{
					report.AppendLine("  NOT SET UP. Run action \"setup-layers\" (or VRChat's own Builder tab) before building:");
					report.AppendLine("  a world built on the wrong layer setup has objects that pass through each other");
					report.AppendLine("  and colliders VRChat will not honour.");
				}
			}

			report.AppendLine("--- post-processing ---");
			report.Append(PostProcessingState());

			report.AppendLine("--- quest whitelist (VRWorld Toolkit 3.4.1) ---");
			report.Append(QuestWhitelist());
			return report.ToString();
		}

		private static string PostProcessingState()
		{
			var report = new StringBuilder();
			Type layer = VrcResolve.Type("UnityEngine.Rendering.PostProcessing.PostProcessLayer");
			if (layer == null)
			{
				report.AppendLine("  com.unity.postprocessing is NOT installed, so a world has no post-processing at all.");
				return report.ToString();
			}
			int onCamera = 0;
			var references = new List<string>();
			foreach (Transform child in UnityEngine.Object.FindObjectsOfType<Transform>(true))
			{
				foreach (Component component in child.GetComponents(layer))
				{
					onCamera++;
					references.Add(child.gameObject.name);
				}
			}
			Component descriptor = PiUdon.SceneDescriptor();
			string referenceCamera = ReadFieldByName(descriptor, "ReferenceCamera")?.ToString() ?? "";
			report.AppendLine("  PostProcessLayer components in the scene: " + onCamera
				+ (onCamera == 0 ? "" : " on " + string.Join(", ", references.ToArray())));
			report.AppendLine("  descriptor ReferenceCamera=" + (referenceCamera.Length == 0 ? "<not set>" : referenceCamera));
			if (onCamera == 0)
			{
				report.AppendLine("  No camera carries a Post Process Layer. VRChat requires one on the reference camera");
				report.AppendLine("  for ANY post-processing to take effect in-game; a PostProcessVolume alone does nothing.");
			}
			if (descriptor != null && referenceCamera.Length == 0)
			{
				report.AppendLine("  The descriptor has no reference camera. Run action \"setup-post-processing\" to add the");
				report.AppendLine("  layer and point the descriptor at the main camera, or VRChat ignores post-processing entirely.");
			}
			return report.ToString();
		}

		/// The materials that cannot build for Android, read from the package's
		/// own whitelist so the list cannot drift from the SDK's.
		private static string QuestWhitelist()
		{
			Type validation = VrcResolve.Type(ValidationType);
			if (validation == null) return "  " + Package + " is not installed, so there is no whitelist to check against.\n";
			var report = new StringBuilder();
			object raw = null;
			try
			{
				FieldInfo field = validation.GetField("WorldShaderWhiteList", BindingFlags.Public | BindingFlags.Static);
				if (field == null) return "  " + ValidationType + ".WorldShaderWhiteList is absent in this version.\n";
				raw = field.GetValue(null);
			}
			catch (Exception error)
			{
				return "  whitelist unreadable: " + error.Message + "\n";
			}
			var allowed = new HashSet<string>(StringComparer.Ordinal);
			if (raw is string[] names) foreach (string name in names) allowed.Add(name);
			else return "  whitelist has an unexpected shape: " + (raw == null ? "null" : raw.GetType().FullName) + "\n";

			var materials = new List<Material>();
			foreach (Material material in UnityEngine.Object.FindObjectsOfType<Material>())
			{
				if (material == null || material.shader == null) continue;
				if (materials.Contains(material)) continue;
				materials.Add(material);
			}
			report.AppendLine("  whitelisted shaders for Android: " + allowed.Count);
			report.AppendLine("  materials in the scene: " + materials.Count);
			var offenders = new List<string>();
			foreach (Material material in materials)
			{
				string shader = material.shader.name;
				if (allowed.Contains(shader)) continue;
				string path = AssetDatabase.GetAssetPath(material);
				offenders.Add(material.name + " -> " + shader + (path.Length == 0 ? "" : " [" + path + "]"));
			}
			report.AppendLine("  materials on a NON-whitelisted shader: " + offenders.Count);
			foreach (string offender in offenders.Take(20)) report.AppendLine("    " + offender);
			if (offenders.Count > 20) report.AppendLine("    ... " + (offenders.Count - 20) + " more");
			if (offenders.Count == 0) report.AppendLine("  Every material uses an Android-safe shader.");
			report.AppendLine("  Caveat: this checks scene materials only. A material in a prefab that is not");
			report.AppendLine("  loaded in this scene is not in this count, and VRChat checks every one at build time.");
			return report.ToString();
		}

		// ------------------------------------------------------- mutating

		/// Sets up the VRChat layers and collision matrix, then reports what the
		/// SDK says afterwards.
		public static string SetupLayers()
		{
			var report = new StringBuilder();
			object layersBefore = null;
			if (!CallStatic(UpdateLayersType, "AreLayersSetup", new object[0], Type.EmptyTypes, out layersBefore))
			{
				throw new Exception(UpdateLayersType + " not found. com.vrchat.base must be installed; "
					+ "this is the VRChat SDK's own layer setup, used by VRChat's Builder tab.");
			}
			report.AppendLine("layers setup before=" + Describe(layersBefore)
				+ "  collision matrix=" + Describe(CallBool("IsCollisionLayerMatrixSetup")));
			if (!CallStatic(UpdateLayersType, "SetupEditorLayers", new object[0], Type.EmptyTypes, out object layersWrite)
				|| !CallStatic(UpdateLayersType, "SetupCollisionLayerMatrix", new object[0], Type.EmptyTypes, out object matrixWrite))
			{
				throw new Exception("Could not call " + UpdateLayersType + ".SetupEditorLayers / SetupCollisionLayerMatrix. "
					+ "Check the Editor log for the reason.");
			}
			object layersAfter = null;
			CallStatic(UpdateLayersType, "AreLayersSetup", new object[0], Type.EmptyTypes, out layersAfter);
			report.AppendLine("layers setup after=" + Describe(layersAfter)
				+ "  collision matrix=" + Describe(CallBool("IsCollisionLayerMatrixSetup")));
			if (!IsTrue(layersAfter)) report.AppendLine("Still reports not set up; see the Editor log.");
			return report.ToString();
		}

		/// The post-processing setup, done through the package's own public
		/// entry point rather than by assembling the pieces here.
		///
		/// SetupPostProcessingGenerics is the one public member of that class:
		/// it adds the layer, copies the profile and creates the volume. What it
		/// does NOT do is point the scene descriptor's ReferenceCamera at a
		/// camera, which is the step that decides whether any of it works in
		/// game -- so that is done here, and only when it is currently unset,
		/// so an already-configured world is not repointed at whatever camera
		/// happens to be tagged Main.
		public static string SetupPostProcessing()
		{
			var report = new StringBuilder();
			Component descriptor = PiUdon.SceneDescriptor();
			if (descriptor == null)
			{
				throw new Exception("No scene descriptor, so there is no reference camera for post-processing to run on. "
					+ "Add the VRCWorld prefab first, then run this again.");
			}
			Camera camera = null;
			object existing = ReadFieldByName(descriptor, "ReferenceCamera");
			if (existing is GameObject referenced) camera = referenced.GetComponent<Camera>();
			if (camera == null)
			{
				camera = Camera.main;
				report.AppendLine("descriptor had no reference camera; using Camera.main");
				if (camera == null)
				{
					throw new Exception("No reference camera on the descriptor and no camera tagged MainCamera in the scene. "
						+ "Create or tag a camera, then run this again -- VRChat renders post-processing from the "
						+ "descriptor's reference camera only.");
				}
				if (!WriteFieldByName(descriptor, "ReferenceCamera", camera.gameObject))
				{
					report.AppendLine("WARNING: this SDK's descriptor has no ReferenceCamera field, so it was not set.");
					report.AppendLine("Set it in the Inspector on " + descriptor.gameObject.name + ".");
				}
			}
			else
			{
				report.AppendLine("descriptor reference camera=" + camera.name + " (left as it was)");
			}

			if (!CallStatic(SetupType, "SetupPostProcessingGenerics", new object[] { camera.gameObject },
					new[] { typeof(GameObject) }, out object ignored))
			{
				throw new Exception(SetupType + ".SetupPostProcessingGenerics could not be called. It needs "
					+ "com.unity.postprocessing (VRWorld Toolkit's own dependency) and UNITY_POST_PROCESSING_STACK_V2 "
					+ "defined for the active build target group.");
			}
			report.AppendLine("added the post process layer, profile and volume to " + camera.name);
			Scene scene = SceneManager.GetActiveScene();
			if (scene.IsValid()) EditorSceneManager.MarkSceneDirty(scene);
			report.AppendLine(PostProcessingState());
			report.AppendLine("Post-processing renders at BUILD time in a world too; the Scene view shows it only");
			report.AppendLine("with the Post Processing effect enabled on the Scene view camera.");
			return report.ToString();
		}

		/// Enables or disables every object carrying a tag, the way the toolkit's
		/// build toggles do.
		///
		/// Tag-scoped on purpose: VRWorld Toolkit's own build callbacks use tags to
		/// mark what exists on PC only and what on Android only, so this is the
		/// operation a Quest/PC switch needs, and it is the one an author can
		/// verify by looking at the hierarchy.
		public static string ToggleTag(string tag, bool active)
		{
			if (string.IsNullOrEmpty(tag)) throw new Exception("toggle-tag requires a tag name.");
			var report = new StringBuilder();
			if (!CallStatic(DisableOnBuildType, "ToggleObjectsUsingTag", new object[] { tag, active, true },
					new[] { typeof(string), typeof(bool), typeof(bool) }, out object ignored))
			{
				throw new Exception(DisableOnBuildType + ".ToggleObjectsUsingTag is not available in this version of "
					+ Package + ". Toggling the objects tagged \"" + tag + "\" by hand is the fallback.");
			}
			int found = 0;
			foreach (Transform child in UnityEngine.Object.FindObjectsOfType<Transform>(true))
			{
				if (child.gameObject.tag == tag) found++;
			}
			report.AppendLine("objects tagged \"" + tag + "\": " + found + ", set active=" + active);
			if (found == 0)
			{
				report.AppendLine("Nothing in this scene carries that tag, so nothing changed.");
				report.AppendLine("Tags that matter for a PC/Quest split are the ones the SDK's Builder sets up,");
				report.AppendLine("plus whatever tag your own objects use for per-platform work.");
			}
			Scene scene = SceneManager.GetActiveScene();
			if (scene.IsValid()) EditorSceneManager.MarkSceneDirty(scene);
			return report.ToString();
		}

		// ------------------------------------------------------- reflection helpers

		/// Reads a public or private instance field by name, tolerating a type
		/// that has no such field -- two SDK generations disagree on the names.
		internal static object ReadFieldByName(Component component, string fieldName)
		{
			if (component == null) return null;
			Type type = component.GetType();
			while (type != null)
			{
				FieldInfo field = type.GetField(fieldName, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
				if (field != null)
				{
					try { return field.GetValue(component); } catch (Exception) { return null; }
				}
				type = type.BaseType;
			}
			return null;
		}

		internal static bool WriteFieldByName(Component component, string fieldName, object value)
		{
			if (component == null) return false;
			Type type = component.GetType();
			while (type != null)
			{
				FieldInfo field = type.GetField(fieldName, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
				if (field != null)
				{
					try
					{
						field.SetValue(component, value);
						EditorUtility.SetDirty(component);
						PrefabUtility.RecordPrefabInstancePropertyModifications(component);
						return true;
					}
					catch (Exception error)
					{
						Debug.LogWarning("[pi] " + component.GetType().Name + "." + fieldName + ": " + error.Message);
						return false;
					}
				}
				type = type.BaseType;
			}
			return false;
		}

		private static object CallBool(string methodName)
		{
			return CallStatic(UpdateLayersType, methodName, new object[0], Type.EmptyTypes, out object result) ? result : "<unavailable>";
		}

		/// A bool field or predicate result, rendered for a report.
		private static string Describe(object value)
		{
			return value == null ? "<unavailable>" : (value is bool flag ? (flag ? "SETUP" : "NOT SET UP") : value.ToString());
		}

		private static bool IsTrue(object value) => value is bool flag && flag;
	}
}