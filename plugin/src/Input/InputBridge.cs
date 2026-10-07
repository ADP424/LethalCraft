using System.Collections.Generic;
using GameNetcodeStuff;
using LethalCraft.Inventory;
using LethalCraft.Link;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.InputSystem.Controls;

namespace LethalCraft.Input
{
	/// <summary>
	/// The keyboard and mouse while Minecraft drives the player, with Minecraft's own default bindings,
	/// and Lethal Company's actions fitted around them (Patches.InputPatches holds the game's side).
	/// There is one inventory, Minecraft's hotbar and offhand; the game's objects are items in it
	/// (Inventory.InventoryBridge).
	///
	///   W A S D, Space, Left Ctrl (sprint), Left Shift (sneak)   Minecraft's movement
	///   E                                                         Minecraft's inventory
	///   1-9, wheel                                                Minecraft's hotbar (the game's objects included)
	///   Q                                                         Minecraft's drop (a game object: the game drops it)
	///   left button                                               Minecraft's attack / mine
	///   right button                                              looking at something of the game's (door, button,
	///                                                             scrap): the game's interact; holding a game object:
	///                                                             its use; otherwise Minecraft's use / place
	///   sneak + right / left button, holding a game object        the object's secondary / tertiary use (the game's Q / E)
	///   middle button                                             the game's scan
	///
	/// While a Minecraft screen (inventory, chat, pause...) is open everything goes to Minecraft, with a
	/// cursor of its own, and the game's input is switched off.
	/// </summary>
	internal sealed class InputBridge
	{
		public static readonly InputBridge Instance = new InputBridge();

		/// <summary>A Minecraft screen is open: it has all the input (read by the patches and the look).</summary>
		public static bool ScreenOpen;

		// SDL scancodes (USB HID usages) of Minecraft's default keys.
		private const ushort W = 26, A = 4, S = 22, D = 7, Space = 44, SprintKey = 224, SneakKey = 225, InventoryKey = 8, DropKey = 20, PerspectiveKey = 62, ChatKey = 23, CommandKey = 56;
		private static readonly (Key key, ushort scancode)[] Movement =
		{
			(Key.W, W), (Key.A, A), (Key.S, S), (Key.D, D), (Key.Space, Space), (Key.LeftCtrl, SprintKey), (Key.LeftShift, SneakKey),
		};
		private static readonly Key[] Digits = { Key.Digit1, Key.Digit2, Key.Digit3, Key.Digit4, Key.Digit5, Key.Digit6, Key.Digit7, Key.Digit8, Key.Digit9 };

		private readonly HashSet<ushort> sentKeys = new HashSet<ushort>();
		private readonly HashSet<int> sentButtons = new HashSet<int>();
		// Keys still held when a Minecraft screen closed: ignored until let go, or E (which closed the
		// inventory) would open it straight away again.
		private readonly HashSet<ushort> swallowed = new HashSet<ushort>();
		private readonly List<char> text = new List<char>();
		private bool textHooked;
		private enum Right { Minecraft, Interact, Use, Secondary }
		private Right right;            // what this right-button press is, decided when it went down
		private bool leftIsGames;       // this left-button press is the held object's tertiary use
		private bool useDown;           // the held object's use is pressed (released with the button)
		private bool gameActionsOff;
		private bool screenWasOpen;

		// Minecraft's cursor while one of its screens is open (overlay pixels).
		public int CursorX, CursorY;

		/// <summary>
		/// Once a frame, after the game's Update. route: Minecraft has the player and nothing of the
		/// game's own (its menu, chat, terminal) wants the keyboard. screenOpen: a Minecraft screen is open.
		/// </summary>
		public void Frame(bool route, bool screenOpen, PlayerControllerB p, int viewportW, int viewportH)
		{
			var kb = Keyboard.current;
			var mouse = Mouse.current;
			HookText();
			if (!route || kb == null || mouse == null || p == null)
			{
				EndUse(p);
				ReleaseAll();
				SetGameActions(true);
				ScreenOpen = false;
				text.Clear();
				return;
			}
			var link = SharedLink.Instance;

			ScreenOpen = screenOpen;
			if (screenOpen)
			{
				if (!screenWasOpen)
				{
					CursorX = viewportW / 2;
					CursorY = viewportH / 2;
				}
				screenWasOpen = true;
				EndUse(p);
				SetGameActions(false);
				ForwardEverything(kb, mouse, viewportW, viewportH);
				return;
			}
			if (screenWasOpen)
			{
				screenWasOpen = false;
				swallowed.UnionWith(sentKeys);
				ReleaseAll();
			}
			SetGameActions(true);
			text.Clear();

			foreach (var (key, scancode) in Movement)
			{
				SetKey(scancode, kb[key].isPressed);
			}
			SetKey(InventoryKey, kb.eKey.isPressed);
			for (int i = 0; i < Digits.Length; i++)
			{
				SetKey((ushort)(30 + i), kb[Digits[i]].isPressed);
			}
			SetKey(DropKey, kb.qKey.isPressed);
			SetKey(PerspectiveKey, kb.f5Key.isPressed);
			SetKey(ChatKey, kb.tKey.isPressed);
			SetKey(CommandKey, kb.slashKey.isPressed);

			// The buttons: who each press belongs to is decided when it goes down.
			bool sneaking = kb.leftShiftKey.isPressed;
			bool holding = InventoryBridge.HoldingSelected(p);
			if (mouse.leftButton.wasPressedThisFrame)
			{
				leftIsGames = holding && sneaking;
				if (leftIsGames)
				{
					InventoryBridge.UseSecondary(p, true);
				}
			}
			SetButton(1, !leftIsGames && mouse.leftButton.isPressed);
			if (mouse.rightButton.wasPressedThisFrame)
			{
				EndUse(p);
				if (holding && UnlocksDoorAhead(p))
				{
					// A key or a lock picker on a locked door: the item's use is what opens it (the door's own interact only says "locked").
					right = Right.Use;
					useDown = true;
					InventoryBridge.Use(p, true);
				}
				else if (LooksAtSomethingOfTheGames(p))
				{
					right = Right.Interact;
				}
				else if (holding && sneaking)
				{
					right = Right.Secondary;
					InventoryBridge.UseSecondary(p, false);
				}
				else if (holding)
				{
					right = Right.Use;
					useDown = true;
					InventoryBridge.Use(p, true);
				}
				else
				{
					right = Right.Minecraft;
				}
			}
			if (!mouse.rightButton.isPressed)
			{
				EndUse(p);
			}
			SetButton(3, right == Right.Minecraft && mouse.rightButton.isPressed);
			float wheel = mouse.scroll.ReadValue().y;
			if (wheel != 0f)
			{
				link.PushInput(Proto.InScroll, 0, wheel > 0 ? 120 : -120);
			}
		}

		/// <summary>The held object's use, let go (for objects used while held down: the jetpack, spray paint...).</summary>
		private void EndUse(PlayerControllerB p)
		{
			if (!useDown)
			{
				return;
			}
			useDown = false;
			if (p != null)
			{
				InventoryBridge.Use(p, false);
			}
		}

		/// <summary>
		/// Holding something that opens locked doors (a key, a lock picker, or a mod's kind of either) and
		/// looking at a locked one, the way those items look for it: 3 m along the camera, the door layers.
		/// </summary>
		public static bool UnlocksDoorAhead(PlayerControllerB p)
		{
			var held = p.currentlyHeldObjectServer;
			if (!(held is KeyItem) && !(held is LockPicker) || p.gameplayCamera == null)
			{
				return false;
			}
			var cam = p.gameplayCamera.transform;
			if (!Physics.Raycast(new Ray(cam.position, cam.forward), out RaycastHit hit, 3f, 2816))
			{
				return false;
			}
			var door = hit.transform.GetComponent<DoorLock>();
			if (door == null)
			{
				var pointer = hit.transform.GetComponent<TriggerPointToDoor>();
				door = pointer != null ? pointer.pointToDoor : null;
			}
			return door != null && door.isLocked && !door.isPickingLock;
		}

		/// <summary>The game's crosshair tip is up (a door, a button, scrap to pick up...): the right button is its interact.</summary>
		public static bool LooksAtSomethingOfTheGames(PlayerControllerB p)
		{
			// Ladders are Minecraft's to climb (World.LadderExporter): walking into one, not interacting with it.
			if (p.hoveringOverTrigger != null && p.hoveringOverTrigger.isLadder)
			{
				return false;
			}
			return p.hoveringOverTrigger != null || (p.cursorTip != null && !string.IsNullOrEmpty(p.cursorTip.text));
		}

		/// <summary>A Minecraft screen: every key, typed text, the buttons, the wheel and a cursor.</summary>
		private void ForwardEverything(Keyboard kb, Mouse mouse, int viewportW, int viewportH)
		{
			var link = SharedLink.Instance;
			foreach (var kv in KeyMap.ToSdl)
			{
				var control = kb[kv.Key];
				if (control == null)
				{
					continue;
				}
				SetKey(kv.Value, control.isPressed);
			}
			foreach (char c in text)
			{
				link.PushInput(Proto.InText, 0, c);
			}
			text.Clear();
			Vector2 delta = mouse.delta.ReadValue();
			if (delta.sqrMagnitude > 0f)
			{
				CursorX = Mathf.Clamp(CursorX + Mathf.RoundToInt(delta.x), 0, viewportW - 1);
				CursorY = Mathf.Clamp(CursorY - Mathf.RoundToInt(delta.y), 0, viewportH - 1);
				link.PushInput(Proto.InCursor, 0, CursorX, CursorY);
			}
			SetButton(1, mouse.leftButton.isPressed);
			SetButton(3, mouse.rightButton.isPressed);
			SetButton(2, mouse.middleButton.isPressed);
			float wheel = mouse.scroll.ReadValue().y;
			if (wheel != 0f)
			{
				link.PushInput(Proto.InScroll, 0, wheel > 0 ? 120 : -120);
			}
		}

		/// <summary>The game's own input actions off while a Minecraft screen has the keyboard (its Esc menu included).</summary>
		private void SetGameActions(bool on)
		{
			if (on == !gameActionsOff || InputSystem.actions == null)
			{
				return;
			}
			gameActionsOff = !on;
			if (on)
			{
				InputSystem.actions.Enable();
			}
			else
			{
				InputSystem.actions.Disable();
			}
		}

		private void HookText()
		{
			if (textHooked || Keyboard.current == null)
			{
				return;
			}
			textHooked = true;
			Keyboard.current.onTextInput += c =>
			{
				if (ScreenOpen)
				{
					text.Add(c);
				}
			};
		}

		private void SetKey(ushort scancode, bool down)
		{
			if (swallowed.Count > 0 && swallowed.Contains(scancode))
			{
				if (!down)
				{
					swallowed.Remove(scancode);
				}
				return;
			}
			if (down && sentKeys.Add(scancode))
			{
				SharedLink.Instance.PushInput(Proto.InKey, scancode, 1);
			}
			else if (!down && sentKeys.Remove(scancode))
			{
				SharedLink.Instance.PushInput(Proto.InKey, scancode, 0);
			}
		}

		private void SetButton(int sdl, bool down)
		{
			if (down && sentButtons.Add(sdl))
			{
				SharedLink.Instance.PushInput(Proto.InMouseButton, (ushort)sdl, 1);
			}
			else if (!down && sentButtons.Remove(sdl))
			{
				SharedLink.Instance.PushInput(Proto.InMouseButton, (ushort)sdl, 0);
			}
		}

		public void ReleaseAll()
		{
			if (sentKeys.Count == 0 && sentButtons.Count == 0)
			{
				return;
			}
			sentKeys.Clear();
			sentButtons.Clear();
			SharedLink.Instance.PushInput(Proto.InReleaseAll, 0);
		}
	}
}
