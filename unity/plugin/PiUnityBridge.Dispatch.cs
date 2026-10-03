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
				case "compose-prefab":
					return ComposePrefab(request);
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

		/// How many Editor log lines `logTail: true` returns. Enough to show a
		/// package's complaint about the snippet's new object, small enough that
		/// it cannot dominate the response.
		private const int LOG_TAIL_LINES = 80;

		private static string HandleExecuteCSharp(Json request)
		{
			string code = request.GetString("code");
			bool saveAfter = request.GetBool("saveAfter");
			string label = request.GetString("label");
			// Opt-in, because the tail is the Editor log and the Editor log also
			// carries package noise from before this snippet ran. Off by default:
			// a caller who asked for their snippet's output should not receive
			// 200 lines of someone else's warning as well.
			bool logTail = request.GetBool("logTail");

			List<CapturedLog> logs;
			string log = CompileAndRun(code, out logs);
			if (saveAfter) SaveScenes();

			Json result = new Json();
			result.OpenObject();
			result.AddString("stdout", log);
			result.AddString("stderr", "");
			result.AddNumber("exitCode", 0);
			result.AddBool("changed", true);
			result.AddString("label", label ?? "");
			WriteLogArray(result, "logs", logs);
			if (logTail)
			{
				string[] tail = RecentEditorLogLines(LOG_TAIL_LINES);
				result.AddNumber("logTailLines", tail.Length);
				result.AddArray("logTail", tail);
			}
			result.CloseObject();
			return result.ToString();
		}

		/// The last few lines of the Editor log, for the case where the output a
		/// caller wants is not the snippet's own.
		///
		/// Read from the same file GetLog() reports, on the same tail-only
		/// reasoning: it is append-only for the whole Editor lifetime, so
		/// anything older is not about this run.
		private static string[] RecentEditorLogLines(int count)
		{
			string path = FindEditorLogPath();
			if (path == null || !File.Exists(path)) return new string[0];
			try
			{
				const int tailBytes = 64 * 1024;
				using (FileStream stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
				{
					long start = Math.Max(0, stream.Length - tailBytes);
					stream.Seek(start, SeekOrigin.Begin);
					byte[] buffer = new byte[stream.Length - start];
					int read = stream.Read(buffer, 0, buffer.Length);
					string text = new System.Text.UTF8Encoding(false).GetString(buffer, 0, read);
					string[] lines = text.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
					if (lines.Length <= count) return lines;
					string[] window = new string[count];
					Array.Copy(lines, lines.Length - count, window, 0, count);
					return window;
				}
			}
			catch (Exception)
			{
				// A log we cannot read is not a failed snippet; say nothing.
				return new string[0];
			}
		}
	}
}
