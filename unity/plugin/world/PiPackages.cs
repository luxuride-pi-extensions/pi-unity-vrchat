// Part of the pi Unity bridge plugin. See PiUnityBridge.Core.cs for the
// protocol overview. The whole plugin is one assembly of partial classes, so
// Unity compiles it into Assembly-CSharp-Editor with no asmdef and no
// package dependencies, which is what lets it drop into any Unity 2021+
// project (a configured VRChat world, an existing game) that pi was pointed at
// rather than generated.

// Read and edit helpers for the three community world packages that get asked
// about most: AudioLink, YamaPlayer and VRCHandMenu.
//
// Every type is reached through VrcResolve, for the same reason every other
// package helper in this plugin does it: a compile-time reference to
// AudioLink.AudioLink or to Yamadev.VRCHandMenu.MenuHandle would break the
// plugin in every project WITHOUT that package, which is the single property
// the no-assembly-definition design exists to preserve. So an absent package
// is a line in a report, never an exception, and an absent optional member is
// a line too. Exceptions are reserved for caller error: an object that is not
// in the scene, and a property that is not a field on it.
//
// TWO FACTS THAT SHAPE EVERY WRITE HERE
// -------------------------------------
// 1. The fields a user actually configures on these three packages are almost
//    all PRIVATE -- VRCHandMenu's MenuHandle exposes nothing public at all.
//    PiLightVolumes.WriteField is public-only and finds nothing here, so every
//    write goes through PiWorldToolkit.WriteFieldByName, which walks the base
//    chain looking at non-public instance fields too.
//
// 2. A field write alone changes NOTHING in game. UdonSharp compiles a
//    UdonSharpBehaviour down to a UdonBehaviour and the VM reads the PROGRAM
//    ASSET, so the proxy has to be copied back onto the UdonBehaviour after
//    every write or the value is discarded at build. That is what
//    CopyProxyToUdon is for, and it is why these three packages need a write
//    path at all where a plain MonoBehaviour package would not.
//
// Type and field names were read out of the live project, not guessed:
//   com.llealloo.audiolink 3.1.2            assembly AudioLink
//   net.kwxxw.yama-stream 1.5.18           assembly Yamadev.YamaStream.Runtime
//   net.kwxxw.vrc-hand-menu 1.3.7           assembly Yamadev.VRCHandMenu.Runtime

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Reflection;
using System.Text;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace PiUnity
{
	public static class PiPackages
	{
		public const string AudioLinkPackage = "com.llealloo.audiolink";
		public const string AudioLinkAssembly = "AudioLink";
		public const string YamaPackage = "net.kwxxw.yama-stream";
		public const string YamaAssembly = "Yamadev.YamaStream.Runtime";
		public const string HandMenuPackage = "net.kwxxw.vrc-hand-menu";
		public const string HandMenuAssembly = "Yamadev.VRCHandMenu.Runtime";

		private const string AudioLinkType = "AudioLink.AudioLink";
		private const string AudioLinkZoneType = "AudioLink.AudioLinkZone";

		/// UdonSharpEditor.UdonSharpEditorUtility, whose CopyProxyToUdon is the
		/// only thing that makes a field write on a UdonSharp proxy reach the
		/// running program.
		private const string UdonSharpUtilityType = "UdonSharpEditor.UdonSharpEditorUtility";

		/// Every AudioLink component type, all of them UdonSharpBehaviour
		/// subclasses. AudioLinkGlobalSlider/Toggle etc. all live in the
		/// AudioLink namespace, so this list is the whole surface a world
		/// author touches.
		public static readonly string[] AudioLinkTypes =
		{
			"AudioLink.AudioLink",
			"AudioLink.AudioLinkZone",
			"AudioLink.AudioReactiveLight",
			"AudioLink.AudioReactiveSurface",
			"AudioLink.AudioReactiveBlendshapes",
			"AudioLink.AudioReactiveObject",
			"AudioLink.AudioReactiveSurfaceArray",
			"AudioLink.AudioLinkMiniPlayer",
			"AudioLink.GlobalSlider",
			"AudioLink.GlobalToggle",
			"AudioLink.ThemeColorController",
		};

		/// YamaPlayer spreads its components over several sub-namespaces, and
		/// bare names are resolved against Yamadev.YamaStream so a caller who
		/// only knows "YamaPlayerController" still lands on the real type.
		/// VrcResolve's simple-name fallback covers a name this file gets wrong,
		/// which is why the report prints the full name each type actually
		/// resolved to.
		public static readonly string[] YamaTypes =
		{
			"Yamadev.YamaStream.Script.YamaPlayer",
			"Yamadev.YamaStream.Script.YamaPlayerController",
			"Yamadev.YamaStream.Script.PlayList",
			"Yamadev.YamaStream.Script.PlayListContainer",
			"Yamadev.YamaStream.Controller",
			"Yamadev.YamaStream.Listener",
			"Yamadev.YamaStream.UI.UIController",
			"Yamadev.YamaStream.UI.Modal",
			"Yamadev.YamaStream.UI.SliderHelper",
			"Yamadev.YamaStream.Modules.KaraokeTrigger",
			"Yamadev.YamaStream.Modules.VideoResolver",
			"Yamadev.YamaStream.YamaPlayerScreen",
			"Yamadev.YamaStream.Playlist",
			"Yamadev.YamaStream.Track",
			"Yamadev.YamaStream.AutoPlay",
			"Yamadev.YamaStream.IndexTrigger",
			"Yamadev.YamaStream.InputController",
			"Yamadev.YamaStream.Permission",
			"Yamadev.YamaStream.VideoPlayerHandle",
			"Yamadev.YamaStream.YamaPlayerAudioSource",
			"Yamadev.YamaStream.YouTubePlaylist",
			// Open generic, so the backtick-2 arity name is what a lookup needs.
			// Its components are per-instantiation (DataDictionary<YamaTrack, AudioSource>)
			// and cannot be enumerated as one type.
			"Yamadev.YamaStream.Libraries.GenericDataContainer.DataDictionary`2",
		};

		/// VRCHandMenu's handles. TeleportHandle is in the GLOBAL namespace in
		/// this package version -- it is not a mistake in the list above, and
		/// rewriting it to the package namespace would resolve nothing.
		public static readonly string[] HandMenuTypes =
		{
			"Yamadev.VRCHandMenu.MenuHandle",
			"Yamadev.VRCHandMenu.Page",
			"Yamadev.VRCHandMenu.SwitchHandle",
			"Yamadev.VRCHandMenu.PenHandle",
			"Yamadev.VRCHandMenu.PPController",
			"Yamadev.VRCHandMenu.Pagination",
			"Yamadev.VRCHandMenu.TimeHandle",
			"Yamadev.VRCHandMenu.TimeEventListener",
			"Yamadev.VRCHandMenu.AlarmHandle",
			"Yamadev.VRCHandMenu.JoinHandle",
			"TeleportHandle",
		};

		/// One component's worth of report, past the cap in a scene that holds
		/// hundreds of them.
		private const int MaxInstancesPerType = 25;

		// ------------------------------------------------------- describe

		public static string DescribeAudioLink()
		{
			List<Component> components;
			string body = DescribePackage(AudioLinkPackage, AudioLinkAssembly, AudioLinkTypes, out components);
			var report = new StringBuilder(body);
			if (!VrcResolve.Has(AudioLinkType)) return report.ToString();

			int roots = CountOf(components, AudioLinkType);
			int zones = CountOf(components, AudioLinkZoneType);
			if (roots == 0)
			{
				report.AppendLine("  WARNING: no " + AudioLinkType + " in this scene. Every AudioReactive* component");
				report.AppendLine("  reads from the one AudioLink in the scene, so with none present NOTHING is");
				report.AppendLine("  audio-reactive in game however the reactive components are configured. Add one");
				report.AppendLine("  (AudioLink > AudioLink) before tuning any of them.");
			}
			else
			{
				report.AppendLine("  audio source of the scene: " + roots + " AudioLink, " + zones + " AudioLinkZone"
					+ (roots > 1 ? " -- more than one AudioLink is ambiguous; the first one found wins at runtime." : ""));
			}

			var unlinked = new List<string>();
			foreach (Component component in components)
			{
				string name = component.GetType().Name;
				if (!name.StartsWith("AudioReactive", StringComparison.Ordinal)) continue;
				if (PiWorldToolkit.ReadFieldByName(component, "audioLink") == null)
				{
					unlinked.Add(component.gameObject.name + " (" + name + ")");
				}
			}
			if (unlinked.Count > 0)
			{
				report.AppendLine("  FINDING: " + unlinked.Count + " audio-reactive component(s) with a null audioLink reference: "
					+ string.Join(", ", unlinked.ToArray()));
				report.AppendLine("  They stay inert in game. Point each at the scene's AudioLink (action \"audio-link-set\","
					+ " object, property \"audioLink\").");
			}
			return report.ToString();
		}

		public static string DescribeYama()
		{
			List<Component> ignored;
			return DescribePackage(YamaPackage, YamaAssembly, YamaTypes, out ignored);
		}

		public static string DescribeHandMenu()
		{
			List<Component> ignored;
			return DescribePackage(HandMenuPackage, HandMenuAssembly, HandMenuTypes, out ignored);
		}

		/// The shared describe pass: which types of the package resolved, how
		/// many of each are in the scene, and the fields each instance actually
		/// has. Every component of every listed type goes in `all`.
		///
		/// The fields are read reflectively and named per component rather than
		/// from a hard-coded table, because two of these components in the same
		/// package carry the same field name with a different meaning and a
		/// fixed table would report a value off the wrong component.
		private static string DescribePackage(string package, string assembly, string[] typeNames, out List<Component> all)
		{
			all = new List<Component>();
			var report = new StringBuilder();
			// The first type is the package's namesake, so it is the one whose
			// absence means the whole package is absent.
			if (!VrcResolve.Has(typeNames[0]))
			{
				report.AppendLine(package + " is NOT installed: assembly " + assembly + " is not loaded and "
					+ typeNames[0] + " does not resolve. Nothing in this scene depends on it.");
				return report.ToString();
			}
			report.AppendLine("--- " + package + " (assembly " + assembly + ") ---");
			int grandTotal = 0;
			int missing = 0;
			var counted = new HashSet<Component>();
			foreach (string typeName in typeNames)
			{
				Type type = VrcResolve.Type(typeName);
				if (type == null)
				{
					report.AppendLine("  " + typeName + ": absent in this package version");
					missing++;
					continue;
				}
				if (type.IsGenericTypeDefinition)
				{
					report.AppendLine("  " + type.Name + " -> " + type.FullName + ": resolved (open generic, "
						+ "so its components cannot be counted as one type)");
					continue;
				}
				// A component listed under two types (one deriving from the other)
				// is one component, so the scene-wide tally and the per-type
				// listing both work from the same deduplicated set.
				List<Component> unique = new List<Component>();
				foreach (Component component in FindComponents(type))
				{
					if (!counted.Add(component)) continue;
					unique.Add(component);
					all.Add(component);
				}
				grandTotal += unique.Count;
				string line = "  " + type.Name + " -> " + type.FullName + "  x" + unique.Count;
				if (type.FullName != typeName) line += "  (resolved from \"" + typeName + "\")";
				report.AppendLine(line);
				int shown = 0;
				foreach (Component component in unique)
				{
					if (shown++ >= MaxInstancesPerType)
					{
						report.AppendLine("      ... " + (unique.Count - MaxInstancesPerType) + " more (read one directly)");
						break;
					}
					AppendComponent(report, component);
				}
			}
			report.AppendLine("components of this package in the scene: " + grandTotal
				+ (missing > 0 ? "  (" + missing + " listed type(s) absent in this version)" : ""));
			report.AppendLine("To read or change a field by name, use this package's -set action with the object name and");
			report.AppendLine("the field name: a wrong field name answers with the fields that DO exist on that object.");
			return report.ToString();
		}

		private static void AppendComponent(StringBuilder report, Component component)
		{
			report.AppendLine("      " + component.gameObject.name
				+ "  udonSharpProxy=" + (IsUdonSharpProxy(component) ? "yes" : "no"));
			string program = ProgramAssetPath(component);
			if (program != null) report.AppendLine("        program asset " + program);
			report.AppendLine("        " + DescribeFields(component));
		}

		/// One component's fields, read through the shared reader, one line for
		/// the component.
		///
		/// UdonSharp's own bookkeeping is dropped: serializationData and the two
		/// backing-behaviour fields are large opaque blobs, and their presence
		/// in a report hides every field a caller can actually set. Compiler
		/// generated names are dropped for the same reason.
		private static string DescribeFields(Component component)
		{
			var line = new StringBuilder();
			Type stop = UdonSharpBehaviourType();
			Type type = component.GetType();
			while (type != null && type != stop && type != typeof(MonoBehaviour) && type != typeof(UnityEngine.Object))
			{
				foreach (FieldInfo field in type.GetFields(BindingFlags.Public | BindingFlags.NonPublic
					| BindingFlags.Instance | BindingFlags.DeclaredOnly))
				{
					if (IsBookkeeping(field.Name)) continue;
					if (line.Length > 0) line.Append("  ");
					line.Append(field.Name);
					line.Append(": ");
					line.Append(FriendlyName(field.FieldType));
					line.Append("=");
					line.Append(Format(PiWorldToolkit.ReadFieldByName(component, field.Name)));
				}
				type = type.BaseType;
			}
			return line.Length == 0 ? "<no readable instance fields>" : line.ToString();
		}

		private static bool IsBookkeeping(string fieldName)
		{
			return fieldName == "serializationData"
				|| fieldName == "_udonSharpBackingUdonBehaviour"
				|| fieldName == "_backingUdonBehaviourDump"
				|| fieldName == "m_Script"
				|| fieldName.Contains("<")
				|| fieldName.Contains("k__");
		}

		// ---------------------------------------------------------- set

		public static string SetAudioLink(string objectName, string property, string value)
		{
			return SetPackageField(AudioLinkPackage, AudioLinkAssembly, AudioLinkTypes, objectName, property, value);
		}

		public static string SetYama(string objectName, string property, string value)
		{
			return SetPackageField(YamaPackage, YamaAssembly, YamaTypes, objectName, property, value);
		}

		public static string SetHandMenu(string objectName, string property, string value)
		{
			return SetPackageField(HandMenuPackage, HandMenuAssembly, HandMenuTypes, objectName, property, value);
		}

		/// One write path for all three packages, because they differ only in
		/// which types they own: the private fields and the proxy-copy step are
		/// common to all three and must not drift apart.
		private static string SetPackageField(string package, string assembly, string[] typeNames,
			string objectName, string property, string value)
		{
			if (string.IsNullOrEmpty(objectName)) throw new Exception("A set needs an object: the GameObject name in the open scene.");
			if (string.IsNullOrEmpty(property)) throw new Exception("A set needs a property: the field name on the component.");
			if (!VrcResolve.Has(typeNames[0]))
			{
				throw new Exception(package + " is not installed (assembly " + assembly + " is not loaded), so no object in "
					+ "this scene carries one of its components.");
			}

			GameObject target = FindSceneObject(objectName);
			Component component = PickComponent(target, typeNames, property, package);
			FieldInfo field = Bridge.FindInstanceField(component.GetType(), property);
			if (field == null)
			{
				// The most useful message this tool can produce: on these three
				// packages the field names are unguessable and non-public, so
				// listing what the component really has is the whole answer.
				throw new Exception(target.name + " has no field \"" + property + "\" on its "
					+ component.GetType().Name + ". Fields that do exist: " + DescribeFieldNames(component)
					+ ". Values are set by the field's own type -- a number as a number, a reference as a scene object name.");
			}

			object coerced = Coerce(value, field.FieldType, target.name + "." + field.Name);
			object before = PiWorldToolkit.ReadFieldByName(component, field.Name);

			var report = new StringBuilder();
			bool written = PiWorldToolkit.WriteFieldByName(component, field.Name, coerced);
			if (!written)
			{
				report.AppendLine("set " + target.name + "." + field.Name + " = " + Format(before) + " -> " + Format(coerced)
					+ "  REJECTED by Unity (see the Editor log); the value was left as it was.");
				return report.ToString();
			}

			report.AppendLine("set " + target.name + "." + field.Name + " = " + Format(before) + " -> " + Format(coerced)
				+ "   (" + FriendlyName(field.FieldType) + " from \"" + value + "\")");

			if (IsUdonSharpProxy(component))
			{
				string program = ProgramAssetPath(component);
				string copyFailure = CopyProxyToUdon(component);
				if (copyFailure == null)
				{
					report.AppendLine("copied the proxy to its UdonBehaviour, so the running program sees the new value"
						+ (program != null ? " (" + program + ")" : "") + ".");
				}
				else
				{
					report.AppendLine("WARNING: " + copyFailure);
					report.AppendLine("  The field on the UdonSharpBehaviour is set, but the Udon program still holds the");
					report.AppendLine("  old value, so the change is discarded at build. See the Editor log for details.");
				}
			}
			else
			{
				report.AppendLine(component.GetType().Name + " is not a UdonSharp proxy, so no Udon copy was needed; "
					+ "the field value is what runs.");
			}

			EditorUtility.SetDirty(target);
			Scene scene = SceneManager.GetActiveScene();
			if (scene.IsValid()) EditorSceneManager.MarkSceneDirty(scene);
			report.AppendLine("inspect next: object \"" + target.name + "\", component \"" + component.GetType().Name
				+ "\" (scene \"" + scene.name + "\" is unsaved -- save it, or Undo).");
			return report.ToString();
		}

		/// The component a property refers to on `target`: the package component
		/// that actually has that field, else the only package component there
		/// (so a wrong property still answers with its field list), else an
		/// error naming what the object does carry.
		private static Component PickComponent(GameObject target, string[] typeNames, string property, string package)
		{
			var candidates = new List<Component>();
			foreach (Component component in target.GetComponents<Component>())
			{
				if (component == null) continue;
				if (!BelongsTo(component.GetType(), typeNames)) continue;
				candidates.Add(component);
			}
			if (candidates.Count == 0)
			{
				throw new Exception(target.name + " carries no " + package + " component. It has: "
					+ string.Join(", ", ComponentNames(target).ToArray())
					+ ". Put the package's component on the object first.");
			}
			foreach (Component component in candidates)
			{
				if (Bridge.FindInstanceField(component.GetType(), property) != null) return component;
			}
			if (candidates.Count == 1) return candidates[0];
			var names = new List<string>();
			foreach (Component component in candidates)
			{
				names.Add(component.GetType().Name + " (" + DescribeFieldNames(component) + ")");
			}
			throw new Exception(target.name + " carries " + candidates.Count + " " + package + " components and none has a "
				+ "field \"" + property + "\": " + string.Join("; ", names.ToArray()) + ". Name the property of one of them.");
		}

		private static bool BelongsTo(Type componentType, string[] typeNames)
		{
			foreach (string typeName in typeNames)
			{
				Type type = VrcResolve.Type(typeName);
				if (type == null || type.IsGenericTypeDefinition) continue;
				if (type.IsAssignableFrom(componentType)) return true;
			}
			return false;
		}

		// ------------------------------------------------------- udon sharp

		private static Type UdonSharpBehaviourType()
		{
			// The full name, not the "UdonSharpBehaviour" simple name the other
			// world helper uses: this one is compared with IsInstanceOfType
			// against a live component, so a simple-name fallback that landed
			// on an unrelated type would silently report every component as a
			// proxy or as not one.
			return VrcResolve.Type("UdonSharp.UdonSharpBehaviour") ?? VrcResolve.Type(PiUdon.UdonSharpBehaviourType);
		}

		private static bool IsUdonSharpProxy(Component component)
		{
			if (component == null) return false;
			Type behaviour = UdonSharpBehaviourType();
			if (behaviour != null && behaviour.IsInstanceOfType(component)) return true;
			MethodInfo method = BindUdonSharpUtility("IsProxyBehaviour");
			if (method == null) return false;
			try { return (bool)method.Invoke(null, new object[] { component }); }
			catch (Exception) { return false; }
		}

		/// The UdonSharp program asset a proxy compiles to, as an Assets path,
		/// or null when it has none.
		private static string ProgramAssetPath(Component component)
		{
			MethodInfo method = BindUdonSharpUtility("GetUdonSharpProgramAsset");
			if (method == null) return null;
			try
			{
				object asset = method.Invoke(null, new object[] { component });
				if (!(asset is UnityEngine.Object program)) return null;
				string path = AssetDatabase.GetAssetPath(program);
				return path.Length == 0 ? program.name : path;
			}
			catch (Exception)
			{
				return null;
			}
		}

		/// Pushes the proxy's field values onto the UdonBehaviour it compiles to.
		/// Without this the write above is discarded at build time.
		///
		/// Returns null on success, or the reason it could not be done. The
		/// reason is worth distinguishing because the two failures need different
		/// fixes: a missing backing behaviour means the component was never
		/// really set up (add the component through UdonSharp, or re-add it),
		/// while a throw from the copy itself is UdonSharp's own failure.
		internal static string CopyProxyToUdon(Component component)
		{
			MethodInfo backing = BindUdonSharpUtility("GetBackingUdonBehaviour");
			if (backing != null)
			{
				try
				{
					if (backing.Invoke(null, new object[] { component }) == null)
					{
						return component.GetType().Name + " has no UdonBehaviour behind it. UdonSharp pairs every script "
							+ "with the UdonBehaviour it compiles to, and this one was never paired, so there is nothing "
							+ "to copy into. Add the component through UdonSharp (create-behaviour does that), or remove "
							+ "and re-add it in the Inspector.";
					}
				}
				catch (Exception)
				{
					// Could not even ask; fall through and try the copy.
				}
			}
			MethodInfo method = BindUdonSharpUtility("CopyProxyToUdon", 1);
			if (method == null)
			{
				return "UdonSharpEditorUtility.CopyProxyToUdon is not available in this UdonSharp version, so the proxy "
					+ "cannot be written through to the Udon program. Saving the scene in the Editor normally does the "
					+ "same job; do that before building.";
			}
			try { method.Invoke(null, new object[] { component }); return null; }
			catch (Exception error)
			{
				Exception cause = error.InnerException ?? error;
				Debug.LogWarning("[pi] UdonSharpEditorUtility.CopyProxyToUdon failed: " + cause.Message);
				return "UdonSharpEditorUtility.CopyProxyToUdon threw " + cause.GetType().Name + ": " + cause.Message
					+ " (see the Editor log).";
			}
		}

		/// A UdonSharpEditorUtility static method, bound by NAME and parameter
		/// COUNT rather than by an exact signature.
		///
		/// Its parameter type is UdonSharp.UdonSharpBehaviour, so binding on
		/// typeof("UdonSharpBehaviour") would never match even with the package
		/// installed; and CopyProxyToUdon is overloaded with a
		/// ProxySerializationPolicy parameter, so the one-argument overload is
		/// picked by arity.
		private static MethodInfo BindUdonSharpUtility(string methodName, int parameterCount = 1)
		{
			Type utility = VrcResolve.Type(UdonSharpUtilityType);
			Type behaviour = UdonSharpBehaviourType();
			if (utility == null) return null;
			try
			{
				foreach (MethodInfo candidate in utility.GetMethods(BindingFlags.Public | BindingFlags.Static))
				{
					if (candidate.Name != methodName) continue;
					ParameterInfo[] parameters = candidate.GetParameters();
					if (parameters.Length != parameterCount) continue;
					if (behaviour != null && parameters[0].ParameterType != behaviour
						&& parameters[0].ParameterType.Name != behaviour.Name) continue;
					return candidate;
				}
			}
			catch (Exception)
			{
				return null;
			}
			return null;
		}

		// -------------------------------------------------------- coercion

		/// Turns the caller's string into a value of the field's own type.
		/// Numbers are invariant-culture so "0.5" and "1e-3" mean what they
		/// look like regardless of the machine's locale.
		///
		/// One implementation for all three packages, because the wire carries a
		/// string for every field on every package and the types overlap heavily
		/// (float, bool, Color, an object reference).
		private static object Coerce(string value, Type target, string label)
		{
			Type underlying = Nullable.GetUnderlyingType(target) ?? target;

			if (underlying == typeof(string)) return value;
			if (underlying == typeof(bool))
			{
				string flag = (value ?? "").Trim().ToLowerInvariant();
				if (flag == "true" || flag == "1" || flag == "yes" || flag == "on") return true;
				if (flag == "false" || flag == "0" || flag == "no" || flag == "off") return false;
				throw new Exception(label + " is a Boolean: give \"true\" or \"false\".");
			}
			if (underlying.IsEnum) return CoerceEnum(value, underlying, label);
			if (underlying == typeof(int) || underlying == typeof(short) || underlying == typeof(byte)
				|| underlying == typeof(long) || underlying == typeof(uint) || underlying == typeof(sbyte)
				|| underlying == typeof(ushort) || underlying == typeof(ulong))
			{
				if (long.TryParse((value ?? "").Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out long whole))
					return Convert.ChangeType(whole, underlying, CultureInfo.InvariantCulture);
				throw new Exception(label + " is a " + FriendlyName(underlying) + ": give a whole number, for example \"3\".");
			}
			if (underlying == typeof(float) || underlying == typeof(double))
			{
				if (double.TryParse((value ?? "").Trim(), NumberStyles.Float | NumberStyles.AllowThousands,
					CultureInfo.InvariantCulture, out double real))
				{
					return underlying == typeof(float) ? (object)(float)real : real;
				}
				throw new Exception(label + " is a " + FriendlyName(underlying) + ": give a number, for example \"0.5\" or \"1e-3\".");
			}
			if (underlying == typeof(Color)) return CoerceColor(value, label);
			if (underlying == typeof(Vector2)) return CoerceVector2(value, label);
			if (underlying == typeof(Vector3)) return CoerceVector3(value, label);
			if (underlying == typeof(Quaternion)) return CoerceQuaternion(value, label);
			if (underlying == typeof(int[]))
			{
				return CoerceArray(value, text => int.Parse(text, CultureInfo.InvariantCulture), label);
			}
			if (underlying == typeof(float[])) return CoerceArray(value, ParseSingle, label);
			if (underlying == typeof(Color[])) return CoerceColorArray(value, label);
			if (typeof(UnityEngine.Object).IsAssignableFrom(underlying)) return CoerceObject(value, underlying, label);

			throw new Exception(label + " is a " + FriendlyName(underlying) + ", which pi cannot build from a string"
				+ CoercionExample(underlying) + ". Set it in the Inspector instead.");
		}

		private static object CoerceEnum(string value, Type enumType, string label)
		{
			string text = (value ?? "").Trim();
			try
			{
				return Enum.Parse(enumType, text, true);
			}
			catch (Exception)
			{
				if (int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out int index))
				{
					try { return Enum.ToObject(enumType, index); }
					catch (Exception) { /* out of range: fall through to the error */ }
				}
				var members = new List<string>();
				foreach (string member in Enum.GetNames(enumType)) members.Add(member);
				throw new Exception(label + " is a " + FriendlyName(enumType) + ". Give one of: "
					+ string.Join(", ", members.ToArray()) + " -- or its index as a number.");
			}
		}

		private static object CoerceColor(string value, string label)
		{
			string text = (value ?? "").Trim();
			if (ColorUtility.TryParseHtmlString(text, out Color parsed)) return parsed;
			float[] parts = SplitNumbers(text, 3, label, "a Color as \"#rrggbb\" (or \"rrggbb\"), or as \"r,g,b\"");
			return new Color(parts[0], parts[1], parts[2]);
		}

		/// A Color array has to use the hex form for its elements: the comma
		/// form of a Color would be indistinguishable from the separator, so a
		/// semicolon-separated list of hex colours is accepted instead.
		private static object CoerceColorArray(string value, string label)
		{
			string text = (value ?? "").Trim();
			char separator = text.Contains(";") ? ';' : ',';
			string[] elements = text.Split(separator);
			var colors = new List<Color>();
			foreach (string element in elements)
			{
				string trimmed = element.Trim();
				if (trimmed.Length == 0) continue;
				if (ColorUtility.TryParseHtmlString(trimmed, out Color parsed)) colors.Add(parsed);
				else throw new Exception(label + " is a Color array: give hex colours separated by commas, "
					+ "for example \"#ff0000, #00ff00\" (only the hex form works inside a list).");
			}
			return colors.ToArray();
		}

		private static object CoerceVector2(string value, string label)
		{
			float[] parts = SplitNumbers(value, 2, label, "a Vector2 as \"x,y\", for example \"1,0.5\"");
			return new Vector2(parts[0], parts[1]);
		}

		private static object CoerceVector3(string value, string label)
		{
			float[] parts = SplitNumbers(value, 3, label, "a Vector3 as \"x,y,z\", for example \"0,1,0\"");
			return new Vector3(parts[0], parts[1], parts[2]);
		}

		private static object CoerceQuaternion(string value, string label)
		{
			float[] parts = SplitNumbers(value, 4, label, "a Quaternion as \"x,y,z,w\" (w is the scalar, Unity order)");
			return new Quaternion(parts[0], parts[1], parts[2], parts[3]);
		}

		private static T[] CoerceArray<T>(string value, Func<string, T> parse, string label)
		{
			string text = (value ?? "").Trim();
			if (text.Length == 0) return new T[0];
			string[] elements = text.Split(',');
			var values = new List<T>();
			foreach (string element in elements)
			{
				string trimmed = element.Trim();
				if (trimmed.Length == 0) continue;
				try
				{
					values.Add(parse(trimmed));
				}
				catch (Exception)
				{
					throw new Exception(label + " is a " + FriendlyName(typeof(T[])) + ": \"" + trimmed
						+ "\" is not one of its elements. Give comma-separated values.");
				}
			}
			return values.ToArray();
		}

		private static float ParseSingle(string text)
		{
			if (!float.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out float parsed))
			{
				throw new FormatException(text);
			}
			return parsed;
		}

		private static float[] SplitNumbers(string value, int count, string label, string example)
		{
			string[] elements = (value ?? "").Split(',');
			if (elements.Length != count)
			{
				throw new Exception(label + " needs " + example + ".");
			}
			var numbers = new float[count];
			for (int index = 0; index < count; index++)
			{
				string trimmed = elements[index].Trim();
				if (!float.TryParse(trimmed, NumberStyles.Float, CultureInfo.InvariantCulture, out float parsed))
				{
					throw new Exception(label + " needs " + example + "; \"" + trimmed + "\" is not a number.");
				}
				numbers[index] = parsed;
			}
			return numbers;
		}

		/// A scene object by name, or an ASSET when the value looks like a path.
		///
		/// A field of type UnityEngine.Object on these packages is usually a
		/// scene reference (an audio material, an AudioLink) but is sometimes an
		/// asset (a Material, a shader), and an asset has no scene name to match
		/// on -- so a value containing a path separator is treated as a path.
		private static object CoerceObject(string value, Type target, string label)
		{
			string text = (value ?? "").Trim();
			if (text.Length == 0 || text == "<null>" || text.Equals("none", StringComparison.OrdinalIgnoreCase)
				|| text.Equals("null", StringComparison.OrdinalIgnoreCase))
			{
				return null;
			}
			if (text.Contains("/"))
			{
				// Loaded as the base type first so the ONE overload that exists is
				// used; the typed call is for the rare case where the base load
				// returns something of the wrong type for the field.
				UnityEngine.Object asset = AssetDatabase.LoadAssetAtPath<UnityEngine.Object>(text);
				if (asset == null || !target.IsInstanceOfType(asset)) asset = AssetDatabase.LoadAssetAtPath(text, target);
				if (asset == null)
				{
					throw new Exception(label + " is a reference to a " + FriendlyName(target) + ", and there is no asset at \""
						+ text + "\" of that type.");
				}
				return asset;
			}
			var named = new List<GameObject>();
			foreach (Transform child in UnityEngine.Object.FindObjectsOfType<Transform>(true))
			{
				if (child.gameObject.name == text) named.Add(child.gameObject);
			}
			if (named.Count == 0)
			{
				throw new Exception(label + " is a reference to a " + FriendlyName(target) + ", and no scene object (inactive "
					+ "included) is named \"" + text + "\". Give a scene object name, or an asset path such as \"Assets/M.mat\".");
			}
			if (named.Count > 1)
			{
				throw new Exception("\"" + text + "\" names " + named.Count + " objects in the scene. Name the one meant "
					+ "by path, or rename them: a reference cannot be resolved unambiguously.");
			}
			if (target.IsInstanceOfType(named[0])) return named[0];
			// GetComponent(Type) throws on an abstract or interface-shaped target
			// it cannot satisfy, which is an answer, not a failure.
			Component component = null;
			try { component = named[0].GetComponent(target); }
			catch (Exception) { }
			if (component != null) return component;
			throw new Exception("\"" + text + "\" is a " + FriendlyName(named[0].GetType()) + ", and " + label + " needs a "
				+ FriendlyName(target) + ". It has: " + string.Join(", ", ComponentNames(named[0]).ToArray()) + ".");
		}

		// ------------------------------------------------------- scene work

		private static List<Component> FindComponents(Type type)
		{
			var found = new List<Component>();
			foreach (Transform child in UnityEngine.Object.FindObjectsOfType<Transform>(true))
			{
				Component component;
				try { component = child.GetComponent(type); }
				catch (Exception) { continue; }
				if (component != null) found.Add(component);
			}
			return found;
		}

		private static int CountOf(List<Component> components, string typeName)
		{
			Type type = VrcResolve.Type(typeName);
			if (type == null) return 0;
			int count = 0;
			foreach (Component component in components)
			{
				if (type.IsInstanceOfType(component)) count++;
			}
			return count;
		}

		private static GameObject FindSceneObject(string objectName)
		{
			var named = new List<GameObject>();
			foreach (Transform child in UnityEngine.Object.FindObjectsOfType<Transform>(true))
			{
				if (child.gameObject.name == objectName) named.Add(child.gameObject);
			}
			if (named.Count == 0)
			{
				throw new Exception("No object named \"" + objectName + "\" in the scene (inactive objects count). "
					+ "Top-level objects in " + SceneManager.GetActiveScene().name + ": " + DescribeRoots() + ".");
			}
			if (named.Count > 1)
			{
				throw new Exception("\"" + objectName + "\" names " + named.Count + " objects in the scene: "
					+ string.Join(", ", named.Select(HierarchyPath).ToArray()) + ". A name that matches two cannot be edited "
					+ "unambiguously.");
			}
			return named[0];
		}

		private static string DescribeRoots()
		{
			Scene scene = SceneManager.GetActiveScene();
			if (!scene.IsValid()) return "<no open scene>";
			var names = new List<string>();
			foreach (GameObject root in scene.GetRootGameObjects()) names.Add(root.name);
			if (names.Count == 0) return "<none>";
			return string.Join(", ", names.ToArray());
		}

		private static string HierarchyPath(GameObject gameObject)
		{
			var parts = new List<string>();
			Transform cursor = gameObject.transform;
			while (cursor != null)
			{
				parts.Insert(0, cursor.name);
				cursor = cursor.parent;
			}
			return string.Join("/", parts.ToArray());
		}

		private static List<string> ComponentNames(GameObject gameObject)
		{
			var names = new List<string>();
			foreach (Component component in gameObject.GetComponents<Component>())
			{
				if (component != null) names.Add(component.GetType().Name);
			}
			if (names.Count == 0) names.Add("<none>");
			return names;
		}


		/// The field names and types on a component, for the "no such property"
		/// error. Names only, because a value the caller cannot read is of no
		/// use to them.
		private static string DescribeFieldNames(Component component)
		{
			var names = new List<string>();
			Type stop = UdonSharpBehaviourType();
			Type type = component.GetType();
			while (type != null && type != stop && type != typeof(MonoBehaviour) && type != typeof(UnityEngine.Object))
			{
				foreach (FieldInfo field in type.GetFields(BindingFlags.Public | BindingFlags.NonPublic
					| BindingFlags.Instance | BindingFlags.DeclaredOnly))
				{
					if (IsBookkeeping(field.Name)) continue;
					names.Add(field.Name + " (" + FriendlyName(field.FieldType) + ")");
				}
				type = type.BaseType;
			}
			if (names.Count == 0) return "<none readable>";
			return string.Join(", ", names.ToArray());
		}

		// ------------------------------------------------------ formatting

		private static string CoercionExample(Type type)
		{
			if (type.IsEnum) return " -- give a member name";
			if (type == typeof(string)) return "";
			if (type == typeof(bool)) return " -- give \"true\" or \"false\"";
			if (type == typeof(int) || type == typeof(float) || type == typeof(double)) return " -- give a number";
			if (type == typeof(Color)) return " -- give \"#rrggbb\"";
			if (type == typeof(Vector2) || type == typeof(Vector3) || type == typeof(Quaternion)) return " -- give comma-separated components";
			if (type == typeof(int[]) || type == typeof(float[]) || type == typeof(Color[])) return " -- give comma-separated elements";
			if (typeof(UnityEngine.Object).IsAssignableFrom(type)) return " -- give a scene object name or an asset path";
			return "";
		}

		private static string FriendlyName(Type type)
		{
			if (type == typeof(string)) return "String";
			if (type == typeof(bool)) return "Boolean";
			if (type == typeof(int)) return "Int32";
			if (type == typeof(float)) return "Single";
			if (type == typeof(double)) return "Double";
			if (type == typeof(Color)) return "Color";
			if (type == typeof(Vector2)) return "Vector2";
			if (type == typeof(Vector3)) return "Vector3";
			if (type == typeof(Quaternion)) return "Quaternion";
			if (type == typeof(GameObject)) return "GameObject";
			if (type.IsEnum) return type.Name;
			if (type.IsArray) return FriendlyName(type.GetElementType()) + "[]";
			return type.Name;
		}

		/// A value in a form the caller can type back into a set, which is the
		/// point of printing it.
		private static string Format(object value)
		{
			if (value == null) return "<null>";
			if (value is Color color) return "#" + ColorUtility.ToHtmlStringRGB(color);
			if (value is Color[] colors)
			{
				var parts = new List<string>();
				foreach (Color entry in colors) parts.Add("#" + ColorUtility.ToHtmlStringRGB(entry));
				return string.Join(", ", parts.ToArray());
			}
			if (value is float[] numbers)
			{
				var parts = new List<string>();
				foreach (float entry in numbers) parts.Add(entry.ToString("R", CultureInfo.InvariantCulture));
				return string.Join(", ", parts.ToArray());
			}
			if (value is UnityEngine.Object target)
			{
				string path = AssetDatabase.GetAssetPath(target);
				return path.Length == 0 ? target.name : target.name + " [" + path + "]";
			}
			return Convert.ToString(value, CultureInfo.InvariantCulture);
		}
	}
}