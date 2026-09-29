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

namespace PiUnity
{
	public static partial class Bridge
	{
		private static SnippetCompiler compiler;
		private static string compilerSearchError;

		/// Compiles a snippet into a throwaway in-memory assembly, invokes its
		/// entry point, and returns what it logged. No domain reload, no
		/// recompile of the project, no asmdef to maintain.
		private static string CompileAndRun(string code)
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
			// Debug.Log does NOT come through here: Unity routes it to the Editor
			// log, not to the process stdout. A snippet that wants its output
			// returned must use Console.WriteLine.
			TextWriter previousOut = Console.Out;
			TextWriter previousError = Console.Error;
			StringWriter captured = new StringWriter();
			try
			{
				Console.SetOut(captured);
				Console.SetError(captured);
				entry.Invoke(null, null);
			}
			catch (TargetInvocationException error)
			{
				Exception cause = error.InnerException ?? error;
				// Append what the snippet printed BEFORE it threw.
				//
				// A snippet that builds a list of findings, logs them, and then
				// hits a null reference has already done the work a caller wants,
				// and the lines that explain WHY it failed are usually printed
				// just above the throw. The capture was restored and read, but
				// only on the success path -- the throw discarded the buffer, so
				// the caller got the exception with no stdout at all, and re-ran
				// the snippet to collect output it had already paid for.
				Console.SetOut(previousOut);
				Console.SetError(previousError);
				string printed = captured.ToString().TrimEnd();
				if (printed.Length > 0)
				{
					throw new Exception(
						"C# snippet threw: " + cause + "\n\n--- output before the throw ---\n" + printed,
						cause);
				}
				throw new Exception("C# snippet threw: " + cause, cause);
			}
			finally
			{
				// Restore before reading, or the captured buffer is incomplete.
				Console.SetOut(previousOut);
				Console.SetError(previousError);
			}

			return captured.ToString().TrimEnd();
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
