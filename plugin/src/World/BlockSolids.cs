using System.Collections.Generic;
using UnityEngine;
using UnityEngine.AI;

namespace LethalCraft.World
{
	/// <summary>
	/// Minecraft's solid blocks are solid in Lethal Company too. Per 16^3 section, Minecraft sends
	/// which blocks are solid (WorldExporter, RenSolids); here each run of them (greedy-merged boxes)
	/// becomes a static box collider on the game's Room layer (what its line-of-sight, item-landing
	/// and footstep checks see), with a carving NavMeshObstacle, so the game's monsters, which walk
	/// its navigation mesh rather than physics, path around walls the player builds and can't walk
	/// through them.
	///
	/// The boxes hang off the block root (BlockRenderer), so they ride the ship and moving platforms
	/// with the blocks; they are marked as ours, so Minecraft is never sent its own blocks back as
	/// the game's collision (CollisionExporter).
	/// </summary>
	internal sealed unsafe class BlockSolids
	{
		public static readonly BlockSolids Instance = new BlockSolids();

		/// <summary>The game's "Room" layer: static level geometry.</summary>
		private const int RoomLayer = 8;

		private readonly Dictionary<long, (GameObject go, byte[] bits)> sections = new Dictionary<long, (GameObject, byte[])>();
		private GameObject root;

		public int Sections => sections.Count;

		private Transform Root
		{
			get
			{
				if (root == null)
				{
					root = new GameObject("LethalCraft solids") { layer = RoomLayer };
					root.AddComponent<LethalCraftOwned>();
					root.transform.SetParent(Render.BlockRenderer.Instance.Root.transform, false);
					sections.Clear();
				}
				return root.transform;
			}
		}

		/// <summary>Render message: RenSolids {sx, sy, sz, count} + 512-byte bitset (bit x + 16z + 256y).</summary>
		public void OnSolids(byte* data, uint bytes)
		{
			int sx = *(int*)data, sy = *(int*)(data + 4), sz = *(int*)(data + 8);
			uint count = *(uint*)(data + 12);
			long key = Clip.Key(sx, sy, sz);
			var parent = Root;
			if (sections.TryGetValue(key, out var old))
			{
				if (old.go != null)
				{
					Object.Destroy(old.go);
				}
				sections.Remove(key);
			}
			if (count == 0 || bytes < 16 + 512)
			{
				return;
			}
			var bits = new byte[512];
			for (int i = 0; i < 512; i++)
			{
				bits[i] = data[16 + i];
			}
			var go = new GameObject($"solids {sx} {sy} {sz}") { layer = RoomLayer };
			go.transform.SetParent(parent, false);
			go.transform.localPosition = Render.BlockRenderer.Local(sx * 16.0, sy * 16.0, sz * 16.0);
			float k = Coords.K;
			foreach (var box in Merge(bits))
			{
				// Box in section block coords [x0, x1) etc. -> the root's space (metres, Z mirrored).
				var child = new GameObject("box") { layer = RoomLayer };
				child.transform.SetParent(go.transform, false);
				var size = new Vector3((box.x1 - box.x0) / k, (box.y1 - box.y0) / k, (box.z1 - box.z0) / k);
				child.transform.localPosition = new Vector3((box.x0 + box.x1) * 0.5f / k, (box.y0 + box.y1) * 0.5f / k, -(box.z0 + box.z1) * 0.5f / k);
				var col = child.AddComponent<BoxCollider>();
				col.size = size;
				var obstacle = child.AddComponent<NavMeshObstacle>();
				obstacle.shape = NavMeshObstacleShape.Box;
				obstacle.size = size;
				obstacle.carving = true;
				obstacle.carveOnlyStationary = true; // riding the ship or a lift, it carves again where it stops
			}
			sections[key] = (go, bits);
		}

		public void Clear()
		{
			foreach (var s in sections.Values)
			{
				if (s.go != null)
				{
					Object.Destroy(s.go);
				}
			}
			sections.Clear();
		}

		/// <summary>Greedy boxes over a 16^3 bitset: rows along x, then z, then y.</summary>
		private static List<(int x0, int y0, int z0, int x1, int y1, int z1)> Merge(byte[] bits)
		{
			var left = new bool[4096];
			for (int i = 0; i < 4096; i++)
			{
				left[i] = (bits[i >> 3] & (1 << (i & 7))) != 0;
			}
			bool L(int x, int y, int z) => x < 16 && y < 16 && z < 16 && left[x + 16 * z + 256 * y];
			var boxes = new List<(int, int, int, int, int, int)>();
			for (int y = 0; y < 16; y++)
			{
				for (int z = 0; z < 16; z++)
				{
					for (int x = 0; x < 16; x++)
					{
						if (!L(x, y, z))
						{
							continue;
						}
						int x1 = x;
						while (L(x1 + 1, y, z))
						{
							x1++;
						}
						int z1 = z;
						for (bool grow = true; grow;)
						{
							for (int xx = x; xx <= x1 && grow; xx++)
							{
								grow = L(xx, y, z1 + 1);
							}
							z1 += grow ? 1 : 0;
						}
						int y1 = y;
						for (bool grow = true; grow;)
						{
							for (int zz = z; zz <= z1 && grow; zz++)
							{
								for (int xx = x; xx <= x1 && grow; xx++)
								{
									grow = L(xx, y1 + 1, zz);
								}
							}
							y1 += grow ? 1 : 0;
						}
						for (int yy = y; yy <= y1; yy++)
						{
							for (int zz = z; zz <= z1; zz++)
							{
								for (int xx = x; xx <= x1; xx++)
								{
									left[xx + 16 * zz + 256 * yy] = false;
								}
							}
						}
						boxes.Add((x, y, z, x1 + 1, y1 + 1, z1 + 1));
					}
				}
			}
			return boxes;
		}
	}
}
