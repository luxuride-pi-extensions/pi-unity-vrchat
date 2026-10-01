// Part of the pi Unity bridge plugin. See PiUnityBridge.Core.cs for the
// protocol overview. The whole plugin is one assembly of partial classes, so
// Unity compiles it into Assembly-CSharp-Editor with no asmdef and no
// package dependencies, which is what lets it drop into any Unity 2021+
// project (a configured VRChat world, an existing game) that pi was pointed at
// rather than generated.
//
// World AUDIO helpers: read the scene's AudioSources, add one, and change a
// clip's importer load type.
//
// WHY THE IMPORTER IS REACHED BY REFLECTION
// -----------------------------------------
// A clip's load type lives on AudioImporter.defaultSampleSetting, and that
// property has not been a public member on every Unity version this plugin
// has to compile inside. A direct reference to a member one version lacks is
// a compile error in EVERY project the plugin is dropped into -- the exact
// failure mode the no-assembly-definition design exists to avoid -- so the
// property is fetched by name and the answer degrades to "<unknown>" instead
// of taking the whole plugin down. PiCensus reads the same setting the same
// way; the helper is duplicated here rather than shared because it belongs to
// a different report.
//
// EVERY NUMBER BELOW IS READ FROM A LIVE OBJECT
// A 3D source with maxDistance 1 and minDistance 1 renders, imports, plays
// and reports no error anywhere -- it is just inaudible one step from the
// listener. The only thing that catches it is reading the component, so every
// value printed here is read back out of the scene rather than remembered
// from what was written.

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Reflection;
using System.Text;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace PiUnity
{
	public static class PiAudio
	{
		public const string DefaultObjectName = "Pi_AudioSource";

		/// This report's own threshold for "a clip that will hurt on Quest".
		/// It is GUIDANCE, not a limit VRChat publishes and not anything read
		/// out of the project, and every line that uses it says so.
		public const float QuestLongClipSeconds = 30f;

		private static readonly string[] LoadTypeNames =
		{
			"DecompressOnLoad",
			"CompressedInMemory",
			"Streaming",
		};

		// ------------------------------------------------------------ describe

		/// Every AudioSource in the scene, and every clip those sources use.
		public static string DescribeAudio()
		{
			var report = new StringBuilder();
			AudioSource[] sources = UnityEngine.Object.FindObjectsOfType<AudioSource>(true);

			report.AppendLine("--- audio in the open scene [measured] ---");
			report.AppendLine("AudioSources=" + sources.Length
				+ "  (inactive objects included; a disabled source is still in the scene and still costs memory)");

			if (sources.Length == 0)
			{
				report.AppendLine("There is no AudioSource anywhere in this scene: nothing plays, in the editor or in-game.");
				report.AppendLine("Nothing was changed. Add one with action \"add-audio-source\".");
			}

			var clipsInUse = new List<AudioClip>();
			int twoDimensional = 0;
			int threeDimensional = 0;
			int withoutClip = 0;
			int looped = 0;
			int overGuidance = 0;
			var unclassified = new List<string>();

			foreach (AudioSource source in SortByPath(sources))
			{
				GameObject host = source.gameObject;
				report.AppendLine();
				report.AppendLine("  \"" + host.name + "\"  path=" + HierarchyPath(host));

				AudioClip clip = source.clip;
				if (clip == null)
				{
					withoutClip++;
					report.AppendLine("    clip: NONE ASSIGNED -- the source is silent until a clip is set on it");
				}
				else
				{
					report.AppendLine("    clip: \"" + clip.name + "\"  length=" + Num(clip.length)
						+ "s  channels=" + Number(clip.channels) + "  frequency=" + Number(clip.frequency) + "Hz"
						+ "  loadType=" + LoadTypeOf(clip));
					if (clip.length > QuestLongClipSeconds)
					{
						overGuidance++;
						report.AppendLine("      over " + Num(QuestLongClipSeconds) + "s, which is over the guidance below");
					}
					if (!clipsInUse.Contains(clip)) clipsInUse.Add(clip);
				}

				report.AppendLine("    volume=" + ReadNumber(source, "volume")
					+ "  rolloffMode=" + ReadEnumName(source, "rolloffMode")
					+ "  loop=" + ReadFlag(source, "loop")
					+ "  playOnAwake=" + ReadFlag(source, "playOnAwake"));

				float spatial = ReadFloat(source, "spatialBlend", 0f, out bool spatialRead);
				report.AppendLine("    spatialBlend=" + ReadNumber(source, "spatialBlend")
					+ "  minDistance=" + ReadNumber(source, "minDistance")
					+ "  maxDistance=" + ReadNumber(source, "maxDistance"));

				if (!spatialRead)
				{
					// Counting an unreadable blend as 2D would put a real number in
					// the 2D total that nothing in the scene supports.
					unclassified.Add("\"" + host.name + "\"");
					report.AppendLine("    UNCLASSIFIED: spatialBlend could not be read, so this source is in neither "
						+ "the 2D nor the 3D total below. It is not a 2D source by default.");
				}
				else if (spatial <= 0f)
				{
					twoDimensional++;
					report.AppendLine("    2D: full volume everywhere in the world, no falloff with distance.");
				}
				else
				{
					threeDimensional++;
					report.AppendLine("    3D: attenuated by distance from the listener.");
					AppendAudibility(report, source);
				}

				if (ReadFlagValue(source, "loop")) looped++;
			}

			report.AppendLine();
			report.AppendLine("--- totals ---");
			report.AppendLine("  AudioSources=" + sources.Length
				+ "  2D=" + twoDimensional + "  3D=" + threeDimensional);
			report.AppendLine("  with no clip assigned=" + withoutClip
				+ "  looping=" + looped
				+ "  over " + Num(QuestLongClipSeconds) + "s=" + overGuidance);
			if (unclassified.Count > 0)
			{
				report.AppendLine("  UNCLASSIFIED (spatialBlend unreadable, in neither the 2D nor the 3D count)="
					+ unclassified.Count + ": " + string.Join(", ", unclassified.ToArray()));
			}

			report.AppendLine();
			report.AppendLine("--- clips this scene references [" + clipsInUse.Count + "] ---");
			if (clipsInUse.Count == 0)
			{
				report.AppendLine("  No clip is assigned to any source, so the scene references no AudioClip asset.");
			}
			else
			{
				foreach (AudioClip clip in clipsInUse)
				{
					string path = AssetDatabase.GetAssetPath(clip);
					report.AppendLine("  \"" + clip.name + "\"  length=" + Num(clip.length)
						+ "s  channels=" + Number(clip.channels)
						+ "  frequency=" + Number(clip.frequency) + "Hz"
						+ "  loadType=" + LoadTypeOf(clip)
						+ (string.IsNullOrEmpty(path) ? "  [no asset path]" : "  [" + path + "]")
						+ (clip.length > QuestLongClipSeconds ? "  <- over " + Num(QuestLongClipSeconds) + "s" : ""));
				}
				report.AppendLine("  loadType is read from the clip's AudioImporter. \"<unknown>\" means this Unity "
					+ "version does not expose the setting, not that the clip has no load type.");
			}

			report.AppendLine();
			report.Append(QuestGuidance());
			return report.ToString();
		}

		/// Whether a 3D source can be heard from anywhere useful.
		///
		/// Unity's rolloff runs from minDistance to maxDistance and mutes the
		/// source at maxDistance, so a source whose max is not meaningfully above
		/// its min is silent one step from the listener -- with no warning
		/// anywhere in the Editor or the SDK.
		private static void AppendAudibility(StringBuilder report, AudioSource source)
		{
			float min = ReadFloat(source, "minDistance", 0f, out bool minRead);
			float max = ReadFloat(source, "maxDistance", 0f, out bool maxRead);
			if (!minRead || !maxRead)
			{
				report.AppendLine("      COULD NOT READ minDistance/maxDistance, so whether this source is audible "
					+ "at a distance is UNKNOWN. Both are reported above.");
				return;
			}
			if (!(max > min))
			{
				report.AppendLine("      NOT AUDIBLE at any sensible listener distance: maxDistance=" + Num(max)
					+ " is not greater than minDistance=" + Num(min)
					+ ", so the rolloff ends at " + Num(min) + "m and the source goes silent one step from the listener.");
				return;
			}
			if (max <= min * 2f)
			{
				report.AppendLine("      audible only out to " + Num(max) + "m (minDistance=" + Num(min)
					+ "m): a listener further than that hears nothing at all.");
				return;
			}
			report.AppendLine("      audible between " + Num(min) + "m and " + Num(max) + "m; silent beyond that.");
		}

		private static string QuestGuidance()
		{
			var report = new StringBuilder();
			report.AppendLine("--- Quest and long clips: GUIDANCE, not measured from this project ---");
			report.AppendLine("  Nothing below was read out of this scene or this build; it is how VRChat's mobile");
			report.AppendLine("  build behaves, and the " + Num(QuestLongClipSeconds) + "s figure is this report's round number.");
			report.AppendLine("  VRChat's Android build rejects or truncates long audio, so a clip of more than roughly");
			report.AppendLine("  " + Num(QuestLongClipSeconds) + "s is the most common reason a PC-only world fails a Quest build.");
			report.AppendLine("  Shorten such clips, or split them, rather than relying on Streaming to make them fit:");
			report.AppendLine("  Streaming lowers memory use but does not change the clip's length.");
			return report.ToString();
		}

		// --------------------------------------------------------------- add

		/// Adds an AudioSource to an existing object, or to a new one.
		public static string AddAudioSource(string objectName, string clip, string volume, string spatial, string loop)
		{
			AudioClip resolved = ResolveClipAsset(clip, "add-audio-source");

			// Parsed before anything is written: an exception thrown once the
			// component is on the object would leave a half-configured source behind.
			float volumeValue = ParseFloat(volume, 1f, "volume");
			float spatialValue = ParseFloat(spatial, 0f, "spatial");
			bool loopValue = ParseSwitch(loop, false);

			var report = new StringBuilder();
			report.AppendLine("--- add-audio-source ---");
			report.AppendLine("clip \"" + resolved.name + "\" [" + AssetDatabase.GetAssetPath(resolved) + "]"
				+ "  length=" + Num(resolved.length) + "s");

			GameObject target;
			if (string.IsNullOrEmpty(objectName) || objectName.Trim().Length == 0)
			{
				target = CreateHost(out string undoState);
				report.AppendLine("created GameObject \"" + target.name + "\"");
				report.AppendLine("undo: " + undoState);
			}
			else
			{
				target = PiWorldCreate.FindSceneObject(objectName.Trim(), out int matches);
				if (target == null)
				{
					throw new Exception("No GameObject named \"" + objectName.Trim() + "\" in the open scene, so nothing was "
						+ "added. Top-level objects in " + SceneManager.GetActiveScene().name + ": " + DescribeRoots() + ".");
				}
				if (matches > 1)
				{
					throw new Exception("\"" + objectName.Trim() + "\" names " + matches + " objects in the scene: "
						+ string.Join(", ", PathsNamed(objectName.Trim()).ToArray())
						+ ". A name that matches two cannot be configured unambiguously -- pass a \"Root/Child\" path.");
				}
				report.AppendLine("using the existing GameObject \"" + target.name + "\" at " + HierarchyPath(target));
			}

			// AudioSource is a built-in Unity component, not a UdonSharp script,
			// so PiUdon.AddBehaviourComponent would only reach plain AddComponent
			// for it: UdonSharp pairs a proxy MonoBehaviour with a backing
			// UdonBehaviour it compiles to, and AudioSource has no program to
			// compile to. Unity drives AudioSource itself.
			AudioSource audio = target.GetComponent<AudioSource>();
			if (audio != null)
			{
				// Reconfigured rather than duplicated: a GameObject may carry
				// several AudioSources, every one of them plays, and two sources
				// on one object double the sound with nothing in the scene to
				// say so.
				report.AppendLine("\"" + target.name + "\" already had an AudioSource, so that one was reconfigured "
					+ "instead of a second being added.");
			}
			else
			{
				audio = Undo.AddComponent<AudioSource>(target);
				report.AppendLine("added AudioSource to \"" + target.name + "\"");
			}

			audio.clip = resolved;
			audio.volume = volumeValue;
			audio.spatialBlend = spatialValue;
			audio.loop = loopValue;

			if (spatialValue > 0f)
			{
				if (!(audio.maxDistance > audio.minDistance))
				{
					// minDistance 1 with maxDistance 1 (Unity's default pair, and
					// what a new AudioSource carries) ends the rolloff at 1m:
					// audible at the object, silent a step away, no warning
					// anywhere. Any max at or below min has the same fault.
					float beforeMin = audio.minDistance;
					float beforeMax = audio.maxDistance;
					audio.minDistance = 1f;
					audio.maxDistance = 25f;
					report.AppendLine("set minDistance=" + Num(audio.minDistance) + " maxDistance=" + Num(audio.maxDistance)
						+ " (were " + Num(beforeMin) + "/" + Num(beforeMax) + "): max was not greater than min, which "
						+ "makes a 3D source inaudible one step from the listener.");
				}
				if (loopValue)
				{
					report.AppendLine("WARNING: a LOOPING 3D source is audibly repetitive -- the same few seconds "
						+ "(" + Num(resolved.length) + "s here) repeat from a fixed point for as long as it plays. "
						+ "Loop a 2D source instead, or shorten the clip.");
				}
			}

			report.AppendLine();
			report.AppendLine("--- read back from the live component ---");
			report.AppendLine("  object=" + HierarchyPath(target));
			report.AppendLine("  clip=" + (audio.clip == null ? "<none>" : "\"" + audio.clip.name + "\"")
				+ "  length=" + (audio.clip == null ? "n/a" : Num(audio.clip.length) + "s"));
			report.AppendLine("  volume=" + ReadNumber(audio, "volume")
				+ "  spatialBlend=" + ReadNumber(audio, "spatialBlend")
				+ "  loop=" + ReadFlag(audio, "loop")
				+ "  playOnAwake=" + ReadFlag(audio, "playOnAwake"));
			report.AppendLine("  minDistance=" + ReadNumber(audio, "minDistance")
				+ "  maxDistance=" + ReadNumber(audio, "maxDistance")
				+ "  rolloffMode=" + ReadEnumName(audio, "rolloffMode"));

			if (resolved.length > QuestLongClipSeconds)
			{
				report.AppendLine("GUIDANCE (not measured from this project): \"" + resolved.name + "\" is "
					+ Num(resolved.length) + "s, over the ~" + Num(QuestLongClipSeconds) + "s where VRChat's mobile "
					+ "build commonly rejects or truncates audio. Shorten or split it before publishing to Quest.");
			}

			report.AppendLine("inspect with unity_inspect { gameObject: \"" + target.name
				+ "\", component: \"AudioSource\" }");
			report.AppendLine(MarkActiveSceneDirty()
				? "scene marked dirty (save it to keep the change)"
				: "no valid active scene, so nothing was marked dirty");
			return report.ToString();
		}

		private static GameObject CreateHost(out string undoState)
		{
			GameObject go = new GameObject(PiWorldCreate.UniqueName(DefaultObjectName));
			// Same standard the other write paths hold to: a mistaken call is one
			// Ctrl-Z away. A registration Unity refuses is logged rather than
			// swallowed, because a silently un-undoable object looks exactly like
			// a correctly registered one.
			try
			{
				Undo.RegisterCreatedObjectUndo(go, "pi create " + go.name);
				undoState = "registered as one undo step";
			}
			catch (Exception error)
			{
				undoState = "NOT registered (" + error.Message + "): delete \"" + go.name + "\" by hand if unwanted";
			}
			return go;
		}

		// ---------------------------------------------------------- load type

		/// Sets a clip's importer load type, then reads the importer back.
		public static string SetClipLoadType(string clip, string loadType)
		{
			AudioClip resolved = ResolveClipAsset(clip, "set-clip-load-type");
			string requested = CanonicalLoadType(loadType);

			var report = new StringBuilder();
			report.AppendLine("--- set-clip-load-type ---");
			report.AppendLine("clip \"" + resolved.name + "\" [" + AssetDatabase.GetAssetPath(resolved) + "]"
				+ "  length=" + Num(resolved.length) + "s");

			string path = AssetDatabase.GetAssetPath(resolved);
			if (string.IsNullOrEmpty(path))
			{
				throw new Exception("\"" + resolved.name + "\" is not an asset, so it has no importer settings. "
					+ "A clip generated at runtime cannot have its load type changed from here.");
			}

			AudioImporter importer = AssetImporter.GetAtPath(path) as AudioImporter;
			if (importer == null)
			{
				throw new Exception("\"" + path + "\" is not imported as audio, so it has no AudioImporter to write a "
					+ "load type to. A clip packed inside a model or generated by the project is not an imported audio asset.");
			}

			if (!TryReadSampleSetting(importer, out string before, out string readFailure))
			{
				report.AppendLine("the load type could NOT be read before the change: " + readFailure);
			}
			else
			{
				report.AppendLine("load type before=" + before);
			}

			if (!TryWriteLoadType(importer, requested, out string writeFailure))
			{
				throw new Exception("Could not write loadType=" + requested + " on \"" + path + "\": " + writeFailure
					+ ". Nothing was persisted; the clip's import settings are unchanged.");
			}
			report.AppendLine("load type written=" + requested);

			// Both calls, in this order: WriteImportSettingsIfDirty is what puts
			// the new setting on disk, and ImportAsset is what makes the clip the
			// project now loads actually use it. Without the second, the .meta
			// carries the new value and the loaded clip keeps the old one.
			try
			{
				AssetDatabase.WriteImportSettingsIfDirty(path);
				AssetDatabase.ImportAsset(path);
				report.AppendLine("persisted with AssetDatabase.WriteImportSettingsIfDirty + AssetDatabase.ImportAsset");
			}
			catch (Exception error)
			{
				report.AppendLine("WARNING: the settings could NOT be persisted (" + error.Message + "). The in-memory "
					+ "importer holds " + requested + ", but the .meta on disk was not confirmed rewritten.");
			}

			AudioImporter after = AssetImporter.GetAtPath(path) as AudioImporter;
			if (after == null)
			{
				report.AppendLine("FAILED: the importer for \"" + path + "\" is gone after the reimport, so the load type "
					+ "on disk could not be read. Check the Editor log and the clip's import settings in the Inspector.");
			}
			else if (!TryReadSampleSetting(after, out string onDisk, out string readBackFailure))
			{
				report.AppendLine("COULD NOT READ BACK: " + readBackFailure + ". Whether " + requested
					+ " is now on disk is UNCONFIRMED -- open the clip's import settings to check.");
			}
			else
			{
				report.AppendLine("load type on disk after the reimport=" + onDisk);
				if (string.Equals(onDisk, requested, StringComparison.Ordinal))
				{
					report.AppendLine("verified: the importer on disk holds " + onDisk + ", which is what was asked for.");
				}
				else
				{
					report.AppendLine("*** MISMATCH: " + requested + " was requested and " + onDisk
						+ " is what the importer reports after the reimport. The write did NOT take -- check the clip's "
						+ "import settings in the Inspector, and whether the clip is inside a model or overridden by a "
						+ "per-platform override, before trusting this change.");
				}
			}

			report.AppendLine();
			report.AppendLine("--- what each mode costs ---");
			report.AppendLine("  DecompressOnLoad: the most memory -- the whole clip is held decompressed -- and no seek gap.");
			report.AppendLine("  Streaming: very little memory, but it can gap on slow storage when it starts or loops.");
			report.AppendLine("  CompressedInMemory: the usual compromise -- held compressed in memory, no gap, "
				+ "decoded on the fly.");
			return report.ToString();
		}

		private static string CanonicalLoadType(string loadType)
		{
			string text = (loadType ?? "").Trim();
			if (text.Length == 0)
			{
				throw new Exception("set-clip-load-type requires a load type. Accepted values: "
					+ string.Join(", ", LoadTypeNames) + ".");
			}
			foreach (string name in LoadTypeNames)
			{
				if (name.Equals(text, StringComparison.OrdinalIgnoreCase)) return name;
			}
			throw new Exception("\"" + text + "\" is not a load type. Accepted values: "
				+ string.Join(", ", LoadTypeNames) + " (case-insensitive).");
		}

		// ------------------------------------------------------ clip assets

		/// The clip an action's `clip` value names, as an ASSET.
		private static AudioClip ResolveClipAsset(string wanted, string action)
		{
			string text = (wanted ?? "").Trim();
			if (text.Length == 0)
			{
				throw new Exception(
					action + " requires a clip. Pass the clip's asset path (\"Assets/Audio/beep.ogg\") or its "
					+ "asset name (\"beep\"). It is resolved as a clip ASSET: a clip is a project asset, not a scene "
					+ "object, so a scene object name will not resolve.");
			}

			// A slash can only mean a path: an asset name cannot contain one, and
			// there is no scene object to fall back to.
			if (text.IndexOf('/') >= 0)
			{
				AudioClip loaded = AssetDatabase.LoadAssetAtPath<AudioClip>(text);
				if (loaded != null) return loaded;
				throw new Exception("No AudioClip asset at \"" + text + "\". Copy the clip's own path from the Project "
					+ "window -- this must be the .wav/.ogg/.mp3 file itself, not a folder and not a scene object name.");
			}

			var exact = new List<string>();
			var partial = new List<string>();
			foreach (string guid in AssetDatabase.FindAssets(text + " t:AudioClip"))
			{
				string path = AssetDatabase.GUIDToAssetPath(guid);
				if (path.Length == 0 || exact.Contains(path) || partial.Contains(path)) continue;
				// Checked on the LOADED asset rather than on the guid: FindAssets
				// answers with the containing asset's path for a clip packed
				// inside a model, and only a real AudioClip may be assigned.
				AudioClip candidate = AssetDatabase.LoadAssetAtPath<AudioClip>(path);
				if (candidate == null) continue;
				if (candidate.name == text) exact.Add(path);
				else partial.Add(path);
			}

			if (exact.Count == 1) return AssetDatabase.LoadAssetAtPath<AudioClip>(exact[0]);
			if (exact.Count > 1)
			{
				throw new Exception("\"" + text + "\" names " + exact.Count + " clip assets: "
					+ string.Join(", ", exact.ToArray()) + ". A name that matches two cannot be picked for you -- pass "
					+ "the asset path instead.");
			}
			if (partial.Count == 1) return AssetDatabase.LoadAssetAtPath<AudioClip>(partial[0]);
			if (partial.Count > 1)
			{
				throw new Exception("No clip asset is named exactly \"" + text + "\". These clip assets contain that "
					+ "name: " + string.Join(", ", partial.ToArray()) + ". Pass one of those paths.");
			}
			throw new Exception("\"" + text + "\" was not found as a clip asset (searched the whole project for "
				+ "AudioClip). Pass the clip's asset path, such as \"Assets/Audio/beep.ogg\".");
		}

		// ------------------------------------------------- importer settings

		/// The enum member a caller named, or the reason the name is unusable.
		///
		/// Enum.ToObject looks like it takes a name and does not: that overload
		/// accepts a numeric value or an enum value, and throws "must be an enum
		/// base or an underlying type" for a string. Enum.Parse is the one that
		/// reads a name, so the name is matched here and the caller never sees
		/// the exception.
		private static object ParseEnum(Type enumType, string name)
		{
			foreach (string member in Enum.GetNames(enumType))
			{
				if (member.Equals(name, StringComparison.OrdinalIgnoreCase)) return Enum.Parse(enumType, member);
			}
			throw new Exception("\"" + name + "\" is not a member of " + enumType.Name + ". Use one of: "
				+ string.Join(", ", Enum.GetNames(enumType)) + ".");
		}

		/// The importer's sample-setting property, under either spelling.
		///
		/// Unity renamed it: 2020 and earlier expose `defaultSampleSetting` (no
		/// trailing s), 2021.2 and later expose `defaultSampleSettings`. A lookup
		/// for only one of them returns null on half the Unity versions in use,
		/// which reads as "this version has no load type" and is a wrong answer
		/// on every Unity that has it under the other name.
		private static PropertyInfo SampleSettingProperty(AudioImporter importer)
		{
			Type type = importer.GetType();
			foreach (string name in new[] { "defaultSampleSettings", "defaultSampleSetting" })
			{
				PropertyInfo property = type.GetProperty(name);
				if (property != null) return property;
			}
			return null;
		}

		/// The importer's sample setting, reached by NAME.
		///
		/// Returns false -- with the reason -- when the property cannot be read,
		/// which is a different fact from a load type of "Streaming" and needs a
		/// different fix.
		private static bool TryReadSampleSetting(AudioImporter importer, out string loadType, out string reason)
		{
			loadType = "";
			reason = "";
			try
			{
				PropertyInfo property = SampleSettingProperty(importer);
				if (property == null || !property.CanRead)
				{
					reason = "AudioImporter exposes no readable defaultSampleSetting/defaultSampleSettings in this "
						+ "Unity version";
					return false;
				}
				object settings = property.GetValue(importer, null);
				FieldInfo field = settings == null
					? null
					: settings.GetType().GetField("loadType", BindingFlags.Public | BindingFlags.Instance);
				if (field == null)
				{
					reason = "defaultSampleSetting has no loadType field in this Unity version";
					return false;
				}
				object value = field.GetValue(settings);
				loadType = value == null ? "" : value.ToString();
				return true;
			}
			catch (Exception error)
			{
				reason = "reading the importer threw: " + error.Message;
				return false;
			}
		}

		/// Writes one field of the sample setting rather than the whole struct.
		///
		/// AudioImporterSampleSettings also carries quality, sample rate,
		/// channels and compression. Building one from scratch to write back
		/// would reset all of those to this Unity version's defaults, so the
		/// struct is read, its loadType is changed on the boxed copy, and the
		/// copy is written back whole -- the other fields travel unchanged.
		private static bool TryWriteLoadType(AudioImporter importer, string loadType, out string reason)
		{
			reason = "";
			try
			{
				PropertyInfo property = SampleSettingProperty(importer);
				if (property == null || !property.CanRead || !property.CanWrite)
				{
					reason = "AudioImporter exposes no writable defaultSampleSetting/defaultSampleSettings in this "
						+ "Unity version, so the load type cannot be set from here";
					return false;
				}
				object settings = property.GetValue(importer, null);
				FieldInfo field = settings == null
					? null
					: settings.GetType().GetField("loadType", BindingFlags.Public | BindingFlags.Instance);
				if (field == null || !field.FieldType.IsEnum)
				{
					reason = "the sample setting has no loadType enum field in this Unity version";
					return false;
				}

				// Built from the FIELD's own enum type, so the caller passes a
				// name and never has to name a Unity type that a version may
				// spell differently.
				object value = ParseEnum(field.FieldType, loadType);
				field.SetValue(settings, value);
				// Written as the boxed ENUM. Reflecting SetValue on a struct
				// field mutates the box in place but hands the property an
				// Object whose runtime type is the enum, not the struct -- which
				// AudioImporter rejects as "not an enum base or its underlying
				// type". A struct cannot be SetValue'd through reflection, so it
				// is copied field by field back into the box the property got.
				object boxed = property.GetValue(importer, null);
				if (boxed == null || boxed.GetType().IsValueType == false)
				{
					reason = "the sample setting did not read back as a value type, so the load type cannot be "
						+ "written through reflection on this Unity version";
					return false;
				}
				foreach (FieldInfo member in boxed.GetType()
					.GetFields(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance))
				{
					object current = member.GetValue(boxed);
					if (member.Name == field.Name) current = value;
					member.SetValue(boxed, current);
				}
				property.SetValue(importer, boxed, null);
				return true;
			}
			catch (Exception error)
			{
				reason = error.Message;
				return false;
			}
		}

		private static string LoadTypeOf(AudioClip clip)
		{
			string path = AssetDatabase.GetAssetPath(clip);
			if (!string.IsNullOrEmpty(path))
			{
				try
				{
					// The importer is the setting that is saved; the AudioClip's own
					// loadType only reflects an imported asset.
					AudioImporter importer = AssetImporter.GetAtPath(path) as AudioImporter;
					if (importer != null && TryReadSampleSetting(importer, out string fromImporter, out _))
					{
						return fromImporter;
					}
				}
				catch (Exception)
				{
					// Fall through to the clip's own copy below.
				}
			}
			try
			{
				return clip.loadType.ToString();
			}
			catch (Exception)
			{
				return "<unknown>";
			}
		}

		// ------------------------------------------------------------ reading

		/// A numeric member of a component, with "could not read" kept apart
		/// from a real zero.
		///
		/// AudioSource.volume of 0 is a deliberate mute; a volume this cannot
		/// read is an unknown that no report should print as silence.
		private static bool TryReadMember(Component component, string member, out object value)
		{
			value = null;
			if (component == null) return false;
			Type type = component.GetType();
			while (type != null)
			{
				PropertyInfo property = type.GetProperty(member, BindingFlags.Public | BindingFlags.Instance);
				if (property != null && property.CanRead && property.GetIndexParameters().Length == 0)
				{
					try { value = property.GetValue(component, null); return value != null; }
					catch (Exception) { value = null; }
					break;
				}
				FieldInfo field = type.GetField(member, BindingFlags.Public | BindingFlags.Instance);
				if (field != null)
				{
					try { value = field.GetValue(component); return value != null; }
					catch (Exception) { value = null; }
					break;
				}
				type = type.BaseType;
			}
			return false;
		}

		private static string ReadNumber(Component component, string member)
		{
			if (!TryReadMember(component, member, out object value)) return "<unreadable>";
			if (!(value is float number)) return "<not a number: " + value.GetType().Name + ">";
			return Num(number);
		}

		private static float ReadFloat(Component component, string member, float fallback, out bool read)
		{
			read = TryReadMember(component, member, out object value) && value is float;
			return read ? (float)value : fallback;
		}

		private static string ReadFlag(Component component, string member)
		{
			if (!TryReadMember(component, member, out object value)) return "<unreadable>";
			if (!(value is bool flag)) return "<not a bool: " + value.GetType().Name + ">";
			return flag ? "true" : "false";
		}

		private static bool ReadFlagValue(Component component, string member)
		{
			return TryReadMember(component, member, out object value) && value is bool flag && flag;
		}

		private static string ReadEnumName(Component component, string member)
		{
			if (!TryReadMember(component, member, out object value)) return "<unreadable>";
			return value.ToString();
		}

		// ----------------------------------------------------------- parsing

		private static float ParseFloat(string raw, float fallback, string label)
		{
			string text = (raw ?? "").Trim();
			if (text.Length == 0) return fallback;
			if (!float.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out float parsed))
			{
				throw new Exception("\"" + raw + "\" is not a number, so " + label + " was left at " + Num(fallback)
					+ " and nothing else about the call was guessed.");
			}
			return parsed;
		}

		private static bool ParseSwitch(string raw, bool fallback)
		{
			string text = (raw ?? "").Trim();
			if (text.Length == 0) return fallback;
			if (text.Equals("true", StringComparison.OrdinalIgnoreCase) || text == "1"
				|| text.Equals("yes", StringComparison.OrdinalIgnoreCase)) return true;
			if (text.Equals("false", StringComparison.OrdinalIgnoreCase) || text == "0"
				|| text.Equals("no", StringComparison.OrdinalIgnoreCase)) return false;
			throw new Exception("Expected \"true\" or \"false\", not \"" + raw + "\".");
		}

		// ------------------------------------------------------------ display

		private static string Num(float value)
		{
			return value.ToString("0.###", CultureInfo.InvariantCulture);
		}

		private static string Number(int value)
		{
			return value.ToString(CultureInfo.InvariantCulture);
		}

		private static List<AudioSource> SortByPath(AudioSource[] sources)
		{
			var sorted = new List<AudioSource>(sources);
			// Stable output across two runs over the same scene: FindObjectsOfType
			// returns no guaranteed order, so a report that reordered itself
			// between calls reads as the scene having changed.
			sorted.Sort((left, right) => string.CompareOrdinal(
				HierarchyPath(left.gameObject), HierarchyPath(right.gameObject)));
			return sorted;
		}

		private static string HierarchyPath(GameObject gameObject)
		{
			var parts = new List<string>();
			Transform cursor = gameObject.transform;
			while (cursor != null)
			{
				parts.Insert(0, cursor.name);
				cursor = cursor.parent;
			}
			return string.Join("/", parts.ToArray());
		}

		private static List<string> PathsNamed(string name)
		{
			var paths = new List<string>();
			foreach (Transform child in UnityEngine.Object.FindObjectsOfType<Transform>(true))
			{
				if (child.gameObject.name == name) paths.Add(HierarchyPath(child.gameObject));
			}
			return paths;
		}

		private static string DescribeRoots()
		{
			Scene scene = SceneManager.GetActiveScene();
			if (!scene.IsValid()) return "<no open scene>";
			var names = new List<string>();
			foreach (GameObject root in scene.GetRootGameObjects()) names.Add(root.name);
			return names.Count == 0 ? "<none>" : string.Join(", ", names.ToArray());
		}

		private static bool MarkActiveSceneDirty()
		{
			Scene scene = SceneManager.GetActiveScene();
			if (!scene.IsValid()) return false;
			EditorSceneManager.MarkSceneDirty(scene);
			return true;
		}
	}
}