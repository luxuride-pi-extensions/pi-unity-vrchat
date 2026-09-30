// Part of the pi Unity bridge plugin. See PiUnityBridge.Core.cs for the
// protocol overview. The whole plugin is one assembly of partial classes, so
// Unity compiles it into Assembly-CSharp-Editor with no asmdef and no package
// dependencies, which is what lets it drop into any Unity 2021+ project.

// Prefab composition: place a prefab asset into the scene, and read a prefab
// asset without instantiating it.
//
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace PiUnity
{
	public static partial class Bridge
	{
		/// Instantiates a prefab asset into the active scene and places it in one
		/// call.
		///
		/// Instantiation and placement are together deliberately. Split across two
		/// calls, the caller has to name the object it just made -- which is the
		/// one step that cannot be done reliably, because a prefab's children
		/// repeat the same names in every instance, so a bare name is ambiguous
		/// the moment the asset is placed twice.
		private static string InstantiatePrefabAsset(Json request)
		{
			string prefabPath = request.GetString("prefabPath");
			if (string.IsNullOrEmpty(prefabPath))
				throw new Exception("instantiate-prefab requires a prefabPath.");

			var asset = AssetDatabase.LoadAssetAtPath<GameObject>(prefabPath);
			if (asset == null)
				throw new Exception(
					"No prefab asset at \"" + prefabPath + "\". Call unity_scene_info { categories: [\"assets\"] } " +
					"for the real paths -- a path is asset-relative, so \"Assets/...\", not an absolute path."
				);

			GameObject parent = null;
			string parentName = request.GetString("parent");
			if (!string.IsNullOrEmpty(parentName))
			{
				parent = FindGameObjectByName(parentName);
				if (parent == null)
					throw new Exception("No GameObject named or pathed \"" + parentName + "\" in the active scene.");
			}

			// PrefabUtility.InstantiatePrefab, NOT Object.Instantiate. The latter
			// copies the hierarchy and leaves a plain GameObject that merely looks
			// like the prefab: the connection to the asset is gone, so a later edit
			// to the asset does not reach this instance and PrefabUtility cannot
			// report its overrides.
			var instance = PrefabUtility.InstantiatePrefab(asset, parent != null ? parent.scene : SceneManager.GetActiveScene()) as GameObject;
			if (instance == null)
			{
				throw new Exception(
					"Unity produced no instance from \"" + prefabPath + "\". A prefab whose root carries no renderable " +
					"component still instantiates; this usually means the asset at that path is not a prefab."
				);
			}

			Undo.RegisterCreatedObjectUndo(instance, "pi instantiate prefab");
			if (parent != null) instance.transform.SetParent(parent.transform, false);

			string instanceName = request.GetString("name");
			if (!string.IsNullOrEmpty(instanceName)) instance.name = instanceName;
			else instance.name = NextAvailableName(asset.name);

			instance.transform.position = ReadVector3(request, "position", Vector3.zero);
			instance.transform.eulerAngles = ReadVector3(request, "rotation", Vector3.zero);
			instance.transform.localScale = ReadVector3(request, "scale", Vector3.one);

			// A rename or a reparent on a fresh instance is an OVERRIDE, and an
			// override with nothing recorded as its base is what makes the next
			// Apply silently discard it.
			PrefabUtility.RecordPrefabInstancePropertyModifications(instance.transform);
			PrefabUtility.RecordPrefabInstancePropertyModifications(instance);
			EditorUtility.SetDirty(instance);

			Scene scene = instance.scene;
			if (scene.IsValid() && !scene.isDirty) EditorSceneManager.MarkSceneDirty(scene);

			Json result = new Json();
			result.OpenObject();
			result.AddString("gameObject", instance.name);
			result.AddString("path", PathInScene(instance));
			result.AddString("prefabAssetPath", prefabPath);
			result.AddString("scene", scene.path);
			result.AddArray("position", VectorToJson(instance.transform.position));
			result.AddArray("rotationEuler", VectorToJson(instance.transform.eulerAngles));
			result.AddArray("localScale", VectorToJson(instance.transform.localScale));
			// The child inventory is what the caller would otherwise have to
			// discover with a second call -- and it is the call that fails when
			// names repeat.
			result.AddValue("children", DescribeChildren(instance.transform, 0));
			result.AddValue("bounds", DescribeInstanceBounds(instance));
			result.CloseObject();
			return result.ToString();
		}

		/// Reads a prefab ASSET: its hierarchy, components, materials and the
		/// bounds its geometry would occupy once placed.
		///
		/// This is the call that makes placing a prefab deliberate rather than a
		/// guess. Instantiating to find out what a prefab contains leaves a
		/// half-built scene behind when the answer is "not what I wanted".
		private static string GetPrefabInfo(Json request)
		{
			string prefabPath = request.GetString("prefabPath");
			if (string.IsNullOrEmpty(prefabPath))
				throw new Exception("prefab-info requires a prefabPath.");

			var asset = AssetDatabase.LoadAssetAtPath<GameObject>(prefabPath);
			if (asset == null)
			{
				// An FBX is a model asset, not a prefab, and pi's own import-fbx
				// leaves one of each in the folder. Saying which it is saves a
				// round trip through the asset listing.
				var model = AssetDatabase.LoadAssetAtPath<GameObject>(prefabPath);
				string type = model != null ? "a model asset (FBX/OBJ), not a prefab" : "not a loadable asset";
				throw new Exception(prefabPath + " is " + type + ". List assets with unity_scene_info { categories: [\"assets\"] }.");
			}

			var renderer = asset.GetComponentInChildren<Renderer>(true);
			var skinned = asset.GetComponentInChildren<SkinnedMeshRenderer>(true);
			int meshCount = asset.GetComponentsInChildren<MeshFilter>(true).Count(f => f.sharedMesh != null)
				+ asset.GetComponentsInChildren<SkinnedMeshRenderer>(true).Count(r => r.sharedMesh != null);

			Json result = new Json();
			result.OpenObject();
			result.AddString("prefabPath", prefabPath);
			result.AddString("rootName", asset.name);
			result.AddNumber("meshCount", meshCount);
			result.AddNumber("childCount", asset.GetComponentsInChildren<Transform>(true).Length - 1);
			result.AddArray("localScale", VectorToJson(asset.transform.localScale));
			result.AddBool("hasAnimator", asset.GetComponentInChildren<Animator>(true) != null);
			result.AddValue("bounds", DescribeAssetBounds(asset, renderer));
			result.AddValue("materials", DescribePrefabMaterials(asset));
			result.AddValue("children", DescribeChildren(asset.transform, 0));
			result.CloseObject();
			return result.ToString();
		}

		/// Every material the prefab actually references, including ones embedded
		/// in the source FBX.
		///
		/// `unity_scene_info { categories: ["materials"] }` uses
		/// FindAssets("t:Material"), which cannot see a material packed inside a
		/// model asset -- so for a transferred model it reports a project with no
		/// materials, which reads as "the materials were lost" rather than "they
		/// are somewhere FindAssets does not look".
		private static Json DescribePrefabMaterials(GameObject asset)
		{
			Json materials = new Json();
			materials.OpenArray();
			var seen = new List<string>();
			string assetPath = AssetDatabase.GetAssetPath(asset);

			foreach (Renderer renderer in asset.GetComponentsInChildren<Renderer>(true))
			{
				foreach (Material material in renderer.sharedMaterials)
				{
					if (material == null) continue;
					string path = AssetDatabase.GetAssetPath(material);
					string key = string.IsNullOrEmpty(path) ? material.name : path;
					if (seen.Contains(key)) continue;
					seen.Add(key);

					materials.OpenObject();
					materials.AddString("name", material.name);
					// Empty means the material lives INSIDE the model asset, which is
					// a different kind of problem from a missing reference.
					materials.AddString("assetPath", path);
					materials.AddString("parentAsset", path == assetPath ? assetPath : "");
					materials.AddString("shader", material.shader == null ? "" : material.shader.name);
					string colorProperty = FirstExisting(material, ColorProperties);
					if (colorProperty != null) materials.AddArray("color", ColorToJson(material.GetColor(colorProperty)));
					materials.CloseObject();
				}
			}
			materials.CloseArray();
			return materials;
		}

		/// Bounded recursive child listing, as JSON.
		private static Json DescribeChildren(Transform parent, int depth)
		{
			Json children = new Json();
			children.OpenArray();
			foreach (Transform child in parent)
			{
				children.OpenObject();
				children.AddString("name", child.name);
				children.AddString("path", PathInScene(child.gameObject));
				children.AddArray("localPosition", VectorToJson(child.localPosition));
				children.AddArray("localScale", VectorToJson(child.localScale));
				children.AddNumber("childCount", child.childCount);
				children.AddArray(
					"components",
					child.gameObject.GetComponents<Component>()
						.Where(c => c != null)
						.Select(c => c.GetType().Name)
						.Distinct()
						.ToArray());
				// A mesh with no vertices renders as nothing, so "has a MeshFilter"
				// is not evidence the geometry survived.
				MeshFilter filter = child.GetComponent<MeshFilter>();
				if (filter != null && filter.sharedMesh != null) children.AddNumber("vertexCount", filter.sharedMesh.vertexCount);
				if (depth < kChildDescribeDepth) children.AddValue("children", DescribeChildren(child, depth + 1));
				else if (child.childCount > 0) children.AddString("childrenTruncatedAt", kChildDescribeDepth.ToString());
				children.CloseObject();
			}
			children.CloseArray();
			return children;
		}

		private const int kChildDescribeDepth = 6;

		/// A name no scene object already holds, so a second instance of the same
		/// prefab is still addressable by name.
		private static string NextAvailableName(string baseName)
		{
			if (FindGameObjectByName(baseName) == null) return baseName;
			for (int suffix = 2; suffix < 10000; suffix++)
			{
				string candidate = baseName + " (" + suffix + ")";
				if (FindGameObjectByName(candidate) == null) return candidate;
			}
			return baseName + " (" + Guid.NewGuid().ToString("N").Substring(0, 6) + ")";
		}

		/// World bounds of everything the instance renders, which is the only
		/// number that answers "is this prefab the right size where I put it".
		///
		/// Skinned renderers report a bounds volume that can exceed the mesh at
		/// rest, so their figure is labelled rather than trusted as a size.
		private static Json DescribeInstanceBounds(GameObject instance)
		{
			Json bounds = new Json();
			bounds.OpenObject();
			var renderers = instance.GetComponentsInChildren<Renderer>(true).Where(r => r.enabled).ToArray();
			if (renderers.Length == 0)
			{
				bounds.AddString("state", "no enabled renderer, so there is nothing to measure");
				bounds.CloseObject();
				return bounds;
			}
			Bounds world = renderers[0].bounds;
			foreach (Renderer renderer in renderers.Skip(1)) world.Encapsulate(renderer.bounds);
			bounds.AddString("state", renderers.Any(r => r is SkinnedMeshRenderer)
				? "includes a skinned bounds volume, which can read larger than the mesh at rest"
				: "renderer bounds");
			bounds.AddArray("size", VectorToJson(world.size));
			bounds.AddArray("center", VectorToJson(world.center));
			bounds.CloseObject();
			return bounds;
		}

		/// The same measurement for an uninstantiated prefab.
		///
		/// A prefab asset's renderers report bounds in the asset's own local
		/// space, so the numbers are what the geometry occupies -- not what it
		/// will occupy once placed.
		private static Json DescribeAssetBounds(GameObject asset, Renderer renderer)
		{
			Json bounds = new Json();
			bounds.OpenObject();
			if (renderer == null)
			{
				bounds.AddString("state", "no renderer, so the prefab has no measurable geometry");
				bounds.CloseObject();
				return bounds;
			}
			Bounds local = renderer.bounds;
			foreach (Renderer each in asset.GetComponentsInChildren<Renderer>(true)) local.Encapsulate(each.bounds);
			bounds.AddString("state", renderer is SkinnedMeshRenderer
				? "includes a skinned bounds volume, which can read larger than the mesh at rest"
				: "asset-local renderer bounds, before any placement");
			bounds.AddArray("size", VectorToJson(local.size));
			bounds.AddArray("center", VectorToJson(local.center));
			bounds.CloseObject();
			return bounds;
		}

		private static Vector3 ReadVector3(Json request, string key, Vector3 fallback)
		{
			string[] parts = SplitNumbers(request.GetString(key) ?? "");
			return parts.Length >= 3
				? new Vector3(Pf(parts[0]), Pf(parts[1]), Pf(parts[2]))
				: fallback;
		}

		private static Json ColorToJson(Color color)
		{
			Json json = new Json();
			json.OpenArray();
			json.Add(color.r);
			json.Add(color.g);
			json.Add(color.b);
			json.Add(color.a);
			json.CloseArray();
			return json;
		}
	}
}