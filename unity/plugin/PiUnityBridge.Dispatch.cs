// Part of the pi Unity bridge plugin. See PiUnityBridge.Core.cs for the
// protocol overview. The whole plugin is one assembly of partial classes, so
// Unity compiles it into Assembly-CSharp-Editor with no asmdef and no package
// dependencies, which is what lets it drop into any Unity 2021+ project.

// Request dispatch
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
		private static string Dispatch(Json request)
		{
			string type = request.GetString("type");
			switch (type)
			{
				case "ping":
					// Json.Object builds a Json; every handler returns the string form.
					return Json.Object(new Json[] { new Json().Add("pong", true) }).ToString();
				case "get-session-context":
					return GetSessionContext();
				case "get-scene-info":
					return GetSceneInfo(request);
				case "get-bones":
					return GetBones(request);
				case "inspect":
					return Inspect(request);
				case "set-property":
					return SetProperty(request);
				case "set-transform":
					return SetTransform(request);
				case "vrc":
					return VrcAction(request);
				case "world":
					return WorldAction(request);
				case "open-scene":
					return OpenScene(request);
				case "reload-scene":
					return ReloadScene();
				case "render":
					return Render(request);
				case "capture-view":
					return CaptureView(request);
				case "set-tool":
					return SetTool(request);
				case "execute-csharp":
					return HandleExecuteCSharp(request);
				case "import-fbx":
					return ImportFbx(request);
				case "instantiate-prefab":
					return InstantiatePrefabAsset(request);
				case "prefab-info":
					return GetPrefabInfo(request);
				case "geometry-probe":
					// Read-only, so it is safe while another request holds the
					// Editor: it never writes, dirties, saves or claims an
					// iteration.
					return GeometryProbe(request);
				case "export-obj":
					return ExportObj(request);
				case "find-source-models":
					return FindSourceModels(request);
				case "export-source-models":
					return ExportSourceModels(request);
				case "get-log":
					return GetLog();
				case "compile-status":
					return CompileStatus();
				default:
					throw new Exception("Unknown Unity bridge request type: " + type);
			}
		}

		private static string HandleExecuteCSharp(Json request)
		{
			string code = request.GetString("code");
			bool saveAfter = request.GetBool("saveAfter");
			string label = request.GetString("label");

			string log = CompileAndRun(code);
			if (saveAfter) SaveScenes();

			Json result = new Json();
			result.OpenObject();
			result.AddString("stdout", log);
			result.AddString("stderr", "");
			result.AddNumber("exitCode", 0);
			result.AddBool("changed", true);
			result.AddString("label", label ?? "");
			result.CloseObject();
			return result.ToString();
		}
	}
}
