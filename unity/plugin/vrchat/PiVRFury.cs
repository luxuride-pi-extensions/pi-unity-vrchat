// Part of the pi Unity bridge plugin. See PiUnityBridge.Core.cs for the
// protocol overview. The whole plugin is one assembly of partial classes, so
// Unity compiles it into Assembly-CSharp-Editor with no asmdef and no
// package dependencies, which is what lets it drop into any Unity 2021+
// project (a configured VRChat world, an existing game) that pi was pointed at
// rather than generated.

// VRCFury helpers.
//
// API surface verified by reflection over a live project, not guessed:
//
//   com.vrcfury.api.FuryComponents
//     static FuryArmatureLink CreateArmatureLink(GameObject)
//     static FuryFullController CreateFullController(GameObject)
//     static FuryToggle         CreateToggle(GameObject)
//     static FurySocket         CreateSocket(GameObject)
//   com.vrcfury.api.FuryUtils
//     static GameObject GetBone(GameObject avatarObject, HumanBodyBones bone)
//
// VRCFury reported ZERO public types from its runtime assembly in the recon and
// its editor assembly was not loaded, so anything reaching for a VRCFury type by
// a compile-time `using` would be a guess. FuryComponents is in
// com.vrcfury.api, which IS public and loaded.
//
// CORRECTION (VRCFury 1.1365+): the header above used to claim every
// Create* returns a COMPONENT. It does not. The return types live in
// `com.vrcfury.api.Components` and are plain data objects deriving from
// System.Object -- they wrap a VF.Model.VRCFury (or VF.Component
// .VRCFuryHapticSocket) that the factory itself adds to the GameObject:
//
//   namespace com.vrcfury.api.Components {
//     public class FuryArmatureLink { ... }   // no base class
//     public class FuryFullController { ... }
//     public class FuryToggle { ... }
//     public class FurySocket { ... }
//   }
//
// So `created as Component` was ALWAYS null, which made unity_vrc
// action=vrcfury-add report a hard failure after having successfully created
// the element. Add() below therefore returns the COMPONENT the factory added,
// and the wrapper's type name separately. See Add and ComponentTypes.

using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;

namespace PiUnity
{
	public static class PiVRCFury
	{
		public const string Package = "com.vrcfury.vrcfury";
		private const string Components = "com.vrcfury.api.FuryComponents";
		private const string Utils = "com.vrcfury.api.FuryUtils";

		/// The VRCFury elements pi knows how to add, and the factory method each
		/// is created through. Read out of the live assembly, not a guess.
		///
		/// Exposed as data so the TS layer and a model can read the list without
		/// hard-coding it, and so a project with a different VRCFury version
		/// reports what IT has rather than what this file expects.
		public static readonly string[] AddableElements = { "armature-link", "full-controller", "toggle", "socket" };

		private static string FactoryMethod(string element)
		{
			switch (element)
			{
				case "armature-link": return "CreateArmatureLink";
				case "full-controller": return "CreateFullController";
				case "toggle": return "CreateToggle";
				case "socket": return "CreateSocket";
				default: return null;
			}
		}

		public static bool IsInstalled()
		{
			return VrcResolve.Has(Components);
		}

		/// The Component types VRCFury actually puts on a GameObject.
		///
		/// These are the ones `FindAll` scans for. The four
		/// `com.vrcfury.api.Components.Fury*` types that used to be in this list
		/// are NOT components (see the header), so asking Unity for them on a
		/// GameObject threw:
		///
		///   GetComponent requires that the requested component
		///   'FuryArmatureLink' derives from MonoBehaviour or Component or is
		///   an interface.
		///
		/// It threw because VrcResolve.Type's simple-name fallback found the real
		/// FuryArmatureLink after the exact lookup for the old namespace missed.
		/// That fallback is still there for names a caller only knows loosely, so
		/// the Component check in VrcResolve.ComponentsOn/ComponentOn is the real
		/// guard -- but a name that can never resolve to a Component has no
		/// business being in the scan list at all.
		///
		/// VF.Model.VRCFury holds one FeatureModel (`content`) per component and
		/// is what actually lands on the avatar for armature links, toggles and
		/// full controllers; VF.Component.VRCFuryHapticSocket is what a socket
		/// adds. The UV types are absent in 1.1365 but the guard keeps them
		/// harmless if a version brings them back.
		private static readonly string[] ComponentTypes =
		{
			"VF.Model.VRCFury", "VF.Component.VRCFuryHapticSocket",
			"VF.Gizmo.UVShrink", "VF.Model.UVMeasure",
		};

		/// The wrapper object types the public API returns, and the element each
		/// maps to. NOT components -- see the header. Kept for reporting, so a
		/// caller can tell "VRCFury is installed but this build renamed the
		/// element" from "VRCFury is absent".
		private static readonly Dictionary<string, string> ElementTypes = new Dictionary<string, string>(StringComparer.Ordinal)
		{
			{ "armature-link", "com.vrcfury.api.Components.FuryArmatureLink" },
			{ "full-controller", "com.vrcfury.api.Components.FuryFullController" },
			{ "toggle", "com.vrcfury.api.Components.FuryToggle" },
			{ "socket", "com.vrcfury.api.Components.FurySocket" },
		};

		/// Every VRCFury component on an avatar, with the object it sits on.
		public static List<Component> FindAll(GameObject avatar)
		{
			var found = new List<Component>();
			if (avatar == null) return found;
			foreach (string type in ComponentTypes) found.AddRange(VrcResolve.ComponentsOn(avatar, type));
			return found;
		}

		/// The wrapper object type `element` is created as, or null if the name
		/// is unknown. For reporting only: the wrapper is not a Component and
		/// cannot be inspected or edited as one.
		public static string ElementType(string element)
		{
			string found;
			return ElementTypes.TryGetValue(element, out found) ? found : null;
		}

		/// Adds a VRCFury element to a GameObject.
		///
		/// Returns the COMPONENT the factory added to `target` -- which is what
		/// the caller can then inspect and edit -- or null with the reason logged
		/// when VRCFury is not installed or the element name is unknown. It does
		/// NOT throw: a project without VRCFury is a normal state, not an error,
		/// and the caller's next step (inspect and set fields) needs to know the
		/// difference between "created" and "unavailable" without catching.
		///
		/// The factory's own return value is a `com.vrcfury.api.Components.Fury*`
		/// wrapper, NOT a component, so returning it (as this used to, via
		/// `as Component`) always yielded null. The component is found by asking
		/// `target` for the VRCFury component types after the call, which is
		/// version-tolerant: whichever type this build adds, the scan finds it.
		public static Component Add(GameObject target, string element)
		{
			if (target == null) return null;
			string method = FactoryMethod(element);
			if (method == null)
			{
				Debug.LogWarning("[pi] unknown VRCFury element \"" + element + "\". Known: " + string.Join(", ", AddableElements));
				return null;
			}
			object created = VrcResolve.Call(Components, method, new object[] { target }, new[] { typeof(GameObject) });
			if (created == null) return null;

			// The wrapper is a plain object, so it carries no useful position in
			// the scene; the component is the addressable thing. Prefer the type
			// the factory just added, then fall back to any VRCFury component on
			// the object.
			foreach (string type in ComponentTypes)
			{
				Component component = VrcResolve.ComponentOn(target, type);
				if (component != null) return component;
			}
			Debug.LogWarning("[pi] VRCFury created " + created.GetType().FullName
				+ " on " + target.name + " but added no component to inspect.");
			return null;
		}

		/// The GameObject a VRCFury element should be added to, given a bone or
		/// transform. A toggle belongs on a bone; a full controller belongs on
		/// the avatar root. Getting this wrong is the most common VRCFury
		/// mistake, so it is an explicit helper rather than a convention.
		public static GameObject TargetFor(string element, GameObject avatar, HumanBodyBones? bone)
		{
			if (bone == null) return avatar;
			if (avatar == null) return null;
			object boneObject = VrcResolve.Call(Utils, "GetBone", new object[] { avatar, bone.Value },
				new[] { typeof(GameObject), typeof(HumanBodyBones) });
			return boneObject as GameObject;
		}
	}
}
