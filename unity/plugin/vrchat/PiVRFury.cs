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
// Every one of those returns a COMPONENT, so adding a VRCFury element is
// "create the component, then let the caller set its fields through
// unity_inspect" -- the same path as any other component, and the only one that
// works when the component's fields are serialized and version-specific.
//
// VRCFury reported ZERO public types from its runtime assembly in the recon and
// its editor assembly was not loaded, so anything reaching for a VRCFury type by
// a compile-time `using` would be a guess. FuryComponents is in
// com.vrcfury.api, which IS public and loaded.

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

		/// The VRCFury components pi knows how to add, and the type each is
		/// created through. Read out of the live assembly, not a guess.
		///
		/// "create" is the FuryComponents factory method; "component" is the type
		/// it returns. Exposed as data so the TS layer and a model can read the
		/// list without hard-coding it, and so a project with a different VRCFury
		/// version reports what IT has rather than what this file expects.
		public static readonly string[] AddableElements = { "armature-link", "full-controller", "toggle", "socket" };

		private static string FactoryType(string element)
		{
			switch (element)
			{
				case "armature-link": return "com.vrcfury.api.FuryArmatureLink";
				case "full-controller": return "com.vrcfury.api.FuryFullController";
				case "toggle": return "com.vrcfury.api.FuryToggle";
				case "socket": return "com.vrcfury.api.FurySocket";
				default: return null;
			}
		}

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

		/// Every VRCFury component on an avatar, with the object it sits on.
		public static List<Component> FindAll(GameObject avatar)
		{
			var found = new List<Component>();
			if (avatar == null) return found;
			string[] types =
			{
				"com.vrcfury.api.FuryArmatureLink", "com.vrcfury.api.FuryFullController",
				"com.vrcfury.api.FuryToggle", "com.vrcfury.api.FurySocket",
				"VF.Model.VRCFury", "VF.Gizmo.UVShrink", "VF.Model.UVMeasure",
			};
			foreach (string type in types) found.AddRange(VrcResolve.ComponentsOn(avatar, type));
			return found;
		}

		/// Adds a VRCFury element to a GameObject.
		///
		/// Returns the created component, or null with the reason logged when
		/// VRCFury is not installed or the element name is unknown. It does NOT
		/// throw: a project without VRCFury is a normal state, not an error, and
		/// the caller's next step (inspect and set fields) needs to know the
		/// difference between "created" and "unavailable" without catching.
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
			return created as Component;
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
