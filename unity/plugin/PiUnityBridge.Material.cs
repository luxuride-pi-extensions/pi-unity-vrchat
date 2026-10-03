// Part of the pi Unity bridge plugin. See PiUnityBridge.Core.cs for the
// protocol overview. The whole plugin is one assembly of partial classes, so
// Unity compiles it into Assembly-CSharp-Editor with no asmdef and no
// package dependencies, which is what lets it drop into any Unity 2021+
// project (a configured VRChat world, an existing game) that pi was pointed at
// rather than generated.

// Material and shader helpers, callable from a snippet.
//
// WHY THIS EXISTS
// ---------------
// Probe scripts in a real VRChat world exposed a failure mode that produces no
// error anywhere: a lilToon material in a project on the BUILT-IN pipeline
// rendered pure white. Nothing reported a problem -- Shader.Find resolved it,
// the assignment succeeded, HasProperty("_BaseColor") was true. The result
// reads as "the renderer is broken" rather than "wrong material", so it costs
// a lot of wasted debugging.
//
// Note the failure is SILENT rather than magenta, which is what distinguishes
// it from a shader that is simply not installed for this pipeline. The root
// cause was never identified; see CandidatesForCurrentPipeline for what is
// actually known and what is only a precaution.
//
// Two related traps, both handled here:
//   - Assigning a NEW shader RESETS the material's properties, so a colour set
//     before the swap is silently lost.
//   - Shader.Find returns null for a shader that is not installed. A
//     null-assignment "succeeds" and leaves a magenta object.
//
// USAGE FROM A SNIPPET
// ---------------------
// Snippets can see the plugin's own assembly (verified live: the reference
// union includes Assembly-CSharp-Editor), so these are plain static calls:
//
//     var material = PiUnity.PiMaterial.Create("Assets/Materials/Bone.mat", Color.grey);
//     PiUnity.PiMaterial.Assign(someGameObject, material);
//
// and to find out what this project actually has before choosing:
//
//     Debug.Log(PiUnity.PiMaterial.Describe());
//
// This is deliberately a public non-partial class rather than more of Bridge:
// a snippet calls it directly, and a snippet's compiled assembly is a
// different one, so it must be public and must not depend on the bridge's
// private plumbing.

using System.Text;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;

namespace PiUnity
{
	public static class PiMaterial
	{
		/// Shader preference, most-preferred first, per render pipeline.
		///
		/// WHY lilToon IS RANKED LAST OFF-URP -- and why the reason is not the
		/// one you will find in an earlier version of this comment.
		///
		/// The previous comment claimed lilToon "IS a URP shader" and therefore
		/// renders white on the built-in pipeline. That was measured and it is
		/// FALSE: in Chiffon (built-in) lilToon reports an EMPTY RenderPipeline
		/// tag and isSupported = true, so nothing about the shader itself says it
		/// belongs to URP.
		///
		/// What IS established is narrower: a lilToon material in a built-in
		/// project rendered pure white with no error, and every static check
		/// (Shader.Find, HasProperty, isSupported, pipeline tag) still passed on
		/// it. A lilToon material also never renders magenta, which is what a
		/// genuinely wrong-pipeline shader does. So the cause was never
		/// identified, and the ranking below is a PRECAUTION -- prefer the
		/// pipeline's own Lit shader when it exists, since that one is certainly
		/// correct -- not a diagnosed fix.
		private static string[] CandidatesForCurrentPipeline()
		{
			bool urp = currentRenderPipelineName().Contains("Universal");
			if (urp)
			{
				return new[] { "Universal Render Pipeline/Lit", "lilToon", "lilToon/Lite", "Standard" };
			}
			if (currentRenderPipelineName().Contains("HighDefinition"))
			{
				return new[] { "HDRP/Lit", "Standard" };
			}
			return new[] { "Standard", "Universal Render Pipeline/Lit", "lilToon" };
		}

		private static string currentRenderPipelineName()
		{
			RenderPipelineAsset asset = GraphicsSettings.currentRenderPipeline;
			return asset == null ? "built-in" : asset.GetType().Name;
		}

		/// The best installed shader for the pipeline this project is actually
		/// on, or null when none of the candidates is installed.
		public static Shader PickShader()
		{
			string[] candidates = CandidatesForCurrentPipeline();
			foreach (string name in candidates)
			{
				Shader shader = Shader.Find(name);
				if (shader != null) return shader;
			}
			return null;
		}

		/// A human-readable report of the pipeline, which shaders are installed,
		/// and which one this helper would pick.
		///
		/// Worth calling before hand-picking a shader: it answers "is lilToon
		/// even here, and will it work in this project" without a probe script.
		public static string Describe()
		{
			var report = new StringBuilder();
			string pipeline = currentRenderPipelineName();
			report.AppendLine("renderPipeline=" + pipeline);
			report.AppendLine("preferred (in order): " + string.Join(", ", CandidatesForCurrentPipeline()));
			report.AppendLine("-- Shader.Find across common names --");
			foreach (string name in new[]
			{
				"Standard", "Universal Render Pipeline/Lit", "Universal Render Pipeline/Unlit",
				"HDRP/Lit", "lilToon", "lilToon/Lite", "Unlit/Color", "Sprites/Default",
			})
			{
				Shader shader = Shader.Find(name);
				report.AppendLine("  " + (shader == null ? "not installed" : "installed  isSupported=" + shader.isSupported) + "  " + name);
			}
			Shader picked = PickShader();
			report.AppendLine("PickShader() -> " + (picked == null ? "<null: no candidate installed>" : picked.name));
			if (picked != null)
			{
				report.AppendLine("note: assigning a shader RESETS material properties; set colours after any shader change.");
			}
			return report.ToString();
		}

		/// Creates or updates a material asset at an Assets/-relative path, using
		/// a shader appropriate for the current pipeline.
		///
		/// Idempotent: an existing asset at that path is updated rather than
		/// duplicated, so a re-run is safe.
		///
		/// The base colour is applied AFTER the shader, because a shader
		/// assignment wipes existing properties.
		public static Material Create(string assetPath, Color baseColor)
		{
			Shader shader = PickShader();
			if (shader == null)
			{
				throw new System.Exception(
					"No usable shader is installed for this project's render pipeline (" + currentRenderPipelineName() + "). " +
					"Tried: " + string.Join(", ", CandidatesForCurrentPipeline()) + ". " +
					"Call PiUnity.PiMaterial.Describe() to see what is actually available."
				);
			}

			EnsureFolder(System.IO.Path.GetDirectoryName(assetPath));

			Material material = AssetDatabase.LoadAssetAtPath<Material>(assetPath);
			if (material == null)
			{
				material = new Material(shader);
				AssetDatabase.CreateAsset(material, assetPath);
			}
			else if (material.shader != shader)
			{
				// Only swap when it actually differs, and warn: a swap discards
				// every property the old shader had.
				material.shader = shader;
			}

			SetColor(material, baseColor);
			EditorUtility.SetDirty(material);
			AssetDatabase.SaveAssets();
			return material;
		}

		/// Sets whichever colour property the shader actually has, so the same
		/// call works for URP's _BaseColor and built-in/Standard's _Color.
		public static void SetColor(Material material, Color color)
		{
			if (material == null) throw new System.ArgumentNullException("material");
			if (material.HasProperty("_BaseColor")) material.SetColor("_BaseColor", color);
			if (material.HasProperty("_Color")) material.SetColor("_Color", color);
		}

		/// Assigns a material to a GameObject's renderer, creating the pieces a
		/// bare GameObject may not have.
		public static Renderer Assign(GameObject target, Material material)
		{
			if (target == null) throw new System.ArgumentNullException("target");
			if (material == null) throw new System.ArgumentNullException("material");

			Renderer renderer = target.GetComponent<Renderer>();
			if (renderer == null) renderer = target.AddComponent<MeshRenderer>();
			renderer.sharedMaterial = material;
			return renderer;
		}

		/// internal, not private, because Bridge composes a prefab into a folder
		/// that may not exist yet. PiMaterial keeps it in one place so both call
		/// sites create folders the same way; it is not public because a snippet's
		/// separate assembly has no use for it.
		internal static void EnsureFolder(string folder)
		{
			if (string.IsNullOrEmpty(folder) || folder == "Assets") return;
			if (AssetDatabase.IsValidFolder(folder)) return;

			// Create each missing level, deepest last, so AssetDatabase is happy.
			string[] parts = folder.Split('/');
			string current = parts[0];
			for (int i = 1; i < parts.Length; i++)
			{
				string next = current + "/" + parts[i];
				if (!AssetDatabase.IsValidFolder(next)) AssetDatabase.CreateFolder(current, parts[i]);
				current = next;
			}
		}
	}
}
