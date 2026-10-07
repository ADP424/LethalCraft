package dev.lethalcraft.combat;

import dev.lethalcraft.LethalCraft;
import dev.lethalcraft.link.Proto;
import dev.lethalcraft.link.HostLink;
import java.util.ArrayList;
import java.util.HashMap;
import java.util.Iterator;
import java.util.List;
import java.util.Map;
import net.fabricmc.fabric.api.event.lifecycle.v1.ServerTickEvents;
import net.fabricmc.fabric.api.object.builder.v1.entity.FabricDefaultAttributeRegistry;
import net.minecraft.core.Registry;
import net.minecraft.core.registries.BuiltInRegistries;
import net.minecraft.core.registries.Registries;
import net.minecraft.network.chat.Component;
import net.minecraft.resources.Identifier;
import net.minecraft.resources.ResourceKey;
import net.minecraft.server.MinecraftServer;
import net.minecraft.server.level.ServerLevel;
import net.minecraft.server.level.ServerPlayer;
import net.minecraft.world.damagesource.DamageSource;
import net.minecraft.world.damagesource.DamageSources;
import net.minecraft.world.entity.Entity;
import net.minecraft.world.entity.EntityType;
import net.minecraft.world.entity.LivingEntity;
import net.minecraft.world.entity.MobCategory;
import org.jspecify.annotations.Nullable;

/**
 * Combat between the Minecraft player and Lethal Company actors, server side.
 *
 * <p>Every Lethal Company actor near the player gets an invisible {@link HostActorEntity} at its exact
 * position. Minecraft weapons hit those like any mob; the resulting damage is sent to Lethal Company, which
 * applies it to the real actor (scaled by level) and makes it fight back. Lethal Company's hits on the player
 * come back as Minecraft damage from the attacker's stand-in, so armor, shields, knockback, hurt
 * sounds and death all work the Minecraft way.
 */
public final class HostCombat {
	public static final ResourceKey<EntityType<?>> HOST_ACTOR_KEY =
		ResourceKey.create(Registries.ENTITY_TYPE, Identifier.fromNamespaceAndPath(LethalCraft.MOD_ID, "host_actor"));
	public static final EntityType<HostActorEntity> HOST_ACTOR = Registry.register(
		BuiltInRegistries.ENTITY_TYPE,
		HOST_ACTOR_KEY,
		EntityType.Builder.<HostActorEntity>of(HostActorEntity::new, MobCategory.MISC)
			.sized(0.6F, 1.8F)
			.noSave()
			.noSummon()
			.noLootTable()
			.clientTrackingRange(10)
			.updateInterval(1)
			.build(HOST_ACTOR_KEY)
	);

	/** Lethal Company damage is divided by this for Minecraft (a 15-damage bandit swing = 3 = 1.5 hearts). */
	public static final float HOST_TO_MC_DAMAGE = 5.0F;

	private static final Map<Integer, HostActorEntity> PROXIES = new HashMap<>();
	private static final List<HostLink.Actor> ACTORS = new ArrayList<>();

	private HostCombat() {
	}

	public static void init() {
		FabricDefaultAttributeRegistry.register(HOST_ACTOR, LivingEntity.createLivingAttributes());
		ServerTickEvents.END_SERVER_TICK.register(HostCombat::serverTick);
	}

	public static @Nullable HostActorEntity proxy(int actorId) {
		return PROXIES.get(actorId);
	}

	private static void serverTick(MinecraftServer server) {
		List<ServerPlayer> players = server.getPlayerList().getPlayers();
		if (!HostLink.active() || players.isEmpty()) {
			removeAll();
			return;
		}
		ServerLevel level = players.getFirst().level();
		for (ServerPlayer player : players) {
			pickUpNearby(player);
		}
		if (HostLink.readActors(ACTORS)) {
			sync(level);
		}
		// Hits land during the tick (melee, sweeps, arrows, fire); send one combined hit per actor.
		for (HostActorEntity proxy : PROXIES.values()) {
			java.util.UUID by = proxy.attacker();
			float[] hit = proxy.takeHit();
			if (hit != null && (hit[0] > 0.0F || hit[3] > 0.0F)) {
				// The hit lands through the hitter's own Lethal Company (the game credits and syncs it from there):
				// the host's through the link, a guest's through their client.
				ServerPlayer guest = by != null ? server.getPlayerList().getPlayer(by) : null;
				if (guest != null && !dev.lethalcraft.net.LethalNet.isHost(guest)) {
					dev.lethalcraft.net.LethalNet.sendHitActor(guest, proxy.actorId(), hit);
				} else {
					HostLink.pushEvent(
						Proto.EV_HIT_ACTOR, proxy.actorId(), hit[0], hit[1], hit[2], hit[3], Float.floatToRawIntBits(hit[4]), Float.floatToRawIntBits(hit[5])
					);
				}
				LethalCraft.LOG.info("LethalCraft: hit {} for {} (knockback {})", proxy.getName().getString(), hit[0], hit[3]);
			}
		}
	}

	private static void sync(ServerLevel level) {
		Map<Integer, HostLink.Actor> live = new HashMap<>();
		for (HostLink.Actor a : ACTORS) {
			if (!a.dead()) {
				live.put(a.actorId(), a);
			}
		}
		for (Iterator<Map.Entry<Integer, HostActorEntity>> it = PROXIES.entrySet().iterator(); it.hasNext(); ) {
			Map.Entry<Integer, HostActorEntity> e = it.next();
			HostActorEntity proxy = e.getValue();
			if (!live.containsKey(e.getKey()) || proxy.isRemoved() || proxy.level() != level) {
				proxy.discard();
				it.remove();
			}
		}
		int before = PROXIES.size();
		for (HostLink.Actor a : live.values()) {
			HostActorEntity proxy = PROXIES.get(a.actorId());
			if (proxy == null) {
				proxy = new HostActorEntity(HOST_ACTOR, level);
				proxy.setActorId(a.actorId());
				proxy.setSize(a.width(), a.height());
				proxy.snapTo(a.x(), a.y(), a.z(), a.yaw(), 0.0F);
				if (!a.name().isEmpty()) {
					proxy.setCustomName(Component.literal(a.name()));
				}
				if (!level.addFreshEntity(proxy)) {
					continue;
				}
				PROXIES.put(a.actorId(), proxy);
				continue;
			}
			proxy.setSize(a.width(), a.height());
			var from = proxy.position();
			proxy.setPos(a.x(), a.y(), a.z());
			proxy.setYRot(a.yaw());
			proxy.setYHeadRot(a.yaw());
			touchBlocks(level, proxy, from);
		}
		if (PROXIES.size() != before && (PROXIES.size() % 5 == 0 || PROXIES.size() < 5)) {
			LethalCraft.LOG.info("LethalCraft: {} Lethal Company actors mirrored as hittable stand-ins", PROXIES.size());
		}
	}

	/**
	 * What Minecraft's blocks do to a monster in or on them, as they do to anything that walks there: lava,
	 * fire and campfires burn, cactus and berry bushes prick, wither roses wither, cobwebs and powder snow
	 * hold, magma scorches, pressure plates press and tripwires trip. Stand-ins are placed, not moved
	 * (no physics), so Minecraft never checks; this does, along the way the game moved the monster this tick.
	 * The damage lands on the real monster like any other hit (actuallyHurt).
	 */
	private static void touchBlocks(ServerLevel level, HostActorEntity proxy, net.minecraft.world.phys.Vec3 from) {
		proxy.applyEffectsFromBlocks(from, proxy.position());
		var below = proxy.getBlockPosBelowThatAffectsMyMovement();
		var floor = level.getBlockState(below);
		if (!floor.isAir()) {
			floor.getBlock().stepOn(level, below, floor, proxy);
		}
	}

	/**
	 * Items and stuck arrows on Lethal Company ground rest on its collision voxels, which on steep or rough
	 * terrain can sit a little off from where the player (on Lethal Company's exact triangles) stands.
	 * Touch them over a slightly bigger area than vanilla's so walking over them picks them up.
	 * playerTouch applies all of Minecraft's own rules (pickup delay, owner, inventory space).
	 */
	private static void pickUpNearby(ServerPlayer player) {
		if (!player.isAlive() || player.isSpectator()) {
			return;
		}
		for (Entity entity : player.level().getEntities(player, player.getBoundingBox().inflate(1.25, 1.0, 1.25))) {
			if (!entity.isRemoved() && (entity instanceof net.minecraft.world.entity.item.ItemEntity
				|| entity instanceof net.minecraft.world.entity.projectile.arrow.AbstractArrow)) {
				entity.playerTouch(player);
			}
		}
	}

	private static void removeAll() {
		if (PROXIES.isEmpty()) {
			return;
		}
		PROXIES.values().forEach(Entity::discard);
		PROXIES.clear();
	}

	/**
	 * Lethal Company hit the player. Runs on the server thread. {@code kind} is a Proto.HURT_* value and
	 * {@code hostDamage} is what Lethal Company would have taken off the player's health.
	 */
	public static void hurtPlayer(ServerPlayer player, int kind, float hostDamage, int attackerId, int flags) {
		if (!player.isAlive() || hostDamage <= 0.0F) {
			return;
		}
		ServerLevel level = player.level();
		HostActorEntity attacker = PROXIES.get(attackerId);
		if (attacker != null && attacker.distanceToSqr(player) > 24.0 * 24.0) {
			attacker = null; // a guest's own enemy with the same id as one of the host's
		}
		DamageSources sources = level.damageSources();
		DamageSource source = switch (kind) {
			case Proto.HURT_MELEE -> attacker != null ? sources.mobAttack(attacker) : sources.generic();
			case Proto.HURT_PROJECTILE -> attacker != null ? sources.mobProjectile(attacker, attacker) : sources.generic();
			case Proto.HURT_MAGIC -> attacker != null ? sources.indirectMagic(attacker, attacker) : sources.magic();
			default -> sources.generic();
		};
		float damage = hostDamage / HOST_TO_MC_DAMAGE;
		float healthBefore = player.getHealth();
		boolean blocking = player.isBlocking();
		boolean hurt = player.hurtServer(level, source, damage);
		trainDefence(player, damage, blocking && player.getHealth() >= healthBefore - 1.0E-3F);
		LethalCraft.LOG.info("LethalCraft: Lethal Company hit the player for {} ({} Minecraft): health {} -> {}{}", hostDamage, damage, healthBefore, player.getHealth(),
			hurt ? "" : " (blocked/immune)");
		if (hurt && attacker != null && (flags & Proto.HURT_POWER_ATTACK) != 0 && !player.isBlocking()) {
			// Power attacks shove harder, like a sprint hit does in Minecraft.
			player.knockback(0.5, attacker.getX() - player.getX(), attacker.getZ() - player.getZ(), source, damage);
		}
	}

	/**
	 * Lethal Company skills for taking a hit: Block when the shield caught it, otherwise Light or Heavy
	 * Armor by what the player mostly wears (leather, chainmail, gold, copper and turtle count as
	 * light; iron, diamond and netherite as heavy). Only the host's own Lethal Company is told.
	 */
	private static void trainDefence(ServerPlayer player, float damage, boolean blocked) {
		if (!dev.lethalcraft.net.LethalNet.isHost(player) || damage <= 0.0F) {
			return;
		}
		if (blocked) {
			HostLink.pushEvent(Proto.EV_SKILL_USE, Proto.SKILL_BLOCK, damage, 0.0F, 0.0F, 0.0F, 0);
			return;
		}
		int light = 0, heavy = 0;
		for (var slot : new net.minecraft.world.entity.EquipmentSlot[] { net.minecraft.world.entity.EquipmentSlot.HEAD, net.minecraft.world.entity.EquipmentSlot.CHEST,
			net.minecraft.world.entity.EquipmentSlot.LEGS, net.minecraft.world.entity.EquipmentSlot.FEET }) {
			var stack = player.getItemBySlot(slot);
			if (stack.isEmpty()) {
				continue;
			}
			String path = net.minecraft.core.registries.BuiltInRegistries.ITEM.getKey(stack.getItem()).getPath();
			if (path.startsWith("iron_") || path.startsWith("diamond_") || path.startsWith("netherite_")) {
				heavy++;
			} else {
				light++;
			}
		}
		if (light + heavy > 0) {
			HostLink.pushEvent(Proto.EV_SKILL_USE, heavy > light ? Proto.SKILL_HEAVY_ARMOR : Proto.SKILL_LIGHT_ARMOR, damage * (light + heavy) / 4.0F, 0.0F, 0.0F,
				0.0F, 0);
		}
	}

	/** Id of the Lethal Company actor behind a damage source, or 0. */
	public static int attackerId(DamageSource source) {
		return source.getEntity() instanceof HostActorEntity proxy ? proxy.actorId() : 0;
	}
}
