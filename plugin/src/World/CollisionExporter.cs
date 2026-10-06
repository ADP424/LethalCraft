using System;
using System.Collections.Generic;
using LethalCraft.Link;
using UnityEngine;

namespace LethalCraft.World
{
	/// <summary>
	/// Streams the ship's static collision around the player to Minecraft, one 8x8x8-block region
	/// at a time: exact triangles for the player's smooth collider and 1/8-block voxels for everything
	/// else (CollisionWorker does that part off the main thread).
	///
	/// The ship is mostly BoxColliders (some with negative scale), which go over exactly
	/// as oriented boxes; capsules and spheres as what they are; mesh colliders (stairs, ramps, curved
	/// walls) as their triangles, or probed with ray casts when the mesh can't be read. Only what
	/// stays put: anything on a rigidbody (doors, loot, carts, enemies) is pushed by the player's own
	/// body in Lethal Company instead.
	/// </summary>
	internal sealed class CollisionExporter
	{
		public static readonly CollisionExporter Instance = new CollisionExporter();

		private const int Radius = 4;    // regions around the player horizontally (arrows fly far)
		private const int Below = 3;
		private const int Above = 2;
		private const float RefreshNearSeconds = 0.25f; // re-check regions next to the player this often (doors opening); unchanged ones cost a query
		private const int MaxPerFrame = 3;
		private const float FrameBudgetMs = 2.5f;

		private readonly CollisionWorker worker = new CollisionWorker();
		private readonly Dictionary<long, float> harvested = new Dictionary<long, float>();
		private readonly List<(int x, int y, int z)> offsets = new List<(int, int, int)>();
		private readonly List<(int x, int y, int z)> urgent = new List<(int, int, int)>();
		private readonly Collider[] hits = new Collider[2048];
		private readonly Dictionary<Mesh, (Vector3[] v, int[] t)> meshCache = new Dictionary<Mesh, (Vector3[], int[])>();
		private readonly HashSet<Mesh> unreadable = new HashSet<Mesh>();
		private readonly Dictionary<Collider, Info> infos = new Dictionary<Collider, Info>();
		private int mask;
		private bool started;
		private uint epoch;

		public uint Epoch => epoch;
		public int Pending => worker.Pending;

		/// <summary>What a collider is to Minecraft: whether it digs, what into, and whether it's the land.</summary>
		private struct Info
		{
			public bool Skip;
			public uint Flags;
		}

		public void Start()
		{
			if (started)
			{
				return;
			}
			started = true;
			for (int dx = -Radius; dx <= Radius; dx++)
			{
				for (int dz = -Radius; dz <= Radius; dz++)
				{
					for (int dy = -Below; dy <= Above; dy++)
					{
						offsets.Add((dx, dy, dz));
					}
				}
			}
			offsets.Sort((a, b) => (a.x * a.x + a.z * a.z + a.y * a.y * 2).CompareTo(b.x * b.x + b.z * b.z + b.y * b.y * 2));
						worker.Start();
		}

		/// <summary>Drops everything; Minecraft clears its store when it sees the new epoch.</summary>
		public void Reset(uint newEpoch)
		{
			epoch = newEpoch;
			harvested.Clear();
			signatures.Clear();
			urgent.Clear();
			infos.Clear();
			meshCache.Clear();
			unreadable.Clear();
			meshIndices.Clear();
			pieces.Clear();
			mask = 0;
			worker.Reset(newEpoch);
		}

		/// <summary>
		/// Once a frame with the player's position (Minecraft coords), and where the other players in
		/// our Minecraft world are (hosting a lobby's world: its server needs Lethal Company's ground under them).
		/// </summary>
		public void Update(Vector3 playerMc, List<Vector3> others = null)
		{
			if (!started || worker.Pending > 64)
			{
				return; // the worker is behind (or Minecraft is not reading the ring)
			}
			var centre = RegionOf(playerMc);
			centres.Clear();
			centres.Add(centre);
			if (others != null)
			{
				foreach (var p in others)
				{
					centres.Add(RegionOf(p));
				}
			}
			float now = Time.realtimeSinceStartup;
			var watch = System.Diagnostics.Stopwatch.StartNew();
			try
			{
				UpdateRegions(now, watch);
			}
			finally
			{
				float ms = (float)watch.Elapsed.TotalMilliseconds;
				if (ms > MaxMs)
				{
					MaxMs = ms;
				}
			}
		}

		/// <summary>The slowest frame's harvest since the last read (diagnostics).</summary>
		public float MaxMs;

		private void UpdateRegions(float now, System.Diagnostics.Stopwatch watch)
		{
			var centre = centres[0];
			int done = 0;
			Physics.SyncTransforms();
			while (urgent.Count > 0 && done < MaxPerFrame * 2)
			{
				var r = urgent[urgent.Count - 1];
				urgent.RemoveAt(urgent.Count - 1);
				if (!NearAny(r))
				{
					harvested.Remove(Clip.Key(r.x, r.y, r.z)); // far away: sent again whenever it's needed
					continue;
				}
				Harvest(r.x, r.y, r.z, true);
				harvested[Clip.Key(r.x, r.y, r.z)] = now;
				done++;
			}
			foreach (var o in offsets)
			{
				int rx = centre.x + o.x, ry = centre.y + o.y, rz = centre.z + o.z;
				long key = Clip.Key(rx, ry, rz);
				// Regions are 8 blocks tall: the player's head is often in the one above their feet, so a door
				// that opened there must be seen too (or its closed shape stays and blocks the doorway).
				bool near = Math.Abs(o.x) <= 1 && Math.Abs(o.z) <= 1 && o.y >= -1 && o.y <= 1;
				bool known = harvested.TryGetValue(key, out float at);
				if (known && !(near && now - at > RefreshNearSeconds))
				{
					continue;
				}
				// A periodic refresh is skipped if nothing changed (a cheap query, not counted); a region not
				// known here always goes.
				bool sent = Harvest(rx, ry, rz, !known);
				harvested[key] = now;
				if ((sent && ++done >= MaxPerFrame) || watch.Elapsed.TotalMilliseconds > FrameBudgetMs)
				{
					break;
				}
			}
			// Around the others: what isn't known yet, nearest first, with what's left of the budget.
			for (int i = 1; i < centres.Count && done < MaxPerFrame && watch.Elapsed.TotalMilliseconds <= FrameBudgetMs; i++)
			{
				var c = centres[i];
				foreach (var o in offsets)
				{
					if (Math.Abs(o.x) > OthersRadius || Math.Abs(o.z) > OthersRadius || o.y < -OthersBelow || o.y > Above)
					{
						continue;
					}
					long key = Clip.Key(c.x + o.x, c.y + o.y, c.z + o.z);
					if (harvested.ContainsKey(key))
					{
						continue;
					}
					Harvest(c.x + o.x, c.y + o.y, c.z + o.z, true);
					harvested[key] = now;
					if (++done >= MaxPerFrame || watch.Elapsed.TotalMilliseconds > FrameBudgetMs)
					{
						break;
					}
				}
			}
			if (harvested.Count > offsets.Count * (4 + 2 * (centres.Count - 1)))
			{
				harvested.Clear();
			}
			if (signatures.Count > offsets.Count * (8 + 2 * (centres.Count - 1)))
			{
				signatures.Clear();
			}
		}

		private const int OthersRadius = 3;
		private const int OthersBelow = 2;
		private readonly List<(int x, int y, int z)> centres = new List<(int, int, int)>();

		private static (int x, int y, int z) RegionOf(Vector3 mc) => (
			FloorDiv((int)Math.Floor(mc.x), CollisionWorker.RegionSize),
			FloorDiv((int)Math.Floor(mc.y), CollisionWorker.RegionSize),
			FloorDiv((int)Math.Floor(mc.z), CollisionWorker.RegionSize));

		private bool NearAny((int x, int y, int z) r)
		{
			for (int i = 0; i < centres.Count; i++)
			{
				var c = centres[i];
				int reach = i == 0 ? Radius : OthersRadius;
				int below = i == 0 ? Below : OthersBelow;
				if (Math.Abs(r.x - c.x) <= reach + 1 && Math.Abs(r.z - c.z) <= reach + 1 && r.y - c.y >= -below - 1 && r.y - c.y <= Above + 1)
				{
					return true;
				}
			}
			return false;
		}

		private static int FloorDiv(int a, int b) => (int)Math.Floor((double)a / b);

		// ---- one region ----------------------------------------------------------------------------

		private readonly Dictionary<long, long> signatures = new Dictionary<long, long>();

		/// <summary>Sends the region to Minecraft unless nothing in it changed (and not forced); true if it went.</summary>
		private bool Harvest(int rx, int ry, int rz, bool force)
		{
			var job = new CollisionJob { Rx = rx, Ry = ry, Rz = rz, Epoch = epoch };
			int s = CollisionWorker.RegionSize;
			const float margin = 0.25f;
			// The region's box: axis-aligned in the frame's own space (Minecraft's blocks), so an oriented box in the world.
			Coords.RegionBox(rx * s - margin, ry * s - margin, rz * s - margin, (rx + 1) * s + margin, (ry + 1) * s + margin, (rz + 1) * s + margin,
				out Vector3 centre, out Vector3 half, out Quaternion rot);
			int n = Physics.OverlapBoxNonAlloc(centre, half, hits, rot, Mask, QueryTriggerInteraction.Ignore);
			var regionBounds = WorldAabb(centre, half, rot);
			// Nothing moved, appeared or went since this region was last sent: Minecraft has it already.
			long sig = n * 7919L;
			for (int i = 0; i < n; i++)
			{
				var c = hits[i];
				if (c == null || !c.enabled)
				{
					continue;
				}
				var tp = c.transform.position;
				sig = sig * 31 + c.GetInstanceID();
				sig = sig * 31 + Mathf.RoundToInt(tp.x * 200f) * 73856093L + Mathf.RoundToInt(tp.y * 200f) * 19349663L + Mathf.RoundToInt(tp.z * 200f) * 83492791L;
				// The whole rotation: a swinging door keeps its position (it turns about its hinge), and an
				// imported model at -90 degrees pitch shows a turn about the vertical in its other Euler angles.
				var tr = c.transform.rotation;
				sig = sig * 31 + Mathf.RoundToInt(tr.x * 1000f) * 73856093L + Mathf.RoundToInt(tr.y * 1000f) * 19349663L + Mathf.RoundToInt(tr.z * 1000f) * 83492791L + Mathf.RoundToInt(tr.w * 1000f);
			}
			long rkey = Clip.Key(rx, ry, rz);
			if (!force && signatures.TryGetValue(rkey, out long last) && last == sig)
			{
				return false;
			}
			signatures[rkey] = sig;
			long regionStart = watchAll.ElapsedTicks;
			slowestTicks = 0;
			slowest = null;
			try
			{
				HarvestColliders(n, job, regionBounds, rkey);
			}
			finally
			{
				double ms = (watchAll.ElapsedTicks - regionStart) * 1000.0 / System.Diagnostics.Stopwatch.Frequency;
				if (ms > 15.0 && Time.realtimeSinceStartup > nextSlowReport)
				{
					nextSlowReport = Time.realtimeSinceStartup + 3f;
					double worst = slowestTicks * 1000.0 / System.Diagnostics.Stopwatch.Frequency;
					string what = slowest == null ? "?" : slowest.name + " (" + slowest.GetType().Name
						+ (slowest is MeshCollider smc && smc.sharedMesh != null ? ", mesh " + smc.sharedMesh.name + ", readable=" + smc.sharedMesh.isReadable : "") + ")";
					Log.Info($"collision: slow region ({rx} {ry} {rz}): {ms:0.0} ms for {n} colliders; slowest {what}: {worst:0.0} ms");
				}
			}
			return true;
		}

		private void HarvestColliders(int n, CollisionJob job, Bounds regionBounds, long rkey)
		{
			for (int i = 0; i < n; i++)
			{
				var col = hits[i];
				if (col == null || !col.enabled)
				{
					continue;
				}
				Info info = InfoFor(col);
				if (info.Skip)
				{
					continue;
				}
				try
				{
					long t0 = watchAll.ElapsedTicks;
					CollectCached(col, info.Flags, job, regionBounds, rkey);
					long took = watchAll.ElapsedTicks - t0;
					if (took > slowestTicks)
					{
						slowestTicks = took;
						slowest = col;
					}
				}
				catch (Exception e)
				{
					Log.Debug($"collision: {col.name}: {e.Message}");
				}
			}
			worker.Enqueue(job);
		}

		// Diagnostics: the slowest collider of a slow region, said once in a while.
		private readonly System.Diagnostics.Stopwatch watchAll = System.Diagnostics.Stopwatch.StartNew();
		private long slowestTicks;
		private Collider slowest;
		private float nextSlowReport;

		/// <summary>
		/// What one collider gives a region, kept as long as the collider doesn't move: a region seen
		/// again (walking back, a refresh) costs nothing for its static geometry.
		/// </summary>
		private sealed class Piece
		{
			public Matrix4x4 Matrix;
			public Tri[] Tris;
			public Obb[] Boxes;
			public Capsule[] Capsules;
		}

		private readonly Dictionary<(int, long), Piece> pieces = new Dictionary<(int, long), Piece>();

		private void CollectCached(Collider col, uint flags, CollisionJob job, Bounds region, long rkey)
		{
			var key = (col.GetInstanceID(), rkey);
			var m = col.transform.localToWorldMatrix;
			if (pieces.TryGetValue(key, out var piece) && piece.Matrix == m)
			{
				job.Tris.AddRange(piece.Tris);
				job.Boxes.AddRange(piece.Boxes);
				job.Capsules.AddRange(piece.Capsules);
				return;
			}
			int t0 = job.Tris.Count, b0 = job.Boxes.Count, c0 = job.Capsules.Count;
			Collect(col, flags, job, region);
			if (pieces.Count > 50000)
			{
				pieces.Clear();
			}
			pieces[key] = new Piece
			{
				Matrix = m,
				Tris = job.Tris.GetRange(t0, job.Tris.Count - t0).ToArray(),
				Boxes = job.Boxes.GetRange(b0, job.Boxes.Count - b0).ToArray(),
				Capsules = job.Capsules.GetRange(c0, job.Capsules.Count - c0).ToArray(),
			};
		}

		/// <summary>
		/// What a surface is made of, from the game's own footstep tags: Minecraft plays that block's
		/// footsteps on it (HostFootsteps).
		/// </summary>
		private static byte SurfaceMaterial(Collider col)
		{
			switch (col.tag)
			{
				case "Grass":
				case "Bush": return Proto.DigGrass;
				case "Gravel": return Proto.DigGravel;
				case "Snow": return Proto.DigSnow;
				case "Wood": return Proto.DigPlanks;
				case "Tree": return Proto.DigOakLog;
				case "Carpet": return Proto.DigCloth;
				case "Puddle":
				case "Slime": return Proto.DigMud;
				case "Metal":
				case "Aluminum":
				case "Catwalk": return Proto.DigMetal;
				case "Concrete":
				case "Tiles":
				case "Rock": return Proto.DigStone;
				default: return col is TerrainCollider ? Proto.DigDirt : Proto.DigStone;
			}
		}

		/// <summary>The oriented box's world axis-aligned bounds.</summary>
		private static Bounds WorldAabb(Vector3 centre, Vector3 half, Quaternion rot)
		{
			var b = new Bounds(centre, Vector3.zero);
			for (int i = 0; i < 8; i++)
			{
				b.Encapsulate(centre + rot * new Vector3((i & 1) != 0 ? half.x : -half.x, (i & 2) != 0 ? half.y : -half.y, (i & 4) != 0 ? half.z : -half.z));
			}
			return b;
		}

		/// <summary>The layers the player's CharacterController collides with, from the game's own collision matrix.</summary>
		private static int PlayerMask()
		{
			var p = Lc.Game.Player;
			int playerLayer = p != null ? p.gameObject.layer : 3;
			int m = 0;
			for (int i = 0; i < 32; i++)
			{
				if (!Physics.GetIgnoreLayerCollision(playerLayer, i))
				{
					m |= 1 << i;
				}
			}
			return m;
		}

		private int Mask => mask != 0 ? mask : (mask = PlayerMask());

		/// <summary>What a collider is to Minecraft: static ship geometry the player collides with, or nothing.</summary>
		private Info InfoFor(Collider col)
		{
			if (infos.TryGetValue(col, out Info info))
			{
				return info;
			}
			info = new Info();
			var frame = Coords.Frame;
			int layer = col.gameObject.layer;
			// Kinematic bodies are the level's moving parts (the ship's hangar door, facility doors): they
			// stay, and are sent again when they move. Physics bodies (vehicles, ragdolls) are pushed instead.
			bool physicsBody = col.attachedRigidbody != null && !col.attachedRigidbody.isKinematic;
			if (physicsBody && frame != null && col.transform.IsChildOf(frame))
			{
				physicsBody = false; // riding a vehicle (a platform frame): its body is the ground, and it doesn't move in its own frame
			}
			if (col.isTrigger || col is CharacterController || physicsBody || layer == 3 /* Player */ || layer == 19 /* Enemies */
				|| layer == 20 /* PlayerRagdoll */ || layer == 23 /* EnemiesNotRendered */
				|| col.GetComponentInParent<LethalCraftOwned>() != null
				|| col.GetComponentInParent<GameNetcodeStuff.PlayerControllerB>() != null
				|| col.GetComponentInParent<GrabbableObject>() != null
				|| (frame != null && !col.transform.IsChildOf(frame)))
			{
				info.Skip = true; // other players, scrap, enemies, our own blocks, and (in the ship's frame) anything that isn't the ship
			}
			else
			{
				info.Flags = (uint)SurfaceMaterial(col) << Proto.TriMaterialShift;
			}
			infos[col] = info;
			return info;
		}

		private void Collect(Collider col, uint flags, CollisionJob job, Bounds region)
		{
			switch (col)
			{
				case BoxCollider box:
					AddBox(box, flags, job);
					break;
				case CapsuleCollider cap:
					AddCapsule(cap, flags, job);
					break;
				case SphereCollider sph:
					AddSphere(sph, flags, job);
					break;
				case MeshCollider mc when mc.sharedMesh != null:
					AddMesh(mc, flags, job, region);
					break;
				case TerrainCollider terrain:
					AddTerrain(terrain, flags, job, region);
					break;
				default:
					if (col.GetType().Name != "WheelCollider")
					{
						var bb = col.bounds;
						AddObb(bb.center, Vector3.right, Vector3.up, Vector3.forward, bb.extents, flags, job);
					}
					break;
			}
		}

		private static void AddBox(BoxCollider box, uint flags, CollisionJob job)
		{
			var t = box.transform;
			Vector3 c = t.TransformPoint(box.center);
			Vector3 s = Vector3.Scale(box.size, Abs(t.lossyScale)) * 0.5f;
			if ((flags & Proto.TriTerrain) != 0)
			{
				// The land: only its top surface (Minecraft's "below it is solid" takes care of the rest),
				// plus the box itself as voxels for everything else.
				AddObb(c, t.right, t.up, t.forward, s, flags & ~Proto.TriTerrain, job, shellOnly: false, noShell: true);
				Vector3 up = Vector3.up;
				// The face whose normal points most nearly up.
				Vector3[] axes = { t.right, t.up, t.forward };
				float[] ext = { s.x, s.y, s.z };
				int best = 0;
				float bestDot = 0f;
				for (int i = 0; i < 3; i++)
				{
					float d = Vector3.Dot(axes[i], up);
					if (Mathf.Abs(d) > Mathf.Abs(bestDot))
					{
						bestDot = d;
						best = i;
					}
				}
				Vector3 n = axes[best] * Mathf.Sign(bestDot);
				Vector3 u = axes[(best + 1) % 3] * ext[(best + 1) % 3], v = axes[(best + 2) % 3] * ext[(best + 2) % 3];
				Vector3 top = c + n * ext[best];
				AddQuad(top - u - v, top + u - v, top + u + v, top - u + v, n, flags, job);
				return;
			}
			AddObb(c, t.right, t.up, t.forward, s, flags, job);
		}

		private static void AddObb(Vector3 c, Vector3 ax, Vector3 ay, Vector3 az, Vector3 half, uint flags, CollisionJob job, bool shellOnly = false, bool noShell = false)
		{
			float k = Coords.K;
			Vector3 mc = Coords.ToMcF(c);
			Vector3 x = Coords.DirToMc(ax.normalized), y = Coords.DirToMc(ay.normalized), z = Coords.DirToMc(az.normalized);
			if (noShell)
			{
				// Voxels only (CollisionWorker fills boxes; its triangles come from Triangulate, skipped
				// here by passing it as a capsule-free fill): add as a box with a flag the worker reads.
				job.Boxes.Add(new Obb
				{
					Cx = mc.x, Cy = mc.y, Cz = mc.z, A0x = x.x, A0y = x.y, A0z = x.z, A1x = y.x, A1y = y.y, A1z = y.z, A2x = z.x, A2y = z.y, A2z = z.z,
					H0 = half.x * k, H1 = half.y * k, H2 = half.z * k, Flags = flags | CollisionWorker.NoShell,
				});
				return;
			}
			job.Boxes.Add(new Obb
			{
				Cx = mc.x, Cy = mc.y, Cz = mc.z, A0x = x.x, A0y = x.y, A0z = x.z, A1x = y.x, A1y = y.y, A1z = y.z, A2x = z.x, A2y = z.y, A2z = z.z,
				H0 = half.x * k, H1 = half.y * k, H2 = half.z * k, Flags = flags,
			});
		}

		private static void AddQuad(Vector3 a, Vector3 b, Vector3 c, Vector3 d, Vector3 outward, uint flags, CollisionJob job)
		{
			AddTri(a, b, c, outward, flags, job);
			AddTri(a, c, d, outward, flags, job);
		}

		/// <summary>A Unity-space triangle, wound so its Minecraft normal faces <paramref name="outward"/> (Unity).</summary>
		private static void AddTri(Vector3 a, Vector3 b, Vector3 c, Vector3 outward, uint flags, CollisionJob job)
		{
			Vector3 ma = Coords.ToMcF(a), mb = Coords.ToMcF(b), mcc = Coords.ToMcF(c);
			Vector3 n = Vector3.Cross(mb - ma, mcc - ma);
			if (Vector3.Dot(n, Coords.DirToMc(outward)) < 0f)
			{
				(mb, mcc) = (mcc, mb);
			}
			job.Tris.Add(new Tri(ma.x, ma.y, ma.z, mb.x, mb.y, mb.z, mcc.x, mcc.y, mcc.z, flags));
		}

		private static void AddCapsule(CapsuleCollider cap, uint flags, CollisionJob job)
		{
			var t = cap.transform;
			Vector3 ls = Abs(t.lossyScale);
			int dir = cap.direction;
			float axisScale = dir == 0 ? ls.x : dir == 1 ? ls.y : ls.z;
			float radiusScale = dir == 0 ? Mathf.Max(ls.y, ls.z) : dir == 1 ? Mathf.Max(ls.x, ls.z) : Mathf.Max(ls.x, ls.y);
			float r = cap.radius * radiusScale;
			float h = Mathf.Max(cap.height * axisScale, 2f * r);
			Vector3 axis = dir == 0 ? t.right : dir == 1 ? t.up : t.forward;
			Vector3 c = t.TransformPoint(cap.center);
			Vector3 pa = c - axis * (h * 0.5f - r), pb = c + axis * (h * 0.5f - r);
			AddRoundShell(pa, pb, r, flags, job);
		}

		private static void AddSphere(SphereCollider sph, uint flags, CollisionJob job)
		{
			var t = sph.transform;
			Vector3 ls = Abs(t.lossyScale);
			float r = sph.radius * Mathf.Max(ls.x, Mathf.Max(ls.y, ls.z));
			Vector3 c = t.TransformPoint(sph.center);
			AddRoundShell(c, c, r, flags, job);
		}

		/// <summary>A capsule (or sphere, a == b): its surface as triangles, its inside as voxels.</summary>
		private static void AddRoundShell(Vector3 a, Vector3 b, float r, uint flags, CollisionJob job)
		{
			float k = Coords.K;
			Vector3 ma = Coords.ToMcF(a), mb = Coords.ToMcF(b);
			job.Capsules.Add(new Capsule { Ax = ma.x, Ay = ma.y, Az = ma.z, Bx = mb.x, By = mb.y, Bz = mb.z, R = r * k, Flags = flags | CollisionWorker.NoShell });
			Vector3 axis = b - a;
			float len = axis.magnitude;
			Vector3 up = len > 1e-4f ? axis / len : Vector3.up;
			Vector3 side = Vector3.Cross(up, Mathf.Abs(up.y) < 0.9f ? Vector3.up : Vector3.right).normalized;
			Vector3 fwd = Vector3.Cross(side, up);
			const int seg = 10, rings = 4;
			// Rings from the bottom pole to the top pole: lower hemisphere around a, upper around b.
			var ringPts = new List<Vector3[]>();
			for (int i = 0; i <= rings * 2; i++)
			{
				float phi = Mathf.PI * i / (rings * 2) - Mathf.PI / 2; // -90..90
				Vector3 centre = i <= rings ? a : b;
				var pts = new Vector3[seg];
				for (int j = 0; j < seg; j++)
				{
					float th = 2 * Mathf.PI * j / seg;
					Vector3 d = (side * Mathf.Cos(th) + fwd * Mathf.Sin(th)) * Mathf.Cos(phi) + up * Mathf.Sin(phi);
					pts[j] = centre + d * r;
				}
				ringPts.Add(pts);
				if (i == rings && len > 1e-4f)
				{
					// The cylinder between the two hemispheres.
					var top = new Vector3[seg];
					for (int j = 0; j < seg; j++)
					{
						top[j] = pts[j] + axis;
					}
					ringPts.Add(top);
				}
			}
			Vector3 mid = (a + b) * 0.5f;
			for (int i = 0; i + 1 < ringPts.Count; i++)
			{
				var lo = ringPts[i];
				var hi = ringPts[i + 1];
				for (int j = 0; j < seg; j++)
				{
					int jn = (j + 1) % seg;
					Vector3 o = ((lo[j] + hi[jn]) * 0.5f - mid).normalized;
					AddTri(lo[j], lo[jn], hi[jn], o, flags, job);
					AddTri(lo[j], hi[jn], hi[j], o, flags, job);
				}
			}
		}

		private void AddMesh(MeshCollider mc, uint flags, CollisionJob job, Bounds region)
		{
			Mesh mesh = mc.sharedMesh;
			if (!meshCache.TryGetValue(mesh, out var data))
			{
				if (mesh.isReadable && !unreadable.Contains(mesh))
				{
					data = (mesh.vertices, mesh.triangles);
					meshCache[mesh] = data;
				}
				else
				{
					if (unreadable.Add(mesh))
					{
						Log.Debug($"collision: mesh {mesh.name} isn't readable; probing it with rays");
					}
					Probe(mc, flags, job, region);
					return;
				}
			}
			var index = IndexFor(mc, data.v, data.t);
			index.Query(region, queryScratch);
			var wv = index.World;
			var tris = data.t;
			foreach (int i in queryScratch)
			{
				Vector3 a = wv[tris[i]], b = wv[tris[i + 1]], c = wv[tris[i + 2]];
				var tb = new Bounds(a, Vector3.zero);
				tb.Encapsulate(b);
				tb.Encapsulate(c);
				if (!tb.Intersects(region))
				{
					continue;
				}
				// Unity's winding is clockwise from the front: the face normal is (b - a) x (c - a) reversed
				// in Minecraft's mirrored space, which AddTri sorts out from the Unity normal.
				Vector3 n = Vector3.Cross(b - a, c - a);
				if (n.sqrMagnitude < 1e-12f)
				{
					continue;
				}
				AddTri(a, b, c, n, flags, job);
			}
		}

		private readonly Dictionary<MeshCollider, MeshIndex> meshIndices = new Dictionary<MeshCollider, MeshIndex>();
		private readonly List<int> queryScratch = new List<int>();

		/// <summary>The collider's triangles in world space, bucketed by region-sized cells (rebuilt if it moved).</summary>
		private MeshIndex IndexFor(MeshCollider mc, Vector3[] v, int[] tris)
		{
			var m = mc.transform.localToWorldMatrix;
			if (meshIndices.TryGetValue(mc, out var index) && index.Matrix == m)
			{
				return index;
			}
			index = new MeshIndex(v, tris, m, CollisionWorker.RegionSize / Coords.K);
			meshIndices[mc] = index;
			return index;
		}

		/// <summary>
		/// A big mesh (a moon's ground and rocks can be one mesh of tens of thousands of triangles) is
		/// gone through once, not once per region: each region only looks at the triangles in its cells.
		/// </summary>
		private sealed class MeshIndex
		{
			public readonly Matrix4x4 Matrix;
			public readonly Vector3[] World;
			private readonly float cell;
			private readonly Dictionary<long, List<int>> cells = new Dictionary<long, List<int>>();
			private readonly int[] seen;
			private int stamp;

			public MeshIndex(Vector3[] v, int[] tris, Matrix4x4 m, float cellSize)
			{
				Matrix = m;
				cell = Mathf.Max(cellSize, 0.5f);
				World = new Vector3[v.Length];
				for (int i = 0; i < v.Length; i++)
				{
					World[i] = m.MultiplyPoint3x4(v[i]);
				}
				seen = new int[tris.Length / 3 + 1];
				for (int i = 0; i + 2 < tris.Length; i += 3)
				{
					Vector3 a = World[tris[i]], b = World[tris[i + 1]], c = World[tris[i + 2]];
					Vector3 lo = Vector3.Min(a, Vector3.Min(b, c)), hi = Vector3.Max(a, Vector3.Max(b, c));
					int x0 = Cell(lo.x), y0 = Cell(lo.y), z0 = Cell(lo.z), x1 = Cell(hi.x), y1 = Cell(hi.y), z1 = Cell(hi.z);
					for (int x = x0; x <= x1; x++)
					{
						for (int y = y0; y <= y1; y++)
						{
							for (int z = z0; z <= z1; z++)
							{
								long key = Key(x, y, z);
								if (!cells.TryGetValue(key, out var list))
								{
									list = new List<int>();
									cells[key] = list;
								}
								list.Add(i);
							}
						}
					}
				}
			}

			private int Cell(float f) => Mathf.FloorToInt(f / cell);

			private static long Key(int x, int y, int z) => ((long)(x & 0x1FFFFF) << 42) | ((long)(y & 0x1FFFFF) << 21) | (long)(z & 0x1FFFFF);

			/// <summary>The first index of every triangle in a cell the bounds touch, each once.</summary>
			public void Query(Bounds b, List<int> output)
			{
				output.Clear();
				stamp++;
				int x0 = Cell(b.min.x), y0 = Cell(b.min.y), z0 = Cell(b.min.z), x1 = Cell(b.max.x), y1 = Cell(b.max.y), z1 = Cell(b.max.z);
				for (int x = x0; x <= x1; x++)
				{
					for (int y = y0; y <= y1; y++)
					{
						for (int z = z0; z <= z1; z++)
						{
							if (!cells.TryGetValue(Key(x, y, z), out var list))
							{
								continue;
							}
							foreach (int t in list)
							{
								if (seen[t / 3] != stamp)
								{
									seen[t / 3] = stamp;
									output.Add(t);
								}
							}
						}
					}
				}
			}
		}

		/// <summary>
		/// A collider whose shape can't be read (an unreadable mesh, or terrain): its walkable surfaces as a height field, ray cast
		/// straight down onto it on a quarter-block grid (stairs and ramps, which is what Lethal Company's mesh
		/// colliders mostly are), and its sides from horizontal rays as voxels.
		/// </summary>
		private static void Probe(Collider mc, uint flags, CollisionJob job, Bounds region)
		{
			var b = mc.bounds;
			if (!b.Intersects(region))
			{
				return;
			}
			var area = new Bounds();
			area.SetMinMax(Vector3.Max(b.min, region.min), Vector3.Min(b.max, region.max));
			// Big meshes (a moon's rocks and outer ground) at half a block; small ones (stairs, ramps) finer.
			bool big = b.size.x > 40f || b.size.z > 40f;
			float step = (big ? 0.5f : 0.25f) / Coords.K;
			// A room-sized mesh can have floors under its roof (a facility room: ceiling, then floor; a
			// two-storey building): each ray goes on down through them. A moon's ground mesh doesn't.
			bool layered = !big;
			int nx = Mathf.Clamp(Mathf.CeilToInt(area.size.x / step) + 1, 1, 64), nz = Mathf.Clamp(Mathf.CeilToInt(area.size.z / step) + 1, 1, 64);
			var h = new float[nx, nz];
			var hit = new bool[nx, nz];
			float top = b.max.y + 0.1f, depth = b.size.y + 0.2f;
			for (int i = 0; i < nx; i++)
			{
				for (int j = 0; j < nz; j++)
				{
					var origin = new Vector3(area.min.x + i * step, top, area.min.z + j * step);
					if (mc.Raycast(new Ray(origin, Vector3.down), out RaycastHit rh, depth))
					{
						h[i, j] = rh.point.y;
						hit[i, j] = true;
						if (layered)
						{
							FloorsBelow(mc, rh.point, origin.y - depth, step, flags, job, region);
						}
					}
				}
			}
			float maxStep = 0.6f / Coords.K;
			for (int i = 0; i + 1 < nx; i++)
			{
				for (int j = 0; j + 1 < nz; j++)
				{
					if (!hit[i, j] || !hit[i + 1, j] || !hit[i, j + 1] || !hit[i + 1, j + 1])
					{
						continue;
					}
					float lo = Mathf.Min(Mathf.Min(h[i, j], h[i + 1, j]), Mathf.Min(h[i, j + 1], h[i + 1, j + 1]));
					float hi = Mathf.Max(Mathf.Max(h[i, j], h[i + 1, j]), Mathf.Max(h[i, j + 1], h[i + 1, j + 1]));
					var p00 = new Vector3(area.min.x + i * step, h[i, j], area.min.z + j * step);
					var p10 = new Vector3(area.min.x + (i + 1) * step, h[i + 1, j], area.min.z + j * step);
					var p01 = new Vector3(area.min.x + i * step, h[i, j + 1], area.min.z + (j + 1) * step);
					var p11 = new Vector3(area.min.x + (i + 1) * step, h[i + 1, j + 1], area.min.z + (j + 1) * step);
					if (hi - lo > maxStep)
					{
						// A step: a flat top at the highest corner's height, and the riser as a wall.
						float y = hi;
						p00.y = p10.y = p01.y = p11.y = y;
						Vector3 c = (p00 + p11) * 0.5f;
						AddObb(new Vector3(c.x, (y + lo) * 0.5f, c.z), Vector3.right, Vector3.up, Vector3.forward,
							new Vector3(step * 0.5f, (y - lo) * 0.5f, step * 0.5f), flags & ~Proto.TriTerrain, job);
						continue;
					}
					AddTri(p00, p10, p11, Vector3.up, flags & ~Proto.TriTerrain, job);
					AddTri(p00, p11, p01, Vector3.up, flags & ~Proto.TriTerrain, job);
				}
			}
		}

		/// <summary>
		/// Under the top surface a ray found, the floors further down the same mesh: a ray started just
		/// below a surface passes out through the solid's underside (ray casts don't hit back faces) and
		/// on to the next floor's top. Each walkable one becomes a flat tile a grid step wide.
		/// </summary>
		private static void FloorsBelow(Collider mc, Vector3 surface, float bottom, float step, uint flags, CollisionJob job, Bounds region)
		{
			Vector3 from = surface + Vector3.down * 0.05f;
			for (int layer = 0; layer < 3; layer++)
			{
				float left = from.y - bottom;
				if (left < 0.3f || from.y < region.min.y)
				{
					return;
				}
				if (!mc.Raycast(new Ray(from, Vector3.down), out RaycastHit rh, left))
				{
					return;
				}
				if (rh.normal.y > 0.6f && rh.point.y <= region.max.y && rh.point.y >= region.min.y)
				{
					float half = step * 0.5f;
					var p = rh.point;
					var a = new Vector3(p.x - half, p.y, p.z - half);
					var c = new Vector3(p.x + half, p.y, p.z + half);
					var bb = new Vector3(p.x + half, p.y, p.z - half);
					var d = new Vector3(p.x - half, p.y, p.z + half);
					AddTri(a, bb, c, Vector3.up, flags & ~Proto.TriTerrain, job);
					AddTri(a, c, d, Vector3.up, flags & ~Proto.TriTerrain, job);
				}
				from = rh.point + Vector3.down * 0.05f;
			}
		}

		/// <summary>
		/// A moon's terrain: its height field, read straight from the terrain data on a half-block grid
		/// (no ray casts), as triangles. Steep parts come out as steep triangles, which Minecraft treats
		/// as walls.
		/// </summary>
		private static void AddTerrain(TerrainCollider tc, uint flags, CollisionJob job, Bounds region)
		{
			var terrain = tc.GetComponent<Terrain>();
			if (terrain == null)
			{
				Probe(tc, flags, job, region);
				return;
			}
			var b = tc.bounds;
			if (!b.Intersects(region))
			{
				return;
			}
			float x0 = Mathf.Max(b.min.x, region.min.x), x1 = Mathf.Min(b.max.x, region.max.x);
			float z0 = Mathf.Max(b.min.z, region.min.z), z1 = Mathf.Min(b.max.z, region.max.z);
			float step = 0.5f / Coords.K;
			int nx = Mathf.Clamp(Mathf.CeilToInt((x1 - x0) / step) + 1, 2, 64), nz = Mathf.Clamp(Mathf.CeilToInt((z1 - z0) / step) + 1, 2, 64);
			float baseY = terrain.transform.position.y;
			var h = new float[nx, nz];
			for (int i = 0; i < nx; i++)
			{
				for (int j = 0; j < nz; j++)
				{
					h[i, j] = baseY + terrain.SampleHeight(new Vector3(x0 + i * step, 0f, z0 + j * step));
				}
			}
			for (int i = 0; i + 1 < nx; i++)
			{
				for (int j = 0; j + 1 < nz; j++)
				{
					float lo = Mathf.Min(Mathf.Min(h[i, j], h[i + 1, j]), Mathf.Min(h[i, j + 1], h[i + 1, j + 1]));
					float hi = Mathf.Max(Mathf.Max(h[i, j], h[i + 1, j]), Mathf.Max(h[i, j + 1], h[i + 1, j + 1]));
					if (hi < region.min.y || lo > region.max.y)
					{
						continue;
					}
					var p00 = new Vector3(x0 + i * step, h[i, j], z0 + j * step);
					var p10 = new Vector3(x0 + (i + 1) * step, h[i + 1, j], z0 + j * step);
					var p01 = new Vector3(x0 + i * step, h[i, j + 1], z0 + (j + 1) * step);
					var p11 = new Vector3(x0 + (i + 1) * step, h[i + 1, j + 1], z0 + (j + 1) * step);
					AddTri(p00, p10, p11, Vector3.up, flags, job);
					AddTri(p00, p11, p01, Vector3.up, flags, job);
				}
			}
		}

		private static Vector3 Abs(Vector3 v) => new Vector3(Mathf.Abs(v.x), Mathf.Abs(v.y), Mathf.Abs(v.z));
	}

	/// <summary>Marks GameObjects LethalCraft made (Minecraft's own block colliders): never sent back to Minecraft as Lethal Company geometry.</summary>
	internal sealed class LethalCraftOwned : MonoBehaviour
	{
	}
}
