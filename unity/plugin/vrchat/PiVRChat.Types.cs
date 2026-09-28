// Part of the pi Unity bridge plugin. See PiUnityBridge.Core.cs for the
// protocol overview. The whole plugin is one assembly of partial classes, so
// Unity compiles it into Assembly-CSharp-Editor with no asmdef and no
// package dependencies, which is what lets it drop into any Unity 2021+
// project (a configured VRChat world, an existing game) that pi was pointed at
// rather than generated.

// VRChat-specific helpers. See the subdirectory's PiVRChat.Types.cs header for
// why every package type is reached by reflection.
//
// The rule for this whole directory: a project without VRCFury, without Avatar
// Optimizer, or with no VRChat SDK at all must still COMPILE this plugin. A
// direct `using VRCFury;` would break the build of every non-VRChat project
// the plugin is designed to drop into, which is the one property that makes it
// usable at all.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using UnityEngine;

namespace PiUnity
{
	/// Reflection plumbing shared by the VRChat helpers.
	///
	/// Type and member names here were NOT guessed: every one was read out of
	/// the live assemblies by reflection over Chiffon's package set. That
	/// matters, because every guessed Unity/VRChat API in this plugin's history
	/// was wrong -- SkeletonBone.transform, Mono.CSharp.AssemblyBuilder,
	/// SerializedProperty.enumDisplayName, HumanBodyBones.HandL and
	/// Resources.FindObjectsOfTypeAll(Type[]) all compiled or nearly compiled and
	/// then failed at runtime or compile time. Names are the risky part; shapes
	/// are not.
	internal static class VrcResolve
	{
		private static readonly Dictionary<string, Type> typeCache = new Dictionary<string, Type>(StringComparer.Ordinal);
		private static readonly Dictionary<string, Assembly> assemblyCache = new Dictionary<string, Assembly>(StringComparer.Ordinal);

		/// An assembly, by simple name prefix. "" means every loaded assembly.
		internal static Assembly Assembly_(string prefix)
		{
			if (assemblyCache.TryGetValue(prefix, out Assembly cached)) return cached;
			foreach (Assembly assembly in AppDomain.CurrentDomain.GetAssemblies())
			{
				string name = assembly.GetName().Name ?? "";
				if (prefix.Length == 0 || name == prefix || name.StartsWith(prefix + ".", StringComparison.Ordinal))
				{
					assemblyCache[prefix] = assembly;
					return assembly;
				}
			}
			assemblyCache[prefix] = null;
			return null;
		}

		/// A public type, by full name, searched across every loaded assembly.
		///
		/// Searched globally rather than per-package because the VRChat packages
		/// split across ~35 assemblies (com.anatawa12.avatar-optimizer.*,
		/// nadena.dev.ndmf.*, lilToon.*) whose exact names differ per version, and
		/// a caller naming the TYPE should not have to know the ASSEMBLY.
		internal static Type Type(string fullName)
		{
			if (typeCache.TryGetValue(fullName, out Type cached)) return cached;
			// Fast path: a direct GetType on the assembly that declares the name.
			foreach (Assembly assembly in AppDomain.CurrentDomain.GetAssemblies())
			{
				try
				{
					Type found = assembly.GetType(fullName, false);
					if (found != null)
					{
						typeCache[fullName] = found;
						return found;
					}
				}
				catch (Exception)
				{
					// An assembly whose reference set cannot resolve; keep looking.
				}
			}
			// Fallback: match on simple name, for a caller that only knows it.
			int lastDot = fullName.LastIndexOf('.');
			string simple = lastDot >= 0 ? fullName.Substring(lastDot + 1) : fullName;
			foreach (Assembly assembly in AppDomain.CurrentDomain.GetAssemblies())
			{
				try
				{
					foreach (Type candidate in assembly.GetExportedTypes())
					{
						if (candidate.Name != simple) continue;
						typeCache[fullName] = candidate;
						return candidate;
					}
				}
				catch (Exception)
				{
					// ReflectionTypeLoadException; the next assembly may still hold it.
				}
			}
			typeCache[fullName] = null;
			return null;
		}

		/// True when a type is present, i.e. the owning package is installed.
		internal static bool Has(string fullName) => Type(fullName) != null;

		/// A static method, bound by name and argument types.
		internal static MethodInfo StaticMethod(string typeName, string methodName, params Type[] parameterTypes)
		{
			Type type = Type(typeName);
			if (type == null) return null;
			try
			{
				return type.GetMethod(methodName, BindingFlags.Public | BindingFlags.Static, null, parameterTypes, null);
			}
			catch (AmbiguousMatchException)
			{
				// A package may expose the name more than once; take the first that
				// binds rather than failing the whole helper.
				foreach (MethodInfo candidate in type.GetMethods(BindingFlags.Public | BindingFlags.Static))
				{
					if (candidate.Name != methodName) continue;
					if (candidate.GetParameters().Length != parameterTypes.Length) continue;
					return candidate;
				}
			}
			return null;
		}

		/// Invokes a static method, returning null when the package is absent or
		/// the call throws. Callers MUST treat null as "unavailable", never as a
		/// result, so a missing package degrades instead of crashing the bridge.
		internal static object Call(string typeName, string methodName, object[] args, Type[] parameterTypes)
		{
			MethodInfo method = StaticMethod(typeName, methodName, parameterTypes);
			if (method == null) return null;
			try
			{
				return method.Invoke(null, args);
			}
			catch (TargetInvocationException error)
			{
				Debug.LogWarning("[pi] " + typeName + "." + methodName + " threw: " + (error.InnerException ?? error));
				return null;
			}
			catch (Exception error)
			{
				Debug.LogWarning("[pi] " + typeName + "." + methodName + " unavailable: " + error.Message);
				return null;
			}
		}

		/// Every component of type `typeName` on `root` and its children.
		internal static List<Component> ComponentsOn(GameObject root, string typeName)
		{
			Type type = Type(typeName);
			var found = new List<Component>();
			if (type == null || root == null) return found;
			foreach (Component component in root.GetComponentsInChildren<Transform>(true))
			{
				if (type.IsInstanceOfType(component)) found.Add(component);
			}
			return found;
		}

		/// A single component of type `typeName`, or null.
		internal static Component ComponentOn(GameObject root, string typeName)
		{
			Type type = Type(typeName);
			if (type == null || root == null) return null;
			foreach (Transform child in root.GetComponentsInChildren<Transform>(true))
			{
				Component component = child.GetComponent(type);
				if (component != null) return component;
			}
			return null;
		}

		/// The Assets-relative path of a component's backing object, or null for a
		/// scene instance.
		///
		/// The distinction matters for edits: a component on a PREFAB ASSET and
		/// the same component on a scene INSTANCE holding that prefab are
		/// different targets, and writing to the wrong one is either lost on
		/// reload or edits every instance at once.
		internal static string AssetPathOf(Component component)
		{
			if (component == null) return null;
			try
			{
				string path = UnityEditor.AssetDatabase.GetAssetPath(component);
				return string.IsNullOrEmpty(path) ? null : path;
			}
			catch (Exception)
			{
				return null;
			}
		}

		/// True when the component lives on a prefab asset rather than a scene
		/// instance.
		internal static bool IsAssetComponent(Component component)
		{
			return AssetPathOf(component) != null;
		}
	}
}
