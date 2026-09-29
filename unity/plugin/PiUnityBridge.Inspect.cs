// Part of the pi Unity bridge plugin. See PiUnityBridge.Core.cs for the
// protocol overview. The whole plugin is one assembly of partial classes, so
// Unity compiles it into Assembly-CSharp-Editor with no asmdef and no
// package dependencies, which is what lets it drop into any Unity 2021+
// project (a configured VRChat world, an existing game) that pi was pointed at
// rather than generated.

// Transforms, bones, and the Inspector.
//
// THREE REQUESTS, THREE DIFFERENT QUESTIONS
// ------------------------------------------
// "get-bones"      Where is a bone? A VRChat avatar is not a scene object: the
//                  rig lives inside a prefab and is addressed by Humanoid bone
//                  name, not by GameObject path.
//
// "inspect"        What does this component contain? Implemented over
//                  SerializedObject rather than by reading fields, because pi
//                  cannot know the type ahead of time -- VRC_AvatarDescriptor,
//                  lilToon, VRCFury and a user's own MonoBehaviour all have
//                  fields pi has never heard of. SerializedObject walks the
//                  same data the Inspector shows, so this sees exactly what a
//                  human sees, including serialized private fields and
//                  [HideInInspector] ones.
//
// "set-property"   Write one property back.
//
// DESIGN NOTES THAT COST REAL DEBUGGING
// ------------------------------------
// 1. SkeletonBone is unusable in this reference set. `bone.transform` does not
//    resolve and `bone == null` is rejected outright (CS1061 / CS0019). Rigs are
//    therefore resolved the way Unity itself does: an ancestor of a
//    SkinnedMeshRenderer, with the bone list taken from the renderer's own
//    `bones` array. On the reference avatar that returns 186 bones, and
//    `Animator.GetBoneTransform` covers 53 of the 55 HumanBodyBones.
//
// 2. Positions are JSON ARRAYS, not joined strings, and always formatted with
//    InvariantCulture. A comma decimal separator would otherwise emit
//    [0,5 1 0] and silently corrupt every coordinate.
//
// 3. Every write goes through Undo.RecordObject and marks the asset or scene
//    dirty. Without that, a model's edit is invisible to Ctrl-Z and vanishes on
//    the next reload, which is the worst possible failure for a tool whose whole
//    purpose is making small edits.

using System;
using System.Collections.Generic;
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
		// ------------------------------------------------------------ bones

		/// Resolves a component by type name, tolerating the ways a caller might
		/// name it.
		///
		/// Unity's GetComponent(string) matches ignoring case AND underscores, so
		/// it finds "VRC_AvatarDescriptor" against the real type
		/// VRCAvatarDescriptor. A plain reflection comparison does not, because
		/// GetType().Name is "VRCAvatarDescriptor" and FullName is the long
		/// namespace. That is why this helper exists rather than the caller doing
		/// a Name == comparison: a model reads a type name out of unity_inspect
		/// output and passes it straight back, and the underscore form is what it
		/// will send.
		private static Component ResolveComponent(GameObject target, string componentName, out string available)
		{
			Component byName = target.GetComponent(componentName);
			if (byName != null)
			{
				available = "";
				return byName;
			}
			foreach (Component candidate in target.GetComponents<Component>())
			{
				if (candidate == null) continue;
				if (candidate.GetType().Name == componentName || candidate.GetType().FullName == componentName)
				{
					available = "";
					return candidate;
				}
			}
			// Normalised comparison as a last resort: strip underscores and
			// compare case-insensitively, which is what Unity itself does.
			string wanted = NormalizeTypeName(componentName);
			foreach (Component candidate in target.GetComponents<Component>())
			{
				if (candidate == null) continue;
				if (NormalizeTypeName(candidate.GetType().Name) != wanted) continue;
				available = "";
				return candidate;
			}
			available = string.Join(", ", target.GetComponents<Component>()
				.Where(c => c != null)
				.Select(c => c.GetType().Name)
				.ToArray());
			return null;
		}

		private static string NormalizeTypeName(string value)
		{
			return value == null
				? ""
				: value.Replace("_", "").Replace(".", "").ToLowerInvariant();
		}

		private static string GetBones(Json request)
		{
			string gameObjectName = request.GetString("gameObject");
			string[] requested = request.GetStringArray("bones").ToArray();
			int limit = request.GetInt("limit", 200);

			GameObject avatar = ResolveAvatarRoot(gameObjectName);
			if (avatar == null)
			{
				throw new Exception(
					"No avatar found. Pass gameObject with the avatar root's name, or open a scene containing one. " +
					"Scene roots: " + string.Join(", ", SceneManager.GetActiveScene().GetRootGameObjects().Select(go => go.name).ToArray())
				);
			}

			Animator animator = FindInChildren<Animator>(avatar);
			SkinnedMeshRenderer skinned = FindInChildren<SkinnedMeshRenderer>(avatar);

			Json result = new Json();
			result.OpenObject();
			result.AddString("gameObject", avatar.name);
			result.AddString("scene", SceneManager.GetActiveScene().path);
			result.AddBool("hasAnimator", animator != null);
			result.AddBool("hasSkinnedRenderer", skinned != null);
			if (animator != null)
			{
				result.AddBool("isHuman", animator.isHuman);
				result.AddString("avatarMask", animator.avatar == null ? "" : animator.avatar.name);
			}
			if (skinned != null)
			{
				result.AddString("skinnedRenderer", skinned.gameObject.name);
				result.AddString("rootBone", skinned.rootBone == null ? "" : skinned.rootBone.name);
				result.AddNumber("boneCount", skinned.bones.Length);
			}

			// ---- Humanoid addressing, by enum name ------------------------
			// A keyed MAP, so OpenObject, not OpenArray. Both `humanoidBones` and
			// `rig` used to open as arrays while being filled with "key":{...}
			// members, which emits `[ "Hips": {...}` -- a JSON syntax error, so
			// every response failed to parse. AddArray(key, Json) already writes
			// a keyed member; the container is the only thing that was wrong.
			Json humanoid = new Json();
			humanoid.OpenObject();
			if (animator != null && animator.isHuman)
			{
				foreach (HumanBodyBones bone in Enum.GetValues(typeof(HumanBodyBones)))
				{
					if (bone == HumanBodyBones.LastBone) continue;
					Transform found;
					try
					{
						found = animator.GetBoneTransform(bone);
					}
					catch (Exception)
					{
						continue;
					}
					if (found == null) continue;
					if (requested.Length > 0 && !MatchesBoneRequest(bone.ToString(), found.name, requested)) continue;
					humanoid.AddValue(bone.ToString(), DescribeBone(found, bone.ToString()));
				}
			}
			humanoid.CloseObject();
			result.AddValue("humanoidBones", humanoid);

			// ---- Full rig, by transform path ------------------------------
			// The Humanoid enum covers 53 bones; a rig usually has far more
			// (186 here), including every finger. This is the complete list, so
			// "where is the left index fingertip" is answerable too.
			Json rig = new Json();
			rig.OpenObject();
			int rigCount = 0;
			if (skinned != null)
			{
				foreach (Transform bone in skinned.bones)
				{
					if (bone == null) continue;
					if (rigCount >= limit) break;
					if (requested.Length > 0 && !MatchesBoneRequest(bone.name, bone.name, requested)) continue;
					rig.AddValue(PathFromAncestorOf(bone, skinned.rootBone), DescribeBone(bone, ""));
					rigCount++;
				}
			}
			else if (animator != null)
			{
				foreach (Transform child in avatar.GetComponentsInChildren<Transform>(true))
				{
					if (rigCount >= limit) break;
					if (requested.Length > 0 && !MatchesBoneRequest(child.name, child.name, requested)) continue;
					rig.AddValue(PathFromAncestorOf(child, avatar.transform), DescribeBone(child, ""));
					rigCount++;
				}
			}
			rig.CloseObject();
			result.AddValue("rig", rig);
			result.AddNumber("rigCount", rigCount);

			// Truncated only when something was actually dropped, not merely
			// because a limit was configured.
			result.AddBool("truncated", skinned != null && rigCount >= limit && skinned.bones.Length > rigCount);
			result.CloseObject();
			return result.ToString();
		}

		/// Matches a requested bone against the enum name and the transform name.
		///
		/// Callers ask in whichever vocabulary they know -- "Head" as a
		/// HumanBodyBones value, "Head" or "head" or "Hips" as a GameObject
		/// name -- and a case-insensitive substring match serves both without
		/// making the caller know which layer the bone came from.
		private static bool MatchesBoneRequest(string enumName, string transformName, string[] requested)
		{
			foreach (string candidate in requested)
			{
				if (enumName.IndexOf(candidate, StringComparison.OrdinalIgnoreCase) >= 0) return true;
				if (transformName != null && transformName.IndexOf(candidate, StringComparison.OrdinalIgnoreCase) >= 0) return true;
			}
			return false;
		}

		private static Json DescribeBone(Transform bone, string humanoidName)
		{
			Json entry = new Json();
			entry.OpenObject();
			entry.AddString("name", bone.name);
			if (!string.IsNullOrEmpty(humanoidName)) entry.AddString("humanoidBone", humanoidName);
			entry.AddArray("position", VectorToJson(bone.position));
			entry.AddArray("localPosition", VectorToJson(bone.localPosition));
			entry.AddArray("rotationEuler", VectorToJson(bone.eulerAngles));
			entry.AddArray("localRotationEuler", VectorToJson(bone.localEulerAngles));
			entry.AddArray("localScale", VectorToJson(bone.localScale));
			entry.AddString("path", AnimationUtility.CalculateTransformPath(bone, bone.root));
			entry.AddBool("active", bone.gameObject.activeSelf);
			entry.CloseObject();
			return entry;
		}

		/// A stable path relative to a root, so a caller can name a bone
		/// unambiguously even when the rig has two children named "Bone".
		private static string PathFromAncestorOf(Transform transform, Transform root)
		{
			if (root == null) return transform.name;
			try
			{
				return AnimationUtility.CalculateTransformPath(transform, root);
			}
			catch (Exception)
			{
				return transform.name;
			}
		}

		/// Resolves the avatar root: the named GameObject, else the first object
		/// carrying an Animator or SkinnedMeshRenderer.
		private static GameObject ResolveAvatarRoot(string name)
		{
			if (!string.IsNullOrEmpty(name))
			{
				GameObject named = FindGameObjectByName(name);
				if (named != null) return named;
				throw new Exception("No GameObject named \"" + name + "\" in the open scene.");
			}

			foreach (GameObject root in SceneManager.GetActiveScene().GetRootGameObjects())
			{
				if (root.GetComponentInChildren<Animator>(true) != null) return root;
			}
			foreach (GameObject root in SceneManager.GetActiveScene().GetRootGameObjects())
			{
				if (root.GetComponentInChildren<SkinnedMeshRenderer>(true) != null) return root;
			}
			return null;
		}

		private static T FindInChildren<T>(GameObject root) where T : Component
		{
			return root.GetComponentInChildren<T>(true);
		}

		// -------------------------------------------------------- inspector

		private static string Inspect(Json request)
		{
			string gameObjectName = request.GetString("gameObject");
			string componentName = request.GetString("component");
			bool includeChildren = request.GetBool("includeChildComponents");

			GameObject target = FindGameObjectByName(gameObjectName);
			if (target == null)
			{
				throw new Exception(
					"No GameObject named \"" + gameObjectName + "\" in the active scene. " +
					"Call unity_scene_info for the list of roots."
				);
			}

			Json result = new Json();
			result.OpenObject();
			result.AddString("gameObject", target.name);
			result.AddString("path", PathInScene(target));
			result.AddArray("position", VectorToJson(target.transform.position));
			result.AddArray("localPosition", VectorToJson(target.transform.localPosition));
			result.AddString("tag", target.tag);
			result.AddString("layer", LayerMask.LayerToName(target.layer));
			result.AddBool("active", target.activeSelf);
			result.AddString(
				"prefabAssetPath",
				PrefabUtility.GetPrefabInstanceStatus(target) != PrefabInstanceStatus.NotAPrefab
					? PrefabUtility.GetPrefabAssetPathOfNearestInstanceRoot(target) ?? ""
					: "");

			Json components = new Json();
			components.OpenArray();
			foreach (Component component in target.GetComponents<Component>())
			{
				// A missing script leaves a null slot; reporting it is more useful
				// than skipping it, because a null component IS a bug.
				if (component == null)
				{
					Json missing = new Json();
					missing.OpenObject();
					missing.AddString("type", "<missing script>");
					missing.AddBool("missing", true);
					missing.CloseObject();
					components.AddElement(missing);
					continue;
				}
				components.AddElement(DescribeComponent(component));
			}
			components.CloseArray();
			result.AddValue("components", components);

			// A single named component's fields in full, which is what a caller
			// asking "inspect X" actually wants to read.
			if (!string.IsNullOrEmpty(componentName))
			{
				string available;
				Component component = ResolveComponent(target, componentName, out available);
				if (component == null)
				{
					throw new Exception(
						"\"" + gameObjectName + "\" has no component \"" + componentName + "\". " +
						"It has: " + available
					);
				}
				result.AddValue("inspect", DescribeComponent(component, true));
			}

			if (includeChildren)
			{
				Json children = new Json();
				children.OpenArray();
				foreach (Transform child in target.transform)
				{
					children.OpenObject();
					children.AddString("name", child.name);
					children.AddString("path", PathInScene(child.gameObject));
					children.AddArray("localPosition", VectorToJson(child.localPosition));
					children.AddNumber("childCount", child.childCount);
					children.CloseObject();
				}
				children.CloseArray();
				result.AddValue("children", children);
			}

			result.CloseObject();
			return result.ToString();
		}

		/// Dumps a component through SerializedObject.
		///
		/// This is the mechanism that makes the Inspector scriptable for a type
		/// pi has never seen. Field access by reflection would miss serialized
		/// private fields, [HideInInspector] fields, and everything VRCFury or
		/// lilToon adds; SerializedObject walks exactly the set Unity's own
		/// Inspector displays.
		private static Json DescribeComponent(Component component, bool deep = false)
		{
			Json entry = new Json();
			entry.OpenObject();
			entry.AddString("type", component.GetType().FullName);
			entry.AddString("shortName", component.GetType().Name);
			entry.AddString("assembly", component.GetType().Assembly.GetName().Name);
			entry.AddString("scriptAssetPath", MonoScriptFromComponent(component) ?? "");
			entry.AddBool("enabled", IsBehaviourEnabled(component));

			var serialized = new SerializedObject(component);
			Json properties = new Json();
			properties.OpenArray();
			SerializedProperty property = serialized.GetIterator();
			// EnterChildren(false) so the first level is a property, not a child
			// of one; a deep pass descends for arrays, which is where a
			// VRC_AvatarDescriptor's PhysBones live.
			bool enterChildren = deep;
			while (property.NextVisible(enterChildren))
			{
				enterChildren = deep;
				properties.AddElement(DescribeProperty(property, deep));
			}
			properties.CloseArray();
			entry.AddValue("properties", properties);
			entry.CloseObject();
			return entry;
		}

		private static Json DescribeProperty(SerializedProperty property, bool deep)
		{
			Json entry = new Json();
			entry.OpenObject();
			entry.AddString("path", property.propertyPath);
			entry.AddString("displayName", property.displayName);
			entry.AddString("type", property.propertyType.ToString());
			if (property.propertyPath == "m_Script")
			{
				// m_Script is Unity's handle to the script itself; reporting it as
				// an object reference is noise.
				entry.CloseObject();
				return entry;
			}
			entry.AddValue("value", SerializePropertyValue(property));
			entry.CloseObject();
			return entry;
		}

		/// A property's value as JSON, covering the SerializedPropertyType set.
		///
		/// Every branch matters: a model writing a description field will hit
		/// String, a shader property float Float, a toggled feature Boolean, a
		/// bone list Generic, and an object reference ObjectReference. Anything
		/// unhandled returns its raw text rather than being silently dropped, so
		/// an unexpected type is visible instead of looking like a null value.
		private static Json SerializePropertyValue(SerializedProperty property)
		{
			switch (property.propertyType)
			{
				case SerializedPropertyType.Integer:
					{
						Json number = new Json();
						number.OpenArray();
						number.Add(property.longValue);
						number.CloseArray();
						return number;
					}
				case SerializedPropertyType.Enum:
					{
						// displayName is the enum's human-readable member name
						// ("Running", "Enabled"). There is no enumDisplayName on
						// SerializedProperty; enumNames[] holds the raw names and
						// enumValueIndex the selected index, so displayName is the
						// readable one a caller can write straight back.
						Json entry = new Json();
						entry.OpenArray();
						entry.Add(property.enumValueIndex);
						entry.AddString(property.displayName ?? "");
						entry.CloseArray();
						return entry;
					}
				case SerializedPropertyType.Boolean:
					return Json.Bool(property.boolValue);
				case SerializedPropertyType.Float:
					{
						Json value = new Json();
						value.Add(property.doubleValue);
						return value;
					}
				case SerializedPropertyType.String:
					return Json.Str(property.stringValue ?? "");
				case SerializedPropertyType.Color:
					return VectorToJson(new Vector3(property.colorValue.r, property.colorValue.g, property.colorValue.b));
				case SerializedPropertyType.Vector3:
					return VectorToJson(property.vector3Value);
				case SerializedPropertyType.Vector2:
					return VectorToJson(new Vector3(property.vector2Value.x, property.vector2Value.y, 0f));
				case SerializedPropertyType.Vector4:
					return VectorToJson(new Vector3(property.vector4Value.x, property.vector4Value.y, property.vector4Value.z));
				case SerializedPropertyType.Quaternion:
					{
						Quaternion q = property.quaternionValue;
						Json value = new Json();
						value.OpenArray();
						value.Add(q.x);
						value.Add(q.y);
						value.Add(q.z);
						value.Add(q.w);
						value.CloseArray();
						return value;
					}
				case SerializedPropertyType.ObjectReference:
					return Json.Str(property.objectReferenceValue == null
						? ""
						: property.objectReferenceValue.GetType().Name + ":" + property.objectReferenceValue.name);
				case SerializedPropertyType.LayerMask:
					{
						Json value = new Json();
						value.Add(property.intValue);
						return value;
					}
				case SerializedPropertyType.ManagedReference:
				case SerializedPropertyType.Generic:
					{
						// A nested struct or list: report it as a count plus a hint
						// rather than recursing unboundedly. A deep inspect call
						// already descended into its children via NextVisible, so
						// they are reachable by path in the same response.
						Json summary = new Json();
						summary.OpenObject();
						summary.AddString("kind", "nested; use property paths like \"" + property.propertyPath + ".Array.size\" or \"" + property.propertyPath + "[0]\"");
						summary.CloseObject();
						return summary;
					}
				default:
					// Return the VALUE, not the type. This branch is reached for
					// the property types above the switch cannot render -- a
					// Hash128, a managed reference that refused to resolve, an
					// enum-with-flags. The comment on SerializePropertyValue
					// promises "its raw text rather than being silently dropped,
					// so an unexpected type is visible"; a raw text that is the
					// type's own name is no text at all, and it repeated what
					// the caller already has in its hand. It has been returning
					// propertyType.ToString() since the initial commit, which
					// made the promise false for every unhandled type.
					string raw = SafePropertyValue(property);
					return Json.Str(property.propertyType + ":" + (raw.Length > 0 ? raw : "<empty>"));
			}
		}

		/// The property's current value as text, for a type SerializePropertyValue
		/// does not render.
		///
		/// Best effort by type rather than by reflection over every field:
		/// SerializedProperty's typed accessors are the only ones that survive a
		/// property of a type pi has never seen, and they are individually
		/// guarded because a property whose value cannot be read must still
		/// yield the other branches rather than abort the whole inspect.
		private static string SafePropertyValue(SerializedProperty property)
		{
			try
			{
				switch (property.propertyType)
				{
					case SerializedPropertyType.Hash128: return property.hash128Value.ToString();
					case SerializedPropertyType.Bounds: return property.boundsValue.ToString();
					case SerializedPropertyType.Rect: return property.rectValue.ToString();
					case SerializedPropertyType.AnimationCurve: return property.animationCurveValue == null ? "" : "curve";
					case SerializedPropertyType.BoundsInt: return property.boundsIntValue.ToString();
					case SerializedPropertyType.RectInt: return property.rectIntValue.ToString();
					case SerializedPropertyType.Enum:
						// enumNames plus the index, so the caller gets the same
						// shape AssignProperty accepts: a display name or an index.
						// SerializedPropertyType has no Char member -- a char is
						// serialized as Integer, which SerializePropertyValue
						// already renders.
						return property.enumValueIndex + " " + (property.displayName ?? "");
					default:
						// Anything still unrendered falls back to the raw text
						// Unity itself shows for it, which is the one accessor
						// that covers every remaining type.
						return property.stringValue ?? "";
				}
			}
			catch (Exception)
			{
				return "";
			}
		}

		private static bool IsBehaviourEnabled(Component component)
		{
			var behaviour = component as Behaviour;
			return behaviour == null || behaviour.enabled;
		}

		private static string MonoScriptFromComponent(Component component)
		{
			try
			{
				return MonoScript.FromMonoBehaviour(component as MonoBehaviour) != null
					? AssetDatabase.GetAssetPath(MonoScript.FromMonoBehaviour(component as MonoBehaviour))
					: "";
			}
			catch (Exception)
			{
				return "";
			}
		}

		private static string PathInScene(GameObject target)
		{
			string path = target.name;
			for (Transform parent = target.transform.parent; parent != null; parent = parent.parent)
			{
				path = parent.name + "/" + path;
			}
			return path;
		}

		// ---------------------------------------------------------- writing

		/// Writes one serialized property and applies it.
		///
		/// The `space` argument is the caller's explicit choice about what a
		/// write means. Defaulting to undoable scene space would silently make
		/// asset edits unrevertable, and defaulting to global would be worse.
		private static string SetProperty(Json request)
		{
			string gameObjectName = request.GetString("gameObject");
			string componentName = request.GetString("component");
			string propertyPath = request.GetString("property");
			string rawValue = request.GetString("value");
			string spaceName = request.GetString("space") ?? "scene";

			GameObject target = FindGameObjectByName(gameObjectName);
			if (target == null)
			{
				throw new Exception("No GameObject named \"" + gameObjectName + "\" in the active scene.");
			}

			string availableTypes;
			Component component = ResolveComponent(target, componentName, out availableTypes);
			if (component == null)
			{
				throw new Exception(
					"\"" + gameObjectName + "\" has no component \"" + componentName + "\". " +
					"It has: " + availableTypes
				);
			}

			var serialized = new SerializedObject(component);
			SerializedProperty property = serialized.FindProperty(propertyPath);
			if (property == null)
			{
				// Listing the real paths is the difference between a usable
				// error and a dead end.
				List<string> availablePaths = new List<string>();
				SerializedProperty it = serialized.GetIterator();
				bool enter = true;
				while (it.NextVisible(enter))
				{
					enter = false;
					availablePaths.Add(it.propertyPath);
					if (availablePaths.Count >= 40) break;
				}
				throw new Exception(
					"No property \"" + propertyPath + "\" on " + componentName + ". " +
					"Available properties: " + string.Join(", ", availablePaths.ToArray())
				);
			}

			// Undo.GetCurrentGroup returns an int; the group is what
			// RevertAllDownToGroup needs to roll a failed write back.
			int undoGroup = Undo.GetCurrentGroup();
			Undo.RecordObject(component, "pi set " + propertyPath);
			// The pre-write value, rendered through the same path the read side
			// uses, so the hint a failed write carries is the value the caller
			// would have got by reading it -- and not its type, which is what
			// this reported before SafePropertyValue existed.
			string before = property.propertyType + "=" + SafePropertyValue(property);

			try
			{
				AssignProperty(property, rawValue);
			}
			catch (Exception error)
			{
				Undo.RevertAllDownToGroup(undoGroup);
				throw new Exception(
					"Could not set " + propertyPath + " on " + componentName + " to \"" + rawValue + "\": " + error.Message +
					". The property is of type " + property.propertyType + "."
				);
			}

			serialized.ApplyModifiedProperties();
			EditorUtility.SetDirty(component);
			MarkDirtyForSpace(component, spaceName);

			Json result = new Json();
			result.OpenObject();
			result.AddString("gameObject", target.name);
			result.AddString("component", component.GetType().FullName);
			result.AddString("property", propertyPath);
			result.AddValue("value", SerializePropertyValue(property));
			result.AddString("space", spaceName);
			result.AddString("previousTypeAndHint", before);
			result.CloseObject();
			return result.ToString();
		}

		private static void MarkDirtyForSpace(Component component, string spaceName)
		{
			string assetPath = AssetDatabase.GetAssetPath(component);
			if (spaceName == "global" || (!string.IsNullOrEmpty(assetPath) && spaceName == "asset"))
			{
				AssetDatabase.SaveAssetIfDirty(component);
				return;
			}
			// Scene: mark the scene so Unity offers to save, and do NOT write to
			// disk -- the module checkpoint logic owns the .unity file.
			Scene scene = SceneManager.GetActiveScene();
			if (scene.IsValid() && scene.isDirty == false) EditorSceneManager.MarkSceneDirty(scene);
		}

		/// Parses and assigns a value, choosing the parse by the property's own
		/// type. Guessing from the string would let "1" write to a string field
		/// as text and to a float as a number, which is exactly the ambiguity a
		/// caller cannot see.
		private static void AssignProperty(SerializedProperty property, string raw)
		{
			string value = (raw ?? "").Trim();
			// Allow a caller to pass [1,2,3] for a Vector3 without JSON plumbing.
			string inner = value.StartsWith("[") && value.EndsWith("]") ? value.Substring(1, value.Length - 2) : value;

			switch (property.propertyType)
			{
				case SerializedPropertyType.String:
					property.stringValue = raw ?? "";
					return;
				case SerializedPropertyType.Boolean:
					property.boolValue = ParseBool(value);
					return;
				case SerializedPropertyType.Float:
					property.doubleValue = ParseDouble(inner);
					return;
				case SerializedPropertyType.Integer:
				case SerializedPropertyType.LayerMask:
					property.longValue = (long)ParseDouble(inner);
					return;
				case SerializedPropertyType.Enum:
					{
						// Accept either the display name or the raw index, because a
						// caller reading the value back sees the display name.
						string[] names = property.enumNames;
						if (names != null && names.Length > 0)
						{
							string[] lower = names.Select(n => n.ToLowerInvariant()).ToArray();
							int found = Array.IndexOf(lower, value.ToLowerInvariant());
							if (found >= 0) { property.enumValueIndex = found; return; }
						}
						property.enumValueIndex = (int)ParseDouble(inner);
						return;
					}
				case SerializedPropertyType.Color:
					{
						string[] parts = SplitNumbers(inner);
						property.colorValue = parts.Length >= 3
							? new Color(Pf(parts[0]), Pf(parts[1]), Pf(parts[2]))
							: Color.white;
						return;
					}
				case SerializedPropertyType.Vector3:
					{
						string[] parts = SplitNumbers(inner);
						property.vector3Value = parts.Length >= 3
							? new Vector3(Pf(parts[0]), Pf(parts[1]), Pf(parts[2]))
							: Vector3.zero;
						return;
					}
				case SerializedPropertyType.Vector2:
					{
						string[] parts = SplitNumbers(inner);
						property.vector2Value = parts.Length >= 2
							? new Vector2(Pf(parts[0]), Pf(parts[1]))
							: Vector2.zero;
						return;
					}
				case SerializedPropertyType.Vector4:
					{
						string[] parts = SplitNumbers(inner);
						property.vector4Value = parts.Length >= 4
							? new Vector4(Pf(parts[0]), Pf(parts[1]), Pf(parts[2]), Pf(parts[3]))
							: Vector4.zero;
						return;
					}
				case SerializedPropertyType.Quaternion:
					{
						string[] parts = SplitNumbers(inner);
						property.quaternionValue = parts.Length >= 4
							? new Quaternion(Pf(parts[0]), Pf(parts[1]), Pf(parts[2]), Pf(parts[3]))
							: Quaternion.identity;
						return;
					}
				case SerializedPropertyType.ObjectReference:
					{
						// Resolved by name rather than path: a caller reading the
						// value back sees "Type:Name", so it must accept the same.
						string typeName = value;
						string objectName = "";
						int colon = value.IndexOf(':');
						if (colon >= 0)
						{
							typeName = value.Substring(0, colon);
							objectName = value.Substring(colon + 1);
						}
						property.objectReferenceValue = ResolveObjectReference(typeName, objectName);
						return;
					}
				default:
					throw new Exception(
						"Cannot set a property of type " + property.propertyType +
						". Read it with unity_inspect and edit it through a snippet for these types."
					);
			}
		}

		private static UnityEngine.Object ResolveObjectReference(string typeName, string objectName)
		{
			// Empty name means "clear the reference", which is a legitimate edit.
			if (string.IsNullOrEmpty(objectName) && !typeName.Equals("none", StringComparison.OrdinalIgnoreCase))
			{
				return null;
			}
			if (typeName.Equals("none", StringComparison.OrdinalIgnoreCase)) return null;

			// Resolved by name rather than path: a caller reading the value back
			// sees "Type:Name", so it must accept the same. Resources
			// .FindObjectsOfTypeAll takes a single Type, not an array, so this
			// walks the matching types individually.
			foreach (Type type in AppDomain.CurrentDomain.GetAssemblies()
				.SelectMany(assembly => { try { return assembly.GetTypes(); } catch (Exception) { return new Type[0]; } })
				.Where(type => type.Name == typeName || type.FullName == typeName)
				.ToArray())
			{
				if (!typeof(UnityEngine.Object).IsAssignableFrom(type)) continue;
				try
				{
					foreach (UnityEngine.Object candidate in Resources.FindObjectsOfTypeAll(type))
					{
						if (candidate != null && candidate.name == objectName) return candidate;
					}
				}
				catch (Exception)
				{
					// A type FindObjectsOfTypeAll cannot handle; keep looking.
				}
			}
			return null;
		}

		private static string[] SplitNumbers(string value)
		{
			return value.Split(new[] { ',', ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
		}

		private static float Pf(string value)
		{
			return float.Parse(value.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture);
		}

		private static double ParseDouble(string value)
		{
			return double.Parse(value.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture);
		}

		private static bool ParseBool(string value)
		{
			if (value.Equals("true", StringComparison.OrdinalIgnoreCase) || value == "1") return true;
			if (value.Equals("false", StringComparison.OrdinalIgnoreCase) || value == "0") return false;
			throw new Exception("\"" + value + "\" is not a boolean; use true or false.");
		}

		// --------------------------------------------------------- transform

		/// Moves/rotates/scales a GameObject, in world or local space.
		private static string SetTransform(Json request)
		{
			string gameObjectName = request.GetString("gameObject");
			string spaceName = request.GetString("space") ?? "world";

			GameObject target = FindGameObjectByName(gameObjectName);
			if (target == null)
			{
				throw new Exception("No GameObject named \"" + gameObjectName + "\" in the active scene.");
			}

			Transform transform = target.transform;
			Vector3 before = spaceName == "local" ? transform.localPosition : transform.position;

			// Record BEFORE the first mutation, and validate every argument
			// before applying any of them.
			//
			// Both halves were wrong. Undo.RecordObject snapshots the object as
			// it is at the moment of the call, so recording after the writes
			// captured the already-changed state and Ctrl-Z restored nothing --
			// this file's header promises every write is undoable, and for the
			// transform tool it was not. And applying position before parsing
			// rotation meant a malformed rotation left a moved object behind
			// with no record of it, which is the one failure a tool making small
			// edits exists to prevent.
			Undo.RecordObject(transform, "pi set transform " + gameObjectName);

			string position = request.GetString("position");
			string rotation = request.GetString("rotationEuler");
			string scale = request.GetString("scale");

			// Parsed up front, so a bad number fails the whole call with nothing
			// written, rather than partway through.
			Vector3? parsedPosition = null;
			Vector3? parsedRotation = null;
			Vector3? parsedScale = null;
			if (!string.IsNullOrEmpty(position))
			{
				string[] parts = SplitNumbers(position);
				if (parts.Length < 3) throw new Exception("position needs three numbers, got \"" + position + "\".");
				parsedPosition = new Vector3(Pf(parts[0]), Pf(parts[1]), Pf(parts[2]));
			}
			if (!string.IsNullOrEmpty(rotation))
			{
				string[] parts = SplitNumbers(rotation);
				if (parts.Length < 3) throw new Exception("rotationEuler needs three numbers, got \"" + rotation + "\".");
				parsedRotation = new Vector3(Pf(parts[0]), Pf(parts[1]), Pf(parts[2]));
			}
			if (!string.IsNullOrEmpty(scale))
			{
				string[] parts = SplitNumbers(scale);
				if (parts.Length < 3) throw new Exception("scale needs three numbers, got \"" + scale + "\".");
				parsedScale = new Vector3(Pf(parts[0]), Pf(parts[1]), Pf(parts[2]));
			}

			if (parsedPosition.HasValue)
			{
				if (spaceName == "local") transform.localPosition = parsedPosition.Value;
				else transform.position = parsedPosition.Value;
			}
			if (parsedRotation.HasValue)
			{
				if (spaceName == "local") transform.localEulerAngles = parsedRotation.Value;
				else transform.eulerAngles = parsedRotation.Value;
			}
			// Scale is always local: localScale is the only scale a Transform has,
			// and world scale does not exist.
			if (parsedScale.HasValue) transform.localScale = parsedScale.Value;

			EditorUtility.SetDirty(target);
			Scene scene = SceneManager.GetActiveScene();
			if (scene.IsValid()) EditorSceneManager.MarkSceneDirty(scene);

			Json result = new Json();
			result.OpenObject();
			result.AddString("gameObject", target.name);
			result.AddString("space", spaceName);
			result.AddArray("position", VectorToJson(transform.position));
			result.AddArray("localPosition", VectorToJson(transform.localPosition));
			result.AddArray("rotationEuler", VectorToJson(transform.eulerAngles));
			result.AddArray("localScale", VectorToJson(transform.localScale));
			result.AddString("previousPosition", string.Format(CultureInfo.InvariantCulture, "{0},{1},{2}", before.x, before.y, before.z));
			result.CloseObject();
			return result.ToString();
		}
	}
}
