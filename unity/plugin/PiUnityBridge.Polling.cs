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
				// The first poll after a reload is the earliest moment the Editor
				// can tell that the cycle is over, and for a build that had a
				// domain reload it is the ONLY moment: compilationFinished fires
				// before the reload, so its handler runs in a domain that is about
				// to be thrown away. Observed live -- a successful build left the
				// start marker standing, reported as a compile still in flight
				// for the whole budget. A failed build raises no reload, so its
				// handler did run and the file was correct; the two paths diverge
				// exactly on the reload.
				CloseFinishedCompileCycle();
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
						// A snippet that threw has usually already said the useful
						// thing through Debug.Log, and that is thrown away with the
						// stack frame that carried it unless the dispatcher keeps it.
						// So the two failure kinds are recognised by TYPE here
						// rather than by matching message text.
						//
						// This is a catch of a deliberately-thrown exception type, not
						// of every Exception: any other failure keeps the plain
						// message, because inventing a `logs` key for an unrelated
						// bug (a missing file, a bad request) would be a lie about
						// where the answer is.
						var thrown = error as SnippetException;
						if (thrown != null)
						{
							Json partial = new Json();
							partial.OpenObject();
							partial.AddString("stdout", thrown.output ?? "");
							partial.AddString("stderr", thrown.Message ?? "");
							partial.AddNumber("exitCode", 1);
							partial.AddBool("changed", true);
							// The full stack trace, from the snippet's own frames
							// down. TargetInvocationException is unwrapped in
							// CompileAndRun, so the top frame is the snippet's, not
							// a reflection frame that means nothing to a caller.
							partial.AddString("stackTrace", thrown.InnerException != null ? thrown.InnerException.StackTrace ?? "" : thrown.StackTrace ?? "");
							Bridge.WriteLogArray(partial, "logs", thrown.logs);
							partial.CloseObject();
							resultJson = partial.ToString();
							errorMessage = thrown.Message;
						}
						else
						{
							errorMessage = error.Message;
							var compile = error as CompileDiagnosticsException;
							if (compile != null)
							{
								Json partial = new Json();
								partial.OpenObject();
								partial.AddString("stdout", "");
								partial.AddString("stderr", compile.Message ?? "");
								partial.AddNumber("exitCode", 1);
								partial.AddBool("changed", false);
								partial.AddString("stackTrace", "");
								partial.AddArray("diagnostics", compile.diagnostics.ToArray());
								partial.AddArray("logs", new string[0]);
								partial.CloseObject();
								resultJson = partial.ToString();
							}
						}
					}

					if (resultJson != null && errorMessage != null)
					{
						// Both are set only for the failures above, which have both a
						// human message and a structured payload to put in front of
						// it. ok:false with a result keeps the error visible in the
						// normal place while still delivering what the snippet
						// learned -- previously this was an either/or, so a caller
						// either got the exception or got the logs, never both.
						WritePartialResponse(requestId, errorMessage, resultJson);
					}
					else if (resultJson != null) WriteResponse(requestId, true, null, resultJson);
					else WriteResponse(requestId, false, errorMessage ?? "Request handler returned nothing.", null);
				}
			}
			catch (Exception error)
			{
				// Never let a bridge bug take down the Editor.
				AppendLog("poll error: " + error);
			}
		}

		/// Closes a compile cycle that is over, for the case the event cannot reach.
		///
		/// isCompiling has just gone false, so a cycle the start handler recorded
		/// and no finished handler closed is finished: either it built no
		/// assembly, or its finished event died with the domain reload the build
		/// itself caused. Either way the compiler had nothing to say, and saying
		/// so is what stops a completed compile from reading as one still
		/// running.
		private static void CloseFinishedCompileCycle()
		{
			if (string.IsNullOrEmpty(CompilerMessagesPath)) return;
			if (CompilerMessagesCapturedAssembly != "") return;
			// The statics are empty again after a reload even when the compiler
			// DID speak before it, so the file on disk is what says whether the
			// verdict is already recorded. Overwriting it would throw away the
			// compiler's own answer.
			try
			{
				Json json = Json.Parse(File.ReadAllText(CompilerMessagesPath));
				if (json.GetBool("completed")) return;
			}
			catch (Exception)
			{
				// No capture, or one that cannot be read: nothing to preserve.
			}
			try
			{
				WriteCompileCapture("", new string[0], new string[0], true);
			}
			catch (Exception error)
			{
				AppendLog("compile cycle close failed: " + error.Message);
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

		/// A failure reply that still carries the payload.
		///
		/// The same atomic publish as WriteResponse, factored out rather than
		/// duplicated because the two differ only in the envelope -- and the
		/// difference has to be exact: a caller reads `error` to decide it failed
		/// and `result` to find out what the snippet managed to say, and getting
		/// the shape wrong in either direction loses one of those.
		private static void WritePartialResponse(string requestId, string error, string resultJson)
		{
			string payload = "{\"ok\":false,\"error\":" + Json.Quote(error) + ",\"result\":" + (resultJson ?? "null") + "}";
			string target = Path.Combine(ResponsesDir, requestId + ".json");
			string temp = target + ".tmp";
			try
			{
				File.WriteAllText(temp, payload);
				if (File.Exists(target)) File.Delete(target);
				File.Move(temp, target);
			}
			catch (Exception)
			{
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
			// right. A source file written before the last compile cycle the
			// plugin watched ENDED was rebuilt after that error was written, so
			// the line is the log's memory rather than the compiler's.
			//
			// The assembly timestamps are NOT the reference here, for the same
			// reason they are not the reference in ScriptsNeedingCompile: the
			// build keys on content hashes, so a compile that changed nothing
			// leaves every output mtime where it was and a genuinely rebuilt
			// file can be older than its output's stale clock. Falls back to the
			// outputs when the plugin has never seen a cycle finish, which is the
			// weaker question but better than reporting a fixed error as live.
			string projectRoot = Path.GetDirectoryName(Application.dataPath);
			DateTime? lastCycle = LastCompletedCompileUtc();
			DateTime reference;
			if (lastCycle != null)
			{
				reference = lastCycle.Value.ToLocalTime();
			}
			else
			{
				var outputs = new[]
				{
					Path.Combine(projectRoot ?? "", "Library/ScriptAssemblies/Assembly-CSharp.dll"),
					Path.Combine(projectRoot ?? "", "Library/ScriptAssemblies/Assembly-CSharp-Editor.dll")
				};
				var built = outputs.Where(File.Exists).Select(File.GetLastWriteTime).ToList();
				if (built.Count == 0) return latest;
				reference = built.Max();
			}

			var live = new List<string>();
			foreach (string error in latest)
			{
				string absolute = Path.Combine(projectRoot ?? "", error.Split('(')[0]);
				if (!File.Exists(absolute)) continue;   // the file is gone, so the error is
				if (File.GetLastWriteTime(absolute) <= reference) continue;
				live.Add(error);
			}
			return live;
		}

		/// Scripts on disk that the last completed compile did not account for.
		///
		/// The error log cannot answer "has this project ever compiled": a
		/// project whose every script is broken logs its errors once and then
		/// logs nothing again, so the log goes quiet and an empty read looks
		/// like success. Something has to, and the build output is the usual
		/// answer -- a script file newer than the assembly that would hold it
		/// is a script that never built.
		///
		/// It is not a sound answer on Unity 2022.3, though, and that is measured
		/// rather than suspected. Compilation goes through Bee, which keys its
		/// work on CONTENT HASHES, so touching a file without changing a byte
		/// of it rebuilds no assembly and leaves the output's timestamp exactly
		/// where it was. Observed live: a runtime script touched and then
		/// compiled with no errors at all, with the runtime assembly's
		/// timestamp unchanged by the build, which a file-newer-than-output
		/// rule reports as a script that has never compiled. Forever, because
		/// nothing a later compile does will move that timestamp either.
		///
		/// What is sound is the compile itself, and the plugin already records
		/// when one ended. A source written after the last completed cycle
		/// cannot have been part of it; one written before it was, whatever the
		/// build chose to emit. So the capture is the reference, and the output
		/// timestamps are only the fallback for a project whose plugin has not
		/// yet seen a cycle finish.
		public static List<string> ScriptsNeedingCompile()
		{
			var stale = new List<string>();
			string projectRoot = Path.GetDirectoryName(Application.dataPath);
			if (string.IsNullOrEmpty(projectRoot)) return stale;

			DateTime? lastCycle = LastCompletedCompileUtc();
			if (lastCycle == null)
			{
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
				lastCycle = built.Max();
			}

			string assets = Path.Combine(projectRoot, "Assets");
			if (!Directory.Exists(assets)) return stale;

			DateTime reference = lastCycle.Value.ToLocalTime();
			foreach (string file in Directory.GetFiles(assets, "*.cs", SearchOption.AllDirectories))
			{
				DateTime written = File.GetLastWriteTime(file);
				if (written <= reference) continue;
				stale.Add("Assets/" + file.Substring(assets.Length + 1).Replace('\\', '/') + "  (changed " + written.ToString("HH:mm:ss") + ")");
			}
			return stale;
		}

		/// When the last compile cycle the plugin watched actually ENDED.
		///
		/// Null when there is no completed capture on disk: the plugin has not
		/// been alive through a cycle, or the file is unparsable. The caller
		/// falls back to the output assemblies then, which is the weaker
		/// question but is better than no answer.
		private static DateTime? LastCompletedCompileUtc()
		{
			if (string.IsNullOrEmpty(CompilerMessagesPath) || !File.Exists(CompilerMessagesPath)) return null;
			try
			{
				Json json = Json.Parse(File.ReadAllText(CompilerMessagesPath));
				if (!json.GetBool("completed")) return null;
				DateTime at = DateTime.Parse(json.GetString("finishedAt"), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);
				return at.ToUniversalTime();
			}
			catch (Exception)
			{
				return null;
			}
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

		/// The compiler's own diagnostics for the most recent assembly build.
		///
		/// This is the ONLY event that carries them. compilationFinished hands
		/// over Bee.BeeDriver.ActiveBuild, a compile job with no messages on it,
		/// and 2022.3 has no CompilationContext to ask afterwards -- which is
		/// why the fallback below reads the log instead.
		///
		/// Written to a FILE rather than kept in a static because a successful
		/// script compile ends in a domain reload, which discards every static
		/// here. Anything cached in memory is gone by the time the caller that
		/// wanted it asks, so the one store that outlives the compile is the
		/// only one that can answer.
		///
		/// Best-effort throughout: a failure to record feedback must never be
		/// able to fail the compile that produced it.
		private static void OnAssemblyCompilationFinished(string assemblyPath, CompilerMessage[] messages)
		{
			try
			{
				if (string.IsNullOrEmpty(CompilerMessagesPath)) return;

				var errors = new List<string>();
				var warnings = new List<string>();
				foreach (CompilerMessage message in messages ?? new CompilerMessage[0])
				{
					string text = FormatCompilerMessage(message);
					if (message.type == CompilerMessageType.Error) errors.Add(text);
					else if (message.type == CompilerMessageType.Warning) warnings.Add(text);
				}

				// Kept for the cycle handler, which runs after this one and has
				// to decide whether the compiler already spoke. Statics die at
				// the domain reload that follows, which is fine: the file is the
				// store that has to survive, and this only has to last until the
				// end of this cycle.
				CompilerMessagesCapturedAssembly = assemblyPath ?? "";
				CompilerMessagesCapturedErrors = errors.ToArray();
				CompilerMessagesCapturedWarnings = warnings.ToArray();
				WriteCompileCapture(CompilerMessagesCapturedAssembly, CompilerMessagesCapturedErrors, CompilerMessagesCapturedWarnings, true);
			}
			catch (Exception error)
			{
				AppendLog("compile message capture failed: " + error.Message);
			}
		}

		/// The one place a capture is written, from any of the three handlers.
		///
		/// All three write the same path, so the LAST writer wins and ordering is
		/// the whole design: the start marker lands, then either the compiler's
		/// verdict or the cycle's own "no assembly to report" overwrites it.
		/// Write-through is safe under the same rule as the request responses --
		/// one writer, identical bytes.
		private static void WriteCompileCapture(string assemblyPath, string[] errors, string[] warnings, bool completed)
		{
			Json json = new Json();
			json.OpenObject();
			json.AddString("finishedAt", DateTime.Now.ToString("o", CultureInfo.InvariantCulture));
			json.AddString("assembly", assemblyPath ?? "");
			json.AddBool("completed", completed);
			json.AddArray("errors", errors ?? new string[0]);
			json.AddArray("warnings", warnings ?? new string[0]);
			json.CloseObject();

			File.WriteAllText(CompilerMessagesPath, json.ToString());
		}

		/// A compiler message verbatim, so a caller reads one format whichever
		/// source answered: the log parse and this event emit the same
		/// "file(line,col): error CSxxxx: text" string.
		///
		/// CompilerMessage.message arrives in BOTH shapes. A Roslyn diagnostic
		/// already reads "Assets/Foo.cs(12,5): error CS1002: ; expected", while
		/// some other messages arrive as the bare text with the location only on
		/// the sibling fields. Prepending the location to the first shape would
		/// double it -- observed live, once the event actually fired. So the
		/// message is used verbatim whenever it already carries its own
		/// location, and only bare text is given one.
		///
		/// A message with no file is reported as-is rather than dressed up as
		/// one, because "no file" is a real answer for a project-level error and
		/// a filename would be a lie.
		private static string FormatCompilerMessage(CompilerMessage message)
		{
			string file = message.file ?? "";
			string text = message.message ?? "";
			string kind = message.type == CompilerMessageType.Error ? "error" : "warning";

			if (text.Length == 0)
				return file.Length == 0 ? "(no file): " + kind : file + "(" + message.line + "," + message.column + "): " + kind;

			// The text is already located -- either at this message's own
			// coordinates, or with no file at all, which needs no location.
			if (text.IndexOf("(" + message.line + "," + message.column + ")") >= 0) return text;
			if (file.Length == 0) return text;
			return file + "(" + message.line + "," + message.column + "): " + text;
		}

		/// Whether a compile capture exists on disk at all.
		private static bool CompilerMessagesCaptured()
		{
			return !string.IsNullOrEmpty(CompilerMessagesPath) && File.Exists(CompilerMessagesPath);
		}

		/// Errors from the captured compile, or null when there is no capture.
		///
		/// Null and empty are different answers: null means the event never
		/// fired, so the log is the only evidence available, and empty means a
		/// compile really did finish with nothing wrong with it. Collapsing
		/// them would report "no errors" for a project that never compiled.
		private static string[] CapturedCompileErrors()
		{
			if (string.IsNullOrEmpty(CompilerMessagesPath) || !File.Exists(CompilerMessagesPath)) return null;
			try
			{
				Json json = Json.Parse(File.ReadAllText(CompilerMessagesPath));
				return json.GetStringArray("errors").ToArray();
			}
			catch (Exception)
			{
				// A half-written or unparsable file is not evidence of anything;
				// the log parse is still available.
				return null;
			}
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
