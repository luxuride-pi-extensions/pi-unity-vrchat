// Part of the pi Unity bridge plugin. See PiUnityBridge.Core.cs for the
// protocol overview. The whole plugin is one assembly of partial classes, so
// Unity compiles it into Assembly-CSharp-Editor with no asmdef and no package
// dependencies, which is what lets it drop into any Unity 2021+ project.

// Structured scene inspection and compile status
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
		private static string GetSceneInfo(Json request)
		{
			var categories = request.GetStringArray("categories");
			bool all = categories.Count == 0;
			bool want(string name) { return all || categories.Contains(name); }

			Scene scene = SceneManager.GetActiveScene();
			Json result = new Json();
			result.OpenObject();
			result.AddString("projectPath", ProjectRoot());
			result.AddString("activeScene", scene.path);
			result.AddString("unityVersion", Application.unityVersion);

			if (want("objects"))
			{
				GameObject[] roots = scene.GetRootGameObjects();
				Json objects = new Json();
				objects.OpenArray();
				foreach (GameObject root in roots)
				{
					GameObject[] children = root.GetComponentsInChildren<Transform>(true)
						.Select(child => child.gameObject)
						.ToArray();
					objects.OpenObject();
					objects.AddString("name", root.name);
					objects.AddString("parent", root.transform.parent != null ? root.transform.parent.name : "");
					objects.AddNumber("childCount", children.Length - 1);
					objects.AddBool("active", root.activeSelf);
					// Transform data, so "where is this object" is answered by the
					// same call that lists objects rather than needing a second
					// request per object. World AND local, because they answer
					// different questions: world is where it is in the scene, local
					// is how it sits inside its parent (which is what a parented
					// rig or a modular-avatar piece needs).
					Transform rootTransform = root.transform;
					objects.AddArray("position", VectorToJson(rootTransform.position));
					objects.AddArray("rotationEuler", VectorToJson(rootTransform.eulerAngles));
					objects.AddArray("localPosition", VectorToJson(rootTransform.localPosition));
					objects.AddArray("localRotationEuler", VectorToJson(rootTransform.localEulerAngles));
					objects.AddArray("localScale", VectorToJson(rootTransform.localScale));
					objects.AddString("tag", root.tag);
					objects.AddString("layer", LayerMask.LayerToName(root.layer));
					objects.AddBool("isPrefab", PrefabUtility.GetPrefabInstanceStatus(root) != PrefabInstanceStatus.NotAPrefab);
					objects.AddString(
						"prefabAssetPath",
						PrefabUtility.GetPrefabInstanceStatus(root) != PrefabInstanceStatus.NotAPrefab
							? PrefabUtility.GetPrefabAssetPathOfNearestInstanceRoot(root) ?? ""
							: "");
					objects.AddArray(
						"components",
						root.GetComponents<Component>()
							.Where(c => c != null)
							.Select(c => c.GetType().Name)
							.Distinct()
							.ToArray());
					objects.AddBool("hasMesh", root.GetComponentInChildren<MeshFilter>(true) != null);
					objects.AddBool("hasRenderer", root.GetComponentInChildren<Renderer>(true) != null);
					objects.CloseObject();
				}
				objects.CloseArray();
				result.AddValue("objects", objects);
			}

			if (want("materials"))
			{
				Json materials = new Json();
				materials.OpenArray();
				foreach (Material material in AssetDatabase.FindAssets("t:Material")
					.Select(AssetDatabase.GUIDToAssetPath)
					.Select(AssetDatabase.LoadAssetAtPath<Material>)
					.Where(m => m != null)
					.Distinct()
					.OrderBy(m => m.name, StringComparer.Ordinal))
				{
					string path = AssetDatabase.GetAssetPath(material);
					materials.OpenObject();
					materials.AddString("name", material.name);
					materials.AddString("assetPath", path);
					materials.AddString("shader", material.shader != null ? material.shader.name : "");
					if (material.HasProperty("_BaseColor"))
					{
						Color color = material.GetColor("_BaseColor");
						// Anonymous array: an overload taking a bare key did not exist.
						materials.OpenArray();
						materials.Add(color.r);
						materials.Add(color.g);
						materials.Add(color.b);
						materials.Add(color.a);
						materials.CloseArray();
					}
					materials.AddString("renderQueue", material.renderQueue.ToString(CultureInfo.InvariantCulture));
					materials.CloseObject();
				}
				materials.CloseArray();
				result.AddValue("materials", materials);
			}

			if (want("cameras"))
			{
				Json cameras = new Json();
				cameras.OpenArray();
				foreach (Camera camera in UnityEngine.Object.FindObjectsOfType<Camera>())
				{
					cameras.OpenObject();
					cameras.AddString("name", camera.name);
					cameras.AddString("gameObject", camera.gameObject.name);
					cameras.AddBool("orthographic", camera.orthographic);
					cameras.AddNumber("fieldOfView", camera.fieldOfView);
					cameras.AddNumber("nearClipPlane", camera.nearClipPlane);
					cameras.AddNumber("farClipPlane", camera.farClipPlane);
					cameras.AddNumber("depth", camera.depth);
					cameras.AddString(
						"position",
						string.Format(CultureInfo.InvariantCulture, "{0},{1},{2}", camera.transform.position.x, camera.transform.position.y, camera.transform.position.z));
					cameras.AddString(
						"rotationEuler",
						string.Format(CultureInfo.InvariantCulture, "{0},{1},{2}", camera.transform.eulerAngles.x, camera.transform.eulerAngles.y, camera.transform.eulerAngles.z));
					cameras.CloseObject();
				}
				cameras.CloseArray();
				result.AddValue("cameras", cameras);
			}

			if (want("lights"))
			{
				Json lights = new Json();
				lights.OpenArray();
				foreach (Light light in UnityEngine.Object.FindObjectsOfType<Light>())
				{
					lights.OpenObject();
					lights.AddString("name", light.name);
					lights.AddString("type", light.type.ToString());
					lights.AddNumber("intensity", light.intensity);
					lights.AddString("color", ColorToString(light.color));
					lights.AddString("shadows", light.shadows.ToString());
					lights.CloseObject();
				}
				lights.CloseArray();
				result.AddValue("lights", lights);
			}

			if (want("renderSettings"))
			{
				result.OpenObject("renderSettings");
				result.AddString("renderPipeline", UnityEngine.Rendering.GraphicsSettings.currentRenderPipeline != null
					? UnityEngine.Rendering.GraphicsSettings.currentRenderPipeline.GetType().Name
					: "BuiltIn");
				result.AddString("colorSpace", QualitySettings.activeColorSpace.ToString());
				result.AddNumber("antiAliasing", QualitySettings.antiAliasing);
				result.AddNumber("qualityLevel", QualitySettings.GetQualityLevel());
				result.CloseObject();
			}

			if (want("packages"))
			{
				Json packages = new Json();
				packages.OpenArray();
				foreach (var package in UnityEditor.PackageManager.PackageInfo.GetAllRegisteredPackages().OrderBy(p => p.name, StringComparer.Ordinal))
				{
					packages.OpenObject();
					packages.AddString("name", package.name);
					packages.AddString("version", package.version);
					packages.CloseObject();
				}
				packages.CloseArray();
				result.AddValue("packages", packages);
			}

			if (want("assets"))
			{
				// Everything under Assets/, so a worker can find where a material
				// or model actually lives before editing it.
				Json assets = new Json();
				assets.OpenArray();
				foreach (string guid in AssetDatabase.FindAssets("", new[] { "Assets" }))
				{
					string path = AssetDatabase.GUIDToAssetPath(guid);
					if (string.IsNullOrEmpty(path)) continue;
					var type = AssetDatabase.GetMainAssetTypeAtPath(path);
					if (type == typeof(MonoScript)) continue;
					assets.OpenObject();
					assets.AddString("path", path);
					assets.AddString("type", type != null ? type.Name : "");
					assets.CloseObject();
				}
				assets.CloseArray();
				result.AddValue("assets", assets);
			}

			if (want("compileStatus"))
			{
				result.AddValue("compileStatus", CompileStatusJson());
			}

			result.CloseObject();
			return result.ToString();
		}

		private static string CompileStatus()
		{
			return CompileStatusJson().ToString();
		}

		private static Json CompileStatusJson()
		{
			Json status = new Json();
			status.OpenObject();
			status.AddBool("isCompiling", EditorApplication.isCompiling);
			status.AddString("lastError", LastCompileError);
			status.AddString("projectPath", ProjectRoot());
			status.CloseObject();
			return status;
		}

		/// Where the logs actually are.
		///
		/// Reporting a path that does not exist is worse than reporting nothing:
		/// a caller who is told "the log is at X" opens X, finds nothing, and
		/// concludes there is no log rather than that the path was wrong. So each
		/// candidate is probed and `exists` says so.
		///
		/// The Editor's own log is NOT at <project>/Logs/Editor.log. That is the
		/// *player* log location. The Editor writes to the XDG config home, so
		/// which file is real depends on how the Editor was launched: a Hub
		/// child inherits the Hub's sandbox XDG_CONFIG_HOME, and pi points
		/// XDG_CONFIG_HOME at the same sandbox so the licence resolves (see
		/// findLicensedConfigHome in launch.ts). Either way the current process
		/// can be asked directly, which beats guessing from a path.
		private static string GetLog()
		{
			Json logs = new Json();
			logs.OpenArray();
			AddLogCandidate(logs, EditorLogPath, "pi bridge log (C# side only; Debug.Log does not reach it)");
			AddLogCandidate(logs, CurrentEditorLogPath(), "Unity Editor log for this process");
			AddLogCandidate(logs, Path.Combine(ProjectRoot(), "Logs", "Editor.log"), "player log");
			logs.CloseArray();

			Json result = new Json();
			result.OpenObject();
			result.AddArray("logs", logs);
			result.CloseObject();
			return result.ToString();
		}

		private static void AddLogCandidate(Json array, string path, string role)
		{
			Json entry = new Json();
			entry.OpenObject();
			entry.AddString("path", path ?? "");
			entry.AddString("role", role);
			entry.AddBool("exists", !string.IsNullOrEmpty(path) && File.Exists(path));
			entry.CloseObject();
			array.AddElement(entry);
		}

		/// Ask the running Editor where it writes its log, rather than deriving
		/// it. Returns "" when this Unity version has no such API.
		private static string CurrentEditorLogPath()
		{
			try
			{
				System.Reflection.MethodInfo method = typeof(UnityEditor.EditorApplication).GetMethod(
					"GetCurrentLogFilePath",
					System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic |
					System.Reflection.BindingFlags.Static,
					null, Type.EmptyTypes, null);
				if (method == null) return "";
				return method.Invoke(null, null) as string ?? "";
			}
			catch (Exception)
			{
				return "";
			}
		}
	}
}
