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
				return GetActiveTransformToolName();
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
		///
		/// A name containing "/" is read as a SCENE PATH and resolved exactly:
		/// the only cost of that is that no GameObject may be named "A/B", which
		/// Unity's own hierarchy cannot produce either since a name cannot contain
		/// a slash.
		private static GameObject FindGameObjectByName(string name)
		{
			if (name != null && name.IndexOf('/') >= 0) return FindGameObjectByPath(name);
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
					+ ". Name the object unambiguously, or scope the call with its scene path (\""
					+ string.Join("/", owners[0].Split('/')) + "/...\") instead of a bare name."
				);
			}
			return found;
		}

		/// Resolves an EXACT scene path, "Root/Child/Grandchild", which is the
		/// only unambiguous way to name an object whose parent already holds a
		/// same-named child.
		///
		/// Every segment must match a real transform, and the whole chain must
		/// end inside the active scene. A prefix that matches nothing is a miss,
		/// not a shortcut to a partial match -- otherwise "Head" would resolve
		/// inside an unrelated rig.
		private static GameObject FindGameObjectByPath(string path)
		{
			string[] segments = path.Split('/');
			if (segments.Length == 0 || segments[0].Length == 0) return null;

			Scene scene = SceneManager.GetActiveScene();
			Transform current = null;
			foreach (GameObject root in scene.GetRootGameObjects())
			{
				if (root.name != segments[0]) continue;
				current = root.transform;
				break;
			}
			if (current == null) return null;

			for (int index = 1; index < segments.Length; index++)
			{
				Transform next = null;
				foreach (Transform child in current)
				{
					if (child.name != segments[index]) continue;
					next = child;
					break;
				}
				if (next == null) return null;
				current = next;
			}
			return current.gameObject;
		}

		/// The transform tool lives on `UnityEditor.EditorTools.EditorTools`, a
		/// TYPE that sits INSIDE a namespace of the same name.
		///
		/// Getting this wrong is silent and total. An earlier version asked for
		/// `typeof(Editor).Assembly.GetType("UnityEditor.EditorTools")`, which
		/// looks like it should work and returns null: the name resolves to the
		/// NAMESPACE, not to a type. So SetTransformTool returned false for every
		/// input and `unity_set_tool { tool: "move" }` answered `Unknown
		/// transform tool "move". Use move, rotate, scale, or rect.` -- the tool
		/// rejected its own documented input, on a code path that still compiled
		/// clean and looked correct while reading. It is also not recoverable
		/// with non-public binding, because GetTypes() does not contain a type of
		/// that name either.
		///
		/// Reflection is still required: the type is public but lives in a
		/// `UnityEditor.EditorTools` namespace that the predefined editor
		/// assembly cannot be assumed to reference, and naming it directly would
		/// add that coupling to every project the plugin is dropped into.
		private static Type GetEditorToolsType()
		{
			return typeof(Editor).Assembly.GetType("UnityEditor.EditorTools.EditorTools");
		}

		/// SetTransformTool drives the tool through `SetActiveTool(Type)`, taking
		/// the tool's TYPE rather than an enum value. There is no public setter on
		/// `activeToolType` (attempting one throws "Set Method not found"), and
		/// the legacy `EditorTools.transformTool` enum property that earlier
		/// versions targeted does not exist on 2022.3 either.
		private static bool SetTransformTool(string tool)
		{
			Type toolsType = GetEditorToolsType();
			if (toolsType == null) return false;
			MethodInfo setActive = toolsType.GetMethod(
				"SetActiveTool",
				BindingFlags.Public | BindingFlags.Static,
				null,
				new[] { typeof(Type) },
				null
			);
			if (setActive == null) return false;

			// Resolved by name so a Unity upgrade that renames a tool fails loudly
			// here instead of silently doing nothing.
			string typeName = tool switch
			{
				"move" => "UnityEditor.MoveTool",
				"rotate" => "UnityEditor.RotateTool",
				"scale" => "UnityEditor.ScaleTool",
				// RectTool DOES exist on 2022.3 -- an earlier version mapped
				// rect onto Scale on the belief that the rect tool was 2023.1+.
				"rect" => "UnityEditor.RectTool",
				_ => "",
			};
			if (typeName.Length == 0) return false;

			Type toolType = typeof(Editor).Assembly.GetType(typeName);
			if (toolType == null) return false;
			setActive.Invoke(null, new object[] { toolType });
			return true;
		}

		private static string GetActiveTransformToolName()
		{
			try
			{
				Type toolsType = GetEditorToolsType();
				PropertyInfo property = toolsType?.GetProperty(
					"activeToolType",
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
