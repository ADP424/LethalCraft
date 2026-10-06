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
				FrameBlocks.ToMoon(landedPose, 0.0, moonOffset, lo, hi, MoonSlot, true, "ship landed");
			}
			else if (haveLandedPose)
			{
				// The moon's stretch -> the ship's (then the moon is cleared).
				FrameBlocks.FromMoon(landedPose, 0.0, moonOffset, lo, hi, MoonSlot, false, "ship took off");
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

		private int MoonSlot => (int)System.Math.Round(moonOffset / SlotBlocks);

	}
}
