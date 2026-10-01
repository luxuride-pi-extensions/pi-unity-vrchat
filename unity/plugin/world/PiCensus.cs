// Part of the pi Unity bridge plugin. See PiUnityBridge.Core.cs for the
// protocol overview. The whole plugin is one assembly of partial classes, so
// Unity compiles it into Assembly-CSharp-Editor with no asmdef and no
// package dependencies, which is what lets it drop into any Unity 2021+
// project (a configured VRChat world, an existing game) that pi was pointed at
// rather than generated.
//
// Three READ-ONLY reports about a world scene: its Udon, what it will cost to
// run, and how it looks.
//
// THE PERFORMANCE REPORT IS A PROXY, NOT THE RATING
// -------------------------------------------------
// VRChat computes the Performance rank at BUILD time, from the built content
// and the frame cost on the target device -- data this Editor does not expose.
// Every number here is a stand-in for that cost, so nothing in DescribePerformance
// IS a rating, and every section says whether it is measured or estimated.
//
// A CHECK THAT DID NOT RUN IS A LINE, NOT AN ABSENCE
// ---------------------------------------------------
// Every optional member prints "not available: <reason>" when it cannot be bound.
// A section that silently prints nothing is indistinguishable from a section
// that ran and found nothing, and those two need opposite fixes.

using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text;
using UnityEditor;
using UnityEngine;

namespace PiUnity
{
	public static class PiCensus
	{
		/// VARIABLE TABLES ARE NOT IN NAMESPACE VRC.Udon.
		///
		/// publicVariables is an IUdonVariableTable from
		/// VRC.Udon.Common.Interfaces; the type that IMPLEMENTS it is in VRC.Udon.
		/// Binding the wrong one looks right until the call throws at runtime.
		private const string VariableTableInterface = "VRC.Udon.Common.Interfaces.IUdonVariableTable";

		private const string UdonSharpEditorUtility = "UdonSharpEditor.UdonSharpEditorUtility";
		private const string PostProcessLayerType = "UnityEngine.Rendering.PostProcessing.PostProcessLayer";
		private const string VolumeType = "UnityEngine.Rendering.PostProcessing.Volume";
		private const string VolumeProfileType = "UnityEngine.Rendering.PostProcessing.VolumeProfile";
		private const string PointLightVolumeType = "VRCLightVolumes.PointLightVolume";
		private const string ToolkitValidationType = "VRWorldToolkit.Editor.Validation";
		private const string SdkValidationType = "VRC.SDKBase.Validation.WorldValidation";

		/// A UdonSharp proxy with hundreds of synced variables is a finding, not a
		/// transcript. The full count is always reported; the listing is capped.
		private const int VariablesPerBehaviour = 40;

		private const int RankedBehaviourCount = 5;
		private const int LeverCount = 5;

		/// A scene measurement, kept alongside the line it was printed on so the
		/// closing "biggest levers" list is ranked from what was actually counted.
		private sealed class Lever
		{
			public string Name;
			public long Count;
			public bool Estimate;
			public string Reduction;
		}

		/// One UdonBehaviour's identity and weight, for the ranking at the end.
		private sealed class UdonEntry
		{
			public string Name;
			public string Path;
			public int Variables;
			public long ProgramSize;
		}

		// ===================================================== 1. Udon

		/// Every UdonBehaviour in the scene, what script each one really runs,
		/// and what it can see.
		public static string DescribeUdon()
		{
			var report = new StringBuilder();
			report.AppendLine("--- Udon inventory ---");
			List<Component> behaviours = PiUdon.FindUdonBehaviours();
			report.AppendLine("UdonBehaviours=" + behaviours.Count
				+ "  UdonSharp installed=" + (PiUdon.IsUdonSharpInstalled() ? "yes" : "no"));
			if (behaviours.Count == 0)
			{
				report.AppendLine("not available: no VRC.Udon.UdonBehaviour was found in the open scene"
					+ (PiUdon.IsWorldsInstalled()
						? ". The scene carries no Udon."
						: " because com.vrchat.worlds is not installed (nothing to inventory)."));
				return report.ToString();
			}

			var entries = new List<UdonEntry>();
			int totalVariables = 0;
			var failures = new List<UdonEntry>();

			for (int index = 0; index < behaviours.Count; index++)
			{
				Component behaviour = behaviours[index];
				report.AppendLine("[" + (index + 1) + "/" + behaviours.Count + "]");
				var entry = new UdonEntry
				{
					Name = behaviour.gameObject.name,
					Path = PathOf(behaviour.transform),
				};
				report.AppendLine("  object: " + entry.Name);
				report.AppendLine("  path: " + entry.Path);
				AppendMember(report, "  ", behaviour, "SyncMethod", "");
				AppendProgram(report, behaviour);
				AppendMember(report, "  ", behaviour, "HasError", "");
				AppendMember(report, "  ", behaviour, "ProgramId", "");
				AppendMember(report, "  ", behaviour, "ProgramSize", " bytes");
				AppendMember(report, "  ", behaviour, "InteractionText", "");
				AppendMember(report, "  ", behaviour, "IsInteractive", "");
				AppendMember(report, "  ", behaviour, "IsNetworkingSupported", "");

				if (TryReadMember(behaviour, "HasError", out object error) && error is bool failed && failed)
				{
					failures.Add(entry);
				}

				entry.Variables = AppendVariables(report, behaviour);
				totalVariables += entry.Variables;
				// ProgramSize is a ulong, so an int cast here would report every
				// real program as 0 bytes -- which is what a size of zero means.
				if (TryReadMember(behaviour, "ProgramSize", out object size))
				{
					entry.ProgramSize = (long)Convert.ToUInt64(size);
				}
				entries.Add(entry);
			}

			report.AppendLine("--- summary ---");
			report.AppendLine("total behaviours=" + behaviours.Count
				+ "  total public variables=" + totalVariables);
			report.AppendLine("The variable table is every PUBLIC variable the program can read; which of them are");
			report.AppendLine("synced is a per-variable UdonSharp attribute and is not exposed to the Editor, so the");
			report.AppendLine("synced count is bounded by this total and cannot be read exactly here.");
			report.AppendLine("heaviest behaviours (by variable count, then program size):");
			bool anyRanked = false;
			foreach (UdonEntry entry in entries
				.OrderByDescending(e => e.Variables)
				.ThenByDescending(e => e.ProgramSize)
				.Take(RankedBehaviourCount))
			{
				anyRanked = true;
				report.AppendLine("  " + entry.Name + "  variables=" + entry.Variables
					+ "  programSize=" + entry.ProgramSize + " bytes  path=" + entry.Path);
			}
			if (!anyRanked) report.AppendLine("  none to rank.");

			if (failures.Count == 0)
			{
				report.AppendLine("no behaviour reports HasError.");
			}
			else
			{
				report.AppendLine("FAIL: " + failures.Count + " behaviour(s) report HasError=True. Udon refuses to run a");
				report.AppendLine("program that failed to compile, so these do NOTHING in VRChat and will not warn there.");
				foreach (UdonEntry entry in failures)
				{
					report.AppendLine("  " + entry.Name + "  path=" + entry.Path
						+ "  (recompile UdonSharp and read the Console for the compile error)");
				}
			}
			return report.ToString();
		}

		/// The script a behaviour actually runs.
		///
		/// A UdonSharp proxy's own class name IS the script name, and the program
		/// asset path is the fallback for a hand-written Udon program, which has no
		/// proxy at all. Both are printed: the path is the only place a non-UdonSharp
		/// program is named, and for a UdonSharp behaviour that asset sits under
		/// UdonSharpProgramAssets rather than carrying the script name.
		private static void AppendProgram(StringBuilder report, Component behaviour)
		{
			Component proxy = ProxyBehaviour(behaviour, out string proxyUnavailable);
			if (proxy == null)
			{
				report.AppendLine("  script: " + (proxyUnavailable == null
					? "(no UdonSharp proxy on this behaviour -- a hand-written Udon program)"
					: "not available: " + proxyUnavailable));
			}
			else
			{
				report.AppendLine("  script: " + proxy.GetType().Name
					+ "  (UdonSharp proxy, [" + proxy.GetType().Assembly.GetName().Name + "])");
			}

			UnityEngine.Object program = null;
			foreach (string member in new[] { "programAsset", "serializedProgramAsset" })
			{
				if (TryReadMember(behaviour, member, out object asset) && asset is UnityEngine.Object found)
				{
					program = found;
					break;
				}
			}
			if (program == null)
			{
				report.AppendLine("  programAsset: not available: no readable programAsset on this behaviour");
				return;
			}
			string path = PathOf(program);
			report.AppendLine("  programAsset: " + path);
			if (proxy == null && !string.IsNullOrEmpty(AssetPathOf(program)))
			{
				report.AppendLine("  script (from program asset): " + ScriptNameFromPath(path));
			}
		}

		private static int AppendVariables(StringBuilder report, Component behaviour)
		{
			// Read the table off the UDONBEHAVIOUR, not the proxy. A UdonSharp
			// proxy's publicVariables is its own C# field, so a report built from
			// it shows the values as they are typed in the Inspector -- including
			// every edit made since the last build. The UdonBehaviour's table is
			// the COMPILED program's variables, which is the thing that will
			// actually run, and a value in the proxy that never reached it is
			// exactly the fault this report exists to surface.
			if (!TryReadMember(behaviour, "publicVariables", out object raw))
			{
				report.AppendLine("  variables: not available: no readable publicVariables member on "
					+ behaviour.GetType().FullName);
				return 0;
			}
			if (raw == null)
			{
				report.AppendLine("  variables: not available: publicVariables is null (this behaviour has no"
					+ " variable table yet)");
				return 0;
			}

			List<string> lines = VariableLines(raw, out string reason);
			if (reason != null)
			{
				report.AppendLine("  variables: not available: " + reason);
				return 0;
			}
			report.AppendLine("  variables: " + lines.Count);
			for (int index = 0; index < lines.Count && index < VariablesPerBehaviour; index++)
			{
				report.AppendLine("    " + lines[index]);
			}
			if (lines.Count > VariablesPerBehaviour)
			{
				report.AppendLine("    ... " + (lines.Count - VariablesPerBehaviour)
					+ " more on this behaviour (read it directly with unity_inspect if you need them)");
			}
			return lines.Count;
		}

		private static List<string> VariableLines(object table, out string reason)
		{
			var lines = new List<string>();
			reason = null;
			Type iface = ResolveVariableTableInterface(table);
			if (iface == null)
			{
				reason = "the variable table's type does not implement " + VariableTableInterface;
				return lines;
			}

			IReadOnlyCollection<string> symbols = null;
			try
			{
				PropertyInfo property = iface.GetProperty("VariableSymbols");
				if (property == null || !property.CanRead)
				{
					reason = VariableTableInterface + " has no readable VariableSymbols in this Udon version";
					return lines;
				}
				symbols = property.GetValue(table, null) as IReadOnlyCollection<string>;
			}
			catch (Exception error)
			{
				reason = "VariableSymbols threw: " + (error.InnerException ?? error).Message;
				return lines;
			}
			if (symbols == null)
			{
				reason = "VariableSymbols returned null on this behaviour";
				return lines;
			}

			MethodInfo getValue = iface.GetMethod("TryGetVariableValue", new[] { typeof(string), typeof(object).MakeByRefType() });
			MethodInfo getType = iface.GetMethod("TryGetVariableType", new[] { typeof(string), typeof(Type).MakeByRefType() });
			if (getValue == null)
			{
				reason = VariableTableInterface + " has no TryGetVariableValue in this Udon version";
				return lines;
			}

			// Sorted so two runs over the same scene produce the same report; the
			// table itself is an unordered set.
			foreach (string symbol in symbols.OrderBy(s => s, StringComparer.Ordinal))
			{
				lines.Add(symbol + " (" + VariableType(table, symbol, getType) + ") = "
					+ VariableValue(table, symbol, getValue));
			}
			return lines;
		}

		private static string VariableType(object table, string symbol, MethodInfo getType)
		{
			if (getType == null) return "<type not available>";
			try
			{
				object[] arguments = { symbol, null };
				if (getType.Invoke(table, arguments) is bool ok && !ok)
				{
					return "<type not available: TryGetVariableType returned false>";
				}
				object type = arguments[1];
				if (type is Type named) return named.Name;
				return type == null ? "<unknown>" : type.ToString();
			}
			catch (Exception error)
			{
				return "<type not available: " + (error.InnerException ?? error).Message + ">";
			}
		}

		private static string VariableValue(object table, string symbol, MethodInfo getValue)
		{
			try
			{
				object[] arguments = { symbol, null };
				if (getValue.Invoke(table, arguments) is bool ok && !ok)
				{
					return "<not available: TryGetVariableValue returned false>";
				}
				return FormatValue(arguments[1]);
			}
			catch (Exception error)
			{
				return "<not available: " + (error.InnerException ?? error).Message + ">";
			}
		}

		private static Type ResolveVariableTableInterface(object table)
		{
			Type resolved = VrcResolve.Type(VariableTableInterface);
			if (resolved != null && resolved.IsInstanceOfType(table)) return resolved;
			foreach (Type candidate in table.GetType().GetInterfaces())
			{
				if (candidate.Name == "IUdonVariableTable") return candidate;
			}
			return resolved;
		}

		private static Component ProxyBehaviour(Component behaviour, out string unavailable)
		{
			unavailable = null;
			Type type = VrcResolve.Type(UdonSharpEditorUtility);
			if (type == null)
			{
				unavailable = UdonSharpEditorUtility + " is not loaded (UdonSharp is not installed, so no behaviour"
					+ " in this scene has a proxy)";
				return null;
			}
			MethodInfo method = type.GetMethod("GetProxyBehaviour", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static);
			if (method == null)
			{
				method = type.GetMethod("FindProxyBehaviour", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static);
			}
			if (method == null)
			{
				unavailable = UdonSharpEditorUtility + " has neither GetProxyBehaviour nor FindProxyBehaviour"
					+ " in this UdonSharp version";
				return null;
			}
			try
			{
				return method.Invoke(null, new object[] { behaviour }) as Component;
			}
			catch (Exception error)
			{
				unavailable = UdonSharpEditorUtility + "." + method.Name + " threw: "
					+ (error.InnerException ?? error).Message;
				return null;
			}
		}

		// ================================================ 2. performance

		/// What this scene costs to run, as a proxy for the build rating.
		public static string DescribePerformance()
		{
			var report = new StringBuilder();
			report.AppendLine("--- scene performance census ---");
			report.AppendLine("PROXY ONLY: VRChat computes the Performance rank at BUILD time from data this Editor");
			report.AppendLine("does not expose, so no number below is a rating -- each section says measured or estimate.");

			var levers = new List<Lever>();
			AppendObjects(report, levers);
			AppendRenderers(report, levers);
			AppendMaterials(report, levers);
			AppendLights(report, levers);
			AppendParticles(report, levers);
			AppendTextures(report, levers);
			AppendAudio(report, levers);
			AppendLightmaps(report, levers);
			AppendColliders(report, levers);
			AppendLevers(report, levers);
			return report.ToString();
		}

		private static void AppendObjects(StringBuilder report, List<Lever> levers)
		{
			report.AppendLine("--- objects [measured] ---");
			Transform[] transforms = UnityEngine.Object.FindObjectsOfType<Transform>(true);
			int activeInHierarchy = 0;
			int activeSelf = 0;
			var byTag = new Dictionary<string, int>(StringComparer.Ordinal);
			foreach (Transform child in transforms)
			{
				if (child.gameObject.activeInHierarchy) activeInHierarchy++;
				if (child.gameObject.activeSelf) activeSelf++;
				string tag;
				try { tag = child.gameObject.tag; } catch (Exception) { tag = "<tag not in the tag manager>"; }
				byTag.TryGetValue(tag, out int seen);
				byTag[tag] = seen + 1;
			}
			report.AppendLine("  GameObjects=" + transforms.Length + "  Transforms=" + transforms.Length
				+ "  (every GameObject carries exactly one Transform, so the two counts are the same number);");
			report.AppendLine("  active in hierarchy=" + activeInHierarchy
				+ "  inactive in hierarchy=" + (transforms.Length - activeInHierarchy));
			report.AppendLine("  active self=" + activeSelf
				+ "  self-disabled=" + (transforms.Length - activeSelf));
			report.AppendLine("  by tag:");
			foreach (KeyValuePair<string, int> entry in byTag.OrderByDescending(p => p.Value).ThenBy(p => p.Key, StringComparer.Ordinal))
			{
				report.AppendLine("    " + entry.Key + "=" + entry.Value);
			}
			if (byTag.Count == 0) report.AppendLine("    none (empty scene)");
			levers.Add(new Lever
			{
				Name = "scene objects",
				Count = transforms.Length,
				Estimate = false,
				Reduction = "delete the objects you do not need and merge the ones that never move; each one is a"
					+ " culling and batching unit, and a PC-only prop belongs behind EasyQuestSwitch",
			});
		}

		private static void AppendRenderers(StringBuilder report, List<Lever> levers)
		{
			report.AppendLine("--- renderers [measured counts, estimated cost] ---");
			MeshRenderer[] meshRenderers = UnityEngine.Object.FindObjectsOfType<MeshRenderer>(true);
			SkinnedMeshRenderer[] skinned = UnityEngine.Object.FindObjectsOfType<SkinnedMeshRenderer>(true);
			Renderer[] renderers = UnityEngine.Object.FindObjectsOfType<Renderer>(true);

			// UNIQUE meshes, not one entry per renderer: a prop kit that instances
			// the same mesh 50 times costs 50 renderers but one mesh in the bundle,
			// and summing per renderer overstates the mesh side of it by 50x.
			var meshes = new HashSet<Mesh>();
			foreach (MeshFilter filter in UnityEngine.Object.FindObjectsOfType<MeshFilter>(true))
			{
				if (filter.sharedMesh != null) meshes.Add(filter.sharedMesh);
			}
			foreach (SkinnedMeshRenderer renderer in skinned)
			{
				if (renderer.sharedMesh != null) meshes.Add(renderer.sharedMesh);
			}
			long vertices = 0;
			foreach (Mesh mesh in meshes) vertices += mesh.vertexCount;

			report.AppendLine("  Renderer=" + renderers.Length
				+ "  MeshRenderer=" + meshRenderers.Length + "  SkinnedMeshRenderer=" + skinned.Length);
			report.AppendLine("  unique meshes=" + meshes.Count + "  total mesh vertices=" + vertices
				+ "  (estimate: vertex count is a stand-in for GPU memory and vertex-shader cost, not the real one)");
			int multiMaterial = renderers.Count(r => r.sharedMaterials.Length > 1);
			report.AppendLine("  renderers with more than one material=" + multiMaterial
				+ "  (each extra material is another draw call on the same mesh)");
			levers.Add(new Lever
			{
				Name = "renderers",
				Count = renderers.Length,
				Estimate = false,
				Reduction = "combine static props that share a material, and turn off the shadows of anything that"
					+ " does not need them",
			});
			levers.Add(new Lever
			{
				Name = "mesh vertices",
				Count = vertices,
				Estimate = true,
				Reduction = "decimate the heaviest meshes and strip unused channels; the Android build's mesh and"
					+ " vertex budget is where large props fail first, and this number is a stand-in for that cost",
			});
		}

		private static void AppendMaterials(StringBuilder report, List<Lever> levers)
		{
			report.AppendLine("--- materials [measured counts] ---");
			// From the SCENE's renderers, not from every loaded Material: an
			// asset that merely happens to be loaded in this Editor is not
			// something the world ships, and counting it would invent findings.
			// The same rule is why build-checks and this report agree.
			List<Material> materials = SceneMaterials();
			var shaders = new HashSet<Shader>();
			foreach (Material material in materials)
			{
				if (material.shader != null) shaders.Add(material.shader);
			}
			report.AppendLine("  materials=" + materials.Count + "  unique shaders=" + shaders.Count);

			HashSet<string> allowed;
			string whitelistSource;
			string whitelistReason;
			if (!PiMaterials.TryReadShaderWhiteList(out allowed, out whitelistSource, out whitelistReason))
			{
				report.AppendLine("  Android shader whitelist: not available: " + whitelistReason
					+ ", so the count below did not run.");
			}
			else
			{
				report.AppendLine("  Android shader whitelist from " + whitelistSource + " (" + allowed.Count + " shaders)");
				var offenders = new List<string>();
				foreach (Material material in materials)
				{
					if (material == null || material.shader == null) continue;
					if (allowed.Contains(material.shader.name)) continue;
					offenders.Add(material.name + " -> " + material.shader.name + " [" + PathOf(material) + "]");
				}
				report.AppendLine("  materials on a NON-whitelisted shader=" + offenders.Count
					+ "  (a material whose shader is not whitelisted does not build for Android at all)");
				foreach (string offender in offenders.Take(20)) report.AppendLine("    " + offender);
				if (offenders.Count > 20) report.AppendLine("    ... " + (offenders.Count - 20) + " more");
				report.AppendLine("  scene materials only: a material in a prefab that is not loaded in this scene is");
				report.AppendLine("  not in this count, and VRChat checks every one of them at build time.");
				if (offenders.Count > 0)
				{
					levers.Add(new Lever
					{
						Name = "materials on a non-whitelisted shader",
						Count = offenders.Count,
						Estimate = false,
						Reduction = "swap each one to a whitelisted shader; it is a build blocker, not a cost"
							+ " estimate, so it is the cheapest item on this list to fix",
					});
				}
			}
			levers.Add(new Lever
			{
				Name = "unique shaders",
				Count = shaders.Count,
				Estimate = true,
				Reduction = "share materials: every unique shader in the scene is a shader variant set and a"
					+ " switch on the GPU, and this count is a stand-in for that cost",
			});
		}

		/// The materials the SCENE's renderers actually use.
		///
		/// FindObjectsOfType<Material> reports every material the Editor has
		/// loaded, which is not the world's material list: an asset loaded by
		/// another scene or by a package editor would be counted as scene cost.
		/// SceneTextures uses the same source, so the two reports cannot
		/// disagree about what the scene contains.
		private static List<Material> SceneMaterials()
		{
			var materials = new List<Material>();
			var seen = new HashSet<Material>();
			foreach (Renderer renderer in UnityEngine.Object.FindObjectsOfType<Renderer>(true))
			{
				foreach (Material material in renderer.sharedMaterials)
				{
					if (material == null || !seen.Add(material)) continue;
					materials.Add(material);
				}
			}
			return materials;
		}

		/// The textures the SCENE's renderers actually use, deduplicated.
		///
		/// Every Texture subclass counts, not just Texture2D: a RenderTexture
		/// or Cubemap referenced by a scene material is a real upload cost, and
		/// a Texture2D-only search would not see it.
		private static Texture[] SceneTextures()
		{
			var textures = new List<Texture>();
			var seen = new HashSet<Texture>();
			foreach (Material material in SceneMaterials())
			{
				foreach (string slot in material.GetTexturePropertyNames())
				{
					Texture value = material.GetTexture(slot);
					if (value == null || !seen.Add(value)) continue;
					textures.Add(value);
				}
			}
			return textures.ToArray();
		}


		private static void AppendLights(StringBuilder report, List<Lever> levers)
		{
			report.AppendLine("--- lights [measured] ---");
			Light[] lights = UnityEngine.Object.FindObjectsOfType<Light>(true);
			int withShadows = 0;
			var byType = new Dictionary<string, int>(StringComparer.Ordinal);
			foreach (Light light in lights)
			{
				string type = light.type.ToString();
				byType.TryGetValue(type, out int seen);
				byType[type] = seen + 1;
				if (light.shadows != LightShadows.None) withShadows++;
				report.AppendLine("  " + light.name + "  type=" + type
					+ "  range=" + light.range.ToString("F2")
					+ "  intensity=" + light.intensity.ToString("F2")
					+ "  shadows=" + light.shadows
					+ "  path=" + PathOf(light.transform));
			}
			if (lights.Length == 0) report.AppendLine("  none");
			report.AppendLine("  total=" + lights.Length + "  with shadows=" + withShadows
				+ "  by type: " + FormatCounts(byType));
			report.AppendLine("  Every realtime light is per-frame work for every player; baked light is not.");

			if (!PiLightVolumes.IsInstalled())
			{
				report.AppendLine("  light volumes: not available: " + PointLightVolumeType + " is not loaded ("
					+ PiLightVolumes.Package + " is not installed), so nothing in this world bakes its lighting.");
			}
			else
			{
				List<Component> volumes = PiLightVolumes.FindAll();
				int dynamic = 0;
				foreach (Component volume in volumes)
				{
					if (PiLightVolumes.ReadField<bool>(volume, "Dynamic")) dynamic++;
				}
				report.AppendLine("  PointLightVolume=" + volumes.Count + "  Dynamic=" + dynamic
					+ "  baked=" + (volumes.Count - dynamic));
				report.AppendLine("  A Dynamic volume costs per frame like a realtime light; a non-dynamic one bakes"
					+ " into the volume and costs nothing at runtime.");
				levers.Add(new Lever
				{
					Name = "dynamic point light volumes",
					Count = dynamic,
					Estimate = false,
					Reduction = "turn Dynamic off on the volumes you do not need moving, so they bake instead",
				});
			}
			levers.Add(new Lever
			{
				Name = "realtime lights",
				Count = lights.Length,
				Estimate = false,
				Reduction = "bake the point and spot lights as light volumes and keep only what must move",
			});
		}

		private static void AppendParticles(StringBuilder report, List<Lever> levers)
		{
			report.AppendLine("--- particles [measured counts] ---");
			ParticleSystem[] systems = UnityEngine.Object.FindObjectsOfType<ParticleSystem>(true);
			int overBudget = 0;
			int withSubEmitters = 0;
			int subEmittersUnreadable = 0;
			long maxParticleTotal = 0;
			foreach (ParticleSystem system in systems)
			{
				maxParticleTotal += system.main.maxParticles;
				if (system.main.maxParticles > 1000) overBudget++;
				int subs = CountSubEmitters(system);
				if (subs < 0) subEmittersUnreadable++;
				else if (subs > 0) withSubEmitters++;
			}
			report.AppendLine("  ParticleSystem=" + systems.Length
				+ "  with maxParticles > 1000=" + overBudget
				+ "  with sub-emitters=" + withSubEmitters
				+ "  sub-emitter module unreadable=" + subEmittersUnreadable
				+ "  sum of maxParticles=" + maxParticleTotal);
			report.AppendLine("  maxParticles is a CEILING, not the live count; the live count only shows in play mode,");
			report.AppendLine("  so treat the sum as the worst case the scene can reach, not what it costs right now.");
			if (systems.Length == 0) report.AppendLine("  none");
			levers.Add(new Lever
			{
				Name = "particle systems",
				Count = systems.Length,
				Estimate = false,
				Reduction = "disable the emitters on Quest (QuestSwitch, or a UdonSharp script) rather than trying to"
					+ " tune them on both platforms",
			});
			levers.Add(new Lever
			{
				Name = "particle systems over the 1000 maxParticles budget",
				Count = overBudget,
				Estimate = false,
				Reduction = "lower maxParticles to what the effect actually emits; the ceiling is what VRChat"
					+ " measures, so an emitter that never reaches it still costs as if it did",
			});
		}

		/// -1 when the module could not be read, which is not the same answer as
		/// "this emitter has no sub-emitters".
		///
		/// The module is reached by REFLECTION because the accessor has been
		/// renamed across Unity versions (subEmitters / SubEmittersModule), and a
		/// compile against the wrong one would take the whole plugin down rather
		/// than skip this one line.
		private static int CountSubEmitters(ParticleSystem system)
		{
			try
			{
				PropertyInfo accessor = typeof(ParticleSystem).GetProperty("subEmitters");
				if (accessor == null) return -1;
				object module = accessor.GetValue(system, null);
				if (module == null) return -1;
				FieldInfo list = module.GetType().GetField("m_SubEmitters",
					BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
				Array subs = list == null ? null : list.GetValue(module) as Array;
				return subs == null ? 0 : subs.Length;
			}
			catch (Exception)
			{
				return -1;
			}
		}

		private static void AppendTextures(StringBuilder report, List<Lever> levers)
		{
			report.AppendLine("--- textures [measured counts, estimated VRAM cost] ---");
			// The scene's own textures, deduplicated by instance: a material
			// shared by ten renderers is one upload, and a loaded-but-unused
			// asset is not in the world at all.
			Texture[] textures = SceneTextures();
			long texels = 0;
			var oversized = new List<string>();
			int androidOverrides = 0;
			int androidUnknown = 0;
			foreach (Texture texture in textures)
			{
				if (texture == null) continue;
				texels += (long)texture.width * texture.height;
				int largest = Math.Max(texture.width, texture.height);
				if (largest > 2048) oversized.Add(texture.name + " " + texture.width + "x" + texture.height
					+ " [" + PathOf(texture) + "]");
				if (HasAndroidOverride(texture)) androidOverrides++;
				else if (string.IsNullOrEmpty(PathOf(texture))) androidUnknown++;
			}
			report.AppendLine("  unique textures=" + textures.Length + "  total texels=" + texels);
			report.AppendLine("  max size over 2048=" + oversized.Count
				+ "  with an Android platform override=" + androidOverrides
				+ "  with no asset path to override=" + androidUnknown);
			foreach (string entry in oversized.Take(20)) report.AppendLine("    " + entry);
			if (oversized.Count > 20) report.AppendLine("    ... " + (oversized.Count - 20) + " more");
			report.AppendLine("  texel count is an ESTIMATE of memory: it ignores compression, mipmaps and format.");
			report.AppendLine("  (textures referenced by the scene's renderers, deduplicated; a texture used only");
			report.AppendLine("  by an unloaded prefab is not here, and VRChat checks every one of them at build.)");
			levers.Add(new Lever
			{
				Name = "textures over 2048",
				Count = oversized.Count,
				Estimate = true,
				Reduction = "downscale to 2048 and set an Android platform override, so the small build does not"
					+ " ship the full-size one",
			});
			levers.Add(new Lever
			{
				Name = "texture texels",
				Count = texels,
				Estimate = true,
				Reduction = "cut texture resolution before cutting texture count; this is a stand-in for VRAM, not"
					+ " a measurement of it",
			});
		}

		private static bool HasAndroidOverride(Texture texture)
		{
			string path = AssetPathOf(texture);
			if (string.IsNullOrEmpty(path)) return false;
			try
			{
				if (!(AssetImporter.GetAtPath(path) is TextureImporter importer)) return false;
				// Read the override's VALUE rather than asking whether it exists:
				// a platform with no override silently reports the default
				// settings, and "has an override" is the question being asked.
				TextureImporterPlatformSettings settings = importer.GetPlatformTextureSettings("Android");
				return settings.overridden;
			}
			catch (Exception)
			{
				return false;
			}
		}

		private static void AppendAudio(StringBuilder report, List<Lever> levers)
		{
			report.AppendLine("--- audio [measured counts] ---");
			AudioSource[] sources = UnityEngine.Object.FindObjectsOfType<AudioSource>(true);
			AudioClip[] clips = UnityEngine.Object.FindObjectsOfType<AudioClip>();
			double totalSeconds = 0;
			int streaming = 0;
			int decompressOnLoad = 0;
			int unknownLoadType = 0;
			var longLoaders = new List<string>();
			foreach (AudioClip clip in clips)
			{
				if (clip == null) continue;
				totalSeconds += clip.length;
				switch (ClipLoadType(clip))
				{
					case "Streaming": streaming++; break;
					case "DecompressOnLoad": decompressOnLoad++; break;
					default: unknownLoadType++; break;
				}
				if (clip.length > 60 && ClipLoadType(clip) == "DecompressOnLoad")
				{
					longLoaders.Add(clip.name + " " + clip.length.ToString("F1") + "s [" + PathOf(clip) + "]");
				}
			}
			report.AppendLine("  AudioSource=" + sources.Length + "  AudioClip=" + clips.Length
				+ "  total clip duration=" + totalSeconds.ToString("F1") + "s");
			report.AppendLine("  loadType: Streaming=" + streaming + "  DecompressOnLoad=" + decompressOnLoad
				+ "  unknown=" + unknownLoadType);
			report.AppendLine("  Streaming is the Quest-safe choice: a DecompressOnLoad clip is held whole in memory.");
			foreach (string entry in longLoaders.Take(20)) report.AppendLine("    long DecompressOnLoad: " + entry);
			if (longLoaders.Count > 20) report.AppendLine("    ... " + (longLoaders.Count - 20) + " more");
			report.AppendLine("  Only LOADED clips are counted, so an unloaded prefab's audio is not here.");
			levers.Add(new Lever
			{
				Name = "audio sources",
				Count = sources.Length,
				Estimate = false,
				Reduction = "disable the sources that are not needed on Quest; an enabled-but-silent source is not"
					+ " free, and a music source that is still playing is",
			});
			levers.Add(new Lever
			{
				Name = "long DecompressOnLoad clips",
				Count = longLoaders.Count,
				Estimate = false,
				Reduction = "set long clips to Streaming in the importer; the 60s cut-off is this report's judgement,"
					+ " not VRChat's, and the memory cost is set by the sample rate rather than the duration",
			});
		}

		private static string ClipLoadType(AudioClip clip)
		{
			string path = AssetPathOf(clip);
			if (!string.IsNullOrEmpty(path))
			{
				try
				{
					// The importer is the setting that is actually saved; AudioClip
					// carries it too, but only for a clip loaded from an asset.
					if (AssetImporter.GetAtPath(path) is AudioImporter importer)
					{
						return AudioSampleSetting(importer);
					}
				}
				catch (Exception)
				{
					// Fall through to the clip's own copy below.
				}
			}
			try
			{
				return clip.loadType.ToString();
			}
			catch (Exception)
			{
				return "<unknown>";
			}
		}

		/// The importer's own sample setting, read by NAME because the property
		/// that carries it has been spelled differently across Unity versions and
		/// a reflection miss costs this one line rather than the whole report.
		private static string AudioSampleSetting(AudioImporter importer)
		{
			try
			{
				// Both spellings: Unity renamed the property in 2021.2, so a
				// lookup for one of them alone reports "<unknown>" on every
				// version that uses the other.
				Type type = importer.GetType();
				PropertyInfo property = type.GetProperty("defaultSampleSettings")
					?? type.GetProperty("defaultSampleSetting");
				if (property != null && property.CanRead)
				{
					object settings = property.GetValue(importer, null);
					if (settings != null)
					{
						object loadType = settings.GetType().GetField("loadType",
							BindingFlags.Public | BindingFlags.Instance);
						if (loadType is FieldInfo loadTypeField) return loadTypeField.GetValue(settings).ToString();
					}
				}
			}
			catch (Exception)
			{
				// Fall through to "unknown": the clip's own copy is the next line.
			}
			return "<unknown>";
		}

		/// True when the scene carries a baked LightingData asset.
		private static bool AppendLightmaps(StringBuilder report, List<Lever> levers)
		{
			report.AppendLine("--- lightmaps and GI [measured] ---");
			// These live on LightmapSettings, not Lightmapping: Lightmapping is the
			// cooking API and has never carried the mode or the baked data.
			// Reflected rather than referenced because the member was renamed
			// across Unity versions (lightmapsMode / lightmapsModeLegacy), and a
			// direct reference to a member one Unity version lacks is a compile
			// error in every project this plugin has to drop into.
			if (TryReadStaticMember(typeof(LightmapSettings), "lightmapsMode", out object mode))
			{
				report.AppendLine("  lightmapsMode=" + mode);
			}
			else if (TryReadStaticMember(typeof(LightmapSettings), "lightmapsModeLegacy", out object legacyMode))
			{
				report.AppendLine("  lightmapsMode: not available: no LightmapSettings.lightmapsMode in this Unity version");
				report.AppendLine("  lightmapsModeLegacy=" + legacyMode);
			}
			else
			{
				report.AppendLine("  lightmapsMode: not available: LightmapSettings exposes neither lightmapsMode"
					+ " nor lightmapsModeLegacy in this Unity version");
			}

			bool bakedLighting = false;
			UnityEngine.Object lightingData = BakedLightingData(out string lightingReason);
			if (lightingData != null)
			{
				bakedLighting = true;
				report.AppendLine("  baked lighting data: present [" + PathOf(lightingData) + "]");
			}
			else
			{
				report.AppendLine("  baked lighting data: not available: " + lightingReason);
			}

			int lightmapCount = 0;
			if (TryReadStaticMember(typeof(LightmapSettings), "lightmaps", out object lightmaps)
				&& lightmaps is Array entries)
			{
				lightmapCount = entries.Length;
				report.AppendLine("  lightmap entries=" + lightmapCount
					+ "  (an entry with no data in it is a placeholder, not a baked map)");
			}
			else
			{
				report.AppendLine("  lightmap entries: not available: LightmapSettings.lightmaps did not read");
			}

			report.AppendLine("  ambient mode=" + RenderSettings.ambientMode
				+ "  intensity=" + RenderSettings.ambientIntensity.ToString("F2"));
			if (TryReadStaticMember(typeof(RenderSettings), "ambientSource", out object source))
			{
				report.AppendLine("  ambient source=" + source);
			}
			else
			{
				report.AppendLine("  ambient source: not available: this Unity version has no"
					+ " RenderSettings.ambientSource (it is 2021.2 and newer)");
			}

			bool fog = RenderSettings.fog;
			report.AppendLine("  fog=" + (fog ? "on" : "off") + "  (a world with fog on pays for it every frame)");
			levers.Add(new Lever
			{
				Name = "lightmap entries in the scene",
				Count = lightmapCount,
				Estimate = false,
				Reduction = "bake the static lighting; VRChat bakes its own at build time from this scene either way,"
					+ " so unbaked static lights are paid for twice",
			});
			return bakedLighting;
		}

		private static void AppendColliders(StringBuilder report, List<Lever> levers)
		{
			report.AppendLine("--- colliders [measured] ---");
			Collider[] colliders = UnityEngine.Object.FindObjectsOfType<Collider>(true);
			var byType = new Dictionary<string, int>(StringComparer.Ordinal);
			foreach (Collider collider in colliders)
			{
				string type = collider.GetType().Name;
				byType.TryGetValue(type, out int seen);
				byType[type] = seen + 1;
			}
			report.AppendLine("  total=" + colliders.Length + "  by type: "
				+ (byType.Count == 0 ? "none" : FormatCounts(byType)));
			report.AppendLine("  A collider that is never touched can be a trigger or removed entirely; a mesh collider");
			report.AppendLine("  costs more than a primitive and more again on Android.");
			levers.Add(new Lever
			{
				Name = "colliders",
				Count = colliders.Length,
				Estimate = false,
				Reduction = "remove colliders from objects nothing collides with, or make them triggers",
			});
		}

		private static void AppendLevers(StringBuilder report, List<Lever> levers)
		{
			report.AppendLine("--- biggest levers ---");
			report.AppendLine("The five largest measured items above, each with the reduction that actually moves them.");
			report.AppendLine("The marked ones are ESTIMATES: the count is a stand-in for cost, not the cost itself.");
			foreach (Lever lever in levers
				.OrderByDescending(l => l.Count)
				.ThenBy(l => l.Name, StringComparer.Ordinal)
				.Take(LeverCount))
			{
				report.AppendLine("  " + lever.Name + "=" + lever.Count
					+ (lever.Estimate ? "  [ESTIMATE of cost]" : "  [measured count]"));
				report.AppendLine("    -> " + lever.Reduction);
			}
			if (levers.Count < LeverCount) report.AppendLine("  fewer than " + LeverCount + " items were measured.");
			report.AppendLine("Reminder: this is not the Performance rank. VRChat computes that at build time from the");
			report.AppendLine("built world on the target device, and the only honest reading of the real rating is the");
			report.AppendLine("VRChat SDK's build window or a real upload to a test world.");
		}

		// ==================================================== 3. environment

		/// How the world LOOKS, read out of the scene as it is now.
		public static string DescribeWorldEnvironment()
		{
			var report = new StringBuilder();
			report.AppendLine("--- world environment ---");
			AppendSkybox(report);
			AppendFog(report);
			int directional = AppendDirectionalLights(report);
			CameraPostState cameraPostProcessing = AppendReferenceCamera(report);
			UnityEngine.Object lightingData = BakedLightingData(out string bakedReason);
			AppendBakedLighting(report, lightingData, bakedReason);
			AppendPostProcessing(report);
			report.AppendLine("--- what this scene does NOT configure ---");
			report.AppendLine(RenderingGaps(directional, cameraPostProcessing, lightingData != null));
			return report.ToString();
		}

		private static void AppendBakedLighting(StringBuilder report, UnityEngine.Object data, string unavailable)
		{
			report.AppendLine("--- baked lighting [measured] ---");
			report.AppendLine(data == null
				? "  lighting data: not available: " + unavailable
				: "  lighting data: " + data.name + " [" + PathOf(data) + "]");
		}

		/// The LightingData asset the scene carries, or null with the reason why
		/// there is none. Read reflectively: the member has moved between Unity
		/// versions and this plugin has to compile in all of them.
		private static UnityEngine.Object BakedLightingData(out string unavailable)
		{
			unavailable = null;
			if (!TryReadStaticMember(typeof(LightmapSettings), "lightingDataAsset", out object data)
				&& !TryReadStaticMember(TypeOfAny("UnityEditor.Lightmapping", "UnityEngine.Lightmapping"),
					"lightingDataAsset", out data))
			{
				unavailable = "this Unity version exposes no readable lightingDataAsset";
				return null;
			}
			if (data == null)
			{
				unavailable = "no LightingData asset is assigned, so the scene carries no baked lighting";
				return null;
			}
			return data as UnityEngine.Object;
		}

		private static void AppendSkybox(StringBuilder report)
		{
			report.AppendLine("--- skybox and ambient [measured] ---");
			Material skybox = RenderSettings.skybox;
			if (skybox == null)
			{
				report.AppendLine("  skybox: (none) -- the world has no sky material, so its background comes from"
					+ " whatever the build defaults to, not from this scene.");
			}
			else
			{
				report.AppendLine("  skybox: " + skybox.name + "  shader="
					+ (skybox.shader == null ? "<none>" : skybox.shader.name) + "  [" + PathOf(skybox) + "]");
			}
			report.AppendLine("  ambient mode=" + RenderSettings.ambientMode
				+ "  intensity=" + RenderSettings.ambientIntensity.ToString("F2"));
			if (TryReadStaticMember(typeof(RenderSettings), "ambientSource", out object source))
			{
				report.AppendLine("  ambient source=" + source);
			}
			else
			{
				// ambientSource was removed in 2021.2; ambientSkyboxAmount took over
				// the sky contribution, so reading that is the same fact, not a gap.
				if (TryReadStaticMember(typeof(RenderSettings), "ambientSkyboxAmount", out object amount))
				{
					report.AppendLine("  ambient source=skybox, amount="
						+ Convert.ToSingle(amount).ToString("F2")
						+ "  (RenderSettings.ambientSource was removed in Unity 2021.2;");
					report.AppendLine("    ambientSkyboxAmount is what replaced it)");
				}
				else
				{
					report.AppendLine("  ambient source: not available: this Unity version has no"
						+ " RenderSettings.ambientSource and no ambientSkyboxAmount");
				}
			}
		}

		private static void AppendFog(StringBuilder report)
		{
			report.AppendLine("--- fog [measured] ---");
			if (!RenderSettings.fog)
			{
				report.AppendLine("  fog: off");
				return;
			}
			report.AppendLine("  fog: on  mode=" + RenderSettings.fogMode
				+ "  colour=" + FormatValue(RenderSettings.fogColor)
				+ "  start=" + RenderSettings.fogStartDistance.ToString("F1")
				+ "  end=" + RenderSettings.fogEndDistance.ToString("F1"));
		}

		private static int AppendDirectionalLights(StringBuilder report)
		{
			report.AppendLine("--- directional lights [measured] ---");
			Light[] lights = UnityEngine.Object.FindObjectsOfType<Light>(true);
			int count = 0;
			foreach (Light light in lights)
			{
				if (light.type != LightType.Directional) continue;
				count++;
				Vector3 angles = light.transform.eulerAngles;
				report.AppendLine("  " + light.name
					+ "  rotation=(" + angles.x.ToString("F1") + ", " + angles.y.ToString("F1") + ", "
					+ angles.z.ToString("F1") + ")"
					+ "  colour=" + FormatValue(light.color)
					+ "  intensity=" + light.intensity.ToString("F2")
					+ "  shadows=" + light.shadows
					+ "  path=" + PathOf(light.transform));
			}
			if (count == 0)
			{
				report.AppendLine("  none. With no directional light the world has no sun, and everything visible is"
					+ " ambient, baked, or realtime point and spot lights.");
			}
			else if (count > 1)
			{
				report.AppendLine("  " + count + " directional lights: VRChat expects one. They add up rather than"
					+ " choosing between each other.");
			}
			return count;
		}

		/// True when the descriptor's reference camera carries a post process layer.
		/// What the reference camera says about post-processing. Unknown is a
		/// third answer: with no descriptor or no camera there is nothing to check,
		/// which is not the same finding as a camera with no layer on it.
		private enum CameraPostState
		{
			Unknown,
			Present,
			Absent,
		}

		private static CameraPostState AppendReferenceCamera(StringBuilder report)
		{
			report.AppendLine("--- reference camera [measured] ---");
			Component descriptor = PiUdon.SceneDescriptor();
			if (descriptor == null)
			{
				report.AppendLine("  not available: no scene descriptor in the open scene, so there is no"
					+ " ReferenceCamera to read.");
				return CameraPostState.Unknown;
			}
			object reference = PiWorldToolkit.ReadFieldByName(descriptor, "ReferenceCamera");
			Camera camera = null;
			if (reference is GameObject gameObject) camera = gameObject.GetComponent<Camera>();
			else if (reference is Component component) camera = component.GetComponent<Camera>();
			if (camera == null)
			{
				report.AppendLine("  ReferenceCamera: "
					+ (reference == null
						? "not set on the descriptor, so VRChat picks the camera itself"
						: "not available: the field holds a " + reference.GetType().Name + " with no Camera on it"));
				return CameraPostState.Unknown;
			}
			report.AppendLine("  " + camera.name + "  tagged MainCamera=" + camera.CompareTag("MainCamera")
				+ "  path=" + PathOf(camera.transform));
			report.AppendLine("  near clip=" + camera.nearClipPlane.ToString("F3")
				+ "  far clip=" + camera.farClipPlane.ToString("F0")
				+ "  fov=" + camera.fieldOfView.ToString("F1")
				+ "  culling mask layers=" + BitCount(camera.cullingMask)
				+ "  (everything excluded from the mask is not rendered at all)");
			Type layerType = VrcResolve.Type(PostProcessLayerType);
			if (layerType == null)
			{
				report.AppendLine("  PostProcessLayer: not available: " + PostProcessLayerType + " is not loaded"
					+ " (com.unity.postprocessing is not installed)");
				return CameraPostState.Unknown;
			}
			bool has = camera.GetComponent(layerType) != null;
			report.AppendLine("  PostProcessLayer on this camera=" + has);
			if (!has)
			{
				report.AppendLine("  VRChat renders post-processing from the reference camera's layer only, so a"
					+ " volume in the scene does nothing without one.");
			}
			return has ? CameraPostState.Present : CameraPostState.Absent;
		}

		private static void AppendPostProcessing(StringBuilder report)
		{
			report.AppendLine("--- post-processing and AudioLink [measured] ---");
			Type volumeType = VrcResolve.Type(VolumeType);
			if (volumeType == null)
			{
				report.AppendLine("  volumes: not available: " + VolumeType + " is not loaded"
					+ " (com.unity.postprocessing is not installed)");
			}
			else
			{
				var volumes = new List<Component>();
				var profiles = new HashSet<string>(StringComparer.Ordinal);
				foreach (Transform child in UnityEngine.Object.FindObjectsOfType<Transform>(true))
				{
					foreach (Component component in child.GetComponents(volumeType))
					{
						volumes.Add(component);
						// A volume with no shared profile contributes nothing at all,
						// so an empty one is not a working post-processing setup.
						if (TryReadMember(component, "sharedProfile", out object profile)
							&& profile is UnityEngine.Object profileAsset)
						{
							profiles.Add(profile.GetType().Name + " " + profileAsset.name
								+ " [" + PathOf(profileAsset) + "]");
						}
					}
				}
				report.AppendLine("  Volume components=" + volumes.Count);
				foreach (Component volume in volumes)
				{
					report.AppendLine("    " + PathOf(volume.transform)
						+ "  profile=" + (TryReadMember(volume, "sharedProfile", out object own) && own != null
							? own.ToString() : "(none -- this volume does nothing)"));
				}
				report.AppendLine("  distinct VolumeProfiles=" + profiles.Count
					+ "  (of type " + VolumeProfileType + ")");
				foreach (string profileName in profiles.OrderBy(p => p, StringComparer.Ordinal))
				{
					report.AppendLine("    " + profileName);
				}
			}

			var audioLink = new List<string>();
			foreach (Transform child in UnityEngine.Object.FindObjectsOfType<Transform>(true))
			{
				foreach (Component component in child.GetComponents<Component>())
				{
					if (component == null) continue;
					string name = component.GetType().Name;
					if (!name.StartsWith("AudioReactive", StringComparison.Ordinal)) continue;
					audioLink.Add(name + "  " + PathOf(child));
				}
			}
			report.AppendLine("  AudioReactive components (AudioLink)=" + audioLink.Count);
			foreach (string entry in audioLink.Take(20)) report.AppendLine("    " + entry);
			if (audioLink.Count > 20) report.AppendLine("    ... " + (audioLink.Count - 20) + " more");
			if (audioLink.Count == 0)
			{
				report.AppendLine("    none. Without AudioLink nothing in the world reacts to music, so a world that"
					+ " expects audio reactivity is inert in-game.");
			}
		}

		/// The conclusion the report exists to make: what the player will see that
		/// this scene does not set up.
		private static string RenderingGaps(int directionalLights, CameraPostState cameraPostProcessing, bool bakedLighting)
		{
			var lines = new List<string>();
			if (directionalLights == 0)
			{
				lines.Add("No directional light, so the world has no sun: what you see in the Scene view is"
					+ " ambient and whatever point and spot lights are in the scene.");
			}
			if (cameraPostProcessing == CameraPostState.Absent)
			{
				lines.Add("No post process layer on the reference camera, so every post-processing volume in the"
					+ " scene is ignored in-game and the image is exactly the Scene view's colour.");
			}
			else if (cameraPostProcessing == CameraPostState.Unknown)
			{
				lines.Add("Whether post-processing runs could not be checked (see the reference camera section);"
					+ " VRChat renders post-processing from the reference camera's layer, and nothing else.");
			}
			if (RenderSettings.skybox == null)
			{
				lines.Add("No skybox material, so the world's background is the build default rather than anything"
					+ " chosen here.");
			}
			if (!bakedLighting)
			{
				lines.Add("No baked lighting data in the scene, so nothing here is baked: VRChat will bake at build"
					+ " time from whatever it finds, which is not what the Scene view shows.");
			}
			if (!RenderSettings.fog)
			{
				lines.Add("Fog is off, so the world renders with no distance falloff -- VRChat applies the world's own"
					+ " fog settings at build time and this scene does not configure any.");
			}
			if (lines.Count == 0)
			{
				lines.Add("Nothing above is missing: the scene sets a skybox, a directional light, fog and a"
					+ " post-processing camera, so what the Scene view shows is close to what VRChat will render.");
				lines.Add("VRChat still overrides lighting, ambient and the player camera at build time, so this is a"
					+ " comparison point and not a promise.");
			}
			return string.Join("\n", lines.ToArray());
		}

		// ================================================ shared reflection

		private static bool TryReadMember(object instance, string memberName, out object value)
		{
			value = null;
			if (instance == null) return false;
			try
			{
				Type type = instance.GetType();
				while (type != null)
				{
					FieldInfo field = type.GetField(memberName,
						BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
					if (field != null)
					{
						value = field.GetValue(instance);
						return true;
					}
					PropertyInfo property = type.GetProperty(memberName,
						BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
					if (property != null && property.CanRead && property.GetIndexParameters().Length == 0)
					{
						value = property.GetValue(instance, null);
						return true;
					}
					type = type.BaseType;
				}
			}
			catch (Exception error)
			{
				Debug.LogWarning("[pi] " + memberName + ": " + error.Message);
				value = null;
			}
			return false;
		}

		/// The first of several candidate type names that is actually loaded, or
		/// null. A type that moved between Unity versions resolves under one name
		/// or the other, and TryReadStaticMember already treats null as unreadable.
		private static Type TypeOfAny(params string[] typeNames)
		{
			foreach (string typeName in typeNames)
			{
				Type type = VrcResolve.Type(typeName);
				if (type != null) return type;
			}
			return null;
		}

		private static bool TryReadStaticMember(Type type, string memberName, out object value)
		{
			value = null;
			if (type == null) return false;
			try
			{
				FieldInfo field = type.GetField(memberName, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static);
				if (field != null)
				{
					value = field.GetValue(null);
					return true;
				}
				PropertyInfo property = type.GetProperty(memberName, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static);
				if (property != null && property.CanRead && property.GetIndexParameters().Length == 0)
				{
					value = property.GetValue(null, null);
					return true;
				}
			}
			catch (Exception error)
			{
				Debug.LogWarning("[pi] " + type.Name + "." + memberName + ": " + error.Message);
				value = null;
			}
			return false;
		}

		private static void AppendMember(StringBuilder report, string indent, object instance, string memberName, string suffix)
		{
			if (TryReadMember(instance, memberName, out object value))
			{
				report.AppendLine(indent + memberName + ": " + FormatValue(value) + suffix);
				return;
			}
			report.AppendLine(indent + memberName + ": not available: no readable field or property \"" + memberName
				+ "\" on " + (instance == null ? "<no object>" : instance.GetType().FullName));
		}

		private static string FormatValue(object value)
		{
			if (value == null) return "(null)";
			if (value is Color color) return "#" + ColorUtility.ToHtmlStringRGB(color);
			return value.ToString();
		}

		private static string FormatCounts(Dictionary<string, int> counts)
		{
			if (counts.Count == 0) return "none";
			return string.Join(", ", counts
				.OrderByDescending(p => p.Value)
				.ThenBy(p => p.Key, StringComparer.Ordinal)
				.Select(p => p.Key + "=" + p.Value)
				.ToArray());
		}

		/// An Assets-relative path, for anything, or "(scene instance)".
		///
		/// VrcResolve.AssetPathOf takes a Component; the census also asks about
		/// materials, textures, clips and skyboxes, which are not components.
		private static string PathOf(UnityEngine.Object target)
		{
			if (target == null) return "(none)";
			string path = AssetPathOf(target);
			return path.Length == 0 ? "(scene instance)" : path;
		}

		/// The Assets path of an asset, or "" for anything that is not one. An
		/// importer lookup on a path that is not an asset logs an error in the
		/// Console, which is a worse answer than an empty string.
		private static string AssetPathOf(UnityEngine.Object target)
		{
			if (target == null) return "";
			try
			{
				string path = AssetDatabase.GetAssetPath(target);
				return path ?? "";
			}
			catch (Exception)
			{
				return "";
			}
		}

		/// A stable path from the scene root, so two objects with the same name
		/// are still distinguishable in the report.
		private static string PathOf(Transform transform)
		{
			if (transform == null) return "(no object)";
			try
			{
				string path = AnimationUtility.CalculateTransformPath(transform, transform.root);
				return path.Length == 0 ? transform.name : path;
			}
			catch (Exception)
			{
				return transform.name;
			}
		}

		private static string ScriptNameFromPath(string path)
		{
			if (string.IsNullOrEmpty(path) || path.StartsWith("(", StringComparison.Ordinal)) return "(unknown)";
			int assets = path.IndexOf("Assets/", StringComparison.Ordinal);
			string relative = assets >= 0 ? path.Substring(assets + "Assets/".Length) : path;
			int slash = relative.LastIndexOf('/');
			if (slash >= 0) relative = relative.Substring(slash + 1);
			int dot = relative.LastIndexOf('.');
			if (dot > 0) relative = relative.Substring(0, dot);
			return relative.Length == 0 ? path : relative;
		}

		private static int BitCount(int value)
		{
			int count = 0;
			for (int bit = 0; bit < 32; bit++)
			{
				if ((value & (1 << bit)) != 0) count++;
			}
			return count;
		}
	}
}
