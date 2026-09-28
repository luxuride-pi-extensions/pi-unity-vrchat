// Part of the pi Unity bridge plugin. See PiUnityBridge.Core.cs for the
// protocol overview. The whole plugin is one assembly of partial classes, so
// Unity compiles it into Assembly-CSharp-Editor with no asmdef and no package
// dependencies, which is what lets it drop into any Unity 2021+ project.

// Reflective access to internal UnityEditor APIs
//
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using UnityEditor;
using UnityEditor.Compilation;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace PiUnity
{
	public static partial class Bridge
	{
		/// cosmetic field must never fail a status request.
		private static string GetActiveToolName()
		{
			try
			{
				Type toolsType = typeof(Editor).Assembly.GetType("UnityEditor.EditorTools");
				PropertyInfo property = toolsType?.GetProperty(
					"activeTool",
					BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static
				);
				object value = property?.GetValue(null, null);
				return value == null ? "" : value.GetType().Name;
			}
			catch (Exception)
			{
				return "";
			}
		}

		/// FindInSceneByName is constrained to Component, so a plain GameObject
		/// lookup gets its own helper rather than a cast dance at the call site.
		///
		/// An AMBIGUOUS name is an error, not a first-match guess. This matters
		/// in practice: `vrc action=path gameObject=Chiffon child=Head` returned a
		/// bone from "Chiffon Quest Friendly" instead, because the old version
		/// walked the scene roots in order and returned the first hit. Silently
		/// resolving to the wrong avatar is worse than refusing, because the
		/// answer looks authoritative.
		private static GameObject FindGameObjectByName(string name)
		{
			GameObject found = null;
			List<string> owners = new List<string>();
			Scene scene = SceneManager.GetActiveScene();
			foreach (GameObject root in scene.GetRootGameObjects())
			{
				foreach (Transform child in root.GetComponentsInChildren<Transform>(true))
				{
					if (child.gameObject.name != name) continue;
					if (found == null)
					{
						found = child.gameObject;
						owners.Add(root.name);
					}
					else if (found != child.gameObject)
					{
						// Same name, different object, and it belongs to another
						// scene root: the name cannot identify it.
						if (!owners.Contains(root.name)) owners.Add(root.name);
					}
				}
			}

			if (found == null) return null;
			if (owners.Count > 1)
			{
				throw new Exception(
					"\"" + name + "\" is ambiguous: it exists under " + string.Join(" and ", owners.ToArray())
					+ ". Name the object unambiguously, or scope the call with a path instead of a bare name."
				);
			}
			return found;
		}

		/// EditorTools.transformTool and the TransformTool enum are both internal
		/// to UnityEditor, so naming them directly does not compile (CS0103 /
		/// CS0122). Reflection is the documented way to drive the transform
		/// tool from a script; the enum is resolved by name from the same
		/// assembly, so a Unity upgrade that renames a member fails loudly here
		/// instead of silently doing nothing.
		private static bool SetTransformTool(string tool)
		{
			Type toolsType = typeof(Editor).Assembly.GetType("UnityEditor.EditorTools");
			if (toolsType == null) return false;
			PropertyInfo property = toolsType.GetProperty(
				"transformTool",
				BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static
			);
			Type enumType = toolsType.GetProperty("transformTool", BindingFlags.NonPublic | BindingFlags.Static)?.PropertyType;
			if (property == null || enumType == null) return false;

			// rect is a Unity 2023.1+ alias for the scale tool; on 2022 LTS the
			// rect tool is the legacy Rect not in the enum, so map it to Scale
			// and let Tools.hidden carry the distinct visual behaviour.
			string enumName = tool switch
			{
				"move" => "Move",
				"rotate" => "Rotate",
				"scale" => "Scale",
				"rect" => "Scale",
				_ => "",
			};
			if (enumName.Length == 0) return false;
			property.SetValue(null, Enum.Parse(enumType, enumName), null);
			return true;
		}

		private static string GetActiveTransformToolName()
		{
			try
			{
				Type toolsType = typeof(Editor).Assembly.GetType("UnityEditor.EditorTools");
				PropertyInfo property = toolsType?.GetProperty(
					"transformTool",
					BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static
				);
				object value = property?.GetValue(null, null);
				return value == null ? "" : value.ToString();
			}
			catch (Exception)
			{
				// A cosmetic readout must never fail a status request.
				return "";
			}
		}

		private static T FindInSceneByName<T>(string name) where T : Component
		{
			Scene scene = SceneManager.GetActiveScene();
			foreach (GameObject root in scene.GetRootGameObjects())
			{
				foreach (T found in root.GetComponentsInChildren<T>(true))
				{
					if (found.gameObject.name == name) return found;
				}
			}
			return null;
		}

		private static string ColorToString(Color color)
		{
			return string.Format(CultureInfo.InvariantCulture, "{0},{1},{2}", color.r, color.g, color.b);
		}

		/// A Vector3 as a JSON array, so a consumer gets numbers rather than one
		/// comma-joined string it has to parse.
		///
		/// ColorToString's joined-string form is kept for colours because those
		/// are display-only; a position is data a model will reason about and then
		/// write back, so it must arrive as three parseable numbers. Invariant
		/// formatting throughout: a locale with a comma decimal separator would
		/// otherwise produce [0,5 1 0] and silently corrupt every position.
		private static Json VectorToJson(Vector3 value)
		{
			Json array = new Json();
			array.OpenArray();
			array.Add(value.x);
			array.Add(value.y);
			array.Add(value.z);
			array.CloseArray();
			return array;
		}
	}
}
