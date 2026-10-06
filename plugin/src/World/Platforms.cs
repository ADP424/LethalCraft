using System.Collections.Generic;
using GameNetcodeStuff;
using UnityEngine;

namespace LethalCraft.World
{
	/// <summary>
	/// Riding things that move (the mineshaft's elevator, the Company Cruiser's bed, a modded lift):
	/// while the thing the player stands on moves, it is Minecraft's frame, as the ship is in flight.
	/// Minecraft's world then holds still around the player (a separate stretch, the platforms' slot),
	/// its collision is the platform's own, and the player rides it without falling through a floor
	/// that moved away. Once it has stood still for a moment the moon's frame comes back, so the
	/// player walks off as usual.
	///
	/// What counts: the game's own moving-platform region (PlayerPhysicsRegion: it sets the player's
	/// physicsParent), or else whatever moving object is under the player's feet (the topmost of its
	/// parents that moved), so lifts without a physics region work too.
	///
	/// Blocks built on a platform go with it (FrameBlocks): into the platforms' stretch when it starts
	/// to move, back into the moon's where it stopped. The ship has its own handling (ShipBlockMover).
	/// </summary>
	internal sealed class Platforms
	{
		public static readonly Platforms Instance = new Platforms();

		/// <summary>Minecraft X of the platforms' stretch (moons are at +4096 per level, the ship at 0).</summary>
		public const double SlotOffset = -4096;

		private const float StillSeconds = 0.5f; // standing still this long: part of the moon again
		private const float LeaveSeconds = 0.3f; // off it this long: not riding it any more
		private const float StartSeconds = 0.15f; // moving this long under the player: a ride (not a nudge)
		private const float Margin = 0.75f;      // metres around its colliders: blocks built onto it
		private const float MovedMetres = 0.0005f;
		private const float MovedDegrees = 0.02f;

		private Transform current;
		private Transform standingOn;     // the collider under the player's feet this frame
		private Transform foundMoving;    // what Under found moving this frame, and its pose the frame before
		private (Vector3 pos, Quaternion rot) foundWas;
		private bool moving;
		private float stillFor, offFor, movingFor;
		private Vector3 lastPos;
		private Quaternion lastRot;
		private Matrix4x4 stillPose;      // its local-to-world as it last stood still
		private double moonOffset;
		private Vector3 boxLo, boxHi;     // its own space, metres
		private readonly Dictionary<Transform, (Vector3 pos, Quaternion rot)> seen = new Dictionary<Transform, (Vector3, Quaternion)>();
		private readonly Dictionary<Transform, (Vector3 pos, Quaternion rot)> seenNow = new Dictionary<Transform, (Vector3, Quaternion)>();

		/// <summary>The platform riding now, for diagnostics.</summary>
		public string Riding => moving && current != null ? current.name : "-";

		public void Reset()
		{
			current = null;
			moving = false;
			seen.Clear();
		}

		/// <summary>
		/// Every frame: the moving platform the player rides (Minecraft's frame), or null. moonSlotOffset:
		/// the current moon's stretch, where the platform's blocks stand while it's still.
		/// </summary>
		public Transform Frame(PlayerControllerB p, bool session, double moonSlotOffset, float dt)
		{
			if (!session || p == null || !p.isPlayerControlled || p.isPlayerDead || p.isInElevator || moonSlotOffset <= 0)
			{
				if (current != null && moving)
				{
					Stop("left the moon");
				}
				Reset();
				return null;
			}
			Transform under = Under(p);
			if (current == null)
			{
				if (under == null)
				{
					return null;
				}
				Begin(under, moonSlotOffset);
			}
			bool moved = Moved(current, ref lastPos, ref lastRot);
			stillFor = moved ? 0f : stillFor + dt;
			movingFor = moved ? movingFor + dt : 0f;
			bool on = under == current || p.physicsParent == current || standingOn != null && standingOn.IsChildOf(current);
			offFor = on ? 0f : offFor + dt;
			if (offFor > LeaveSeconds)
			{
				if (moving)
				{
					Stop("got off");
				}
				current = null;
				moving = false;
				return null;
			}
			if (!moving)
			{
				if (moved && on && movingFor >= StartSeconds)
				{
					Start();
				}
				else if (!moved)
				{
					stillPose = current.localToWorldMatrix;
				}
			}
			else if (stillFor > StillSeconds)
			{
				Stop("stopped");
			}
			return moving ? current : null;
		}

		/// <summary>What the player rides: the game's physics region, or the moving thing under their feet.</summary>
		private Transform Under(PlayerControllerB p)
		{
			if (p.physicsParent != null)
			{
				standingOn = null;
				return p.physicsParent;
			}
			seenNow.Clear();
			standingOn = null;
			Transform found = null;
			int mask = StartOfRound.Instance != null ? StartOfRound.Instance.collidersAndRoomMaskAndDefault : Physics.DefaultRaycastLayers;
			var ship = StartOfRound.Instance != null ? StartOfRound.Instance.elevatorTransform : null;
			if (Physics.Raycast(p.transform.position + Vector3.up * 0.5f, Vector3.down, out RaycastHit hit, 1.2f, mask, QueryTriggerInteraction.Ignore))
			{
				standingOn = hit.collider.transform;
				for (var t = hit.collider.transform; t != null; t = t.parent)
				{
					if (ship != null && (t == ship || t.IsChildOf(ship)))
					{
						break; // the ship is ShipBlockMover's
					}
					seenNow[t] = (t.position, t.rotation);
					if (seen.TryGetValue(t, out var was) && Differs(was.pos, was.rot, t.position, t.rotation))
					{
						found = t; // the topmost of them that moved: the whole lift, not just its floor
						foundWas = was;
					}
				}
			}
			foundMoving = found;
			seen.Clear();
			foreach (var kv in seenNow)
			{
				seen[kv.Key] = kv.Value;
			}
			return found;
		}

		private void Begin(Transform platform, double moonSlotOffset)
		{
			current = platform;
			moonOffset = moonSlotOffset;
			moving = false;
			stillFor = offFor = movingFor = 0f;
			lastPos = platform.position;
			lastRot = platform.rotation;
			stillPose = platform.localToWorldMatrix;
			// Found moving under the feet: where it stood last frame is its still pose (and this frame's move counts).
			if (platform == foundMoving)
			{
				var was = foundWas;
				lastPos = was.pos;
				lastRot = was.rot;
				stillPose = Matrix4x4.TRS(was.pos, was.rot, platform.lossyScale);
			}
			Box(platform);
		}

		/// <summary>Its colliders' bounds in its own space, with a margin.</summary>
		private void Box(Transform platform)
		{
			boxLo = Vector3.one * float.MaxValue;
			boxHi = Vector3.one * float.MinValue;
			bool any = false;
			foreach (var c in platform.GetComponentsInChildren<Collider>())
			{
				if (c.isTrigger || !c.enabled)
				{
					continue;
				}
				var b = c.bounds;
				for (int i = 0; i < 8; i++)
				{
					var w = new Vector3((i & 1) != 0 ? b.max.x : b.min.x, (i & 2) != 0 ? b.max.y : b.min.y, (i & 4) != 0 ? b.max.z : b.min.z);
					var l = platform.InverseTransformPoint(w);
					boxLo = Vector3.Min(boxLo, l);
					boxHi = Vector3.Max(boxHi, l);
				}
				any = true;
			}
			if (!any)
			{
				boxLo = -Vector3.one;
				boxHi = Vector3.one;
			}
			boxLo -= Vector3.one * Margin;
			boxHi += Vector3.one * Margin;
		}

		private void Start()
		{
			moving = true;
			Log.Info($"platform: riding {current.name} (it started moving)");
			if (GameNetworkManager.Instance.isHostingGame)
			{
				// The moon's stretch -> the platforms' (nothing is cleared).
				FrameBlocks.FromMoon(stillPose, SlotOffset, moonOffset, boxLo, boxHi, 0, false, $"platform {current.name} started moving");
			}
		}

		private void Stop(string why)
		{
			moving = false;
			stillPose = current.localToWorldMatrix;
			Log.Info($"platform: {current.name} {why}; the moon's frame again");
			if (GameNetworkManager.Instance.isHostingGame)
			{
				// The platforms' stretch -> the moon's, where it is now.
				FrameBlocks.ToMoon(stillPose, SlotOffset, moonOffset, boxLo, boxHi, 0, false, $"platform {current.name} {why}");
			}
		}

		private static bool Moved(Transform t, ref Vector3 pos, ref Quaternion rot)
		{
			bool moved = Differs(pos, rot, t.position, t.rotation);
			pos = t.position;
			rot = t.rotation;
			return moved;
		}

		private static bool Differs(Vector3 aPos, Quaternion aRot, Vector3 bPos, Quaternion bRot) =>
			(aPos - bPos).sqrMagnitude > MovedMetres * MovedMetres || Quaternion.Angle(aRot, bRot) > MovedDegrees;
	}
}
