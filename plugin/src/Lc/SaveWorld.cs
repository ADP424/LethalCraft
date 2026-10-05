using HarmonyLib;

namespace LethalCraft.Lc
{
	/// <summary>
	/// Which Minecraft world belongs to the game's save file. The Minecraft player (inventory, health,
	/// hunger, what was built) is part of the save: continuing a save continues its Minecraft world, a
	/// new save (or one deleted and started over, or a run ended by being fired) gets a fresh one.
	///
	/// The link between the two is a random key kept in the save file itself, written whenever the
	/// game saves, so it goes wherever the save goes and dies with it: deleting the file deletes the
	/// key, and the next game on that slot makes a new one.
	/// </summary>
	internal static class SaveWorld
	{
		private const string Key = "LethalCraftWorld";

		private static string file;
		private static string key = "";

		/// <summary>The current save's key (empty in the main menu, or as a guest: the host's world is theirs to choose).</summary>
		public static string Current
		{
			get
			{
				var net = GameNetworkManager.Instance;
				if (net == null || !Game.InSession || !net.isHostingGame)
				{
					return key;
				}
				string saveFile = net.currentSaveFileName;
				if (saveFile != file)
				{
					file = saveFile;
					key = Load(saveFile);
				}
				return key;
			}
		}

		private static string Load(string saveFile)
		{
			try
			{
				string existing = ES3.Load<string>(Key, saveFile, "");
				if (!string.IsNullOrEmpty(existing))
				{
					Log.Info($"save {saveFile}: its Minecraft world is {existing}");
					return existing;
				}
			}
			catch (System.Exception e)
			{
				Log.Warn($"save {saveFile}: couldn't read its Minecraft world key ({e.Message})");
			}
			string made = NewKey();
			Log.Info($"save {saveFile}: new, so a new Minecraft world {made} (kept with the save when the game next saves)");
			return made;
		}

		private static string NewKey() => System.Guid.NewGuid().ToString("N").Substring(0, 12);

		/// <summary>The game saved: the key goes into the file with everything else.</summary>
		internal static void Saved(GameNetworkManager net)
		{
			if (!net.isHostingGame || string.IsNullOrEmpty(key) || net.currentSaveFileName != file)
			{
				return;
			}
			try
			{
				ES3.Save<string>(Key, key, file);
			}
			catch (System.Exception e)
			{
				Log.Warn($"save {file}: couldn't write its Minecraft world key ({e.Message})");
			}
		}

		/// <summary>Fired: the save starts over from day one, and so does its Minecraft world.</summary>
		internal static void RunReset(GameNetworkManager net)
		{
			if (!net.isHostingGame)
			{
				return;
			}
			file = net.currentSaveFileName;
			key = NewKey();
			Log.Info($"save {file}: the run starts over, and with it a new Minecraft world {key}");
			Saved(net);
		}
	}

	[HarmonyPatch(typeof(GameNetworkManager))]
	internal static class SaveWorldPatches
	{
		[HarmonyPostfix, HarmonyPatch(nameof(GameNetworkManager.SaveGame))]
		private static void SaveGame(GameNetworkManager __instance) => SaveWorld.Saved(__instance);

		[HarmonyPostfix, HarmonyPatch(nameof(GameNetworkManager.ResetSavedGameValues))]
		private static void ResetSavedGameValues(GameNetworkManager __instance) => SaveWorld.RunReset(__instance);
	}
}
