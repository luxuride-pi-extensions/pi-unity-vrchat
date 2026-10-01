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

		/// The script compile errors the Editor logged most recently.
		///
		/// Two traps make this less obvious than it looks. EditorApplication
		/// .isCompiling is false both after a compile that succeeded and after
		/// one that failed hard, so a check that only reads it reports "idle"
		/// for a project whose scripts never compiled -- which is exactly when
		/// the caller most needs to be told. And CompilationPipeline
		/// .compilationFinished does NOT hand over diagnostics: its object is
		/// Bee.BeeDriver.ActiveBuild, a compile job with no messages on it.
		/// So the messages are read from the Editor log, which is where the
		/// compiler already wrote them.
		///
		/// A tail rather than the whole log, and never a remembered byte offset.
		/// The log is append-only across the whole Editor lifetime, so scanning
		/// it whole reports an error fixed yesterday as if it were live; a tail
		/// is the compiler's most recent word. A cached offset would be worse
		/// still: a script compile ends in a domain reload, which discards
		/// statics, so the offset resets to zero on the one read that matters.
		public static List<string> CompileErrorMessages()
		{
			var errors = new List<string>();
			string path = FindEditorLogPath();
			if (path == null) return errors;

			try
			{
				const int tailBytes = 256 * 1024;
				using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
				{
					stream.Seek(Math.Max(0, stream.Length - tailBytes), SeekOrigin.Begin);
					using (var reader = new StreamReader(stream))
					{
						string line;
						while ((line = reader.ReadLine()) != null)
						{
							var match = CompileErrorPattern.Match(line.TrimEnd());
							if (!match.Success) continue;
							errors.Add(match.Groups["file"].Value + "(" + match.Groups["line"].Value + "," + match.Groups["col"].Value + "): error "
								+ match.Groups["code"].Value + ": " + match.Groups["msg"].Value);
						}
					}
				}
			}
			catch (Exception error)
			{
				errors.Add("the Editor log could not be read: " + error.Message);
			}

			// One compile reports the same error several times over, and a fixed
			// error keeps its last line while a file still broken elsewhere adds
			// a new one, so the newest line per file is the current state.
			var latest = errors.GroupBy(x => x.Split('(')[0]).Select(g => g.Last()).ToList();

			// The log is append-only, so its last error can be one that has since
			// been fixed -- a stale line read as live is worse than no feedback at
			// all, because it sends the caller to edit a file that is already
			// right. Unity only rewrites an output assembly when its compile
			// succeeds, so a source file older than the assembly that holds it has
			// been rebuilt clean since the error was written.
			string projectRoot = Path.GetDirectoryName(Application.dataPath);
			var live = new List<string>();
			foreach (string error in latest)
			{
				string relative = error.Split('(')[0];
				string absolute = Path.Combine(projectRoot ?? "", relative);
				if (!File.Exists(absolute)) continue;                        // the file is gone, so the error is
				if (AssemblyFor(relative, projectRoot) is DateTime built     // the compiler's stale memory
					&& File.GetLastWriteTime(absolute) <= built) continue;
				live.Add(error);
			}
			return live;
		}

		/// When the assembly that would contain a source file was last built.
		///
		/// Null when there is no output to compare against, which leaves the
		/// caller treating the error as live -- the safe direction, since a real
		/// compile error reported when it is gone costs one glance and a missed
		/// one costs a build that cannot succeed.
		private static DateTime? AssemblyFor(string projectRelativeSource, string projectRoot)
		{
			if (string.IsNullOrEmpty(projectRoot)) return null;
			// A file under any "Editor" folder compiles into the editor assembly.
			bool editorSide = projectRelativeSource.Split('/').Any(part => part == "Editor");
			string dll = Path.Combine(projectRoot, "Library/ScriptAssemblies/Assembly-CSharp"
				+ (editorSide ? "-Editor" : "") + ".dll");
			return File.Exists(dll) ? File.GetLastWriteTime(dll) : (DateTime?)null;
		}

		/// Scripts on disk that no output assembly accounts for.
		///
		/// The error log cannot answer "has this project ever compiled": a
		/// project whose every script is broken logs its errors once and then
		/// logs nothing again, so the log goes quiet and an empty read looks
		/// like success. The build output does answer it -- Unity only
		/// rewrites Assembly-CSharp.dll when a compile succeeds, so a script
		/// file newer than that dll is a script that never built.
		public static List<string> ScriptsNeedingCompile()
		{
			var stale = new List<string>();
			string projectRoot = Path.GetDirectoryName(Application.dataPath);
			if (string.IsNullOrEmpty(projectRoot)) return stale;

			var assemblies = new[]
			{
				Path.Combine(projectRoot, "Library/ScriptAssemblies/Assembly-CSharp.dll"),
				Path.Combine(projectRoot, "Library/ScriptAssemblies/Assembly-CSharp-Editor.dll")
			};
			var built = assemblies.Where(File.Exists).Select(File.GetLastWriteTime).ToList();
			if (built.Count == 0)
			{
				stale.Add("(the project has never produced an Assembly-CSharp.dll, so nothing in it has compiled)");
				return stale;
			}
			DateTime newest = built.Max();

			string assets = Path.Combine(projectRoot, "Assets");
			if (!Directory.Exists(assets)) return stale;
			foreach (string file in Directory.GetFiles(assets, "*.cs", SearchOption.AllDirectories))
			{
				if (File.GetLastWriteTime(file) <= newest) continue;
				stale.Add("Assets/" + file.Substring(assets.Length + 1).Replace('\\', '/') + "  (changed " + File.GetLastWriteTime(file).ToString("HH:mm:ss") + ")");
			}
			return stale;
		}

		/// Where the Editor writes its log.
		///
		/// The obvious candidate, Application.dataPath's parent, holds nothing
		/// on a flatpak install, and so does the directory above
		/// EditorApplication.applicationPath -- on this machine applicationPath
		/// is a launcher script, not the real binary, so its install directory
		/// is not where the log went. The portable location is the one Unity
		/// itself resolves, and it is reachable: SpecialFolder.ApplicationData
		/// is the config root it chose, with "unity3d" under it. That is tried
		/// last so a conventional install is not second-guessed.
		///
		/// Named Find... because EditorLogPath is already this plugin's own log
		/// file inside the bridge directory, and shadowing it would silently
		/// make the compiler-error reader parse the bridge's request log.
		private static string FindEditorLogPath()
		{
			string name = "Editor.log";
			string dataPath = Application.dataPath;
			if (!string.IsNullOrEmpty(dataPath))
			{
				string projectDir = Path.GetDirectoryName(dataPath);
				if (!string.IsNullOrEmpty(projectDir))
				{
					string candidate = Path.Combine(projectDir, name);
					if (File.Exists(candidate)) return candidate;
				}
			}
			string config = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
			if (!string.IsNullOrEmpty(config))
			{
				string candidate = Path.Combine(config, "unity3d", name);
				if (File.Exists(candidate)) return candidate;
			}
			return null;
		}

		private static readonly System.Text.RegularExpressions.Regex CompileErrorPattern = new System.Text.RegularExpressions.Regex(
			@"^(?<file>[^\s].*?)\((?<line>\d+),(?<col>\d+)\):\s*error\s*(?<code>CS\d+):\s*(?<msg>.*)$");

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
