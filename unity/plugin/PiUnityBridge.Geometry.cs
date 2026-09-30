// Part of the pi Unity bridge plugin. See PiUnityBridge.Core.cs for the
// protocol overview. The whole plugin is one assembly of partial classes, so
// Unity compiles it into Assembly-CSharp-Editor with no asmdef and no package
// dependencies, which is what lets it drop into any Unity 2021+ project.

// Read-only geometry measurement.
//
// Read-only BY CONSTRUCTION: this reads mesh and renderer data and returns
// numbers. It never writes a transform, never marks a scene dirty, never saves,
// never writes an asset, and never opens a dialog. That is what makes the
// measurement safe to take while a worker holds the Editor mid-iteration, and
// a probe that dirtied the scene could not be.
//
// Every approximation is labelled in a `state` field rather than presented as
// a measurement, following the `bounds.state` convention in
// PiUnityBridge.Prefabs.cs. Subsampling, skinned bounds volumes and the
// closed-mesh requirement of the inside test all produce numbers that are
// close to right rather than exactly right, and a bare float cannot tell a
// caller which it is reading.
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using UnityEditor;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace PiUnity
{
	/// Integer cell coordinate. A struct key rather than a packed long because
	/// a 21-bit field per axis runs out on a scene with a large floor, and a
	/// wrapped key silently merges two cells that must stay apart.
	internal readonly struct ProbeKey3 : IEquatable<ProbeKey3>
	{
		public readonly int X;
		public readonly int Y;
		public readonly int Z;

		public ProbeKey3(int x, int y, int z) { X = x; Y = y; Z = z; }

		public bool Equals(ProbeKey3 other) { return X == other.X && Y == other.Y && Z == other.Z; }
		public override bool Equals(object obj) { return obj is ProbeKey3 other && Equals(other); }
		public override int GetHashCode() { return unchecked(X * 73856093 ^ Y * 19349663 ^ Z * 83492791); }
	}

	/// World-space triangle soup with a uniform grid over it, answering "how
	/// close is this point to the surface" and "is it inside".
	///
	/// Unity has no BVH and no closest-point query, so the grid is what keeps a
	/// pair measurement linear in the sampled vertex count instead of
	/// multiplying it by the triangle count. The alternative -- Physics
	/// queries -- would need colliders, and a probe that had to add them to
	/// measure a gap would be a probe that mutates the scene.
	internal sealed class ProbeSurface
	{
		public bool Available;
		public string State = "";

		private Vector3[] positions = new Vector3[0];
		private int[] triangles = new int[0];
		private readonly Dictionary<ProbeKey3, List<int>> cells = new Dictionary<ProbeKey3, List<int>>();
		private float cellSize = 1f;
		private Vector3 origin;

		/// Sampled world-space vertices, the points the pair check measures from.
		public readonly List<Vector3> samples = new List<Vector3>();
		public Vector3 WorldMin;
		public Vector3 WorldMax;

		public static ProbeSurface Build(Transform owner, Mesh mesh, int vertexLimit, int triangleLimit)
		{
			var surface = new ProbeSurface();
			if (mesh == null)
			{
				surface.State = "no mesh to measure";
				return surface;
			}
			if (!mesh.isReadable)
			{
				// Vertex data stays in native memory until the importer's
				// Read/Write flag is on, and no public API can read past that.
				// Saying which setting fixes it is the difference between a
				// caveat and a dead end.
				surface.State = "mesh is not Read/Write Enabled in its import settings, so its vertices cannot be read without changing the import";
				return surface;
			}

			Vector3[] local;
			int[] indices;
			try
			{
				local = mesh.vertices;
				indices = mesh.triangles;
			}
			catch (Exception error)
			{
				surface.State = "mesh data could not be read: " + error.Message;
				return surface;
			}

			if (local.Length == 0)
			{
				surface.State = "mesh has no vertices";
				return surface;
			}

			Matrix4x4 matrix = owner.localToWorldMatrix;
			var world = new Vector3[local.Length];
			for (int i = 0; i < local.Length; i++) world[i] = matrix.MultiplyPoint3x4(local[i]);

			surface.WorldMin = world[0];
			surface.WorldMax = world[0];
			foreach (Vector3 point in world)
			{
				if (point.x < surface.WorldMin.x) surface.WorldMin.x = point.x;
				if (point.y < surface.WorldMin.y) surface.WorldMin.y = point.y;
				if (point.z < surface.WorldMin.z) surface.WorldMin.z = point.z;
				if (point.x > surface.WorldMax.x) surface.WorldMax.x = point.x;
				if (point.y > surface.WorldMax.y) surface.WorldMax.y = point.y;
				if (point.z > surface.WorldMax.z) surface.WorldMax.z = point.z;
			}
			int vertexStride = Math.Max(1, world.Length / Math.Max(1, vertexLimit));
			for (int i = 0; i < world.Length; i += vertexStride) surface.samples.Add(world[i]);
			// A stride that never lands on the last vertex drops the tip of a
			// long thin part, which is the very end a pair gap is measured at.
			if (vertexStride > 1) surface.samples.Add(world[world.Length - 1]);

			int triangleCount = indices.Length / 3;
			int triangleStride = Math.Max(1, triangleCount / Math.Max(1, triangleLimit));
			var sampled = new List<int>(Math.Min(triangleCount, triangleLimit) + 1);
			for (int t = 0; t < triangleCount; t += triangleStride)
			{
				sampled.Add(indices[t * 3]);
				sampled.Add(indices[t * 3 + 1]);
				sampled.Add(indices[t * 3 + 2]);
			}
			if (triangleStride > 1 && triangleCount > 0)
			{
				int last = triangleCount - 1;
				sampled.Add(indices[last * 3]);
				sampled.Add(indices[last * 3 + 1]);
				sampled.Add(indices[last * 3 + 2]);
			}
			surface.positions = world;
			surface.triangles = sampled.ToArray();

			Vector3 size = surface.WorldMax - surface.WorldMin;
			float largest = Mathf.Max(size.x, Mathf.Max(size.y, size.z));
			// Fixed at 24 per axis: the grid costs cellsPerAxis^3 cells, which a
			// floor measured in metres would blow up if the count tracked the
			// extent. 24 also makes the cell an eighth of the longest side, so a
			// closest-point search normally settles in the first ring or two.
			const int kCellsPerAxis = 24;
			surface.cellSize = Mathf.Max(largest / kCellsPerAxis, 1e-5f);
			surface.origin = surface.WorldMin;

			for (int t = 0; t < surface.triangles.Length; t += 3)
			{
				ProbeKey3 key = surface.CellOf(surface.positions[surface.triangles[t]]);
				if (!surface.cells.TryGetValue(key, out List<int> bucket))
				{
					bucket = new List<int>();
					surface.cells[key] = bucket;
				}
				bucket.Add(t);
			}

			surface.Available = true;
			surface.State = surface.triangles.Length / 3 < triangleCount
				? "closest-point figures use " + (surface.triangles.Length / 3) + " of " + triangleCount
					+ " triangles, evenly strided, so a gap thinner than the stride can read larger than it is"
				: "world-space triangles, full triangle set";
			return surface;
		}

		private ProbeKey3 CellOf(Vector3 point)
		{
			return new ProbeKey3(
				(int)Math.Floor((point.x - origin.x) / cellSize),
				(int)Math.Floor((point.y - origin.y) / cellSize),
				(int)Math.Floor((point.z - origin.z) / cellSize));
		}

		/// Unsigned distance to the nearest sampled triangle, with the surface
		/// normal and the surface point reached. The return is false when there
		/// is no triangle to be near.
		///
		/// The hit point is returned rather than kept in a field because the
		/// inside test needs the vector from surface to query, and a stored
		/// last-hit field would make two interleaved queries on one surface
		/// read each other's answer.
		public bool Closest(Vector3 point, out float distance, out Vector3 normal, out Vector3 hit)
		{
			distance = float.MaxValue;
			normal = Vector3.zero;
			hit = point;
			if (triangles.Length == 0) return false;

			ProbeKey3 home = CellOf(point);
			int bestTriangle = -1;
			for (int ring = 0; ring <= Bridge.kProbeCellRings; ring++)
			{
				bool found = false;
				for (int x = home.X - ring; x <= home.X + ring; x++)
				for (int y = home.Y - ring; y <= home.Y + ring; y++)
				for (int z = home.Z - ring; z <= home.Z + ring; z++)
				{
					// The shell of the ring only; the interior was covered by
					// the previous, smaller ring.
					if (ring > 0 && x != home.X - ring && x != home.X + ring
						&& y != home.Y - ring && y != home.Y + ring
						&& z != home.Z - ring && z != home.Z + ring) continue;
					if (!cells.TryGetValue(new ProbeKey3(x, y, z), out List<int> bucket)) continue;
					found = true;
					foreach (int t in bucket)
					{
						Vector3 a = positions[triangles[t]];
						Vector3 b = positions[triangles[t + 1]];
						Vector3 c = positions[triangles[t + 2]];
						Vector3 candidateHit = ClosestPointOnTriangle(point, a, b, c);
						float candidate = (candidateHit - point).magnitude;
						if (candidate >= distance) continue;
						distance = candidate;
						hit = candidateHit;
						bestTriangle = t;
					}
				}
				// Everything past this ring is at least one cell further away
				// than the cell boundary already searched, so the best hit
				// cannot be improved.
				if (found && distance <= ring * cellSize) break;
			}
			if (bestTriangle < 0) return false;

			Vector3 p0 = positions[triangles[bestTriangle]];
			Vector3 p1 = positions[triangles[bestTriangle + 1]];
			Vector3 p2 = positions[triangles[bestTriangle + 2]];
			normal = Vector3.Cross(p1 - p0, p2 - p0).normalized;
			return true;
		}

		/// Farthest intersection of a ray with the sampled triangles, used by the
		/// radial convexity test. `hit` is false when the ray misses entirely.
		public bool FarthestAlong(Vector3 originPoint, Vector3 direction, out float far)
		{
			far = 0f;
			bool hit = false;
			for (int t = 0; t < triangles.Length; t += 3)
			{
				float distance;
				if (RayTriangle(originPoint, direction, positions[triangles[t]], positions[triangles[t + 1]], positions[triangles[t + 2]], out distance)
					&& distance > far)
				{
					far = distance;
					hit = true;
				}
			}
			return hit;
		}

		/// Ericson's closest-point-on-triangle. The barycentric region walk is
		/// what makes the answer exact on an edge or a vertex, where a
		/// interpolated-projection method reports a point off the face.
		public static Vector3 ClosestPointOnTriangle(Vector3 p, Vector3 a, Vector3 b, Vector3 c)
		{
			Vector3 ab = b - a, ac = c - a, ap = p - a;
			float d1 = Vector3.Dot(ab, ap), d2 = Vector3.Dot(ac, ap);
			if (d1 <= 0f && d2 <= 0f) return a;

			Vector3 bp = p - b;
			float d3 = Vector3.Dot(ab, bp), d4 = Vector3.Dot(ac, bp);
			if (d3 >= 0f && d4 <= d3) return b;

			float vc = d1 * d4 - d3 * d2;
			if (vc <= 0f && d1 >= 0f && d3 <= 0f) return a + ab * (d1 / (d1 - d3));

			Vector3 cp = p - c;
			float d5 = Vector3.Dot(ab, cp), d6 = Vector3.Dot(ac, cp);
			if (d6 >= 0f && d5 <= d6) return c;

			float vb = d5 * d2 - d1 * d6;
			if (vb <= 0f && d2 >= 0f && d6 <= 0f) return a + ac * (d2 / (d2 - d6));

			float va = d3 * d6 - d5 * d4;
			if (va <= 0f && (d4 - d3) >= 0f && (d5 - d6) >= 0f)
			{
				return b + (c - b) * ((d4 - d3) / ((d4 - d3) + (d5 - d6)));
			}

			float denominator = 1f / (va + vb + vc);
			return a + ab * (vb * denominator) + ac * (vc * denominator);
		}

		/// Moller-Trumbore. A ray is the right probe for "how far does this
		/// part reach", because the far intersection along a vertex direction is
		/// the convex hull itself.
		public static bool RayTriangle(Vector3 originPoint, Vector3 direction, Vector3 a, Vector3 b, Vector3 c, out float distance)
		{
			distance = 0f;
			Vector3 edge1 = b - a, edge2 = c - a;
			Vector3 pvec = Vector3.Cross(direction, edge2);
			float determinant = Vector3.Dot(edge1, pvec);
			// Either side means a back-facing triangle. A mesh with both
			// windings would be counted as non-manifold, so the two readings
			// cannot be told apart here; the topology family reports the count.
			if (Mathf.Abs(determinant) < 1e-12f) return false;
			float inverse = 1f / determinant;
			Vector3 tvec = originPoint - a;
			float u = Vector3.Dot(tvec, pvec) * inverse;
			if (u < 0f || u > 1f) return false;
			Vector3 qvec = Vector3.Cross(tvec, edge1);
			float v = Vector3.Dot(direction, qvec) * inverse;
			if (v < 0f || u + v > 1f) return false;
			distance = Vector3.Dot(edge2, qvec) * inverse;
			return distance > 0f;
		}
	}

	public static partial class Bridge
	{
		private static readonly string[] GeometryProbeChecks =
			{ "transform", "bounds", "axis", "pair", "shape", "topology" };

		private const double kProbeDefaultTolerance = 0.002;

		// Sampled rather than exhaustive: a pair query costs one nearest-surface
		// lookup per sampled vertex, so an uncapped pass over a 200k-vertex mesh
		// holds the Editor's main thread long enough to look like a hang. The
		// stride is always reported, so a sampled figure is never mistaken for
		// a whole-mesh one.
		private const int kProbeVertexSampleLimit = 2000;
		private const int kProbeShapeVertexSampleLimit = 20000;
		private const int kProbeTriangleSampleLimit = 4000;
		private const int kProbeConvexityRayLimit = 1000;
		/// Bounded so a query that lands far outside the surface's own grid
		/// returns its best hit so far instead of walking the whole grid.
		internal const int kProbeCellRings = 24;

		// A deviation below this fraction of the mesh's own size is float noise
		// on a mirrored mesh rather than an asymmetry, so a mirror plane is
		// only claimed at this threshold.
		private const double kProbeSymmetryRatioLimit = 0.05;
		// A thin-to-long extent ratio under this is ring- or tube-like.
		private const double kProbeRingLikeRatio = 0.05;
		// Relative slack on the contact threshold, so float noise cannot flip
		// the verdict for parts sitting exactly at the tolerance asked for.
		private const double kProbeToleranceEpsilon = 1e-9;
		// A vertex of a genuinely convex mesh lies ON its hull, so this is
		// float noise on the mesh's own scale, not a modelling tolerance.
		private const double kProbeConvexityEpsilon = 1e-5;
		// Duplicate detection is exact-per-cell rather than approximate, so it
		// needs the real positions and a count to work through.
		private const int kProbeDuplicateVertexLimit = 400000;
		private const double kProbePositionQuantum = 1e5;

		private static double ProbeRound(double value, int digits = 5)
		{
			return Math.Round(value, digits, MidpointRounding.AwayFromZero);
		}

		private static Json ProbeVector(Vector3 value)
		{
			Json array = new Json();
			array.OpenArray();
			array.Add(ProbeRound(value.x));
			array.Add(ProbeRound(value.y));
			array.Add(ProbeRound(value.z));
			array.CloseArray();
			return array;
		}

		// ---------------------------------------------------------- entry point

		/// `objects` selects the targets, `pairs` overrides it for the pair
		/// family alone, and an omitted `checks` means every family.
		private static string GeometryProbe(Json request)
		{
			List<string> requested = request.GetStringArray("checks");
			if (requested.Count == 0) requested = GeometryProbeChecks.ToList();
			List<string> unknown = requested.Where(check => !GeometryProbeChecks.Contains(check)).ToList();
			if (unknown.Count > 0)
			{
				throw new Exception(
					"Unknown geometry-probe check(s): " + string.Join(", ", unknown.ToArray()) +
					". Available: " + string.Join(", ", GeometryProbeChecks) + ".");
			}

			string rawTolerance = request.GetString("tolerance");
			double tolerance = string.IsNullOrEmpty(rawTolerance) ? kProbeDefaultTolerance : ParseDouble(rawTolerance);
			if (tolerance < 0) throw new Exception("geometry-probe: tolerance is a distance in metres and cannot be negative.");

			string expectedAxis = (request.GetString("expectedAxis") ?? "Z").ToUpperInvariant();
			if (expectedAxis != "X" && expectedAxis != "Y" && expectedAxis != "Z")
			{
				throw new Exception("geometry-probe: expectedAxis must be X, Y or Z, not \"" + expectedAxis + "\".");
			}

			List<GameObject> targets = ResolveProbeTargets(request.GetStringArray("objects"));
			List<string[]> pairs = ReadProbePairs(request);

			Json result = new Json();
			result.OpenObject();
			result.AddString("scene", SceneManager.GetActiveScene().path);
			result.AddArray("checks", requested.ToArray());
			result.AddNumber("tolerance", ProbeRound(tolerance, 6));
			result.AddString("expectedAxis", expectedAxis);
			// Unity's unit is the metre, and nothing in the scene data says
			// otherwise, so a size here is a size in metres and a 0.002
			// tolerance is 2 mm.
			result.AddString("unitNote", "Unity units: 1.0 = 1 metre, so every distance is metres.");

			// The surface cache is built once per probed object and shared by
			// the object, shape and pair families. Rebuilding it per family
			// would re-read and re-transform the same vertices up to four times.
			var surfaces = new Dictionary<string, ProbeSurface>(StringComparer.Ordinal);

			Json objects = new Json();
			objects.OpenArray();
			foreach (GameObject target in targets)
			{
				string path = PathInScene(target);
				Json entry = new Json();
				entry.OpenObject();
				entry.AddString("name", target.name);
				entry.AddString("path", path);
				DescribeProbeMeshSource(target, entry, out Transform meshTransform, out Mesh mesh);

				if (requested.Contains("transform")) AddProbeTransform(target, entry);
				if (requested.Contains("bounds")) AddProbeBounds(target, meshTransform, mesh, entry);
				if (requested.Contains("axis")) AddProbeAxis(meshTransform, mesh, expectedAxis, entry);
				if (requested.Contains("shape")) AddProbeShape(meshTransform, mesh, entry);
				if (requested.Contains("topology")) AddProbeTopology(mesh, entry);

				if (requested.Contains("pair") && !surfaces.ContainsKey(path))
				{
					surfaces[path] = ProbeSurface.Build(meshTransform, mesh, kProbeVertexSampleLimit, kProbeTriangleSampleLimit);
				}
				entry.CloseObject();
				objects.AddElement(entry);
			}
			objects.CloseArray();
			result.AddArray("objects", objects);

			Json pairResults = new Json();
			pairResults.OpenArray();
			foreach (string[] pair in ResolveProbePairOrder(targets, pairs, requested.Contains("pair")))
			{
				EnsureProbeSurface(pair[0], surfaces);
				EnsureProbeSurface(pair[1], surfaces);
				pairResults.AddElement(MeasureProbePair(pair[0], pair[1], tolerance, surfaces));
			}
			pairResults.CloseArray();
			result.AddArray("pairs", pairResults);
			result.CloseObject();
			return result.ToString();
		}

		// ------------------------------------------------------------- targets

		/// Probing the whole scene by default rather than demanding a name: a
		/// misplaced part is found by its relationship to the rest, and the
		/// culprit is usually not yet known. Active objects only, so a disabled
		/// prop is not reported as a mesh that occupies space.
		private static List<GameObject> ResolveProbeTargets(List<string> names)
		{
			Scene scene = SceneManager.GetActiveScene();
			if (names.Count == 0)
			{
				List<GameObject> everything = new List<GameObject>();
				foreach (GameObject root in scene.GetRootGameObjects())
				{
					if (root.activeInHierarchy && CarriesMesh(root)) everything.Add(root);
				}
				if (everything.Count == 0)
				{
					throw new Exception(
						"No active mesh root to probe in the active scene. Pass `objects` explicitly if the scene is empty or every root is filtered out.");
				}
				return everything;
			}

			var resolved = new List<GameObject>();
			foreach (string name in names)
			{
				// FindGameObjectByName refuses an ambiguous bare name, which is
				// what keeps this from silently measuring the first of two
				// objects called "Leg".
				GameObject found = FindGameObjectByName(name);
				if (found == null) throw new Exception("No GameObject named or pathed \"" + name + "\" in the active scene.");
				resolved.Add(found);
			}
			return resolved;
		}

		private static bool CarriesMesh(GameObject root)
		{
			return root.GetComponentsInChildren<MeshFilter>(false).Any(filter => filter.sharedMesh != null)
				|| root.GetComponentsInChildren<SkinnedMeshRenderer>(false).Any(skin => skin.sharedMesh != null);
		}

		/// `pairs` is an array of arrays, which GetStringArray flattens into
		/// nulls, so it is read off the parsed node directly.
		private static List<string[]> ReadProbePairs(Json request)
		{
			var pairs = new List<string[]>();
			if (!request.properties.TryGetValue("pairs", out Json raw) || raw == null) return pairs;
			foreach (Json pair in raw.items)
			{
				if (pair.items.Count < 2)
				{
					throw new Exception(
						"geometry-probe: every `pairs` entry needs exactly two object names, e.g. [[\"Mug\", \"MugHandle\"]].");
				}
				pairs.Add(new[] { pair.items[0].asString, pair.items[1].asString });
			}
			return pairs;
		}

		/// Explicit pairs first, then the two-target case the caller did not
		/// have to spell out. `pairs` takes precedence over `objects` for this
		/// family alone, so a pair may name an object the other families were
		/// never asked about.
		private static List<string[]> ResolveProbePairOrder(
			List<GameObject> targets,
			List<string[]> pairs,
			bool wantPair)
		{
			var ordered = new List<string[]>();
			if (!wantPair) return ordered;

			if (pairs.Count > 0)
			{
				foreach (string[] pair in pairs)
				{
					GameObject first = FindGameObjectByName(pair[0]);
					GameObject second = FindGameObjectByName(pair[1]);
					if (first == null || second == null)
					{
						throw new Exception(
							"geometry-probe: pair references a missing object: \"" + pair[0] + "\", \"" + pair[1] + "\".");
					}
					ordered.Add(new[] { PathInScene(first), PathInScene(second) });
				}
				return ordered;
			}

			if (targets.Count == 2) ordered.Add(new[] { PathInScene(targets[0]), PathInScene(targets[1]) });
			return ordered;
		}

		/// Builds a pair member's surface on demand, because a pair names its
		/// own objects and those need not be in the `objects` list.
		private static void EnsureProbeSurface(string path, Dictionary<string, ProbeSurface> surfaces)
		{
			if (surfaces.ContainsKey(path)) return;
			GameObject target = FindGameObjectByPath(path);
			if (target == null) throw new Exception("geometry-probe: pair references \"" + path + "\", which is not in the active scene.");
			DescribeProbeMeshSource(target, new Json(), out Transform meshTransform, out Mesh mesh);
			surfaces[path] = ProbeSurface.Build(meshTransform, mesh, kProbeVertexSampleLimit, kProbeTriangleSampleLimit);
		}

		// ------------------------------------------------------------ per check

		/// Which object the mesh actually came from, and why.
		///
		/// A prefab's mesh lives on a child, so a root named by the caller has
		/// no mesh of its own. Falling through to a child is the useful
		/// behaviour, but it is not the same object, so the owner is named
		/// rather than left implied.
		private static void DescribeProbeMeshSource(
			GameObject target,
			Json entry,
			out Transform meshTransform,
			out Mesh mesh)
		{
			MeshFilter filter = target.GetComponent<MeshFilter>();
			SkinnedMeshRenderer skin = target.GetComponent<SkinnedMeshRenderer>();
			meshTransform = target.transform;

			if (filter != null && filter.sharedMesh != null) { mesh = filter.sharedMesh; return; }
			if (skin != null && skin.sharedMesh != null) { mesh = skin.sharedMesh; return; }

			foreach (Transform child in target.GetComponentsInChildren<Transform>(true))
			{
				MeshFilter childFilter = child.GetComponent<MeshFilter>();
				if (childFilter != null && childFilter.sharedMesh != null)
				{
					mesh = childFilter.sharedMesh;
					meshTransform = child.transform;
					entry.AddString("meshOwner", PathInScene(child.gameObject));
					entry.AddString("meshState", "no mesh on \"" + target.name + "\" itself; measured the mesh on \"" + child.name + "\"");
					return;
				}
				SkinnedMeshRenderer childSkin = child.GetComponent<SkinnedMeshRenderer>();
				if (childSkin != null && childSkin.sharedMesh != null)
				{
					mesh = childSkin.sharedMesh;
					meshTransform = child.transform;
					entry.AddString("meshOwner", PathInScene(child.gameObject));
					entry.AddString("meshState", "no mesh on \"" + target.name + "\" itself; measured the skinned mesh on \"" + child.name + "\"");
					return;
				}
			}

			// An empty MeshFilter reference is a real state, not an error: it is
			// what a broken or mid-import model looks like.
			mesh = null;
			entry.AddString("meshOwner", "");
			entry.AddString("meshState", filter != null || skin != null
				? "a mesh component is present but its mesh reference is empty, so there is no geometry to measure"
				: "no MeshFilter or SkinnedMeshRenderer on this object or its children, so there is no geometry to measure");
		}

		/// localScale against lossyScale, and the mirror that neither of the
		/// scene listings surfaces.
		private static void AddProbeTransform(GameObject target, Json entry)
		{
			Transform transform = target.transform;
			Vector3 local = transform.localScale;
			Vector3 lossy = transform.lossyScale;
			// The 3x3's determinant carries the mirror. lossyScale alone does
			// not say WHICH axis was flipped, and reporting only a boolean loses
			// the one fact that fixes it: which component to correct.
			float determinant = transform.localToWorldMatrix.determinant;
			bool mirrored = determinant < 0f;
			float largest = Mathf.Max(lossy.x, Mathf.Max(lossy.y, lossy.z));
			float smallest = Mathf.Min(lossy.x, Mathf.Min(lossy.y, lossy.z));

			Json node = new Json();
			node.OpenObject();
			node.AddArray("position", VectorToJson(transform.position));
			node.AddArray("rotationEuler", VectorToJson(transform.eulerAngles));
			node.AddArray("localScale", VectorToJson(local));
			node.AddArray("lossyScale", VectorToJson(lossy));
			node.AddString("parent", transform.parent == null ? "" : PathInScene(transform.parent.gameObject));
			node.AddBool("unappliedScale", Math.Abs(largest - 1f) > 1e-4f || Math.Abs(smallest - 1f) > 1e-4f);
			node.AddBool("nonUniformScale", largest - smallest > 1e-4f);
			node.AddBool("mirrorScale", mirrored);
			node.AddString("mirroredAxis", mirrored ? MirroredAxis(local) : "");
			// A mirrored transform is the defect a render cannot show and a
			// scene listing does not carry: the object looks correct and its
			// normals, winding and any BVH offset are all inverted.
			node.AddString("state", mirrored
				? "MIRRORED: determinant " + ProbeRound(determinant, 3) + ". A negative scale component inverts normals and winding; a flipped object is correct-looking and wrong-shaded, and a negative determinant is the only place that shows."
				: "transform scale; a non-unit value is a scale that was never applied to the mesh");
			entry.AddValue("transform", node);
		}

		private static string MirroredAxis(Vector3 local)
		{
			// A single negative component is the mirrored case a user can fix;
			// two or three of them are a rotation, not a flip, and naming one
			// component would point at the wrong thing.
			int negative = 0;
			if (local.x < 0f) negative++;
			if (local.y < 0f) negative++;
			if (local.z < 0f) negative++;
			if (negative != 1) return "";
			if (local.x < 0f) return "X";
			if (local.y < 0f) return "Y";
			return "Z";
		}

		/// World-space bounds, reported twice because the two disagree in a way
		/// that matters: the mesh's own box is the geometry, and the renderer's
		/// box is what was last drawn.
		private static void AddProbeBounds(GameObject target, Transform meshTransform, Mesh mesh, Json entry)
		{
			Json node = new Json();
			node.OpenObject();

			Renderer[] renderers = target.GetComponentsInChildren<Renderer>(true).Where(r => r.enabled).ToArray();
			if (renderers.Length == 0)
			{
				node.AddString("state", "no enabled renderer, so there is nothing to measure");
				entry.AddValue("bounds", node);
				return;
			}

			Bounds world = renderers[0].bounds;
			foreach (Renderer renderer in renderers.Skip(1)) world.Encapsulate(renderer.bounds);
			bool skinned = renderers.Any(renderer => renderer is SkinnedMeshRenderer);
			node.AddString("state", skinned
				? "renderer bounds; a SkinnedMeshRenderer bounds volume is sized for the whole animation and can read much larger than the mesh at rest"
				: "renderer bounds, world space");
			node.AddArray("size", VectorToJson(world.size));
			node.AddArray("center", VectorToJson(world.center));
			node.AddArray("min", VectorToJson(world.min));
			node.AddArray("max", VectorToJson(world.max));

			if (mesh != null && mesh.isReadable)
			{
				// The mesh's own local box carried into world space. Unity gives
				// no public accessor for the world AABB of the real vertices, and
				// transforming the mesh bounds is the closest honest answer.
				Json measured = new Json();
				measured.OpenObject();
				Bounds local = mesh.bounds;
				Matrix4x4 matrix = meshTransform.localToWorldMatrix;
				Bounds corners = new Bounds(matrix.MultiplyPoint3x4(local.center), Vector3.zero);
				Vector3 size = local.size;
				for (int i = 0; i < 8; i++)
				{
					Vector3 corner = new Vector3(
						(i & 1) == 0 ? local.min.x : local.max.x,
						(i & 2) == 0 ? local.min.y : local.max.y,
						(i & 4) == 0 ? local.min.z : local.max.z);
					corners.Encapsulate(matrix.MultiplyPoint3x4(corner));
				}
				measured.AddString("state", "the mesh's own local bounding box carried into world space by the owner's transform; the true vertex AABB is only equal to this when the mesh's own box is tight, and a negative scale mirrors it");
				measured.AddArray("size", VectorToJson(corners.size));
				measured.AddArray("center", VectorToJson(corners.center));
				measured.AddArray("min", VectorToJson(corners.min));
				measured.AddArray("max", VectorToJson(corners.max));
				measured.AddNumber("localSizeX", size.x);
				measured.AddNumber("localSizeY", size.y);
				measured.AddNumber("localSizeZ", size.z);
				node.AddValue("meshBounds", measured);
			}
			entry.AddValue("bounds", node);
		}

		/// The object's own thin and long axes, and how far each is off an
		/// expected world axis.
		///
		/// The axis is read off the mesh's local bounding box rather than a
		/// covariance sweep, because that is the only version of "the object's
		/// own axis" a caller can act on: the local frame is the one a scale or
		/// a rotation fix applies to. The mapping into world space is the
		/// owner's rotation alone, and a negative scale contributes no
		/// direction at all, so it is excluded rather than folded in.
		private static void AddProbeAxis(Transform meshTransform, Mesh mesh, string expectedAxis, Json entry)
		{
			Json node = new Json();
			node.OpenObject();
			node.AddString("expectedAxis", expectedAxis);

			if (mesh == null)
			{
				node.AddString("state", "no mesh, so the object has no axis to measure");
				entry.AddValue("axis", node);
				return;
			}

			Bounds local = mesh.bounds;
			Vector3 size = local.size;
			int thinIndex = 0, longIndex = 0;
			for (int i = 1; i < 3; i++)
			{
				if (size[i] < size[thinIndex]) thinIndex = i;
				if (size[i] > size[longIndex]) longIndex = i;
			}

			Vector3 thinLocal = AxisVector(thinIndex);
			Vector3 longLocal = AxisVector(longIndex);
			Quaternion rotation = meshTransform.rotation;
			Vector3 thinWorld = rotation * thinLocal;
			Vector3 longWorld = rotation * longLocal;
			Vector3 expected = AxisVector("XYZ".IndexOf(expectedAxis));

			double thinAngle = ProbeAngle(thinWorld, expected);
			double longAngle = ProbeAngle(longWorld, expected);
			float thin = size[thinIndex];
			float longest = size[longIndex];
			double ratio = longest > 1e-9f ? thin / longest : 0.0;

			Matrix4x4 matrix = meshTransform.localToWorldMatrix;
			Json measured = new Json();
			measured.OpenObject();
			measured.AddString("state", "derived from the mesh's LOCAL bounding box, so the axis is exact for an axis-aligned mesh and an approximation for a rotated or tapered one; a box's smallest axis is not always the part's own axis");
			measured.AddArray("thinAxisLocal", ProbeVector(thinLocal));
			measured.AddArray("thinAxisWorld", ProbeVector(thinWorld));
			measured.AddArray("longAxisLocal", ProbeVector(longLocal));
			measured.AddArray("longAxisWorld", ProbeVector(longWorld));
			measured.AddNumber("thinExtent", ProbeRound(thin, 6));
			measured.AddNumber("longExtent", ProbeRound(longest, 6));
			measured.AddNumber("thinAxisAngleToExpected", ProbeRound(thinAngle, 2));
			measured.AddNumber("longAxisAngleToExpected", ProbeRound(longAngle, 2));
			measured.AddNumber("axisRatio", ProbeRound(ratio));
			measured.AddBool("isRingLike", ratio < kProbeRingLikeRatio && longest > 1e-9f);
			measured.AddArray("center", VectorToJson(matrix.MultiplyPoint3x4(local.center)));
			measured.AddBool("mirrorScale", matrix.determinant < 0f);
			measured.AddString(
				"centerNote",
				matrix.determinant < 0f
					? "the owner is mirrored, so this centre is a valid position but the world axes read through it are handedness-reversed"
					: "world position of the mesh's local bounding-box centre");
			node.AddValue("measured", measured);
			entry.AddValue("axis", node);
		}

		private static Vector3 AxisVector(int index)
		{
			if (index == 0) return Vector3.right;
			if (index == 1) return Vector3.up;
			return Vector3.forward;
		}

		private static double ProbeAngle(Vector3 first, Vector3 second)
		{
			float dot = Mathf.Clamp(Vector3.Dot(first.normalized, second.normalized), -1f, 1f);
			return Math.Acos(dot) * (180.0 / Math.PI);
		}

		/// Self-symmetry, convexity and open edges: the defects a render cannot
		/// show, because the offending part faces away or is occluded.
		private static void AddProbeShape(Transform meshTransform, Mesh mesh, Json entry)
		{
			Json node = new Json();
			node.OpenObject();

			if (mesh == null)
			{
				node.AddString("state", "no mesh, so there is no shape to measure");
				node.AddNumber("vertices", 0);
				entry.AddValue("shape", node);
				return;
			}
			if (!mesh.isReadable)
			{
				node.AddString("state", "mesh is not Read/Write Enabled in its import settings, so its vertices cannot be read without changing the import");
				node.AddNumber("vertices", mesh.vertexCount);
				entry.AddValue("shape", node);
				return;
			}

			Vector3[] local;
			try
			{
				local = mesh.vertices;
			}
			catch (Exception error)
			{
				node.AddString("state", "mesh data could not be read: " + error.Message);
				node.AddNumber("vertices", mesh.vertexCount);
				entry.AddValue("shape", node);
				return;
			}

			Bounds box = mesh.bounds;
			Vector3 size = box.size;
			node.AddNumber("vertices", local.Length);
			node.AddNumber("triangles", mesh.triangles.Length / 3);
			node.AddArray("dimensions", ProbeVector(size));

			int total = local.Length;
			int stride = Math.Max(1, total / kProbeShapeVertexSampleLimit);
			var sampled = new List<Vector3>();
			for (int i = 0; i < total; i += stride) sampled.Add(local[i]);
			if (stride > 1 && total > 0) sampled.Add(local[total - 1]);

			Json symmetry = MeasureProbeSymmetry(sampled, box);
			node.AddValue("selfSymmetry", symmetry);
			Json convexity = MeasureProbeConvexity(meshTransform, mesh, box, local);
			node.AddValue("convexity", convexity);

			int[] indices;
			try
			{
				indices = mesh.triangles;
			}
			catch (Exception error)
			{
				node.AddString("state", "triangle data could not be read: " + error.Message);
				entry.AddValue("shape", node);
				return;
			}

			var edgeUse = new Dictionary<long, int>();
			var vertexUse = new bool[local.Length];
			int degenerate = 0;
			for (int t = 0; t + 2 < indices.Length; t += 3)
			{
				int a = indices[t], b = indices[t + 1], c = indices[t + 2];
				// A mesh whose index buffer disagrees with its vertex count is
				// corrupt; skip rather than throw, since the rest of the shape
				// figures are still worth reporting.
				if (a < 0 || b < 0 || c < 0 || a >= vertexUse.Length || b >= vertexUse.Length || c >= vertexUse.Length) continue;
				vertexUse[a] = true;
				vertexUse[b] = true;
				vertexUse[c] = true;
				if (a == b || b == c || a == c)
				{
					degenerate++;
					continue;
				}
				CountEdge(edgeUse, a, b);
				CountEdge(edgeUse, b, c);
				CountEdge(edgeUse, a, c);
			}
			// Fewer than two adjacent faces, not zero: a boundary edge of a
			// grid has exactly one, and a zero test reports a wide-open plane
			// as watertight.
			int openEdges = 0;
			foreach (KeyValuePair<long, int> edge in edgeUse) if (edge.Value < 2) openEdges++;
			int looseVertices = vertexUse.Count(used => !used);

			node.AddNumber("openEdges", openEdges);
			node.AddNumber("looseVertices", looseVertices);
			node.AddNumber("degenerateFaces", degenerate);
			node.AddString(
				"state",
				"counts are exact over every triangle in the mesh; the symmetry and convexity figures are the sampled ones and say so themselves");
			if (stride > 1)
			{
				node.AddString(
					"sampleNote",
					"symmetry and convexity used " + sampled.Count + " of " + total + " vertices, evenly strided");
			}
			entry.AddValue("shape", node);
		}

		private static void CountEdge(Dictionary<long, int> edgeUse, int a, int b)
		{
			int low = Math.Min(a, b), high = Math.Max(a, b);
			// The edge is a pair of indices, and a mesh can exceed 2^31
			// vertices, so the key is two 32-bit halves rather than a
			// multiplication that could collide.
			long key = ((long)low << 32) | (uint)high;
			int count;
			edgeUse[key] = edgeUse.TryGetValue(key, out count) ? count + 1 : 1;
		}

		/// How far each candidate mirror plane disagrees with the mesh.
		///
		/// The mirror plane is the mesh's OWN bounding-box centre, not the
		/// origin: a model that merely sits off to one side is still symmetric,
		/// and testing against the origin reports every placed object as
		/// lopsided. Each mirrored point is matched to its nearest real vertex
		/// rather than to an index, so uneven vertex density does not read as
		/// asymmetry.
		private static Json MeasureProbeSymmetry(List<Vector3> sampled, Bounds box)
		{
			Json node = new Json();
			node.OpenObject();
			if (sampled.Count < 3)
			{
				node.AddString("state", "fewer than 3 sampled vertices, so there is no symmetry to measure");
				node.AddString("mirrorAxis", "");
				return node;
			}

			// Nearest-vertex lookup by quantised position: a Dictionary rather
			// than an O(n^2) scan, because a 20k-vertex sample is 400M pairs.
			var lookup = new Dictionary<ProbeKey3, List<int>>();
			for (int i = 0; i < sampled.Count; i++)
			{
				ProbeKey3 key = Quantize(sampled[i]);
				if (!lookup.TryGetValue(key, out List<int> bucket))
				{
					bucket = new List<int>();
					lookup[key] = bucket;
				}
				bucket.Add(i);
			}

			float largest = Mathf.Max(box.size.x, Mathf.Max(box.size.y, box.size.z));
			if (largest <= 0f) largest = 1f;
			Vector3 center = box.center;

			var perAxis = new Json();
			perAxis.OpenArray();
			double bestRatio = double.MaxValue;
			string bestAxis = "";
			for (int axis = 0; axis < 3; axis++)
			{
				double total = 0.0;
				double worst = 0.0;
				foreach (Vector3 point in sampled)
				{
					Vector3 mirrored = point;
					float component = axis == 0 ? mirrored.x : (axis == 1 ? mirrored.y : mirrored.z);
					float centreComponent = axis == 0 ? center.x : (axis == 1 ? center.y : center.z);
					float flipped = 2f * centreComponent - component;
					if (axis == 0) mirrored.x = flipped;
					else if (axis == 1) mirrored.y = flipped;
					else mirrored.z = flipped;

					double distance = NearestSampleDistance(lookup, sampled, mirrored);
					total += distance;
					if (distance > worst) worst = distance;
				}
				double mean = total / sampled.Count;
				double ratio = mean / largest;
				Json entry = new Json();
				entry.OpenObject();
				entry.AddString("axis", "XYZ".Substring(axis, 1));
				entry.AddNumber("meanDeviation", ProbeRound(mean, 6));
				entry.AddNumber("maxDeviation", ProbeRound(worst, 6));
				entry.AddNumber("deviationRatio", ProbeRound(ratio));
				entry.CloseObject();
				perAxis.AddElement(entry);
				if (ratio < bestRatio)
				{
					bestRatio = ratio;
					bestAxis = "XYZ".Substring(axis, 1);
				}
			}
			perAxis.CloseArray();

			bool symmetric = bestRatio <= kProbeSymmetryRatioLimit;
			node.AddString("mirrorAxis", symmetric ? bestAxis : "");
			node.AddNumber("deviationRatio", ProbeRound(bestRatio));
			node.AddString(
				"state",
				(symmetric
					? "symmetric about the mesh's own " + bestAxis + " mirror plane to within " + kProbeSymmetryRatioLimit * 100.0 + "% of its size"
					: "least-asymmetric of the three world axes; none is within " + kProbeSymmetryRatioLimit * 100.0 + "% of the mesh size, so no mirror plane is claimed")
				+ ". Deviation is measured to the nearest SAMPLED vertex, so a mesh whose mirror is finer than the sample stride can read as symmetric when it is not.");
			node.AddValue("perAxis", perAxis);
			return node;
		}

		private static ProbeKey3 Quantize(Vector3 point)
		{
			return new ProbeKey3(
				(int)Math.Floor(point.x * kProbePositionQuantum),
				(int)Math.Floor(point.y * kProbePositionQuantum),
				(int)Math.Floor(point.z * kProbePositionQuantum));
		}

		/// Nearest sampled vertex to a query point, expanding a ring of cells
		/// until the hit cannot be beaten. Bounded, so a query that lands
		/// outside the sample's own grid returns its best found so far rather
		/// than looping.
		private static double NearestSampleDistance(Dictionary<ProbeKey3, List<int>> lookup, List<Vector3> sampled, Vector3 query)
		{
			ProbeKey3 home = Quantize(query);
			double best = double.MaxValue;
			for (int ring = 0; ring <= 8; ring++)
			{
				bool found = false;
				for (int x = home.X - ring; x <= home.X + ring; x++)
				for (int y = home.Y - ring; y <= home.Y + ring; y++)
				for (int z = home.Z - ring; z <= home.Z + ring; z++)
				{
					if (!lookup.TryGetValue(new ProbeKey3(x, y, z), out List<int> bucket)) continue;
					found = true;
					foreach (int index in bucket)
					{
						double candidate = (sampled[index] - query).magnitude;
						if (candidate < best) best = candidate;
					}
				}
				if (found && best <= ring) break;
			}
			return best == double.MaxValue ? 0.0 : best;
		}

		/// Fraction of the surface that sits on the mesh's own convex hull.
		///
		/// Measured radially: a vertex is on the hull when the farthest
		/// intersection along its own direction from the mesh's centre is
		/// itself. That is exact for a star-shaped mesh, and a torus or a
		/// dumbbell is not star-shaped about its centre, so the ratio reads high
		/// for a legitimately concave part -- which is a property of the test
		/// and is stated rather than hidden.
		private static Json MeasureProbeConvexity(Transform meshTransform, Mesh mesh, Bounds box, Vector3[] local)
		{
			Json node = new Json();
			node.OpenObject();
			if (local.Length < 4)
			{
				node.AddBool("available", false);
				node.AddString("state", "fewer than 4 vertices, so convexity has nothing to measure");
				node.AddNumber("convexityRatio", 0);
				return node;
			}

			var surface = ProbeSurface.Build(meshTransform, mesh, kProbeVertexSampleLimit, kProbeTriangleSampleLimit);
			if (!surface.Available)
			{
				node.AddBool("available", false);
				node.AddString("state", surface.State);
				node.AddNumber("convexityRatio", 0);
				return node;
			}

			Vector3 center = meshTransform.localToWorldMatrix.MultiplyPoint3x4(box.center);
			int limit = Math.Min(kProbeConvexityRayLimit, local.Length);
			int rayStride = Math.Max(1, local.Length / limit);
			int onHull = 0, counted = 0;
			double worstDepth = 0.0;

			for (int i = 0; i < local.Length; i += rayStride)
			{
				Vector3 world = meshTransform.localToWorldMatrix.MultiplyPoint3x4(local[i]);
				Vector3 offset = world - center;
				float reach = offset.magnitude;
				if (reach <= 1e-9f) continue;
				float far;
				// A ray that misses the mesh entirely means the mesh is not
				// closed around the centre, which the state reports rather than
				// counting as a hull hit.
				if (!surface.FarthestAlong(center, offset / reach, out far)) continue;
				counted++;
				if (Math.Abs(far - reach) <= kProbeConvexityEpsilon) onHull++;
				else worstDepth = Math.Max(worstDepth, far - reach);
			}

			if (counted == 0)
			{
				node.AddBool("available", false);
				node.AddString("state", "no vertex ray met the surface, so the mesh is not closed around its own centre");
				node.AddNumber("convexityRatio", 0);
				return node;
			}

			node.AddBool("available", true);
			node.AddNumber("convexityRatio", ProbeRound((double)onHull / counted, 4));
			node.AddNumber("sampledVertices", counted);
			node.AddNumber("deepestInsideHull", ProbeRound(worstDepth, 6));
			node.AddString(
				"state",
				"radial test from the mesh's own bounding-box centre: a vertex counts as on the hull when the farthest hit along its direction is itself. Exact for a star-shaped mesh, and reading HIGH for a legitimately concave part such as a torus or a dumbbell, because the centre is outside their solid.");
			return node;
		}

		/// Non-manifold, loose and duplicate geometry counts. Exact over every
		/// triangle, because a defect count is only worth reading when it is
		/// the real one.
		private static void AddProbeTopology(Mesh mesh, Json entry)
		{
			Json node = new Json();
			node.OpenObject();
			if (mesh == null || !mesh.isReadable)
			{
				// Zeroes, not an exception and not an absent section: a caller
				// scanning for defects reads these numbers, and an empty mesh
				// genuinely has none. The state is what distinguishes "clean"
				// from "never measured", which a bare 0 cannot.
				node.AddString("state", mesh == null
					? "no mesh, so there is no topology to inspect"
					: "mesh is not Read/Write Enabled in its import settings, so its indices and vertices cannot be read without changing the import");
				node.AddNumber("vertices", mesh == null ? 0 : mesh.vertexCount);
				node.AddNumber("triangles", 0);
				node.AddNumber("nonManifoldEdges", 0);
				node.AddNumber("boundaryEdges", 0);
				node.AddNumber("looseVertices", 0);
				node.AddNumber("degenerateFaces", 0);
				// duplicateVertices is deliberately ABSENT rather than 0: nothing
				// was counted, and a 0 would read as "welded cleanly".
				node.AddString("duplicateState", mesh == null
					? "not counted: there is no mesh"
					: "not counted: the mesh's vertex positions could not be read");
				entry.AddValue("topology", node);
				return;
			}

			int[] indices;
			try
			{
				indices = mesh.triangles;
			}
			catch (Exception error)
			{
				node.AddString("state", "triangle data could not be read: " + error.Message);
				node.AddNumber("vertices", mesh.vertexCount);
				node.AddNumber("triangles", 0);
				node.AddNumber("nonManifoldEdges", 0);
				node.AddNumber("boundaryEdges", 0);
				node.AddNumber("looseVertices", 0);
				node.AddNumber("degenerateFaces", 0);
				node.AddString("duplicateState", "not counted: the mesh's triangle data could not be read");
				entry.AddValue("topology", node);
				return;
			}

			var edgeUse = new Dictionary<long, int>();
			var vertexUse = new bool[mesh.vertexCount];
			int degenerate = 0;
			for (int t = 0; t + 2 < indices.Length; t += 3)
			{
				int a = indices[t], b = indices[t + 1], c = indices[t + 2];
				if (a < 0 || b < 0 || c < 0 || a >= vertexUse.Length || b >= vertexUse.Length || c >= vertexUse.Length) continue;
				vertexUse[a] = true;
				vertexUse[b] = true;
				vertexUse[c] = true;
				if (a == b || b == c || a == c)
				{
					degenerate++;
					continue;
				}
				CountEdge(edgeUse, a, b);
				CountEdge(edgeUse, b, c);
				CountEdge(edgeUse, a, c);
			}

			int nonManifold = 0, boundary = 0;
			foreach (KeyValuePair<long, int> edge in edgeUse)
			{
				if (edge.Value == 1) boundary++;
				else if (edge.Value > 2) nonManifold++;
			}
			// A wire edge carries no face at all, so it cannot appear in a
			// triangle-derived map: it is only reachable through the vertices,
			// and those are exactly what looseVertices counts.
			int looseVertices = vertexUse.Count(used => !used);

			// A vertex repeated at one position is a split that never healed. It is
			// a weld defect rather than a topology one, but it is the number that
			// explains a visible seam, so it is reported here. Omitted rather
			// than zeroed when not counted, because a 0 would read as "welded
			// cleanly" and mean the opposite.
			int? duplicates = null;
			string duplicateState = "";
			if (mesh.vertexCount > kProbeDuplicateVertexLimit)
			{
				duplicateState = "not counted: " + mesh.vertexCount + " vertices is over the "
					+ kProbeDuplicateVertexLimit + " limit for an exact position comparison";
			}
			else
			{
				try
				{
					var seen = new Dictionary<ProbeKey3, int>();
					duplicates = 0;
					foreach (Vector3 vertex in mesh.vertices)
					{
						ProbeKey3 key = Quantize(vertex);
						if (seen.ContainsKey(key)) duplicates++;
						else seen[key] = 1;
					}
					duplicateState = "vertices sharing a position to within 1e-5 of the mesh's own units";
				}
				catch (Exception error)
				{
					duplicates = null;
					duplicateState = "vertex positions could not be read: " + error.Message;
				}
			}

			node.AddNumber("vertices", mesh.vertexCount);
			node.AddNumber("triangles", indices.Length / 3);
			node.AddNumber("nonManifoldEdges", nonManifold);
			node.AddNumber("boundaryEdges", boundary);
			node.AddNumber("looseVertices", looseVertices);
			node.AddNumber("degenerateFaces", degenerate);
			if (duplicates.HasValue) node.AddNumber("duplicateVertices", duplicates.Value);
			node.AddString("duplicateState", duplicateState);
			node.AddString(
				"state",
				"non-manifold counts edges shared by MORE than two faces; a consistent-winding assumption is not made, so a mesh with flipped faces is reported here rather than corrected");
			entry.AddValue("topology", node);
		}

		// ---------------------------------------------------------------- pair

		/// The measurement that catches two placed prefabs occupying the same
		/// space: a verdict plus the numbers behind it, so the report is a
		/// measurement rather than an impression.
		private static Json MeasureProbePair(
			string firstPath,
			string secondPath,
			double tolerance,
			Dictionary<string, ProbeSurface> surfaces)
		{
			Json node = new Json();
			node.OpenObject();
			node.AddString("a", firstPath);
			node.AddString("b", secondPath);
			node.AddNumber("tolerance", ProbeRound(tolerance, 6));

			ProbeSurface first, second;
			if (!surfaces.TryGetValue(firstPath, out first) || !surfaces.TryGetValue(secondPath, out second))
			{
				throw new Exception(
					"geometry-probe: could not build a measurable surface for \"" + firstPath + "\" or \"" + secondPath + "\".");
			}

			if (!first.Available || !second.Available)
			{
				node.AddString("verdict", "unknown");
				node.AddString("reason", !first.Available ? firstPath + ": " + first.State : secondPath + ": " + second.State);
				node.CloseObject();
				return node;
			}
			if (first.samples.Count == 0 || second.samples.Count == 0)
			{
				node.AddString("verdict", "unknown");
				node.AddString("reason", "no vertices to sample on one side of the pair");
				node.CloseObject();
				return node;
			}

			// Symmetric on purpose: measuring only A against B misses a part of B
			// that has reached into A and stopped, which is the half of the
			// interpenetration that reads as "fine, they are not touching" from
			// one direction.
			double firstClosest = double.MaxValue, firstDeepest = 0.0;
			int firstInside = SampleAgainst(first, second, out firstClosest, out firstDeepest);
			double secondClosest = double.MaxValue, secondDeepest = 0.0;
			int secondInside = SampleAgainst(second, first, out secondClosest, out secondDeepest);

			double closest = Math.Min(firstClosest, secondClosest);
			double deepest = Math.Max(firstDeepest, secondDeepest);
			int inside = firstInside + secondInside;

			double overlapX = Math.Min(first.WorldMax.x, second.WorldMax.x) - Math.Max(first.WorldMin.x, second.WorldMin.x);
			double overlapY = Math.Min(first.WorldMax.y, second.WorldMax.y) - Math.Max(first.WorldMin.y, second.WorldMin.y);
			double overlapZ = Math.Min(first.WorldMax.z, second.WorldMax.z) - Math.Max(first.WorldMin.z, second.WorldMin.z);

			string verdict;
			string reason;
			if (inside > 0)
			{
				verdict = "interpenetrating";
				reason = inside + " sampled vertex/vertices sit inside the other object";
			}
			else if (closest <= tolerance * (1.0 + kProbeToleranceEpsilon) + kProbeToleranceEpsilon)
			{
				// The relative slack is not decoration: a gap measuring exactly
				// the requested tolerance lands a few ulps above it, and a bare
				// `<=` then reports "separated" for parts that are touching at
				// the threshold the caller asked for.
				verdict = "in-contact";
				reason = "closest sampled surfaces are within the " + ProbeRound(tolerance, 6) + " m tolerance";
			}
			else
			{
				verdict = "separated";
				reason = "closest sampled surfaces are " + ProbeRound(closest, 5) + " m apart, beyond the "
					+ ProbeRound(tolerance, 6) + " m tolerance";
			}

			Json overlap = new Json();
			overlap.OpenObject();
			overlap.AddNumber("x", ProbeRound(overlapX));
			overlap.AddNumber("y", ProbeRound(overlapY));
			overlap.AddNumber("z", ProbeRound(overlapZ));
			overlap.CloseObject();

			Json insideCounts = new Json();
			insideCounts.OpenObject();
			insideCounts.AddNumber(firstPath, firstInside);
			insideCounts.AddNumber(secondPath, secondInside);
			insideCounts.CloseObject();

			Json sampledCounts = new Json();
			sampledCounts.OpenObject();
			sampledCounts.AddNumber(firstPath, first.samples.Count);
			sampledCounts.AddNumber(secondPath, second.samples.Count);
			sampledCounts.CloseObject();

			node.AddString("verdict", verdict);
			node.AddString("reason", reason);
			node.AddNumber("closestSurfaceDistance", closest == double.MaxValue ? 0.0 : ProbeRound(closest));
			node.AddNumber("deepestPenetration", ProbeRound(deepest));
			node.AddValue("verticesInsideTarget", insideCounts);
			node.AddValue("sampledVertexCount", sampledCounts);
			node.AddValue("boundingBoxOverlap", overlap);
			node.AddString(
				"state",
				"inside is a sign test against the nearest face normal, which is only meaningful on a CLOSED mesh: an open surface reports a side rather than an interior, so a non-zero verticesInsideTarget on a mesh with open edges may be that. Distance and penetration are to the SAMPLED triangles, and a negative boundingBoxOverlap component means separated on that axis.");
			node.CloseObject();
			return node;
		}

		/// Distance from every sampled point of `from` to `to`'s surface, and
		/// how many of those points sit behind that surface.
		///
		/// `closest` is tracked over every sample including the ones found
		/// inside, because for a fully interpenetrating pair that is the only
		/// distance figure there is -- and a pair reporting 0 for its own gap
		/// reads as "touching" when the truth is "buried".
		private static int SampleAgainst(ProbeSurface from, ProbeSurface to, out double closest, out double deepest)
		{
			closest = double.MaxValue;
			deepest = 0.0;
			int inside = 0;
			foreach (Vector3 point in from.samples)
			{
				Vector3 normal, hit;
				float distance;
				if (!to.Closest(point, out distance, out normal, out hit)) continue;
				if (distance < closest) closest = distance;
				// Behind the nearest face, for a closed mesh, means inside it.
				if (Vector3.Dot(point - hit, normal) < 0f)
				{
					inside++;
					deepest = Math.Max(deepest, distance);
				}
			}
			return inside;
		}
	}
}
