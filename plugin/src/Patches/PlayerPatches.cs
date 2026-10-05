using System.Collections.Generic;
using System.Reflection;
using System.Reflection.Emit;
using GameNetcodeStuff;
using HarmonyLib;
using LethalCraft.Player;
using UnityEngine;
using UnityEngine.InputSystem;

namespace LethalCraft.Patches
{
	/// <summary>
	/// Lethal Company's movement stands down while Minecraft drives the player. PlayerControllerB.Update
	/// is one long method (input, stamina, animation, forces, gravity, a single
	/// CharacterController.Move), so it isn't skipped: its move input is zeroed, that one call is
	/// replaced, and a postfix puts the body where Minecraft says and the animator where Minecraft's
	/// movement is.
	/// </summary>
	[HarmonyPatch(typeof(PlayerControllerB), "Update")]
	internal static class PlayerUpdatePatch
	{
		private static readonly MethodInfo OriginalMove = AccessTools.Method(typeof(CharacterController), nameof(CharacterController.Move), new[] { typeof(Vector3) });
		private static readonly MethodInfo Replacement = AccessTools.Method(typeof(PlayerUpdatePatch), nameof(Move));

		private static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
		{
			int replaced = 0;
			foreach (var ins in instructions)
			{
				if ((ins.opcode == OpCodes.Callvirt || ins.opcode == OpCodes.Call) && ins.operand is MethodInfo m && m == OriginalMove)
				{
					replaced++;
					ins.opcode = OpCodes.Call;
					ins.operand = Replacement;
					yield return ins;
					continue;
				}
				yield return ins;
			}
			if (replaced == 1)
			{
				Log.Info("patched PlayerControllerB.Update: its CharacterController.Move can be handed to Minecraft");
			}
			else
			{
				Log.Error($"PlayerControllerB.Update has {replaced} CharacterController.Move calls, expected 1 (a game update?): Minecraft cannot drive the player");
			}
		}

		/// <summary>The game's own move: untouched unless Minecraft has the player, then only enough contact for the controller's grounded state.</summary>
		public static CollisionFlags Move(CharacterController controller, Vector3 motion)
		{
			if (!Puppet.Active)
			{
				return controller.Move(motion);
			}
			return controller.Move(Puppet.OnGround ? Vector3.down * 0.05f : Vector3.zero);
		}

		private static void Prefix(PlayerControllerB __instance, out bool __state)
		{
			__state = false;
			if (Puppet.Active && __instance == Lc.Game.Player)
			{
				// Lethal Company's WASD, sprint and stamina are not the player's movement now: Minecraft's keys are.
				__state = __instance.disableMoveInput;
				__instance.disableMoveInput = true;
				Puppet.Instance.BeforeUpdate(__instance);
			}
		}

		private static void Postfix(PlayerControllerB __instance, bool __state)
		{
			if (Puppet.Active && __instance == Lc.Game.Player)
			{
				__instance.disableMoveInput = __state;
				Puppet.Instance.AfterUpdate(__instance);
			}
		}
	}

	/// <summary>Lethal Company's jump and crouch keys do nothing while Minecraft drives: Minecraft's jump and sneak happen, and Lethal Company follows.</summary>
	[HarmonyPatch(typeof(PlayerControllerB))]
	internal static class PlayerActionPatches
	{
		[HarmonyPrefix, HarmonyPatch("Jump_performed")]
		private static bool Jump(PlayerControllerB __instance, InputAction.CallbackContext context) => !(Puppet.Active && __instance == Lc.Game.Player);

		[HarmonyPrefix, HarmonyPatch("Crouch_performed")]
		private static bool Crouch(PlayerControllerB __instance, InputAction.CallbackContext context) => !(Puppet.Active && __instance == Lc.Game.Player);
	}

	/// <summary>Minecraft's mouse look and camera while Minecraft drives (Puppet.Look, Puppet.Camera).</summary>
	[HarmonyPatch(typeof(PlayerControllerB))]
	internal static class PlayerCameraPatches
	{
		[HarmonyPrefix, HarmonyPatch("PlayerLookInput")]
		private static bool LookInput(PlayerControllerB __instance) => !(Puppet.Active && __instance == Lc.Game.Player && Puppet.Instance.Look(__instance));

		[HarmonyPostfix, HarmonyPatch("LateUpdate")]
		private static void LateUpdate(PlayerControllerB __instance)
		{
			if (Puppet.Active && __instance == Lc.Game.Player)
			{
				Puppet.Instance.Camera(__instance);
			}
		}
	}

	/// <summary>
	/// The game moving the player itself (entrances, ladders' ends, the ship's "everyone back aboard"):
	/// Minecraft lets go at once, before this frame's puppet postfix would put the body back where
	/// Minecraft had it, and is resynced to the new place.
	/// </summary>
	[HarmonyPatch(typeof(PlayerControllerB), nameof(PlayerControllerB.TeleportPlayer))]
	internal static class PlayerTeleportPatch
	{
		private static void Postfix(PlayerControllerB __instance)
		{
			if (__instance == Lc.Game.Player && __instance.isPlayerControlled && !__instance.isPlayerDead)
			{
				if (Puppet.Active)
				{
					Puppet.Instance.Release();
				}
				Puppet.TeleportedByGame = true;
			}
		}
	}

	/// <summary>
	/// The player's own sounds are Minecraft's while it drives (its footsteps, falls, hurt): Lethal
	/// Company's footstep and landing sounds for the local player are skipped. Their noise (what
	/// monsters hear) is made before these are called, and stays.
	/// </summary>
	[HarmonyPatch(typeof(PlayerControllerB))]
	internal static class PlayerSoundPatches
	{
		private static bool Local(PlayerControllerB p) => Puppet.Active && p == Lc.Game.Player;

		[HarmonyPrefix, HarmonyPatch(nameof(PlayerControllerB.PlayFootstepSound))]
		private static bool Footstep(PlayerControllerB __instance) => !Local(__instance);

		[HarmonyPrefix, HarmonyPatch("PlayerHitGroundEffects")]
		private static bool HitGround(PlayerControllerB __instance) => !Local(__instance);
	}
}
