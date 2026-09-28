// Part of the pi Unity bridge plugin. See PiUnityBridge.Core.cs for the
// protocol overview. The whole plugin is one assembly of partial classes, so
// Unity compiles it into Assembly-CSharp-Editor with no asmdef and no package
// dependencies.
//
// Source-model handoff for the Unity -> Blender leg.
//
// WHY THIS EXISTS
// ---------------
// Exporting a mesh to OBJ re-serialises geometry that already exists on disk as
// a source model. An imported FBX is a far better handoff: Blender reads FBX
// natively, the file carries the material description, and no geometry is lost
// to a format conversion. So when the meshes you selected came from a real
// model file under Assets/, hand the FILE over instead of re-exporting it.
//
// Two measured facts shaped this, both from the live Chiffon avatar:
//
//   1. A MeshFilter-only walk MISSES THE ENTIRE AVATAR. The body is a
//      SkinnedMeshRenderer (10009 verts, 186 bones) and never appears in a
//      MeshFilter enumeration. Walking both renderer kinds is required.
//
//   2. Most MeshFilter results have no file to hand over. Unity's built-in
//      primitives (`sphere`, `arrow`, `target`) report their mesh path as
//      "Library/unity default resources" -- a path Blender cannot open. Those
//      must be filtered out, not passed along, or the transfer hands Blender a
//      pointer into Unity's resource database and reports success.
//
// SCOPE: whole files. When a selection resolves to a source model, the entire
// file is copied -- not just the selected sub-meshes. FBX sub-mesh selection is
// not expressible in the export formats, and silently narrowing or silently
// widening the transfer would both be lies. Which sub-objects resolved from
// where is reported so the widening is visible rather than implied.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEngine;

namespace PiUnity
{
	public static partial class Bridge
	{
		/// Extensions Blender can actually import. Anything else (and anything
		/// outside Assets/) is not a file to hand over.
		private static readonly string[] HandoffExtensions = { ".fbx", ".blend", ".obj", ".dae", ".gltf", ".glb" };

		/// True when a mesh's asset path is a real file on disk.
		///
		/// The Assets/ prefix is the load-bearing part: built-in primitives
		/// report "Library/unity default resources", which is not a file.
		private static bool IsHandoffableModelPath(string path)
		{
			if (string.IsNullOrEmpty(path)) return false;
			if (!path.StartsWith("Assets/", StringComparison.Ordinal)) return false;
			string extension = Path.GetExtension(path).ToLowerInvariant();
			return HandoffExtensions.Contains(extension);
		}

		private static GameObject[] ResolveHandoffRoots(Json request)
		{
			string[] names = request.GetStringArray("gameObjectNames").ToArray();
			var scene = UnityEngine.SceneManagement.SceneManager.GetActiveScene();
			if (names.Length == 0) return scene.GetRootGameObjects();

			var roots = new List<GameObject>();
			foreach (string name in names)
			{
				GameObject found = GameObject.Find(name);
				if (found == null) found = FindGameObjectByName(name);
				if (found == null) throw new Exception("No GameObject named \"" + name + "\" in the active scene.");
				roots.Add(found);
			}
			return roots.ToArray();
		}

		/// Walks both MeshFilter and SkinnedMeshRenderer, grouping every mesh
		/// under the real source file it was imported from.
		private sealed class SourceGroup
		{
			public string AssetPath = "";
			public string AbsolutePath = "";
			public readonly List<string> Objects = new List<string>();
			public int VertexTotal;
			public int BoneTotal;
		}

		private static List<SourceGroup> CollectSourceGroups(
			GameObject[] roots,
			out List<string> ungrouped,
			out List<string> ungroupedNames,
			out Dictionary<string, int> ungroupedCounts)
		{
			var byPath = new Dictionary<string, SourceGroup>(StringComparer.Ordinal);
			var withoutModel = new List<string>();
			var withoutModelNames = new List<string>();
			var withoutModelCounts = new Dictionary<string, int>(StringComparer.Ordinal);
			var seen = new HashSet<string>();

			void Consider(GameObject owner, Mesh mesh, int bones)
			{
				if (mesh == null) return;
				string path = AssetDatabase.GetAssetPath(mesh);
				if (!IsHandoffableModelPath(path))
				{
					// A built-in primitive or procedural mesh. Named so the caller
					// learns why it did not travel, instead of wondering.
					//
					// The BARE name is reported separately from the "name -> path"
					// explanation because the binder has to feed these objects
					// back into the OBJ exporter by name, and "arrow -> Library/..."
					// is not something GameObject.Find can resolve.
					//
					// A name can be shared by several objects (Chiffon has five
					// "sphere" primitives). The OBJ fallback resolves a name to ONE
					// object, so the count is reported: otherwise a transfer would
					// quietly bring across one of five and claim all five.
					if (seen.Add("u:" + owner.name))
					{
						withoutModel.Add(owner.name + " -> " + (string.IsNullOrEmpty(path) ? "<scene instance>" : path));
						withoutModelNames.Add(owner.name);
					}
					if (withoutModelCounts.ContainsKey(owner.name)) withoutModelCounts[owner.name]++;
					else withoutModelCounts[owner.name] = 1;
					return;
				}
				SourceGroup group;
				if (!byPath.TryGetValue(path, out group))
				{
					group = new SourceGroup();
					group.AssetPath = path;
					group.AbsolutePath = Path.Combine(ProjectRoot(), path);
					byPath[path] = group;
				}
				if (!group.Objects.Contains(owner.name)) group.Objects.Add(owner.name);
				group.VertexTotal += mesh.vertexCount;
				if (bones > group.BoneTotal) group.BoneTotal = bones;
			}

			foreach (GameObject root in roots)
			{
				foreach (MeshFilter filter in root.GetComponentsInChildren<MeshFilter>(false))
					Consider(filter.gameObject, filter.sharedMesh, 0);
				foreach (SkinnedMeshRenderer skin in root.GetComponentsInChildren<SkinnedMeshRenderer>(false))
					Consider(skin.gameObject, skin.sharedMesh, skin.bones.Length);
			}

			ungrouped = withoutModel;
			ungroupedNames = withoutModelNames;
			ungroupedCounts = withoutModelCounts;
			return byPath.Values.OrderBy(group => group.AssetPath, StringComparer.Ordinal).ToList();
		}

		/// Reports which real source model files a selection resolves to, without
		/// copying anything. Read-only, so it is safe to call before deciding.
		/// Emits how many objects carry each unhandoffable name.
		///
		/// The OBJ fallback resolves a GameObject name to exactly ONE object, so
		/// five objects called "sphere" cannot all be exported by passing that
		/// one name. Without the count, a transfer reports "sphere transferred"
		/// after moving one of the five, which is the kind of result that is
		/// worse than an error because it looks complete.
		private static void AddUnhandoffableCounts(Json target, Dictionary<string, int> counts)
		{
			Json nested = target.OpenObject("unhandoffableCounts");
			foreach (KeyValuePair<string, int> entry in counts)
				nested.AddNumber(entry.Key, entry.Value);
			nested.CloseObject();
		}

		private static string FindSourceModels(Json request)
		{
			GameObject[] roots = ResolveHandoffRoots(request);
			List<string> ungrouped;
			List<string> ungroupedNames;
			Dictionary<string, int> ungroupedCounts;
			List<SourceGroup> groups = CollectSourceGroups(roots, out ungrouped, out ungroupedNames, out ungroupedCounts);

			Json result = new Json();
			result.OpenObject();

			Json entries = new Json();
			entries.OpenArray();
			foreach (SourceGroup group in groups)
			{
				Json entry = new Json();
				entry.OpenObject();
				entry.AddString("assetPath", group.AssetPath);
				entry.AddString("absolutePath", group.AbsolutePath);
				entry.AddString("fileName", Path.GetFileName(group.AssetPath));
				entry.AddArray("objects", group.Objects.ToArray());
				entry.AddNumber("vertexTotal", group.VertexTotal);
				entry.AddNumber("boneTotal", group.BoneTotal);
				entry.AddBool("exists", File.Exists(group.AbsolutePath));
				entry.CloseObject();
				entries.AddElement(entry);
			}
			entries.CloseArray();
			result.AddArray("models", entries);

			result.AddArray("unhandoffableObjects", ungrouped.ToArray());
			result.AddArray("unhandoffableNames", ungroupedNames.ToArray());
			AddUnhandoffableCounts(result, ungroupedCounts);
			result.AddNumber("modelCount", groups.Count);
			result.AddNumber("unhandoffableCount", ungrouped.Count);
			result.AddString(
				"scope",
				"whole file per source model: an FBX cannot carry a sub-mesh selection, so every " +
				"sub-mesh from a file travels with it. 'objects' lists which ones you asked for. " +
				"'unhandoffableNames' holds bare GameObject names, ready to pass to export-obj as " +
				"gameObjectNames, so the same selection can be transferred by BOTH routes at once.");
			result.CloseObject();
			return result.ToString();
		}

		/// Copies the resolved source model files into a staging directory so the
		/// binder can hand them to Blender verbatim.
		///
		/// Copying the file rather than re-exporting is the whole point: the FBX
		/// Unity itself imported is exactly what Blender then imports, so no
		/// material description is lost in translation.
		private static string ExportSourceModels(Json request)
		{
			string outputDir = request.GetString("outputDir");
			if (string.IsNullOrEmpty(outputDir)) throw new Exception("export-source-models requires an outputDir.");
			Directory.CreateDirectory(outputDir);

			GameObject[] roots = ResolveHandoffRoots(request);
			List<string> ungrouped;
			List<string> ungroupedNames;
			Dictionary<string, int> ungroupedCounts;
			List<SourceGroup> groups = CollectSourceGroups(roots, out ungrouped, out ungroupedNames, out ungroupedCounts);

			var copied = new List<string>();
			var missing = new List<string>();
			var report = new List<Json>();

			foreach (SourceGroup group in groups)
			{
				Json entry = new Json();
				entry.OpenObject();
				entry.AddString("assetPath", group.AssetPath);

				// A model can be referenced by the scene but absent on disk (a
				// library-only checkout). Copying would throw; report it instead.
				if (!File.Exists(group.AbsolutePath))
				{
					entry.AddString("stagedPath", "");
					entry.AddBool("copied", false);
					entry.AddString("error", "source file does not exist on disk");
					missing.Add(group.AssetPath);
				}
				else
				{
					string target = Path.Combine(outputDir, Path.GetFileName(group.AssetPath));
					File.Copy(group.AbsolutePath, target, true);
					entry.AddString("stagedPath", target);
					entry.AddBool("copied", true);
					entry.AddNumber("bytes", new FileInfo(target).Length);
					copied.Add(target);
				}
				entry.AddArray("objects", group.Objects.ToArray());
				entry.AddNumber("vertexTotal", group.VertexTotal);
				entry.AddNumber("boneTotal", group.BoneTotal);
				entry.CloseObject();
				report.Add(entry);
			}

			Json result = new Json();
			result.OpenObject();
			Json entries = new Json();
			entries.OpenArray();
			foreach (Json entry in report) entries.AddElement(entry);
			entries.CloseArray();
			result.AddArray("models", entries);
			result.AddArray("stagedPaths", copied.ToArray());
			result.AddArray("missingSources", missing.ToArray());
			result.AddArray("unhandoffableObjects", ungrouped.ToArray());
			result.AddArray("unhandoffableNames", ungroupedNames.ToArray());
			AddUnhandoffableCounts(result, ungroupedCounts);
			result.AddNumber("copiedCount", copied.Count);
			result.AddNumber("missingCount", missing.Count);
			result.AddNumber("unhandoffableCount", ungrouped.Count);
			result.CloseObject();
			return result.ToString();
		}
	}
}
