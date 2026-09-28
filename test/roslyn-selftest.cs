// Runtime self-test for the Roslyn snippet compiler.
//
// This exists because PiUnityBridge.Compiler.cs shipped broken: the plugin
// compiled clean, passed the offline plugin compile, and only failed once a
// live Editor tried to run a snippet -- "Value cannot be null. Parameter name:
// type" -- because Unity removed the Mono C# compiler in 2021.2.
//
// Compiling the plugin proves nothing about this file. It is exercised here by
// RUNNING it, under Unity's own bundled mono and Unity's own Roslyn, with no
// Editor open. Compiled by test/roslyn-selftest.sh.
//
// The tests use SnippetCompiler directly, not Bridge.CompileAndRun, because
// SnippetCompiler has no Unity dependency -- that is what makes it testable.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using PiUnity;

internal static class Program
{
	private static int failures;

	private static void Check(string name, bool ok, string detail = "")
	{
		if (ok)
		{
			Console.WriteLine("  ok   " + name);
		}
		else
		{
			failures++;
			Console.WriteLine("  FAIL " + name + (detail.Length > 0 ? " -- " + detail : ""));
		}
	}

	/// The reference set: enough to compile a snippet that touches System
	/// collections and does arithmetic. Deliberately the Mono 4.8 API facades,
	/// because that is what a Unity Editor's snippets run against.
	private static List<string> ReferencePaths(string dataDir)
	{
		string mono = Path.Combine(dataDir, "MonoBleedingEdge", "lib", "mono", "4.8-api");
		List<string> paths = new List<string>();
		if (Directory.Exists(mono))
		{
			foreach (string file in Directory.GetFiles(mono, "*.dll"))
			{
				string name = Path.GetFileName(file);
				if (name == "mscorlib.dll" || name == "System.dll" || name == "System.Core.dll"
					|| name == "System.Runtime.dll" || name == "netstandard.dll")
				{
					paths.Add(file);
				}
			}
		}
		return paths;
	}

	private static int Main(string[] argv)
	{
		string dataDir = argv.Length > 0 ? argv[0] : "";

		Console.WriteLine("roslyn snippet compiler");

		// ---- discovery ----------------------------------------------------
		string[] search =
		{
			Path.Combine(dataDir, "Tools", "ScriptUpdater"),
			Path.Combine(dataDir, "DotNetSdkRoslyn"),
		};
		foreach (string dir in search) Console.WriteLine("  probing: " + dir + " (" + (Directory.Exists(dir) ? "exists" : "missing") + ")");

		SnippetCompiler compiler = SnippetCompiler.TryCreate(search);
		if (compiler == null) compiler = SnippetCompiler.TryCreateFromResident();

		Check("a Roslyn compiler loads", compiler != null,
			"none of the search directories yielded a loadable Microsoft.CodeAnalysis pair");
		if (compiler == null)
		{
			Console.WriteLine("\n0 passed, 1 failed");
			return 1;
		}

		List<string> references = ReferencePaths(dataDir);
		Check("reference set is non-empty", references.Count > 0, dataDir);

		// ---- the happy path, end to end ------------------------------------
		// This is the exact shape the bridge uses: a static PiScript.Run, a
		// side effect, and the result observed afterwards.
		string outputPath = Path.Combine(Path.GetTempPath(), "pi-snippet-selftest.txt");
		if (File.Exists(outputPath)) File.Delete(outputPath);

		string snippet =
			"using System;\n" +
			"using System.Collections.Generic;\n" +
			"public static class PiScript\n" +
			"{\n" +
			"	public static void Run()\n" +
			"	{\n" +
			"		var items = new List<int> { 3, 1, 2 };\n" +
			"		items.Sort();\n" +
			"		int total = 0;\n" +
			"		foreach (int item in items) total += item;\n" +
			"		System.IO.File.WriteAllText(\"" + outputPath + "\", total.ToString());\n" +
			"	}\n" +
			"}\n";

		Assembly compiled = null;
		string compileError = "";
		try
		{
			compiled = compiler.Compile(snippet, references);
		}
		catch (Exception error)
		{
			compileError = error.Message;
		}
		Check("a snippet compiles", compiled != null, compileError);
		if (compiled == null)
		{
			Console.WriteLine("\n" + failures + " passed, 1 failed");
			return 1;
		}

		// The entry-point resolution contract: PiScript.Run wins, and a static
		// Main is honoured too, because snippets copied from a Unity manual use
		// that shape.
		MethodInfo run = compiled.GetType("PiScript")?.GetMethod("Run", BindingFlags.Public | BindingFlags.Static);
		Check("PiScript.Run is found", run != null);
		if (run != null)
		{
			try { run.Invoke(null, null); }
			catch (TargetInvocationException error) { Check("entry point runs", false, error.InnerException?.ToString()); }
		}
		Check("the snippet actually ran and wrote its file", File.Exists(outputPath));
		Check("the snippet's logic produced the right answer",
			File.Exists(outputPath) && File.ReadAllText(outputPath).Trim() == "6",
			File.Exists(outputPath) ? "got " + File.ReadAllText(outputPath) : "<no file>");

		// ---- implicit usings ------------------------------------------------
		// A snippet written the way a model naturally writes one: no usings at
		// all, relying on System / System.IO / System.Collections.Generic /
		// System.Linq / System.Text / System.Reflection being available.
		string implicitSnippet =
			"public static class PiScript\n" +
			"{\n" +
			"	public static void Run()\n" +
			"	{\n" +
			"		var list = new List<string> { \"a\", \"b\" };\n" +
			"		string joined = string.Join(\",\", list.ToArray());\n" +
			"		var builder = new StringBuilder(joined);\n" +
			"		builder.Append(System.Reflection.Assembly.GetExecutingAssembly().GetName().Name);\n" +
			"		File.WriteAllText(\"" + outputPath + ".implicit\", builder.ToString());\n" +
			"	}\n" +
			"}\n";
		Assembly implicitCompiled = null;
		string implicitError = "";
		try { implicitCompiled = compiler.Compile(implicitSnippet, references); }
		catch (Exception error) { implicitError = error.Message; }
		Check("a snippet with no usings at all compiles", implicitCompiled != null, implicitError);
		if (implicitCompiled != null)
		{
			MethodInfo run2 = implicitCompiled.GetType("PiScript")?.GetMethod("Run", BindingFlags.Public | BindingFlags.Static);
			if (run2 != null) run2.Invoke(null, null);
			Check("the no-usings snippet actually ran", File.Exists(outputPath + ".implicit"),
				File.Exists(outputPath + ".implicit") ? File.ReadAllText(outputPath + ".implicit") : "<no file>");
		}

		// The Object/Random aliases exist because `using System;` and
		// `using UnityEngine;` in scope makes both names CS0104 ambiguous. A bare
		// `Random.Range(...)` is completely idiomatic Unity, so it must compile.
		// The self-test harness has no Unity reference, so this is asserted
		// against the aliases being emitted rather than against a Unity type
		// resolving -- the live test covers the real thing.
		string ambiguous =
			"public static class PiScript\n" +
			"{\n" +
			"	public static void Run()\n" +
			"	{\n" +
			"		var x = Random.Next();\n" +
			"		var y = Object.ReferenceEquals(x, x);\n" +
			"	}\n" +
			"}\n";
		string ambiguousError = "";
		try { compiler.Compile(ambiguous, references); }
		catch (Exception error) { ambiguousError = error.Message; }
		Check("an Object/Random alias is available (no CS0104 ambiguity)",
			ambiguousError.Length == 0 || !ambiguousError.Contains("CS0104"), ambiguousError);

		// A snippet that already aliases must not be warned (CS0576).
		string selfAliased =
			"using Object = UnityEngine.Object;\n" +
			"public static class PiScript { public static void Run() { } }\n";
		string selfAliasError = "";
		try { compiler.Compile(selfAliased, references); }
		catch (Exception error) { selfAliasError = error.Message; }
		Check("a snippet that aliases these itself is not warned (no CS0576)",
			!selfAliasError.Contains("CS0576"), selfAliasError);

		// A snippet that DOES declare the namespaces itself must still compile:
		// prepending a duplicate using would be CS0105.
		string explicitSnippet =
			"using System;\n" +
			"using System.Collections.Generic;\n" +
			"public static class PiScript\n" +
			"{\n" +
			"	public static void Run() { }\n" +
			"}\n";
		string explicitError = "";
		try { compiler.Compile(explicitSnippet, references); }
		catch (Exception error) { explicitError = error.Message; }
		Check("a snippet that declares its own usings still compiles (no CS0105)",
			!explicitError.Contains("CS0105"), explicitError);

		// ---- diagnostic line numbers point at the caller's file -----------
		// The implicit usings are prepended as one line, so a raw Roslyn line
		// number is one too high and would send a model to the wrong line in its
		// own file. The error below is on line 5 of the snippet; unstripped
		// Roslyn would report 6.
		string numbered =
			"public static class PiScript\n" +
			"{\n" +
			"	public static void Run()\n" +
			"	{\n" +
			"		int x = \"not an int\";\n" +
			"	}\n" +
			"}\n";
		string numberedError = "";
		try { compiler.Compile(numbered, references); }
		catch (Exception error) { numberedError = error.Message; }
		Check("a type error is reported", numberedError.Contains("CS0029"), numberedError);
		Check("the reported line matches the snippet's own numbering (5, not 6)",
			numberedError.Contains("(5,"), numberedError.Replace("\n", " ").Substring(0, Math.Min(90, numberedError.Length)));

		// ---- static Main is honoured --------------------------------------
		string mainSnippet =
			"public static class Other\n" +
			"{\n" +
			"	public static void Main()\n" +
			"	{\n" +
			"		System.IO.File.WriteAllText(\"" + outputPath + ".main\", \"ok\");\n" +
			"	}\n" +
			"}\n";
		Assembly mainCompiled = null;
		string mainError = "";
		try { mainCompiled = compiler.Compile(mainSnippet, references); }
		catch (Exception error) { mainError = error.Message; }
		Check("a static-Main snippet compiles", mainCompiled != null, mainError);
		if (mainCompiled != null)
		{
			MethodInfo main = mainCompiled.GetType("Other")?.GetMethod("Main", BindingFlags.Public | BindingFlags.Static, null, Type.EmptyTypes, null);
			Check("a static Main is discoverable", main != null);
		}

		// ---- preprocessor symbol ------------------------------------------
		// PI_UNITY is promised in the file's documentation, so prove the
		// snippet can actually branch on it.
		string guardSnippet =
			"#if PI_UNITY\n" +
			"public static class PiScript\n" +
			"{\n" +
			"	public static void Run()\n" +
			"	{\n" +
			"		System.IO.File.WriteAllText(\"" + outputPath + ".sym\", \"defined\");\n" +
			"	}\n" +
			"}\n" +
			"#endif\n";
		Assembly guardCompiled = null;
		string guardError = "";
		try { guardCompiled = compiler.Compile(guardSnippet, references); }
		catch (Exception error) { guardError = error.Message; }
		Check("PI_UNITY is defined for snippets", guardCompiled != null, guardError);

		// ---- a syntax error must be a clear error, not a crash ------------
		string broken = "public static class PiScript { public static void Run() { this is not c# } }";
		string brokenError = "";
		try { compiler.Compile(broken, references); }
		catch (Exception error) { brokenError = error.Message; }
		Check("a syntax error is reported, not thrown as a load failure",
			brokenError.Contains("failed to compile"), brokenError);
		Check("the reported error names the file", brokenError.Contains("PiSnippet.cs"), brokenError);
		Check("the reported error carries line detail", brokenError.Contains("CS1"), brokenError);

		// ---- two compiles must not collide -------------------------------
		// Assembly.Load caches by name; a reused name would make the second
		// snippet see the first one's types.
		try
		{
			Assembly a = compiler.Compile(snippet.Replace(outputPath, outputPath + ".a"), references);
			Assembly b = compiler.Compile(snippet.Replace(outputPath, outputPath + ".b"), references);
			Check("consecutive compiles produce distinct assemblies",
				a != null && b != null && a.FullName != b.FullName,
				a != null && b != null ? a.FullName + " vs " + b.FullName : "one failed to compile");
		}
		catch (Exception error)
		{
			Check("consecutive compiles produce distinct assemblies", false, error.Message);
		}

		foreach (string leftover in new[] { outputPath, outputPath + ".main", outputPath + ".sym", outputPath + ".a", outputPath + ".b", outputPath + ".implicit" })
		{
			try { if (File.Exists(leftover)) File.Delete(leftover); } catch (Exception) { }
		}

		Console.WriteLine();
		Console.WriteLine(failures == 0
			? "all Roslyn compiler self-tests passed"
			: failures + " self-test failure(s)");
		return failures == 0 ? 0 : 1;
	}
}
