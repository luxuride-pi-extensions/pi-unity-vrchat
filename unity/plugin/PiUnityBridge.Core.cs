// Part of the pi Unity bridge plugin. See PiUnityBridge.Core.cs for the
// protocol overview. The whole plugin is one assembly of partial classes, so
// Unity compiles it into Assembly-CSharp-Editor with no asmdef and no package
// dependencies, which is what lets it drop into any Unity 2021+ project.

// Bootstrap and the request poll loop
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
		private const double PollIntervalSeconds = 0.5;

		private static string BridgeDir;
		private static string RequestsDir;
		private static string ResponsesDir;
		private static string HeartbeatPath;
		private static string EditorLogPath;
		private static string CompilerMessagesPath;
		private static string LastCompileError = "";
		private static string CompilerMessagesCapturedAssembly = "";
		private static string[] CompilerMessagesCapturedErrors = new string[0];
		private static string[] CompilerMessagesCapturedWarnings = new string[0];
		private static double NextPollTime;
		private static bool Registered;

		// InitializeOnLoadMethod, not InitializeOnLoad: the latter is a
		// *class* attribute and fails to compile on a method (CS0592), which
		// left the whole bridge dead with no heartbeat and no error in the
		// status tool. This one is the documented form for a static method.
		[InitializeOnLoadMethod]
		private static void Initialize()
		{
			if (Registered) return;
			Registered = true;

			// The env var is how pi tells an already-open Editor where to listen.
			// If it is absent the project is simply not pi-managed right now, and
			// we must stay completely inert: an artist's Editor should not be
			// polling, logging, or reacting to a stale cache dir.
			string configured = Environment.GetEnvironmentVariable("PI_UNITY_BRIDGE_DIR");
			if (string.IsNullOrEmpty(configured))
			{
				// Fall back to a marker file pi writes into the project so a
				// manually launched `Unity -projectPath` still finds the bridge
				// without needing the env var exported in the user's shell.
				string marker = Path.Combine(ProjectRoot(), "unity-bridge-dir.txt");
				if (!File.Exists(marker)) return;
				configured = (File.ReadAllText(marker) ?? "").Trim();
				if (string.IsNullOrEmpty(configured)) return;
			}

			Configure(configured);
			EditorApplication.update += Poll;
			SubscribeCompilerEvents();
		}

		private static void SubscribeCompilerEvents()
		{
			// Detach before attaching so a domain reload -- which re-runs this
			// whole method, because a compile ends in one -- does not stack
			// subscriptions, and tolerate the teardown period where the old
			// static is already null.
			//
			// The re-subscribe is not optional. A script compile ends in a domain
			// reload, so a handler attached once per process stays attached to
			// the PRE-reload CompilationPipeline instance, holding the PRE-reload
			// CompilerMessagesPath: the capture then went to a BridgeDir the
			// Editor had stopped using, or was skipped because the field was
			// null. Observed live: a compile that ran to success produced no
			// capture at all, forever, because the first reload after the feature
			// shipped was the reload that installed it.
			try
			{
				CompilationPipeline.compilationStarted -= OnCompilationCycleStarted;
				CompilationPipeline.compilationFinished -= OnCompilationCycleFinished;
			}
			catch (Exception)
			{
				/* teardown may have already cleared the field */
			}
			CompilationPipeline.compilationStarted += OnCompilationCycleStarted;
			CompilationPipeline.compilationFinished += OnCompilationCycleFinished;
			try
			{
				CompilationPipeline.assemblyCompilationFinished -= OnAssemblyCompilationFinished;
			}
			catch (Exception)
			{
				/* teardown may have already cleared the field */
			}
			CompilationPipeline.assemblyCompilationFinished += OnAssemblyCompilationFinished;
		}

		/// Records that a compile cycle is under way, and that it settled.
		///
		/// The assembly-level events cannot answer either question on their own.
		/// Unity only raises assemblyCompilationFinished for an assembly it
		/// actually BUILDS: a request in a project whose editor scripts are
		/// already up to date touches no assembly, so neither assembly event
		/// fires at all, and a caller waiting on diagnostics waits out its whole
		/// budget for a compile that finished in a second. Measured live in the
		/// Test World: compilationStarted and compilationFinished both fired 1s
		/// after such a request, while neither assembly event fired ever.
		///
		/// So the two levels answer different questions, and both are needed.
		/// compilationStarted/Finished say a cycle ran and finished;
		/// assemblyCompilationFinished says what the compiler said about it. A
		/// cycle that ends with no capture on disk is one Unity had no assembly
		/// to report, which is a real "compiled, nothing to fix" -- and is
		/// written explicitly here so a waiter can tell it from "never ran",
		/// which would otherwise look identical.
		///
		/// Carries Bee.BeeDriver.ActiveBuild, which is a compile job with no
		/// messages on it; the payload is unread on purpose.
		private static void OnCompilationCycleStarted(object activeBuild)
		{
			try
			{
				if (string.IsNullOrEmpty(CompilerMessagesPath)) return;
				WriteCompileCapture("", new string[0], new string[0], false);
			}
			catch (Exception error)
			{
				AppendLog("compile start marker failed: " + error.Message);
			}
		}

		private static void OnCompilationCycleFinished(object activeBuild)
		{
			try
			{
				if (string.IsNullOrEmpty(CompilerMessagesPath)) return;
				// The assembly handler already wrote the compiler's own verdict,
				// which is always the better answer; this must not overwrite it.
				// The empty case is the point of the handler: a cycle that built
				// no assembly raises no assembly event, so without this write a
				// no-op compile in a clean project leaves the start marker
				// standing and the wait runs out on a compile that is already
				// done.
				if (!string.IsNullOrEmpty(CompilerMessagesCapturedAssembly)) return;
				WriteCompileCapture(CompilerMessagesCapturedAssembly, CompilerMessagesCapturedErrors, CompilerMessagesCapturedWarnings, true);
			}
			catch (Exception error)
			{
				AppendLog("compile finish marker failed: " + error.Message);
			}
		}

		private static void Configure(string dir)
		{
			BridgeDir = dir;
			RequestsDir = Path.Combine(BridgeDir, "requests");
			ResponsesDir = Path.Combine(BridgeDir, "responses");
			HeartbeatPath = Path.Combine(BridgeDir, "heartbeat");
			EditorLogPath = Path.Combine(BridgeDir, "Editor-csharp.log");
			CompilerMessagesPath = Path.Combine(BridgeDir, "compile-messages.json");
			Directory.CreateDirectory(RequestsDir);
			Directory.CreateDirectory(ResponsesDir);
		}

		private static string ProjectRoot()
		{
			return Directory.GetParent(Application.dataPath).FullName;
		}
	}
}
