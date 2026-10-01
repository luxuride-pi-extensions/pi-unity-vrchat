// Part of the pi Unity bridge plugin. See PiUnityBridge.Core.cs for the
// protocol overview. The whole plugin is one assembly of partial classes, so
// Unity compiles it into Assembly-CSharp-Editor with no asmdef and no
// package dependencies, which is what lets it drop into any Unity 2021+
// project (a configured VRChat world, an existing game) that pi was pointed at
// rather than generated.
//
// Udon and world-scene helpers.
//
// WHY THESE NAMES ARE FULL, NOT SIMPLE
// -----------------------------------
// The world SDK's behaviour types ship as COMPILED PLUGIN DLLs, so they were
// once listed by SIMPLE NAME on the grounds that the full names were unknown.
// That was wrong in two ways, both observed live against a real world project:
//   - the SDK3 behaviours are NOT spelled VRC_Station / VRC_SimpleTeleport:
//     the simple names below were half guesses, and half of them (VRC_Station,
//     VRC_SimpleTeleport, VRC_PickupDriver, VRC_AvatarScaler, VRC_UdonCube,
//     VRCMusicSettings, VRC_SceneSettings, VRCWorldPreloadedLibrary) do not
//     exist in any assembly the project loads;
//   - VRC_SceneDescriptor is the LEGACY VRC.SDKBase type, while the component a
//     modern world scene actually carries is VRC.SDK3.Components.
//     VRCSceneDescriptor -- a different name for the same job. A simple-name
//     lookup found the legacy type first, so the scene reported a descriptor of
//     the wrong class.
//
// Every name in WorldBehaviourNames is now verified against the compiled
// VRCSDK3.dll / VRCSDKBase.dll of com.vrchat.worlds 3.10.1. Resolution still
// falls back to a simple-name match (VrcResolve.Type does it), so a caller who
// knows only "VRCObjectSync" still resolves, but nothing here depends on that.
//
// Two names are verified from SDK source rather than from a DLL:
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

		/// World behaviour types, by verified FULL name, SDK3 first. The
		/// legacy VRC.SDKBase types are kept because a world authored on SDK2
		/// still carries them, and a scene can legitimately hold both.
		public static readonly string[] WorldBehaviourNames =
		{
			"VRC.SDK3.Components.VRCSceneDescriptor",
			"VRC.SDK3.Components.VRCObjectSync",
			"VRC.SDK3.Components.VRCPickup",
			"VRC.SDK3.Components.VRCStation",
			"VRC.SDK3.Components.VRCPortalMarker",
			"VRC.SDK3.Components.VRCMirrorReflection",
			"VRC.SDK3.Components.VRCObjectPool",
			"VRC.SDK3.Components.VRCSpatialAudioSource",
			"VRC.SDK3.Components.VRCEnablePersistence",
			"VRC.SDK3.Components.VRCPlayerObject",
			"VRC.SDK3.Components.VRCVisualDamage",
			"VRC.SDK3.Components.VRCAvatarPedestal",
			"VRC.SDK3.Components.VRCInteractable",
			"VRC.SDKBase.VRC_SceneDescriptor",
			"VRC.SDKBase.VRC_Pickup",
			"VRC.SDKBase.VRCStation",
			"VRC.SDKBase.VRC_PortalMarker",
			"VRC.SDKBase.VRC_Trigger",
			"VRC.SDKBase.VRC_PhysicsRoot",
		};

		/// Descriptor candidates in preference order. A modern world scene uses
		/// the SDK3 type; the legacy one is the fallback, so the report names the
		/// class that is really on the object rather than the first name that
		/// happened to resolve.
		public static readonly string[] SceneDescriptorNames =
		{
			"VRC.SDK3.Components.VRCSceneDescriptor",
			"VRC.SDKBase.VRC_SceneDescriptor",
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
				// GetComponents, not GetComponent: a GameObject can carry SEVERAL
				// behaviours of one type -- a scene descriptor holds one UdonBehaviour
				// per spawn point, and the manager the light-volumes package creates
				// holds a set -- and GetComponent returns only the first, so the
				// count was silently short by however many were on the object.
				foreach (Component component in child.GetComponents(type))
				{
					if (component != null) found.Add(component);
				}
			}
			return found;
		}

		/// Resolves a world behaviour by full or simple name.
		///
		/// With no `root` this searches the WHOLE scene. Scoping silently to
		/// nothing was a real defect: a find-behaviour call without a
		/// gameObject returned found:false even for a behaviour that was sitting
		/// on the scene descriptor, and the note blamed the missing GameObject
		/// rather than admitting nothing had been searched.
		public static Component FindBehaviour(string name, GameObject root = null)
		{
			Type type = ResolveWorldType(name);
			if (type == null) return null;
			IEnumerable<Transform> search = root == null
				? UnityEngine.Object.FindObjectsOfType<Transform>(true)
				: root.GetComponentsInChildren<Transform>(true);
			foreach (Transform child in search)
			{
				// Every component of the type, not just the first: a caller naming
				// a behaviour wants to know it is there, and the object it is on
				// may hold more than one.
				foreach (Component component in child.GetComponents(type))
				{
					if (component != null) return component;
				}
			}
			return null;
		}

		private static Type ResolveWorldType(string name)
		{
			// VrcResolve.Type tries the full name first and then falls back to a
			// simple-name match across loaded assemblies, which is what keeps a
			// caller who only knows "VRCObjectSync" working.
			return VrcResolve.Type(name);
		}

		/// The scene descriptor for the open scene, or null.
		public static Component SceneDescriptor()
		{
			Scene scene = UnityEngine.SceneManagement.SceneManager.GetActiveScene();
			foreach (string name in SceneDescriptorNames)
			{
				Type type = VrcResolve.Type(name);
				if (type == null) continue;
				foreach (GameObject root in scene.GetRootGameObjects())
				{
					foreach (Transform child in root.GetComponentsInChildren<Transform>(true))
					{
						foreach (Component component in child.GetComponents(type))
						{
							if (component != null) return component;
						}
					}
				}
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
			Component descriptor = SceneDescriptor();
			report.AppendLine("--- scene descriptor ---");
			if (descriptor == null)
			{
				report.AppendLine("  no scene descriptor in the open scene (looked for "
					+ string.Join(", ", SceneDescriptorNames) + ").");
				report.AppendLine("  A world scene needs one; without it the scene is not a world and will not upload.");
			}
			else
			{
				report.AppendLine("  on " + descriptor.gameObject.name);
				report.AppendLine("  type " + descriptor.GetType().FullName + " ["
					+ descriptor.GetType().Assembly.GetName().Name + "]");
				report.AppendLine("  assetPath=" + (VrcResolve.AssetPathOf(descriptor) ?? "<scene instance>"));
				report.AppendLine("  Read its fields with unity_inspect { gameObject: \"" + descriptor.gameObject.name
					+ "\", component: \"" + descriptor.GetType().Name + "\" }.");
			}

			// -- Udon ------------------------------------------------------
			List<Component> udon = FindUdonBehaviours();
			report.AppendLine("--- Udon ---");
			report.AppendLine("  UdonBehaviours=" + udon.Count);
			// Counted as the scene's UdonSharpBehaviour COMPONENTS, not as the
			// UdonBehaviours that happen to derive from it: a UdonSharp script
			// is two components -- the proxy MonoBehaviour the author edits and
			// the UdonBehaviour its program compiles to -- and the proxy is not a
			// subclass of UdonBehaviour at all, so counting inside the list
			// above could only ever report 0.
			int sharpCount = 0;
			Type sharpType = VrcResolve.Type(UdonSharpBehaviourType);
			if (sharpType != null)
			{
				foreach (Transform child in UnityEngine.Object.FindObjectsOfType<Transform>(true))
				{
					foreach (Component proxy in child.GetComponents(sharpType)) sharpCount++;
				}
			}
			report.AppendLine("  UdonSharpBehaviours=" + sharpCount
				+ (udon.Count > 0 ? "  (UdonSharp scripts compile DOWN to a UdonBehaviour; there are usually more behaviours than programs)"
					: ""));
			if (sharpCount > udon.Count)
			{
				report.AppendLine("  MORE UdonSharp proxies than UdonBehaviours means some proxies have no backing "
					+ "UdonBehaviour; their field values are discarded at build. Re-add the component through UdonSharp.");
			}

			// -- world behaviours ------------------------------------------
			report.AppendLine("--- world behaviours ---");
			// Counted scene-wide rather than per root: a behaviour is routinely
			// parented under a spawn object or a pickup, so a per-root tally
			// reports the same object once per root it happens to sit under and
			// misses nothing only by accident. Names that resolve to no loaded
			// type are SKIPPED rather than printed: a name the project does not
			// have is not a fact about the scene, and listing it as "0" reads as
			// "this world should have one and does not".
			int namedTotal = 0;
			var perName = new System.Text.StringBuilder();
			// One component, one count. The SDK3 descriptor DERIVES from the
			// legacy VRC_SceneDescriptor, so GetComponent(legacy) returns the very
			// same instance and a per-name tally reported the scene descriptor
			// twice, once under each name.
			var counted = new HashSet<Component>();
			foreach (string name in WorldBehaviourNames)
			{
				Type type = ResolveWorldType(name);
				if (type == null) continue;
				int count = 0;
				foreach (Transform child in UnityEngine.Object.FindObjectsOfType<Transform>(true))
				{
					// GetComponents here too, for the same reason as the Udon
					// count: the descriptor alone carries one UdonBehaviour per
					// spawn, so a per-object GetComponent under-reports it.
					foreach (Component component in child.GetComponents(type))
					{
						if (component == null || !counted.Add(component)) continue;
						count++;
					}
				}
				if (count == 0) continue;
				namedTotal += count;
				perName.AppendLine("  " + type.Name + "=" + count);
			}
			if (namedTotal == 0) report.AppendLine("  none of the known world behaviours are in this scene");
			else report.Append(perName);

			// -- light volumes ---------------------------------------------
			report.AppendLine("--- light volumes ---");
			report.Append(PiLightVolumes.Describe());
			return report.ToString();
		}

		/// Adds a component to `target`, going through UdonSharp's own add path
		/// when `type` is a UdonSharpBehaviour.
		///
		/// Plain AddComponent is NOT enough for a UdonSharp script, and the
		/// failure is silent until something reads the values back: UdonSharp
		/// keeps a proxy MonoBehaviour that is meant to be paired with a
		/// UdonBehaviour it compiles to, and only its editor extension creates
		/// that pair. A proxy added on its own has no backing UdonBehaviour, so
		/// UdonSharpEditorUtility.CopyProxyToUdon throws
		/// ArgumentNullException("key") from its formatter and every field write
		/// to that script is discarded at build. The Inspector's "Add Component"
		/// and UdonSharpComponentExtensions.AddUdonSharpComponent both call
		/// RunBehaviourSetup; that is the whole difference.
		///
		/// Returns the added component, and whether the UdonSharp setup ran, so
		/// a caller can report which of the two happened instead of assuming.
		///
		/// Every branch adds through Undo.AddComponent, which is the ONLY add
		/// form that one Ctrl-Z actually reverses. Measured on Unity 2022.3, with
		/// the component added first and Undo.RecordObject(go) called after (the
		/// obvious shape, and the one this used to use):
		///
		///   Undo.AddComponent(go, typeof(C))            -> revert removes it
		///   AddComponent(C) then RecordObject(go)        -> revert KEEPS it
		///   AddComponent(C) then RegisterComplete..(go)  -> revert KEEPS it
		///   RecordObject(go) then AddComponent(C)        -> revert KEEPS it
		///
		/// RecordObject snapshots the object's state as it is NOW, so every
		/// after-the-fact variant records a state that already contains the new
		/// component, and reverting to it is a no-op. Recording BEFORE the add
		/// does not help either: a component added by plain AddComponent is not
		/// part of the GameObject's undo state at all. So a report claiming
		/// "recorded as one undo step" is a claim about a record that cannot
		/// undo the thing it describes.
		///
		/// The UdonSharp branch needs the same guarantee for two components (the
		/// proxy and the UdonBehaviour it compiles to), so it registers the proxy
		/// itself and then calls RunBehaviourSetup rather than the package's
		/// AddUdonSharpComponent, which adds plainly and is not undoable.
		public static Component AddBehaviourComponent(GameObject target, Type type, string undoName, out bool udonSharpSetup)
		{
			udonSharpSetup = false;
			MethodInfo add = AddUdonSharpComponentMethod();
			Type behaviour = VrcResolve.Type(UdonSharpBehaviourType);
			if (add != null && behaviour != null && behaviour != type && behaviour.IsAssignableFrom(type))
			{
				// UdonSharp's own add path dereferences the program's asset, so a
				// script that was written but never given one throws a bare
				// NullReferenceException from inside the package, several frames
				// down and naming nothing. Create the asset first, the way the
				// package's Create Script button does.
				LastCreatedProgramAsset = EnsureProgramAsset(type);

				// Register the proxy with Undo, then let UdonSharp attach the
				// backing UdonBehaviour itself. Its AddUdonSharpComponent does
				// both steps, but its first step is a plain AddComponent, so
				// undo leaves the proxy behind -- a half-built component that
				// looks fine until something reads its fields.
				Component proxy = Undo.AddComponent(target, type);
				if (proxy != null)
				{
					RunUdonSharpSetup(proxy);
					udonSharpSetup = true;
				}
				if (proxy != null) return proxy;
			}
			return Undo.AddComponent(target, type);
		}

		/// RunBehaviourSetup, which is what pairs a UdonSharp proxy with the
		/// UdonBehaviour it compiles to. Its own AddUdonSharpComponent calls this
		/// as its second step; doing it here keeps the first step undoable.
		private static void RunUdonSharpSetup(Component proxy)
		{
			MethodInfo setup = VrcResolve.Type("UdonSharpEditor.UdonSharpEditorUtility")
				?.GetMethod("RunBehaviourSetup", new[] { proxy.GetType() });
			if (setup != null) setup.Invoke(null, new object[] { proxy });
		}

		/// The program asset EnsureProgramAsset had to create for the last
		/// AddBehaviourComponent call, or null when none was needed. A caller
		/// about to report success has to be able to name the file: a UdonSharp
		/// script with no program asset is not a working script, and nothing
		/// else in the workflow will say so.
		public static string LastCreatedProgramAsset { get; private set; }

		/// A UdonSharp program asset is a separate .asset file sitting beside the
		/// .cs, and UdonSharpEditorUtility.GetUdonSharpProgramAsset only finds
		/// one that ALREADY exists -- it looks scripts up in a dictionary built
		/// by scanning existing assets. So a freshly written script has no
		/// program until something creates it, and adding the component without
		/// one throws.
		///
		/// The asset type is UdonSharp.UdonSharpProgramAsset, not the
		/// UdonSharpEditor.UdonSharpProgramAsset its own source file's namespace
		/// suggests. The MonoScript is found by asking every script asset which
		/// class it declares, because there is no MonoScript.FromMonoBehaviourType
		/// on this Unity version and the type alone does not give a path.
		public static string EnsureProgramAsset(Type behaviourType)
		{
			Type programAssetType = VrcResolve.Type("UdonSharp.UdonSharpProgramAsset");
			if (programAssetType == null) return null;

			var get = VrcResolve.Type("UdonSharpEditor.UdonSharpEditorUtility")
				?.GetMethod("GetUdonSharpProgramAsset", new[] { typeof(Type) });
			if (get != null && get.Invoke(null, new object[] { behaviourType }) != null) return null;

			string scriptPath = null;
			foreach (string guid in UnityEditor.AssetDatabase.FindAssets("t:MonoScript"))
			{
				string path = UnityEditor.AssetDatabase.GUIDToAssetPath(guid);
				if (string.IsNullOrEmpty(path) || !path.EndsWith(".cs")) continue;
				var loaded = UnityEditor.AssetDatabase.LoadAssetAtPath<MonoScript>(path);
				if (loaded == null || loaded.GetClass() != behaviourType) continue;
				scriptPath = path;
				break;
			}
			if (string.IsNullOrEmpty(scriptPath)) return null;

			string assetPath = scriptPath.Substring(0, scriptPath.Length - 3) + ".asset";
			if (UnityEditor.AssetDatabase.LoadAssetAtPath<UnityEngine.Object>(assetPath) != null) return null;

			FieldInfo source = programAssetType.GetField("sourceCsScript",
				BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
			var script = UnityEditor.AssetDatabase.LoadAssetAtPath<MonoScript>(scriptPath);
			if (source == null || script == null) return null;

			var program = ScriptableObject.CreateInstance(programAssetType);
			source.SetValue(program, script);
			UnityEditor.AssetDatabase.CreateAsset(program, assetPath);
			UnityEditor.AssetDatabase.SaveAssets();
			return assetPath;
		}

		/// UdonSharpEditor.UdonSharpComponentExtensions.AddUdonSharpComponent,
		/// which is a public static extension method on a static class -- so
		/// reflection over the class finds it as an ordinary static, and its
		/// GameObject parameter is what has to be matched, not the script type.
		private static MethodInfo AddUdonSharpComponentMethod()
		{
			Type extensions = VrcResolve.Type("UdonSharpEditor.UdonSharpComponentExtensions");
			if (extensions == null) return null;
			try
			{
				foreach (MethodInfo candidate in extensions.GetMethods(BindingFlags.Public | BindingFlags.Static))
				{
					if (candidate.Name != "AddUdonSharpComponent") continue;
					ParameterInfo[] parameters = candidate.GetParameters();
					if (parameters.Length != 2) continue;
					if (parameters[0].ParameterType != typeof(GameObject)) continue;
					if (parameters[1].ParameterType != typeof(Type)) continue;
					return candidate;
				}
			}
			catch (Exception)
			{
				return null;
			}
			return null;
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
		/// What is and is not automated about UdonSharp, because the two halves are
		/// easy to confuse and confusing them wastes a whole module.
		///
		/// WRITING A SCRIPT is not automated: it means authoring a .cs and
		/// compiling it, and a compile of this size runs 4-6 minutes -- longer
		/// than a bridge request may wait, so a helper that did it would look
		/// like it worked and then hang the Editor. The compile itself is not
		/// blocked either: request it in one short call and read the result with
		/// the "compile-status" request, which reports the compiler errors.
		///
		/// PLACING ONE is automated. Once the script exists, create-behaviour
		/// adds it, creates the program asset a hand-written script does not get
		/// on its own, and copies any field values into the compiled program.
		public static string ExplainWhyNoScriptCreation()
		{
			return
				"Writing UdonSharp SOURCE is not automated; placing a script that exists is.\n" +
				"Writing means authoring a .cs and compiling it. A compile in a project this\n" +
				"size runs 4-6 minutes, which is longer than a bridge request may wait, so a\n" +
				"helper that did it would appear to work and then hang the Editor. That is\n" +
				"why it is left to a human at the Editor. The compile is not stuck, though:\n" +
				"request it in one short call, then read the \"compile-status\" request for the\n" +
				"compiler's own errors rather than sleeping and guessing.\n" +
				"\n" +
				"Placing one IS automated. For a script you wrote in the Project window:\n" +
				"  unity_world create-behaviour { behaviour: \"Full.Type.Name\", gameObject, fields }\n" +
				"creates the program asset if the script has none (a fresh script does not,\n" +
				"and adding the component without one throws from inside the package), adds\n" +
				"the behaviour, and copies the field values into the compiled program.\n" +
				"\n" +
				"Do NOT set a UdonSharpBehaviour field with unity_set_property. The proxy keeps\n" +
				"its own copy of every field, so that writes the copy the Inspector shows and\n" +
				"nothing the world runs; the value is discarded at build with no error. Read\n" +
				"udon-inventory to check what the program actually holds.";
		}
	}
}
