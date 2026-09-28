// Part of the pi Unity bridge plugin. See PiUnityBridge.Core.cs for the
// protocol overview. The whole plugin is one assembly of partial classes, so
// Unity compiles it into Assembly-CSharp-Editor with no asmdef and no
// package dependencies, which is what lets it drop into any Unity 2021+
// project (a configured VRChat world, an existing game) that pi was pointed at
// rather than generated.

// Avatar Optimizer (anatawa12) helpers.
//
// Components verified present in a live project, all runtime-side so they are
// what actually executes on upload:
//
//   Anatawa12.AvatarOptimizer.TraceAndOptimize                 <- the main one
//   Anatawa12.AvatarOptimizer.MergeSkinnedMesh
//   Anatawa12.AvatarOptimizer.MergePhysBone
//   Anatawa12.AvatarOptimizer.RemoveMeshByMask
//   Anatawa12.AvatarOptimizer.RemoveMeshByBlendShape
//   Anatawa12.AvatarOptimizer.RemoveMeshInBox
//   Anatawa12.AvatarOptimizer.EditSkinnedMeshComponent
//   Anatawa12.AvatarOptimizer.AvatarGlobalComponent
//   Anatawa12.AvatarOptimizer.AvatarTagComponent
//
// Optimizer API surface also verified (com.anatawa12.avatar-optimizer.api.editor):
//   MeshRemovalProvider.GetForRenderer(SkinnedMeshRenderer)
//   UVUsageCompabilityAPI.IsTexCoordUsed(SkinnedMeshRenderer, int channel)
//   ShaderInformationRegistry.RegisterShaderInformation(...)
//
// The important operational fact: these components do NOTHING in the Editor.
// They run inside NDMF at build time, when the avatar is uploaded. Adding
// TraceAndOptimize here changes what the UPLOAD produces, not the scene. A model
// that expects the mesh to merge immediately will be confused, so that is stated
// in the docs the helpers return.

using System;
using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEngine;

namespace PiUnity
{
	public static class PiAvatarOptimizer
	{
		public const string Package = "com.anatawa12.avatar-optimizer";

		/// Optimizer components, with what each one is for. "role" is carried as
		/// data so the caller can explain the finding instead of a model guessing
		/// from a type name.
		public static readonly string[][] KnownComponents =
		{
			new[] { "TraceAndOptimize", "Anatawa12.AvatarOptimizer.TraceAndOptimize", "runs the whole optimizer pass at build time" },
			new[] { "MergeSkinnedMesh", "Anatawa12.AvatarOptimizer.MergeSkinnedMesh", "merges meshes sharing a material" },
			new[] { "MergePhysBone", "Anatawa12.AvatarOptimizer.MergePhysBone", "merges PhysBone chains" },
			new[] { "RemoveMeshByMask", "Anatawa12.AvatarOptimizer.RemoveMeshByMask", "removes mesh by a mask weight" },
			new[] { "RemoveMeshByBlendShape", "Anatawa12.AvatarOptimizer.RemoveMeshByBlendShape", "removes mesh by a blendshape" },
			new[] { "RemoveMeshInBox", "Anatawa12.AvatarOptimizer.RemoveMeshInBox", "removes mesh inside a box collider" },
			new[] { "EditSkinnedMesh", "Anatawa12.AvatarOptimizer.EditSkinnedMeshComponent", "edits mesh vertices" },
			new[] { "AvatarGlobal", "Anatawa12.AvatarOptimizer.AvatarGlobalComponent", "global optimizer settings" },
			new[] { "AvatarTag", "Anatawa12.AvatarOptimizer.AvatarTagComponent", "per-object optimizer settings" },
		};

		public static bool IsInstalled()
		{
			return VrcResolve.Has("Anatawa12.AvatarOptimizer.TraceAndOptimize");
		}

		/// Every optimizer component on an avatar, named by the friendly key from
		/// KnownComponents where it matches, and by full type name otherwise, so a
		/// component added by a newer version is still reported.
		public static List<Component> FindAll(GameObject avatar)
		{
			var found = new List<Component>();
			if (avatar == null) return found;
			foreach (string[] entry in KnownComponents)
			{
				Component component = VrcResolve.ComponentOn(avatar, entry[1]);
				if (component != null) found.Add(component);
			}
			// Anything else in the optimizer namespace, so a version bump does not
			// hide a component this file has never heard of.
			found.AddRange(ComponentsInNamespace(avatar, "Anatawa12.AvatarOptimizer"));
			return found;
		}

		/// The main entry point: does the avatar have TraceAndOptimize, and if not,
		/// what is present instead.
		///
		/// This is the question worth answering before an upload, because an avatar
		/// with no TraceAndOptimize uploads unoptimized and can hit the 70 MB /
		/// 70k-vertex performance limits without anything visibly failing.
		public static string Describe(GameObject avatar)
		{
			var report = new System.Text.StringBuilder();
			report.AppendLine("package=" + Package + "  installed=" + IsInstalled());
			if (!IsInstalled())
			{
				report.AppendLine("Not installed. Add " + Package + " to enable build-time optimization.");
				return report.ToString();
			}

			bool hasTrace = VrcResolve.ComponentOn(avatar, "Anatawa12.AvatarOptimizer.TraceAndOptimize") != null;
			report.AppendLine("TraceAndOptimize=" + (hasTrace ? "present" : "MISSING -- this avatar uploads unoptimized"));

			var present = new List<string>();
			foreach (string[] entry in KnownComponents)
			{
				if (VrcResolve.ComponentOn(avatar, entry[1]) == null) continue;
				present.Add(entry[0] + " (" + entry[2] + ")");
			}
			report.AppendLine("present components: " + present.Count);
			foreach (string line in present) report.AppendLine("  " + line);

			// Mesh and vertex budget, because that is the limit an upload actually
			// fails on and it is not visible anywhere in the Inspector.
			int vertexTotal = 0;
			var meshes = new List<string>();
			foreach (SkinnedMeshRenderer renderer in avatar.GetComponentsInChildren<SkinnedMeshRenderer>(true))
			{
				Mesh mesh = renderer.sharedMesh;
				if (mesh == null) continue;
				vertexTotal += mesh.vertexCount;
				meshes.Add(renderer.name + "=" + mesh.vertexCount);
			}
			report.AppendLine("renderers=" + meshes.Count + "  totalVertices=" + vertexTotal
				+ "  (VRChat limit 70000 per avatar)");
			report.AppendLine("  " + string.Join(", ", meshes.ToArray()));
			report.AppendLine("NOTE: optimizer components run at BUILD time (NDMF upload), not in the Editor. "
				+ "Adding TraceAndOptimize here does not change the scene you are looking at.");
			return report.ToString();
		}

		/// Adds TraceAndOptimize to the avatar root if it is not already there.
		///
		/// Idempotent by construction: re-running must not stack components, which
		/// would silently change upload behaviour.
		public static Component EnsureTraceAndOptimize(GameObject avatar)
		{
			if (avatar == null) return null;
			System.Type type = VrcResolve.Type("Anatawa12.AvatarOptimizer.TraceAndOptimize");
			if (type == null)
			{
				Debug.LogWarning("[pi] " + Package + " is not installed; cannot add TraceAndOptimize.");
				return null;
			}
			Component existing = avatar.GetComponent(type);
			if (existing != null) return existing;
			Undo.AddComponent(avatar, type);
			return avatar.GetComponent(type);
		}

		private static List<Component> ComponentsInNamespace(GameObject root, string ns)
		{
			var found = new List<Component>();
			foreach (Transform child in root.GetComponentsInChildren<Transform>(true))
			{
				foreach (Component component in child.GetComponents<Component>())
				{
					if (component == null) continue;
					string full = component.GetType().FullName ?? "";
					if (!full.StartsWith(ns + ".", StringComparison.Ordinal)) continue;
					found.Add(component);
				}
			}
			return found;
		}
	}
}
