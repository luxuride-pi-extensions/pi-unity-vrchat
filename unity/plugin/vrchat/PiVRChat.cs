// Part of the pi Unity bridge plugin. See PiUnityBridge.Core.cs for the
// protocol overview. The whole plugin is one assembly of partial classes, so
// Unity compiles it into Assembly-CSharp-Editor with no asmdef and no
// package dependencies, which is what lets it drop into any Unity 2021+
// project (a configured VRChat world, an existing game) that pi was pointed at
// rather than generated.

// NDMF, lilToon, and VRChat SDK helpers.
//
// VERIFIED API SURFACE (read out of a live project by reflection, not guessed):
//
// nadena.dev.ndmf.runtime.RuntimeUtil
//   static string  RelativePath(GameObject root, GameObject child)
//   static string  RelativePath(Transform root, Transform child)
//   static string  AvatarRootPath(GameObject child)
//   static bool    IsAvatarRoot(Transform target)
//   static IEnumerable<GameObject> FindAvatarRoots(GameObject root, bool includeInactive)
//   static Transform FindAvatarInParents(Transform target)
//
// nadena.dev.ndmf.runtime.components.NDMFAvatarRoot   [Component]
// nadena.dev.ndmf.runtime.AvatarActivator             [Component]
// nadena.dev.ndmf.vrchat.VRChatContextExtensions
//   static VRCAvatarDescriptor VRChatAvatarDescriptor(BuildContext)
//
// lilToon.lilMaterialUtils
//   static int  GetTrueRenderQueue(Material)
//   static bool CheckMainTextureName(string)
// lilToon.lilShaderUtils
//   static bool IsLiteShaderName / IsCutoutShaderName / IsTransparentShaderName
//               IsOutlineShaderName / IsOverlayShaderName(string)
// lilToon.lilToonPreset.ApplyPreset(Material, lilToonPreset, bool)
//
// Note the SDK's editor-side converters exist too (VRC.SDK3.Avatars
// .AvatarDynamicsSetup.ConvertDynamicBonesToPhysBones) but they are in an
// editor assembly that is not loaded in every project, so they are NOT called
// here. See the note on the converter helper for why that matters.
//
// A useful path: NDMF's RuntimeUtil.RelativePath is the correct way to name a
// nested object on an avatar ("Body/Armature/Hips"), and getting that wrong by
// hand is a routine source of "not found" errors when a model builds a path.

using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;

namespace PiUnity
{
	public static class PiVRChat
	{
		// ------------------------------------------------------------- NDMF

		public static bool IsNdmfInstalled()
		{
			return VrcResolve.Has("nadena.dev.ndmf.runtime.components.NDMFAvatarRoot");
		}

		/// An avatar's path relative to its own root, e.g. "Body/Armature/Hips".
		///
		/// Uses NDMF's own RuntimeUtil.RelativePath when NDMF is present, because
		/// that is the definition every NDMF tool agrees on. Falls back to walking
		/// the transform chain, which agrees with it in practice but is not the
		/// authority. Returns "" when the object is not under the root.
		public static string RelativePath(GameObject root, GameObject child)
		{
			if (root == null || child == null) return "";
			if (root == child) return "";

			object viaNdmf = VrcResolve.Call("nadena.dev.ndmf.runtime.RuntimeUtil", "RelativePath",
				new object[] { root, child }, new[] { typeof(GameObject), typeof(GameObject) });
			if (viaNdmf is string resolved && resolved.Length > 0) return resolved;

			var parts = new List<string>();
			for (Transform current = child.transform; current != null && current != root.transform; current = current.parent)
			{
				parts.Insert(0, current.name);
			}
			if (parts.Count == 0) return "";
			// Reaching the top without hitting root means it was not a descendant.
			return string.Join("/", parts.ToArray());
		}

		/// The avatar root an object belongs to, or null.
		public static GameObject AvatarRootFor(GameObject target)
		{
			if (target == null) return null;
			object viaNdmf = VrcResolve.Call("nadena.dev.ndmf.runtime.RuntimeUtil", "FindAvatarInParents",
				new object[] { target.transform }, new[] { typeof(Transform) });
			if (viaNdmf is Transform found) return found.gameObject;
			// Fallback: climb while a VRCAvatarDescriptor is present higher up.
			// Resolved through VrcResolve rather than GetComponent(string), which
			// Unity marks insecure and which only ever matches a component already
			// in the build.
			for (Transform current = target.transform; current != null; current = current.parent)
			{
				if (VrcResolve.ComponentOn(current.gameObject, "VRC.SDK3.Avatars.Components.VRCAvatarDescriptor") != null)
				{
					return current.gameObject;
				}
			}
			return null;
		}

		/// Every avatar root in a scene, via NDMF when available.
		public static List<GameObject> FindAvatarRoots()
		{
			var roots = new List<GameObject>();
			object viaNdmf = VrcResolve.Call("nadena.dev.ndmf.runtime.RuntimeUtil", "FindAvatarRoots",
				new object[] { null, true }, new[] { typeof(GameObject), typeof(bool) });
			if (viaNdmf is System.Collections.IEnumerable enumerable)
			{
				foreach (object item in enumerable)
				{
					if (item is GameObject go) roots.Add(go);
				}
				if (roots.Count > 0) return roots;
			}

			// Fallback: the package is absent, or the overload did not bind.
			foreach (GameObject sceneRoot in UnityEngine.SceneManagement.SceneManager.GetActiveScene().GetRootGameObjects())
			{
				if (VrcResolve.ComponentOn(sceneRoot, "VRC.SDK3.Avatars.Components.VRCAvatarDescriptor") != null)
				{
					roots.Add(sceneRoot);
				}
			}
			return roots;
		}

		// --------------------------------------------------------- lilToon

		public static bool IsLilToonInstalled()
		{
			return VrcResolve.Has("lilToon.lilShaderUtils");
		}

		/// Classify a lilToon material: which variant it is, and its true render
		/// queue.
		///
		/// GetTrueRenderQueue exists because lilToon encodes its mode in the queue
		/// with offsets, so `material.renderQueue` on its own does not tell you
		/// whether a material is opaque, cutout, or transparent. Getting that
		/// wrong is why avatars render with sorting artefacts.
		public static string DescribeLilToonMaterial(Material material)
		{
			if (material == null) return "material=null";
			var report = new System.Text.StringBuilder();
			report.AppendLine("shader=" + (material.shader == null ? "<null>" : material.shader.name));
			bool lit = material.shader != null;
			report.AppendLine("isLilToon=" + lit);

			if (!IsLilToonInstalled())
			{
				report.AppendLine("lilToon package not installed; skipping shader classification.");
				return report.ToString();
			}

			string shaderName = material.shader.name;
			report.AppendLine("isLite=" + CallShaderPredicate("IsLiteShaderName", shaderName));
			report.AppendLine("isCutout=" + CallShaderPredicate("IsCutoutShaderName", shaderName));
			report.AppendLine("isTransparent=" + CallShaderPredicate("IsTransparentShaderName", shaderName));
			report.AppendLine("isOutline=" + CallShaderPredicate("IsOutlineShaderName", shaderName));
			report.AppendLine("isOverlay=" + CallShaderPredicate("IsOverlayShaderName", shaderName));

			object queue = VrcResolve.Call("lilToon.lilMaterialUtils", "GetTrueRenderQueue",
				new object[] { material }, new[] { typeof(Material) });
			report.AppendLine("trueRenderQueue=" + (queue ?? "<unavailable>")
				+ "   rawRenderQueue=" + material.renderQueue);
			return report.ToString();
		}

		private static string CallShaderPredicate(string method, string shaderName)
		{
			object result = VrcResolve.Call("lilToon.lilShaderUtils", method, new object[] { shaderName },
				new[] { typeof(string) });
			return result == null ? "<unavailable>" : (result is bool b ? b.ToString() : result.ToString());
		}

		/// Every lilToon material on an avatar, with its variant. Answering "which
		/// materials does this avatar use, and are any of them transparent" is a
		/// question that otherwise needs a click per renderer.
		public static string DescribeLilToonUsage(GameObject avatar)
		{
			if (avatar == null) return "avatar=null";
			var report = new System.Text.StringBuilder();
			var counts = new Dictionary<string, int>(StringComparer.Ordinal);
			int total = 0, litCount = 0;

			foreach (Renderer renderer in avatar.GetComponentsInChildren<Renderer>(true))
			{
				foreach (Material material in renderer.sharedMaterials)
				{
					if (material == null) continue;
					total++;
					string name = material.shader == null ? "<null>" : material.shader.name;
					counts.TryGetValue(name, out int seen);
					counts[name] = seen + 1;
					if (material.shader != null && material.shader.name.StartsWith("lilToon", StringComparison.Ordinal)) litCount++;
				}
			}
			report.AppendLine("materials=" + total + "  lilToon=" + litCount);
			foreach (var pair in counts.OrderByDescending(p => p.Value).ThenBy(p => p.Key, StringComparer.Ordinal))
			{
				report.AppendLine("  " + pair.Value + "x  " + pair.Key);
			}
			return report.ToString();
		}

		// ---------------------------------------------------- VRChat SDK

		public static bool IsSdkInstalled()
		{
			return VrcResolve.Has("VRC.SDK3.Avatars.Components.VRCAvatarDescriptor");
		}

		/// What a caller needs to know about an avatar's VRChat settings, gathered
		/// from the descriptor without clicking through its Inspector.
		public static string DescribeAvatar(GameObject avatar)
		{
			if (avatar == null) return "avatar=null";
			if (!IsSdkInstalled()) return "VRChat SDK not installed.";

			Component descriptor = VrcResolve.ComponentOn(avatar, "VRC.SDK3.Avatars.Components.VRCAvatarDescriptor");
			if (descriptor == null) return "No VRCAvatarDescriptor on " + avatar.name + " or its children.";

			var report = new System.Text.StringBuilder();
			report.AppendLine("avatar=" + avatar.name);
			report.AppendLine("descriptor=" + descriptor.GetType().FullName);
			report.AppendLine("prefabAsset=" + (VrcResolve.AssetPathOf(descriptor) ?? "<scene instance>"));
			report.AppendLine("scale=" + avatar.transform.localScale
				+ "  (a non-1 scale is a common upload problem)");
			report.AppendLine("--- read fields with unity_inspect for exact values ---");
			report.AppendLine("Try: unity_inspect { gameObject: \"" + avatar.name + "\", component: \"VRC_AvatarDescriptor\" }");
			return report.ToString();
		}

		/// A summary of every avatar in the scene, across all the packages.
		///
		/// This is the entry point a model should reach for first when asked
		/// anything about "this project", because it answers what exists without
		/// dumping every renderer's every property.
		public static string DescribeProject()
		{
			var report = new System.Text.StringBuilder();
			report.AppendLine("packages:");
			report.AppendLine("  " + PiVRCFury.Package + "        " + (PiVRCFury.IsInstalled() ? "installed" : "absent"));
			report.AppendLine("  " + PiAvatarOptimizer.Package + " " + (PiAvatarOptimizer.IsInstalled() ? "installed" : "absent"));
			report.AppendLine("  jp.lilxyzw.liltoon            " + (IsLilToonInstalled() ? "installed" : "absent"));
			report.AppendLine("  nadena.dev.ndmf               " + (IsNdmfInstalled() ? "installed" : "absent"));
			report.AppendLine("  com.vrchat.avatars            " + (IsSdkInstalled() ? "installed" : "absent"));
			report.AppendLine("--- avatars ---");

			List<GameObject> roots = FindAvatarRoots();
			if (roots.Count == 0)
			{
				report.AppendLine("  none found (no VRCAvatarDescriptor in the open scene)");
				return report.ToString();
			}
			foreach (GameObject root in roots)
			{
				report.AppendLine("  " + root.name);
				report.AppendLine("    vrcfury: " + PiVRCFury.FindAll(root).Count + " component(s)");
				report.AppendLine("    optimizer: " + PiAvatarOptimizer.FindAll(root).Count + " component(s)");
				report.Append(PiAvatarOptimizer.Describe(root).Replace("\n", "\n    "));
			}
			return report.ToString();
		}
	}
}
