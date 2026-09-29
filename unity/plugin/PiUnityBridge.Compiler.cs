// Part of the pi Unity bridge plugin. See PiUnityBridge.Core.cs for the
// protocol overview. The whole plugin is one assembly of partial classes, so
// Unity compiles it into Assembly-CSharp-Editor with no asmdef and no
// package dependencies, which is what lets it drop into any Unity 2021+
// project (a configured VRChat world, an existing game) that pi was pointed at
// rather than generated.

// C# snippet compilation, driven through Roslyn by reflection.
//
// WHY THIS FILE EXISTS
// --------------------
// The first implementation compiled snippets with Mono.CSharp's
// AssemblyBuilder. Unity removed the Mono C# compiler in 2021.2 and replaced it
// with Roslyn, so on Unity 2022.3 (and every VRChat SDK world, which pins
// 2022.3) that path failed at runtime with:
//
//   Value cannot be null.  Parameter name: type
//
// because Assembly.Load("Mono.CSharp") still succeeds but
// GetType("Mono.CSharp.AssemblyBuilder") returns null. It compiled clean, so
// only a live Editor exposed it.
//
// WHY EVERY ROSLYN TYPE IS REACHED BY REFLECTION
// ----------------------------------------------
// The plugin compiles into the predefined Assembly-CSharp-Editor, which has no
// reference to Microsoft.CodeAnalysis. Adding one would force an asmdef onto a
// file that lives inside a user's project. Reflection keeps the drop-in
// property that is the entire point of this plugin.
//
// WHY THIS IS A SEPARATE CLASS FROM Bridge
// ----------------------------------------
// So it can be exercised offline. The Roslyn plumbing is the part most likely
// to break silently -- a wrong overload, a type that moved between Roslyn
// versions -- and none of that shows up to a C# compiler. SnippetCompiler has
// no Unity dependency: give it source text and a list of reference paths and
// it returns an assembly. test/roslyn-selftest.cs does exactly that under
// Unity's own mono and Unity's own Roslyn, with no Editor running.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.RegularExpressions;

namespace PiUnity
{
	/// Compiles a C# snippet to an in-memory assembly using whichever Roslyn the
	/// host provides. Deliberately free of any UnityEditor/UnityEngine reference
	/// so it is testable without an Editor.
	internal sealed class SnippetCompiler
	{
		private System.Reflection.Assembly core;
		private System.Reflection.Assembly csharp;

		private Type parseOptionsType;
		private Type syntaxTreeType;
		private Type compilationType;
		private Type compilationOptionsType;
		private Type languageVersionType;
		private Type outputKindType;
		private Type metadataReferenceType;
		private Type diagnosticSeverityType;
		private Type diagnosticType;

		/// CSharpParseOptions has no default constructor -- its ctor's first
		/// parameter is a LanguageVersion -- so Activator.CreateInstance(type)
		/// throws "Default constructor not found", a runtime-only failure the
		/// offline plugin compile cannot see.
		///
		/// The ctor cannot be found with GetConstructor(new[] { languageVersionType })
		/// either: its parameters are all OPTIONAL, so the real signature has four
		/// parameters, not one. So the ctor is located by its first parameter's
		/// type and the rest are filled from metadata defaults. A matching ctor is
		/// built the same way in CreateCompilationOptions.
		private object CreateWithLatestLanguage()
		{
			ConstructorInfo constructor = null;
			foreach (ConstructorInfo candidate in parseOptionsType.GetConstructors())
			{
				ParameterInfo[] parameters = candidate.GetParameters();
				if (parameters.Length == 0) continue;
				if (!parameters[0].ParameterType.IsAssignableFrom(languageVersionType)
					&& !languageVersionType.IsAssignableFrom(parameters[0].ParameterType)) continue;
				if (constructor == null || candidate.GetParameters().Length < constructor.GetParameters().Length)
				{
					constructor = candidate;
				}
			}
			if (constructor == null) throw new MissingMethodException("No usable CSharpParseOptions constructor.");

			ParameterInfo[] parseParameters = constructor.GetParameters();
			object[] parseArgs = new object[parseParameters.Length];
			parseArgs[0] = Enum.Parse(languageVersionType, "Latest");
			for (int i = 1; i < parseArgs.Length; i++)
			{
				parseArgs[i] = parseParameters[i].HasDefaultValue
					? parseParameters[i].DefaultValue
					: DefaultOf(parseParameters[i].ParameterType);
			}
			return constructor.Invoke(parseArgs);
		}

		/// CSharpCompilationOptions likewise has no parameterless ctor; its first
		/// parameter is the OutputKind.
		private object CreateWithOutputKind(object outputKind)
		{
			ConstructorInfo constructor = null;
			foreach (ConstructorInfo candidate in compilationOptionsType.GetConstructors())
			{
				ParameterInfo[] parameters = candidate.GetParameters();
				if (parameters.Length == 0) continue;
				if (!parameters[0].ParameterType.IsInstanceOfType(outputKind)) continue;
				if (constructor == null || parameters.Length < constructor.GetParameters().Length)
				{
					constructor = candidate;
				}
			}
			if (constructor == null) throw new MissingMethodException("No usable CSharpCompilationOptions constructor.");

			ParameterInfo[] optionParameters = constructor.GetParameters();
			object[] optionArgs = new object[optionParameters.Length];
			optionArgs[0] = outputKind;
			for (int i = 1; i < optionArgs.Length; i++)
			{
				optionArgs[i] = optionParameters[i].HasDefaultValue
					? optionParameters[i].DefaultValue
					: DefaultOf(optionParameters[i].ParameterType);
			}
			return constructor.Invoke(optionArgs);
		}

		/// Roslyn ships twice in a Unity Editor, and only one of the two builds
		/// can be loaded into the editor's own AppDomain:
		///   Data/DotNetSdkRoslyn      -- .NETCoreApp 3.1, for the csc *process*
		///   Data/Tools/ScriptUpdater  -- .NETStandard 2.0, loaded in-process
		/// The ScriptUpdater copy is the one Unity itself loads in-process, so it
		/// is tried first.
		///
		/// Roslyn also depends on System.Memory and System.Collections.Immutable,
		/// which are NOT in the Editor's 4.8-api reference set. So a resolve hook
		/// probes the search directories (and Mono's own directories) for a
		/// missing dependency. Without it, loading Roslyn succeeds and the first
		/// real call throws FileNotFoundException from deep inside Roslyn.
		public static SnippetCompiler TryCreate(IEnumerable<string> searchDirectories)
		{
			RegisterDependencyProbe(searchDirectories);
			foreach (string directory in searchDirectories)
			{
				SnippetCompiler compiler = TryCreateFrom(directory);
				if (compiler != null) return compiler;
			}
			return null;
		}

		private static readonly List<string> dependencyProbeDirectories = new List<string>();
		private static bool resolveHookInstalled;

		private static void RegisterDependencyProbe(IEnumerable<string> directories)
		{
			foreach (string directory in directories)
			{
				if (!dependencyProbeDirectories.Contains(directory)) dependencyProbeDirectories.Add(directory);
			}
			if (resolveHookInstalled) return;
			resolveHookInstalled = true;
			AppDomain.CurrentDomain.AssemblyResolve += (sender, args) =>
			{
				string simpleName = new AssemblyName(args.Name).Name;
				foreach (string directory in dependencyProbeDirectories)
				{
					string candidate = Path.Combine(directory, simpleName + ".dll");
					if (!File.Exists(candidate)) continue;
					try { return Assembly.LoadFrom(candidate); } catch (Exception) { }
				}
				// Mono's own copies are the last resort: the Editor's reference
				// set has System.Runtime.CompilerServices.Unsafe but not
				// System.Memory, which Roslyn 3.11 requires. The Mono root is
				// derived from a probe directory (not from the loop variable,
				// which is out of scope here): .../Editor/Data/Tools/... -> walk
				// up until a Data directory is found, then into MonoBleedingEdge.
				foreach (string probe in dependencyProbeDirectories)
				{
					string dataDir = probe;
					while (!string.IsNullOrEmpty(dataDir) && !dataDir.EndsWith("Data", StringComparison.Ordinal))
					{
						dataDir = Path.GetDirectoryName(dataDir);
					}
					if (string.IsNullOrEmpty(dataDir)) break;
					string monoLib = Path.Combine(dataDir, "MonoBleedingEdge", "lib", "mono");
					if (!Directory.Exists(monoLib)) break;
					foreach (string relative in new[]
					{
						"msbuild/Current/bin/Roslyn",
						"net_4_x-linux",
						"4.8-api/Facades",
						"unityaot-linux/Facades",
					})
					{
						string candidate = Path.Combine(monoLib, relative.Replace('/', Path.DirectorySeparatorChar), simpleName + ".dll");
						if (!File.Exists(candidate)) continue;
						try { return Assembly.LoadFrom(candidate); } catch (Exception) { }
					}
					break;
				}
				return null;
			};
		}

		private static SnippetCompiler TryCreateFrom(string directory)
		{
			try
			{
				string corePath = Path.Combine(directory, "Microsoft.CodeAnalysis.dll");
				string csharpPath = Path.Combine(directory, "Microsoft.CodeAnalysis.CSharp.dll");
				if (!File.Exists(corePath) || !File.Exists(csharpPath)) return null;

				SnippetCompiler compiler = new SnippetCompiler();
				compiler.core = Assembly.LoadFrom(corePath);
				compiler.csharp = Assembly.LoadFrom(csharpPath);
				if (compiler.BindTypes()) return compiler;
			}
			catch (Exception)
			{
				// Wrong target framework, or a dependency that will not resolve.
				// Try the next candidate rather than failing outright.
			}
			return null;
		}

		/// A Roslyn already resident in the AppDomain is guaranteed loadable and
		/// needs no path discovery, so it is worth trying first.
		public static SnippetCompiler TryCreateFromResident()
		{
			foreach (Assembly resident in AppDomain.CurrentDomain.GetAssemblies())
			{
				if (resident.GetName().Name != "Microsoft.CodeAnalysis.CSharp") continue;
				Assembly core = AppDomain.CurrentDomain.GetAssemblies()
					.FirstOrDefault(a => a.GetName().Name == "Microsoft.CodeAnalysis");
				if (core == null) continue;
				SnippetCompiler compiler = new SnippetCompiler { core = core, csharp = resident };
				if (compiler.BindTypes()) return compiler;
			}
			return null;
		}

		/// Resolves every Roslyn type up front so a version mismatch fails with
		/// one clear message instead of a NullReferenceException deep inside the
		/// compile.
		private bool BindTypes()
		{
			parseOptionsType = csharp.GetType("Microsoft.CodeAnalysis.CSharp.CSharpParseOptions");
			syntaxTreeType = csharp.GetType("Microsoft.CodeAnalysis.CSharp.CSharpSyntaxTree");
			compilationType = csharp.GetType("Microsoft.CodeAnalysis.CSharp.CSharpCompilation");
			compilationOptionsType = csharp.GetType("Microsoft.CodeAnalysis.CSharp.CSharpCompilationOptions");
			languageVersionType = csharp.GetType("Microsoft.CodeAnalysis.CSharp.LanguageVersion");
			outputKindType = core.GetType("Microsoft.CodeAnalysis.OutputKind");
			metadataReferenceType = core.GetType("Microsoft.CodeAnalysis.MetadataReference");
			diagnosticSeverityType = core.GetType("Microsoft.CodeAnalysis.DiagnosticSeverity");
			diagnosticType = core.GetType("Microsoft.CodeAnalysis.Diagnostic");

			return parseOptionsType != null && syntaxTreeType != null && compilationType != null
				&& compilationOptionsType != null && languageVersionType != null
				&& outputKindType != null && metadataReferenceType != null
				&& diagnosticSeverityType != null && diagnosticType != null;
		}

		/// The namespaces a snippet gets for free.
		///
		/// Without these, every model-authored script has to spell out
		/// `using System;` and `using UnityEngine;` or it fails with "The name
		/// 'GameObject' does not exist in the current context" -- a papercut that
		/// cost several probe scripts here and would cost a worker agent a retry
		/// every time.
		///
		/// PiUnity is included so the plugin's own helpers (PiMaterial) are
		/// reachable; the reference union already includes
		/// Assembly-CSharp-Editor, so this is a using directive, not a reference.
		///
		/// CSharpParseOptions has no usings API (verified by reflection against
		/// Unity's own Roslyn 3.11), so they have to be prepended textually.
		/// Everything sits on ONE line because multiple using directives sharing a
		/// line is legal C#, and a single line keeps the diagnostic offset at
		/// exactly 1 -- which is subtracted back off every reported line number.
		/// Prepending one line per namespace would silently shift every error
		/// report, which is worse than the papercut.
		private static readonly string[] BaseUsings =
		{
			"System", "System.IO", "System.Collections.Generic", "System.Linq", "System.Text", "System.Reflection",
		};

		/// Aliases that resolve the two names UnityEngine and System both define.
		///
		/// With `using System;` and `using UnityEngine;` in scope, a bare `Random`
		/// or `Object` is CS0104 "ambiguous between ...". Both are far more often
		/// meant as the Unity type in a Unity snippet, so the alias wins. Without
		/// these, a snippet writing `Object.DestroyImmediate(x)` or
		/// `Random.Range(0f, 1f)` -- both completely idiomatic Unity -- would fail
		/// to compile for a reason invisible in the snippet's own text.
		private static readonly string[][] UnityAliases =
		{
			new[] { "Object", "UnityEngine.Object" },
			new[] { "Random", "UnityEngine.Random" },
		};

		/// True when the reference set includes Unity, which it always does inside
		/// an Editor and never does in the offline self-test.
		///
		/// The Unity usings and aliases MUST be conditional: a `using X;` naming a
		/// namespace that no referenced assembly defines is CS0246, so emitting
		/// them unconditionally broke the self-test -- which deliberately runs with
		/// only mscorlib/System/System.Core so the Roslyn plumbing can be
		/// exercised without an Editor.
		private static bool HasUnityReferences(IEnumerable<string> referencePaths)
		{
			foreach (string path in referencePaths)
			{
				string name = Path.GetFileName(path);
				if (name.StartsWith("UnityEngine", StringComparison.Ordinal)) return true;
				if (name.StartsWith("UnityEditor", StringComparison.Ordinal)) return true;
			}
			return false;
		}

		/// True when the plugin's own assembly is referenced, which is what makes
		/// PiUnity.PiMaterial reachable from a snippet.
		private static bool HasPiUnityReference(IEnumerable<string> referencePaths)
		{
			foreach (string path in referencePaths)
			{
				if (Path.GetFileName(path).StartsWith("Assembly-CSharp", StringComparison.Ordinal)) return true;
			}
			return false;
		}

		private static string WithImplicitUsings(string code, IEnumerable<string> referencePaths)
		{
			// Materialise once: this is walked several times, and referencePaths may
			// be a lazy sequence.
			List<string> references = new List<string>(referencePaths);
			bool unity = HasUnityReferences(references);

			var wanted = new List<string>(BaseUsings);
			if (unity) wanted.Add("UnityEngine");
			if (unity) wanted.Add("UnityEditor");
			if (HasPiUnityReference(references)) wanted.Add("PiUnity");

			var needed = new List<string>();
			foreach (string ns in wanted)
			{
				// Skip anything the snippet already declares: a duplicate using at
				// the same scope is CS0105, and a snippet that is written
				// conventionally should not be warned about for it.
				if (Regex.IsMatch(code, @"using\s+" + Regex.Escape(ns) + @"\s*;")) continue;
				needed.Add("using " + ns + ";");
			}

			if (unity)
			{
				foreach (string[] alias in UnityAliases)
				{
					// A snippet that aliases these itself must not be warned (CS0576).
					if (Regex.IsMatch(code, @"using\s+" + Regex.Escape(alias[0]) + @"\s*=")) continue;
					needed.Add("using " + alias[0] + " = " + alias[1] + ";");
				}
			}

			if (needed.Count == 0) return code;
			return string.Join(" ", needed.ToArray()) + "\n" + code;
		}

		/// Compiles the snippet and loads the result. Throws with Roslyn's own
		/// diagnostics on failure, because those are the only thing that tells a
		/// model where its snippet is wrong.
		public Assembly Compile(string code, IEnumerable<string> referencePaths)
		{
			// Parse options: latest C#, plus PI_UNITY so a snippet can branch on
			// being run inside the bridge. Every Roslyn call below passes its
			// arguments BY NAME, because the exact overloads differ between
			// Roslyn versions (3.11's ParseText takes 5 parameters where earlier
			// ones took 4) and hard-coding an arity silently breaks on upgrade.
			object parseOptions = CreateWithLatestLanguage();
			parseOptions = InvokeNamed(parseOptions, "WithPreprocessorSymbols", new Dictionary<string, object>
			{
				{ "preprocessorSymbols", new List<string> { "PI_UNITY" } },
			});

			object tree = InvokeNamed(syntaxTreeType, "ParseText", true, new Dictionary<string, object>
			{
				{ "text", WithImplicitUsings(code, referencePaths) },
				{ "options", parseOptions },
				{ "path", "PiSnippet.cs" },
				{ "cancellationToken", System.Threading.CancellationToken.None },
			});

			object compilationOptions = CreateWithOutputKind(
				Enum.Parse(outputKindType, "DynamicallyLinkedLibrary"));

			List<object> references = new List<object>();
			foreach (string path in referencePaths)
			{
				if (string.IsNullOrEmpty(path)) continue;
				try
				{
					object reference = InvokeNamed(metadataReferenceType, "CreateFromFile", true, new Dictionary<string, object>
					{
						{ "path", path },
					});
					if (reference != null) references.Add(reference);
				}
				catch (Exception)
				{
					// A reference that will not load is dropped rather than
					// failing the whole compile. One unresolvable package should
					// not make every snippet uncompilable.
				}
			}

			// A unique name per compile: Assembly.Load caches by name, so reusing
			// one would make a second snippet see the first one's types.
			string assemblyName = "PiSnippet_" + Guid.NewGuid().ToString("N");

			object compilation = InvokeNamed(compilationType, "Create", true, new Dictionary<string, object>
			{
				{ "assemblyName", assemblyName },
				{ "syntaxTrees", new List<object> { tree } },
				{ "references", references },
				{ "options", compilationOptions },
			});

			List<string> errors = CollectErrors(InvokeNamed(compilation, "GetDiagnostics", new Dictionary<string, object>()));
			if (errors.Count > 0)
			{
				throw new Exception("C# snippet failed to compile:\n" + string.Join("\n", errors.ToArray()));
			}

			return EmitAndLoad(compilation, assemblyName);
		}

		private Assembly EmitAndLoad(object compilation, string assemblyName)
		{
			using (MemoryStream stream = new MemoryStream())
			{
				// Emit by named argument for the same reason as ParseText: the
				// overload set grew from 7 to 10 parameters across Roslyn
				// versions, and only "peStream" is actually required.
				object result = InvokeNamed(compilation, "Emit", new Dictionary<string, object>
				{
					{ "peStream", stream },
				});
				bool success = (bool)result.GetType().GetProperty("Success").GetValue(result, null);
				if (!success)
				{
					List<string> errors = CollectErrors(result.GetType().GetProperty("Diagnostics").GetValue(result, null));
					throw new Exception(
						"C# snippet emitted no assembly" +
						(errors.Count > 0 ? ":\n" + string.Join("\n", errors.ToArray()) : ".")
					);
				}

				return Assembly.Load(stream.ToArray());
			}
		}

		private List<string> CollectErrors(object diagnostics)
		{
			List<string> errors = new List<string>();
			if (diagnostics == null) return errors;
			object severityError = Enum.Parse(diagnosticSeverityType, "Error");
			// System.Collections.IEnumerable, not the generic one: only
			// System.Collections.Generic is imported here, so a bare IEnumerable
			// would resolve to IEnumerable<T> and demand a type argument.
			foreach (object diagnostic in (System.Collections.IEnumerable)diagnostics)
			{
				object severity = diagnosticType.GetProperty("Severity").GetValue(diagnostic, null);
				if (!Equals(severity, severityError)) continue;
				string text = diagnosticType.GetMethod("ToString", Type.EmptyTypes).Invoke(diagnostic, null).ToString();
				errors.Add(StripImplicitUsingOffset(text));
			}
			return errors;
		}

		/// Rewrites a diagnostic's line number back to the snippet's own numbering.
		///
		/// The implicit usings are prepended as a single line, so every line in
		/// the compiled text sits one lower than in what the caller wrote. Roslyn
		/// reports against the compiled text, so "PiSnippet.cs(12,19)" would
		/// point a model at a line that is not in its file -- an error message
		/// that is actively misleading about where the bug is. Subtracting the
		/// offset makes the reported line match the file on disk.
		///
		/// The match is anchored on the "(line,col)" shape immediately after the
		/// file name, so a column, a CS code, or a number inside the message text
		/// is never rewritten.
		private static string StripImplicitUsingOffset(string diagnostic)
		{
			return Regex.Replace(diagnostic, @"^(\s*[^\(\r\n]+\()(\d+)(,\d+\))",
				match =>
				{
					int line;
					return int.TryParse(match.Groups[2].Value, out line) && line > 1
						? match.Groups[1].Value + (line - 1) + match.Groups[3].Value
						: match.Value;
				});
		}

		private static object DefaultOf(Type type)
		{
			return type.IsValueType ? Activator.CreateInstance(type) : null;
		}

		/// Reflection call that binds arguments BY NAME.
		///
		/// This replaces positional matching, which was the original design and
		/// was wrong: Roslyn's overload set for ParseText and Emit changes
		/// between versions (3.11's ParseText has 5 parameters, earlier ones 4),
		/// so a hard-coded arity breaks on a Unity upgrade, and positional
		/// matching can silently pick the WRONG overload -- passing a null
		/// CancellationToken is fine, but mis-binding a Stream parameter is not.
		///
		/// Name binding also removes the need to know an arity at all: parameters
		/// left unbound take their declared default, or the type's default value
		/// when the parameter has none.
		private static object InvokeNamed(object target, string name, Dictionary<string, object> named)
		{
			MethodInfo method = FindMethod(target.GetType(), name, named, false);
			if (method == null) throw new Exception(target.GetType().FullName + " has no usable method: " + name);
			return method.Invoke(target, BuildArgs(method, named));
		}

		private static object InvokeNamed(Type type, string name, bool isStatic, Dictionary<string, object> named)
		{
			MethodInfo method = FindMethod(type, name, named, isStatic);
			if (method == null) throw new Exception(type.FullName + " has no usable static method: " + name);
			return method.Invoke(null, BuildArgs(method, named));
		}

		private static object[] BuildArgs(MethodInfo method, Dictionary<string, object> named)
		{
			ParameterInfo[] parameters = method.GetParameters();
			object[] args = new object[parameters.Length];
			for (int i = 0; i < parameters.Length; i++)
			{
				object value;
				if (named.TryGetValue(parameters[i].Name ?? "", out value))
				{
					args[i] = CoerceArgument(parameters[i].ParameterType, value);
				}
				else if (parameters[i].HasDefaultValue)
				{
					args[i] = parameters[i].DefaultValue;
				}
				else
				{
					args[i] = DefaultOf(parameters[i].ParameterType);
				}
			}
			return args;
		}

		/// Converts a List&lt;object&gt; into the strongly typed collection a Roslyn
		/// parameter asks for.
		///
		/// CSharpCompilation.Create takes IEnumerable&lt;SyntaxTree&gt; and
		/// IEnumerable&lt;MetadataReference&gt;. Reflection cannot infer those
		/// element types at a call site, so a List&lt;object&gt; is the natural thing
		/// to build -- but List&lt;object&gt; is not assignable to
		/// IEnumerable&lt;SyntaxTree&gt;, so a strict type check rejects it and the
		/// call looks like "no usable method". The list is rebuilt as List&lt;T&gt;
		/// here instead of loosening the check, which would risk binding the wrong
		/// overload.
		private static object CoerceArgument(Type target, object value)
		{
			if (value == null) return null;
			if (target.IsInstanceOfType(value)) return value;

			List<object> source = value as List<object>;
			if (source == null || !target.IsGenericType) return value;

			Type elementType = target.GetGenericArguments()[0];
			Type listType = typeof(List<>).MakeGenericType(elementType);
			// System.Collections.IList, not the generic one: only
			// System.Collections.Generic is imported, so a bare IList resolves to
			// IList<T> and demands a type argument.
			System.Collections.IList typed = (System.Collections.IList)Activator.CreateInstance(listType);
			for (int i = 0; i < source.Count; i++)
			{
				object item = source[i];
				if (item == null)
				{
					// Null is a legal element for a reference-typed element type,
					// so it is passed through rather than dropped. Dropping it
					// would shorten the caller's list by one position and shift
					// every later argument.
					if (elementType.IsValueType)
					{
						throw new ArgumentException(
							"Argument " + (i + 1) + " is null but " + target.Name + " holds " +
							elementType.Name + ", which cannot be null. A non-null list keeps its length."
						);
					}
					typed.Add(null);
					continue;
				}
				if (!elementType.IsInstanceOfType(item))
				{
					// Throwing rather than skipping, which is what this did.
					// Skipping produced a SHORTER list, FindMethod then bound the
					// overload successfully because a List<T> is assignable
					// whatever its length, and the snippet ran with the caller's
					// data silently absent -- no diagnostic, and a wrong result
					// in whatever the call did with the list. A compile error
					// names the position and both types; a dropped element names
					// nothing and costs a debugging session.
					throw new ArgumentException(
						"Argument " + (i + 1) + " is a " + item.GetType().FullName + ", but " +
						target.Name + " holds " + elementType.FullName + "."
					);
				}
				typed.Add(item);
			}
			return typed;
		}

		/// Picks the overload that has a parameter for every supplied name and
		/// whose parameter types accept the supplied values. Prefers the fewest
		/// parameters, so a full overload is chosen over a partial one when both
		/// could bind.
		private static MethodInfo FindMethod(Type type, string name, Dictionary<string, object> named, bool isStatic)
		{
			MethodInfo best = null;
			int bestCount = int.MaxValue;
			foreach (MethodInfo candidate in type.GetMethods())
			{
				if (candidate.Name != name) continue;
				if (candidate.IsStatic != isStatic) continue;

				ParameterInfo[] parameters = candidate.GetParameters();
				bool binds = true;
				foreach (KeyValuePair<string, object> pair in named)
				{
					ParameterInfo match = null;
					foreach (ParameterInfo parameter in parameters)
					{
						if (parameter.Name == pair.Key) { match = parameter; break; }
					}
					// A supplied name the overload does not have means this
					// overload cannot express the call.
					if (match == null) { binds = false; break; }
					if (pair.Value != null && !match.ParameterType.IsInstanceOfType(CoerceArgument(match.ParameterType, pair.Value)))
					{
						binds = false;
						break;
					}
				}
				if (!binds) continue;

				int count = parameters.Length;
				if (count < bestCount)
				{
					best = candidate;
					bestCount = count;
				}
			}
			return best;
		}
	}
}
