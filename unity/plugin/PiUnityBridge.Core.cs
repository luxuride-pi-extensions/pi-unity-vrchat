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
		private static string LastCompileError = "";
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
			// CompilationPipeline, not CompilePipeline -- adding the namespace
			// alone left the name unresolved (CS0103).
			CompilationPipeline.compilationFinished += OnCompilationFinished;
			EditorApplication.delayCall += () => { LastCompileError = ""; };
		}

		private static void Configure(string dir)
		{
			BridgeDir = dir;
			RequestsDir = Path.Combine(BridgeDir, "requests");
			ResponsesDir = Path.Combine(BridgeDir, "responses");
			HeartbeatPath = Path.Combine(BridgeDir, "heartbeat");
			EditorLogPath = Path.Combine(BridgeDir, "Editor-csharp.log");
			Directory.CreateDirectory(RequestsDir);
			Directory.CreateDirectory(ResponsesDir);
		}

		private static string ProjectRoot()
		{
			return Directory.GetParent(Application.dataPath).FullName;
		}
	}
}
