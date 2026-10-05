using System.Collections.Generic;
using GameNetcodeStuff;
using LethalCraft.Link;
using UnityEngine;

namespace LethalCraft.World
{
	/// <summary>
	/// The game's ladders as Minecraft ladders (dev.lethalcraft.world.HostLadders): for each ladder
	/// near the player, a climbable box where the game would put a climbing player (its
	/// ladderHorizontalPosition), from the ladder's foot to a little over its top so the player can
	/// step off onto the floor above. The game's own ladder mode (snapping on with interact) is off
	/// while Minecraft drives (Patches.InventoryPatches).
	/// </summary>
	internal sealed class LadderExporter
	{
		public static readonly LadderExporter Instance = new LadderExporter();

		private const float HalfWidth = 0.55f;   // metres around where the game puts the climber
		private const float BelowFoot = 0.2f;
		private const float OverTop = 0.6f;
		private const float Range = 48f;
		private const float FindEvery = 5f;      // new ladders appear with the level and the facility
		private const float SendEvery = 0.25f;

		private readonly List<InteractTrigger> ladders = new List<InteractTrigger>();
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
			int n = 0;
			foreach (var t in ladders)
			{
				if (n >= Proto.MaxLadders)
				{
					break;
				}
				if (t == null || !t.isActiveAndEnabled || !t.interactable)
				{
					continue;
				}
				Vector3 h = t.ladderHorizontalPosition.position;
				float bottom = t.bottomOfLadderPosition.position.y - BelowFoot;
				float top = t.topOfLadderPosition.position.y + OverTop;
				if ((new Vector3(h.x, Mathf.Clamp(at.y, bottom, top), h.z) - at).sqrMagnitude > Range * Range)
				{
					continue;
				}
				// The column's corners in Minecraft's coordinates (the current frame: the ship's, or the moon's).
				var lo = Vector3.one * float.MaxValue;
				var hi = Vector3.one * float.MinValue;
				// Where the game puts the climber, and the ladder itself (its trigger), so walking into it stays inside.
				var xz = new Bounds(new Vector3(h.x, 0f, h.z), new Vector3(HalfWidth * 2f, 0f, HalfWidth * 2f));
				var trigger = t.GetComponent<Collider>();
				if (trigger != null)
				{
					var tb = trigger.bounds;
					xz.Encapsulate(new Vector3(tb.min.x, 0f, tb.min.z));
					xz.Encapsulate(new Vector3(tb.max.x, 0f, tb.max.z));
				}
				for (int i = 0; i < 8; i++)
				{
					var w = new Vector3((i & 1) != 0 ? xz.max.x : xz.min.x, (i & 2) != 0 ? top : bottom, (i & 4) != 0 ? xz.max.z : xz.min.z);
					var mc = Coords.ToMcF(w);
					lo = Vector3.Min(lo, mc);
					hi = Vector3.Max(hi, mc);
				}
				boxes[n * 2] = lo;
				boxes[n * 2 + 1] = hi;
				n++;
			}
			link.WriteLadders(boxes, n);
		}
	}
}
