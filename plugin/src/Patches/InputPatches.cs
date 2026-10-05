using GameNetcodeStuff;
using HarmonyLib;
using LethalCraft.Input;
using LethalCraft.Player;
using UnityEngine.InputSystem;

namespace LethalCraft.Patches
{
	/// <summary>
	/// The game's side of InputBridge's bindings while Minecraft drives: its interact moves from E
	/// (Minecraft's inventory) to the right button, its scan from the right button to the middle one,
	/// and its item keys (use, wheel, secondary and tertiary use, drop, utility belt) are off: its
	/// objects are in Minecraft's hotbar (Inventory.InventoryBridge). Its emotes (1 and 2, Minecraft's
	/// hotbar) are off too.
	///
	/// The extra bindings are added to the game's actions (not overrides: those go into its settings
	/// file); each handler then checks which control fired it.
	/// </summary>
	internal static class InputPatches
	{
		private static bool bindingsAdded;

		/// <summary>Once the game's actions exist: right button for interact, middle button for scan.</summary>
		public static void AddBindings()
		{
			if (bindingsAdded || InputSystem.actions == null)
			{
				return;
			}
			var interact = InputSystem.actions.FindAction("Interact", false);
			var scan = InputSystem.actions.FindAction("PingScan", false);
			if (interact == null || scan == null)
			{
				return;
			}
			bindingsAdded = true;
			Add(interact, "<Mouse>/rightButton");
			Add(scan, "<Mouse>/middleButton");
			Log.Info("input: the game's interact is also on the right button, its scan on the middle button");
		}

		private static void Add(InputAction action, string path)
		{
			bool enabled = action.enabled;
			if (enabled)
			{
				action.Disable();
			}
			action.AddBinding(path);
			if (enabled)
			{
				action.Enable();
			}
		}

		public static bool Driving(PlayerControllerB p) => Puppet.Active && p != null && p == Lc.Game.Player;

		public static bool FromMouse(InputAction.CallbackContext context, string button) =>
			context.control != null && context.control.device is Mouse && context.control.name == button;
	}

	[HarmonyPatch(typeof(PlayerControllerB))]
	internal static class PlayerInputPatches
	{
		// Interact: the right button only (E is Minecraft's inventory), and only when InputBridge gave it the press.
		[HarmonyPrefix, HarmonyPatch("Interact_performed")]
		private static bool Interact(PlayerControllerB __instance, InputAction.CallbackContext context) =>
			!InputPatches.Driving(__instance) || InputPatches.FromMouse(context, "rightButton");

		// Hold-to-interact (the ship's lever, doors with a timer) reads the action directly: the right button only.
		[HarmonyPrefix, HarmonyPatch(nameof(PlayerControllerB.ClickHoldInteraction))]
		private static bool ClickHold(PlayerControllerB __instance)
		{
			if (!InputPatches.Driving(__instance) || Mouse.current == null || Mouse.current.rightButton.isPressed)
			{
				return true;
			}
			__instance.isHoldingInteract = false;
			__instance.StopHoldInteractionOnTrigger();
			return false;
		}

		// The game's item keys are off while Minecraft drives: InventoryBridge uses and drops the object
		// from Minecraft's controls (right click, sneak + click, Q), and Minecraft's hotbar selects it.
		[HarmonyPrefix, HarmonyPatch("ActivateItem_performed")]
		private static bool ActivateItem(PlayerControllerB __instance) => !InputPatches.Driving(__instance);

		[HarmonyPrefix, HarmonyPatch("ActivateItem_canceled")]
		private static bool ActivateItemUp(PlayerControllerB __instance) => !InputPatches.Driving(__instance);

		[HarmonyPrefix, HarmonyPatch("ScrollMouse_performed")]
		private static bool Scroll(PlayerControllerB __instance) => !InputPatches.Driving(__instance);

		[HarmonyPrefix, HarmonyPatch("ItemSecondaryUse_performed")]
		private static bool SecondaryUse(PlayerControllerB __instance) => !InputPatches.Driving(__instance);

		[HarmonyPrefix, HarmonyPatch("ItemTertiaryUse_performed")]
		private static bool TertiaryUse(PlayerControllerB __instance) => !InputPatches.Driving(__instance);

		[HarmonyPrefix, HarmonyPatch("QEItemInteract_performed")]
		private static bool QEItem(PlayerControllerB __instance) => !InputPatches.Driving(__instance);

		[HarmonyPrefix, HarmonyPatch("Discard_performed")]
		private static bool Discard(PlayerControllerB __instance) => !InputPatches.Driving(__instance);

		[HarmonyPrefix, HarmonyPatch("UseUtilitySlot_performed")]
		private static bool UtilitySlot(PlayerControllerB __instance) => !InputPatches.Driving(__instance);

		[HarmonyPrefix, HarmonyPatch("Emote1_performed")]
		private static bool Emote1(PlayerControllerB __instance) => !InputPatches.Driving(__instance);

		[HarmonyPrefix, HarmonyPatch("Emote2_performed")]
		private static bool Emote2(PlayerControllerB __instance) => !InputPatches.Driving(__instance);
	}

	[HarmonyPatch(typeof(HUDManager), "PingScan_performed")]
	internal static class ScanPatch
	{
		// Scan: the middle button only while Minecraft drives (the right button is interact / Minecraft's use).
		private static bool Prefix(InputAction.CallbackContext context) =>
			!InputPatches.Driving(Lc.Game.Player) || InputPatches.FromMouse(context, "middleButton");
	}
}
