using LethalCraft.Input;
using LethalCraft.Lc;
using LethalCraft.Link;
using LethalCraft.Patches;
using LethalCraft.Player;
using LethalCraft.Render;
using LethalCraft.World;
using UnityEngine;

namespace LethalCraft
{
	/// <summary>
	/// The per-frame glue between Lethal Company and Minecraft: reads Minecraft's state, decides who
	/// drives the player, streams the ship's collision to Minecraft and draws Minecraft's overlay.
	///
	/// Minecraft drives the player wherever they walk freely: in the moving ship (its own coordinate
	/// frame) and on the moon (the moon's), with Lethal Company taking over for its own animations
	/// (ladders, terminal, vehicles...) and Minecraft resynced afterwards.
	/// </summary>
	internal sealed class Driver : MonoBehaviour
	{
		private const float SettleSeconds = 0.5f;   // the ship settles (doors, furniture) before collision goes

		private readonly McState mc = new McState();
		private readonly TickInterpolator ticks = new TickInterpolator();
		private bool mcWasAlive;
		private uint lastMcPid;
		private uint teleportSeq = (uint)System.Environment.TickCount | 1u;
		private bool teleportPending = true;
		private uint epoch;
		private float settle = SettleSeconds;
		private float waterTimer;
		private float logTimer;
		private bool puppetPrev;
		private float puppetFor;
		private Transform lastFrame;
		private double lastOffset = -1;
		private Vector3 lastGroundMc;
		private bool haveGround;
		private const double SlotBlocks = 4096;          // each moon gets its own stretch of Minecraft's world
		private const float FallRescueBlocks = 48f;      // further than this below the last ground is a gap in the collision

		private void Awake()
		{
			gameObject.AddComponent<Overlay>();
		}

		private void Start()
		{
			CollisionExporter.Instance.Start();
		}

		private void Update()
		{
			var link = SharedLink.Instance;
			if (!link.Valid)
			{
				return;
			}
			link.Heartbeat();
			// Which Minecraft world goes with the save being played.
			link.WriteSaveKey(SaveWorld.Current);
			InputPatches.AddBindings();
			float dt = Time.unscaledDeltaTime;

			bool mcAlive = link.McAlive();
			bool haveMc = mcAlive && link.ReadMcState(mc);
			uint pid = link.McPid();
			bool newProcess = mcAlive && pid != 0 && pid != lastMcPid;
			if (mcAlive)
			{
				lastMcPid = pid;
			}
			if (mcAlive && (!mcWasAlive || newProcess))
			{
				Log.Info($"Minecraft connected (pid {pid})");
				link.ResetOverlay();
				Overlay.Instance.Clear();
				ResetWorld();
				ticks.Reset();
			}
			if (!mcAlive && mcWasAlive)
			{
				Log.Info("Minecraft disconnected");
				Overlay.Instance.Clear();
			}
			mcWasAlive = mcAlive;
			bool mcInWorld = haveMc && mc.Has(Proto.McInWorld);
			bool mcScreenOpen = haveMc && mc.Has(Proto.McScreenOpen);

			var p = Game.Player;
			bool session = Game.InSession;
			bool inPlay = Game.InPlay;
			bool menu = Game.MenuOpen;

			// Which stretch of Minecraft's world the player is in (a change is a new world):
			//  - the ship's own frame while it moves (orbit, landing, take off) and the player is aboard;
			//  - otherwise the moon's: Unity's world space, its own slot along Minecraft's X. Once the
			//    ship is down (its doors work) it is part of the moon, so walking out is seamless.
			Transform frame = null;
			double offset = 0;
			var round = StartOfRound.Instance;
			if (session)
			{
				bool landed = ShipLanded(round, dt);
				World.ShipBlockMover.Instance.Frame(round, landed, round.currentLevel != null ? (round.currentLevel.levelID + 1) * SlotBlocks : 0);
				double moon = !round.inShipPhase && round.currentLevel != null ? (round.currentLevel.levelID + 1) * SlotBlocks : 0;
				var platform = World.Platforms.Instance.Frame(p, !(Game.OnShip && !landed), moon, dt);
				if (Game.OnShip && !landed)
				{
					frame = round.elevatorTransform;
				}
				else if (platform != null)
				{
					// Riding something that moves (an elevator, a vehicle): its own frame, the platforms' slot.
					frame = platform;
					offset = World.Platforms.SlotOffset;
				}
				else if (!round.inShipPhase && round.currentLevel != null)
				{
					offset = (round.currentLevel.levelID + 1) * SlotBlocks;
				}
			}
			else
			{
				World.ShipBlockMover.Instance.Reset();
				World.Platforms.Instance.Reset();
			}
			if (frame != lastFrame || offset != lastOffset)
			{
				Log.Info(frame == null ? $"Minecraft's world is the moon's ({Game.LevelName}, slot at x {offset:0})"
					: frame == round?.elevatorTransform ? "Minecraft's world follows the ship" : $"Minecraft's world follows {frame.name} (a moving platform)");
				lastFrame = frame;
				lastOffset = offset;
				Puppet.Instance.Release();
				Coords.Frame = frame;
				Coords.OffsetX = offset;
				ResetWorld();
			}
			bool loading = Game.Loading || !session;

			if (Puppet.TeleportedByGame)
			{
				Puppet.TeleportedByGame = false;
				Log.Info("the game teleported the player; Minecraft follows");
				teleportPending = true;
			}
			if (p != null && Puppet.Active && Puppet.Instance.MovedByGame(p))
			{
				Log.Info("Lethal Company moved the player; resyncing Minecraft");
				Puppet.Instance.Release();
				teleportPending = true;
			}
			bool canDrive = inPlay && !loading && Puppet.CanDrive(p);
			if (!canDrive && Puppet.Active)
			{
				Puppet.Instance.Release();
				teleportPending = true;
			}
			if (canDrive && teleportPending)
			{
				teleportSeq++;
				// Wherever Minecraft lands, the ground it last stood on was somewhere else.
				haveGround = false;
				teleportPending = false;
			}

			// Minecraft has taken the teleport to where the game's player is: it drives from here.
			bool puppet = canDrive && haveMc && mcInWorld && mc.TeleportAck == teleportSeq;
			puppetFor = puppet ? puppetFor + dt : 0f;
			if (puppet != puppetPrev)
			{
				puppetPrev = puppet;
				ticks.Reset();
				if (!puppet)
				{
					Puppet.Instance.Release();
					InputBridge.Instance.ReleaseAll();
				}
			}
			// Minecraft fell through a gap in the collision: back to where it last stood, before the
			// void kills it (and the game's player with it).
			if (puppet && p != null)
			{
				var mcFeet = new Vector3((float)mc.X, (float)mc.Y, (float)mc.Z);
				if (mc.Has(Proto.McOnGround) && !mc.Has(Proto.McDead))
				{
					lastGroundMc = mcFeet;
					haveGround = true;
				}
				else if (haveGround && mcFeet.y < lastGroundMc.y - FallRescueBlocks)
				{
					Log.Warn($"Minecraft fell {lastGroundMc.y - mcFeet.y:0} blocks through the game's collision; back to where it last stood");
					puppet = false;
					puppetPrev = false;
					Puppet.Instance.Release();
					InputBridge.Instance.ReleaseAll();
					p.TeleportPlayer(Coords.ToUnity(lastGroundMc));
					teleportPending = true;
				}
			}
			if (puppet)
			{
				ticks.Sample(mc);
				Puppet.Instance.Frame(mc, ticks, dt);
			}

			// Multiplayer: the lobby in one Minecraft world, the host's.
			Lobby.Frame(link, session && !loading, dt);

			// One health bar, Minecraft's; the game's monsters as Minecraft's stand-ins; its hits on them.
			Combat.Combat.McOwnsHealth = haveMc && mcInWorld && session && !loading && p != null && p.isPlayerControlled && !p.isPlayerDead;
			Combat.Combat.Instance.Frame(haveMc && mcInWorld && session && !loading, p, mc);

			// The game's ladders climb like Minecraft's.
			World.LadderExporter.Instance.Frame(session && !loading, p, dt);

			// One inventory, Minecraft's: the game's objects as its hotbar items.
			bool mcPlays = haveMc && mcInWorld && session && !loading && p != null && p.isPlayerControlled;
			Inventory.InventoryBridge.McFreeSlots = haveMc && mcInWorld ? mc.FreeSlots : 1u;
			Inventory.InventoryBridge.McSelected = haveMc && mcInWorld ? mc.SelectedObject : 0u;
			Inventory.InventoryBridge.Instance.Frame(session && !loading, mcPlays, p);
			if (session && !loading)
			{
				Inventory.ItemIcons.Instance.Frame();
			}
			hideGameHud = mcPlays;

			// Where the game's player is and where they look, for Minecraft.
			Vector3 feet = p != null ? p.transform.position : Vector3.zero;
			Coords.ToMc(feet, out double px, out double py, out double pz);
			float yaw = 0f, pitch = 0f;
			if (p != null)
			{
				yaw = Coords.YawToMc(Coords.FrameYaw(p.transform.forward));
				pitch = Mathf.Clamp(p.cameraUp, -90f, 90f);
			}
			link.WriteHostState(new HostState
			{
				Flags = (session ? Proto.HostInGame : 0u) | (menu ? Proto.HostMenuOpen : 0u) | (loading ? Proto.HostLoading : 0u) | (Config.ShowMinecraftWindow ? Proto.HostShowWindow : 0u),
				WorldId = 1,
				CollisionEpoch = epoch,
				PosX = px,
				PosY = py,
				PosZ = pz,
				Yaw = yaw,
				Pitch = pitch,
				TeleportSeq = teleportSeq,
				ViewportW = (uint)Mathf.Min(Screen.width, Proto.MaxOverlayW),
				ViewportH = (uint)Mathf.Min(Screen.height, Proto.MaxOverlayH),
				GameHour = 13f,
			});

			// The ship's collision to Minecraft.
			settle -= dt;
			Vector3 centreMc = puppet ? new Vector3((float)mc.X, (float)mc.Y, (float)mc.Z) : new Vector3((float)px, (float)py, (float)pz);
			if (haveMc && !loading && settle <= 0f)
			{
				Lobby.OthersInWorld(others);
				CollisionExporter.Instance.Update(centreMc, others);
			}
			waterTimer -= dt;
			if (haveMc && waterTimer <= 0f)
			{
				// The game's water as Minecraft water around the player (swimming, air, drowning: Minecraft's).
				waterTimer = 0.25f;
				int gx = Mathf.FloorToInt(centreMc.x) - Proto.WaterGridSize / 2, gz = Mathf.FloorToInt(centreMc.z) - Proto.WaterGridSize / 2;
				var surface = World.WaterSurface.Instance.Fill(session && !loading && p != null, p != null ? p.transform.position : Vector3.zero, gx, gz, 0.25f);
				link.WriteWaterGrid(gx, gz, 1u, surface);
			}

			// Minecraft's world to Lethal Company.
			BlockRenderer.Instance.Drain();
			worldShown = session;
			lightsAt = haveMc && mcInWorld && session && !loading ? centreMc : (Vector3?)null;
			avatarShown = puppet && mc.CameraMode != 0;

			var overlay = Overlay.Instance;
			overlay.Pull();
			overlay.Show = mcInWorld && inPlay && !menu;
			overlay.ShowCursor = mcScreenOpen;
			overlay.Cursor = new Vector2Int(InputBridge.Instance.CursorX, InputBridge.Instance.CursorY);
			lastScreenOpen = mcScreenOpen;

			logTimer -= dt;
			if (Config.Diagnostics && logTimer <= 0f)
			{
				logTimer = 2f;
				Log.Debug($"state: session={session} play={inPlay} loading={loading} menu={menu} frame={(lastFrame == null ? "moon" : lastFrame == StartOfRound.Instance?.elevatorTransform ? "ship" : "platform " + lastFrame.name)} slot={lastOffset:0} canDrive={canDrive} puppet={puppet} seq={teleportSeq} ack={mc.TeleportAck}"
					+ $" | mc inWorld={mcInWorld} pos=({mc.X:0.00} {mc.Y:0.00} {mc.Z:0.00}) ground={mc.Has(Proto.McOnGround)} flags={mc.Flags:X}"
					+ $" | game=({px:0.00} {py:0.00} {pz:0.00}) yaw={yaw:0} pitch={pitch:0} | collision epoch={epoch} pending={CollisionExporter.Instance.Pending} sections={BlockRenderer.Instance.SectionCount} materials={Materials.Count} lights={BlockLights.Instance.LightsOn}/{BlockLights.Instance.Emitters} overlay frames={overlay.FramesShown} late={ticks.LateFrames} harvest max={CollisionExporter.Instance.MaxMs:0.0}ms");
				CollisionExporter.Instance.MaxMs = 0f;
			}
		}

		private void LateUpdate()
		{
			// Minecraft's keys, once the game has had its Update.
			InputBridge.Instance.Frame(Puppet.Active && Application.isFocused && !Game.MenuOpen, lastScreenOpen, Game.Player,
				Mathf.Min(Screen.width, Proto.MaxOverlayW), Mathf.Min(Screen.height, Proto.MaxOverlayH));
			HideArms(Game.Player, Overlay.Instance.Show);
			HideGameHud(hideGameHud);
			HideBody(Game.Player, hideGameHud);
			// After the ship has moved this frame: the blocks go where Minecraft's origin now is.
			var blocks = BlockRenderer.Instance;
			blocks.Place(worldShown);
			BlockLights.Instance.Update(blocks.Root.transform, worldShown ? lightsAt : null, Time.unscaledDeltaTime);
			EntityRenderer.Instance.Frame(blocks.Root.transform, blocks.Atlas, worldShown && lightsAt != null);
			// The interpolated feet the player's body is placed at (frame space), back to Minecraft's.
			Vector3 feet = Puppet.Instance.FeetLocal;
			blocks.PlaceAvatar(feet.x * Coords.K + Coords.OffsetX, feet.y * Coords.K, -feet.z * Coords.K, avatarShown);
		}

		private bool worldShown, avatarShown, hideGameHud;
		private readonly System.Collections.Generic.List<Vector3> others = new System.Collections.Generic.List<Vector3>();

		/// <summary>
		/// Minecraft's HUD is the player's: the game's item slots, its top-left health/stamina/weight
		/// panel and its control hints go (after its own fades, which run in Update).
		/// </summary>
		private static void HideGameHud(bool hide)
		{
			var hud = HUDManager.Instance;
			if (!hide || hud == null)
			{
				return;
			}
			Hide(hud.Inventory);
			Hide(hud.PlayerInfo);
			Hide(hud.Tooltips);
			Hide(hud.Chat);
		}

		private static void Hide(HUDElement e)
		{
			if (e != null && e.canvasGroup != null)
			{
				e.canvasGroup.alpha = 0f;
			}
		}
		private Vector3? lightsAt;

		private Vector3 lastShipPos;
		private float shipStillFor;
		private bool shipLanded;

		/// <summary>
		/// The ship is down on the moon and staying there: it has stopped moving (its doors are enabled
		/// while it's still descending, so they don't tell), until it starts to leave.
		/// </summary>
		private bool ShipLanded(StartOfRound round, float dt)
		{
			var ship = round.elevatorTransform;
			if (round.inShipPhase || round.shipIsLeaving || ship == null)
			{
				shipLanded = false;
				shipStillFor = 0f;
				if (ship != null)
				{
					lastShipPos = ship.position;
				}
				return false;
			}
			shipStillFor = (ship.position - lastShipPos).sqrMagnitude < 1e-6f ? shipStillFor + dt : 0f;
			lastShipPos = ship.position;
			if (shipStillFor > 0.5f)
			{
				shipLanded = true;
			}
			return shipLanded;
		}

		private bool armsHidden;
		private bool lastScreenOpen;

		/// <summary>
		/// Lethal Company's first-person arms go while Minecraft's picture (its own hand, held item and
		/// HUD) is on screen. The game turns them back on when its camera takes over, so this runs every
		/// frame, after its Update.
		/// </summary>
		private readonly System.Collections.Generic.HashSet<Renderer> visorHidden = new System.Collections.Generic.HashSet<Renderer>();

		private void HideArms(GameNetcodeStuff.PlayerControllerB p, bool hide)
		{
			if (p == null || p.thisPlayerModelArms == null)
			{
				armsHidden = false;
				visorHidden.Clear();
				return;
			}
			if (hide)
			{
				p.thisPlayerModelArms.enabled = false;
				armsHidden = true;
				// The helmet visor (its frame and glass around the view, and its cracks) too: Minecraft's
				// player wears no helmet. Every frame, since the game turns parts of it on (cracks when hurt).
				if (p.localVisor != null)
				{
					foreach (var r in p.localVisor.GetComponentsInChildren<Renderer>(true))
					{
						if (r.enabled)
						{
							r.enabled = false;
							visorHidden.Add(r);
						}
					}
				}
			}
			else if (armsHidden)
			{
				armsHidden = false;
				// Only where the game itself shows them: alive, in the player's own camera.
				p.thisPlayerModelArms.enabled = !p.isPlayerDead && !p.isCameraDisabled;
				foreach (var r in visorHidden)
				{
					if (r != null)
					{
						r.enabled = true;
					}
				}
				visorHidden.Clear();
			}
		}

		private readonly System.Collections.Generic.List<Renderer> bodyHidden = new System.Collections.Generic.List<Renderer>();

		/// <summary>
		/// The player's own Lethal Company body (its model, LODs and badges) goes while Minecraft has the
		/// player: Minecraft's body is the player's (drawn in third person, and casting the shadow), and
		/// the game's would show through it. Every frame, since the game turns them back on (a suit
		/// change, a revive); given back only where the game would show it (alive).
		/// </summary>
		private void HideBody(GameNetcodeStuff.PlayerControllerB p, bool hide)
		{
			if (p == null)
			{
				bodyHidden.Clear();
				return;
			}
			if (hide)
			{
				var badge = p.playerBadgeMesh != null ? p.playerBadgeMesh.GetComponent<Renderer>() : null;
				foreach (var r in new Renderer[] { p.thisPlayerModel, p.thisPlayerModelLOD1, p.thisPlayerModelLOD2, p.playerBetaBadgeMesh, badge })
				{
					if (r != null && r.enabled)
					{
						r.enabled = false;
						if (!bodyHidden.Contains(r))
						{
							bodyHidden.Add(r);
						}
					}
				}
				return;
			}
			if (bodyHidden.Count == 0)
			{
				return;
			}
			foreach (var r in bodyHidden)
			{
				if (r != null && !p.isPlayerDead)
				{
					r.enabled = true;
				}
			}
			bodyHidden.Clear();
		}

		/// <summary>Everything Minecraft has of the game's world is stale: resend it from a new epoch.</summary>
		private void ResetWorld()
		{
			epoch++;
			CollisionExporter.Instance.Reset(epoch);
			teleportPending = true;
			settle = SettleSeconds;
			haveGround = false;
		}
	}
}
