// Part of the pi Unity bridge plugin. See PiUnityBridge.Core.cs for the
// protocol overview. The whole plugin is one assembly of partial classes, so
// Unity compiles it into Assembly-CSharp-Editor with no asmdef and no
// package dependencies, which is what lets it drop into any Unity 2021+
// project (a configured VRChat world, an existing game) that pi was pointed at
// rather than generated.

// The Unity-side glue between Bridge and the pure SnippetCompiler.
//
// The split is deliberate: SnippetCompiler (PiUnityBridge.Compiler.cs) has no
// Unity dependency at all, so the Roslyn plumbing -- the part most likely to
// break silently, and the part that shipped broken on 2022.3 -- can be run and
// asserted offline by test/roslyn-selftest.cs with no Editor open. Only this
// file, which needs to know where the Editor's Data directory and the
// project's ScriptAssemblies are, depends on UnityEditor.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using UnityEditor;
using UnityEngine;

namespace PiUnity
{
	public static partial class Bridge
	{
		private static SnippetCompiler compiler;
		private static string compilerSearchError;

		/// A Debug.Log line captured while a snippet was running.
		/// `level` is Unity's own LogType/LogTypeToString mapping, so it reads

		/// Records Debug.Log output for the duration of one snippet.
		///
		/// Unity routes Debug.Log to the Editor log, never to the process
		/// stdout, so the Console.SetOut redirect in CompileAndRun cannot see
		/// it. Application.logMessageReceived is the only in-process hook, and
		/// it is GLOBAL: it fires for every Debug.Log anywhere in the Editor,
		/// including pi's own plugin and any package running in the background.
		///
		/// So the handler is installed only for the window of the snippet and
		/// removed in a finally -- a permanently-installed handler would both
		/// leak (one per snippet, each holding a growing list) and attribute
		/// unrelated Editor chatter to whichever snippet ran last.
		///
		/// The two overloads are both required and neither alone is sufficient:
		///   - logMessageReceivedThreaded fires on the LOGGING thread. It sees
		///     messages from any thread, but the stack is null and handlers
		///     there are documented as not being able to touch the API.
		///   - logMessageReceived fires on the MAIN thread only, and carries the
		///     stack trace.
		/// Registering both and de-duplicating on (level, message, stack) gets
		/// both coverage and a stack.
		private static readonly List<CapturedLog> capturing = new List<CapturedLog>();
		private static readonly object captureLock = new object();

		private static void BeginLogCapture()
		{
			lock (captureLock)
			{
				capturing.Clear();
				Application.logMessageReceivedThreaded -= OnLogThreaded;
				Application.logMessageReceived -= OnLogMainThread;
				Application.logMessageReceivedThreaded += OnLogThreaded;
				Application.logMessageReceived += OnLogMainThread;
			}
		}

		private static List<CapturedLog> EndLogCapture()
		{
			lock (captureLock)
			{
				Application.logMessageReceivedThreaded -= OnLogThreaded;
				Application.logMessageReceived -= OnLogMainThread;
				return new List<CapturedLog>(capturing);
			}
		}

		private static void OnLogThreaded(string condition, string stack, LogType type)
		{
			Record(condition, stack, type);
		}

		private static void OnLogMainThread(string condition, string stack, LogType type)
		{
			Record(condition, stack, type);
		}

		private static void Record(string condition, string stack, LogType type)
		{
			if (condition == null) return;
			CapturedLog entry = new CapturedLog(
				type.ToString(),
				condition,
				// The threaded overload never has a stack, so prefer one if the
				// main-thread overload already delivered the same line.
				stack ?? "");
			lock (captureLock)
			{
				capturing.Add(entry);
			}
		}

		/// Folds captured Debug.Log lines into the console text a caller reads.
		///
		/// Two shapes, deliberately. `stdout` stays Console.WriteLine's output
		/// alone so a snippet that already prints to stdout is not double-fed;
		/// the log lines are appended below it, separated, so a caller reading
		/// only stdout still sees them. That is the behaviour that makes
		/// Debug.Log work without asking every existing snippet to change.
		private static string MergeLogIntoStdout(string consoleOutput, List<CapturedLog> logs)
		{
			if (logs == null || logs.Count == 0) return (consoleOutput ?? "").TrimEnd();

			System.Text.StringBuilder merged = new System.Text.StringBuilder();
			string stdout = (consoleOutput ?? "").TrimEnd();
			if (stdout.Length > 0) merged.Append(stdout).Append('\n');
			merged.Append("--- Debug.Log ---");
			foreach (CapturedLog entry in logs)
			{
				merged.Append('\n').Append('[').Append(entry.level).Append("] ").Append(entry.message);
				if (!string.IsNullOrEmpty(entry.stack)) merged.Append("\n  at ").Append(TrimStack(entry.stack));
			}
			return merged.ToString();
		}

		/// Unity's stack for a Debug.LogError is "at Foo.Bar() in ..." already
		/// newline-prefixed per frame; collapse it to one readable line.
		private static string TrimStack(string stack)
		{
			string[] frames = stack.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
			return string.Join(" | ", frames);
		}

		private static void WriteLogArray(Json target, string key, List<CapturedLog> logs)
		{
			target.AddArray(key, Json.Array(logs.ConvertAll(entry =>
			{
				Json item = new Json();
				item.OpenObject();
				item.AddString("level", entry.level);
				item.AddString("message", entry.message);
				item.AddString("stack", entry.stack ?? "");
				item.CloseObject();
				return item;
			}).ToArray()));
		}

		/// Compile diagnostics a caller can read separately from the message.
		///
		/// The message already carries them, so this is not extra information --
		/// it is the same information in a shape a caller can act on without
		/// parsing prose. A model reading `diagnostics` gets the file, line,
		/// column and error code as fields; a model parsing "C# snippet failed to
		/// compile:\n..." has to strip a prefix first and can get it wrong.
		///
		/// public, not internal, because SnippetCompiler lives in its own
		/// (non-nested) class -- `internal` here would mean internal to Bridge,

		/// A snippet threw. The captured Debug.Log lines travel with the
		/// exception rather than being dropped, because they are frequently the

		/// Compiles a snippet into a throwaway in-memory assembly, invokes its
		/// entry point, and returns what it logged. No domain reload, no
		/// recompile of the project, no asmdef to maintain.
		///
		/// Debug.Log output captured while the snippet ran is returned through
		/// `logsOut`. It is an out parameter rather than a property because the
		/// failure path must carry it too, and an out parameter is the only shape
		/// that survives a throw.
		private static string CompileAndRun(string code, out List<CapturedLog> logsOut)
		{
			SnippetCompiler active = GetCompiler();
			Assembly compiled = active.Compile(code ?? "", CollectReferencePaths());
			MethodInfo entry = FindEntryPoint(compiled);
			if (entry == null)
			{
				throw new Exception(
					"C# snippet compiled but exposed no entry point. Define `public static class PiScript { public static void Run() { ... } }`, " +
					"or a static Run/Main/Execute method."
				);
			}

			// The snippet is a *caller*: it may legitimately need to destroy
			// objects or close windows, so unwrap the reflection exception and
			// report what actually went wrong instead of a TargetInvocationException.
			//
			// Console.Out/Error are redirected for the duration so a snippet can
			// report findings with Console.WriteLine. Previously this returned a
			// hardcoded "", so stdout was silently empty for every snippet.
			//
			// Debug.Log does NOT come through the Console redirect: Unity routes
			// it to the Editor log, not to the process stdout. The capture
			// handler below closes that gap for the duration of the snippet.
			// A snippet can therefore report with either Console.WriteLine or
			// Debug.Log and both land in the response.
			TextWriter previousOut = Console.Out;
			TextWriter previousError = Console.Error;
			StringWriter captured = new StringWriter();
			logsOut = new List<CapturedLog>();
			BeginLogCapture();
			try
			{
				Console.SetOut(captured);
				Console.SetError(captured);
				entry.Invoke(null, null);
			}
			catch (TargetInvocationException error)
			{
				Exception cause = error.InnerException ?? error;
				// End the capture BEFORE building the message, so the logs below
				// are complete: the wrapper this throws must carry what the
				// snippet logged, and the finally cannot do it because it runs
				// after this method has already thrown. EndLogCapture is
				// idempotent, so the finally is a harmless second call.
				logsOut = EndLogCapture();
				// Append what the snippet printed BEFORE it threw.
				//
				// A snippet that builds a list of findings, logs them, and then
				// hits a null reference has already done the work a caller wants,
				// and the lines that explain WHY it failed are usually printed
				// just above the throw. The capture was restored and read, but
				// only on the success path -- the throw discarded the buffer, so
				// the caller got the exception with no stdout at all, and re-ran
				// the snippet to collect output it had already paid for.
				string merged = MergeLogIntoStdout(captured.ToString(), logsOut);
				string message = merged.Length > 0
					? "C# snippet threw: " + cause + "\n\n--- output before the throw ---\n" + merged
					: "C# snippet threw: " + cause;
				throw new SnippetException(message, cause, logsOut, merged);
			}
			finally
			{
				// Restore before reading, or the captured buffer is incomplete.
				Console.SetOut(previousOut);
				Console.SetError(previousError);
				// The single exit point for the capture. This has to run even on
				// the failure path: leaving the handler installed would attribute
				// every later Debug.Log in the Editor to a snippet that finished
				// minutes ago. Idempotent, so the catch above having already
				// ended it costs nothing.
				logsOut = EndLogCapture();
			}

			return MergeLogIntoStdout(captured.ToString(), logsOut);
		}

		private static SnippetCompiler GetCompiler()
		{
			if (compiler != null) return compiler;

			// A Roslyn already loaded in this domain is guaranteed loadable, so
			// try it before any path discovery.
			compiler = SnippetCompiler.TryCreateFromResident();

			// EditorApplication.applicationContentsPath ALREADY ends in
			// "Data" (e.g. .../Editor/2022.3.22f1/Editor/Data), so appending
			// another "Data" here produced .../Editor/Data/Data and the search
			// silently missed every candidate. Normalise instead of assuming.
			string contents = EditorApplication.applicationContentsPath.TrimEnd('/', '\\');
			string dataDir = contents.EndsWith("Data", StringComparison.Ordinal) ? contents : Path.Combine(contents, "Data");
			if (compiler == null)
			{
				compiler = SnippetCompiler.TryCreate(new[]
				{
					// ScriptUpdater is the copy Unity itself loads in-process
					// (.NETStandard 2.0). DotNetSdkRoslyn is .NETCoreApp 3.1 and
					// is meant for the csc process, so it is only a fallback.
					Path.Combine(dataDir, "Tools", "ScriptUpdater"),
					Path.Combine(dataDir, "DotNetSdkRoslyn"),
				});
			}

			if (compiler == null)
			{
				compilerSearchError =
					"Could not load a Roslyn compiler from the Unity Editor. Looked for " +
					"Microsoft.CodeAnalysis.dll + Microsoft.CodeAnalysis.CSharp.dll in " +
					Path.Combine(dataDir, "Tools", "ScriptUpdater") + " and " +
					Path.Combine(dataDir, "DotNetSdkRoslyn") + ", and in the current AppDomain. " +
					"If the Editor was installed without its script-updater tools, unity_execute_csharp cannot compile snippets on it. " +
					"Report the editor version; this is a limitation of the installed build, not of the project.";
				throw new Exception(compilerSearchError);
			}

			return compiler;
		}

		/// References a snippet can see: the union of every assembly already loaded
		/// in the Editor plus the project's own compiled output.
		///
		/// Taking the union of *loaded* assemblies is the part that makes this work
		/// in someone else's project. A VRChat world pulls in the VRChat SDK,
		/// lilToon, VRCFury, URP and more; pi has no idea what that package set
		/// is, but anything the Editor has touched this session is already
		/// resident and therefore already referenced. Library/ScriptAssemblies
		/// covers the rest -- types that exist in source but were never touched
		/// this session are not loaded yet.
		private static List<string> CollectReferencePaths()
		{
			HashSet<string> paths = new HashSet<string>(StringComparer.Ordinal);

			foreach (Assembly assembly in AppDomain.CurrentDomain.GetAssemblies())
			{
				if (assembly.IsDynamic) continue;
				try
				{
					if (!string.IsNullOrEmpty(assembly.Location)) paths.Add(assembly.Location);
				}
				catch (Exception)
				{
					// Dynamic or unloadable; skip it.
				}
			}

			string scriptAssemblies = Path.Combine(ProjectRoot(), "Library", "ScriptAssemblies");
			try
			{
				if (Directory.Exists(scriptAssemblies))
				{
					foreach (string file in Directory.GetFiles(scriptAssemblies, "*.dll"))
					{
						// Project code, so a snippet can name a project type.
						// Roslyn itself is deliberately NOT added from here: a
						// duplicate of the compiler's own assembly would be
						// ambiguous, and the compiler already sees itself.
						paths.Add(file);
					}
				}
			}
			catch (Exception)
			{
				// Directory missing or unreadable; the resident assemblies still
				// cover UnityEngine, UnityEditor and any loaded package.
			}

			return paths.ToList();
		}

		/// Prefers an explicit `PiScript` type, then a static method named
		/// Run/Main/Execute. Unity's own convention (a static Main) is honoured so
		/// snippets copied from a Unity manual just work.
		private static MethodInfo FindEntryPoint(Assembly assembly)
		{
			string[] preferred = { "PiScript", "PiEntryPoint" };
			foreach (string typeName in preferred)
			{
				Type type = assembly.GetType(typeName);
				if (type == null) continue;
				MethodInfo method = type.GetMethod("Run", BindingFlags.Public | BindingFlags.Static);
				if (method != null) return method;
			}

			foreach (Type type in assembly.GetTypes())
			{
				foreach (string name in new[] { "Run", "Main", "Execute" })
				{
					MethodInfo method = type.GetMethod(name, BindingFlags.Public | BindingFlags.Static, null, Type.EmptyTypes, null);
					if (method != null && !method.IsAbstract) return method;
				}
			}
			return null;
		}
	}
}
