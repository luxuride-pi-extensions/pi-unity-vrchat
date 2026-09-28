// Part of the pi Unity bridge plugin. See PiUnityBridge.Core.cs for the
// protocol overview. The whole plugin is one assembly of partial classes, so
// Unity compiles it into Assembly-CSharp-Editor with no asmdef and no
// package dependencies, which is what lets it drop into any Unity 2021+
// project (a configured VRChat world, an existing game) that pi was pointed at
// rather than generated.
//
// The bridge request that exposes the world helpers.
//
// A SEPARATE request type from the avatar `vrc` one, deliberately. A world
// project has no avatar, so the two share almost no resolution logic, and a
// world call in an avatar project should say so rather than return an empty
// report. Two types also mean the two packages can be improved independently.
//
// Actions are strings for the same reason as the avatar side: these helpers grow
// with package versions, and an unrecognised action returns the list of known
// ones, which turns a typo into a self-correcting call.

using System;
using System.Globalization;
using System.Linq;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace PiUnity
{
	public static partial class Bridge
	{
		private static string WorldAction(Json request)
		{
			string action = request.GetString("action") ?? "";

			switch (action)
			{
				case "project":
				case "scene":
					// One call to make first: packages, descriptor, Udon, world
					// behaviours, light volumes.
					return WorldTextResult(PiUdon.Describe());

				case "descriptor":
					{
						Component descriptor = PiUdon.SceneDescriptor();
						if (descriptor == null)
						{
							throw new Exception(
								"No VRC_SceneDescriptor in the open scene. A world scene needs one; without it "
								+ "the scene is not a world and will not upload. If this is an AVATAR project, "
								+ "use the vrc request instead."
							);
						}
						Json result = new Json();
						result.OpenObject();
						result.AddString("gameObject", descriptor.gameObject.name);
						result.AddString("type", descriptor.GetType().FullName);
						result.AddString("assembly", descriptor.GetType().Assembly.GetName().Name);
						result.AddString("assetPath", VrcResolve.AssetPathOf(descriptor) ?? "");
						result.AddString("note", "Read fields with unity_inspect { gameObject, component: \"" + descriptor.GetType().Name + "\" }.");
						result.CloseObject();
						return result.ToString();
					}

				case "light-volumes":
					return WorldTextResult(PiLightVolumes.Describe());

				case "create-light-volume":
					{
						string targetName = request.GetString("gameObject");
						GameObject target = string.IsNullOrEmpty(targetName)
							? NewWorldObject("Pi_LightVolume")
							: FindGameObjectByName(targetName);
						if (target == null) throw new Exception("No GameObject named \"" + targetName + "\" in the active scene.");

						string lightType = request.GetString("lightType") ?? "PointLight";
						float range = request.GetInt("range", 10);
						float intensity = request.GetInt("intensity", 1);
						string colorHex = request.GetString("color") ?? "ffffff";

						Color color = ParseHexColor(colorHex);
						Component volume = PiLightVolumes.Create(target, lightType, range, intensity, color);
						Scene scene = SceneManager.GetActiveScene();
						if (scene.IsValid()) EditorSceneManager.MarkSceneDirty(scene);

						Json result = new Json();
						result.OpenObject();
						result.AddString("gameObject", target.name);
						result.AddString("component", volume.GetType().FullName);
						result.AddString("type", lightType);
						result.AddString("range", range.ToString(CultureInfo.InvariantCulture));
						result.AddString("intensity", intensity.ToString(CultureInfo.InvariantCulture));
						result.AddString("color", colorHex);
						result.AddString("note",
							"Defaults are chosen to be VISIBLE (range 10, intensity 1). A light volume with "
							+ "zero range or intensity bakes fine and contributes nothing. "
							+ "Light volumes bake at BUILD time; the scene will not look different until then.");
						result.CloseObject();
						return result.ToString();
					}

				case "udon":
					{
						Json result = new Json();
						result.OpenObject();
						result.AddBool("worldsSdk", PiUdon.IsWorldsInstalled());
						result.AddBool("udonSharp", PiUdon.IsUdonSharpInstalled());
						result.AddNumber("udonBehaviourCount", PiUdon.FindUdonBehaviours().Count);
						result.AddString("explanation", PiUdon.ExplainWhyNoScriptCreation());
						result.CloseObject();
						return result.ToString();
					}

				case "find-behaviour":
					{
						string name = request.GetString("behaviour");
						string onObject = request.GetString("gameObject");
						Component found = string.IsNullOrEmpty(onObject)
							? null
							: PiUdon.FindBehaviour(name, FindGameObjectByName(onObject));
						Json result = new Json();
						result.OpenObject();
						result.AddString("behaviour", name);
						// Report the RESOLVED name rather than assuming one. The world
						// SDK ships its behaviours as plugin DLLs whose full names were
						// not verified, so "VRC_SceneDescriptor" resolves to whatever this
						// project actually has -- and an unresolved name is a fact worth
						// returning, not an error worth raising.
						result.AddString("resolvedType", found == null ? "" : found.GetType().FullName);
						result.AddString("assembly", found == null ? "" : found.GetType().Assembly.GetName().Name);
						result.AddBool("found", found != null);
						if (found != null)
						{
							result.AddString("gameObject", found.gameObject.name);
						}
						else
						{
							result.AddString("note", "Not found under that GameObject. Known world behaviour names: "
								+ string.Join(", ", PiUdon.WorldBehaviourNames) + ".");
						}
						result.CloseObject();
						return result.ToString();
					}

				case "actions":
					return WorldTextResult("actions: scene, descriptor, light-volumes, create-light-volume, udon, find-behaviour");

				default:
					throw new Exception(
						"Unknown world action \"" + action + "\". Known actions: scene, descriptor, light-volumes, "
						+ "create-light-volume, udon, find-behaviour. Start with action \"scene\"."
					);
			}
		}

		/// A fresh, EMPTY GameObject on the active scene's root, named as asked.
		///
		/// Created with Undo registration so a mistaken call is one Ctrl-Z away,
		/// which is the same standard the other write paths hold to.
		private static GameObject NewWorldObject(string name)
		{
			GameObject created = new GameObject(name);
			Undo.RegisterCreatedObjectUndo(created, "pi create " + name);
			return created;
		}

		/// "#rrggbb" or "rrggbb" to a Color. Alpha is always 1: a light volume with
		/// alpha 0 is legal and contributes nothing, which reads as broken.
		private static Color ParseHexColor(string hex)
		{
			string value = (hex ?? "").Trim().TrimStart('#');
			if (value.Length == 3)
			{
				// char + char is arithmetic in C# (an int), so each piece must be
				// made a string first or this silently becomes a number.
				string r = value[0].ToString();
				string g = value[1].ToString();
				string b = value[2].ToString();
				value = r + r + g + g + b + b;
			}
			if (value.Length != 6) return Color.white;
			try
			{
				// float, not int: a byte / 255f is a fraction, and declaring int
				// truncates every channel to 0 or 1.
				float r = Convert.ToInt32(value.Substring(0, 2), 16) / 255f;
				float g = Convert.ToInt32(value.Substring(2, 2), 16) / 255f;
				float b = Convert.ToInt32(value.Substring(4, 2), 16) / 255f;
				return new Color(r, g, b, 1f);
			}
			catch (Exception)
			{
				return Color.white;
			}
		}

		private static string WorldTextResult(string text)
		{
			Json result = new Json();
			result.OpenObject();
			result.AddString("report", text ?? "");
			result.CloseObject();
			return result.ToString();
		}
	}
}
