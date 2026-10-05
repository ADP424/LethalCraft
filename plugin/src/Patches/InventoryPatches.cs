using System;
using GameNetcodeStuff;
using HarmonyLib;
using LethalCraft.Inventory;
using UnityEngine;
using UnityEngine.UI;

namespace LethalCraft.Patches
{
	/// <summary>
	/// The game's side of the one inventory (Inventory.InventoryBridge):
	///  - every player has 10 item slots, one per Minecraft hotbar slot plus the offhand (on every
	///    client, since the game syncs slot switches by number); its HUD's 4-slot arrays are padded
	///    to match, never shown;
	///  - picking something up needs a free hotbar/offhand slot in Minecraft;
	///  - the crosshair hints name the keys Minecraft's controls use.
	/// </summary>
	[HarmonyPatch]
	internal static class InventoryPatches
	{
		[HarmonyPostfix, HarmonyPatch(typeof(PlayerControllerB), "Awake")]
		private static void MoreSlots(PlayerControllerB __instance)
		{
			if (__instance.ItemSlots != null && __instance.ItemSlots.Length < InventoryBridge.Slots)
			{
				var slots = new GrabbableObject[InventoryBridge.Slots];
				Array.Copy(__instance.ItemSlots, slots, __instance.ItemSlots.Length);
				__instance.ItemSlots = slots;
			}
		}

		[HarmonyPostfix, HarmonyPatch(typeof(HUDManager), "Awake")]
		private static void PadSlotHud(HUDManager __instance)
		{
			var frames = __instance.itemSlotIconFrames;
			var icons = __instance.itemSlotIcons;
			if (frames == null || icons == null || frames.Length == 0 || frames.Length >= InventoryBridge.Slots)
			{
				return;
			}
			int had = frames.Length;
			Array.Resize(ref frames, InventoryBridge.Slots);
			Array.Resize(ref icons, InventoryBridge.Slots);
			var template = frames[had - 1];
			for (int i = had; i < InventoryBridge.Slots; i++)
			{
				// A copy of the last slot (its frame, its icon and its animator), shrunk away: the game's own
				// code can address slots 5 to 10 like 1 to 4, and nothing shows.
				var copy = UnityEngine.Object.Instantiate(template.gameObject, template.transform.parent, false);
				copy.name = $"{template.gameObject.name} (LethalCraft {i + 1})";
				copy.transform.localScale = Vector3.zero;
				frames[i] = copy.GetComponent<Image>();
				Image icon = null;
				foreach (var image in copy.GetComponentsInChildren<Image>(true))
				{
					if (image != frames[i])
					{
						icon = image;
						break;
					}
				}
				icons[i] = icon != null ? icon : frames[i];
			}
			__instance.itemSlotIconFrames = frames;
			__instance.itemSlotIcons = icons;
			Log.Info($"inventory: the game's slot HUD padded from {had} to {InventoryBridge.Slots}");
		}

		private static bool Driving(PlayerControllerB p) => InputPatches.Driving(p);

		// The game's ladder mode (snap on, climb with its own controls) is off: its ladders are Minecraft's.
		[HarmonyPrefix, HarmonyPatch(typeof(InteractTrigger), nameof(InteractTrigger.Interact))]
		private static bool NoLadderMode(InteractTrigger __instance) => !__instance.isLadder || !Driving(Lc.Game.Player);

		[HarmonyPrefix, HarmonyPatch(typeof(PlayerControllerB), "BeginGrabObject")]
		private static bool HotbarFull(PlayerControllerB __instance)
		{
			return !Driving(__instance) || InventoryBridge.McFreeSlots > 0;
		}

		[HarmonyPostfix, HarmonyPatch(typeof(PlayerControllerB), "SetHoverTipAndCurrentInteractTrigger")]
		private static void Hints(PlayerControllerB __instance)
		{
			if (!Driving(__instance) || __instance.cursorTip == null)
			{
				return;
			}
			if (__instance.hoveringOverTrigger != null && __instance.hoveringOverTrigger.isLadder)
			{
				__instance.cursorTip.text = ""; // climbed by walking into it, Minecraft's way
				return;
			}
			string text = __instance.cursorTip.text;
			if (string.IsNullOrEmpty(text))
			{
				return;
			}
			if (text.StartsWith("Grab") || text.StartsWith("Equip to belt"))
			{
				__instance.cursorTip.text = InventoryBridge.McFreeSlots > 0 ? "Grab : [Right click]" : "Hotbar full!";
				return;
			}
			if (text.Contains("[E]") || text.Contains("[LMB]"))
			{
				__instance.cursorTip.text = text.Replace("[E]", "[Right click]").Replace("[LMB]", "[Right click]");
			}
		}
	}
}
