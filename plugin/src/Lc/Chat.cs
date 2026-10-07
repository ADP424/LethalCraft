using System.Text;
using System.Text.RegularExpressions;
using HarmonyLib;
using LethalCraft.Link;
using LethalCraft.Patches;

namespace LethalCraft.Lc
{
	/// <summary>
	/// One chat, Minecraft's to look at and type in, the game's to carry. A line sent from Minecraft's
	/// chat (EV_CHAT_CHAR / EV_CHAT_END) goes to the lobby through the game's own chat
	/// (AddTextToChatOnServer: its range, walkie-talkies, the dead and the living apart), in pieces under
	/// its 50-character limit; every line the game's chat shows (players' and its own notices) is shown
	/// in Minecraft's chat instead (IN_CHAT, CHAT_SHOW). The game's chat box itself is hidden while
	/// Minecraft drives (Driver), and its key is off (Patches below).
	/// </summary>
	internal static class Chat
	{
		private const int GameLineLimit = 49; // the game's server drops lines of 50 or more
		private static readonly StringBuilder typed = new StringBuilder();
		private static readonly Regex Tags = new Regex("<[^>]*>");

		/// <summary>A unit of the line being typed in Minecraft's chat.</summary>
		public static void OnChar(uint unit)
		{
			if (typed.Length < 512)
			{
				typed.Append((char)unit);
			}
		}

		/// <summary>The line is complete: to the lobby, the game's way.</summary>
		public static void OnEnd()
		{
			string line = typed.ToString().Trim();
			typed.Length = 0;
			var hud = HUDManager.Instance;
			var p = Game.Player;
			if (line.Length == 0 || hud == null || p == null)
			{
				return;
			}
			for (int i = 0; i < line.Length; i += GameLineLimit)
			{
				string piece = line.Substring(i, System.Math.Min(GameLineLimit, line.Length - i));
				hud.AddTextToChatOnServer(piece, (int)p.playerClientId);
			}
		}

		/// <summary>A line the game's chat just added, for Minecraft's chat.</summary>
		public static void Show(string message, string name)
		{
			var round = StartOfRound.Instance;
			var sb = new StringBuilder(message ?? "");
			if (round != null && round.allPlayerScripts != null)
			{
				for (int i = 0; i < round.allPlayerScripts.Length && i < 4; i++)
				{
					sb.Replace($"[playerNum{i}]", round.allPlayerScripts[i].playerUsername);
				}
			}
			bool notice = string.IsNullOrEmpty(name);
			string text = Tags.Replace(sb.ToString(), "");
			string line = notice ? text : $"<{name}> {text}";
			var link = SharedLink.Instance;
			foreach (char c in line)
			{
				link.PushInput(Proto.InChat, Proto.ChatChar, c);
			}
			link.PushInput(Proto.InChat, Proto.ChatShow, notice ? 1 : 0);
		}
	}

	[HarmonyPatch(typeof(HUDManager))]
	internal static class ChatPatches
	{
		// The game's chat box doesn't open while Minecraft drives: Minecraft's chat (T, /) is the chat.
		[HarmonyPrefix, HarmonyPatch("EnableChat_performed")]
		private static bool NoGameChatBox() => !InputPatches.Driving(Game.Player);

		// Whatever the game's chat adds (it skips repeats and the sender's own echo), Minecraft's chat shows too.
		[HarmonyPrefix, HarmonyPatch("AddChatMessage")]
		private static void Before(HUDManager __instance, out string __state)
		{
			var history = __instance.ChatMessageHistory;
			__state = history != null && history.Count > 0 ? history[history.Count - 1] : null;
		}

		[HarmonyPostfix, HarmonyPatch("AddChatMessage")]
		private static void After(HUDManager __instance, string chatMessage, string nameOfUserWhoTyped, string __state)
		{
			var history = __instance.ChatMessageHistory;
			string last = history != null && history.Count > 0 ? history[history.Count - 1] : null;
			if (last != null && !ReferenceEquals(last, __state))
			{
				Chat.Show(chatMessage, nameOfUserWhoTyped);
			}
		}
	}
}
