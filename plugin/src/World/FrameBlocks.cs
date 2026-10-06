using LethalCraft.Link;
using UnityEngine;

namespace LethalCraft.World
{
	/// <summary>
	/// Moving the blocks built on something that moves (the ship, an elevator, a vehicle) between its
	/// own stretch of Minecraft's world, where they ride with it, and the moon's, where they stand
	/// while it's still. The frame's stretch holds its own space (Unity metres scaled to blocks, Z
	/// mirrored) from x = frameOffset; the moon's holds Unity's world space from x = moonOffset. pose
	/// is the frame's local-to-world matrix as it stands (or stood) in the moon. Minecraft does the
	/// moving (ShipBlocks); this works out the box, the quarter turns and the offset (Minecraft blocks
	/// only turn in quarters: the nearest one is used).
	/// </summary>
	internal static class FrameBlocks
	{
		/// <summary>The frame's box (its own space, metres) from its stretch into the moon's, where pose stands it.</summary>
		public static void ToMoon(Matrix4x4 pose, double frameOffset, double moonOffset, Vector3 lo, Vector3 hi, int clearSlot, bool clearFirst, string why)
		{
			Move(Corners(lo, hi, frameOffset), v => FrameToMoon(v, pose, frameOffset, moonOffset), why, clearSlot, clearFirst);
		}

		/// <summary>The same box, from where pose stands it in the moon's stretch, back into the frame's.</summary>
		public static void FromMoon(Matrix4x4 pose, double frameOffset, double moonOffset, Vector3 lo, Vector3 hi, int clearSlot, bool clearFirst, string why)
		{
			var corners = Corners(lo, hi, frameOffset);
			for (int i = 0; i < corners.Length; i++)
			{
				corners[i] = FrameToMoon(corners[i], pose, frameOffset, moonOffset);
			}
			var inverse = pose.inverse;
			Move(corners, v => MoonToFrame(v, inverse, frameOffset, moonOffset), why, clearSlot, clearFirst);
		}

		private static Vector3[] Corners(Vector3 lo, Vector3 hi, double frameOffset)
		{
			float k = Coords.K;
			var c = new Vector3[8];
			for (int i = 0; i < 8; i++)
			{
				var l = new Vector3((i & 1) != 0 ? hi.x : lo.x, (i & 2) != 0 ? hi.y : lo.y, (i & 4) != 0 ? hi.z : lo.z);
				c[i] = new Vector3((float)(l.x * k + frameOffset), l.y * k, -l.z * k);
			}
			return c;
		}

		private static Vector3 FrameToMoon(Vector3 frameMc, Matrix4x4 pose, double frameOffset, double moonOffset)
		{
			float k = Coords.K;
			var world = pose.MultiplyPoint3x4(new Vector3((float)((frameMc.x - frameOffset) / k), frameMc.y / k, -frameMc.z / k));
			return new Vector3((float)(world.x * k + moonOffset), world.y * k, -world.z * k);
		}

		private static Vector3 MoonToFrame(Vector3 moonMc, Matrix4x4 inverse, double frameOffset, double moonOffset)
		{
			float k = Coords.K;
			var world = new Vector3((float)((moonMc.x - moonOffset) / k), moonMc.y / k, -moonMc.z / k);
			var l = inverse.MultiplyPoint3x4(world);
			return new Vector3((float)(l.x * k + frameOffset), l.y * k, -l.z * k);
		}

		/// <summary>Every block in the source box (corners, source Minecraft coords) to where map puts it; clearSlot (0: none) cleared before or after.</summary>
		private static void Move(Vector3[] corners, System.Func<Vector3, Vector3> map, string why, int clearSlot, bool clearFirst)
		{
			var lo = Vector3.one * float.MaxValue;
			var hi = Vector3.one * float.MinValue;
			foreach (var c in corners)
			{
				lo = Vector3.Min(lo, c);
				hi = Vector3.Max(hi, c);
			}
			var min = Vector3Int.FloorToInt(lo);
			var max = Vector3Int.FloorToInt(hi);
			// The turn: where map sends east (+x), to the nearest quarter (1: east -> south, Minecraft's clockwise).
			var from = new Vector3(min.x + 0.5f, min.y + 0.5f, min.z + 0.5f);
			Vector3 east = map(from + Vector3.right) - map(from);
			int q = ((int)Mathf.Round(Mathf.Atan2(east.z, east.x) * Mathf.Rad2Deg / 90f) % 4 + 4) % 4;
			// The offset: where the box's first block lands, less where the turn alone puts it.
			var to = Vector3Int.FloorToInt(map(from));
			var offset = to - Turn(min, q);
			SharedLink.Instance.RequestBlockMove(min, max, q, offset, clearSlot, clearFirst);
			Log.Info($"{why}: its blocks move with it (box {min} .. {max}, {q} quarter turns, offset {offset})"
				+ (clearSlot > 0 ? $"; moon slot {clearSlot} cleared {(clearFirst ? "first" : "after")}" : ""));
		}

		/// <summary>Quarter turns as Minecraft's ShipBlocks.rotate does them.</summary>
		private static Vector3Int Turn(Vector3Int p, int q)
		{
			switch (q)
			{
				case 1: return new Vector3Int(-p.z, p.y, p.x);
				case 2: return new Vector3Int(-p.x, p.y, -p.z);
				case 3: return new Vector3Int(p.z, p.y, -p.x);
				default: return p;
			}
		}
	}
}
