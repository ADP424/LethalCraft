using BepInEx;
using HarmonyLib;
using UnityEngine;

// Compiled against a publicized Assembly-CSharp (tools\Publicizer); Mono honours this at runtime.
[assembly: System.Runtime.CompilerServices.IgnoresAccessChecksTo("Assembly-CSharp")]

namespace System.Runtime.CompilerServices
{
	[AttributeUsage(AttributeTargets.Assembly, AllowMultiple = true)]
	internal sealed class IgnoresAccessChecksToAttribute : Attribute
	{
		public IgnoresAccessChecksToAttribute(string assemblyName) => AssemblyName = assemblyName;

		public string AssemblyName { get; }
	}
}

namespace LethalCraft
{
	/// <summary>
	/// LethalCraft: play Lethal Company as a Minecraft player. A hidden Minecraft (with the LethalCraft
	/// Fabric mod) runs the player's movement, inventory, combat and blocks; this plugin feeds it the
	/// game's geometry, enemies and input over shared memory and draws what Minecraft sends back
	/// inside the game's frame. Neither game is rewritten: each runs its own logic, this translates.
	/// </summary>
	[BepInPlugin(Guid, "LethalCraft", Version)]
	public sealed class Plugin : BaseUnityPlugin
	{
		public const string Guid = "dev.lethalcraft";
		public const string Version = "0.0.1";

		internal static Plugin Instance { get; private set; }
		internal static Harmony Harmony { get; private set; }

		private void Awake()
		{
			Instance = this;
			Log.Init(Logger);
			LethalCraft.Config.Bind(base.Config);
			Log.Info($"LethalCraft {Version} loading");

			if (!Link.SharedLink.Instance.Create())
			{
				Log.Error("couldn't create the shared memory: LethalCraft stays off");
				return;
			}

			Harmony = new Harmony(Guid);
			try
			{
				Harmony.PatchAll(typeof(Plugin).Assembly);
			}
			catch (System.Exception e)
			{
				Log.Error($"patching failed (a game update may have changed what LethalCraft hooks): {e}");
			}

			var host = new GameObject("LethalCraft");
			DontDestroyOnLoad(host);
			host.hideFlags = HideFlags.HideAndDontSave;
			host.AddComponent<Driver>();

			Launcher.StartMinecraft();
		}
	}
}
