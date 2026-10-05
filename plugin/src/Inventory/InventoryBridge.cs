using System.Collections.Generic;
using GameNetcodeStuff;
using LethalCraft.Link;
using UnityEngine;

namespace LethalCraft.Inventory
{
	/// <summary>
	/// One inventory, Minecraft's (its hotbar and offhand). The game keeps its objects where it always
	/// does, in the player's item slots (10 of them here, plus its utility belt), with all its own
	/// networking; Minecraft holds one token item per object (dev.lethalcraft.item.GameItems).
	///
	///  - Out, every frame: the objects the player holds (id, name, value, battery, two-handed, in
	///    hand). Minecraft adds and removes tokens to match.
	///  - In: which token Minecraft has selected. The game's hand switches to that object (its own
	///    synced slot switch); a Minecraft item or an empty slot selected is an empty hand.
	///  - In: a token dropped in Minecraft (Q, or thrown out of its inventory screen): the game drops
	///    the object, or puts it on the company's counter when it's there.
	///  - Using the object (right click; sneak + right / left click for its secondary / tertiary
	///    use) is InputBridge's, through <see cref="Use"/> and <see cref="UseSecondary"/>.
	/// </summary>
	internal sealed class InventoryBridge
	{
		public static readonly InventoryBridge Instance = new InventoryBridge();

		/// <summary>Item slots every player gets: one per Minecraft hotbar slot, plus the offhand.</summary>
		public const int Slots = 10;
		private const int UtilitySlot = 50;
		private const float DropTimeout = 2f;

		/// <summary>Minecraft's free hotbar/offhand slots (picking something up needs one).</summary>
		public static uint McFreeSlots = 1;

		/// <summary>The game object Minecraft has selected in its main hand (0: none).</summary>
		public static uint McSelected;

		private readonly GameItemRecord[] records = new GameItemRecord[Proto.MaxGameItems];
		private int lastHash;
		private uint version;
		private readonly List<(uint id, float time)> drops = new List<(uint, float)>();

		public static uint IdOf(GrabbableObject g) => g != null ? (uint)g.NetworkObjectId + 1 : 0u;

		/// <summary>Every frame. driving: Minecraft has the player and a game is on.</summary>
		public void Frame(bool session, bool driving, PlayerControllerB p)
		{
			var link = SharedLink.Instance;
			if (!session || p == null)
			{
				link.WriteGameItems(false, version, records, 0);
				drops.Clear();
				return;
			}
			int count = Collect(p);
			int hash = Hash(count);
			if (hash != lastHash)
			{
				lastHash = hash;
				version++;
			}
			link.WriteGameItems(true, version, records, count);
			if (!driving || p.isPlayerDead || !p.isPlayerControlled)
			{
				drops.Clear();
				return;
			}
			if (drops.Count > 0)
			{
				DropNext(p);
				return;
			}
			FollowSelection(p);
		}

		// ---- the game's objects out ----------------------------------------------------------------

		private int Collect(PlayerControllerB p)
		{
			int n = 0;
			if (p.ItemSlots != null)
			{
				foreach (var g in p.ItemSlots)
				{
					Add(p, g, ref n);
				}
			}
			Add(p, p.ItemOnlySlot, ref n);
			return n;
		}

		private void Add(PlayerControllerB p, GrabbableObject g, ref int n)
		{
			if (g == null || g.itemProperties == null || n >= records.Length)
			{
				return;
			}
			var item = g.itemProperties;
			uint flags = 0;
			if (item.twoHanded)
			{
				flags |= Proto.GameItemTwoHanded;
			}
			if (g == p.currentlyHeldObjectServer)
			{
				flags |= Proto.GameItemInHand;
			}
			float battery = 0f;
			if (item.requiresBattery && g.insertedBattery != null)
			{
				flags |= Proto.GameItemBattery;
				battery = Mathf.Clamp01(g.insertedBattery.charge);
			}
			records[n++] = new GameItemRecord
			{
				Id = IdOf(g),
				Flags = flags,
				Value = item.isScrap ? g.scrapValue : 0,
				Battery = battery,
				Name = item.itemName,
			};
		}

		private int Hash(int count)
		{
			unchecked
			{
				int h = 17 + count;
				for (int i = 0; i < count; i++)
				{
					var r = records[i];
					h = h * 31 + (int)r.Id;
					h = h * 31 + (int)r.Flags;
					h = h * 31 + r.Value;
					h = h * 31 + Mathf.RoundToInt(r.Battery * 100f);
				}
				return h;
			}
		}

		// ---- Minecraft's selection in --------------------------------------------------------------

		private static GrabbableObject InSlot(PlayerControllerB p, int slot) =>
			slot == UtilitySlot ? p.ItemOnlySlot : slot >= 0 && slot < p.ItemSlots.Length ? p.ItemSlots[slot] : null;

		private static int SlotOf(PlayerControllerB p, uint id)
		{
			for (int i = 0; i < p.ItemSlots.Length; i++)
			{
				if (IdOf(p.ItemSlots[i]) == id)
				{
					return i;
				}
			}
			return p.ItemOnlySlot != null && IdOf(p.ItemOnlySlot) == id ? UtilitySlot : -1;
		}

		private static bool Busy(PlayerControllerB p) =>
			p.isGrabbingObjectAnimation || p.throwingObject || p.activatingItem || p.inSpecialInteractAnimation || p.jetpackControls || p.disablingJetpackControls
			|| p.isTypingChat || p.inTerminalMenu || p.timeSinceSwitchingSlots < 0.1f;

		private static void FollowSelection(PlayerControllerB p)
		{
			// Both hands full: the game won't let go, and Minecraft's hotbar is held on it (GameItemsClient).
			if (p.twoHanded || Busy(p))
			{
				return;
			}
			int target;
			if (McSelected != 0)
			{
				target = SlotOf(p, McSelected);
				if (target < 0)
				{
					return; // a token whose object the game has let go of: Minecraft removes it shortly
				}
			}
			else
			{
				if (InSlot(p, p.currentItemSlot) == null)
				{
					return; // already an empty hand
				}
				target = -1;
				for (int i = 0; i < p.ItemSlots.Length; i++)
				{
					if (p.ItemSlots[i] == null)
					{
						target = i;
						break;
					}
				}
				if (target < 0)
				{
					return;
				}
			}
			if (target != p.currentItemSlot)
			{
				SwitchTo(p, target);
			}
		}

		/// <summary>The game's own switch to a slot, synced to everyone (what its utility-belt key does).</summary>
		private static void SwitchTo(PlayerControllerB p, int slot)
		{
			ShipBuildModeManager.Instance.CancelBuildMode(true);
			p.playerBodyAnimator.SetBool("GrabValidated", false);
			p.SwitchToItemSlot(slot, null);
			p.SwitchToSlotServerRpc(slot);
			if (p.currentlyHeldObjectServer != null)
			{
				var audio = p.currentlyHeldObjectServer.gameObject.GetComponent<AudioSource>();
				if (audio != null && p.currentlyHeldObjectServer.itemProperties.grabSFX != null)
				{
					audio.PlayOneShot(p.currentlyHeldObjectServer.itemProperties.grabSFX, 0.6f);
				}
			}
			p.timeSinceSwitchingSlots = 0f;
		}

		// ---- dropping ------------------------------------------------------------------------------

		/// <summary>Minecraft dropped this object's token: the game drops the object.</summary>
		public void OnDropped(uint id)
		{
			drops.Add((id, Time.time));
		}

		private void DropNext(PlayerControllerB p)
		{
			var (id, time) = drops[0];
			int slot = SlotOf(p, id);
			if (slot < 0 || Time.time - time > DropTimeout)
			{
				drops.RemoveAt(0);
				return;
			}
			if (p.isGrabbingObjectAnimation || p.throwingObject || p.activatingItem || p.isTypingChat || p.inSpecialInteractAnimation)
			{
				return;
			}
			if (slot != p.currentItemSlot)
			{
				if (!p.twoHanded)
				{
					SwitchTo(p, slot);
				}
				return; // the drop goes next frame, with the object in hand
			}
			drops.RemoveAt(0);
			ShipBuildModeManager.Instance.CancelBuildMode(true);
			var desk = Object.FindObjectOfType<DepositItemsDesk>();
			if (desk != null && p.currentlyHeldObjectServer != null && desk.triggerCollider.bounds.Contains(p.currentlyHeldObjectServer.transform.position))
			{
				desk.PlaceItemOnCounter(p);
				return;
			}
			Log.Info($"dropping {p.currentlyHeldObjectServer?.itemProperties?.itemName} (its token was dropped in Minecraft)");
			p.DiscardHeldObject(false, null, default(Vector3), true);
		}

		// ---- using ---------------------------------------------------------------------------------

		/// <summary>The selected token is the object in the game's hand.</summary>
		public static bool HoldingSelected(PlayerControllerB p) =>
			p != null && McSelected != 0 && p.currentlyHeldObjectServer != null && IdOf(p.currentlyHeldObjectServer) == McSelected;

		/// <summary>Right click with an object: the game's "use item", press and release (what its left button does).</summary>
		public static void Use(PlayerControllerB p, bool down)
		{
			if (!p.CanUseItem())
			{
				return;
			}
			if (down)
			{
				if (p.timeSinceSwitchingSlots < 0.075f)
				{
					return;
				}
				ShipBuildModeManager.Instance.CancelBuildMode(true);
				p.currentlyHeldObjectServer.UseItemOnClient(true);
				p.timeSinceSwitchingSlots = 0f;
				return;
			}
			if (!p.currentlyHeldObjectServer.itemProperties.holdButtonUse)
			{
				return;
			}
			ShipBuildModeManager.Instance.CancelBuildMode(true);
			p.currentlyHeldObjectServer.UseItemOnClient(false);
		}

		/// <summary>Sneak + right click: the object's secondary use (the game's Q); sneak + left click: tertiary (its E).</summary>
		public static void UseSecondary(PlayerControllerB p, bool tertiary)
		{
			if (!p.equippedUsableItemQE || p.isGrabbingObjectAnimation || p.isTypingChat || p.inTerminalMenu || p.inSpecialInteractAnimation || p.throwingObject)
			{
				return;
			}
			if (p.timeSinceSwitchingSlots < 0.2f || p.currentlyHeldObjectServer == null)
			{
				return;
			}
			p.timeSinceSwitchingSlots = 0f;
			p.currentlyHeldObjectServer.ItemInteractLeftRightOnClient(tertiary);
		}
	}
}
