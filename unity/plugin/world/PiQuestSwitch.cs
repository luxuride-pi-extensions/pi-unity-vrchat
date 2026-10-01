// Part of the pi Unity bridge plugin. See PiUnityBridge.Core.cs for the
// protocol overview.

// EasyQuestSwitch helpers.
//
// EasyQuestSwitch is a small package with one idea: a scene carries a hidden
// EQS_DATA object listing the objects that must be configured differently for
// PC and for Android, and the package rewrites them when the build target
// changes. Everything is driven by EQS_Data.
//
// The package is optional and its types are reached by reflection, for the
// same reason as VRWorldToolkit in PiWorldToolkit.cs: a compile-time
// reference would break this plugin in every project without the package.
//
// What this adds on top of the package's own window:
//  - the state of the switch set, without opening the window, including which
//    per-platform values actually DIFFER (the entries where PC and Quest agree
//    are the ones costing nothing and worth seeing too);
//  - adding an object to the switch set, including creating the EQS_DATA
//    holder when the scene has none -- the package only creates it from its
//    window, so a scene that has never had the window opened has no way to
//    hold entries at all;
//  - applying a platform without changing the project's active build target,
//    which is what previewing the Quest state of a world actually needs.

using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace PiUnity
{
	public static class PiQuestSwitch
	{
		/// vrchat.jordo.easyquestswitch
		public const string Package = "vrchat.jordo.easyquestswitch";

		private const string DataType = "EasyQuestSwitch.EQS_Data";

		/// The name the package's own window gives the holder object, and the
		/// name its watcher looks for. Renaming it would make the package stop
		/// finding it, so this is a constant and not a name parameter.
		private const string HolderName = "EQS_DATA";

		public static bool IsInstalled()
		{
			return VrcResolve.Type(DataType) != null;
		}

		private static Component FindData()
		{
			Type type = VrcResolve.Type(DataType);
			if (type == null) return null;
			foreach (Transform child in UnityEngine.Object.FindObjectsOfType<Transform>(true))
			{
				if (child.name != HolderName) continue;
				Component component = child.GetComponent(type);
				if (component != null) return component;
			}
			return null;
		}

		/// The switch set's list, or null when the component's list was never
		/// initialised.
		///
		/// The field is public and typed, yet a freshly added component carries a
		/// NULL list -- nothing initialises it outside the package's window, which
		/// is why a scene that has never had that window opened cannot hold
		/// entries at all. So "not a list" is a real state of this package, not a
		/// version problem, and it is reported as an empty switch set.
		private static IList Entries(Component data)
		{
			return PiWorldToolkit.ReadFieldByName(data, "Objects") as IList;
		}

		/// The list, created if absent. Writing the list back through the field
		/// (rather than mutating a fresh local) is what makes the addition
		/// survive a scene save.
		private static IList EnsureEntries(Component data)
		{
			IList entries = Entries(data);
			if (entries != null) return entries;
			FieldInfo field = Bridge.FindInstanceField(data.GetType(), "Objects");
			if (field == null) return null;
			object created = Activator.CreateInstance(field.FieldType);
			if (!PiWorldToolkit.WriteFieldByName(data, "Objects", created)) return null;
			return created as IList;
		}


		private static BuildTarget TargetFor(string name)
		{
			if (string.IsNullOrEmpty(name)) return BuildTarget.StandaloneWindows64;
			string lowered = name.Trim().ToLowerInvariant();
			if (lowered == "android" || lowered == "quest" || lowered == "mobile") return BuildTarget.Android;
			if (lowered == "pc" || lowered == "windows" || lowered == "standalone"
				|| lowered == "standalonewindows64" || lowered == "desktop") return BuildTarget.StandaloneWindows64;
			throw new Exception("target must be \"pc\" or \"android\" (quest). \"" + name + "\" is neither. "
				+ "The package supports exactly two platforms; it handles any other build target by refusing to switch.");
		}

		/// The switch set as it stands: what is in it, and for each entry what
		/// the two platforms get.
		public static string Describe()
		{
			if (!IsInstalled())
			{
				return Package + " is not installed. Add it to keep one scene working on both PC and Android; "
					+ "without it a world has exactly one state, and it is the state you authored on the last platform you built.";
			}
			var report = new StringBuilder();
			Component data = FindData();
			report.AppendLine("active build target: " + EditorUserBuildSettings.activeBuildTarget);
			if (data == null)
			{
				report.AppendLine("No " + HolderName + " object in the scene, so this world switches nothing: "
					+ "every object has one state and it is the state in the scene file.");
				report.AppendLine("Add one with action \"quest-add\" (or Tools > EasyQuestSwitch in the menu), then "
					+ "add the objects whose PC and Quest settings differ.");
				return report.ToString();
			}
			report.AppendLine(HolderName + " on " + data.gameObject.name
				+ "  hidden=" + (data.gameObject.hideFlags != HideFlags.None) + "  tag=" + data.gameObject.tag);
			IList entries = Entries(data);
			if (entries == null)
			{
				report.AppendLine("entries: 0 (its list was never initialised, which is the state of every scene "
					+ "whose EasyQuestSwitch window has never been opened)");
				report.AppendLine("Add one with action \"quest-add\".");
				return report.ToString();
			}
			report.AppendLine("entries: " + entries.Count);
			for (int index = 0; index < entries.Count; index++)
			{
				object entry = entries[index];
				report.AppendLine("  [" + index + "] " + DescribeEntry(entry));
			}
			report.AppendLine("Each entry keeps two values for every property the handler knows, one for PC and one");
			report.AppendLine("for Android, and writes the right one when the build target changes. An entry with no");
			report.AppendLine("difference listed below is one that currently switches nothing.");
			return report.ToString();
		}

		private static string DescribeEntry(object entry)
		{
			if (entry == null) return "<null entry>";
			string name = entry.GetType().Name;
			UnityEngine.Object target = ReadInstanceField(entry, "Target") as UnityEngine.Object;
			object handler = ReadInstanceField(entry, "Type");
			var line = new StringBuilder();
			line.Append(name);
			line.Append(" target=");
			line.Append(TargetName(target));
			line.Append(" handler=");
			line.Append(handler == null ? "<not validated>" : handler.GetType().Name);
			if (handler != null) line.Append(DescribeShared(handler));
			return line.ToString();
		}

		private static string TargetName(UnityEngine.Object target)
		{
			if (target == null) return "<null>";
			if (target is Component component) return component.gameObject.name;
			if (target is GameObject gameObject) return gameObject.name;
			string path = AssetDatabase.GetAssetPath(target);
			return path.Length == 0 ? target.name : path;
		}

		/// The per-platform pairs a handler holds, read off the handler's own
		/// shared fields.
		///
		/// Every handler keeps its two variants in EasyQuestSwitch.Fields
		/// shared objects with PC and Quest members, so one loop over the
		/// fields covers Light, Renderer, Material and the rest without this
		/// file knowing any of their property names. A handler whose fields are
		/// not shared objects reports nothing here rather than a wrong value.
		private static string DescribeShared(object handler)
		{
			var differences = new List<string>();
			var lines = new List<string>();
			Type type = handler.GetType();
			while (type != null)
			{
				foreach (FieldInfo field in type.GetFields(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly))
				{
					if (!field.FieldType.Namespace.StartsWith("EasyQuestSwitch.Fields")) continue;
					object shared = field.GetValue(handler);
					object pc = ReadSharedMember(shared, "PC");
					object quest = ReadSharedMember(shared, "Quest");
					if (pc == null && quest == null) continue;
					string pcText = pc == null ? "<none>" : pc.ToString();
					string questText = quest == null ? "<none>" : quest.ToString();
					bool differs = pcText != questText;
					if (differs) differences.Add(field.Name);
					lines.Add("      " + field.Name + ": PC=" + pcText + "  Quest=" + questText
						+ (differs ? "" : "  (same)"));
				}
				type = type.BaseType;
			}
			if (lines.Count == 0) return "";
			var report = new StringBuilder();
			report.AppendLine();
			foreach (string line in lines) report.AppendLine(line);
			report.Append("    switches: ");
			report.AppendLine(differences.Count == 0 ? "nothing (every property is the same on both platforms)" : string.Join(", ", differences.ToArray()));
			return report.ToString();
		}

		private static object ReadSharedMember(object shared, string member)
		{
			if (shared == null) return null;
			Type type = shared.GetType();
			while (type != null)
			{
				FieldInfo field = type.GetField(member, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
				if (field != null)
				{
					try { return field.GetValue(shared); } catch (Exception) { return null; }
				}
				type = type.BaseType;
			}
			return null;
		}

		private static object ReadInstanceField(object instance, string fieldName)
		{
			if (instance == null) return null;
			Type type = instance.GetType();
			while (type != null)
			{
				FieldInfo field = type.GetField(fieldName, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
				if (field != null)
				{
					try { return field.GetValue(instance); } catch (Exception) { return null; }
				}
				type = type.BaseType;
			}
			return null;
		}

		/// Applies a platform's values without switching the project's build
		/// target, which is the point: an author checking "what does this world
		/// look like on Quest" should not have to switch platforms and reload
		/// the whole project to find out.
		///
		/// The package's own ApplyTarget is used rather than a replay of it,
		/// because it is the code that knows how to write each handler and it
		/// records the prefab overrides. This only warns when the current
		/// platform is one the package refuses to switch to.
		public static string Apply(string targetName)
		{
			if (!IsInstalled()) throw new Exception(Package + " is not installed, so there is nothing to apply.");
			Component data = FindData();
			if (data == null) throw new Exception("This scene has no " + HolderName + " object, so there is no switch set to apply.");
			BuildTarget target = TargetFor(targetName);
			IList entries = Entries(data);
			MethodInfo apply = data.GetType().GetMethod("ApplyTarget", BindingFlags.Public | BindingFlags.Instance);
			if (apply == null) throw new Exception(DataType + ".ApplyTarget is absent in this package version.");
			var report = new StringBuilder();
			report.AppendLine("applying " + target + " to " + (entries == null ? 0 : entries.Count)
				+ " entries (project build target left at " + EditorUserBuildSettings.activeBuildTarget + ")");
			apply.Invoke(data, new object[] { target });
			report.AppendLine("ApplyTarget returned. It logs a warning per entry it could not apply;");
			report.AppendLine("check the Editor console for those if a setting did not change.");
			Scene scene = SceneManager.GetActiveScene();
			if (scene.IsValid()) EditorSceneManager.MarkSceneDirty(scene);
			report.AppendLine("The scene now holds the " + target + " state and is unsaved. Undo, or save it, before leaving.");
			return report.ToString();
		}

		/// A COMPONENT of the object, when the caller named one.
		///
		/// The package's own window exposes this as a dropdown, because the handler
		/// is chosen from the target's type: an entry pointing at a GameObject only
		/// ever gets the GameObject handler, which switches Active, Tag, Layer and
		/// the static flags and nothing else. A light, a renderer or a collider
		/// has to be added AS that component for its own per-platform values to
		/// exist.
		private static UnityEngine.Object PickComponent(UnityEngine.Object owner, string componentName)
		{
			if (string.IsNullOrEmpty(componentName)) return null;
			Component component = FindComponent(owner, componentName);
			if (component == null)
			{
				throw new Exception(componentName + " is not on " + owner.name + ". It has: "
					+ string.Join(", ", ComponentNames(owner).ToArray()));
			}
			return component;
		}

		private static Component FindComponent(UnityEngine.Object owner, string componentName)
		{
			if (!(owner is GameObject gameObject)) return null;
			foreach (Component component in gameObject.GetComponents<Component>())
			{
				if (component == null) continue;
				if (component.GetType().Name == componentName || component.GetType().FullName == componentName) return component;
			}
			return null;
		}

		private static List<string> ComponentNames(UnityEngine.Object owner)
		{
			var names = new List<string>();
			if (!(owner is GameObject gameObject)) { names.Add("<not an object with components>"); return names; }
			foreach (Component component in gameObject.GetComponents<Component>())
			{
				if (component != null) names.Add(component.GetType().Name);
			}
			if (names.Count == 0) names.Add("<none>");
			return names;
		}

		/// Adds a scene object (or an asset) to the switch set.
		///
		/// Handler selection is delegated to the package's own ValidateData,
		/// which reflects over its handlers and attaches the right one -- the
		/// alternative is a second copy of that table here, which would go stale
		/// the moment the package adds a handler.
		public static string Add(string objectName, string assetPath, string componentName)
		{
			if (!IsInstalled()) throw new Exception(Package + " is not installed, so there is no switch set to add to.");
			UnityEngine.Object target = ResolveTarget(objectName, assetPath, componentName);
			Component data = FindData();
			bool created = false;
			if (data == null)
			{
				// RegisterCreatedObjectUndo, not just adding a component: this
				// creates the whole holder GameObject, and one Ctrl-Z has to
				// remove the holder together with the entry added to it below.
				GameObject holder = new GameObject(HolderName);
				Undo.RegisterCreatedObjectUndo(holder, "pi add " + HolderName);
				holder.tag = "EditorOnly";
				holder.hideFlags = HideFlags.HideInHierarchy;
				data = holder.AddComponent(VrcResolve.Type(DataType));
				created = true;
			}
			IList entries = EnsureEntries(data);
			if (entries == null)
			{
				if (created) UnityEngine.Object.DestroyImmediate(data.gameObject);
				throw new Exception(DataType + ".Objects is absent in this package version, so an entry cannot be "
					+ "added from here. Open Tools > EasyQuestSwitch and add it in the window.");
			}
			// The entry type comes from the list's own generic argument rather than
			// a hard-coded name, so a package that renames or re-nests EQS_Data.Data
			// still gets an entry of the right type.
			Type entryListType = entries.GetType();
			if (!entryListType.IsGenericType || entryListType.GetGenericArguments().Length != 1)
			{
				throw new Exception("The switch set is a " + entryListType.Name + " rather than a typed list, so pi "
					+ "cannot construct an entry for it. Add the object in Tools > EasyQuestSwitch.");
			}
			object entry = Activator.CreateInstance(entryListType.GetGenericArguments()[0]);
			Type entryType = entry.GetType();
			FieldInfo targetField = Bridge.FindInstanceField(entryType, "Target");
			if (targetField == null) throw new Exception("The entry type " + entryType.Name + " has no Target field; "
				+ "this package version is not one whose entries pi understands.");
			targetField.SetValue(entry, target);
			entries.Add(entry);
			int index = entries.Count - 1;
			MethodInfo validate = data.GetType().GetMethod("ValidateData", BindingFlags.Public | BindingFlags.Instance);
			if (validate != null) validate.Invoke(data, new object[] { index });
			var report = new StringBuilder();
			if (created)
			{
				report.AppendLine("created " + HolderName + " (tagged EditorOnly, hidden in the hierarchy), which the "
					+ "package's window would otherwise have been the only way to create");
			}
			report.AppendLine("added " + TargetName(target) + " at index " + index + ": " + DescribeEntry(entry));
			report.AppendLine("undo: one Ctrl-Z removes it"
				+ (created ? ", and with it the " + HolderName + " this call created"
					+ " (its entries are inside it, so they cannot be reverted separately). "
					: ". The " + HolderName + " and its other entries stay. "));
			report.AppendLine("It now has whatever value the current platform uses. Use action \"quest-apply\" with");
			report.AppendLine("the other platform to see, and to switch, the other set of values.");
			Scene scene = SceneManager.GetActiveScene();
			if (scene.IsValid()) EditorSceneManager.MarkSceneDirty(scene);
			return report.ToString();
		}

		private static UnityEngine.Object ResolveTarget(string objectName, string assetPath, string componentName)
		{
			if (!string.IsNullOrEmpty(assetPath))
			{
				UnityEngine.Object asset = AssetDatabase.LoadAssetAtPath<UnityEngine.Object>(assetPath);
				if (asset == null) throw new Exception("No asset at " + assetPath + ".");
				UnityEngine.Object onAsset = PickComponent(asset, componentName);
				return onAsset ?? asset;
			}
			if (string.IsNullOrEmpty(objectName)) throw new Exception("quest-add needs object (a scene object name) or path (an asset path).");
			GameObject found = null;
			foreach (Transform child in UnityEngine.Object.FindObjectsOfType<Transform>(true))
			{
				if (child.gameObject.name != objectName) continue;
				if (found != null)
				{
					throw new Exception("More than one object in the scene is named \"" + objectName + "\". "
						+ "Rename one of them: a name that matches two objects cannot be added unambiguously.");
				}
				found = child.gameObject;
			}
			if (found == null) throw new Exception("No object named \"" + objectName + "\" in the scene (inactive objects count).");
			return PickComponent(found, componentName) ?? found;
		}
	}
}