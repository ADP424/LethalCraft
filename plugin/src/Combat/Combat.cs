using System.Collections.Generic;
using GameNetcodeStuff;
using LethalCraft.Link;
using UnityEngine;

namespace LethalCraft.Combat
{
	/// <summary>
	/// Lethal Company's monsters inside Minecraft, Minecraft's weapons inside Lethal Company, and one
	/// health bar: Minecraft's.
	///  - Every spawned enemy goes to Minecraft as an invisible, hittable stand-in (the actor table),
	///    sized from its own hit colliders, so Minecraft's swords, axes, bows and tridents work on it
	///    with vanilla Minecraft code.
	///  - Minecraft's hits come back as events and land through the game's own HitEnemyOnLocalClient
	///    (what its shovel does; it syncs to everyone). Minecraft damage converts to the game's hit
	///    force by <see cref="Config.DamageToEnemies"/>, carried over between hits, so a fist needs
	///    several punches for one shovel hit and a crit with a good sword does two.
	///  - The game hurting the player is cancelled and sent to Minecraft (Patches.CombatPatches), where
	///    armour, shields, totems and invulnerability frames apply. The game's own health follows
	///    Minecraft's (100 = full); when Minecraft's player dies, the game's dies too, with the cause
	///    and death animation of the hit that did it.
	/// </summary>
	internal sealed class Combat
	{
		public static readonly Combat Instance = new Combat();

		/// <summary>Minecraft has the player's health (set by the Driver each frame).</summary>
		public static bool McOwnsHealth;

		/// <summary>Our own call into the game's damage code: let it through.</summary>
		public static bool Bypass;

		private const float AttackerRange = 5f;  // metres: the enemy a hit is blamed on (shield, knock-back)
		private const float ActorRange = 1000f;  // metres: every enemy on the moon (in a shared world, guests fight far from the host)
		private const float KillWait = 2f;       // seconds Minecraft has to die from the game's kill

		private readonly ActorRecord[] records = new ActorRecord[Proto.MaxActors];
		private readonly Dictionary<uint, EnemyAI> byId = new Dictionary<uint, EnemyAI>();
		private readonly Dictionary<uint, float> carry = new Dictionary<uint, float>();
		private int lastHealth = -1;

		// The game's last hit or kill on the player: how its death goes if Minecraft's follows.
		private struct Cause
		{
			public float Time;
			public Vector3 Velocity;
			public bool SpawnBody;
			public CauseOfDeath Kind;
			public int Animation;
			public Vector3 Offset;
			public bool OverrideDrop;
		}

		private Cause lastCause;
		private bool killPending;

		public static uint IdOf(EnemyAI e) => (uint)e.NetworkObjectId + 1;

		public void Clear()
		{
			byId.Clear();
			carry.Clear();
			killPending = false;
			lastHealth = -1;
		}

		/// <summary>Main thread, every frame: actor table out, Minecraft's events in, health mirrored.</summary>
		public void Frame(bool live, PlayerControllerB p, McState mc)
		{
			var link = SharedLink.Instance;
			int count = live && p != null ? BuildActors(p) : 0;
			link.WriteActors(records, count);
			while (link.PopEvent(out McEvent ev))
			{
				switch (ev.Type)
				{
					case Proto.EvHitActor:
						if (live)
						{
							OnHit(ev, p);
						}
						break;
					case Proto.EvPlayerDied:
						KillGamePlayer(p, "Minecraft's player died");
						break;
					case Proto.EvChatChar:
						Lc.Chat.OnChar(ev.Id);
						break;
					case Proto.EvChatEnd:
						Lc.Chat.OnEnd();
						break;
					case Proto.EvDropGameItem:
						Inventory.InventoryBridge.Instance.OnDropped(ev.Id);
						break;
				}
			}
			if (!McOwnsHealth || p == null)
			{
				lastHealth = -1;
				return;
			}
			if (killPending && Time.time - lastCause.Time > KillWait)
			{
				killPending = false;
				Log.Info("the game's kill didn't kill Minecraft's player (a totem, or armour): the player lives");
			}
			MirrorHealth(p, mc);
		}

		// ---- enemies to Minecraft ----------------------------------------------------------------

		private int BuildActors(PlayerControllerB p)
		{
			var round = RoundManager.Instance;
			if (round == null || round.SpawnedEnemies == null)
			{
				return 0;
			}
			byId.Clear();
			int n = 0;
			Vector3 at = p.transform.position;
			foreach (var e in round.SpawnedEnemies)
			{
				if (n >= records.Length)
				{
					break;
				}
				if (e == null || !e.gameObject.activeInHierarchy || e.enemyType == null)
				{
					continue;
				}
				Bounds b = BodyBounds(e);
				if ((b.center - at).sqrMagnitude > ActorRange * ActorRange)
				{
					continue;
				}
				uint id = IdOf(e);
				byId[id] = e;
				float k = Coords.K;
				Coords.ToMc(new Vector3(b.center.x, b.min.y, b.center.z), out double x, out double y, out double z);
				uint flags = Proto.ActorHostile;
				if (e.isEnemyDead)
				{
					flags |= Proto.ActorDead;
				}
				if (e.targetPlayer != null || e.movingTowardsTargetPlayer)
				{
					flags |= Proto.ActorInCombat;
				}
				records[n++] = new ActorRecord
				{
					Id = id,
					Flags = flags,
					X = (float)x,
					Y = (float)y,
					Z = (float)z,
					Yaw = Coords.YawToMc(Coords.FrameYaw(e.transform.forward)),
					Width = Mathf.Max(0.3f, Mathf.Max(b.size.x, b.size.z) * k),
					Height = Mathf.Max(0.3f, b.size.y * k),
					HealthFrac = 1f,
					Level = 1,
					Name = e.enemyType.enemyName,
				};
			}
			return n;
		}

		/// <summary>The enemy's body as its hit colliders (the ones the game's shovel hits) bound it, world space.</summary>
		public static Bounds BodyBounds(EnemyAI e)
		{
			bool any = false;
			var b = new Bounds();
			foreach (var hit in e.GetComponentsInChildren<EnemyAICollisionDetect>())
			{
				foreach (var c in hit.GetComponents<Collider>())
				{
					if (!c.enabled)
					{
						continue;
					}
					if (!any)
					{
						b = c.bounds;
						any = true;
					}
					else
					{
						b.Encapsulate(c.bounds);
					}
				}
			}
			return any ? b : new Bounds(e.transform.position + Vector3.up * 0.75f, Vector3.one * 1.5f);
		}

		// ---- Minecraft hits an enemy -------------------------------------------------------------

		private void OnHit(McEvent ev, PlayerControllerB p)
		{
			if (!byId.TryGetValue(ev.Id, out EnemyAI e) || e == null || e.isEnemyDead)
			{
				return;
			}
			carry.TryGetValue(ev.Id, out float owed);
			owed += ev.A * Config.DamageToEnemies;
			int force = Mathf.FloorToInt(owed);
			carry[ev.Id] = owed - force;
			Vector3 dir = Coords.DirToUnity(new Vector3(ev.B, 0f, ev.C));
			if (dir.sqrMagnitude < 1e-6f)
			{
				dir = e.transform.position - p.transform.position;
				dir.y = 0f;
			}
			dir = dir.normalized;
			Log.Info($"Minecraft hit {e.enemyType.enemyName} for {ev.A:0.0}: force {force} (owed {owed - force:0.00})");
			if (force > 0)
			{
				e.HitEnemyOnLocalClient(force, dir, p, true, -1);
			}
		}

		// ---- the game hurts the player -----------------------------------------------------------

		/// <summary>The game's damage on the player (its health points, 100 = full), re-routed to Minecraft.</summary>
		public void PlayerHurt(PlayerControllerB p, int damage, CauseOfDeath cause, int deathAnimation, Vector3 force)
		{
			Remember(force, deathAnimation != -1, cause, deathAnimation, Vector3.zero, false);
			EnemyAI attacker = NearestEnemy(p);
			ushort kind = cause == CauseOfDeath.Gunshots ? Proto.HurtProjectile
				: attacker != null ? Proto.HurtMelee
				: Proto.HurtOther;
			float hostDamage = damage * Config.DamageToPlayer;
			SharedLink.Instance.PushInput(Proto.InHurt, kind, Mathf.RoundToInt(hostDamage * 100f), attacker != null ? (int)IdOf(attacker) : 0, 0);
			Log.Info($"the game hurt the player for {damage} ({cause}, {(attacker != null ? attacker.enemyType.enemyName : "no enemy")}): Minecraft takes {hostDamage / 5f:0.0}");
		}

		/// <summary>The game killed the player outright: Minecraft's player takes a killing blow, and the game's dies when it does.</summary>
		public void PlayerKilled(PlayerControllerB p, Vector3 velocity, bool spawnBody, CauseOfDeath cause, int deathAnimation, Vector3 offset, bool overrideDrop)
		{
			Remember(velocity, spawnBody, cause, deathAnimation, offset, overrideDrop);
			killPending = true;
			EnemyAI attacker = NearestEnemy(p);
			SharedLink.Instance.PushInput(Proto.InHurt, attacker != null ? Proto.HurtMelee : Proto.HurtOther, 1000000, attacker != null ? (int)IdOf(attacker) : 0, 0);
			Log.Info($"the game killed the player ({cause}): Minecraft's player takes a killing blow");
		}

		private void Remember(Vector3 velocity, bool spawnBody, CauseOfDeath kind, int animation, Vector3 offset, bool overrideDrop)
		{
			lastCause = new Cause
			{
				Time = Time.time,
				Velocity = velocity,
				SpawnBody = spawnBody,
				Kind = kind,
				Animation = animation,
				Offset = offset,
				OverrideDrop = overrideDrop,
			};
		}

		private EnemyAI NearestEnemy(PlayerControllerB p)
		{
			var round = RoundManager.Instance;
			if (round == null || round.SpawnedEnemies == null)
			{
				return null;
			}
			EnemyAI best = null;
			float bestD = AttackerRange * AttackerRange;
			foreach (var e in round.SpawnedEnemies)
			{
				if (e == null || e.isEnemyDead || !e.gameObject.activeInHierarchy)
				{
					continue;
				}
				float d = (BodyBounds(e).ClosestPoint(p.transform.position) - p.transform.position).sqrMagnitude;
				if (d < bestD)
				{
					bestD = d;
					best = e;
				}
			}
			return best;
		}

		/// <summary>Minecraft's player died: the game's dies too, the way the hit that did it would have killed it.</summary>
		private void KillGamePlayer(PlayerControllerB p, string why)
		{
			killPending = false;
			if (p == null || p.isPlayerDead || !p.isPlayerControlled)
			{
				return;
			}
			bool recent = Time.time - lastCause.Time < KillWait + 1f;
			var c = recent ? lastCause : new Cause { Velocity = Player.Puppet.Instance.Velocity, SpawnBody = true, Kind = CauseOfDeath.Unknown };
			Log.Info($"{why}: the game's player dies too ({c.Kind})");
			Bypass = true;
			try
			{
				p.KillPlayer(c.Velocity, c.SpawnBody, c.Kind, c.Animation, c.Offset, c.OverrideDrop);
			}
			finally
			{
				Bypass = false;
			}
		}

		/// <summary>The game's health shows Minecraft's (its HUD, its "critically injured" limp and blood for the others).</summary>
		private void MirrorHealth(PlayerControllerB p, McState mc)
		{
			if (mc.MaxHealth <= 0f || p.isPlayerDead)
			{
				return;
			}
			int health = Mathf.Clamp(Mathf.CeilToInt(mc.Health / mc.MaxHealth * 100f), 1, 100);
			if (health == lastHealth && health == p.health)
			{
				return;
			}
			bool hurt = lastHealth >= 0 && health < lastHealth;
			lastHealth = health;
			p.health = health;
			HUDManager.Instance.UpdateHealthUI(health, hurt);
			HUDManager.Instance.SetCracksOnVisor(health);
			if (health < 10 && !p.criticallyInjured)
			{
				p.MakeCriticallyInjured(true);
			}
			else if (health >= 10 && p.criticallyInjured)
			{
				p.MakeCriticallyInjured(false);
			}
		}
	}
}
