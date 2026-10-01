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
using System.Reflection;
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
								"No scene descriptor in the open scene (looked for "
								+ string.Join(", ", PiUdon.SceneDescriptorNames) + "). A world scene needs one; without it "
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
						// The descriptor's fields ARE the world's spawn configuration,
						// and they are scattered across two SDK generations (the SDK3
						// type and the legacy one name different fields for the same
						// settings). Reporting the ones that exist turns this action from
						// "here is a GameObject" into "here is how this world spawns
						// players", which is the question this action exists to answer.
						foreach (string field in DescriptorFields)
						{
							// AddString, not Add: the Json writer's bool overload is the
							// only Add(string, ...) there is besides Json itself.
							result.AddString(field, PiLightVolumes.ReadField(descriptor, field)?.ToString() ?? "");
						}
						result.AddString("note", "Read any remaining field with unity_inspect { gameObject, component: \""
							+ descriptor.GetType().Name + "\" }.");
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
						float range = request.GetFloat("range", 10f);
						float intensity = request.GetFloat("intensity", 1f);
						string colorHex = request.GetString("color") ?? "ffffff";

						Color color = PiWorldCreate.ColorFromHex(colorHex);
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
						// Whether a new object appeared is the difference between one
						// volume and two, so it is reported rather than left to be
						// inferred from the object list.
						result.AddBool("newGameObject", string.IsNullOrEmpty(targetName));
						result.AddString("note",
							"Defaults are chosen to be VISIBLE (range 10, intensity 1). A light volume with "
							+ "zero range or intensity bakes fine and contributes nothing. "
							+ "The volume also needs a LightVolumeSetup in the scene to bake at all -- run "
							+ "light-volumes to see whether one is present. "
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

				case "udon-inventory":
					return WorldTextResult(PiCensus.DescribeUdon());

				// The full inventory, not the count: which UdonSharp script each
				// behaviour runs, how it syncs, and what it exposes. A count says a
				// scene has Udon; only the inventory says which script is wrong.
				case "audio-link":
					return WorldTextResult(PiPackages.DescribeAudioLink());
				case "yama":
					return WorldTextResult(PiPackages.DescribeYama());
				case "hand-menu":
					return WorldTextResult(PiPackages.DescribeHandMenu());

				case "audio-link-set":
					return JsonActionResult("audio-link-set", PiPackages.SetAudioLink(
						PackageTarget(request), request.GetString("property"), request.GetString("value")));
				case "yama-set":
					return JsonActionResult("yama-set", PiPackages.SetYama(
						PackageTarget(request), request.GetString("property"), request.GetString("value")));
				case "hand-menu-set":
					return JsonActionResult("hand-menu-set", PiPackages.SetHandMenu(
						PackageTarget(request), request.GetString("property"), request.GetString("value")));

				case "find-behaviour":
					{
						string name = request.GetString("behaviour");
						if (string.IsNullOrEmpty(name))
							throw new Exception("find-behaviour requires a behaviour name, such as VRC.SDK3.Components.VRCObjectSync or VRCObjectSync. Known names: "
								+ string.Join(", ", PiUdon.WorldBehaviourNames) + ".");
						string onObject = request.GetString("gameObject");
						// No gameObject means SEARCH THE SCENE, not search nothing:
						// passing null through is what lets PiUdon widen the search.
						// Short-circuiting here would report a scene-wide miss as
						// though only the named object had been checked.
						Component found = PiUdon.FindBehaviour(name, string.IsNullOrEmpty(onObject) ? null : FindGameObjectByName(onObject));
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
							// The note has to say WHICH search failed: "not found"
							// without naming the scope is indistinguishable from "your
							// project does not have this behaviour", and the two have
							// opposite fixes.
							result.AddString("searched",
								string.IsNullOrEmpty(onObject) ? "the whole scene" : "\"" + onObject + "\" and its children");
							result.AddString("note", string.IsNullOrEmpty(onObject)
								? "No such behaviour anywhere in the open scene. Known world behaviour names: "
									+ string.Join(", ", PiUdon.WorldBehaviourNames) + "."
								: "Not on that GameObject or its children. Known world behaviour names: "
									+ string.Join(", ", PiUdon.WorldBehaviourNames) + ".");
						}
						result.CloseObject();
						return result.ToString();
					}

				case "toolkit-state":
					return WorldTextResult(PiWorldToolkit.DescribeProjectState());

				case "setup-layers":
					// Not routed through VRWorld Toolkit: UpdateLayers is the
					// VRChat SDK's own, so this works with or without the package.
					Scene activeScene = SceneManager.GetActiveScene();
					if (activeScene.IsValid()) EditorSceneManager.MarkSceneDirty(activeScene);
					return JsonActionResult("setup-layers", PiWorldToolkit.SetupLayers());

				case "setup-post-processing":
					return JsonActionResult("setup-post-processing", PiWorldToolkit.SetupPostProcessing());

				case "toggle-tag":
					{
						string tag = request.GetString("tag");
						if (string.IsNullOrEmpty(tag)) throw new Exception("toggle-tag requires a tag.");
						bool active = !request.GetBool("inactive");
						return JsonActionResult("toggle-tag", PiWorldToolkit.ToggleTag(tag, active));
					}

				case "quest-switch":
					return WorldTextResult(PiQuestSwitch.Describe());

				case "quest-add":
					return JsonActionResult("quest-add", PiQuestSwitch.Add(request.GetString("object"),
						request.GetString("path"), request.GetString("component")));

				case "quest-apply":
					return JsonActionResult("quest-apply", PiQuestSwitch.Apply(request.GetString("target")));

				case "build-checks":
					return WorldTextResult(PiBuildChecks.RunBuildChecks());

				case "upload-risk":
					return WorldTextResult(PiBuildChecks.RunUploadRisk());

				case "performance":
					return WorldTextResult(PiCensus.DescribePerformance());

				case "environment":
					return WorldTextResult(PiCensus.DescribeWorldEnvironment());

				case "create-behaviour":
					return JsonActionResult("create-behaviour", PiWorldCreate.CreateBehaviour(
						request.GetString("behaviour"), request.GetString("gameObject"), request.GetString("fields")));

				// Parsed as strings, not through GetFloat: a range or intensity
				// of 0 here means "use the light's own value", and a numeric
				// parser cannot tell that from a deliberate zero.
				case "convert-light":
					return JsonActionResult("convert-light", PiWorldCreate.ConvertLight(
						request.GetString("gameObject"), request.GetString("lightType"),
						request.GetString("range"), request.GetString("intensity"),
						request.GetString("color"), request.GetString("keepOriginal")));

				case "materials":
					return WorldTextResult(PiMaterials.DescribeMaterials());

				case "fix-android-shaders":
					// GetString returns "" for a missing key rather than null, so
					// the fallback needs an explicit emptiness test: a plain ??
					// never fires and the action reads as if no name were sent.
					{
						string materialName = request.GetString("material");
						if (string.IsNullOrEmpty(materialName)) materialName = request.GetString("gameObject");
						return JsonActionResult("fix-android-shaders", PiMaterials.FixAndroidShaders(
							materialName, request.GetString("shader")));
					}

				case "spawn-points":
					return WorldTextResult(PiSpawnPoints.DescribeSpawnPoints());

				case "add-spawn-point":
					return JsonActionResult("add-spawn-point", PiSpawnPoints.AddSpawnPoint(
						request.GetString("gameObject"), request.GetString("position"),
						request.GetString("rotation")));

				case "colliders":
					return WorldTextResult(PiColliders.DescribeColliders());

				case "add-collider":
					return JsonActionResult("add-collider", PiColliders.AddCollider(
						request.GetString("gameObject"), request.GetString("shape"),
						request.GetString("size"), request.GetString("isTrigger")));

				case "add-trigger":
					return JsonActionResult("add-trigger", PiColliders.AddTrigger(
						request.GetString("gameObject"), request.GetString("shape"),
						request.GetString("size")));

				case "audio":
					return WorldTextResult(PiAudio.DescribeAudio());

				case "add-audio-source":
					return JsonActionResult("add-audio-source", PiAudio.AddAudioSource(
						request.GetString("gameObject"), request.GetString("clip"),
						request.GetString("volume"), request.GetString("spatial"),
						request.GetString("loop")));

				case "set-clip-load-type":
					return JsonActionResult("set-clip-load-type", PiAudio.SetClipLoadType(
						request.GetString("clip"), request.GetString("loadType")));

				case "actions":
					return WorldTextResult("actions: " + string.Join(", ", WorldActions));

				default:
					throw new Exception(
						"Unknown world action \"" + action + "\". Known actions: " + string.Join(", ", WorldActions)
						+ ". Start with action \"scene\"."
					);
			}
		}

		/// The GameObject a <package>-set call targets.
		///
		/// `gameObject` is the parameter every other world action already uses,
		/// so it is read here rather than introducing a second spelling. It is
		/// validated before any work: a set call that writes the wrong object is
		/// the one failure a name-addressed tool cannot tolerate.
		private static string PackageTarget(Json request)
		{
			string name = request.GetString("gameObject");
			if (string.IsNullOrEmpty(name))
			{
				throw new Exception("This action sets a field on an EXISTING component, so it needs a gameObject: "
					+ "the name of the object carrying the component. Run the matching describe action first to see "
					+ "which objects have one and which fields they expose.");
			}
			return name;
		}

		/// The action list, in one place so the `actions` answer, the error for a
		/// typo, and the tool description cannot disagree about what exists.
		private static readonly string[] WorldActions =
		{
			"scene", "descriptor", "light-volumes", "create-light-volume", "convert-light",
			"udon", "udon-inventory", "find-behaviour", "create-behaviour",
			"toolkit-state", "setup-layers", "setup-post-processing", "toggle-tag",
			"quest-switch", "quest-add", "quest-apply",
			"build-checks", "upload-risk", "performance", "environment",
			"audio-link", "audio-link-set", "yama", "yama-set", "hand-menu", "hand-menu-set",
			"materials", "fix-android-shaders", "spawn-points", "add-spawn-point",
			"colliders", "add-collider", "add-trigger",
			"audio", "add-audio-source", "set-clip-load-type",
			"actions",
		};

		/// A mutating action's prose report, tagged with the action that produced
		/// it, so a caller that ran two in a row can tell the answers apart.
		private static string JsonActionResult(string action, string report)
		{
			Json result = new Json();
			result.OpenObject();
			result.AddString("action", action);
			result.AddString("report", report ?? "");
			result.CloseObject();
			return result.ToString();
		}

		/// The descriptor fields worth reporting, read by NAME so a field the
		/// project does not have simply comes back empty instead of throwing.
		/// The two SDK generations spell some of these differently, which is
		/// exactly why they are read by name and not assumed present.
		private static readonly string[] DescriptorFields =
		{
			"spawnRadius", "spawnOrder", "spawnOrientation", "RespawnHeightY",
			"ObjectBehaviourAtRespawnHeight", "ForbidUserPortals", "ReferenceCamera",
			"DefaultSpawnScale", "UseCustomSpawnScale", "unityVersion",
		};

		/// A fresh, EMPTY GameObject on the active scene's root, named as asked.
		///
		/// Created with Undo registration so a mistaken call is one Ctrl-Z away,
		/// which is the same standard the other write paths hold to.
		private static GameObject NewWorldObject(string name)
		{
			GameObject created = new GameObject(PiWorldCreate.UniqueName(name));
			Undo.RegisterCreatedObjectUndo(created, "pi create " + name);
			return created;
		}

		/// Walk the base chain for an instance field, public or not.
		///
		/// The base walk is not optional. Every behaviour this plugin configures
		/// is reached through a package class that INHERITS its fields: the
		/// EasyQuestSwitch entry fields, the AudioLink and YamaPlayer properties
		/// and the VRChat behaviour fields all live on a base type, and a
		/// GetField on the concrete type alone returns null for every one of them
		/// -- which reads as "this package has no such field" rather than as
		/// "look further up".
		internal static FieldInfo FindInstanceField(Type type, string name)
		{
			while (type != null)
			{
				FieldInfo field = type.GetField(name, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
				if (field != null) return field;
				type = type.BaseType;
			}
			return null;
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
