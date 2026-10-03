// Runtime self-test for the VRChat reflection resolver.
//
// The bug this covers: unity_vrc { action: "project" } threw on every project
// with a recent VRCFury.
//
//   GetComponent requires that the requested component 'FuryArmatureLink'
//   derives from MonoBehaviour or Component or is an interface.
//
// Two causes compounding. (1) PiVRFury.FindAll asked for
// `com.vrcfury.api.FuryArmatureLink` and friends, which moved to
// `com.vrcfury.api.Components.*` in current VRCFury. (2) VrcResolve.Type's
// simple-name fallback found the REAL FuryArmatureLink after the exact lookup
// missed -- and those types are plain data objects deriving from System.Object,
// not Components. ComponentsOn passed one to GameObject.GetComponents, and
// Unity refuses.
//
// The fallback is still wanted: a caller that only knows a simple name should
// resolve when it can. What must never happen is a non-Component type reaching
// GetComponents. So the guard under test is VrcResolve.IsComponentType, which
// is what ComponentsOn/ComponentOn consult before touching a GameObject.
//
// This cannot be a TS test or a plain plugin compile: the resolution needs a
// live AppDomain with real assemblies. The harness (vrcresolve-selftest.sh)
// compiles the resolver together with this file, which declares stand-ins with
// the same full names and the same shapes the real packages declare, and runs
// it under Unity's bundled mono. GameObject construction needs a Unity runtime,
// so the assertions are at the type level -- the guard that decides whether
// GetComponents is ever reached is the environment-independent part.

using System;
using System.Linq;
using System.Reflection;
using PiUnity;

// ---- stand-ins for the real package types -------------------------------
//
// Shapes copied from com.vrcfury.vrcfury 1.1365:
//   namespace com.vrcfury.api.Components { public class FuryToggle { ... } }
//     -- plain class, no base type, because VRCFury models these as data in an
//        avatar-wide list, not as components on GameObjects.
//   namespace VF.Model { internal class VRCFury : VRCFuryComponent { } }
//     -- a real component, and the thing the factories actually add.
namespace com.vrcfury.api.Components
{
	public class FuryArmatureLink { }
	public class FuryFullController { }
	public class FuryToggle { }
	public class FurySocket { }
}

namespace VF.Model
{
	internal class VRCFury : UnityEngine.MonoBehaviour { }
}

namespace VF.Gizmo
{
	internal class UVShrink : UnityEngine.MonoBehaviour { }
}

// Public stand-in for the fallback case: a caller that only knows the simple
// name. GetExportedTypes sees this one; the internal VF.* types above it does
// not, which is why the exact-name fast path has to exist.
namespace SomeOtherPackage
{
	public class VRCFuryHapticSocket : UnityEngine.MonoBehaviour { }
}

namespace VF.Component
{
	internal class VRCFuryHapticSocket : UnityEngine.MonoBehaviour { }
}

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

	private static int Main()
	{
		// 1. The plain data classes are resolvable -- Type() must still find
		//    them, because IsInstalled()/Has() use it to answer "is VRCFury
		//    here", and because the fallback is deliberate. The fix is not to
		//    stop resolving them; it is to stop treating them as components.
		System.Type toggle = VrcResolve.Type("com.vrcfury.api.Components.FuryToggle");
		Check("the real FuryToggle type resolves", toggle != null);
		Check("it resolves to the Components namespace",
			toggle != null && toggle.FullName == "com.vrcfury.api.Components.FuryToggle",
			toggle == null ? "<null>" : toggle.FullName);

		// 2. And it is refused as a component type. This is the assertion the
		//    whole fix rests on: had ComponentsOn consulted it, Unity would
		//    have thrown the exact error in issue #2.
		Check("a plain FuryToggle is not a Component type",
			!VrcResolve.IsComponentType(toggle));
		foreach (string name in new[] { "FuryArmatureLink", "FuryFullController", "FurySocket" })
		{
			System.Type resolved = VrcResolve.Type("com.vrcfury.api.Components." + name);
			Check("a plain " + name + " is not a Component type",
				resolved != null && !VrcResolve.IsComponentType(resolved),
				resolved == null ? "<unresolved>" : resolved.FullName);
		}

		// 3. The STALE name -- the one PiVRFury.FindAll used to ask for -- must
		//    now resolve, via the simple-name fallback, to something the guard
		//    rejects. That path is exactly what threw before, so it is
		//    asserted rather than assumed.
		//
		//    If the fallback does NOT find it, that is still safe, but it would
		//    be a silent skip: GetExportedTypes() throwing
		//    ReflectionTypeLoadException looks identical to "no such type", and
		//    VrcResolve swallows both. So distinguish them here.
		System.Type stale = VrcResolve.Type("com.vrcfury.api.FuryArmatureLink");
		Check("the stale name resolves via the simple-name fallback (fallback alive)",
			stale != null,
			stale == null
				? "fallback returned null -- either the fallback is broken or "
					+ "GetExportedTypes threw ReflectionTypeLoadException on this assembly"
				: "");
		if (stale != null)
		{
			Check("the stale name resolves to the real class",
				stale.FullName == "com.vrcfury.api.Components.FuryArmatureLink", stale.FullName);
			Check("the stale name is refused as a component",
				!VrcResolve.IsComponentType(stale), stale.FullName);
		}

		// 3b. The same, without the fallback in the way: assert
		//     GetExportedTypes() actually works here. Without this line every
		//     check above can pass vacuously.
		Assembly self = typeof(Program).Assembly;
		bool exportedOk = false;
		string exportedDetail = "";
		try { self.GetExportedTypes(); exportedOk = true; }
		catch (Exception error)
		{
			// UnityEditor reference assemblies are compile-only; if one is on the
			// reference set, this is where it shows.
			var load = error as ReflectionTypeLoadException;
			exportedDetail = load != null && load.LoaderExceptions.Length > 0
				? load.LoaderExceptions[0].Message
				: error.Message;
		}
		Check("GetExportedTypes works on the self-test assembly (no vacuous passes)",
			exportedOk, exportedDetail);

		// 4. Real components are still found, by BOTH routes, and the guard still lets
		//    them through.
		//
		//    The exact-name route must reach internal types: VRCFury's own
		//    VF.Model.VRCFury is `internal`, and FindAll lists it by full name
		//    precisely because GetExportedTypes would never show it. assembly
		//    .GetType ignores visibility, so the fast path finds it.
		Check("VF.Model.VRCFury resolves by exact full name (internal type included)",
			VrcResolve.IsComponentType(VrcResolve.Type("VF.Model.VRCFury")));
		Check("VF.Component.VRCFuryHapticSocket is a Component type",
			VrcResolve.IsComponentType(VrcResolve.Type("VF.Component.VRCFuryHapticSocket")));

		// The fallback deliberately only searches PUBLIC types, so the loose-name
		// case is asserted against a public one. (The FuryToggle case above
		// already covers the fallback end to end; this pins the contract that a
		// caller naming only a simple name of a public package type still
		// resolves, so the guard does not silently degrade them to "absent".)
		Check("a loose simple name still finds a public component (fallback preserved)",
			VrcResolve.IsComponentType(VrcResolve.Type("some.other.package.VRCFuryHapticSocket")),
			"fallback no longer resolves the simple name");
		Check("a package type that does not exist at all is null",
			VrcResolve.Type("com.example.definitely.NotHere") == null);

		// 5. A null type is also refused, not an NRE: "package absent" and
		//    "name resolved to the wrong shape" both have to read as "not
		//    present", which is the contract this directory documents.
		Check("null is not a Component type", !VrcResolve.IsComponentType(null));

		// 6. Interfaces are not Component types either. Unity's error message
		//    lists "or is an interface" as acceptable, but GetComponents on an
		//    interface still returns nothing useful here -- and an interface
		//    resolving through the fallback must not be handed over as though
		//    it were a component.
		Check("an interface is not a Component type",
			!VrcResolve.IsComponentType(typeof(System.IDisposable)));

		Console.WriteLine();
		Console.WriteLine(failures == 0
			? "all VRChat resolver self-tests passed"
			: failures + " self-test failure(s)");
		return failures == 0 ? 0 : 1;
	}
}