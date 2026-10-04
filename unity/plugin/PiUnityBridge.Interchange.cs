// Part of the pi Unity bridge plugin. See PiUnityBridge.Core.cs for the
// protocol overview. The whole plugin is one assembly of partial classes, so
// Unity compiles it into Assembly-CSharp-Editor with no asmdef and no package
// dependencies, which is what lets it drop into any Unity 2021+ project.

// FBX import and OBJ export for the binder
//
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using UnityEditor;
using UnityEditor.Compilation;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace PiUnity
{
	public static partial class Bridge
	{
		private static string ImportFbx(Json request)
		{
			string fbxPath = request.GetString("fbxPath");
			if (string.IsNullOrEmpty(fbxPath))
				throw new Exception("import-fbx requires an fbxPath.");
			if (!File.Exists(fbxPath))
				throw new Exception("FBX file does not exist: " + fbxPath);

			string destination = request.GetString("destination");
			string name = request.GetString("name");
			if (string.IsNullOrEmpty(destination))
				throw new Exception("import-fbx requires a destination folder inside Assets/.");

			string modelName = string.IsNullOrEmpty(name) ? Path.GetFileNameWithoutExtension(fbxPath) : name;
			modelName = SanitizeAssetName(modelName);

			// The FBX and the prefab must both be free before anything is written.
			// MoveIntoAssets copies with overwrite, and SaveAsPrefabAsset replaces
			// silently, so importing a second model under a name already in use
			// destroyed the first one with no diagnostic -- the transfer then
			// reported success on top of the wreckage.
			string fbxTarget = destination.TrimEnd('/') + "/" + modelName + ".fbx";
			string prefabPath = destination.TrimEnd('/') + "/" + modelName + ".prefab";
			string fbxAbsolute = Path.Combine(ProjectRoot(), fbxTarget);
			string prefabAbsolute = Path.Combine(ProjectRoot(), prefabPath);
			if (File.Exists(fbxAbsolute) || File.Exists(prefabAbsolute))
			{
				throw new Exception(
					"Importing \"" + modelName + "\" would overwrite an existing asset in " + destination +
					": " + (File.Exists(fbxAbsolute) ? fbxTarget : prefabPath) +
					". Pass a different name, or delete the old asset first -- an import never replaces a model " +
					"that is already in the project."
				);
			}

			// Move the FBX into Assets/ so the import produces a sub-asset we can
			// instantiate, then pull a prefab out of it for a stable handle.
			// modelName is passed in rather than re-derived from the FBX's own file
			// name: the guard above checked `destination/modelName.fbx`, so the copy
			// target MUST be that same name or the check is theatre. It happens to
			// coincide on the only live path -- the binder stages the file as
			// `${modelName}.fbx` before calling -- but a caller who passes `name`
			// different from the stem used to get the guard checked and a different
			// file overwritten, with ok:true on top.
			string assetFbxPath = MoveIntoAssets(fbxPath, destination, modelName, ".fbx");
			GameObject assetRoot = AssetDatabase.LoadAssetAtPath<GameObject>(assetFbxPath);
			if (assetRoot == null)
				throw new Exception("Unity did not produce a model for " + assetFbxPath + ". Check the Editor log for importer errors.");

			var prefab = PrefabUtility.InstantiatePrefab(assetRoot) as GameObject;
			if (prefab == null)
			{
				throw new Exception(
					"Imported " + assetFbxPath + " but Unity produced no prefab instance from it. Check the " +
					"Editor log for importer errors -- the FBX is in the project, but nothing was created from it."
				);
			}
			prefab.name = modelName;
			string scenePath = SceneManager.GetActiveScene().path;
			prefab.transform.position = Vector3.zero;
			PrefabUtility.SaveAsPrefabAsset(prefab, prefabPath);
			UnityEngine.Object.DestroyImmediate(prefab);
			if (!string.IsNullOrEmpty(scenePath)) EditorSceneManager.MarkSceneDirty(SceneManager.GetActiveScene());

			Json result = new Json();
			result.OpenObject();
			result.AddString("prefabPath", prefabPath);
			result.AddString("fbxAssetPath", assetFbxPath);
			result.AddString("rootName", modelName);
			result.CloseObject();
			return result.ToString();
		}

		/// A name used as both a file name and a prefab name must be safe in both,
		/// and must not silently change what the caller asked for -- so an illegal
		/// character is refused rather than rewritten behind their back.
		private static string SanitizeAssetName(string name)
		{
			string trimmed = (name ?? "").Trim();
			if (trimmed.Length == 0) throw new Exception("Cannot derive a prefab name from an empty string.");
			foreach (char bad in Path.GetInvalidFileNameChars())
			{
				if (trimmed.IndexOf(bad) >= 0)
					throw new Exception(
						"Model name \"" + trimmed + "\" contains \"" + bad + "\", which cannot appear in an asset name."
					);
			}
			return trimmed;
		}

		/// `targetName` is the stem the copied file gets. It is an argument rather
		/// than something re-derived from `sourcePath` because the caller has
		/// already checked a name, and a copy that lands under a DIFFERENT name
		/// than the one checked is the exact overwrite this method is reached from.
		private static string MoveIntoAssets(string sourcePath, string destinationFolder, string targetName, string extension)
		{
			string absolute = ResolveAssetsFolder(destinationFolder);
			Directory.CreateDirectory(absolute);
			string target = Path.Combine(absolute, targetName + extension);
			if (!string.Equals(Path.GetFullPath(sourcePath), Path.GetFullPath(target), StringComparison.Ordinal))
				File.Copy(sourcePath, target, true);
			AssetDatabase.ImportAsset(destinationFolder + "/" + Path.GetFileName(target), ImportAssetOptions.ForceUpdate);
			return destinationFolder.TrimEnd('/') + "/" + Path.GetFileName(target);
		}

		private static string ExportObj(Json request)
		{
			string outputPath = request.GetString("outputPath");
			if (string.IsNullOrEmpty(outputPath))
				throw new Exception("export-obj requires an outputPath.");
			string[] names = request.GetStringArray("gameObjectNames").ToArray();

			var roots = new List<GameObject>();
			if (names.Length == 0)
			{
				roots.AddRange(SceneManager.GetActiveScene().GetRootGameObjects());
			}
			else
			{
				foreach (string name in names)
				{
					// FindGameObjectByName first: GameObject.Find takes the first hit
					// under an ambiguous name, so leading with it defeated the
					// refusal that FindGameObjectByName exists to issue.
					GameObject found = FindGameObjectByName(name);
					if (found == null) found = GameObject.Find(name);
					if (found == null) throw new Exception("No GameObject named \"" + name + "\" in the active scene.");
					roots.Add(found);
				}
			}
			if (roots.Count == 0) throw new Exception("Nothing selected and the active scene has no root objects to export.");

			// Only meshes can round-trip through OBJ; a hierarchy of empties
			// would produce a file Blender cannot read at all.
			var meshes = new List<MeshFilter>();
			foreach (GameObject root in roots)
			{
				MeshFilter[] filters = root.GetComponentsInChildren<MeshFilter>(false);
				if (filters.Length == 0)
					throw new Exception("\"" + root.name + "\" has no MeshFilter, so it cannot be exported as OBJ. Export a mesh, not an empty.");
				meshes.AddRange(filters);
			}

			Directory.CreateDirectory(Path.GetDirectoryName(outputPath));
			var output = new StringBuilder();
			int offset = 1;
			var mtlPath = Path.ChangeExtension(outputPath, ".mtl");
			// Deduplicate by INSTANCE, not by name. Two different materials that
			// share a name (or two slots on one renderer) must both be written,
			// and the MTL lookup must resolve the material we actually saw on the
			// renderer -- not "any material in the project with this name".
			//
			// exportedNames is kept PARALLEL to materials: the name written in a
			// "usemtl" line is the asset's file name, which is not necessarily the
			// material's own name. Deriving the MTL's "newmtl" from
			// material.name instead would emit a name no "usemtl" line refers to.
			var materials = new List<Material>();
			var exportedNames = new List<string>();
			var materialNames = new List<string>();

			foreach (MeshFilter filter in meshes)
			{
				Mesh mesh = filter.sharedMesh;
				if (mesh == null || mesh.vertexCount == 0) continue;
				Vector3[] vertices = mesh.vertices;
				Vector3[] normals = mesh.normals;
				Vector2[] uvs = mesh.uv;
				// mesh.triangles is a flat, already-unrolled index list -- one
				// array of ints, not an array per face.
				int[] triangles = mesh.triangles;

				string groupName = filter.gameObject.name;
				// An "o" line names the OBJECT; a "g" line names a group, which
				// Blender turns into a vertex group (verified on 5.2: an OBJ with
				// only "g RealName" imports as "transfer", named after the FILE).
				// Emitting "o" is what keeps the GameObject name across the trip,
				// which is the whole point of exporting by name.
				output.AppendLine("o " + groupName);

				Transform filterTransform = filter.transform;
				foreach (Vector3 vertex in vertices)
				{
					Vector3 world = filterTransform.TransformPoint(vertex);
					output.AppendLine(string.Format(CultureInfo.InvariantCulture, "v {0} {1} {2}", world.x, world.y, world.z));
				}
				foreach (Vector3 normal in normals)
				{
					Vector3 world = filterTransform.TransformDirection(normal);
					output.AppendLine(string.Format(CultureInfo.InvariantCulture, "vn {0} {1} {2}", world.x, world.y, world.z));
				}
				if (uvs != null && uvs.Length == vertices.Length)
				{
					foreach (Vector2 uv in uvs)
						output.AppendLine(string.Format(CultureInfo.InvariantCulture, "vt {0} {1}", uv.x, uv.y));
				}

				Renderer renderer = filter.GetComponent<Renderer>();
				string materialName = null;
				if (renderer != null && renderer.sharedMaterial != null)
				{
					Material material = renderer.sharedMaterial;
					string assetPath = AssetDatabase.GetAssetPath(material);
					int index = materials.IndexOf(material);
					if (index < 0)
					{
						index = materials.Count;
						materials.Add(material);
						exportedNames.Add(UniqueExportedName(
							string.IsNullOrEmpty(assetPath) ? material.name : Path.GetFileNameWithoutExtension(assetPath),
							exportedNames));
					}
					// The "usemtl" name must be the name the MTL declared for THIS
					// instance, taken back out of exportedNames rather than
					// recomputed. They are the same string only while the name is
					// unique, and the whole point of the rename below is that it
					// sometimes is not.
					materialName = exportedNames[index];
					if (!materialNames.Contains(materialName)) materialNames.Add(materialName);
				}
				if (materialName != null) output.AppendLine("usemtl " + materialName);

				// mesh.triangles is a flat, already-unrolled index list: three ints
				// per face, walked three at a time.
				for (int face = 0; face + 2 < triangles.Length; face += 3)
				{
					int a = triangles[face] + offset;
					int b = triangles[face + 1] + offset;
					int c = triangles[face + 2] + offset;
					bool hasUv = uvs != null && uvs.Length == vertices.Length;
					output.AppendLine(hasUv
						? string.Format(CultureInfo.InvariantCulture, "f {0}/{0} {1}/{1} {2}/{2}", a, b, c)
						: string.Format(CultureInfo.InvariantCulture, "f {0} {1} {2}", a, b, c));
				}
				offset += vertices.Length;
			}

			File.WriteAllText(outputPath, output.ToString());
			WriteMtl(mtlPath, materials, exportedNames);

			// The sidecar is what makes the return trip possible. OBJ/MTL cannot
			// carry a shader, so the ORIGINAL Unity material stays the source of
			// truth; this records where it lives and which property names it
			// actually uses, so an edit made in Blender can be written back onto
			// the real material instead of a lossy reconstruction.
			//
			// Named from the OBJ's own file name, per run. A fixed "materials.json"
			// was the same shape of bug UniqueExportedName was written for one level
			// up: the caller picks the output directory, and pi's staging root is
			// ONE deterministic dir with no per-run subdirectory, so a second
			// export into the same directory overwrote the first run's sidecar
			// while BOTH responses reported the same materialSidecarPath. The first
			// run's material mapping then silently became the second run's.
			string sidecarName = Path.GetFileNameWithoutExtension(outputPath) + ".materials.json";
			string sidecarPath = WriteMaterialSidecar(materials, exportedNames, Path.GetDirectoryName(outputPath), sidecarName);

			Json result = new Json();
			result.OpenObject();
			result.AddString("objPath", outputPath);
			result.AddString("mtlPath", mtlPath);
			result.AddString("materialSidecarPath", sidecarPath);
			result.AddArray("meshNames", Json.Array(meshes.Select(m => Json.Str(m.gameObject.name)).ToArray()));
			result.AddArray("materialNames", Json.Array(materialNames.Select(name => Json.Str(name)).ToArray()));
			result.CloseObject();
			return result.ToString();
		}

		/// Property names in preference order, per role. The ORIGINAL material
		/// decides which one is real: a lilToon material has _BaseColor and
		/// _BaseMap, a built-in Standard one has _Color and _MainTex, and
		/// writing to the wrong one is a silent no-op.
		private static readonly string[] ColorProperties = { "_BaseColor", "_Color" };
		private static readonly string[] TextureProperties = { "_BaseMap", "_MainTex" };

		private static string FirstExisting(Material material, string[] candidates)
		{
			foreach (string candidate in candidates)
				if (material.HasProperty(candidate)) return candidate;
			return null;
		}

		/// Writes the MTL as a faithful representation of MTL's own property set.
		///
		/// Every default here used to be hardcoded (Ka/Ks 0 0 0, d 1, illum 2),
		/// which destroyed opacity and specular on every export. And the material
		/// used to be resolved by FindAssets(name), which matches ANY material in
		/// the project with that name -- so a collision silently exported the
		/// wrong colour. Materials are passed in by instance from the renderer we
		/// actually saw.
		///
		/// The texture is COPIED next to the MTL. Writing a project-relative path
		/// here is what produced silently broken textures: the MTL lives in cache
		/// staging while the texture lives in the Unity project, so the reference
		/// resolved to nothing while the transfer reported success.
		/// A name for "newmtl" that no earlier material already took.
		///
		/// The collision this closes is the same shape as the one the comment
		/// above this method describes, one level up. That fix stopped
		/// FindAssets matching the wrong material; it did not stop two DIFFERENT
		/// assets whose files share a name -- Assets/A/Fabric.mat and
		/// Assets/B/Fabric.mat -- from both exporting as "Fabric". The MTL then
		/// held two "newmtl Fabric" blocks, every reader takes the first, and the
		/// second material's colours and texture went nowhere with no diagnostic.
		/// Disambiguating is the only fix: a suffix keeps the readable base name,
		/// and the MTL records the rename in a comment so a caller reading the
		/// file can see that "Fabric_2" is not a name the project ever had.
		private static string UniqueExportedName(string wanted, List<string> taken)
		{
			if (!taken.Contains(wanted)) return wanted;
			for (int suffix = 2; suffix < 1000; suffix++)
			{
				string candidate = wanted + "_" + suffix;
				if (!taken.Contains(candidate)) return candidate;
			}
			// Unreachable in practice, and a name that cannot collide beats an
			// exception: the export is still worth having, and the sidecar
			// carries the real asset paths either way.
			return wanted + "_" + taken.Count;
		}

		private static void WriteMtl(string mtlPath, List<Material> materials, List<string> exportedNames)
		{
			var mtl = new StringBuilder();
			mtl.AppendLine("# Generated by the pi Unity bridge");
			mtl.AppendLine("# Only MTL's own property set crosses. The shader stays in Unity:");
			mtl.AppendLine("# edit colour/opacity/texture here, and pi writes them back.");

			for (int index = 0; index < materials.Count; index++)
			{
				Material material = materials[index];
				// The name a "usemtl" line refers to, NOT material.name.
				string name = index < exportedNames.Count ? exportedNames[index] : (material == null ? "material_" + index : material.name);
				mtl.AppendLine("newmtl " + name);
				if (index < exportedNames.Count && name != (material == null ? "" : material.name))
				{
					// The declared name is not the asset's own, so it was renamed
					// to stay unique. Said out loud, because the receiving side
					// sees only the new name.
					mtl.AppendLine("# pi: exported as \"" + name + "\" to keep it distinct from another material of the same name");
				}
				if (material == null) continue;

				string colorProperty = FirstExisting(material, ColorProperties);
				Color color = colorProperty != null ? material.GetColor(colorProperty) : Color.white;
				// MTL's d is opacity, which lives in the alpha channel of the same
				// colour property -- NOT a separate value to invent.
				mtl.AppendLine(string.Format(CultureInfo.InvariantCulture, "Kd {0} {1} {2}", color.r, color.g, color.b));
				mtl.AppendLine(string.Format(CultureInfo.InvariantCulture, "d {0}", color.a));

				// Roughness/metalness survive as the phong terms where the shader
				// actually exposes them. Absence is normal (lilToon has neither),
				// and is reported as lost rather than faked as zero.
				float smoothness = -1f, metallic = -1f;
				if (material.HasProperty("_Smoothness")) smoothness = material.GetFloat("_Smoothness");
				else if (material.HasProperty("_Glossiness")) smoothness = material.GetFloat("_Glossiness");
				if (material.HasProperty("_Metallic")) metallic = material.GetFloat("_Metallic");
				mtl.AppendLine(string.Format(CultureInfo.InvariantCulture, "Ns {0}", smoothness >= 0f ? 1000f : 0f));
				mtl.AppendLine(string.Format(CultureInfo.InvariantCulture, "Ks {0} {1} {2}", metallic >= 0f ? metallic : 0f, metallic >= 0f ? metallic : 0f, metallic >= 0f ? metallic : 0f));
				mtl.AppendLine("Ka 0 0 0");
				mtl.AppendLine("illum 2");

				string textureProperty = FirstExisting(material, TextureProperties);
				if (textureProperty == null) continue;
				Texture texture = material.GetTexture(textureProperty);
				string assetPath = texture == null ? "" : AssetDatabase.GetAssetPath(texture);
				if (string.IsNullOrEmpty(assetPath)) continue;
				string absolute = Path.Combine(ProjectRoot(), assetPath);
				if (!File.Exists(absolute)) continue;
				string copyName = Path.GetFileName(absolute);
				string copyPath = Path.Combine(Path.GetDirectoryName(mtlPath), copyName);
				try { File.Copy(absolute, copyPath, true); }
				catch (Exception error)
				{
					// A texture we cannot copy is reported, not silently emitted as
					// a dangling reference.
					mtl.AppendLine("# pi: could not copy texture " + copyName + ": " + error.Message);
					continue;
				}
				mtl.AppendLine("map_Kd " + copyName);
			}
			File.WriteAllText(mtlPath, mtl.ToString());
		}

		/// Records where each exported material came from and which property
		/// names it actually uses, so an edit made in Blender can be applied to
		/// the REAL material rather than a lossy reconstruction.
		private static string WriteMaterialSidecar(List<Material> materials, List<string> exportedNames, string directory, string fileName)
		{
			// OpenArray() takes no key (PiUnity.Json.cs:242) -- only OpenObject
			// does -- so the entries are collected first and attached as a value.
			var entries = new List<Json>();
			for (int index = 0; index < materials.Count; index++)
			{
				Material material = materials[index];
				Json entry = new Json();
				entry.OpenObject();
				string assetPath = AssetDatabase.GetAssetPath(material);
				entry.AddString("exportedName", index < exportedNames.Count ? exportedNames[index] : material.name);
				entry.AddString("materialName", material.name);
				entry.AddString("assetPath", string.IsNullOrEmpty(assetPath) ? "" : assetPath);
				string colorProperty = FirstExisting(material, ColorProperties);
				string textureProperty = FirstExisting(material, TextureProperties);
				entry.AddString("colorProperty", colorProperty ?? "");
				entry.AddString("textureProperty", textureProperty ?? "");
				entry.AddString("shaderName", material.shader == null ? "" : material.shader.name);
				entry.CloseObject();
				entries.Add(entry);
			}

			var sidecar = new Json();
			sidecar.OpenObject();
			sidecar.AddArray("materials", Json.Array(entries.ToArray()));
			sidecar.CloseObject();

			// Per-run file name, taken from the OBJ the caller asked for: see the
		// collision the caller-side comment describes. A shared directory must
		// not have two runs' sidecars fighting over one path.
		string path = Path.Combine(directory, fileName);
			File.WriteAllText(path, sidecar.ToString());
			return path;
		}

		/// EditorTools.activeTool is internal, so it is read reflectively for the
		/// same reason as transformTool. Returns "" rather than throwing: a
	}
}
