// Part of the pi Unity bridge plugin. See PiUnityBridge.Core.cs for the
// protocol overview. The whole plugin is one assembly of partial classes, so
// Unity compiles it into Assembly-CSharp-Editor with no asmdef and no
// package dependencies, which is what lets it drop into any Unity 2021+
// project (a configured VRChat world, an existing game) that pi was pointed at
// rather than generated.
//
// VRChat world spawn points: adding one, and reporting the configured set.
//
// A spawn point is the only object in a world VRChat REQUIRES -- the
// descriptor's spawns list decides where a player appears, and an empty list
// is a build error -- and the only one whose configuration nothing draws. No
// gizmo, no component, no collider: from the Scene view a spawn point is an
// ordinary empty Transform, so "is this world spawnable, and where does it
// put players" is a question a report answers and the hierarchy cannot.
//
// NO COMPONENT IS ADDED, AND NONE IS NEEDED
// VRChat reads a spawn point's transform and nothing else. A collider, a
// renderer, a trigger: all of those are the author's business, and adding a
// MeshRenderer to make the object findable would change what the world renders
// without being asked to. The object this action creates is bare, and the
// report says so rather than leaving it as an omission.
//
// EVERY COUNT PRINTED IS READ BACK
// The spawns array is rewritten, never appended to in place, so the length
// this action intended is not evidence that the write took. Every count in the
// output below comes from reading the field off the live descriptor AFTER the
// write.

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Reflection;
using System.Text;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace PiUnity
{
	public static class PiSpawnPoints
	{
		/// The name a fresh spawn point gets. The same default
		/// PiWorldCreate.DefaultObjectName hands out for the descriptor, so a
		/// world never ends up carrying both spellings of one thing.
		public const string DefaultSpawnName = "Pi_SpawnPoint";

		// ================================================================ add

		/// Puts one spawn point on the descriptor's spawns list, creating the
		/// GameObject when the caller named none.
		///
		/// An existing object is USED, never copied: two objects at one spawn
		/// position is a world whose spawn list disagrees with its hierarchy, and
		/// the copy is the half that nobody maintains afterwards.
		/// Two Euler triples describe the same orientation unless the angles
		/// differ by a whole turn on any axis.
		private static bool SameRotation(Vector3 a, Vector3 b)
		{
			return Mathf.Approximately(Mathf.DeltaAngle(a.x, b.x), 0f)
				&& Mathf.Approximately(Mathf.DeltaAngle(a.y, b.y), 0f)
				&& Mathf.Approximately(Mathf.DeltaAngle(a.z, b.z), 0f);
		}

		public static string AddSpawnPoint(string objectName, string position, string rotation)
		{
			Component descriptor = PiUdon.SceneDescriptor();
			if (descriptor == null)
			{
				throw new Exception(
					"No VRC_SceneDescriptor in the open scene (looked for "
					+ string.Join(", ", PiUdon.SceneDescriptorNames) + "), so no spawn point was added. "
					+ "The descriptor is what HOLDS the spawns list, so a spawn point cannot be added without "
					+ "one -- put the VRCWorld prefab in the scene, then run this again."
				);
			}

			// Parsed before anything is created or moved: a typo in the coordinates
			// must not leave an empty GameObject behind in a world whose spawns
			// list still has no entry for it.
			Vector3 positionValue = ParseTriple(position, "position");
			Vector3 rotationValue = ParseTriple(rotation, "rotation");

			var report = new StringBuilder();
			report.AppendLine("--- add-spawn-point ---");
			report.AppendLine("descriptor: " + descriptor.GetType().FullName + " on \""
				+ descriptor.gameObject.name + "\"");

			GameObject spawn;
			bool created;
			string wanted = (objectName ?? "").Trim();
			if (wanted.Length == 0)
			{
				spawn = new GameObject(PiWorldCreate.UniqueName(DefaultSpawnName));
				created = true;
				try
				{
					Undo.RegisterCreatedObjectUndo(spawn, "pi add spawn point");
				}
				catch (Exception error)
				{
					// Same standard the other write paths hold to: a registration
					// Unity refuses is reported, never swallowed, because an
					// un-undoable object looks exactly like a registered one.
					Debug.LogWarning("[pi] " + spawn.name + " was created but not registered for undo: " + error.Message);
				}
				report.AppendLine("created \"" + spawn.name + "\" (no object name was given, so the default name was used, made unique)");
			}
			else
			{
				spawn = PiWorldCreate.FindSceneObject(wanted, out int matches);
				if (spawn == null)
				{
					throw new Exception(
						"No GameObject named \"" + wanted + "\" in the open scene, so nothing was added and no "
						+ "object was created under a name that reads as a reference to something that exists. "
						+ "Create the object first, or leave the name out entirely to have a fresh \""
						+ DefaultSpawnName + "\" created."
					);
				}
				if (matches > 1)
				{
					throw new Exception(
						matches + " objects in the open scene are named \"" + wanted + "\", so no spawn point was "
						+ "added rather than one of them being picked:\n  " + string.Join("\n  ", PathsNamed(wanted).ToArray())
						+ "\nName the one you mean by its hierarchy path (Root/Child), or rename the others."
					);
				}
				created = false;
				report.AppendLine("using the existing GameObject \"" + spawn.name + "\" -- it was not duplicated");
			}

			// Position and rotation are SET on the object and then READ BACK for
			// the report: an object that is a child of a moved parent does not
			// land where the requested vector says, and the value the world will
			// use is the one on the transform.
			Transform spawnTransform = spawn.transform;
			spawnTransform.position = positionValue;
			spawnTransform.rotation = Quaternion.Euler(rotationValue);
			Vector3 actualPosition = spawnTransform.position;
			Vector3 actualRotation = spawnTransform.eulerAngles;
			report.AppendLine("  position=" + Vec(actualPosition) + "  rotation=" + Vec(actualRotation)
				+ " (Euler degrees, read back off the transform)");
			// Euler angles are only defined modulo 360: a request for -90 and a
			// read back of 270 are the SAME orientation, so comparing the raw
			// triples calls a correct placement a mismatch.
			if (actualPosition != positionValue || !SameRotation(actualRotation, rotationValue))
			{
				report.AppendLine("  note: the transform did not settle on the requested values. That happens when the "
					+ "object is parented under something already moved or scaled; the values above are the ones it really has.");
			}
			if (actualRotation != rotationValue && SameRotation(actualRotation, rotationValue))
			{
				report.AppendLine("  requested rotation " + Vec(rotationValue) + " is the same orientation as "
					+ Vec(actualRotation) + ": Euler angles wrap at 360, so the two triples describe one rotation.");
			}
			report.AppendLine("  components on it: " + DescribeComponents(spawn)
				+ " -- left as they are. VRChat uses the transform position only, so a spawn point needs no collider;");
			report.AppendLine("  a visible marker (a MeshRenderer) is optional and is the author's choice.");

			if (!TryReadSpawns(descriptor, out FieldInfo spawnsField, out Array existingSpawns, out string readFailure))
			{
				throw new Exception(
					"Could not read the descriptor's spawns list, so nothing was written: " + readFailure
					+ " The object \"" + spawn.name + "\" has been moved to " + Vec(actualPosition) + " and is "
					+ (created ? "a new object you may want to delete." : "unchanged otherwise.")
				);
			}

			Type elementType = spawnsField.FieldType.GetElementType();
			int before = existingSpawns == null ? 0 : existingSpawns.Length;
			report.AppendLine("descriptor.spawns before: " + before + " entr" + (before == 1 ? "y" : "ies")
				+ " (declared type " + spawnsField.FieldType.FullName + ")");

			object entry = BuildEntry(elementType, spawnTransform, out string entryNote);
			if (entry == null)
			{
				throw new Exception(
					"The descriptor's spawns list is a " + spawnsField.FieldType.FullName + ", and this action "
					+ "cannot build an entry of that shape (" + entryNote + "). Nothing was written to the "
					+ "descriptor. Add the spawn point in the Inspector's Spawn Points array instead."
				);
			}
			report.AppendLine("new entry: " + entryNote);

			// A NEW array, because a serialized array cannot grow in place: the
			// length is stored in the serialization data, not in the array object,
			// so writing past the end of the old one would either throw or land
			// outside what the serializer ever sees. Copy, append, write back.
			Array grown = Array.CreateInstance(elementType, before + 1);
			if (existingSpawns != null) Array.Copy(existingSpawns, grown, before);
			grown.SetValue(entry, before);

			string undoState;
			try
			{
				Undo.RecordObject(descriptor, "pi add spawn point");
				undoState = "recorded as one undo step on the descriptor";
			}
			catch (Exception error)
			{
				undoState = "NOT registered (" + error.Message + "): the list is written but Ctrl-Z may not revert it";
			}

			if (!PiWorldToolkit.WriteFieldByName(descriptor, "spawns", grown))
			{
				throw new Exception(
					"The descriptor's spawns list could not be written. \"" + spawn.name + "\" is in the scene at "
					+ Vec(actualPosition) + " but is NOT in the world's spawn list -- see the Editor log for the "
					+ "reason. Add it by hand in the Inspector."
				);
			}
			report.AppendLine("undo: " + undoState);

			// The count reported is the one read back off the descriptor, never
			// the one this action built.
			VerifySpawns(descriptor, out int verifiedCount, out Transform verifiedEntry);
			report.AppendLine("descriptor.spawns verified by reading the field back: " + verifiedCount
				+ (verifiedCount == before + 1 ? " entries" : " entries -- EXPECTED " + (before + 1) + ", so the write did not take as intended")
				+ ", and entry [" + (verifiedCount - 1) + "] holds "
				+ (verifiedEntry == null ? "NOTHING" : "\"" + verifiedEntry.name + "\""));
			if (verifiedCount != before + 1 || verifiedEntry != spawnTransform)
			{
				report.AppendLine("  CONsequence: the spawn list on disk disagrees with what was just written. VRChat "
					+ "builds against the list, so open the descriptor in the Inspector before trusting this world.");
			}

			report.AppendLine("scene origin is world " + Vec(Vector3.zero)
				+ "; this spawn sits " + Vec(actualPosition - Vector3.zero) + " from it.");
			report.AppendLine("read the whole set with the \"spawn-points\" action.");
			report.AppendLine(MarkActiveSceneDirty()
				? "scene marked dirty (save it to keep the change)"
				: "no valid active scene, so nothing was marked dirty");
			return report.ToString();
		}

		// ========================================================== describe

		/// The world's configured spawn points and the descriptor settings that
		/// decide how they are used.
		public static string DescribeSpawnPoints()
		{
			var report = new StringBuilder();
			report.AppendLine("--- spawn points ---");

			Component descriptor = PiUdon.SceneDescriptor();
			if (descriptor == null)
			{
				report.AppendLine("no VRC_SceneDescriptor in the open scene (looked for "
					+ string.Join(", ", PiUdon.SceneDescriptorNames) + ").");
				report.AppendLine("A scene without a descriptor is not a world: there is no spawn list to read, and "
					+ "a spawn point cannot be added either, because the descriptor is what holds the list. "
					+ "Add the VRCWorld prefab, then run this again.");
				report.AppendLine("total: no spawn list could be read.");
				return report.ToString();
			}

			report.AppendLine("descriptor: " + descriptor.GetType().FullName + " on \""
				+ descriptor.gameObject.name + "\"");
			Vector3 origin = Vector3.zero;
			report.AppendLine("scene origin: " + Vec(origin) + " (world zero; every delta below is measured from it)");
			report.AppendLine("spawn settings (read from the descriptor):");
			foreach (string field in new[] { "spawnRadius", "spawnOrder", "spawnOrientation", "RespawnHeightY" })
			{
				AppendSetting(report, descriptor, field);
			}

			if (!TryReadSpawns(descriptor, out FieldInfo spawnsField, out Array spawns, out string reason))
			{
				// Distinct from an empty list: this is "the question could not be
				// asked", which is not "the answer is none".
				report.AppendLine("spawns: not available: " + reason);
				report.AppendLine("total: could not be read -- this is NOT the same as a world with no spawn points.");
				return report.ToString();
			}

			int total = spawns == null ? 0 : spawns.Length;
			report.AppendLine("spawns (descriptor.spawns, " + spawnsField.FieldType.FullName + "): "
				+ (spawns == null ? "null -- the descriptor has no spawn list object at all" : total + " entries"));

			if (total == 0)
			{
				report.AppendLine("This world has NO spawn point.");
				report.AppendLine("A world with no spawn point does not choose where players appear: they are placed "
					+ "at the scene origin, " + Vec(origin) + ", which is whatever object happens to sit at world zero --");
				report.AppendLine("often the inside of the floor. VRChat's own build check also refuses a world whose "
					+ "descriptor spawn list is empty, so this scene is not uploadable as it stands.");
				report.AppendLine("Add one with the \"add-spawn-point\" action.");
				report.AppendLine("total: " + total + " spawn point(s).");
				return report.ToString();
			}

			for (int index = 0; index < total; index++)
			{
				object entry = spawns.GetValue(index);
				Transform at = EntryTransform(entry);
				if (at == null)
				{
					report.AppendLine("  [" + index + "] <unassigned> (the entry is "
						+ (entry == null ? "null" : "a " + entry.GetType().Name + " with no Transform in it") + ")");
					report.AppendLine("      VRChat drops an unassigned entry at build, so this slot places nobody.");
					continue;
				}
				Vector3 position = at.position;
				report.AppendLine("  [" + index + "] \"" + at.name + "\"  " + DescribePath(at));
				report.AppendLine("      world position " + Vec(position)
					+ "   delta from the scene origin " + Vec(position - origin)
					+ "   local rotation " + Vec(at.localEulerAngles) + " (Euler degrees)");
			}

			report.AppendLine("total: " + total + " spawn point(s) in descriptor.spawns"
				+ (HasZeroSpawnRadius(descriptor) ? "; spawnRadius is 0, so every spawn places players exactly on its own position."
					: ""));
			report.AppendLine("spawnOrder and spawnOrientation above are the descriptor's DEFAULTS; an entry's own "
				+ "settings override them when they are set, and read them in the Inspector on the descriptor.");
			return report.ToString();
		}

		private static bool HasZeroSpawnRadius(Component descriptor)
		{
			if (!TryReadField(descriptor, "spawnRadius", out object value, out _)) return false;
			return value is float radius && Mathf.Approximately(radius, 0f);
		}

		// ========================================================== spawns I/O

		/// The descriptor's spawns field and its value, with "no such field",
		/// "could not read" and "read as null" kept apart. Read through the
		/// FieldInfo rather than through ReadFieldByName, which returns null for
		/// a field that threw as well as for a field that is null, and those two
		/// mean opposite things here.
		private static bool TryReadSpawns(Component descriptor, out FieldInfo field, out Array spawns, out string reason)
		{
			field = Bridge.FindInstanceField(descriptor.GetType(), "spawns");
			if (field == null)
			{
				spawns = null;
				reason = "this descriptor type has no \"spawns\" field (looked on "
					+ descriptor.GetType().FullName + " and its base types; SDK versions differ here).";
				return false;
			}
			if (!field.FieldType.IsArray)
			{
				spawns = null;
				reason = "descriptor.spawns is a " + field.FieldType.FullName + ", not an array, so its entries "
					+ "cannot be counted or appended to here.";
				return false;
			}
			try
			{
				object value = field.GetValue(descriptor);
				spawns = value as Array;
				if (value != null && spawns == null)
				{
					reason = "descriptor.spawns read back as a " + value.GetType().FullName + ", which is not an array.";
					return false;
				}
				reason = "";
				return true;
			}
			catch (Exception error)
			{
				spawns = null;
				reason = "descriptor.spawns could not be read (" + error.Message + ").";
				return false;
			}
		}

		/// A fresh spawns entry holding `transform`, in whatever shape the
		/// descriptor declares.
		///
		/// The declared element type decides, because the two SDK generations do
		/// not agree: one stores a bare Transform[] and the other stores an array
		/// of a wrapper class that carries a Transform among its own fields. An
		/// entry built for the wrong one does not fit the array at all, and the
		/// write fails on a type mismatch rather than on a missing spawn.
		private static object BuildEntry(Type elementType, Transform transform, out string note)
		{
			if (elementType.IsInstanceOfType(transform))
			{
				note = "the element type is " + elementType.Name + ", so the Transform is the entry";
				return transform;
			}
			if (elementType.IsAbstract || elementType.IsInterface || elementType.IsArray || elementType.IsPrimitive)
			{
				note = elementType.FullName + " is not a constructible entry type";
				return null;
			}
			object created;
			try
			{
				created = Activator.CreateInstance(elementType);
			}
			catch (Exception error)
			{
				note = "a " + elementType.FullName + " could not be constructed (" + error.Message + ")";
				return null;
			}
			FieldInfo slot = FindTransformSlot(elementType);
			if (slot == null)
			{
				note = elementType.FullName + " carries no Transform field to put the object in";
				return null;
			}
			try
			{
				slot.SetValue(created, transform);
			}
			catch (Exception error)
			{
				note = elementType.FullName + "." + slot.Name + " could not be set (" + error.Message + ")";
				return null;
			}
			// Everything else on the wrapper is left at what its constructor
			// produced, which is what the Inspector does for a freshly dragged
			// entry; the report names the fields so the author can see what the
			// new entry actually carries.
			note = "a new " + elementType.Name + " with " + slot.Name + "=\"" + transform.name
				+ "\"; its other fields (" + DescribeFields(elementType, slot) + ") are at their constructed defaults";
			return created;
		}

		/// The Transform an entry points at, whether the entry IS a Transform or
		/// wraps one. Null for an unassigned slot.
		private static Transform EntryTransform(object entry)
		{
			if (entry is Transform direct) return direct;
			if (entry == null) return null;
			FieldInfo slot = FindTransformSlot(entry.GetType());
			if (slot == null) return null;
			try { return slot.GetValue(entry) as Transform; } catch (Exception) { return null; }
		}

		/// A field that can hold the spawn Transform: the SDK's own name first,
		/// then any field of exactly that type.
		private static FieldInfo FindTransformSlot(Type type)
		{
			for (Type current = type; current != null; current = current.BaseType)
			{
				FieldInfo named = current.GetField("transform",
					BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
				if (named != null && named.FieldType == typeof(Transform)) return named;
			}
			for (Type current = type; current != null; current = current.BaseType)
			{
				foreach (FieldInfo candidate in current.GetFields(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance))
				{
					if (candidate.FieldType == typeof(Transform)) return candidate;
				}
			}
			return null;
		}

		/// The field names on an entry type apart from the one holding the
		/// Transform, for the report line that says what a new entry carries.
		private static string DescribeFields(Type type, FieldInfo skip)
		{
			var names = new List<string>();
			for (Type current = type; current != null && current != typeof(object); current = current.BaseType)
			{
				foreach (FieldInfo field in current.GetFields(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance))
				{
					if (field == skip || names.Contains(field.Name)) continue;
					names.Add(field.Name);
				}
			}
			return names.Count == 0 ? "none" : string.Join(", ", names.ToArray());
		}

		/// The entry count read off the live descriptor, plus the Transform its
		/// last entry holds. Every count this file prints about a written list
		/// comes from here rather than from the array that was built.
		private static void VerifySpawns(Component descriptor, out int verifiedCount, out Transform lastEntry)
		{
			verifiedCount = -1;
			lastEntry = null;
			if (!TryReadSpawns(descriptor, out _, out Array spawns, out _)) return;
			if (spawns == null) return;
			verifiedCount = spawns.Length;
			if (verifiedCount == 0) return;
			lastEntry = EntryTransform(spawns.GetValue(verifiedCount - 1));
		}

		// ======================================================== field reads

		/// A field's value, or the reason it could not be read. "No such field"
		/// and "the read threw" are facts about this SDK version; neither is a
		/// zero, and neither is printed as one.
		private static bool TryReadField(Component component, string fieldName, out object value, out string reason)
		{
			value = null;
			reason = "";
			if (component == null)
			{
				reason = "there is no component to read it from";
				return false;
			}
			FieldInfo field = Bridge.FindInstanceField(component.GetType(), fieldName);
			if (field == null)
			{
				reason = "no \"" + fieldName + "\" field on " + component.GetType().FullName + " or its base types";
				return false;
			}
			try
			{
				value = field.GetValue(component);
				return true;
			}
			catch (Exception error)
			{
				reason = "\"" + fieldName + "\" exists but could not be read (" + error.Message + ")";
				return false;
			}
		}

		private static void AppendSetting(StringBuilder report, Component descriptor, string fieldName)
		{
			if (TryReadField(descriptor, fieldName, out object value, out string reason))
			{
				report.AppendLine("  " + fieldName + "=" + Render(value));
				return;
			}
			report.AppendLine("  " + fieldName + "=<not available: " + reason + ">");
		}


		// ========================================================== rendering

		/// "x,y,z" in the invariant culture, so a locale with a comma decimal
		/// separator cannot make one number read as three.
		private static string Vec(Vector3 value)
		{
			return "(" + Num(value.x) + ", " + Num(value.y) + ", " + Num(value.z) + ")";
		}

		private static string Num(float value)
		{
			return value.ToString("0.###", CultureInfo.InvariantCulture);
		}

		private static string Render(object value)
		{
			return Convert.ToString(value, CultureInfo.InvariantCulture);
		}

		private static string DescribePath(Transform transform)
		{
			var names = new List<string>();
			for (Transform current = transform; current != null; current = current.parent) names.Insert(0, current.name);
			return names.Count == 0 ? "<no path>" : string.Join("/", names.ToArray());
		}

		private static List<string> PathsNamed(string name)
		{
			var paths = new List<string>();
			foreach (Transform child in UnityEngine.Object.FindObjectsOfType<Transform>(true))
			{
				if (child.gameObject.name == name) paths.Add(DescribePath(child));
			}
			if (paths.Count == 0) paths.Add("<no object with that name is in the hierarchy>");
			return paths;
		}

		private static string DescribeComponents(GameObject target)
		{
			var names = new StringBuilder();
			foreach (Component component in target.GetComponents<Component>())
			{
				// A missing script comes back as null; naming it would name nothing.
				if (component == null) continue;
				if (names.Length > 0) names.Append(", ");
				names.Append(component.GetType().Name);
			}
			return names.Length == 0 ? "none at all" : names.ToString();
		}

		// ============================================================= parsing

		/// Three invariant numbers, or a throw.
		///
		/// Malformed input is refused rather than read as zero: a spawn silently
		/// placed at world zero because "1,2o,3" failed to parse looks exactly
		/// like a spawn that was asked for and honoured.
		private static Vector3 ParseTriple(string raw, string label)
		{
			string text = (raw ?? "").Trim();
			if (text.Length == 0) return Vector3.zero;
			string[] parts = text.Split(',');
			if (parts.Length != 3)
			{
				throw new Exception(
					"add-spawn-point " + label + " must be three numbers separated by commas, such as \"0,1.5,0\". "
					+ "Got \"" + text + "\" (" + parts.Length + " value"
					+ (parts.Length == 1 ? "" : "s") + "). Leave it out entirely for 0,0,0."
				);
			}
			return new Vector3(
				ParseComponent(parts[0], text, label, "x"),
				ParseComponent(parts[1], text, label, "y"),
				ParseComponent(parts[2], text, label, "z"));
		}

		private static float ParseComponent(string part, string whole, string label, string axis)
		{
			if (float.TryParse(part.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out float value)) return value;
			throw new Exception(
				"add-spawn-point " + label + " \"" + whole + "\" does not parse: \"" + part.Trim() + "\" on " + axis
				+ " is not a number. Numbers are read with the invariant culture, so a decimal COMMA ("
				+ label + "=\"1,5\") reads as two values and is rejected -- use a decimal point."
			);
		}

		private static bool MarkActiveSceneDirty()
		{
			Scene scene = SceneManager.GetActiveScene();
			if (!scene.IsValid()) return false;
			EditorSceneManager.MarkSceneDirty(scene);
			return true;
		}
	}
}