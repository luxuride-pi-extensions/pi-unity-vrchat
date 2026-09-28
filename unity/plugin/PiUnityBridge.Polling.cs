// Part of the pi Unity bridge plugin. See PiUnityBridge.Core.cs for the
// protocol overview. The whole plugin is one assembly of partial classes, so
// Unity compiles it into Assembly-CSharp-Editor with no asmdef and no package
// dependencies, which is what lets it drop into any Unity 2021+ project.

// Polling, response writing, and request dispatch
// (C# snippet compilation lives in PiUnityBridge.Compiler.cs)
//
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
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
		private static void Poll()
		{
			try
			{
				TouchHeartbeat();
				if (EditorApplication.isCompiling) return;
				if (EditorApplication.timeSinceStartup < NextPollTime) return;
				NextPollTime = EditorApplication.timeSinceStartup + PollIntervalSeconds;

				string[] files = Directory.GetFiles(RequestsDir, "*.json");
				Array.Sort(files);
				foreach (string file in files)
				{
					string requestId = Path.GetFileNameWithoutExtension(file);
					string body;
					try
					{
						body = File.ReadAllText(file);
					}
					catch (Exception error)
					{
						WriteResponse(requestId, false, "Failed to read request: " + error.Message, null);
						continue;
					}

					try
					{
						File.Delete(file);
					}
					catch (Exception)
					{
						// Another process still has it open; the response write
						// below is idempotent per requestId, so losing the race
						// only risks one duplicate execution.
					}

					string resultJson = null;
					string errorMessage = null;
					try
					{
						Json request = Json.Parse(body);
						resultJson = Dispatch(request);
					}
					catch (Exception error)
					{
						errorMessage = error.Message;
					}

					if (resultJson != null) WriteResponse(requestId, true, null, resultJson);
					else WriteResponse(requestId, false, errorMessage ?? "Request handler returned nothing.", null);
				}
			}
			catch (Exception error)
			{
				// Never let a bridge bug take down the Editor.
				AppendLog("poll error: " + error);
			}
		}

		private static void TouchHeartbeat()
		{
			try
			{
				if (File.Exists(HeartbeatPath)) File.SetLastWriteTime(HeartbeatPath, DateTime.Now);
				else File.WriteAllText(HeartbeatPath, DateTime.Now.ToString("o", CultureInfo.InvariantCulture));
			}
			catch (Exception)
			{
				/* heartbeat is best-effort */
			}
		}

		private static void AppendLog(string message)
		{
			if (string.IsNullOrEmpty(EditorLogPath)) return;
			try
			{
				File.AppendAllText(EditorLogPath, message + "\n");
			}
			catch (Exception)
			{
				/* best-effort */
			}
		}

		// ------------------------------------------------------------- responses

		private static void WriteResponse(string requestId, bool ok, string error, string resultJson)
		{
			string payload = ok
				? "{\"ok\":true,\"result\":" + (resultJson ?? "null") + "}"
				: "{\"ok\":false,\"error\":" + Json.Quote(error) + "}";
			string target = Path.Combine(ResponsesDir, requestId + ".json");
			string temp = target + ".tmp";
			try
			{
				File.WriteAllText(temp, payload);
				// Atomic publish: pi may be reading this exact path.
				if (File.Exists(target)) File.Delete(target);
				File.Move(temp, target);
			}
			catch (Exception)
			{
				// Rename-over is not supported on every filesystem we might land
				// on, so fall back to a direct write rather than losing the reply.
				try { File.WriteAllText(target, payload); } catch (Exception) { }
			}
		}

		private static void OnCompilationFinished(object obj)
		{
			LastCompileError = "";
		}

		private static void SaveScenes()
		{
			try
			{
				if (!Application.isPlaying) EditorSceneManager.SaveOpenScenes();
			}
			catch (Exception error)
			{
				AppendLog("save failed: " + error.Message);
			}
		}
	}
}
