using System.Collections.Generic;
using GameNetcodeStuff;
using LethalCraft.Link;
using Unity.Collections;
using Unity.Netcode;
using UnityEngine;

namespace LethalCraft.Lc
{
	/// <summary>
	/// Lethal Company multiplayer with one Minecraft world for the whole lobby: the host's.
	///  - The host's Minecraft opens its world to friends once anyone else is in the lobby (e4mc gives
	///    it a public address; without e4mc, its LAN address), and the host tells the lobby the address
	///    and its blocks-per-metre scale (a Netcode named message, so Steam and LAN lobbies alike).
	///  - Everyone else's Minecraft joins that address and uses that scale. Moons map to the same slots
	///    for everyone already (by level id), and the ship to slot 0.
	///  - Each player says whether their Minecraft is in the lobby's world; for those who are, the
	///    others draw their Minecraft body (from the shared world) instead of their Lethal Company one.
	/// The Minecraft side of all this is LobbyWorld (sharing, joining, reporting where it is).
	/// </summary>
	internal static class Lobby
	{
		private const string WorldMessage = "LethalCraft.World"; // host -> all: address, scale
		private const string InMessage = "LethalCraft.In";       // client -> host: our Minecraft is in the lobby's world
		private const string InsMessage = "LethalCraft.Ins";     // host -> all: who is
		private const float SendEvery = 2f;

		private static CustomMessagingManager registered;
		private static string hostWorld = "";
		private static float hostScale;
		private static bool sharing;
		private static float sendTimer;
		private static uint mcState;
		private static string shareLink = "", friendWorld = "";
		private static uint lastNoted = uint.MaxValue;
		private static readonly Dictionary<ulong, bool> ins = new Dictionary<ulong, bool>();
		private static readonly Dictionary<PlayerControllerB, List<Renderer>> hidden = new Dictionary<PlayerControllerB, List<Renderer>>();
		private static readonly List<PlayerControllerB> gone = new List<PlayerControllerB>();

		/// <summary>Our Minecraft is in the lobby's world: the host sharing it, or a guest in it.</summary>
		public static bool InLobbyWorld { get; private set; }

		/// <summary>Blocks per metre while in the host's world (0: our own config's).</summary>
		public static float Scale { get; private set; }

		private static NetworkManager Net => NetworkManager.Singleton != null && NetworkManager.Singleton.IsListening ? NetworkManager.Singleton : null;

		/// <summary>Once a frame: tell Minecraft what the lobby wants, the lobby where Minecraft is, and show the others' Minecraft bodies.</summary>
		public static void Frame(SharedLink link, bool session, float dt)
		{
			var net = session ? Net : null;
			if (net == null)
			{
				Reset(link);
				return;
			}
			Register(net);
			bool host = net.IsServer;
			if (host && !sharing && Config.ShareWorld && net.ConnectedClientsIds.Count > 1)
			{
				sharing = true; // stays open for the rest of the session: friends who leave can come back
				Log.Info("lobby: someone joined; opening our Minecraft world to the lobby");
			}
			string join = !host && Config.ShareWorld ? hostWorld : "";
			link.WriteMpRequest(host && sharing ? Proto.MpShare : join.Length > 0 ? Proto.MpJoin : 0u, join);
			Scale = join.Length > 0 && hostScale > 0f ? hostScale : 0f;

			mcState = link.ReadMpState(out shareLink, out friendWorld);
			InLobbyWorld = host
				? sharing && mcState == Proto.MpsSharing && shareLink.Length > 0
				: join.Length > 0 && mcState == Proto.MpsInFriend && string.Equals(Clean(friendWorld), Clean(join), System.StringComparison.OrdinalIgnoreCase);

			sendTimer -= dt;
			if (sendTimer <= 0f)
			{
				sendTimer = SendEvery;
				Send(net, host);
			}
			Note(host, join);
			Bodies(net);
		}

		private static string Clean(string a) => (a ?? "").Trim().Replace("https://", "").Replace("http://", "").TrimEnd('/');

		private static void Reset(SharedLink link)
		{
			if (sharing || hostWorld.Length > 0 || InLobbyWorld)
			{
				link.WriteMpRequest(0u, "");
			}
			registered = null;
			hostWorld = "";
			hostScale = 0f;
			sharing = false;
			InLobbyWorld = false;
			Scale = 0f;
			ins.Clear();
			ShowAll();
		}

		// ---- messages ------------------------------------------------------------------------------

		private static void Register(NetworkManager net)
		{
			var cmm = net.CustomMessagingManager;
			if (cmm == null || cmm == registered)
			{
				return;
			}
			registered = cmm;
			cmm.RegisterNamedMessageHandler(WorldMessage, (sender, reader) =>
			{
				reader.ReadValueSafe(out string world, false);
				reader.ReadValueSafe(out float scale);
				if (world != hostWorld)
				{
					Log.Info(world.Length > 0 ? $"lobby: the host's Minecraft world is at {world}" : "lobby: the host's Minecraft world isn't open");
				}
				hostWorld = world ?? "";
				hostScale = scale;
			});
			cmm.RegisterNamedMessageHandler(InMessage, (sender, reader) =>
			{
				reader.ReadValueSafe(out bool inWorld);
				ins[sender] = inWorld;
			});
			cmm.RegisterNamedMessageHandler(InsMessage, (sender, reader) =>
			{
				reader.ReadValueSafe(out int count);
				ins.Clear();
				for (int i = 0; i < count && i < 64; i++)
				{
					reader.ReadValueSafe(out ulong client);
					reader.ReadValueSafe(out bool inWorld);
					ins[client] = inWorld;
				}
			});
		}

		private static void Send(NetworkManager net, bool host)
		{
			var cmm = net.CustomMessagingManager;
			if (cmm == null)
			{
				return;
			}
			if (host)
			{
				ins[net.LocalClientId] = InLobbyWorld;
				string world = InLobbyWorld ? shareLink : "";
				using (var w = new FastBufferWriter(256, Allocator.Temp))
				{
					w.WriteValueSafe(world, false);
					w.WriteValueSafe(Config.BlocksPerMeter);
					cmm.SendNamedMessageToAll(WorldMessage, w, NetworkDelivery.Reliable);
				}
				using (var w = new FastBufferWriter(16 + ins.Count * 9, Allocator.Temp))
				{
					w.WriteValueSafe(ins.Count);
					foreach (var kv in ins)
					{
						w.WriteValueSafe(kv.Key);
						w.WriteValueSafe(kv.Value);
					}
					cmm.SendNamedMessageToAll(InsMessage, w, NetworkDelivery.Reliable);
				}
				return;
			}
			using (var w = new FastBufferWriter(8, Allocator.Temp))
			{
				w.WriteValueSafe(InLobbyWorld);
				cmm.SendNamedMessage(InMessage, NetworkManager.ServerClientId, w, NetworkDelivery.Reliable);
			}
		}

		private static void Note(bool host, string join)
		{
			uint key = host ? (sharing ? 100u : 99u) + (InLobbyWorld ? 10u : 0u) : join.Length > 0 ? mcState : uint.MaxValue - 1;
			if (key == lastNoted)
			{
				return;
			}
			lastNoted = key;
			if (host && InLobbyWorld)
			{
				Log.Info($"lobby: our Minecraft world is the lobby's ({shareLink})");
			}
			else if (!host && join.Length > 0 && InLobbyWorld)
			{
				Log.Info($"lobby: in the host's Minecraft world ({join})");
			}
			else if (!host && join.Length > 0 && mcState == Proto.MpsJoinFailed)
			{
				Log.Warn($"lobby: couldn't reach the host's Minecraft world ({join}); trying again shortly");
			}
		}

		// ---- the others' bodies --------------------------------------------------------------------

		/// <summary>A remote player whose Minecraft is in the lobby's world (theirs is drawn by Minecraft).</summary>
		private static bool InWorld(PlayerControllerB p, NetworkManager net) =>
			InLobbyWorld && p != null && p != Game.Player && p.isPlayerControlled && !p.isPlayerDead
			&& ins.TryGetValue(p.actualClientId, out bool b) && b && p.actualClientId != net.LocalClientId;

		private static void Bodies(NetworkManager net)
		{
			var round = StartOfRound.Instance;
			gone.Clear();
			foreach (var kv in hidden)
			{
				if (kv.Key == null || !InWorld(kv.Key, net))
				{
					gone.Add(kv.Key);
				}
			}
			foreach (var p in gone)
			{
				Show(p);
			}
			if (round == null || round.allPlayerScripts == null || !InLobbyWorld)
			{
				return;
			}
			foreach (var p in round.allPlayerScripts)
			{
				if (!InWorld(p, net))
				{
					continue;
				}
				if (!hidden.TryGetValue(p, out var renderers))
				{
					renderers = new List<Renderer>();
					foreach (var r in new Renderer[] { p.thisPlayerModel, p.thisPlayerModelLOD1, p.thisPlayerModelLOD2 })
					{
						if (r != null)
						{
							renderers.Add(r);
						}
					}
					hidden[p] = renderers;
					Log.Info($"lobby: {p.playerUsername} plays in the lobby's Minecraft world; showing their Minecraft body");
				}
				foreach (var r in renderers)
				{
					if (r != null && r.enabled)
					{
						r.enabled = false;
					}
				}
			}
		}

		private static void Show(PlayerControllerB p)
		{
			if (p != null && hidden.TryGetValue(p, out var renderers))
			{
				foreach (var r in renderers)
				{
					if (r != null)
					{
						r.enabled = true;
					}
				}
			}
			hidden.Remove(p);
		}

		private static void ShowAll()
		{
			gone.Clear();
			gone.AddRange(hidden.Keys);
			foreach (var p in gone)
			{
				Show(p);
			}
		}

		/// <summary>The other players in the lobby's world (Minecraft coordinates): the host exports collision around them too.</summary>
		public static void OthersInWorld(List<Vector3> into)
		{
			into.Clear();
			var net = Net;
			var round = StartOfRound.Instance;
			if (net == null || round == null || round.allPlayerScripts == null || !net.IsServer || !InLobbyWorld)
			{
				return;
			}
			foreach (var p in round.allPlayerScripts)
			{
				if (InWorld(p, net))
				{
					into.Add(Coords.ToMcF(p.transform.position));
				}
			}
		}

		public static string StateText => $"lobby: host={(Net != null && Net.IsServer)} sharing={sharing} inWorld={InLobbyWorld} mc={mcState} hostWorld='{hostWorld}' bodies={hidden.Count}";
	}
}
