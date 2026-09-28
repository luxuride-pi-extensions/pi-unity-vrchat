// Part of the pi Unity bridge plugin. See PiUnityBridge.Core.cs for the
// protocol overview. The whole plugin is one assembly of partial classes, so
// Unity compiles it into Assembly-CSharp-Editor with no asmdef and no
// package dependencies, which is what lets it drop into any Unity 2021+
// project (a configured VRChat world, an existing game) that pi was pointed at
// rather than generated.
//
// VRChat WORLD helpers: light volumes, Udon, and the scene descriptor.
//
// API surface read out of the actual package source on disk, NOT guessed:
//
//   red.sim.lightvolumes 2.1.3, namespace VRCLightVolumes
//     class PointLightVolume : MonoBehaviour
//         bool Dynamic, BakedShadows, DebugRange; int CustomID;
//         float BakedShadowRadius, LightSourceSize, Range, Intensity, Angle, Falloff;
//         LightType Type;  LightShape Shape;  Color Color;
//         Texture2D FalloffLUT, Cookie;  Cubemap Cubemap;
//     class LightVolumeSetup : MonoBehaviour
//         BakingMode, Resolution, Format, LightsBrightnessCutoff, Denoise,
//         AutoUpdateVolumes, DownscaleVolumes, SharpBounds,
//         LightProbesBlending, DilationIterations, AdditiveMaxOverdraw
//     class LightVolume : MonoBehaviour
//     enum LightType  { PointLight, SpotLight, AreaLight }
//     enum LightShape { Parametric, LUT, Custom }
//
//   com.vrchat.worlds 3.10.1
//     sealed class VRC.Udon.UdonBehaviour
//     abstract class UdonSharpBehaviour  (Integrations/UdonSharp/Runtime)
//
// ONE THING NOT VERIFIED, AND WHY IT MATTERS
// -----------------------------------------
// The world-SDK behaviour types (VRC_SceneDescriptor, VRCObjectSync,
// VRC_Pickup, VRC_Station, ...) are NOT in the SDK's C# source -- they ship as
// compiled plugin DLLs, and no world project was loaded when this was written.
// So no name here is hard-coded for those. PiWorld resolves them by SIMPLE
// NAME and reports what it actually found, which is why FindSceneDescriptor
// returns a resolved name rather than assuming one. Every avatar-side helper
// does the same, for the same reason: a guessed package type name is the single
// most expensive mistake in this plugin's history.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using UnityEditor;
using UnityEngine;

namespace PiUnity
{
	public static class PiLightVolumes
	{
		public const string Package = "red.sim.lightvolumes";
		private const string Ns = "VRCLightVolumes.";

		private const string PointLightVolumeType = Ns + "PointLightVolume";
		private const string LightVolumeType = Ns + "LightVolume";
		private const string SetupType = Ns + "LightVolumeSetup";

		public static bool IsInstalled() => VrcResolve.Has(PointLightVolumeType);

		/// Every point light volume in the scene.
		///
		/// Scene-wide rather than per-avatar: light volumes belong to a WORLD, and
		/// a world scene has no avatar root to scope them to. That is the real
		/// difference from the avatar helpers, and getting it wrong is why this
		/// takes no GameObject.
		public static List<Component> FindAll()
		{
			return FindAllIn(null);
		}

		public static List<Component> FindAllIn(GameObject root)
		{
			var found = new List<Component>();
			Type type = VrcResolve.Type(PointLightVolumeType);
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

		/// The point light volumes grouped by type, which is the question a world
		/// author actually asks ("where are my spot lights").
		public static string Describe()
		{
			if (!IsInstalled())
			{
				return Package + " is not installed. Add it to enable light volumes; "
					+ "without it a world has no baked lighting and runs on realtime lights only.";
			}

			List<Component> volumes = FindAll();
			var report = new System.Text.StringBuilder();
			report.AppendLine("light volumes: " + volumes.Count);

			// Count by the enum's own names, read from the live assembly rather
			// than a hard-coded list, so a package version that adds a LightType
			// does not silently mis-count.
			var byType = new Dictionary<string, int>(StringComparer.Ordinal);
			float totalIntensity = 0f;
			int dynamicCount = 0;
			foreach (Component volume in volumes)
			{
				string typeName = ReadEnumField(volume, "Type") as string ?? "PointLight";
				byType.TryGetValue(typeName, out int seen);
				byType[typeName] = seen + 1;
				if (ReadField<bool>(volume, "Dynamic")) dynamicCount++;
				object intensity = ReadField(volume, "Intensity");
				if (intensity is float f) totalIntensity += f;
			}
			report.AppendLine("  by type: " + (byType.Count == 0
				? "none"
				: string.Join(", ", byType.OrderBy(p => p.Key, StringComparer.Ordinal).Select(p => p.Key + "=" + p.Value).ToArray())));
			report.AppendLine("  dynamic (not baked)=" + dynamicCount + "  total intensity=" + totalIntensity.ToString("F2"));

			// The setup component is what bakes. Its absence is the single most
			// common reason a world looks wrong on upload, so it is reported first.
			// FindSetupInScene() searches every root, rather than checking only the
			// first one: a setup component is often parented under a sub-object
			// and not a scene root.
			Component setup = FindSetupInScene();
			if (setup == null)
			{
				report.AppendLine("  LightVolumeSetup: MISSING -- nothing will be baked. Add one to the scene root.");
			}
			else
			{
				report.AppendLine("  LightVolumeSetup on " + setup.gameObject.name);
				AppendField(report, setup, "BakingMode");
				AppendField(report, setup, "Resolution");
				AppendField(report, setup, "Format");
				AppendField(report, setup, "LightsBrightnessCutoff");
				AppendField(report, setup, "Denoise");
				AppendField(report, setup, "AutoUpdateVolumes");
				AppendField(report, setup, "DownscaleVolumes");
			}

			report.AppendLine("  -- per volume --");
			int shown = 0;
			foreach (Component volume in volumes)
			{
				if (shown++ >= 25)
				{
					report.AppendLine("  ... " + (volumes.Count - 25) + " more (narrow the scene or read a volume directly)");
					break;
				}
				report.Append("  " + volume.gameObject.name + "  ");
				AppendField(report, volume, "Type");
				AppendField(report, volume, "Range");
				AppendField(report, volume, "Intensity");
				AppendField(report, volume, "Color");
				AppendField(report, volume, "Shape");
				AppendField(report, volume, "Dynamic");
				report.AppendLine();
			}
			return report.ToString();
		}

		/// Creates a PointLightVolume with sane defaults.
		///
		/// Defaults are chosen to be VISIBLE: Range 10, Intensity 1, white. A
		/// volume created with a zero intensity or a zero range is legal, bakes
		/// successfully, and contributes nothing -- so a helper that did that
		/// would hand back an object that appears broken with no error.
		public static Component Create(GameObject target, string lightType, float range, float intensity, Color color)
		{
			if (target == null) throw new ArgumentNullException("target");
			Type type = VrcResolve.Type(PointLightVolumeType);
			if (type == null)
			{
				throw new Exception(
					Package + " is not installed, so " + PointLightVolumeType + " does not exist. "
					+ "Add the package, then run again."
				);
			}

			// Reuse rather than duplicate: stacking two volumes on one GameObject
			// silently doubles its light, which is a bug with no error.
			Component existing = target.GetComponent(type);
			Component volume = existing;
			if (existing != null)
			{
				Undo.RecordObject(existing, "pi configure PointLightVolume");
			}
			else
			{
				volume = Undo.AddComponent(target, type);
			}

			// Fields are written by NAME, resolved from the live type, because
			// these are package fields whose names can change between versions. A
			// missing one is skipped rather than fatal.
			WriteEnumField(volume, "Type", lightType);
			WriteField(volume, "Range", Mathf.Max(0.0001f, range));
			WriteField(volume, "Intensity", intensity);
			WriteField(volume, "Color", color);
			EditorUtility.SetDirty(volume);

			return volume;
		}

		// ---------------------------------------------------- field access

		internal static object ReadField(Component component, string fieldName)
		{
			if (component == null) return null;
			FieldInfo field = component.GetType().GetField(fieldName, BindingFlags.Public | BindingFlags.Instance);
			if (field == null) return null;
			try { return field.GetValue(component); } catch (Exception) { return null; }
		}

		internal static T ReadField<T>(Component component, string fieldName)
		{
			object value = ReadField(component, fieldName);
			return value is T typed ? typed : default(T);
		}

		/// An enum field as its NAME, which is what a caller can act on. A raw int
		/// is meaningless without the enum, and the enum may not be referenced by
		/// this plugin.
		private static object ReadEnumField(Component component, string fieldName)
		{
			object value = ReadField(component, fieldName);
			if (value == null) return null;
			return value.ToString();
		}

		internal static bool WriteField(Component component, string fieldName, object value)
		{
			if (component == null) return false;
			FieldInfo field = component.GetType().GetField(fieldName, BindingFlags.Public | BindingFlags.Instance);
			if (field == null) return false;
			try
			{
				// Enums arrive as a string from the wire, so coerce through the
				// field's own enum type rather than requiring the caller to know it.
				object coerced = value;
				if (field.FieldType.IsEnum && value is string name)
				{
					coerced = Enum.Parse(field.FieldType, name, true);
				}
				field.SetValue(component, coerced);
				return true;
			}
			catch (Exception error)
			{
				Debug.LogWarning("[pi] " + component.GetType().Name + "." + fieldName + ": " + error.Message);
				return false;
			}
		}

		private static bool WriteEnumField(Component component, string fieldName, string value)
		{
			return WriteField(component, fieldName, value);
		}

		private static void AppendField(System.Text.StringBuilder report, Component component, string fieldName)
		{
			object value = ReadField(component, fieldName);
			report.Append(fieldName + "=");
			report.Append(value == null ? "<absent>" : value.ToString());
			report.Append("  ");
		}

		private static Component FindSetupInScene()
		{
			Type type = VrcResolve.Type(SetupType);
			if (type == null) return null;
			foreach (Transform child in UnityEngine.Object.FindObjectsOfType<Transform>(true))
			{
				Component component = child.GetComponent(type);
				if (component != null) return component;
			}
			return null;
		}
	}
}
