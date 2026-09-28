// Part of the pi Unity bridge plugin. See PiUnityBridge.Core.cs for the
// protocol overview. The whole plugin is one assembly of partial classes, so
// Unity compiles it into Assembly-CSharp-Editor with no asmdef and no package
// dependencies, which is what lets it drop into any Unity 2021+ project.

// Live session state
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
		private static string GetSessionContext()
		{
			Scene scene = SceneManager.GetActiveScene();
			string[] availableScenes = EditorSceneManager.GetSceneManagerSetup()
				.Select(part => part.path)
				.Where(path => !string.IsNullOrEmpty(path))
				.ToArray();

			GameObject[] selection = Selection.gameObjects;
			Json selected = Json.Array(selection.Select(go => Json.Str(go.name)).ToArray());
			string activeName = Selection.activeGameObject != null ? Selection.activeGameObject.name : "";

			SceneView sceneView = SceneView.lastActiveSceneView;
			Json result = new Json();
			result.OpenObject();
			result.AddString("projectPath", ProjectRoot());
			result.AddString("unityVersion", Application.unityVersion);
			result.AddBool("isPlaying", EditorApplication.isPlaying);
			result.AddBool("isCompiling", EditorApplication.isCompiling);

			result.OpenObject("scene");
			result.AddString("name", scene.name);
			result.AddString("path", scene.path);
			result.AddBool("isLoaded", scene.isLoaded);
			result.AddArray("availableScenes", Json.Array(availableScenes.Select(path => Json.Str(path)).ToArray()));
			result.CloseObject();

			result.OpenObject("selection");
			result.AddString("activeGameObject", activeName);
			result.AddArray("gameObjects", selected);
			result.AddNumber("count", selection.Length);
			result.CloseObject();

			result.OpenObject("viewport");
			result.AddBool("hasSceneView", sceneView != null);
			if (sceneView != null)
			{
				result.AddString("perspective", sceneView.orthographic ? "ORTHO" : "PERSP");
				result.AddString("cameraName", sceneView.camera != null ? sceneView.camera.name : "");
				result.AddNumber("pivotX", sceneView.pivot.x);
				result.AddNumber("pivotY", sceneView.pivot.y);
				result.AddNumber("pivotZ", sceneView.pivot.z);
			}
			result.CloseObject();

			result.OpenObject("tools");
			result.AddString("activeTransformTool", GetActiveTransformToolName());
			result.AddString("activeTool", GetActiveToolName());
			result.CloseObject();

			result.CloseObject();
			return result.ToString();
		}
	}
}
