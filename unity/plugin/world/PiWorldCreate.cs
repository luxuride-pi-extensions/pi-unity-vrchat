// Part of the pi Unity bridge plugin. See PiUnityBridge.Core.cs for the
// protocol overview. The whole plugin is one assembly of partial classes, so
// Unity compiles it into Assembly-CSharp-Editor with no asmdef and no
// package dependencies, which is what lets it drop into any Unity 2021+
// project (a configured VRChat world, an existing game) that pi was pointed at
// rather than generated.
//
// World CREATION helpers: put a VRChat world behaviour on a GameObject, and
// convert a Unity Light into a light volume.
//
// Both operations are ones an author does by hand and regrets not having done:
// the behaviours are the difference between a world that syncs/persists and one
// that does not, and converting lights is what a Quest publish asks for after
// the PC scene is already built.
//
// NO BEHAVIOUR NAME IS DECLARED HERE
// The list is PiUdon.WorldBehaviourNames, read from there rather than
// re-declared, because the two SDK generations spell the same job differently
// (VRC_SceneDescriptor vs VRCSceneDescriptor) and a second copy of that list
// drifts out of step with the one the `find-behaviour` error prints.

using System;
using System.Globalization;
using System.Reflection;
using System.Text;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace PiUnity
{
	public static class PiWorldCreate
	{
		// Verified from red.sim.lightvolumes 2.1.3 (see PiLightVolumes.cs): the
		// package's asmdef exposes these two types in the VRCLightVolumes
		// namespace, and its asmdef is not referenced by this plugin.
		public const string LightVolumesPackage = "red.sim.lightvolumes";
		public const string PointLightVolumeType = "VRCLightVolumes.PointLightVolume";

		// ------------------------------------------------------------ behaviours

		/// Adds a VRChat world behaviour to a GameObject, creating the GameObject
		/// when it does not exist yet.
		public static string CreateBehaviour(string behaviourName, string objectName, string fieldAssignments)
		{
			string requested = (behaviourName ?? "").Trim();
			if (requested.Length == 0)
			{
				throw new Exception(
					"create-behaviour requires a behaviour name, such as VRC.SDK3.Components.VRCObjectSync or VRCObjectSync. "
					+ "Known world behaviour names: " + string.Join(", ", PiUdon.WorldBehaviourNames) + "."
				);
			}

			// VrcResolve.Type tries the FULL name first and only then falls back to
			// a simple-name match across every loaded assembly, so a caller who
			// only knows "VRCObjectSync" still lands on the SDK3 type while a
			// caller who knows the full name is never answered with the legacy one.
			Type type = VrcResolve.Type(requested);
			if (type == null)
			{
				throw new Exception(
					"\"" + requested + "\" does not exist in any loaded assembly, so nothing was created. World "
					+ "behaviours ship as compiled SDK plugin DLLs, so a name this project does not have cannot be "
					+ "resolved from here. Known world behaviour names: "
					+ string.Join(", ", PiUdon.WorldBehaviourNames) + "."
				);
			}
			if (!typeof(Component).IsAssignableFrom(type))
			{
				throw new Exception(
					type.FullName + " (" + type.Assembly.GetName().Name + ") is not a Component, so it cannot be added "
					+ "to a GameObject. A world behaviour has to be a MonoBehaviour."
				);
			}

			var report = new StringBuilder();
			report.AppendLine("--- create-behaviour: " + requested + " ---");
			// The RESOLVED name, never the requested one: the caller has to be
			// able to see which class actually got the behaviour.
			report.AppendLine("resolved " + type.FullName + " [" + type.Assembly.GetName().Name + "]");

			string desired = string.IsNullOrEmpty(objectName) || objectName.Trim().Length == 0
				? DefaultObjectName(type)
				: objectName.Trim();
			GameObject target = ResolveOrCreate(desired, out bool created);
			report.AppendLine((created ? "created " : "using the existing ") + "GameObject \"" + target.name + "\"");
			if (!created)
			{
				report.AppendLine("  no second object was made; every later call naming \"" + target.name + "\" resolves to this one.");
			}

			// Reuse rather than duplicate: two of one behaviour on one GameObject
			// is a build-time conflict for most of them (two descriptors, two
			// pickups fighting over the same rigidbody), and it fails as a
			// warning nobody reads rather than as an error.
			Component existing = target.GetComponent(type);
			if (existing != null)
			{
				report.AppendLine(type.FullName + " was already on \"" + target.name + "\", so nothing was added "
					+ "and no fields were written to it.");
				report.AppendLine("  read it with " + InspectCall(target, type) + " -- or delete the component first to have it re-added.");
				report.AppendLine("scene unchanged");
				return report.ToString();
			}

			Component added;
			// Undo.AddComponent, not AddComponent followed by RecordObject on the
			// GameObject: a record taken after the add snapshots a state that
			// already contains the component, so the "undo step" it registers
			// cannot undo the add. PiUdon.AddBehaviourComponent does the
			// undoable add (and, for a UdonSharp script, the package's own setup).
			bool udonSharpSetup;
			try
			{
				// Goes through PiUdon.AddBehaviourComponent rather than a plain
				// AddComponent so that (a) a UdonSharp script is added the way
				// its own package adds it -- plain AddComponent leaves the proxy
				// without the UdonBehaviour it compiles to, and every field set
				// below would then be thrown away at build time -- and (b) the
				// add is undoable.
				added = PiUdon.AddBehaviourComponent(target, type, "pi add " + type.Name, out udonSharpSetup);
			}
			catch (Exception error)
			{
				throw new Exception(
					"Could not add " + type.FullName + " to \"" + target.name + "\": " + error.Message
					+ ". The behaviour's own requirements may be missing (a pickup needs a rigidbody, for instance)."
				);
			}
			if (udonSharpSetup)
			{
				report.AppendLine("  added through UdonSharp, so the proxy has its UdonBehaviour and the field values "
					+ "below are what the Udon program will run with.");
				// A UdonSharp script with no program asset is not a working
				// script: it never compiles, and the component sitting on the
				// object is the only evidence anything went wrong.
				if (PiUdon.LastCreatedProgramAsset != null)
					report.AppendLine("  created the UdonSharp program asset it needed: " + PiUdon.LastCreatedProgramAsset
						+ " (a hand-written script does not get one automatically, and UdonSharp cannot compile the behaviour without it)");
			}
			report.AppendLine("added " + type.FullName);
			report.AppendLine("undo: one Ctrl-Z removes it (added through Undo.AddComponent)");

			if (!string.IsNullOrEmpty(fieldAssignments) && fieldAssignments.Trim().Length > 0)
			{
				ApplyFieldAssignments(report, added, fieldAssignments);
			}

			if (udonSharpSetup)
			{
				// A UdonSharp proxy holds its own copy of every field, and the
				// UdonBehaviour beside it holds the values the program will
				// actually run with. Writing the proxy alone leaves the program
				// stale until something copies them across -- and udon-inventory
				// reads the PROGRAM, so without this a value set here reads back
				// as absent, which looks exactly like a lost write.
				string copyFailure = PiPackages.CopyProxyToUdon(added);
				if (copyFailure == null)
				{
					report.AppendLine("copied the field values into the Udon program, so the running behaviour sees them.");
				}
				else
				{
					report.AppendLine("WARNING: " + copyFailure);
					report.AppendLine("  The fields on the UdonSharpBehaviour are set, but the Udon program still holds");
					report.AppendLine("  its own values, so the change is discarded at build. See the Editor log.");
				}
			}

			if (IsSceneDescriptorType(type))
			{
				report.AppendLine("CONSEQUENCE: this world now has a scene descriptor whose Spawns list is empty.");
				report.AppendLine("  A descriptor with no spawn point is a BUILD-TIME error -- VRChat will not upload the");
				report.AppendLine("  world -- and no player can enter it. This action does not add a spawn for you:");
				report.AppendLine("  put a spawn point GameObject in the scene and list it in the descriptor's Spawns");
				report.AppendLine("  array in the Inspector. action \"create-behaviour\" alone is not a working world.");
			}

			report.AppendLine("read it back with " + InspectCall(target, type));
			report.AppendLine(MarkActiveSceneDirty()
				? "scene marked dirty (save it to keep the change)"
				: "no valid active scene, so nothing was marked dirty");
			return report.ToString();
		}

		/// Where a behaviour lands when the caller named no object.
		///
		/// The descriptor is the exception: its spawn points are the world's
		/// spawn list, so it gets a spawn-point name rather than "Pi_VRCSceneDescriptor",
		/// which would read as if the object were the descriptor.
		private static string DefaultObjectName(Type type)
		{
			return IsSceneDescriptorType(type) ? "Pi_SpawnPoint" : "Pi_" + type.Name;
		}

		private static bool IsSceneDescriptorType(Type type)
		{
			foreach (string name in PiUdon.SceneDescriptorNames)
			{
				Type known = VrcResolve.Type(name);
				if (known != null && known == type) return true;
			}
			return false;
		}

		/// Applies "Field=Value, Field=Value" pairs, one failure at a time.
		///
		/// A pair that cannot be written is reported and the rest still go in: a
		/// caller reading a component's fields back out of unity_inspect hits one
		/// wrong name per try, and aborting the whole call would cost them every
		/// pair they got right.
		private static void ApplyFieldAssignments(StringBuilder report, Component component, string assignments)
		{
			string[] tokens = assignments.Split(',');
			string pendingField = null;
			string pendingValue = null;

			for (int index = 0; index < tokens.Length; index++)
			{
				string token = tokens[index].Trim();
				if (token.Length == 0) continue;
				int equals = token.IndexOf('=');
				if (equals < 0)
				{
					// A token with no "=" continues the pair before it rather than
					// starting a broken one: "Color=1,0,0" is one assignment, and a
					// comma inside a value is indistinguishable from a separator
					// otherwise.
					if (pendingField == null)
					{
						report.AppendLine("  FAILED \"" + token + "\": not a Field=Value pair");
					}
					else
					{
						pendingValue += "," + token;
					}
					continue;
				}
				if (pendingField != null) WritePair(report, component, pendingField, pendingValue);
				pendingField = token.Substring(0, equals).Trim();
				pendingValue = token.Substring(equals + 1).Trim();
			}
			if (pendingField != null) WritePair(report, component, pendingField, pendingValue);
		}

		private static void WritePair(StringBuilder report, Component component, string fieldName, string value)
		{
			if (fieldName.Length == 0)
			{
				report.AppendLine("  FAILED \"=" + value + "\": no field name before the \"=\"");
				return;
			}
			if (TryWriteField(component, fieldName, value, out object written, out string reason))
			{
				report.AppendLine("  applied  " + fieldName + " = " + DescribeValue(written));
			}
			else
			{
				report.AppendLine("  FAILED   " + fieldName + ": " + reason);
			}
		}

		/// A wire string as the value a field will actually accept.
		///
		/// The FIELD's type decides, never the string's shape: the same field
		/// across two SDK generations disagrees about whether "true" is a bool or
		/// a 0/1 float, and writing the wrong shape either throws deep in Unity's
		/// serializer or lands a zero nobody notices. A type with no coercion here
		/// comes back as a reported failure naming the type, not as an exception.
		private static bool TryWriteField(Component component, string fieldName, string value, out object written, out string reason)
		{
			written = null;
			reason = "";
			FieldInfo field = Bridge.FindInstanceField(component.GetType(), fieldName);
			if (field == null)
			{
				reason = "no field \"" + fieldName + "\" on " + component.GetType().FullName
					+ " (SDK versions differ here; read the real fields with unity_inspect)";
				return false;
			}
			if (!Coerce(field.FieldType, value, out object coerced, out reason)) return false;
			return TryWriteField(component, fieldName, coerced, out written, out reason);
		}

		private static bool Coerce(Type type, string value, out object coerced, out string problem)
		{
			coerced = null;
			problem = "";

			if (type == typeof(string)) { coerced = value; return true; }

			if (type.IsEnum)
			{
				string[] names = Enum.GetNames(type);
				string[] lowered = new string[names.Length];
				for (int index = 0; index < names.Length; index++) lowered[index] = names[index].ToLowerInvariant();
				// Member name first, because a value read back out of unity_inspect
				// arrives as the name; the underlying index is accepted because an
				// enum a caller did not resolve is sometimes just a number.
				int ordinal = Array.IndexOf(lowered, value.ToLowerInvariant());
				if (ordinal >= 0) { coerced = Enum.ToObject(type, ordinal); return true; }
				if (int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out int byNumber)
					&& byNumber >= 0 && byNumber < names.Length)
				{
					coerced = Enum.ToObject(type, byNumber);
					return true;
				}
				problem = "\"" + value + "\" is not a member of " + type.Name
					+ " (members: " + string.Join(", ", names) + ")";
				return false;
			}

			if (type == typeof(bool))
			{
				if (bool.TryParse(value, out bool flag)) { coerced = flag; return true; }
				string lower = value.ToLowerInvariant();
				if (lower == "1" || lower == "yes" || lower == "on") { coerced = true; return true; }
				if (lower == "0" || lower == "no" || lower == "off") { coerced = false; return true; }
				problem = "\"" + value + "\" is not a bool (true/false)";
				return false;
			}

			// Invariant culture throughout: a locale with a comma decimal
			// separator would otherwise turn "0.5" into 5, or fail outright.
			if (IsNumeric(type))
			{
				try
				{
					coerced = Convert.ChangeType(value, type, CultureInfo.InvariantCulture);
					return true;
				}
				catch (Exception error)
				{
					problem = "\"" + value + "\" is not a valid " + type.Name + " (" + error.Message.Trim() + ")";
					return false;
				}
			}

			if (type == typeof(Color)) { coerced = ColorFromHex(value); return true; }

			if (typeof(UnityEngine.Object).IsAssignableFrom(type))
			{
				GameObject referenced = FindSceneObject(value, out _);
				if (referenced == null)
				{
					problem = "no scene GameObject named \"" + value + "\" (references are resolved by name)";
					return false;
				}
				if (type.IsInstanceOfType(referenced)) { coerced = referenced; return true; }
				Component component = referenced.GetComponent(type);
				if (component == null)
				{
					problem = "\"" + value + "\" carries no " + type.Name + " component";
					return false;
				}
				coerced = component;
				return true;
			}

			problem = "pi cannot build a " + type.Name + " from a string; set this one with unity_set_property";
			return false;
		}

		private static bool IsNumeric(Type type)
		{
			return type == typeof(float) || type == typeof(double) || type == typeof(int) || type == typeof(long)
				|| type == typeof(short) || type == typeof(byte) || type == typeof(uint) || type == typeof(ulong)
				|| type == typeof(sbyte) || type == typeof(ushort) || type == typeof(decimal);
		}


		// --------------------------------------------------------- light volumes

		/// Turns a Unity Light into a VRChat light volume, carrying the light's
		/// own values across rather than substituting defaults.
		public static string ConvertLight(string objectName, string lightType, string range, string intensity, string color, string keepOriginal)
		{
			string wanted = (objectName ?? "").Trim();
			if (wanted.Length == 0)
			{
				throw new Exception(
					"convert-light requires the name of the GameObject carrying the Light. There is no default "
					+ "object to convert: find the light first (unity_inspect, or unity_find), then name it."
				);
			}

			// Parsed before anything is written: an exception thrown once the volume
			// is on the object would leave a half-converted light behind.
			bool keep = ParseSwitch(keepOriginal, true);

			GameObject target = FindSceneObject(wanted, out int matches);
			if (target == null) throw new Exception("No GameObject named \"" + wanted + "\" in the open scene.");

			Light light = target.GetComponent<Light>();
			if (light == null)
			{
				throw new Exception(
					"\"" + target.name + "\" carries no UnityEngine.Light, so there is nothing to convert. "
					+ "It has: " + DescribeComponents(target) + "."
				);
			}

			var report = new StringBuilder();
			report.AppendLine("--- convert-light: " + target.name + " ---");
			report.AppendLine("the object already exists; nothing new was named after it");
			if (matches > 1)
			{
				report.AppendLine("note: " + matches + " objects in the scene are named \"" + wanted
					+ "\"; this acted on one of them. Name it by scene path to be sure which.");
			}

			// The source values are read, not assumed: a converted light that
			// silently got range 10 and intensity 1 instead of the light's own is
			// the failure this action exists to avoid.
			report.AppendLine("source Light on \"" + target.name + "\":");
			report.AppendLine("  type=" + light.type + "  range=" + Num(light.range) + "  intensity=" + Num(light.intensity));
			report.AppendLine("  color=" + DescribeValue(light.color) + "  shadows=" + light.shadows
				+ "  spotAngle=" + Num(light.spotAngle));

			string volumeType = ResolveVolumeType(light, lightType, report);

			float rangeValue = light.range;
			if (!TryOverride(range, light.range, out float rangeWrite, out string rangeSource))
			{
				report.AppendLine("range \"" + (range ?? "").Trim() + "\" is not a number; using the light's own " + Num(light.range));
			}
			else
			{
				rangeValue = rangeWrite;
				report.AppendLine("range " + Num(rangeValue) + " (from " + rangeSource + ")");
			}

			float intensityValue = light.intensity;
			if (!TryOverride(intensity, light.intensity, out float intensityWrite, out string intensitySource))
			{
				report.AppendLine("intensity \"" + (intensity ?? "").Trim() + "\" is not a number; using the light's own " + Num(light.intensity));
			}
			else
			{
				intensityValue = intensityWrite;
				report.AppendLine("intensity " + Num(intensityValue) + " (from " + intensitySource + ")");
			}

			// An empty colour means the light's own; anything else goes through the
			// same hex parsing the bridge uses elsewhere, so one colour spelling
			// works across every action that takes one.
			Color colorValue = string.IsNullOrEmpty(color) || color.Trim().Length == 0
				? light.color
				: ColorFromHex(color);
			report.AppendLine("color " + DescribeValue(colorValue)
				+ (string.IsNullOrEmpty(color) ? " (the light's own)" : " (from \"" + color.Trim() + "\")"));

			// A volume with a zero range or intensity is legal, bakes cleanly and
			// contributes nothing, which reads as a broken conversion rather than
			// as an input the caller left at zero.
			if (rangeValue <= 0f)
			{
				report.AppendLine("WARNING: range is " + Num(rangeValue) + ". A volume with no range bakes and lights "
					+ "nothing -- pass range= if this light had none set.");
			}
			if (intensityValue <= 0f)
			{
				report.AppendLine("WARNING: intensity is " + Num(intensityValue) + ". A volume with no intensity bakes "
					+ "and lights nothing -- pass intensity= if this light had none set.");
			}

			Type volumeComponentType = VrcResolve.Type(PointLightVolumeType);
			if (volumeComponentType == null)
			{
				throw new Exception(
					LightVolumesPackage + " is not installed, so " + PointLightVolumeType + " does not exist. "
					+ "Add the package, then run again -- a converted light needs it to bake."
				);
			}

			Component existing = target.GetComponent(volumeComponentType);
			Component volume = existing;
			string undoState;
			if (existing != null)
			{
				// Reuse rather than duplicate: two volumes on one GameObject
				// double its light at bake time with nothing in the scene to say so.
				report.AppendLine(PointLightVolumeType + " was already on \"" + target.name + "\"; it was reconfigured, not duplicated.");
			}
			else
			{
				try
				{
					// Undo.AddComponent because a plain AddComponent followed by a
					// RecordObject on the GameObject is not undoable: the record
					// snapshots a state that already contains the component.
					volume = Undo.AddComponent(target, volumeComponentType) as Component;
				}
				catch (Exception error)
				{
					throw new Exception(
						"Could not add " + PointLightVolumeType + " to \"" + target.name + "\": " + error.Message + "."
					);
				}
				report.AppendLine("added " + PointLightVolumeType + " to \"" + target.name + "\"");
			}
			// The field writes below go to the COMPONENT, so the component is
			// what has to be recorded -- recording the GameObject would not
			// capture a single one of them.
			try
			{
				Undo.RecordObject(volume, "pi configure " + PointLightVolumeType + " on " + target.name);
				undoState = "one Ctrl-Z removes the component and every value below";
			}
			catch (Exception error)
			{
				undoState = "NOT registered (" + error.Message + ")";
			}
			report.AppendLine("undo: " + undoState);

			report.AppendLine("light volume values:");
			WriteVolumeField(report, volume, "Type", EnumVolumeType(volume, volumeType));
			WriteVolumeField(report, volume, "Range", rangeValue);
			WriteVolumeField(report, volume, "Intensity", intensityValue);
			WriteVolumeField(report, volume, "Color", colorValue);

			if (volumeType == "SpotLight")
			{
				report.AppendLine("  note: the light's spotAngle (" + Num(light.spotAngle) + ") was read but the volume's "
					+ "own Angle field was left at its default -- set it on the component if the spot has to stay that narrow.");
			}

			if (keep)
			{
				report.AppendLine("the Unity Light was KEPT and is still enabled: the scene is lit by it at edit time "
					+ "and at runtime until you disable it yourself.");
			}
			else
			{
				// Disabling the component, never destroying the GameObject: the
				// object may hold a mesh, a collider and a light volume, and a
				// destructive conversion is not something a one-call action gets
				// to undo on the author's behalf.
				try
				{
					Undo.RecordObject(light, "pi disable converted light");
					light.enabled = false;
					report.AppendLine("the Unity Light was DISABLED, not destroyed -- the GameObject, its mesh and its "
						+ "light volume are all still there.");
					report.AppendLine("  a disabled light does not render, but it is also invisible in the Scene view:");
					report.AppendLine("  tick the Light component's checkbox if you need to find it again.");
				}
				catch (Exception error)
				{
					report.AppendLine("the Unity Light could NOT be disabled (" + error.Message + ") and is still enabled, "
						+ "so this scene is now lit by both the light and the volume. Disable it by hand.");
				}
			}

			report.AppendLine("the volume will NOT BAKE until a LightVolumeSetup is in the scene. Run action "
				+ "\"light-volumes\" to see whether one is present; without it the scene looks unchanged in-game.");
			report.AppendLine("inspect with unity_inspect { gameObject: \"" + target.name
				+ "\", component: \"" + volumeComponentType.Name + "\" }");
			report.AppendLine(MarkActiveSceneDirty()
				? "scene marked dirty (save it to keep the change)"
				: "no valid active scene, so nothing was marked dirty");
			return report.ToString();
		}

		private static string ResolveVolumeType(Light light, string requested, StringBuilder report)
		{
			string asked = (requested ?? "").Trim();
			if (asked.Equals("AreaLight", StringComparison.OrdinalIgnoreCase))
			{
				throw new Exception(
					"AreaLight cannot be used: VRChat does not support area lights. red.sim.lightvolumes' LightType "
					+ "enum lists AreaLight, but a volume of that type is not rendered in a VRChat world. Convert this "
					+ "light to PointLight or SpotLight instead."
				);
			}
			if (asked.Equals("PointLight", StringComparison.OrdinalIgnoreCase)) return "PointLight";
			if (asked.Equals("SpotLight", StringComparison.OrdinalIgnoreCase)) return "SpotLight";
			if (asked.Length > 0)
			{
				throw new Exception(
					"\"" + asked + "\" is not a light volume type. Use PointLight or SpotLight. (AreaLight is refused: "
					+ "VRChat does not support area lights.)"
				);
			}

			if (light.type == LightType.Spot) return "SpotLight";
			if (light.type != LightType.Point)
			{
				// Directional and Unity's own Area light both have no volume
				// equivalent, so this degrades to a point volume at the light's
				// own range rather than refusing a conversion that is still worth
				// doing by hand.
				report.AppendLine("note: the light is " + light.type + ", which has no light volume equivalent; using "
					+ "PointLight with this light's range. Adjust the range afterwards if it lights too far.");
			}
			return "PointLight";
		}

		/// A supplied number, or the light's own value.
		///
		/// Zero and empty both mean "unset". A light whose range or intensity is 0
		/// is already invisible, and inheriting that into the volume produces a
		/// converted light that lights nothing -- which looks like the conversion
		/// broke rather than like the input being zero.
		private static bool TryOverride(string raw, float lightValue, out float value, out string source)
		{
			value = lightValue;
			source = "the light's own value";
			string text = (raw ?? "").Trim();
			if (text.Length == 0) return true;
			if (!float.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out float parsed)) return false;
			if (parsed == 0f)
			{
				source = "the light's own value (0 read as unset, not as a value)";
				return true;
			}
			value = parsed;
			source = "the override";
			return true;
		}

		/// The package's LightType enum resolved from the name the caller asked
		/// for, so the write is typed rather than a string that only a name-based
		/// lookup could turn back into an enum.
		private static object EnumVolumeType(Component volume, string name)
		{
			FieldInfo field = Bridge.FindInstanceField(volume.GetType(), "Type");
			if (field == null || !field.FieldType.IsEnum) return name;
			foreach (string member in Enum.GetNames(field.FieldType))
			{
				if (member.Equals(name, StringComparison.OrdinalIgnoreCase)) return Enum.Parse(field.FieldType, member);
			}
			return name;
		}

		private static void WriteVolumeField(StringBuilder report, Component volume, string fieldName, object value)
		{
			if (TryWriteField(volume, fieldName, value, out object written, out string reason))
			{
				report.AppendLine("  " + fieldName + " = " + DescribeValue(written));
			}
			else
			{
				report.AppendLine("  " + fieldName + " NOT written: " + reason);
			}
		}

		/// Writes a field from a value the caller already holds in its real type.
		/// The string overload is for user-typed text that still has to be coerced;
		/// round-tripping a Color or an enum through its DISPLAY form loses it
		/// (a Color displays as "(1, 0.867, 0.667)", which is not a colour this
		/// parser can read back and silently became white).
		private static bool TryWriteField(Component component, string fieldName, object value, out object written, out string reason)
		{
			written = null;
			reason = "";
			FieldInfo field = Bridge.FindInstanceField(component.GetType(), fieldName);
			if (field == null)
			{
				reason = "no field \"" + fieldName + "\" on " + component.GetType().FullName
					+ " (SDK versions differ here; read the real fields with unity_inspect)";
				return false;
			}
			if (value == null || !field.FieldType.IsInstanceOfType(value))
			{
				reason = "cannot write a " + (value == null ? "null" : value.GetType().Name)
					+ " into " + field.FieldType.Name + " " + fieldName;
				return false;
			}
			if (!PiWorldToolkit.WriteFieldByName(component, fieldName, value)) return false;
			written = field.GetValue(component);
			return true;
		}

		// ------------------------------------------------------ shared helpers

		/// The scene object named `name`, or a fresh empty root GameObject when
		/// there is none.
		internal static GameObject ResolveOrCreate(string name, out bool created)
		{
			string wanted = (name ?? "").Trim();
			if (wanted.Length > 0)
			{
				GameObject found = FindSceneObject(wanted, out _);
				if (found != null)
				{
					created = false;
					return found;
				}
			}

			created = true;
			GameObject go = new GameObject(UniqueName(wanted.Length == 0 ? "Pi_Object" : wanted));
			// Same standard the other write paths hold to: a mistaken call is one
			// Ctrl-Z away. A registration that Unity refuses is logged here rather
			// than swallowed, because a silently un-undoable object looks exactly
			// like a correctly registered one.
			try
			{
				Undo.RegisterCreatedObjectUndo(go, "pi create " + go.name);
			}
			catch (Exception error)
			{
				Debug.LogWarning("[pi] " + go.name + " was created but not registered for undo: " + error.Message);
			}
			return go;
		}

		/// A scene object by bare name or by "Root/Child" path, including INACTIVE
		/// ones, with the number of objects that matched.
		///
		/// FindObjectsOfType<Transform>(true) rather than GameObject.Find, which
		/// skips inactive objects: a disabled object's name is still a name its
		/// owner uses, and reusing it would produce two objects with one address.
		internal static GameObject FindSceneObject(string name, out int matches)
		{
			matches = 0;
			if (string.IsNullOrEmpty(name)) return null;

			// A slash can only mean a path: Unity's own hierarchy cannot produce an
			// object named "A/B".
			if (name.IndexOf('/') >= 0)
			{
				GameObject byPath = FindSceneObjectByPath(name);
				matches = byPath == null ? 0 : 1;
				return byPath;
			}

			GameObject found = null;
			foreach (Transform child in UnityEngine.Object.FindObjectsOfType<Transform>(true))
			{
				if (child.gameObject.name != name) continue;
				matches++;
				if (found == null) found = child.gameObject;
			}
			return found;
		}

		private static GameObject FindSceneObjectByPath(string path)
		{
			string[] segments = path.Split('/');
			if (segments.Length == 0 || segments[0].Length == 0) return null;

			Transform current = null;
			foreach (GameObject root in SceneManager.GetActiveScene().GetRootGameObjects())
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

		/// "Pi_Lamp" -> "Pi_Lamp.001" -> "Pi_Lamp.002" when the name is taken.
		///
		/// Two objects sharing a name is not cosmetic. Every name-addressed tool
		/// resolves a bare name to the FIRST match, so a duplicate silently
		/// shadows the object that was just configured: create a second lamp and a
		/// later read of "Pi_Lamp" reports the first one's values, which looks
		/// exactly like the write having been discarded.
		internal static string UniqueName(string desired)
		{
			if (!NameIsTaken(desired)) return desired;
			for (int suffix = 1; suffix < 1000; suffix++)
			{
				string candidate = desired + "." + suffix.ToString("000", CultureInfo.InvariantCulture);
				if (!NameIsTaken(candidate)) return candidate;
			}
			return desired + "." + Guid.NewGuid().ToString("N").Substring(0, 6);
		}

		private static bool NameIsTaken(string name)
		{
			// Scans the open scene's own objects rather than GameObject.Find,
			// which skips INACTIVE ones -- so a name is not reused just because
			// its owner happens to be disabled.
			foreach (Transform child in UnityEngine.Object.FindObjectsOfType<Transform>(true))
			{
				if (child.gameObject.name == name) return true;
			}
			return false;
		}

		/// A colour from "#rrggbb", "rrggbb", "rgb" or "r,g,b" (each 0..1).
		///
		/// Alpha is always 1: a light or a volume with alpha 0 is legal and
		/// contributes nothing, which reads as broken rather than as transparent.
		/// An unparseable value is white, and callers report what they got, so a
		/// typo shows up as "the colour came out white" instead of as a silently
		/// mis-set field.
		internal static Color ColorFromHex(string hex)
		{
			string value = (hex ?? "").Trim();
			if (value.IndexOf(',') >= 0)
			{
				string[] parts = value.Split(',');
				if (parts.Length >= 3
					&& TryColorPart(parts[0], out float red)
					&& TryColorPart(parts[1], out float green)
					&& TryColorPart(parts[2], out float blue))
				{
					return new Color(red, green, blue, 1f);
				}
				return Color.white;
			}

			value = value.TrimStart('#');
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

		private static bool TryColorPart(string text, out float parsed)
		{
			return float.TryParse(text.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out parsed);
		}

		private static string DescribeValue(object value)
		{
			if (value == null) return "null";
			if (value is Color color)
			{
				return string.Format(CultureInfo.InvariantCulture, "({0:0.###}, {1:0.###}, {2:0.###})", color.r, color.g, color.b);
			}
			return value.ToString();
		}

		private static string Num(float value)
		{
			return value.ToString("0.###", CultureInfo.InvariantCulture);
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
			return names.Length == 0 ? "no components at all" : names.ToString();
		}

		private static string InspectCall(GameObject target, Type type)
		{
			return "unity_inspect { gameObject: \"" + target.name + "\", component: \"" + type.Name + "\" }";
		}

		private static bool ParseSwitch(string raw, bool fallback)
		{
			string text = (raw ?? "").Trim();
			if (text.Length == 0) return fallback;
			if (text.Equals("true", StringComparison.OrdinalIgnoreCase) || text == "1"
				|| text.Equals("yes", StringComparison.OrdinalIgnoreCase)) return true;
			if (text.Equals("false", StringComparison.OrdinalIgnoreCase) || text == "0"
				|| text.Equals("no", StringComparison.OrdinalIgnoreCase)) return false;
			throw new Exception("Expected \"true\" or \"false\", not \"" + raw + "\".");
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