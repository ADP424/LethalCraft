using System.Collections.Generic;
using LethalCraft.Link;
using UnityEngine;

namespace LethalCraft.World
{
	/// <summary>
	/// The game's water (lakes, rivers, the flood, water inside the facility: its QuicksandTrigger
	/// volumes marked as water) as Minecraft water. Minecraft gets the water surface over the block
	/// columns around the player (Proto's water grid; dev.lethalcraft.world.HostWater), and swims,
	/// floats, runs out of air and drowns there by its own rules. The game's own drowning is off while
	/// Minecraft has the player's health (Patches.CombatPatches).
	///
	/// The grid has no bottom, so only water the player is near in height counts: a lake doesn't
	/// flood the facility under it.
	/// </summary>
	internal sealed class WaterSurface
	{
		public static readonly WaterSurface Instance = new WaterSurface();

		private const float FindEvery = 5f;
		private const float HeightMargin = 4f; // metres above / below a water volume where it counts
		private readonly List<Collider> water = new List<Collider>();
		private readonly float[] surface = new float[Proto.WaterGridSize * Proto.WaterGridSize];
		private float findTimer;

		public int Volumes => water.Count;

		/// <summary>The surface over the grid at originX/originZ (Minecraft blocks); NoWater where there is none.</summary>
		public float[] Fill(bool session, Vector3 player, int originX, int originZ, float dt)
		{
			for (int i = 0; i < surface.Length; i++)
			{
				surface[i] = Proto.NoWater;
			}
			if (!session)
			{
				water.Clear();
				findTimer = 0f;
				return surface;
			}
			findTimer -= dt;
			if (findTimer <= 0f)
			{
				findTimer = FindEvery;
				water.Clear();
				foreach (var q in Object.FindObjectsOfType<QuicksandTrigger>())
				{
					var c = q.isWater ? q.GetComponent<Collider>() : null;
					if (c != null)
					{
						water.Add(c);
					}
				}
			}
			foreach (var c in water)
			{
				if (c == null || !c.enabled || !c.gameObject.activeInHierarchy)
				{
					continue;
				}
				var b = c.bounds;
				if (player.y < b.min.y - HeightMargin || player.y > b.max.y + HeightMargin)
				{
					continue;
				}
				for (int dz = 0; dz < Proto.WaterGridSize; dz++)
				{
					for (int dx = 0; dx < Proto.WaterGridSize; dx++)
					{
						Vector3 column = Coords.ToUnity(originX + dx + 0.5, 0.0, originZ + dz + 0.5);
						if (column.x < b.min.x || column.x > b.max.x || column.z < b.min.z || column.z > b.max.z)
						{
							continue;
						}
						var ray = new Ray(new Vector3(column.x, b.max.y + 1f, column.z), Vector3.down);
						if (!c.Raycast(ray, out RaycastHit hit, b.size.y + 2f))
						{
							continue;
						}
						float y = Coords.ToMcF(hit.point).y;
						int i = dz * Proto.WaterGridSize + dx;
						if (y > surface[i])
						{
							surface[i] = y;
						}
					}
				}
			}
			return surface;
		}
	}
}
