using BepInEx.Configuration;

namespace LethalCraft
{
	/// <summary>BepInEx/config/dev.lethalcraft.cfg.</summary>
	internal static class Config
	{
		private static ConfigEntry<bool> startWithGame;
		private static ConfigEntry<string> launcher;
		private static ConfigEntry<string> launcherArgs;
		private static ConfigEntry<float> blocksPerMeter;
		private static ConfigEntry<bool> diagnostics;
		private static ConfigEntry<bool> showMinecraftWindow;
		private static ConfigEntry<float> damageToEnemies;
		private static ConfigEntry<float> damageToPlayer;
		private static ConfigEntry<bool> shareWorld;

		public static bool StartWithGame => startWithGame.Value;
		public static string Launcher => launcher.Value;
		public static string LauncherArgs => launcherArgs.Value;
		/// <summary>Minecraft blocks per Unity unit (metre).</summary>
		public static float BlocksPerMeter => blocksPerMeter.Value;
		public static bool Diagnostics => diagnostics?.Value ?? false;
		public static bool ShowMinecraftWindow => showMinecraftWindow?.Value ?? false;
		/// <summary>The game's hit force per point of Minecraft damage.</summary>
		public static float DamageToEnemies => damageToEnemies.Value;
		/// <summary>Multiplier on the game's damage before Minecraft takes it (100 game health = 20 Minecraft).</summary>
		public static float DamageToPlayer => damageToPlayer.Value;
		/// <summary>Multiplayer: the lobby plays in one Minecraft world, the host's.</summary>
		public static bool ShareWorld => shareWorld?.Value ?? true;

		public static void Bind(ConfigFile file)
		{
			startWithGame = file.Bind("Minecraft", "StartWithGame", true,
				"Start Minecraft (hidden) when Lethal Company starts, and close it when the game closes. Off: start the LethalCraft Minecraft instance yourself.");
			launcher = file.Bind("Minecraft", "Launcher", "",
				"Empty: Prism Launcher from its usual install folder. Or the full path of your launcher (Prism, MultiMC, or a .bat file).");
			launcherArgs = file.Bind("Minecraft", "LauncherArguments", "--launch LethalCraft",
				"What to pass to the launcher. For Prism: --launch and the name of the LethalCraft instance.");
			blocksPerMeter = file.Bind("World", "BlocksPerMeter", 0.72f,
				"Minecraft blocks per Lethal Company metre. 0.72 makes the 2.5 m Lethal Company player as tall as a 1.8 block Minecraft one.");
			diagnostics = file.Bind("Debug", "Diagnostics", false,
				"Detailed timing and link logs (several lines a second).");
			damageToEnemies = file.Bind("Combat", "DamageToEnemies", 0.2f,
				"The game's hit force per point of Minecraft damage (a shovel hit is 1). 0.2: an iron sword (6) is a shovel hit, a fist (1) needs five punches.");
			damageToPlayer = file.Bind("Combat", "DamageToPlayer", 1f,
				"Multiplier on the game's damage to the player before Minecraft takes it. 1: the game's 100 health is Minecraft's 20 (10 hearts).");
			shareWorld = file.Bind("Multiplayer", "ShareWorld", true,
				"Play a Lethal Company lobby in one Minecraft world, the host's: hosting, your Minecraft world opens to the lobby when someone joins; as a guest, your Minecraft joins the host's. Off: everyone stays in their own Minecraft world.");
			showMinecraftWindow = file.Bind("Debug", "ShowMinecraftWindow", false,
				"Keep Minecraft's own window on screen (it is normally hidden), to see what it is doing. Read when Lethal Company starts.");
		}
	}
}
