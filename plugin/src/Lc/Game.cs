using GameNetcodeStuff;
using Unity.Netcode;
using UnityEngine;

namespace LethalCraft.Lc
{
	/// <summary>
	/// What Lethal Company is doing right now, from its own singletons. Nothing is cached: the main
	/// menu has no StartOfRound, and the in-game scene is loaded and unloaded around it.
	/// </summary>
	internal static class Game
	{
		/// <summary>The local player's controller, or null in the main menu / while connecting.</summary>
		public static PlayerControllerB Player => GameNetworkManager.Instance != null ? GameNetworkManager.Instance.localPlayerController : null;

		/// <summary>A game session is loaded with a local player in it (alive or dead), not the main menu.</summary>
		public static bool InSession
		{
			get
			{
				var round = StartOfRound.Instance;
				return round != null && Player != null && round.allPlayerScripts != null && !round.newGameIsLoading;
			}
		}

		/// <summary>The local player exists, is spawned and alive, and no cutscene or loading owns the screen.</summary>
		public static bool InPlay
		{
			get
			{
				var p = Player;
				return InSession && p.isPlayerControlled && !p.isPlayerDead && !StartOfRound.Instance.firingPlayersCutsceneRunning;
			}
		}

		/// <summary>
		/// The game is generating a level or switching scenes. Moons without a facility (the Company:
		/// no enemies or scrap) generate no dungeon, so dungeonCompletedGenerating never comes true there.
		/// </summary>
		public static bool Loading
		{
			get
			{
				var round = StartOfRound.Instance;
				return round == null || round.newGameIsLoading
					|| (!round.inShipPhase && round.shipHasLanded && round.currentLevel != null && round.currentLevel.spawnEnemiesAndScrap
						&& RoundManager.Instance != null && !RoundManager.Instance.dungeonCompletedGenerating);
			}
		}

		/// <summary>A Lethal Company menu, the terminal or chat owns the keyboard and mouse.</summary>
		public static bool MenuOpen
		{
			get
			{
				var p = Player;
				if (p == null)
				{
					return true;
				}
				return (p.quickMenuManager != null && p.quickMenuManager.isMenuOpen) || p.isTypingChat || p.inTerminalMenu || p.inSpecialMenu;
			}
		}

		public static bool OnShip => Player != null && Player.isInElevator;

		public static bool InFactory => Player != null && Player.isInsideFactory;

		/// <summary>The ship is in orbit (no moon loaded).</summary>
		public static bool InOrbit => StartOfRound.Instance != null && StartOfRound.Instance.inShipPhase;

		public static string LevelName => StartOfRound.Instance != null && StartOfRound.Instance.currentLevel != null ? StartOfRound.Instance.currentLevel.PlanetName : "";

		public static bool Multiplayer => NetworkManager.Singleton != null && NetworkManager.Singleton.IsListening && NetworkManager.Singleton.ConnectedClientsIds.Count > 1;

		public static bool HasAuthority => NetworkManager.Singleton != null && NetworkManager.Singleton.IsServer;

		public static Camera MainCamera => Player != null && Player.gameplayCamera != null ? Player.gameplayCamera : Camera.main;
	}
}
