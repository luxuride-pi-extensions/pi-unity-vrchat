// Part of the pi Unity bridge plugin. See PiUnityBridge.Core.cs for the
// protocol overview. The whole plugin is one assembly of partial classes, so
// Unity compiles it into Assembly-CSharp-Editor with no asmdef and no package
// dependencies, which is what lets it drop into any Unity 2021+ project.

// Camera renders, viewport capture, transform tool
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
		private static string Render(Json request)
		{
			string outputPath = request.GetString("outputPath");
			int width = request.GetInt("width", 960);
			int height = request.GetInt("height", 540);
			string cameraName = request.GetString("cameraName");
			bool useActive = string.IsNullOrEmpty(cameraName);

			Camera camera = useActive ? null : FindInSceneByName<Camera>(cameraName);
			if (camera == null) camera = Camera.main;
			if (camera == null)
				throw new Exception("No camera available to render. Add a camera tagged MainCamera, or pass cameraName.");

			Directory.CreateDirectory(Path.GetDirectoryName(outputPath));
			RenderTexture target = RenderTexture.GetTemporary(width, height, 24, RenderTextureFormat.ARGB32);
			RenderTexture previousTarget = camera.targetTexture;
			RenderTexture previousActive = RenderTexture.active;
			try
			{
				camera.targetTexture = target;
				camera.Render();
				RenderTexture.active = target;
				Texture2D image = new Texture2D(width, height, TextureFormat.RGB24, false);
				image.ReadPixels(new Rect(0, 0, width, height), 0, 0);
				image.Apply();
				File.WriteAllBytes(outputPath, image.EncodeToPNG());
				UnityEngine.Object.DestroyImmediate(image);
			}
			finally
			{
				// Restore unconditionally: a throw between Render and write must
				// not leave the user's camera permanently bound to a temp RT.
				camera.targetTexture = previousTarget;
				RenderTexture.active = previousActive;
				RenderTexture.ReleaseTemporary(target);
			}

			Json result = new Json();
			result.OpenObject();
			result.AddString("outputPath", outputPath);
			result.AddNumber("width", width);
			result.AddNumber("height", height);
			result.AddString("cameraName", camera.name);
			result.AddBool("usedActiveCamera", useActive);
			result.CloseObject();
			return result.ToString();
		}

		private static string CaptureView(Json request)
		{
			string outputPath = request.GetString("outputPath");
			int width = request.GetInt("width", 960);
			int height = request.GetInt("height", 540);

			SceneView view = SceneView.lastActiveSceneView;
			if (view == null) throw new Exception("No Scene view is open, so there is nothing to capture.");
			if (view.camera == null) throw new Exception("The Scene view has no camera yet; try again after it finishes drawing.");

			Directory.CreateDirectory(Path.GetDirectoryName(outputPath));
			RenderTexture target = RenderTexture.GetTemporary(width, height, 24, RenderTextureFormat.ARGB32);
			RenderTexture previousTarget = view.camera.targetTexture;
			RenderTexture previousActive = RenderTexture.active;
			try
			{
				view.camera.targetTexture = target;
				view.camera.Render();
				RenderTexture.active = target;
				Texture2D image = new Texture2D(width, height, TextureFormat.RGB24, false);
				image.ReadPixels(new Rect(0, 0, width, height), 0, 0);
				image.Apply();
				File.WriteAllBytes(outputPath, image.EncodeToPNG());
				UnityEngine.Object.DestroyImmediate(image);
			}
			finally
			{
				view.camera.targetTexture = previousTarget;
				RenderTexture.active = previousActive;
				RenderTexture.ReleaseTemporary(target);
			}

			Json result = new Json();
			result.OpenObject();
			result.AddString("outputPath", outputPath);
			result.AddString("perspective", view.orthographic ? "ORTHO" : "PERSP");
			result.AddString("sceneViewCamera", view.camera.name);
			result.CloseObject();
			return result.ToString();
		}

		private static string SetTool(Json request)
		{
			string tool = (request.GetString("tool") ?? "").ToLowerInvariant();
			if (!SetTransformTool(tool))
			{
				// Two different failures share this path, and the user needs to be
				// able to tell them apart: a name we do not know, versus a Unity
				// build whose tool API moved. Reporting the first as the second
				// is what made this read as "move is not a tool".
				bool known = tool == "move" || tool == "rotate" || tool == "scale" || tool == "rect";
				string reason = GetEditorToolsType() == null
					? "This Unity build does not expose UnityEditor.EditorTools.EditorTools, so the transform tool cannot be driven from a script."
					: known
						? "This Unity build did not accept the tool; the tool type could not be resolved."
						: "Unknown transform tool \"" + tool + "\". Use move, rotate, scale, or rect.";
				throw new Exception(reason);
			}
			Tools.hidden = tool == "rect";

			Json result = new Json();
			result.OpenObject();
			result.AddString("tool", tool);
			result.AddString("activeTransformTool", GetActiveTransformToolName());
			result.AddArray("selectedGameObjects", Json.Array(Selection.gameObjects.Select(go => Json.Str(go.name)).ToArray()));
			result.CloseObject();
			return result.ToString();
		}
	}
}
