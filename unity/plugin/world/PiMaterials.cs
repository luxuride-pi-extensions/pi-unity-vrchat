// Part of the pi Unity bridge plugin. See PiUnityBridge.Core.cs for the
// protocol overview. The whole plugin is one assembly of partial classes, so
// Unity compiles it into Assembly-CSharp-Editor with no asmdef and no
// package dependencies, which is what lets it drop into any Unity 2021+
// project (a configured VRChat world, an existing game) that pi was pointed at
// rather than generated.
//
// Per-material reporting, and the Android shader swap.
//
// WHAT THIS ADDS ON TOP OF THE OTHER TWO REPORTS
// ---------------------------------------------
// PiBuildChecks and PiCensus answer "is this world safe to upload" and "what
// does this world cost". Neither answers "what does THIS material look like and
// what will happen to it if I swap it", which is the question an author asks
// once a material has already been named as an offender. Everything printed
// below is read off the live material at call time; the one thing that cannot be
// measured from here -- how the swapped material will LOOK -- is left to the
// person who does the swap.
//
// WHY THE WHITELIST IS READ BY REFLECTION
// ---------------------------------------
// The Android whitelist lives in a package this plugin does not reference
// (VRWorldToolkit, falling back to VRC.SDKBase) and is internal in some versions
// and public in others. A compile-time reference would break the plugin in
// every project without those packages, which is the one property the
// no-assembly-definition design exists to preserve.
//
// WHY A SHADER IS REPORTED WITHOUT AN ASSEMBLY NAME
// -------------------------------------------------
// A Shader is a UnityEngine.Object, not a System.Type: it compiles to GPU code
// and has neither a FullName nor an assembly, so its asset path (or the fact
// that it has none) is the honest stand-in for that information.
//
// WHY PROPERTY EXISTENCE IS PROBED BY READING
// -------------------------------------------
// ShaderUtil.GetPropertyCount / GetPropertyName is editor-only and is not
// guaranteed to be callable from every context this plugin runs in. The scratch
// material below carries the target shader, and HasProperty plus a guarded read
// answer the same question without it.

using System;
using System.Collections;
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
	public static class PiMaterials
	{
		private const string ToolkitValidationType = "VRWorldToolkit.Editor.Validation";
		private const string SdkValidationType = "VRC.SDKBase.Validation.WorldValidation";
		private const string ToolkitWhiteListField = "WorldShaderWhiteList";
		private const string SdkWhiteListField = "ShaderWhiteList";

		public const string DefaultTargetShader = "VRChat/Mobile/Standard Lite";
		private const string MobilePrefix = "VRChat/Mobile/";
		private const string QuestFix = "swap to VRChat/Mobile/Standard Lite (or Standard for PC-only)";
		private const int MaxListLines = 20;

		// =========================================================== describe

		/// Every material the SCENE's renderers use, with its shader, its Quest
		/// verdict, its textures and its colours.
		public static string DescribeMaterials()
		{
			var report = new StringBuilder();
			Scene scene = SceneManager.GetActiveScene();
			var checks = new Checklist();
			report.AppendLine("--- materials the scene's renderers use ---");
			report.AppendLine("scene=" + (scene.IsValid() ? scene.name : "<none open>"));

			HashSet<string> allowed;
			string whitelistSource;
			string whitelistReason;
			bool haveWhitelist = TryReadShaderWhiteList(out allowed, out whitelistSource, out whitelistReason);
			if (haveWhitelist)
			{
				report.AppendLine("Android shader whitelist from " + whitelistSource + " (" + allowed.Count + " entries)");
			}
			else
			{
				checks.Skip("quest verdicts", whitelistReason);
				report.AppendLine("Android shader whitelist: not available: " + whitelistReason);
			}

			List<Material> materials = SceneMaterials();
			report.AppendLine("materials: " + materials.Count);
			if (materials.Count == 0)
			{
				report.AppendLine("  no material is referenced by any renderer in the open scene, so there is");
				report.AppendLine("  nothing to report. (Renderers are counted with inactive objects included.)");
				report.Append(checks.Render());
				report.Append(checks.Verdict(0,
					"no material could be judged -- see the not-available lines",
					"every judged material is whitelisted for Android",
					"the offenders are named above, on a shader VRChat will not build for Android"));
				return report.ToString();
			}

			int onWhitelist = 0;
			int offWhitelist = 0;
			int notJudged = 0;
			int withShader = 0;
			for (int index = 0; index < materials.Count; index++)
			{
				Material material = materials[index];
				string label = "material " + (index + 1) + " of " + materials.Count + ": " + material.name;
				report.AppendLine();
				report.AppendLine("[" + (index + 1) + "] " + material.name);
				report.AppendLine("  path: " + PathOf(material));

				Shader shader = material.shader;
				if (shader == null)
				{
					checks.Skip(label, "the material has NO shader (it renders magenta), so there is no shader name "
						+ "to judge against the whitelist");
					report.AppendLine("  shader: <none assigned -- this material renders magenta>");
					report.AppendLine("  textures: not available: no shader, so the material has no texture properties");
					notJudged++;
					continue;
				}
				withShader++;
				report.AppendLine("  shader: " + shader.name);
				report.AppendLine("  shader asset: " + PathOf(shader));
				if (!shader.isSupported) report.AppendLine("  shader.isSupported=false on this platform");

				if (!haveWhitelist)
				{
					checks.Skip(label, "no Android shader whitelist resolved, so this shader could not be judged");
					report.AppendLine("  quest verdict: not available: no Android shader whitelist resolved");
					notJudged++;
				}
				else if (allowed.Contains(shader.name))
				{
					checks.Pass(label, shader.name + " is on the whitelist from " + whitelistSource);
					report.AppendLine("  quest verdict: whitelisted for Android (" + whitelistSource + ")");
					onWhitelist++;
				}
				else
				{
					checks.Fail(label, shader.name + " is NOT on the Android whitelist",
						QuestFix + " -- or keep this object PC-only if it cannot be swapped");
					report.AppendLine("  quest verdict: NOT whitelisted for Android (" + whitelistSource + ")");
					report.AppendLine("    fix: " + QuestFix + " -- or keep this object PC-only if it cannot be swapped");
					offWhitelist++;
				}

				AppendTextures(report, material, shader.name);
				AppendColours(report, material);
			}

			report.AppendLine();
			report.AppendLine("summary: total materials=" + materials.Count + "  with a shader=" + withShader
				+ "  on a whitelisted shader=" + onWhitelist + "  off a whitelisted shader=" + offWhitelist
				+ "  not judged=" + notJudged);
			report.AppendLine("Every number above is a count of what was read. " + notJudged
				+ " of these materials could not be judged against a whitelist, and that is not the same as "
				+ offWhitelist + " being offenders.");
			report.AppendLine("Materials in a prefab that is not loaded in this scene are not in this list at all,");
			report.AppendLine("and VRChat checks every one of them at build time.");
			report.Append(checks.Render());
			report.Append(checks.Verdict(onWhitelist + offWhitelist,
				"no material could be judged -- see the not-available lines",
				"every judged material is whitelisted for Android",
				"the offenders are named above, on a shader VRChat will not build for Android"));
			return report.ToString();
		}

		private static void AppendTextures(StringBuilder report, Material material, string shaderName)
		{
			string[] slots;
			try
			{
				slots = material.GetTexturePropertyNames();
			}
			catch (Exception error)
			{
				report.AppendLine("  textures: not available: Material.GetTexturePropertyNames threw ("
					+ error.Message + ")");
				return;
			}
			Array.Sort(slots, StringComparer.Ordinal);
			if (slots.Length == 0)
			{
				report.AppendLine("  textures: the shader " + shaderName + " declares no texture property at all");
				return;
			}
			report.AppendLine("  textures (slots declared by " + shaderName + "): " + slots.Length);
			foreach (string slot in slots)
			{
				Texture texture;
				try
				{
					texture = material.GetTexture(slot);
				}
				catch (Exception error)
				{
					report.AppendLine("    " + slot + ": not available: GetTexture threw (" + error.Message + ")");
					continue;
				}
				// An EMPTY texture slot is the finding, not a formatting detail:
				// a Standard-family material with no _MainTex draws black on
				// Quest while drawing lit grey on PC, and the Inspector shows it
				// as an ordinary white swatch.
				if (texture == null)
				{
					report.AppendLine("    " + slot + ": <no texture assigned>");
					continue;
				}
				report.AppendLine("    " + slot + ": " + texture.name + "  " + texture.width + "x" + texture.height
					+ "  (" + texture.GetType().Name + ")");
			}
		}

		private static void AppendColours(StringBuilder report, Material material)
		{
			var line = new StringBuilder();
			foreach (string name in new[] { "_Color", "_BaseColor" })
			{
				if (!HasProperty(material, name)) continue;
				if (!TryReadColor(material, name, out Color value))
				{
					if (line.Length > 0) line.Append("  ");
					line.Append(name).Append(": not available: GetColor threw");
					continue;
				}
				if (line.Length > 0) line.Append("  ");
				line.Append(name).Append('=').Append(FormatColor(value));
			}
			if (line.Length == 0)
			{
				report.AppendLine("  colours: none -- this material has neither _Color nor _BaseColor, so its colour");
				report.AppendLine("    comes from somewhere else (a texture, a vertex colour or a shader keyword)");
				return;
			}
			report.AppendLine("  colours: " + line);
		}

		// =============================================================== swap

		/// Puts one scene material onto an Android-safe shader, carrying the
		/// values the old shader had across to the new one.
		///
		/// The carry is the point. Assigning material.shader RESETS every
		/// property, so a swap done by hand leaves a white material where a
		/// coloured one was, and the loss is silent. Every value is therefore
		/// read BEFORE the assignment and written after it, and anything the new
		/// shader cannot accept is named rather than dropped quietly.
		public static string FixAndroidShaders(string materialName, string targetShader)
		{
			string wanted = (materialName ?? "").Trim();
			if (wanted.Length == 0)
			{
				throw new Exception("fix-android-shaders requires a material name. It resolves the name against the "
					+ "materials the open scene's renderers use, so run \"materials\" first to see the "
					+ "exact name and path of the one you mean.");
			}
			string requested = (targetShader ?? "").Trim();
			if (requested.Length == 0) requested = DefaultTargetShader;
			// The VRChat/ prefix is accepted either way because a caller reading
			// the fix line above will type "Mobile/Standard Lite".
			if (!requested.StartsWith("VRChat/", StringComparison.OrdinalIgnoreCase)) requested = "VRChat/" + requested;

			List<Material> materials = SceneMaterials();
			if (materials.Count == 0)
			{
				throw new Exception("No material is referenced by any renderer in the open scene, so \"" + wanted
					+ "\" cannot be found. There is nothing to swap.");
			}

			var report = new StringBuilder();
			report.AppendLine("--- fix-android-shaders: " + wanted + " ---");

			Material material = FindMaterial(materials, wanted);
			report.AppendLine("material: " + material.name + "  path: " + PathOf(material));

			Shader current = material.shader;
			if (current == null)
			{
				throw new Exception("\"" + wanted + "\" has NO shader assigned, so it renders magenta. There is no "
					+ "shader to swap away from and nothing to carry over. Assign a shader in the Inspector first.");
			}
			report.AppendLine("current shader: " + current.name + "  (" + PathOf(current) + ")");

			Shader target = Shader.Find(requested);
			if (target == null)
			{
				throw new Exception("No shader named \"" + requested + "\" is installed in this project -- "
					+ "Shader.Find returned null, which is what a shader inside an uninstalled or non-matching "
					+ "package looks like. Nothing was changed." + MobileWhitelistHint());
			}
			report.AppendLine("target shader: " + target.name + "  (" + PathOf(target) + ")");

			if (current == target || string.Equals(current.name, target.name, StringComparison.Ordinal))
			{
				report.AppendLine("\"" + material.name + "\" is already on " + target.name + ", so nothing was "
					+ "changed: no shader was assigned and no property was written.");
				return report.ToString();
			}

			if (current.name.StartsWith(MobilePrefix, StringComparison.OrdinalIgnoreCase))
			{
				throw new Exception("\"" + material.name + "\" is already on " + current.name + ", which is a "
					+ "VRChat Mobile shader -- the family VRChat whitelists for Android. No swap is needed: running "
					+ "this again would only risk losing a value it currently holds. If you want a DIFFERENT mobile "
					+ "shader, set it by hand in the Inspector, or pass targetShader= a mobile shader you have "
					+ "already checked.");
			}

			List<Carry> carried = new List<Carry>();
			var dropped = new List<string>();
			var absent = new List<string>();
			WarnIfTargetNotWhitelisted(report, target);
			string undoState;
			try
			{
				Undo.RecordObject(material, "pi fix android shader " + material.name);
				undoState = "recorded as one undo step (\"pi fix android shader " + material.name + "\")";
			}
			catch (Exception error)
			{
				undoState = "NOT registered (" + error.Message + "): the shader WAS swapped, but Ctrl-Z may not "
					+ "put the old one back";
			}

			// The scratch carries the TARGET shader, so every existence question
			// below is asked against the shader the values are about to land on
			// and not against the one they came from.
			Material scratch = new Material(target);
			try
			{
				CarryColor(material, scratch, carried, dropped, absent);
				CarryMainTexture(material, scratch, carried, dropped, absent);
				CarryFloat(material, scratch, "_Metallic", carried, dropped, absent);
				CarryFloat(material, scratch, "_Glossiness", carried, dropped, absent);
			}
			finally
			{
				UnityEngine.Object.DestroyImmediate(scratch);
			}

			bool assigned = true;
			try
			{
				material.shader = target;
			}
			catch (Exception error)
			{
				assigned = false;
				report.AppendLine("the shader assignment THREW (" + error.Message + "), so nothing below was written");
			}
			List<string> unwritten = assigned
				? ApplyCarried(material, carried)
				: new List<string> { "nothing was written: the shader assignment threw"};
			EditorUtility.SetDirty(material);
			Scene scene = SceneManager.GetActiveScene();
			if (scene.IsValid()) EditorSceneManager.MarkSceneDirty(scene);

			report.AppendLine("shader: " + current.name + " -> " + (material.shader == null ? "<null>" : material.shader.name));
			if (material.shader == null || !string.Equals(material.shader.name, target.name, StringComparison.Ordinal))
			{
				report.AppendLine("  WARNING: the assignment did not take; the material is on "
					+ (material.shader == null ? "<no shader>" : material.shader.name) + ". The values below were "
					+ "written against " + target.name + " and may not have applied.");
			}

			report.AppendLine("carried over:");
			if (carried.Count == 0) report.AppendLine("  nothing: the old shader set none of the properties this action carries");
			else foreach (Carry carry in carried) report.AppendLine("  " + carry.Text);
			foreach (string line in unwritten) report.AppendLine("  NOT WRITTEN " + line);

			report.AppendLine("dropped:");
			if (dropped.Count == 0)
			{
				report.AppendLine("  nothing: every value read off " + current.name + " has a home on " + target.name);
			}
			else
			{
				foreach (string line in dropped) report.AppendLine("  " + line);
				report.AppendLine("  " + dropped.Count + " propert(y/ies) could not be carried over because "
					+ target.name + " has no matching property: each line above names the property that was lost,");
				report.AppendLine("  the value the material had, and the reason. Set it by hand if "
					+ target.name + " spells that property differently.");
			}

			report.AppendLine("not read:");
			if (absent.Count == 0)
			{
				report.AppendLine("  nothing: every property this action carries exists and was readable on "
					+ current.name);
			}
			else
			{
				foreach (string line in absent) report.AppendLine("  " + line);
				report.AppendLine("  " + absent.Count + " propert(y/ies) skipped for that reason. Nothing was lost "
					+ "here: the old material did not hold them either.");
				report.AppendLine("  These are counted separately from the dropped properties above -- a property the "
					+ "old shader never had is not a value lost by the swap.");
			}

			report.AppendLine("undo: " + undoState);
			report.AppendLine(unwritten.Count == 0
				? "every carried value was written to the material after the shader was assigned"
				: unwritten.Count + " carried value(s) could NOT be written and are listed as NOT WRITTEN above; "
					+ "the material does not hold them");
			report.AppendLine(scene.IsValid()
				? "scene marked dirty (save it to keep the change)"
				: "no valid active scene, so nothing was marked dirty");
			report.AppendLine("This action cannot put the old shader and its values back: the values it read are not");
			report.AppendLine("stored anywhere, so a second run carries the NEW values over, not the original ones.");
			report.AppendLine("Look at the material in the Scene view after this. Whether the swap LOOKS the same is");
			report.AppendLine("not something this action can measure -- only the values it wrote and the names of the");
			report.AppendLine("ones it could not carry are.");
			return report.ToString();
		}

		/// The swap fixes one material; whether the material it was moved ONTO can
		/// build for Android is a separate fact, and swapping one non-whitelisted
		/// shader for another changes nothing that matters.
		private static void WarnIfTargetNotWhitelisted(StringBuilder report, Shader target)
		{
			HashSet<string> allowed;
			string source;
			string reason;
			if (!TryReadShaderWhiteList(out allowed, out source, out reason)) return;
			if (allowed.Contains(target.name)) return;
			report.AppendLine("WARNING: " + target.name + " is NOT on the Android whitelist from " + source
				+ ". This swap would move the material from one shader VRChat will not build for Android to");
			report.AppendLine("  another. Check the name before going ahead; " + QuestFix + ".");
		}

		/// The one material with that name, or an exception that says what
		/// collided and what exists.
		///
		/// Two materials sharing a name is not cosmetic here: the swap rewrites
		/// a material asset in place, so picking the wrong one of two changes a
		/// file the caller never named.
		private static Material FindMaterial(List<Material> materials, string name)
		{
			var matches = new List<Material>();
			foreach (Material material in materials)
			{
				if (string.Equals(material.name, name, StringComparison.Ordinal)) matches.Add(material);
			}
			if (matches.Count == 0)
			{
				throw new Exception("No scene material named \"" + name + "\". The materials the open scene's "
					+ "renderers use:\n  " + MaterialLines(materials) + "\nNothing was changed.");
			}
			if (matches.Count > 1)
			{
				var candidates = new List<string>();
				foreach (Material material in matches) candidates.Add(material.name + "  " + PathOf(material));
				throw new Exception("\"" + name + "\" is " + matches.Count + " DIFFERENT materials in this scene, and "
					+ "the swap writes to whichever one is picked. Candidates:\n  "
					+ string.Join("\n  ", candidates.ToArray())
					+ "\nNothing was changed. Rename one, or swap it by hand in the Inspector.");
			}
			return matches[0];
		}

		private static void CarryColor(Material source, Material scratch, List<Carry> carried, List<string> dropped, List<string> absent)
		{
			string from = HasProperty(source, "_Color") ? "_Color"
				: HasProperty(source, "_BaseColor") ? "_BaseColor" : null;
			if (from == null || !TryReadColor(source, from, out Color value))
			{
				absent.Add((from ?? "_Color and _BaseColor") + " -- this material on " + source.shader.name
					+ " has no readable colour property");
				return;
			}
			string to = HasProperty(scratch, "_Color") ? "_Color"
				: HasProperty(scratch, "_BaseColor") ? "_BaseColor" : null;
			if (to == null)
			{
				dropped.Add(from + "=" + FormatColor(value) + " -- " + scratch.shader.name
					+ " has neither _Color nor _BaseColor, so the colour was lost");
				return;
			}
			carried.Add(new Carry(CarryKind.Color, to, value, from + " -> " + to + " = " + FormatColor(value)));
		}

		private static void CarryMainTexture(Material source, Material scratch, List<Carry> carried, List<string> dropped, List<string> absent)
		{
			if (!HasProperty(source, "_MainTex"))
			{
				absent.Add("_MainTex -- this material on " + source.shader.name + " has no _MainTex, so it has no "
					+ "main texture, no tiling and no offset to carry");
				return;
			}
			if (!HasProperty(scratch, "_MainTex"))
			{
				Texture held = TryReadTexture(source, "_MainTex", out Texture existing) ? existing : null;
				dropped.Add("_MainTex=" + (held == null ? "<none assigned, or the value could not be read>" : held.name)
					+ " -- " + scratch.shader.name + " has no _MainTex, so the texture, its tiling and its offset "
					+ "were all lost");
				return;
			}
			if (TryReadTexture(source, "_MainTex", out Texture texture))
			{
				carried.Add(new Carry(CarryKind.Texture, "_MainTex", texture,
					"_MainTex -> _MainTex = " + (texture == null
						? "<none was assigned on the old material>"
						: texture.name + " (" + texture.width + "x" + texture.height + ")")));
			}
			else
			{
				absent.Add("_MainTex (the texture value could not be read)");
			}
			if (TryReadVector(source, "_MainTex", true, out Vector2 scale))
			{
				carried.Add(new Carry(CarryKind.Scale, "_MainTex", scale,
					"_MainTex scale -> _MainTex scale = " + FormatVector(scale)));
			}
			else
			{
				absent.Add("_MainTex scale (the tiling could not be read)");
			}
			if (TryReadVector(source, "_MainTex", false, out Vector2 offset))
			{
				carried.Add(new Carry(CarryKind.Offset, "_MainTex", offset,
					"_MainTex offset -> _MainTex offset = " + FormatVector(offset)));
			}
			else
			{
				absent.Add("_MainTex offset (the offset could not be read)");
			}
		}

		private static void CarryFloat(Material source, Material scratch, string property, List<Carry> carried, List<string> dropped, List<string> absent)
		{
			if (!HasProperty(source, property) || !TryReadFloat(source, property, out float value))
			{
				absent.Add(property + " -- this material on " + source.shader.name + " has no such property");
				return;
			}
			if (!HasProperty(scratch, property))
			{
				dropped.Add(property + "=" + Num(value) + " -- " + scratch.shader.name + " has no " + property
					+ ", so it was lost");
				return;
			}
			carried.Add(new Carry(CarryKind.Float, property, value, property + " -> " + property + " = " + Num(value)));
		}

		/// Writes the recorded carries onto the swapped material and names every
		/// one Unity refused.
		///
		/// The values are held in their real types rather than re-parsed out of
		/// the report lines: a colour rendered as "(1, 0.5, 0.25, a=1)" does not
		/// parse back into a Color, and a carry that cannot be written is a
		/// property silently lost, which is the failure this action exists to
		/// prevent.
		private static List<string> ApplyCarried(Material material, List<Carry> carried)
		{
			var unwritten = new List<string>();
			foreach (Carry carry in carried)
			{
				try
				{
					switch (carry.Kind)
					{
						case CarryKind.Color:
							material.SetColor(carry.Target, (Color)carry.Value);
							break;
						case CarryKind.Texture:
							material.SetTexture(carry.Target, (Texture)carry.Value);
							break;
						case CarryKind.Scale:
							material.SetTextureScale(carry.Target, (Vector2)carry.Value);
							break;
						case CarryKind.Offset:
							material.SetTextureOffset(carry.Target, (Vector2)carry.Value);
							break;
						default:
							material.SetFloat(carry.Target, (float)carry.Value);
							break;
					}
				}
				catch (Exception error)
				{
					unwritten.Add(carry.Target + ": " + error.Message + " -- reported above as carried but NOT written");
					Debug.LogWarning("[pi] " + material.name + "." + carry.Target + ": " + error.Message);
				}
			}
			return unwritten;
		}

		private enum CarryKind
		{
			Color,
			Texture,
			Scale,
			Offset,
			Float,
		}

		/// One value read off the old shader, with where it is going and the line
		/// the report prints for it.
		private sealed class Carry
		{
			public readonly CarryKind Kind;
			public readonly string Target;
			public readonly object Value;
			public readonly string Text;

			public Carry(CarryKind kind, string target, object value, string text)
			{
				Kind = kind;
				Target = target;
				Value = value;
				Text = text;
			}
		}

		/// The VRChat/Mobile entries of the whitelist, so a mistyped shader name
		/// is answered with the names that DO exist rather than only a refusal.
		private static string MobileWhitelistHint()
		{
			HashSet<string> allowed;
			string source;
			string reason;
			if (!TryReadShaderWhiteList(out allowed, out source, out reason)) return "";
			var mobile = new List<string>();
			foreach (string name in allowed.OrderBy(entry => entry, StringComparer.Ordinal))
			{
				if (!name.StartsWith(MobilePrefix, StringComparison.OrdinalIgnoreCase)) continue;
				mobile.Add(name);
				if (mobile.Count >= MaxListLines) break;
			}
			if (mobile.Count == 0) return "";
			return "\nMobile shaders the " + source + " whitelist accepts: " + string.Join(", ", mobile.ToArray());
		}

		// ============================================================== reading

		/// The materials the SCENE's renderers use, deduplicated. Private rather
		/// than shared: the same walk exists in PiBuildChecks and PiCensus, and
		/// each is private there for the same reason this copy is -- a project
		/// wide material search would report assets this world never ships.
		private static List<Material> SceneMaterials()
		{
			var materials = new List<Material>();
			var seen = new HashSet<Material>();
			foreach (Renderer renderer in UnityEngine.Object.FindObjectsOfType<Renderer>(true))
			{
				foreach (Material material in renderer.sharedMaterials)
				{
					if (material == null) continue;
					if (seen.Add(material)) materials.Add(material);
				}
			}
			return materials;
		}

		/// The Android whitelist, VRWorld Toolkit's list preferred over the SDK's:
		/// the toolkit's is the Android-only list, while the SDK's is the union
		/// including PC-only shaders, so answering from the SDK's alone would
		/// pass a material VRChat still refuses to build for Android.
		/// The Android shader whitelist, from whichever of the two places holds it:
		/// VRWorld Toolkit's own list first, then the SDK's. Both are read
		/// reflectively and both are non-public in at least one SDK version, so a
		/// failure is a `reason`, not an exception -- a caller that cannot answer
		/// has to say WHICH list it failed to read, or the report implies the
		/// project has no whitelist when it has one we could not bind.
		///
		/// Handles any IEnumerable, not just string[], because the two lists are
		/// not the same shape across SDK versions.
		///
		/// Shared rather than reimplemented: PiCensus reads the same list for its
		/// performance census, and two copies of a reflection lookup drift.
		internal static bool TryReadShaderWhiteList(out HashSet<string> allowed, out string source, out string reason)
		{
			foreach (string typeName in new[] { ToolkitValidationType, SdkValidationType })
			{
				string fieldName = typeName == ToolkitValidationType ? ToolkitWhiteListField : SdkWhiteListField;
				Type type = VrcResolve.Type(typeName);
				if (type == null) continue;
				object raw;
				try
				{
					FieldInfo field = type.GetField(fieldName, BindingFlags.Public | BindingFlags.Static);
					if (field == null) continue;
					raw = field.GetValue(null);
				}
				catch (Exception)
				{
					continue;
				}
				HashSet<string> collected;
				if (!Collect(raw, out collected)) continue;
				allowed = collected;
				source = typeName + "." + fieldName;
				reason = null;
				return true;
			}
			allowed = null;
			source = null;
			reason = "neither " + ToolkitValidationType + "." + ToolkitWhiteListField + " (VRWorld Toolkit) nor "
				+ SdkValidationType + "." + SdkWhiteListField + " (com.vrchat.base) resolved to a readable list";
			return false;
		}

		private static bool Collect(object raw, out HashSet<string> collected)
		{
			collected = null;
			var names = new HashSet<string>(StringComparer.Ordinal);
			if (raw is string[] array)
			{
				foreach (string name in array) if (!string.IsNullOrEmpty(name)) names.Add(name);
			}
			else if (raw is IEnumerable sequence)
			{
				foreach (object entry in sequence)
				{
					string name = entry as string ?? entry?.ToString();
					if (!string.IsNullOrEmpty(name)) names.Add(name);
				}
			}
			else
			{
				return false;
			}
			// An empty list is not an answer: "nothing is whitelisted" and "the
			// whitelist did not load" have opposite consequences.
			if (names.Count == 0) return false;
			collected = names;
			return true;
		}

		private static bool HasProperty(Material material, string property)
		{
			if (material == null || string.IsNullOrEmpty(property)) return false;
			try { return material.HasProperty(property); }
			catch (Exception) { return false; }
		}

		private static bool TryReadColor(Material material, string property, out Color value)
		{
			value = Color.white;
			if (!HasProperty(material, property)) return false;
			try { value = material.GetColor(property); return true; }
			catch (Exception) { return false; }
		}

		private static bool TryReadFloat(Material material, string property, out float value)
		{
			value = 0f;
			if (!HasProperty(material, property)) return false;
			try { value = material.GetFloat(property); return true; }
			catch (Exception) { return false; }
		}

		private static bool TryReadTexture(Material material, string property, out Texture value)
		{
			value = null;
			if (!HasProperty(material, property)) return false;
			try { value = material.GetTexture(property); return true; }
			catch (Exception) { value = null; return false; }
		}

		/// A material's tiling (scale) or offset for a texture slot.
		private static bool TryReadVector(Material material, string property, bool scale, out Vector2 value)
		{
			value = Vector2.zero;
			if (!HasProperty(material, property)) return false;
			try
			{
				value = scale ? material.GetTextureScale(property) : material.GetTextureOffset(property);
				return true;
			}
			catch (Exception)
			{
				return false;
			}
		}

		// ============================================================ formatting

		/// An Assets-relative path, or a marker for anything that is not an asset.
		private static string PathOf(UnityEngine.Object target)
		{
			if (target == null) return "(none)";
			string path;
			try { path = AssetDatabase.GetAssetPath(target); }
			catch (Exception) { return "(path not readable)"; }
			return string.IsNullOrEmpty(path) ? "<scene instance: not an asset>" : path;
		}

		private static string MaterialLines(List<Material> materials)
		{
			var lines = new List<string>();
			foreach (Material material in materials.Take(MaxListLines))
			{
				lines.Add(material.name + " -> " + (material.shader == null ? "<no shader>" : material.shader.name)
					+ "  " + PathOf(material));
			}
			if (materials.Count > MaxListLines)
			{
				lines.Add("... and " + (materials.Count - MaxListLines) + " more");
			}
			return string.Join("\n  ", lines.ToArray());
		}

		private static string FormatColor(Color value)
		{
			return string.Format(CultureInfo.InvariantCulture, "({0:0.###}, {1:0.###}, {2:0.###}, a={3:0.###})",
				value.r, value.g, value.b, value.a);
		}

		private static string FormatVector(Vector2 value)
		{
			return string.Format(CultureInfo.InvariantCulture, "({0:0.###}, {1:0.###})", value.x, value.y);
		}

		private static string Num(float value)
		{
			return value.ToString("0.###", CultureInfo.InvariantCulture);
		}
	}
}