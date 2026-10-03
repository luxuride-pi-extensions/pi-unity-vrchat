// Part of the pi Unity bridge plugin. See PiUnityBridge.Core.cs for the
// protocol overview. The whole plugin is one assembly of partial classes, so
// Unity compiles it into Assembly-CSharp-Editor with no asmdef and no
// package dependencies, which is what lets it drop into any Unity 2021+
// project (a configured VRChat world, an existing game) that pi was pointed at
// rather than generated.
//
// The bridge request that exposes the VRChat helpers.
//
// The helpers themselves live in this subdirectory; this file is the wire
// format. Split that way so the package logic stays testable and independent of
// how a model asks for it, and so a change to the JSON shape does not ripple
// into the package wrappers.
//
// Every action is a STRING rather than a flag set, because these helpers grow
// with package versions and a flag enum would need a new value plus a schema
// change for each one. An unrecognised action returns the list of known ones,
// which is what turns a typo into a self-correcting call instead of a dead end.

using System;
using System.Linq;
using UnityEditor;
using UnityEngine;

namespace PiUnity
{
	public static partial class Bridge
	{
		private static string VrcAction(Json request)
		{
			string action = request.GetString("action") ?? "";
			string gameObjectName = request.GetString("gameObject");

			switch (action)
			{
				case "project":
					// The one call to make first: what packages exist, what
					// avatars are in the scene, and what the optimizer says.
					return TextResult(PiVRChat.DescribeProject());

				case "avatar":
					{
						GameObject avatar = ResolveVrcAvatar(gameObjectName);
						return TextResult(PiVRChat.DescribeAvatar(avatar));
					}

				case "optimizer":
					{
						GameObject avatar = ResolveVrcAvatar(gameObjectName);
						return TextResult(PiAvatarOptimizer.Describe(avatar));
					}

				case "ensure-trace-and-optimize":
					{
						GameObject avatar = ResolveVrcAvatar(gameObjectName);
						if (avatar == null) throw new Exception(AvatarMissing(gameObjectName));
						Component created = PiAvatarOptimizer.EnsureTraceAndOptimize(avatar);
						if (created == null)
						{
							throw new Exception(
								"Could not add TraceAndOptimize. " + PiAvatarOptimizer.Package + " is not installed, " +
								"or the component was refused. Run action \"project\" to see what is installed."
							);
						}
						// Idempotent, so the caller cannot tell a fresh add from a
						// no-op. Report which it was, or a second call looks like a
						// silent success.
						Undo.RecordObject(avatar, "pi ensure TraceAndOptimize");
						EditorUtility.SetDirty(avatar);

						Json result = new Json();
						result.OpenObject();
						result.AddString("gameObject", avatar.name);
						result.AddString("component", created.GetType().FullName);
						result.AddString("assetPath", VrcResolve.AssetPathOf(created) ?? "");
						result.AddString("note",
							"TraceAndOptimize runs at BUILD time (NDMF upload), not in the Editor. "
							+ "The scene will look unchanged; the upload will be optimized.");
						result.CloseObject();
						return result.ToString();
					}

				case "vrcfury-list":
					{
						GameObject avatar = ResolveVrcAvatar(gameObjectName);
						Json result = new Json();
						result.OpenObject();
						result.AddBool("installed", PiVRCFury.IsInstalled());
						result.AddArray("addableElements", PiVRCFury.AddableElements);
						Json found = new Json();
						found.OpenArray();
						foreach (Component component in PiVRCFury.FindAll(avatar))
						{
							Json entry = new Json();
							entry.OpenObject();
							entry.AddString("type", component.GetType().FullName);
							entry.AddString("on", component.gameObject.name);
							entry.AddString("path", PiVRChat.RelativePath(avatar, component.gameObject));
							entry.CloseObject();
							found.AddElement(entry);
						}
						found.CloseArray();
						result.AddValue("components", found);
						result.CloseObject();
						return result.ToString();
					}

				case "vrcfury-add":
					{
						GameObject avatar = ResolveVrcAvatar(gameObjectName);
						string element = request.GetString("element");
						if (avatar == null) throw new Exception(AvatarMissing(gameObjectName));

						// A bone targets the BONE, not the root: a toggle belongs on
						// the bone it drives. Getting this wrong is the most common
						// VRCFury mistake, so it is resolved here rather than
						// left to the caller.
						string boneName = request.GetString("bone");
						HumanBodyBones? bone = null;
						if (!string.IsNullOrEmpty(boneName))
						{
							object parsed = Enum.Parse(typeof(HumanBodyBones), boneName, true);
							bone = (HumanBodyBones)parsed;
						}
						GameObject target = PiVRCFury.TargetFor(element, avatar, bone);
						if (target == null) throw new Exception("Could not resolve a target for that element.");

						Undo.RecordObject(target, "pi add VRCFury " + element);
						Component created = PiVRCFury.Add(target, element);
						if (created == null)
						{
							throw new Exception(
								"VRCFury did not create \"" + element + "\" on " + target.name + ". "
								+ "Known elements: " + string.Join(", ", PiVRCFury.AddableElements) + ". "
								+ "Run action \"vrcfury-list\" to check VRCFury is installed."
							);
						}
						EditorUtility.SetDirty(target);

						Json result = new Json();
						result.OpenObject();
						result.AddString("element", element);
						result.AddString("component", created.GetType().FullName);
						// The wrapper the factory returns is NOT a component (see
						// PiVRFury's header); naming it separately is what lets a
						// caller see that this VRCFury version models the element as
						// avatar-level data rather than an editable component field.
						result.AddString("elementType", PiVRCFury.ElementType(element) ?? "");
						result.AddString("addedTo", target.name);
						result.AddString("path", PiVRChat.RelativePath(avatar, target));
						result.AddString("note",
							"Set its fields with unity_inspect { gameObject, component: \"" + created.GetType().Name + "\" } "
							+ "-- the factory only creates the element and its component, it does not set fields.");
						result.CloseObject();
						return result.ToString();
					}

				case "liltoon-usage":
					{
						GameObject avatar = ResolveVrcAvatar(gameObjectName);
						return TextResult(PiVRChat.DescribeLilToonUsage(avatar));
					}

				case "liltoon-material":
					{
						string materialPath = request.GetString("materialPath");
						Material material = string.IsNullOrEmpty(materialPath)
							? null
							: AssetDatabase.LoadAssetAtPath<Material>(materialPath);
						if (material == null)
						{
							throw new Exception("Could not load a material at \"" + materialPath + "\". Use an Assets/-relative path.");
						}
						return TextResult(PiVRChat.DescribeLilToonMaterial(material));
					}

				case "path":
					{
						GameObject avatar = ResolveVrcAvatar(gameObjectName);
						string childName = request.GetString("child");
						if (avatar == null) throw new Exception(AvatarMissing(gameObjectName));
						// Search INSIDE the named avatar, not the whole scene.
						// FindGameObjectByName walks every root, so with two avatars
						// in a scene it returned a bone from the WRONG one: asking
						// for Chiffon/Head came back as
						// "Chiffon Quest Friendly/Armature/.../Head".
						GameObject child = FindDirectChild(avatar, childName);
						if (child == null)
						{
							throw new Exception(
								"\"" + childName + "\" is not inside " + avatar.name + " or its children. "
								+ "Names are ambiguous across avatars, so a child must be looked up inside the named root."
							);
						}
						return TextResult(PiVRChat.RelativePath(avatar, child));
					}

				case "actions":
					return TextResult("actions: project, avatar, optimizer, ensure-trace-and-optimize, "
						+ "vrcfury-list, vrcfury-add, liltoon-usage, liltoon-material, path");

				default:
					throw new Exception(
						"Unknown vrc action \"" + action + "\". Known actions: project, avatar, optimizer, "
						+ "ensure-trace-and-optimize, vrcfury-list, vrcfury-add, liltoon-usage, liltoon-material, path. "
						+ "Start with action \"project\"."
					);
			}
		}

		/// Finds a GameObject by name WITHIN a root, never across the scene.
		///
		/// Unity has many two-avatar scenes, so a scene-wide name lookup resolves
		/// to whichever root happens to come first. Every helper that takes a
		/// child name must scope the search to the avatar the caller named, or it
		/// silently reports the wrong avatar's data.
		private static GameObject FindDirectChild(GameObject root, string name)
		{
			if (root == null || string.IsNullOrEmpty(name)) return null;
			if (root.name == name) return root;
			foreach (Transform child in root.GetComponentsInChildren<Transform>(true))
			{
				if (child.name == name) return child.gameObject;
			}
			return null;
		}

		/// Resolves the named GameObject, or auto-detects the scene's only avatar.
		///
		/// Auto-detection is what makes single-character actions usable: asking
		/// for optimizer status on a one-avatar project should not require the
		/// caller to know the root's name.
		private static GameObject ResolveVrcAvatar(string gameObjectName)
		{
			if (!string.IsNullOrEmpty(gameObjectName))
			{
				GameObject named = FindGameObjectByName(gameObjectName);
				if (named != null) return named;
				throw new Exception("No GameObject named \"" + gameObjectName + "\" in the active scene.");
			}
			var roots = PiVRChat.FindAvatarRoots();
			return roots.Count > 0 ? roots[0] : null;
		}

		private static string AvatarMissing(string gameObjectName)
		{
			return string.IsNullOrEmpty(gameObjectName)
				? "No avatar found. Open a scene containing a VRCAvatarDescriptor, or name the avatar with gameObject. "
					+ "Run action \"project\" to list what is in the scene."
				: "No GameObject named \"" + gameObjectName + "\" in the active scene.";
		}

		/// Wraps a plain-text report in the bridge's ok/result envelope.
		private static string TextResult(string text)
		{
			Json result = new Json();
			result.OpenObject();
			result.AddString("report", text ?? "");
			result.CloseObject();
			return result.ToString();
		}
	}
}
