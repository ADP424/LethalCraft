using GameNetcodeStuff;
using HarmonyLib;
using UnityEngine;

namespace LethalCraft.Patches
{
	/// <summary>
	/// Minecraft owns the player's health: every point the game would take off the local player is
	/// cancelled and sent to Minecraft (Combat.Combat). The game's attack logic still runs; only the
	/// player's side of it changes.
	/// </summary>
	[HarmonyPatch(typeof(PlayerControllerB))]
	internal static class CombatPatches
	{
		private static bool Ours(PlayerControllerB p) =>
			Combat.Combat.McOwnsHealth && !Combat.Combat.Bypass && p != null && p == Lc.Game.Player && p.IsOwner && !p.isPlayerDead;

		[HarmonyPrefix, HarmonyPatch(nameof(PlayerControllerB.DamagePlayer))]
		private static bool DamagePlayer(PlayerControllerB __instance, int damageNumber, CauseOfDeath causeOfDeath, int deathAnimation, bool fallDamage, Vector3 force)
		{
			if (!Ours(__instance) || !__instance.AllowPlayerDeath())
			{
				return true;
			}
			// Falls are Minecraft's own (it takes them by its rules); everything else is a hit.
			if (!fallDamage && damageNumber > 0)
			{
				Combat.Combat.Instance.PlayerHurt(__instance, damageNumber, causeOfDeath, deathAnimation, force);
			}
			return false;
		}

		// Air underwater is Minecraft's (its bubbles, its drowning): the game's oxygen timer never runs down.
		[HarmonyPostfix, HarmonyPatch("Update")]
		private static void NoGameDrowning(PlayerControllerB __instance)
		{
			if (Ours(__instance) && StartOfRound.Instance != null)
			{
				StartOfRound.Instance.drowningTimer = 1f;
			}
		}

		[HarmonyPrefix, HarmonyPatch(nameof(PlayerControllerB.KillPlayer))]
		private static bool KillPlayer(PlayerControllerB __instance, Vector3 bodyVelocity, bool spawnBody, CauseOfDeath causeOfDeath, int deathAnimation, Vector3 positionOffset,
			bool setOverrideDropItems)
		{
			if (!Ours(__instance) || !__instance.AllowPlayerDeath())
			{
				return true;
			}
			Combat.Combat.Instance.PlayerKilled(__instance, bodyVelocity, spawnBody, causeOfDeath, deathAnimation, positionOffset, setOverrideDropItems);
			// Left behind by the ship: no totem saves you from that, the game's death goes ahead now.
			return causeOfDeath == CauseOfDeath.Abandoned;
		}
	}
}
