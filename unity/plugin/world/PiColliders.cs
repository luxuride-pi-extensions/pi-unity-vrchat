// Part of the pi Unity bridge plugin. See PiUnityBridge.Core.cs for the
// protocol overview. The whole plugin is one assembly of partial classes, so
// Unity compiles it into Assembly-CSharp-Editor with no asmdef and no
// package dependencies, which is what lets it drop into any Unity 2021+
// project (a configured VRChat world, an existing game) that pi was pointed at
// rather than generated.
//
// Colliders and VRChat triggers: give a named scene object a collider, and add
// the legacy VRC_Trigger behaviour that makes a trigger do anything at all.
//
// WHY THE TRIGGER BEHAVIOUR IS THE LEGACY SDK TYPE
// ------------------------------------------------
// VRC.SDKBase.VRC_Trigger (com.vrchat.base) is what a world still fires
// OnTriggerEnter against, and it is RESOLVED rather than referenced: a
// compile-time `using VRC.SDKBase;` takes the whole plugin down in every
// project without the SDK, which is the exact failure this plugin's
// no-assembly-definition design exists to avoid. The collider is only the
// volume; the behaviour is what gets called, so a trigger Collider with no
// behaviour on it does nothing in VRChat.
//
// A CAPSULE HAS TWO DIMENSIONS AND THIS ACTION TAKES ONE
// ----------------------------------------------------
// radius and height are independent and a single number cannot carry both, so
// the radius takes it and the height is derived as twice the radius -- the
// shape is a sphere of that radius stretched to its diameter. A capsule that
// has to be long and thin is adjusted on the component afterwards, and the
// report prints what the component actually holds rather than what was asked
// for.

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace PiUnity
{
	public static class PiColliders
	{
		/// com.vrchat.base, resolved at runtime; see the header note.
		private const string TriggerBehaviourType = "VRC.SDKBase.VRC_Trigger";

		/// The shapes this action can configure, in the order they are offered
		/// to a caller that mistyped one.
		private static readonly string[] Shapes = { "Box", "Sphere", "Capsule", "Mesh" };

		/// Gives an object a collider of the requested shape, reusing the one
		/// already there when it has one.
		public static string AddCollider(string objectName, string shape, string size, string isTrigger)
		{
			string wantedShape = ParseShape(shape);
			// Parsed before anything is written: a throw once the collider is on
			// the object would leave a half-configured one behind.
			bool trigger = ParseSwitch(isTrigger, true);
			ParseSize(size, out bool hasSize, out float sizeValue);
			GameObject target = ResolveTarget(objectName);

			var report = new StringBuilder();
			report.AppendLine("--- add-collider: " + target.name + " ---");
			report.AppendLine("object: " + target.name + "  path=" + HierarchyPath(target));
			report.AppendLine("shape: " + wantedShape);

			Collider collider = ApplyShape(report, target, wantedShape, hasSize, sizeValue, trigger);
			AppendColliderReadBack(report, collider);
			AppendTriggerConsequence(report, collider);
			report.AppendLine(MarkSceneDirty()
				? "scene marked dirty (save it to keep the change)"
				: "no valid active scene, so nothing was marked dirty");
			return report.ToString();
		}

		/// A trigger collider plus the VRC_Trigger behaviour, which is what a
		/// VRChat world actually calls.
		///
		/// The collider goes through the same code as add-collider rather than
		/// through a second copy of it: two paths that both shape a collider is
		/// two paths that can disagree about what one is.
		public static string AddTrigger(string objectName, string shape, string size)
		{
			string wantedShape = ParseShape(shape);
			ParseSize(size, out bool hasSize, out float sizeValue);
			GameObject target = ResolveTarget(objectName);

			// Resolved before the collider is added, so a project without
			// com.vrchat.base is told that with nothing half-built on the object.
			Type behaviourType = VrcResolve.Type(TriggerBehaviourType);
			// ABSTRACT, and therefore not addable at all. Verified live on the
			// Test World SDK: both VRC.SDKBase.VRC_Trigger (base type
			// VRC.SDKBase.VRC_Interactable) and VRC.SDK3.Components.VRCInteractable
			// are abstract, and go.AddComponent on either returns null rather than
			// throwing -- so an unchecked call reaches the next line and dies with
			// a bare NullReferenceException. AddComponent returning null for an
			// abstract type is Unity's way of saying "no", and it is not a failure
			// this action can report on its own.
			if (behaviourType != null && behaviourType.IsAbstract)
			{
				throw new Exception(
					TriggerBehaviourType + " is ABSTRACT in this project's SDK, so it cannot be added to anything -- "
					+ "Unity refuses to instantiate an abstract component. Nothing was added to \"" + target.name
					+ "\" and the object is unchanged. This is not a bug in the project: in the current SDK the "
					+ "trigger behaviour is written in UdonSharp (OnTriggerEnter / OnTriggerStay / OnTriggerExit on a "
					+ "UdonSharpBehaviour) or in a Udon program, and the collider above is the volume those fire from. "
					+ "Add the script with action \"create-behaviour\" on a UdonSharp type, or author one in the "
					+ "Inspector."
				);
			}
			if (behaviourType == null)
			{
				throw new Exception(
					TriggerBehaviourType + " is not available (com.vrchat.base is not installed, or no assembly in "
					+ "this Editor declares it), so no trigger was added to \"" + target.name + "\" and the object is "
					+ "unchanged. A plain trigger Collider with no behaviour on it does NOTHING in VRChat: the collider "
					+ "is only the volume, the VRC_Trigger is what gets called. Add an action \"create-behaviour\" for "
					+ "it by hand once the SDK is present, or add the behaviour in the Inspector."
				);
			}

			var report = new StringBuilder();
			report.AppendLine("--- add-trigger: " + target.name + " ---");
			report.AppendLine("object: " + target.name + "  path=" + HierarchyPath(target));
			report.AppendLine("shape: " + wantedShape + "  (isTrigger is always true on this action)");

			Collider collider = ApplyShape(report, target, wantedShape, hasSize, sizeValue, true);
			AppendColliderReadBack(report, collider);

			report.AppendLine("behaviour:");
			Component existing = target.GetComponent(behaviourType);
			if (existing != null)
			{
				report.AppendLine("  " + TriggerBehaviourType + " was already on \"" + target.name
					+ "\"; it was left alone rather than duplicated.");
			}
			else
			{
				bool udonSharpSetup;
				Component behaviour;
				try
				{
					// Goes through PiUdon.AddBehaviourComponent rather than
					// AddComponent: a UdonSharp script added plainly gets no
					// UdonBehaviour behind it and throws its fields away at build.
					behaviour = PiUdon.AddBehaviourComponent(target, behaviourType,
						"pi add " + behaviourType.Name + " to " + target.name, out udonSharpSetup);
				}
				catch (Exception error)
				{
					throw new Exception(
						"Could not add " + TriggerBehaviourType + " to \"" + target.name + "\": " + error.Message
						+ ". The collider above is on the object but has nothing to call it."
					);
				}
				// AddBehaviourComponent returns a Component that is null when the
				// component could not be created; every line below dereferences it,
				// so it is checked here rather than at its first use.
				if (behaviour == null)
				{
					throw new Exception(
						TriggerBehaviourType + " did not come back after being added to \"" + target.name
						+ "\" -- the component could not be created. The collider above is on the object but has "
						+ "nothing to call it."
					);
				}
				report.AppendLine("  added " + behaviour.GetType().FullName + " to \"" + target.name + "\""
					+ (udonSharpSetup
						? "  (through UdonSharp, so the proxy has its UdonBehaviour and runs at build)"
						: "  (added as a plain component; it is not a UdonSharp script, so there was no UdonSharp path)"));
				report.AppendLine("  read it back with unity_inspect { gameObject: \"" + target.name
					+ "\", component: \"" + behaviourType.Name + "\" }");
			}
			report.AppendLine("  the trigger fires from the collider's volume; give the object a Rigidbody if the"
				+ " script expects collision callbacks rather than trigger ones.");

			report.AppendLine(MarkSceneDirty()
				? "scene marked dirty (save it to keep the change)"
				: "no valid active scene, so nothing was marked dirty");
			return report.ToString();
		}

		/// Every collider in the scene with the numbers it actually carries, and
		/// the three ways a collider silently does nothing.
		public static string DescribeColliders()
		{
			var report = new StringBuilder();
			report.AppendLine("--- colliders in the scene ---");

			List<Collider> colliders = new List<Collider>();
			foreach (Collider collider in UnityEngine.Object.FindObjectsOfType<Collider>(true))
			{
				if (collider == null) continue;
				colliders.Add(collider);
			}

			if (colliders.Count == 0)
			{
				report.AppendLine("no colliders at all (inactive objects were searched too).");
				report.AppendLine("Avatars will fall straight through this world: there is no surface for them to");
				report.AppendLine("stand on, and nothing in the scene can be interacted with by touch. Every world");
				report.AppendLine("needs at least a floor -- action \"add-collider\" with shape=Box, or \"add-trigger\"");
				report.AppendLine("for an object a script handles itself.");
				return report.ToString();
			}

			int triggers = 0;
			int nullMesh = 0;
			int nonConvex = 0;
			int otherTypes = 0;
			int unreadable = 0;
			// Sorted by path so two runs over one scene produce the same report;
			// FindObjectsOfType gives no order of its own.
			foreach (Collider collider in colliders.OrderBy(c => HierarchyPath(c.gameObject), StringComparer.Ordinal))
			{
				report.AppendLine(collider.gameObject.name + "  [" + collider.GetType().Name + "]  path="
					+ HierarchyPath(collider.gameObject));
				report.AppendLine("  isTrigger=" + Read(() => collider.isTrigger, "isTrigger")
					+ "  enabled=" + Read(() => collider.enabled, "enabled")
					+ "  layer=" + collider.gameObject.layer);
				// A counter that treated an unreadable isTrigger as false would
				// report a collider that blocks avatars for one nobody could
				// inspect, so an unread flag is counted on its own.
				if (!TryRead(() => collider.isTrigger, out bool isTrigger))
				{
					unreadable++;
					report.AppendLine("  isTrigger could not be read, so this collider is in neither count below.");
				}
				else if (isTrigger)
				{
					triggers++;
				}

				if (collider is BoxCollider box)
				{
					report.AppendLine("  size=" + Read(() => (object)box.size, "BoxCollider.size")
						+ "  centre=" + Read(() => (object)box.center, "BoxCollider.center"));
				}
				else if (collider is SphereCollider sphere)
				{
					report.AppendLine("  radius=" + Read(() => sphere.radius, "SphereCollider.radius")
						+ "  centre=" + Read(() => (object)sphere.center, "SphereCollider.center"));
				}
				else if (collider is CapsuleCollider capsule)
				{
					report.AppendLine("  radius=" + Read(() => capsule.radius, "CapsuleCollider.radius")
						+ "  height=" + Read(() => capsule.height, "CapsuleCollider.height")
						+ "  direction=" + Read(() => capsule.direction, "CapsuleCollider.direction")
						+ " (" + AxisName(capsule.direction) + ")  centre="
						+ Read(() => (object)capsule.center, "CapsuleCollider.center"));
					if (TryRead(() => capsule.height, out float height)
						&& TryRead(() => capsule.radius, out float radius)
						&& height < radius * 2f - 0.0001f)
					{
						report.AppendLine("  NOTE: height (" + Num(height) + ") is under twice the radius ("
							+ Num(radius) + "); a capsule is squashed into a sphere rather than shaped.");
					}
				}
				else if (collider is MeshCollider mesh)
				{
					report.AppendLine("  convex=" + Read(() => mesh.convex, "MeshCollider.convex")
						+ "  mesh=" + DescribeMesh(mesh.sharedMesh));
					if (TryRead(() => mesh.convex, out bool convex) && !convex) nonConvex++;
					if (mesh.sharedMesh == null) nullMesh++;
				}
				else
				{
					otherTypes++;
					report.AppendLine("  a " + collider.GetType().FullName + " carries its shape in its own fields, which"
						+ " this report does not read; read it with unity_inspect.");
				}
			}

			// A mesh with no collider on it renders but cannot be touched, and
			// the mesh a MeshCollider references is a separate reference that is
			// easy to leave empty, so both are counted separately.
			int meshes = 0;
			int meshesWithoutCollider = 0;
			var uncollided = new List<string>();
			var emptyMeshColliders = new List<string>();
			foreach (Collider collider in colliders)
			{
				if (!(collider is MeshCollider mesh) || mesh.sharedMesh != null) continue;
				emptyMeshColliders.Add(HierarchyPath(mesh.gameObject));
			}
			foreach (MeshFilter filter in UnityEngine.Object.FindObjectsOfType<MeshFilter>(true))
			{
				if (filter == null || filter.sharedMesh == null) continue;
				meshes++;
				if (filter.GetComponent<MeshCollider>() != null) continue;
				meshesWithoutCollider++;
				uncollided.Add(HierarchyPath(filter.gameObject));
			}
			uncollided.Sort(StringComparer.Ordinal);
			emptyMeshColliders.Sort(StringComparer.Ordinal);

			report.AppendLine("--- totals ---");
			report.AppendLine("total colliders=" + colliders.Count + "  triggers=" + triggers
				+ "  non-trigger=" + (colliders.Count - triggers - unreadable) + "  unreadable isTrigger="
				+ unreadable + "  collider types this report does not"
				+ " read field by field=" + otherTypes);
			report.AppendLine("meshes carrying a MeshFilter mesh=" + meshes
				+ "  of those with NO MeshCollider=" + meshesWithoutCollider
				+ "  MeshColliders whose mesh reference is EMPTY=" + nullMesh);
			report.AppendLine("non-convex mesh colliders=" + nonConvex);

			if (meshesWithoutCollider > 0)
			{
				report.AppendLine("meshes you can see but cannot touch (" + meshesWithoutCollider + "):");
				foreach (string entry in uncollided) report.AppendLine("  " + entry);
				report.AppendLine("  these render and collide with nothing. That is deliberate for decoration, and a");
				report.AppendLine("  bug for a surface: an avatar walks through the one you meant to stand on.");
			}
			if (nullMesh > 0)
			{
				report.AppendLine("MeshColliders with an EMPTY mesh reference (" + nullMesh + ") -- a common");
				report.AppendLine("  mistake, and the worst kind: the component is present, so it looks configured,");
				report.AppendLine("  while it collides with nothing at all and reports no error:");
				foreach (string entry in emptyMeshColliders) report.AppendLine("  " + entry);
				report.AppendLine("  set the Mesh property to the renderer's mesh (or use a box collider instead).");
			}
			if (nonConvex > 0)
			{
				report.AppendLine("NON-CONVEX mesh colliders (" + nonConvex + "): a non-convex MeshCollider is a known");
				report.AppendLine("  Quest performance problem -- it is processed by PhysX on the CPU, and on Android");
				report.AppendLine("  it can cost a frame or more on its own. Turn Convex on, or replace it with a box,");
				report.AppendLine("  sphere or capsule where the shape allows it.");
			}
			if (triggers == 0)
			{
				report.AppendLine("No trigger collider in the scene, so nothing here can be interacted with by touch.");
			}
			report.AppendLine("Every number above was read off a live component; a value this report could not read"
				+ " prints \"not readable\" rather than a zero.");
			return report.ToString();
		}

		// ------------------------------------------------------- the collider itself

		/// Finds the collider of the requested shape or adds one, sets it up, and
		/// writes what it did into the report.
		private static Collider ApplyShape(StringBuilder report, GameObject target, string shape, bool hasSize,
			float sizeValue, bool trigger)
		{
			Type colliderType = ColliderTypeFor(shape);
			string typeName = colliderType.Name;
			Collider collider = target.GetComponent(colliderType) as Collider;
			if (collider == null)
			{
				bool udonSharpSetup;
				try
				{
					collider = PiUdon.AddBehaviourComponent(target, colliderType,
						"pi add " + colliderType.Name + " to " + target.name, out udonSharpSetup) as Collider;
				}
				catch (Exception error)
				{
					throw new Exception("Could not add " + typeName + " to \"" + target.name + "\": " + error.Message + ".");
				}
				if (collider == null)
				{
					throw new Exception(typeName + " was not on \"" + target.name + "\" after being added; "
						+ "the component could not be created.");
				}
				report.AppendLine("added " + typeName + " to \"" + target.name + "\""
					+ (udonSharpSetup ? "  (through UdonSharp)" : ""));
			}
			else
			{
				// Reuse rather than duplicate: two colliders of one shape on one
				// object both collide, and the second is invisible in the report
				// of anything that counts them.
				report.AppendLine(typeName + " was already on \"" + target.name
					+ "\"; it was configured, not duplicated.");
			}

			// The shape numbers below are written to the COMPONENT, so the
			// component is what has to be recorded -- a record on the GameObject
			// would not capture a single one of them. The ADD itself is already
			// undoable because AddBehaviourComponent added it through
			// Undo.AddComponent.
			string undoState;
			try
			{
				Undo.RecordObject(collider, "pi configure " + typeName + " on " + target.name);
				undoState = "one Ctrl-Z removes the collider and every value below";
			}
			catch (Exception error)
			{
				undoState = "NOT registered (" + error.Message + ")";
			}
			report.AppendLine("undo: " + undoState);

			switch (shape)
			{
				case "Box":
					ConfigureBox(report, (BoxCollider)collider, hasSize, sizeValue);
					break;
				case "Sphere":
					ConfigureSphere(report, (SphereCollider)collider, hasSize, sizeValue);
					break;
				case "Capsule":
					ConfigureCapsule(report, (CapsuleCollider)collider, hasSize, sizeValue);
					break;
				default:
					DescribeMeshCollider(report, (MeshCollider)collider, hasSize, sizeValue);
					break;
			}

			collider.isTrigger = trigger;
			if (hasSize && sizeValue <= 0f)
			{
				report.AppendLine("WARNING: size " + Num(sizeValue) + " was accepted, and a collider that size has no");
				report.AppendLine("  volume: nothing can enter it and it reports no error. Pass a positive size.");
			}
			return collider;
		}

		private static void ConfigureBox(StringBuilder report, BoxCollider box, bool hasSize, float sizeValue)
		{
			if (hasSize)
			{
				// One number for three axes: a cube of that edge length. A box
				// that has to be a slab needs its own size vector set on the
				// component afterwards.
				box.size = Vector3.one * sizeValue;
				report.AppendLine("size " + Num(sizeValue) + " -> BoxCollider.size = ("
					+ Num(sizeValue) + ", " + Num(sizeValue) + ", " + Num(sizeValue) + ")");
			}
			else
			{
				report.AppendLine("no size given; the box kept the size it had ("
					+ Read(() => (object)box.size, "BoxCollider.size") + ")");
			}
		}

		private static void ConfigureSphere(StringBuilder report, SphereCollider sphere, bool hasSize, float sizeValue)
		{
			if (hasSize)
			{
				sphere.radius = sizeValue;
				report.AppendLine("size " + Num(sizeValue) + " -> SphereCollider.radius = " + Num(sizeValue)
					+ " (diameter " + Num(sizeValue * 2f) + ")");
			}
			else
			{
				report.AppendLine("no size given; the sphere kept the radius it had ("
					+ Read(() => sphere.radius, "SphereCollider.radius") + ")");
			}
		}

		private static void ConfigureCapsule(StringBuilder report, CapsuleCollider capsule, bool hasSize, float sizeValue)
		{
			if (!hasSize)
			{
				report.AppendLine("no size given; the capsule kept the numbers it had (radius "
					+ Read(() => capsule.radius, "CapsuleCollider.radius") + ", height "
					+ Read(() => capsule.height, "CapsuleCollider.height") + ")");
				return;
			}

			// A capsule needs BOTH dimensions and one number cannot give both, so
			// the radius takes it and the height becomes the diameter.
			capsule.radius = sizeValue;
			capsule.height = sizeValue * 2f;
			report.AppendLine("size " + Num(sizeValue) + " -> CapsuleCollider.radius = " + Num(sizeValue)
				+ " and CapsuleCollider.height = " + Num(sizeValue * 2f));
			report.AppendLine("  height was DERIVED as twice the radius, because a capsule needs both numbers and"
				+ " this action takes one. Set Height on the component for a capsule that is not as deep as it is wide.");
		}

		private static void DescribeMeshCollider(StringBuilder report, MeshCollider mesh, bool hasSize, float sizeValue)
		{
			report.AppendLine("the size argument does NOT apply to a MeshCollider: it takes its geometry from the"
				+ " object's own mesh, and there is nothing for a single number to set.");
			if (hasSize)
			{
				report.AppendLine("  the size that was passed (" + Num(sizeValue) + ") was IGNORED -- nothing on a"
					+ " MeshCollider was set from it.");
			}
			report.AppendLine("  MeshCollider.mesh = " + DescribeMesh(mesh.sharedMesh));
			if (mesh.sharedMesh == null)
			{
				report.AppendLine("  this collider is EMPTY: it collides with nothing and reports no error. Point");
				report.AppendLine("  its Mesh field at the object's mesh, or use a Box/Sphere/Capsule collider.");
			}
			MeshFilter filter = mesh.GetComponent<MeshFilter>();
			if (filter == null)
			{
				report.AppendLine("  the object has no MeshFilter, so there is no mesh on it to point this at.");
			}
			else
			{
				report.AppendLine("  the object's MeshFilter mesh = " + DescribeMesh(filter.sharedMesh)
					+ (mesh.sharedMesh == null && filter.sharedMesh != null
						? "  -- assign this one to the collider to make it collide"
						: ""));
			}
		}

		/// What the collider holds now, and where that puts it in the world.
		private static void AppendColliderReadBack(StringBuilder report, Collider collider)
		{
			report.AppendLine("read back from the live " + collider.GetType().Name + ":");
			if (collider is BoxCollider box)
			{
				report.AppendLine("  size=" + Read(() => (object)box.size, "BoxCollider.size")
					+ "  centre=" + Read(() => (object)box.center, "BoxCollider.center"));
			}
			else if (collider is SphereCollider sphere)
			{
				report.AppendLine("  radius=" + Read(() => sphere.radius, "SphereCollider.radius")
					+ "  centre=" + Read(() => (object)sphere.center, "SphereCollider.center"));
			}
			else if (collider is CapsuleCollider capsule)
			{
				report.AppendLine("  radius=" + Read(() => capsule.radius, "CapsuleCollider.radius")
					+ "  height=" + Read(() => capsule.height, "CapsuleCollider.height")
					+ "  direction=" + Read(() => capsule.direction, "CapsuleCollider.direction")
					+ " (" + AxisName(capsule.direction) + ")  centre="
					+ Read(() => (object)capsule.center, "CapsuleCollider.center"));
			}
			else if (collider is MeshCollider mesh)
			{
				report.AppendLine("  mesh=" + DescribeMesh(mesh.sharedMesh)
					+ "  convex=" + Read(() => mesh.convex, "MeshCollider.convex"));
			}
			report.AppendLine("  isTrigger=" + Read(() => collider.isTrigger, "isTrigger"));
			report.AppendLine("world bounds: centre=" + Read(() => (object)collider.bounds.center, "bounds.center")
				+ "  size=" + Read(() => (object)collider.bounds.size, "bounds.size")
				+ "  (world space, after the object's own scale and position)");
		}

		private static void AppendTriggerConsequence(StringBuilder report, Collider collider)
		{
			// Read the same way the line above printed it, so the consequence and
			// the value cannot disagree because one of them was re-read.
			if (!TryRead(() => collider.isTrigger, out bool trigger))
			{
				report.AppendLine("isTrigger could not be read off the collider, so whether it blocks avatars or"
					+ " triggers them is unknown. Check it in the Inspector on \"" + collider.name + "\".");
				return;
			}
			if (trigger)
			{
				report.AppendLine("isTrigger is ON: this collider reports overlap to the object instead of blocking");
				report.AppendLine("  anything. A trigger is what a VRChat interaction uses -- add a VRC_Trigger (action");
				report.AppendLine("  \"add-trigger\") or a UdonSharp script, or nothing will ever call it.");
				return;
			}
			report.AppendLine("isTrigger is OFF: avatars will BLOCK against this object rather than trigger it.");
			report.AppendLine("  That is right for a floor, a wall or a platform. If something in the world is meant to");
			report.AppendLine("  react when an avatar touches it, a non-trigger collider is what stops them reaching it");
			report.AppendLine("  at all -- pass isTrigger=true, or use action \"add-trigger\".");
		}

		// ----------------------------------------------------------- argument parsing

		private static GameObject ResolveTarget(string objectName)
		{
			string wanted = (objectName ?? "").Trim();
			if (wanted.Length == 0)
			{
				throw new Exception(
					"This action needs the name of the GameObject to work on. There is no default object: colliders "
					+ "are geometry, and putting one on an object nobody named would make a scene nobody can reason "
					+ "about. List the objects with unity_find, then pass one by name (or by \"Root/Child\" path)."
				);
			}

			GameObject target = PiWorldCreate.FindSceneObject(wanted, out int matches);
			if (target == null)
			{
				// The names, because "not found" on its own sends the caller
				// looking for a spelling problem rather than a wrong scene.
				var names = new List<string>();
				foreach (Transform child in UnityEngine.Object.FindObjectsOfType<Transform>(true))
				{
					if (child.parent == null) names.Add(child.gameObject.name);
				}
				names.Sort(StringComparer.Ordinal);
				throw new Exception("No GameObject named \"" + wanted + "\" in the open scene (inactive objects "
					+ "count). The scene's root objects are: " + string.Join(", ", names.ToArray())
					+ (names.Count == 0 ? ". The scene is empty." : "."));
			}
			if (matches > 1)
			{
				var paths = new List<string>();
				foreach (Transform child in UnityEngine.Object.FindObjectsOfType<Transform>(true))
				{
					if (child.gameObject.name == wanted) paths.Add(HierarchyPath(child.gameObject));
				}
				paths.Sort(StringComparer.Ordinal);
				throw new Exception("\"" + wanted + "\" names " + matches + " objects in the scene: "
					+ string.Join(", ", paths.ToArray()) + ". Pass one of those paths as the object instead -- a bare "
					+ "name that matches several cannot be resolved to one collider, and guessing which one meant is "
					+ "how a collider ends up on the wrong object.");
			}
			return target;
		}

		private static string ParseShape(string shape)
		{
			string wanted = (shape ?? "").Trim();
			if (wanted.Length == 0) return "Box";
			foreach (string known in Shapes)
			{
				if (known.Equals(wanted, StringComparison.OrdinalIgnoreCase)) return known;
			}
			throw new Exception("\"" + wanted + "\" is not a collider shape. Use " + string.Join(", ", Shapes)
				+ " (or leave shape out for " + Shapes[0] + "). Sphere and Capsule take the size as their radius,"
				+ " Box as its edge length, and Mesh ignores it.");
		}

		private static void ParseSize(string size, out bool hasSize, out float value)
		{
			hasSize = false;
			value = 0f;
			string wanted = (size ?? "").Trim();
			if (wanted.Length == 0) return;
			// Invariant culture throughout: a locale with a comma decimal
			// separator would otherwise read "0.5" as 5, or fail outright.
			if (!float.TryParse(wanted, NumberStyles.Float, CultureInfo.InvariantCulture, out value))
			{
				throw new Exception("size \"" + wanted + "\" is not a number. It is a single length in metres, "
					+ "written the same way for every shape.");
			}
			// A zero or negative size is accepted rather than refused: Unity takes
			// it, and the caller warns, because a collider with no volume takes
			// part in nothing and reports no error.
			hasSize = true;
		}

		private static bool ParseSwitch(string raw, bool fallback)
		{
			string text = (raw ?? "").Trim();
			if (text.Length == 0) return fallback;
			if (text.Equals("true", StringComparison.OrdinalIgnoreCase) || text == "1"
				|| text.Equals("yes", StringComparison.OrdinalIgnoreCase)
				|| text.Equals("on", StringComparison.OrdinalIgnoreCase)) return true;
			if (text.Equals("false", StringComparison.OrdinalIgnoreCase) || text == "0"
				|| text.Equals("no", StringComparison.OrdinalIgnoreCase)
				|| text.Equals("off", StringComparison.OrdinalIgnoreCase)) return false;
			throw new Exception("Expected a boolean (\"true\" or \"false\", or 1/0/yes/no/on/off), not \"" + raw + "\".");
		}

		private static Type ColliderTypeFor(string shape)
		{
			if (shape == "Box") return typeof(BoxCollider);
			if (shape == "Sphere") return typeof(SphereCollider);
			if (shape == "Capsule") return typeof(CapsuleCollider);
			return typeof(MeshCollider);
		}

		// ------------------------------------------------------------- rendering

		/// A bool or float read off a live component, where a failure has to be
		/// distinguishable from a real false or a real zero.
		private static bool TryRead<T>(Func<T> read, out T value)
		{
			try
			{
				value = read();
				return true;
			}
			catch (Exception)
			{
				value = default;
				return false;
			}
		}

		/// A value read off a live component, or the reason it could not be read.
		///
		/// The distinction is the point: a failed read printed as 0 is
		/// indistinguishable from a real zero, and "isTrigger=false" from a real
		/// false -- which is the difference between "this collider blocks avatars"
		/// and "nobody knows what this collider does".
		private static string Read(Func<object> read, string label)
		{
			try
			{
				object value = read();
				if (value == null) return "not readable (" + label + " returned null)";
				if (value is float number) return Num(number);
				if (value is Vector3 vector) return "(" + Num(vector.x) + ", " + Num(vector.y) + ", " + Num(vector.z) + ")";
				return value.ToString();
			}
			catch (Exception error)
			{
				return "not readable (" + label + ": " + (error.InnerException ?? error).Message + ")";
			}
		}

		private static string DescribeMesh(Mesh mesh)
		{
			// A null mesh is a real, common and silent state, not a failed read,
			// so it is named rather than rendered as unreadable.
			if (mesh == null) return "(none -- references no mesh, so it collides with nothing)";
			string triangles = "not readable";
			try
			{
				// GetIndexCount, not mesh.triangles: the property allocates an
				// index array the size of the whole mesh, per collider, to print
				// one number.
				long indices = 0;
				for (int submesh = 0; submesh < mesh.subMeshCount; submesh++) indices += (long)mesh.GetIndexCount(submesh);
				triangles = Num(indices / 3);
			}
			catch (Exception error)
			{
				triangles = "not readable (" + (error.InnerException ?? error).Message + ")";
			}
			return mesh.name + " [vertices=" + mesh.vertexCount + ", triangles=" + triangles + "]";
		}

		private static string Num(float value)
		{
			return value.ToString("0.###", CultureInfo.InvariantCulture);
		}

		private static string AxisName(int direction)
		{
			if (direction == 0) return "X";
			if (direction == 1) return "Y";
			if (direction == 2) return "Z";
			return "unknown (" + direction + ")";
		}

		private static string HierarchyPath(GameObject gameObject)
		{
			var parts = new List<string>();
			Transform cursor = gameObject == null ? null : gameObject.transform;
			while (cursor != null)
			{
				parts.Insert(0, cursor.name);
				cursor = cursor.parent;
			}
			return parts.Count == 0 ? "(no object)" : string.Join("/", parts.ToArray());
		}

		private static bool MarkSceneDirty()
		{
			Scene scene = SceneManager.GetActiveScene();
			if (!scene.IsValid()) return false;
			EditorSceneManager.MarkSceneDirty(scene);
			return true;
		}
	}
}