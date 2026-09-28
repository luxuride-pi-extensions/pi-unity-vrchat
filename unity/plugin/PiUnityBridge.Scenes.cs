// Part of the pi Unity bridge plugin. See PiUnityBridge.Core.cs for the
// protocol overview. The whole plugin is one assembly of partial classes, so
// Unity compiles it into Assembly-CSharp-Editor with no asmdef and no package
// dependencies, which is what lets it drop into any Unity 2021+ project.

// Opening and reloading scenes
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
		private static string OpenScene(Json request)
		{
			string scenePath = request.GetString("scenePath");
			if (string.IsNullOrEmpty(scenePath))
				throw new Exception("open-scene requires a scenePath.");
			if (!File.Exists(scenePath))
				throw new Exception("Scene file does not exist: " + scenePath);

			Scene current = SceneManager.GetActiveScene();
			if (current.path == scenePath)
			{
				Json same = new Json();
				same.OpenObject();
				same.AddBool("switched", false);
				same.AddString("scenePath", scenePath);
				same.CloseObject();
				return same.ToString();
			}

			// Same guard as the Blender bridge: a switch must not silently eat
			// unsaved work the user made by hand in the editor.
			if (current.isLoaded && !string.IsNullOrEmpty(current.path) && EditorSceneManager.GetActiveScene().isDirty)
			{
				throw new Exception(
					"The open scene \"" + current.path + "\" has unsaved changes in the Unity Editor. " +
					"Save or discard them before pi switches to \"" + scenePath + "\".");
			}

			Scene opened = EditorSceneManager.OpenScene(scenePath, OpenSceneMode.Single);
			Json result = new Json();
			result.OpenObject();
			result.AddBool("switched", true);
			result.AddString("scenePath", opened.path);
			result.CloseObject();
			return result.ToString();
		}

		private static string ReloadScene()
		{
			Scene current = SceneManager.GetActiveScene();
			string path = current.path;
			bool discarded = current.isDirty;
			if (string.IsNullOrEmpty(path))
				throw new Exception("No scene is currently open, so there is nothing to reload.");

			// A reload is an explicit discard (same-path revert case), so unlike
			// OpenScene it does not refuse: it just reports what it dropped.
			EditorSceneManager.OpenScene(path, OpenSceneMode.Single);
			EditorApplication.delayCall += () => SceneView.RepaintAll();

			Json result = new Json();
			result.OpenObject();
			result.AddString("scenePath", path);
			result.AddBool("discardedUnsavedChanges", discarded);
			result.CloseObject();
			return result.ToString();
		}
	}
}
