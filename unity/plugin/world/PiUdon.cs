// Part of the pi Unity bridge plugin. See PiUnityBridge.Core.cs for the
// protocol overview. The whole plugin is one assembly of partial classes, so
// Unity compiles it into Assembly-CSharp-Editor with no asmdef and no
// package dependencies, which is what lets it drop into any Unity 2021+
// project (a configured VRChat world, an existing game) that pi was pointed at
// rather than generated.
//
// Udon and world-scene helpers.
//
// WHY SO LITTLE IS HARDCODED HERE
// --------------------------------
// The world SDK's behaviour types (VRC_SceneDescriptor, VRCObjectSync,
// VRC_Pickup, VRC_Station, VRC_SimpleTeleport, ...) ship as COMPILED PLUGIN
// DLLs, not C# source, and no world project was loaded when this was written.
// So there is no verified full name for any of them and none is asserted here.
// Everything resolves by SIMPLE NAME and REPORTS what it found, which turns a
// wrong guess into a reported fact rather than a silent miss.
//
// Two names ARE verified, because their source ships with the SDK:
//   sealed class VRC.Udon.UdonBehaviour
//   abstract class UdonSharpBehaviour
//
// A note that matters more than any API: UdonSharp compiles to a UdonBehaviour,
// and the Udon VM reads the SCRIPT ASSET, not the C# component. So editing a
// UdonSharpBehaviour's fields at runtime does nothing to the running program.
// The helpers here therefore read and create, and point at unity_inspect for
// field edits, rather than pretending a field write changes behaviour.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using UnityEditor;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace PiUnity
{
	public static class PiUdon
	{
		// Verified from com.vrchat.worlds 3.10.1 source.
		public const string UdonBehaviourType = "VRC.Udon.UdonBehaviour";
		public const string UdonSharpBehaviourType = "UdonSharpBehaviour";

		/// World behaviour types, by SIMPLE name, because the full names are
		/// unverified. The resolver matches on simple name across all loaded
		/// assemblies, so this works whether the type is
		/// VRC.SDKBase.VRC_SceneDescriptor or anything else.
		public static readonly string[] WorldBehaviourNames =
		{
			"VRC_SceneDescriptor", "VRCObjectSync", "VRC_Pickup", "VRC_PickupDriver",
			"VRC_Station", "VRC_SimpleTeleport", "VRC_SimpleTeleportPad", "VRC_AvatarScaler",
			"VRC_UdonCube", "VRCMusicSettings", "VRC_SceneSettings", "VRCWorldPreloadedLibrary",
		};

		/// Every UdonBehaviour in the scene, with the program it runs.
		public static List<Component> FindUdonBehaviours(GameObject root = null)
		{
			Type type = VrcResolve.Type(UdonBehaviourType);
			var found = new List<Component>();
			if (type == null) return found;
			IEnumerable<Transform> search = root == null
				? UnityEngine.Object.FindObjectsOfType<Transform>(true)
				: root.GetComponentsInChildren<Transform>(true);
			foreach (Transform child in search)
			{
				Component component = child.GetComponent(type);
				if (component != null) found.Add(component);
			}
			return found;
		}

		/// Resolves a world behaviour by simple name, reporting which assemblies
		/// provide it. The reporting is the point: it turns "I could not find it"
		/// into "here is what your project actually has".
		public static Component FindBehaviour(string simpleName, GameObject root = null)
		{
			Type type = ResolveWorldType(simpleName);
			if (type == null || root == null) return null;
			foreach (Transform child in root.GetComponentsInChildren<Transform>(true))
			{
				Component component = child.GetComponent(type);
				if (component != null) return component;
			}
			return null;
		}

		private static Type ResolveWorldType(string simpleName)
		{
			Type direct = VrcResolve.Type(simpleName);
			if (direct != null) return direct;
			// Simple-name match across every loaded assembly, since the full name
			// of a plugin-DLL type is not something this plugin can assume.
			foreach (Assembly assembly in AppDomain.CurrentDomain.GetAssemblies())
			{
				try
				{
					foreach (Type candidate in assembly.GetExportedTypes())
					{
						if (candidate.Name == simpleName) return candidate;
					}
				}
				catch (Exception)
				{
					// ReflectionTypeLoadException; another assembly may have it.
				}
			}
			return null;
		}

		/// The scene descriptor for the open scene, or null.
		public static Component SceneDescriptor()
		{
			foreach (GameObject root in UnityEngine.SceneManagement.SceneManager.GetActiveScene().GetRootGameObjects())
			{
				Component found = FindBehaviour("VRC_SceneDescriptor", root);
				if (found != null) return found;
			}
			return null;
		}

		/// A summary of a world scene: descriptor state, Udon, and behaviours.
		///
		/// This is the entry point for "what is this project", the way
		/// PiVRChat.DescribeProject is for an avatar project.
		public static string Describe()
		{
			var report = new System.Text.StringBuilder();
			Scene scene = UnityEngine.SceneManagement.SceneManager.GetActiveScene();
			report.AppendLine("scene=" + scene.name + "  path=" + scene.path);
			report.AppendLine("packages:");
			report.AppendLine("  com.vrchat.worlds       " + (IsWorldsInstalled() ? "installed" : "absent"));
			report.AppendLine("  " + PiLightVolumes.Package + "     " + (PiLightVolumes.IsInstalled() ? "installed" : "absent"));
			report.AppendLine("  UdonSharp               " + (IsUdonSharpInstalled() ? "installed" : "absent"));

			// -- descriptor ------------------------------------------------
			Component descriptor = SceneDescriptor();			report.AppendLine("--- scene descriptor ---");
			if (descriptor == null)
			{
				report.AppendLine("  VRC_SceneDescriptor: not found in the open scene.");
				report.AppendLine("  A world scene needs one; without it the scene is not a world and will not upload.");
			}
			else
			{
				report.AppendLine("  on " + descriptor.gameObject.name);
				report.AppendLine("  assetPath=" + (VrcResolve.AssetPathOf(descriptor) ?? "<scene instance>"));
				report.AppendLine("  Read its fields with unity_inspect { gameObject: \"" + descriptor.gameObject.name
					+ "\", component: \"VRC_SceneDescriptor\" }.");
			}

			// -- Udon ------------------------------------------------------
			List<Component> udon = FindUdonBehaviours();
			report.AppendLine("--- Udon ---");
			report.AppendLine("  UdonBehaviours=" + udon.Count);
			int sharpCount = 0;
			foreach (Component behaviour in udon)
			{
				if (VrcResolve.Type(UdonSharpBehaviourType) != null
					&& VrcResolve.Type(UdonSharpBehaviourType).IsInstanceOfType(behaviour))
				{
					sharpCount++;
				}
			}
			report.AppendLine("  UdonSharpBehaviours=" + sharpCount
				+ (udon.Count > 0 ? "  (UdonSharp scripts compile DOWN to a UdonBehaviour)" : ""));

			// -- world behaviours ------------------------------------------
			report.AppendLine("--- world behaviours ---");
			// GetRootGameObjects() returns GameObject[], not Transform[]: the
			// foreach variable must be a GameObject, or the scene roots are
			// silently unreachable.
			foreach (GameObject root in scene.GetRootGameObjects())
			{
				foreach (string name in WorldBehaviourNames)
				{
					int count = 0;
					Type type = ResolveWorldType(name);
					if (type == null) continue;
					foreach (Transform child in root.GetComponentsInChildren<Transform>(true))
					{
						if (child.GetComponent(type) != null) count++;
					}
					if (count > 0) report.AppendLine("  " + name + "=" + count);
				}
			}

			// -- light volumes ---------------------------------------------
			report.AppendLine("--- light volumes ---");
			report.Append(PiLightVolumes.Describe());
			return report.ToString();
		}

		public static bool IsWorldsInstalled()
		{
			return VrcResolve.Has(UdonBehaviourType);
		}

		public static bool IsUdonSharpInstalled()
		{
			return VrcResolve.Has(UdonSharpBehaviourType);
		}

		/// Creates a UdonSharpBehaviour script asset and attaches it.
		///
		/// This one is deliberately NOT implemented, and the reason matters.
		/// Creating a UdonSharp script means writing a .cs file into Assets/ and
		/// triggering a UdonSharp compile, which is a domain reload and a long
		/// Editor stall -- exactly the thing that makes a bridge request time out.
		/// Pretending otherwise would be the worst kind of helper: it would appear
		/// to work and then hang the Editor. Stated rather than faked.
		public static string ExplainWhyNoScriptCreation()
		{
			return
				"Creating a UdonSharp script is not automated.\n" +
				"Reason: it means writing a .cs file into Assets/ and running a UdonSharp compile,\n" +
				"which triggers a domain reload and a long Editor stall. That is the exact\n" +
				"condition that makes a bridge request time out, so a helper that did it would\n" +
				"appear to work and then hang the Editor.\n" +
				"Instead: create the script by hand in the Project window, attach it to an object,\n" +
				"then use unity_inspect / unity_set_property on the UdonSharpBehaviour to read and\n" +
				"set its fields.";
		}
	}
}
