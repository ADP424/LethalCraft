using LethalCraft.Link;
using UnityEngine;

namespace LethalCraft.World
{
	/// <summary>
	/// Blocks built on the ship go where it goes. In flight the ship has its own stretch of
	/// Minecraft's world (slot 0, the ship's own space); landed it is part of the moon's (Unity's
	/// world space, the moon's slot). So when it lands, the blocks in and around the ship move from
	/// the ship's stretch to the moon's, where the ship now stands; when it takes off, back again.
	/// Minecraft does the moving (ShipBlocks); this works out the box, the quarter turns and the
	/// offset between the two (Minecraft blocks only turn in quarters: the nearest one is used).
	///
	/// The game makes each moon and its facility anew every day, so what players left there (blocks,
	/// dug holes, dropped items) goes too (MoonBlocks): after the ship's blocks have left it on take
	/// off, and again on landing before they arrive (in case the last visit ended some other way).
	/// </summary>
	internal sealed class ShipBlockMover
	{
		public static readonly ShipBlockMover Instance = new ShipBlockMover();

		private const float Margin = 1.5f; // metres around the ship's bounds: blocks built onto the hull
		private const double SlotBlocks = 4096; // as the Driver's: slot n is centred on x = n * SlotBlocks
		private bool wasLanded;
		private bool haveLandedPose;
		private Matrix4x4 landedPose;      // ship-local -> world, as it stood on the moon
		private double moonOffset;

		/// <summary>Out of a session (the menu, another save): nothing carries over.</summary>
		public void Reset()
		{
			wasLanded = false;
			haveLandedPose = false;
		}

		/// <summary>Every frame in a session: landed is the Driver's ShipLanded.</summary>
		public void Frame(StartOfRound round, bool landed, double slotOffset)
		{
			if (landed == wasLanded)
			{
				return;
			}
			wasLanded = landed;
			var ship = round.elevatorTransform;
			if (ship == null || !GameNetworkManager.Instance.isHostingGame)
			{
				return; // the host's Minecraft world holds the ship; its Lethal Company moves it
			}
			if (!ShipBox(round, ship, out Vector3 lo, out Vector3 hi))
			{
				return;
			}
			if (landed)
			{
				landedPose = ship.localToWorldMatrix;
				moonOffset = slotOffset;
				haveLandedPose = true;
				// Ship's stretch -> the moon's (cleared first).
				Move(ShipCorners(lo, hi), ShipToMoon, "landed", MoonSlot, true);
			}
			else if (haveLandedPose)
			{
				var corners = ShipCorners(lo, hi);
				for (int i = 0; i < corners.Length; i++)
				{
					corners[i] = ShipToMoon(corners[i]);
				}
				// The moon's stretch -> the ship's (then the moon is cleared).
				Move(corners, MoonToShip, "took off", MoonSlot, false);
				haveLandedPose = false;
			}
		}

		/// <summary>The ship's bounds, in its own space (Unity metres), with a margin.</summary>
		private static bool ShipBox(StartOfRound round, Transform ship, out Vector3 lo, out Vector3 hi)
		{
			lo = hi = Vector3.zero;
			var bounds = round.shipBounds;
			if (bounds == null)
			{
				return false;
			}
			var b = bounds.bounds;
			lo = Vector3.one * float.MaxValue;
			hi = Vector3.one * float.MinValue;
			for (int i = 0; i < 8; i++)
			{
				var w = new Vector3((i & 1) != 0 ? b.max.x : b.min.x, (i & 2) != 0 ? b.max.y : b.min.y, (i & 4) != 0 ? b.max.z : b.min.z);
				var l = ship.InverseTransformPoint(w);
				lo = Vector3.Min(lo, l);
				hi = Vector3.Max(hi, l);
			}
			lo -= Vector3.one * Margin;
			hi += Vector3.one * Margin;
			return true;
		}

		/// <summary>The ship box's corners in the ship's stretch (Minecraft blocks: slot 0, the ship's own space).</summary>
		private static Vector3[] ShipCorners(Vector3 lo, Vector3 hi)
		{
			float k = Coords.K;
			var c = new Vector3[8];
			for (int i = 0; i < 8; i++)
			{
				var l = new Vector3((i & 1) != 0 ? hi.x : lo.x, (i & 2) != 0 ? hi.y : lo.y, (i & 4) != 0 ? hi.z : lo.z);
				c[i] = new Vector3(l.x * k, l.y * k, -l.z * k);
			}
			return c;
		}

		private int MoonSlot => (int)System.Math.Round(moonOffset / SlotBlocks);

		private Vector3 ShipToMoon(Vector3 shipMc)
		{
			float k = Coords.K;
			var world = landedPose.MultiplyPoint3x4(new Vector3(shipMc.x / k, shipMc.y / k, -shipMc.z / k));
			return new Vector3((float)(world.x * k + moonOffset), world.y * k, -world.z * k);
		}

		private Vector3 MoonToShip(Vector3 moonMc)
		{
			float k = Coords.K;
			var world = new Vector3((float)((moonMc.x - moonOffset) / k), moonMc.y / k, -moonMc.z / k);
			var l = landedPose.inverse.MultiplyPoint3x4(world);
			return new Vector3(l.x * k, l.y * k, -l.z * k);
		}

		/// <summary>Every block in the source box (corners, source Minecraft coords) to where map puts it; the moon slot cleared before or after.</summary>
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
			var turned = Turn(min, q);
			var offset = to - turned;
			SharedLink.Instance.RequestBlockMove(min, max, q, offset, clearSlot, clearFirst);
			Log.Info($"ship {why}: its blocks move with it (box {min} .. {max}, {q} quarter turns, offset {offset}); moon slot {clearSlot} cleared {(clearFirst ? "first" : "after")}");
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
