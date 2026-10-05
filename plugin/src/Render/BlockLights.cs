using System.Collections.Generic;
using LethalCraft.Link;
using UnityEngine;
using UnityEngine.Rendering.HighDefinition;

namespace LethalCraft.Render
{
	/// <summary>
	/// Minecraft's light-emitting blocks (torches, lanterns, lava, glowstone...) light Lethal Company's
	/// level: a pool of HDRP point lights goes to the brightest emitters near the player (merged per
	/// small cell so a wall of torches doesn't take every light). The lights are children of the
	/// world root, so on the ship they ride with it. Also answers "what does this block do to whoever
	/// stands in it" (lava, fire, magma).
	///
	/// Brightness is HDRP's physical units (lumens); first-draft values, tuned in game.
	/// </summary>
	internal sealed unsafe class BlockLights
	{
		public static readonly BlockLights Instance = new BlockLights();

		private const int CellBlocks = 3;
		private const int MaxLights = 16;
		private const int ShadowLights = 2;
		private const float RangeBlocks = 48f;
		private const float UpdateSeconds = 0.25f;
		// A torch (level 14): about a bright bulb's worth, so it reads against the facility's lamps.
		private const float LumensAtFull = 900f;

		private struct Emitter
		{
			public int X, Y, Z;
			public byte Level;
			public uint Color; // RGB8, top byte: LightKind bits 0-3, BlockHazard bits 4-7
		}

		private readonly Dictionary<long, Emitter[]> sections = new Dictionary<long, Emitter[]>();
		private readonly Dictionary<long, byte> hazards = new Dictionary<long, byte>();
		private readonly HDAdditionalLightData[] pool = new HDAdditionalLightData[MaxLights];
		private readonly float[] baseLumens = new float[MaxLights];
		private readonly uint[] kind = new uint[MaxLights];
		private float timer;
		private float clock;

		public int Emitters
		{
			get
			{
				int n = 0;
				foreach (var l in sections.Values)
				{
					n += l.Length;
				}
				return n;
			}
		}

		public int LightsOn
		{
			get
			{
				int n = 0;
				foreach (var l in pool)
				{
					if (l != null && l.gameObject.activeSelf)
					{
						n++;
					}
				}
				return n;
			}
		}

		public void OnLights(byte* data, uint bytes)
		{
			int sx = *(int*)data, sy = *(int*)(data + 4), sz = *(int*)(data + 8);
			uint count = *(uint*)(data + 12);
			long key = World.Clip.Key(sx, sy, sz);
			if (sections.TryGetValue(key, out Emitter[] old))
			{
				foreach (var e in old)
				{
					hazards.Remove(World.Clip.Key(e.X, e.Y, e.Z));
				}
			}
			if (count == 0 || bytes < 16 + count * 8)
			{
				sections.Remove(key);
				timer = 0;
				return;
			}
			var list = new Emitter[count];
			for (int i = 0; i < count; i++)
			{
				byte* l = data + 16 + i * 8;
				list[i] = new Emitter { X = sx * 16 + l[0], Y = sy * 16 + l[1], Z = sz * 16 + l[2], Level = l[3], Color = *(uint*)(l + 4) };
				byte hazard = (byte)((list[i].Color >> 28) & 0xF);
				if (hazard != Proto.HazardNone)
				{
					hazards[World.Clip.Key(list[i].X, list[i].Y, list[i].Z)] = hazard;
				}
			}
			sections[key] = list;
			timer = 0;
		}

		public void Clear()
		{
			sections.Clear();
			hazards.Clear();
			AllOff();
		}

		private void AllOff()
		{
			foreach (var l in pool)
			{
				if (l != null)
				{
					l.gameObject.SetActive(false);
				}
			}
		}

		/// <summary>Proto.Hazard* of the Minecraft block at (x, y, z).</summary>
		public byte HazardAt(int x, int y, int z) => hazards.TryGetValue(World.Clip.Key(x, y, z), out byte h) ? h : (byte)Proto.HazardNone;

		/// <summary>Main thread, every frame. player: Minecraft coords, or null to turn every light off.</summary>
		public void Update(Transform root, Vector3? player, float dt)
		{
			clock += dt;
			if (player == null)
			{
				AllOff();
				return;
			}
			Flicker();
			timer -= dt;
			if (timer > 0)
			{
				return;
			}
			timer = UpdateSeconds;
			Vector3 p = player.Value;

			// Merge emitters per cell: the brightest one's colour, the cell's centre of light.
			var cells = new Dictionary<long, (Vector3 sum, float weight, int level, uint color)>();
			float range2 = RangeBlocks * RangeBlocks;
			foreach (var list in sections.Values)
			{
				foreach (var e in list)
				{
					var c = new Vector3(e.X + 0.5f, e.Y + 0.5f, e.Z + 0.5f);
					if ((c - p).sqrMagnitude > range2 || e.Level < 6)
					{
						continue;
					}
					long key = World.Clip.Key(Mathf.FloorToInt(c.x / CellBlocks), Mathf.FloorToInt(c.y / CellBlocks), Mathf.FloorToInt(c.z / CellBlocks));
					cells.TryGetValue(key, out var acc);
					acc.sum += c * e.Level;
					acc.weight += e.Level;
					if (e.Level > acc.level)
					{
						acc.level = e.Level;
						acc.color = e.Color;
					}
					cells[key] = acc;
				}
			}
			var chosen = new List<(Vector3 pos, int level, uint color, float score)>();
			foreach (var c in cells.Values)
			{
				Vector3 at = c.sum / c.weight;
				chosen.Add((at, c.level, c.color, (at - p).magnitude - c.level));
			}
			chosen.Sort((a, b) => a.score.CompareTo(b.score));
			for (int i = 0; i < MaxLights; i++)
			{
				if (i >= chosen.Count)
				{
					if (pool[i] != null)
					{
						pool[i].gameObject.SetActive(false);
					}
					continue;
				}
				var c = chosen[i];
				var light = pool[i] != null ? pool[i] : pool[i] = NewLight(root, i);
				light.transform.localPosition = BlockRenderer.Local(c.pos.x, c.pos.y, c.pos.z);
				light.SetColor(new Color32((byte)c.color, (byte)(c.color >> 8), (byte)(c.color >> 16), 255));
				// Minecraft light falls off one level per block: a torch (14) reaches about 13 blocks.
				light.SetRange(c.level * 0.9f / Coords.K);
				baseLumens[i] = LumensAtFull * (c.level / 14f) * (c.level / 14f);
				kind[i] = (c.color >> 24) & 0xF;
				light.SetIntensity(baseLumens[i], LightUnit.Lumen);
				light.gameObject.SetActive(true);
			}
		}

		private static HDAdditionalLightData NewLight(Transform root, int i)
		{
			var go = new GameObject($"Minecraft light {i}");
			go.transform.SetParent(root, false);
			var l = go.AddHDLight(HDLightTypeAndShape.Point);
			l.EnableShadows(i < ShadowLights);
			l.affectsVolumetric = true;
			return l;
		}

		/// <summary>Flames flicker, lava glows slowly, steady lights stay put.</summary>
		private void Flicker()
		{
			for (int i = 0; i < MaxLights; i++)
			{
				var l = pool[i];
				if (l == null || !l.gameObject.activeSelf)
				{
					continue;
				}
				float f = 1f;
				if (kind[i] == Proto.LightFlame)
				{
					f = 0.88f + 0.12f * Mathf.PerlinNoise(clock * 6f, i * 3.1f);
				}
				else if (kind[i] == Proto.LightLava)
				{
					f = 0.85f + 0.15f * Mathf.Sin(clock * 1.3f + i);
				}
				l.SetIntensity(baseLumens[i] * f, LightUnit.Lumen);
			}
		}
	}
}
