using System.Collections.Generic;
using GameNetcodeStuff;
using LethalCraft.Link;
using UnityEngine;

namespace LethalCraft.World
{
	/// <summary>
	/// The game's ladders as Minecraft ladders (dev.lethalcraft.world.HostLadders): for each ladder
	/// near the player, a climbable box where the game would put a climbing player (its
	/// ladderHorizontalPosition), from the ladder's foot to a little over the highest thing the
	/// player has to get their feet over to step off: the floor at the dismount point
	/// (topOfLadderPosition), or a lip, railing or hatch rim on the way to it. The game itself ends
	/// its climb 2 m under the dismount point and slides the player there through all of that, so
	/// a second box (the way off) runs from the column towards the dismount point, under its floor:
	/// the player keeps climbing while moving over to where the way off is. The game's own ladder
	/// mode (snapping on with interact) is off while Minecraft drives (Patches.InventoryPatches).
	/// </summary>
	internal sealed class LadderExporter
	{
		public static readonly LadderExporter Instance = new LadderExporter();

		private const float HalfWidth = 0.55f;   // metres around where the game puts the climber
		private const float BelowFoot = 0.2f;
		private const float OverTop = 0.6f;      // over the highest lip: Minecraft steps up the rest
		private const float LipProbe = 1.2f;     // how far over the dismount floor a lip can stand
		private const float WayOffDepth = 1.0f;  // the way off: a band this deep under the dismount floor (touching it is enough)...
		private const float WayOffShort = 0.3f;  // ...stopping this far short of the dismount point
		private const float Range = 48f;
		private const float FindEvery = 5f;      // new ladders appear with the level and the facility
		private const float SendEvery = 0.25f;

		private readonly List<InteractTrigger> ladders = new List<InteractTrigger>();
		private readonly List<(float, InteractTrigger)> near = new List<(float, InteractTrigger)>();
		private readonly Vector3[] boxes = new Vector3[Proto.MaxLadders * 2];
		private float findTimer, sendTimer;

		public void Frame(bool session, PlayerControllerB p, float dt)
		{
			var link = SharedLink.Instance;
			if (!session || p == null)
			{
				ladders.Clear();
				findTimer = 0f;
				link.WriteLadders(boxes, 0);
				return;
			}
			findTimer -= dt;
			if (findTimer <= 0f)
			{
				findTimer = FindEvery;
				ladders.Clear();
				foreach (var t in Object.FindObjectsOfType<InteractTrigger>())
				{
					if (t.isLadder && t.ladderHorizontalPosition != null && t.bottomOfLadderPosition != null && t.topOfLadderPosition != null)
					{
						ladders.Add(t);
					}
				}
			}
			sendTimer -= dt;
			if (sendTimer > 0f)
			{
				return;
			}
			sendTimer = SendEvery;
			Vector3 at = p.transform.position;
			int mask = StartOfRound.Instance != null ? StartOfRound.Instance.collidersAndRoomMaskAndDefault : Physics.DefaultRaycastLayers;
			near.Clear();
			foreach (var t in ladders)
			{
				if (t == null || !t.isActiveAndEnabled || !t.interactable)
				{
					continue;
				}
				Vector3 h = t.ladderHorizontalPosition.position;
				float bottom = t.bottomOfLadderPosition.position.y - BelowFoot;
				float top = t.topOfLadderPosition.position.y + OverTop + LipProbe;
				float d = (new Vector3(h.x, Mathf.Clamp(at.y, bottom, top), h.z) - at).sqrMagnitude;
				if (d <= Range * Range)
				{
					near.Add((d, t));
				}
			}
			// Nearest first: each ladder takes two of the boxes.
			near.Sort((a, b) => a.Item1.CompareTo(b.Item1));
			int n = 0;
			foreach (var (_, t) in near)
			{
				if (n + 2 > Proto.MaxLadders)
				{
					break;
				}
				Vector3 h = t.ladderHorizontalPosition.position;
				Vector3 off = t.topOfLadderPosition.position;
				float bottom = t.bottomOfLadderPosition.position.y - BelowFoot;
				float top = LipTop(h, off, mask) + OverTop;
				// Where the game puts the climber, and the ladder itself (its trigger), so walking into it stays inside.
				var xz = new Bounds(new Vector3(h.x, 0f, h.z), new Vector3(HalfWidth * 2f, 0f, HalfWidth * 2f));
				var trigger = t.GetComponent<Collider>();
				if (trigger != null)
				{
					var tb = trigger.bounds;
					xz.Encapsulate(new Vector3(tb.min.x, 0f, tb.min.z));
					xz.Encapsulate(new Vector3(tb.max.x, 0f, tb.max.z));
				}
				Box(n++, xz, bottom, top);
				// The way off: from the column over to just short of the dismount point, all under its
				// floor (standing on that floor, the player is off the ladder).
				var way = new Bounds(new Vector3(h.x, 0f, h.z), new Vector3(HalfWidth * 2f, 0f, HalfWidth * 2f));
				Vector3 across = new Vector3(off.x - h.x, 0f, off.z - h.z);
				float far = Mathf.Max(0f, across.magnitude - WayOffShort);
				if (far > 0f)
				{
					Vector3 end = new Vector3(h.x, 0f, h.z) + across.normalized * far;
					way.Encapsulate(new Bounds(end, new Vector3(HalfWidth, 0f, HalfWidth)));
				}
				Box(n++, way, Mathf.Max(bottom, off.y - WayOffDepth), off.y - 0.05f);
			}
			link.WriteLadders(boxes, n);
		}

		/// <summary>Box i: these xz bounds (world, metres) from bottom to top, as a min/max pair in Minecraft's coordinates.</summary>
		private void Box(int i, Bounds xz, float bottom, float top)
		{
			// The corners in Minecraft's coordinates (the current frame: the ship's, or the moon's).
			var lo = Vector3.one * float.MaxValue;
			var hi = Vector3.one * float.MinValue;
			for (int c = 0; c < 8; c++)
			{
				var w = new Vector3((c & 1) != 0 ? xz.max.x : xz.min.x, (c & 2) != 0 ? top : bottom, (c & 4) != 0 ? xz.max.z : xz.min.z);
				var mc = Coords.ToMcF(w);
				lo = Vector3.Min(lo, mc);
				hi = Vector3.Max(hi, mc);
			}
			boxes[i * 2] = lo;
			boxes[i * 2 + 1] = hi;
		}

		/// <summary>
		/// The highest surface between the climber's column and the dismount point (world y): the
		/// dismount floor, or a lip, railing or rim on the way that's up to LipProbe over it.
		/// </summary>
		private static float LipTop(Vector3 column, Vector3 dismount, int mask)
		{
			float top = dismount.y;
			var from = new Vector3(column.x, dismount.y, column.z);
			for (int i = 0; i <= 4; i++)
			{
				Vector3 at = Vector3.Lerp(from, dismount, i / 4f) + Vector3.up * LipProbe;
				if (Physics.Raycast(at, Vector3.down, out RaycastHit hit, LipProbe + 0.5f, mask, QueryTriggerInteraction.Ignore))
				{
					top = Mathf.Max(top, hit.point.y);
				}
			}
			return top;
		}
	}
}
