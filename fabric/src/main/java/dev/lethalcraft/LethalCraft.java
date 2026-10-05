package dev.lethalcraft;

import dev.lethalcraft.combat.HostCombat;
import net.fabricmc.api.ModInitializer;
import net.minecraft.world.entity.EquipmentSlot;
import net.fabricmc.fabric.api.event.lifecycle.v1.ServerLifecycleEvents;
import net.fabricmc.fabric.api.networking.v1.ServerPlayConnectionEvents;
import net.minecraft.server.MinecraftServer;
import net.minecraft.server.level.ServerPlayer;
import net.minecraft.world.item.ItemStack;
import net.minecraft.world.item.Items;
import net.minecraft.world.level.gamerules.GameRules;
import org.slf4j.Logger;
import org.slf4j.LoggerFactory;

public final class LethalCraft implements ModInitializer {
	public static final String MOD_ID = "lethalcraft";
	public static final String WORLD_NAME = "LethalCraft";

	/** One of ours: the default mirror world or a save's own (WORLD_NAME-key). */
	public static boolean isMirrorWorld(String levelId) {
		return WORLD_NAME.equals(levelId) || levelId != null && levelId.startsWith(WORLD_NAME + "-");
	}
	public static final Logger LOG = LoggerFactory.getLogger(MOD_ID);
	private static final String KIT2_TAG = "lethalcraft_builder_kit";

	@Override
	public void onInitialize() {
		HostCombat.init();
		dev.lethalcraft.item.GameItems.init();
		dev.lethalcraft.net.LethalNet.init();
		dev.lethalcraft.world.HostDig.init();
		ServerLifecycleEvents.SERVER_STARTED.register(LethalCraft::configureServer);
		ServerPlayConnectionEvents.JOIN.register((handler, sender, server) -> {
			outOfTheVoid(handler.getPlayer());
			giveStarterKit(handler.getPlayer());
			giveBuilderKit(handler.getPlayer());
			dressTestGuest(handler.getPlayer());
		});
		net.fabricmc.fabric.api.entity.event.v1.ServerPlayerEvents.AFTER_RESPAWN.register((oldPlayer, newPlayer, alive) -> outOfTheVoid(newPlayer));
	}

	/**
	 * The mirror world is a void, so its spawn (where new players and respawns land) is the very
	 * bottom of the world, and a player saved after falling is below it: Minecraft kills them there
	 * again and again before Lethal Company can put them anywhere. Up to y 0 instead (Lethal Company's levels are
	 * around there), where they wait for Lethal Company.
	 */
	private static void outOfTheVoid(ServerPlayer player) {
		var level = player.level();
		if (player.getY() < level.getMinY() + 16) {
			LOG.info("LethalCraft: {} was at y {} (the void); moved up to y 0", player.getPlainTextName(), String.format("%.0f", player.getY()));
			player.teleportTo(level, player.getX(), 0.0, player.getZ(), java.util.Set.of(), player.getYRot(), player.getXRot(), false);
			player.setDeltaMovement(net.minecraft.world.phys.Vec3.ZERO);
			player.resetFallDistance();
		}
	}

	/** The mirror world is a void that only exists to host the player; Lethal Company drives time and spawning. */
	private static void configureServer(MinecraftServer server) {
		GameRules rules = server.getGameRules();
		rules.set(GameRules.ADVANCE_TIME, false, server);
		rules.set(GameRules.ADVANCE_WEATHER, false, server);
		rules.set(GameRules.SPAWN_MOBS, false, server);
		rules.set(GameRules.SPAWN_MONSTERS, false, server);
		rules.set(GameRules.SPAWN_PHANTOMS, false, server);
		rules.set(GameRules.SPAWN_PATROLS, false, server);
		rules.set(GameRules.SPAWN_WANDERING_TRADERS, false, server);
		rules.set(GameRules.PLAYER_MOVEMENT_CHECK, false, server);
		rules.set(GameRules.KEEP_INVENTORY, true, server);
		rules.set(GameRules.IMMEDIATE_RESPAWN, true, server);
		rules.set(GameRules.SHOW_ADVANCEMENT_MESSAGES, false, server);
		server.getCommands().performPrefixedCommand(server.createCommandSourceStack().withSuppressedOutput(), "time set noon");
		LOG.info("LethalCraft: mirror world configured");
	}

	/**
	 * Local multiplayer test guests (tools/fake_guest.py; named Guest, Guest2, ...) wear a random
	 * mix of iron and diamond armour, so they're easy to tell apart.
	 */
	private static void dressTestGuest(ServerPlayer player) {
		if (!player.getName().getString().startsWith("Guest")) {
			return;
		}
		var random = player.getRandom();
		EquipmentSlot[] slots = { EquipmentSlot.HEAD, EquipmentSlot.CHEST, EquipmentSlot.LEGS, EquipmentSlot.FEET };
		net.minecraft.world.item.Item[][] pieces = {
			{ Items.IRON_HELMET, Items.DIAMOND_HELMET },
			{ Items.IRON_CHESTPLATE, Items.DIAMOND_CHESTPLATE },
			{ Items.IRON_LEGGINGS, Items.DIAMOND_LEGGINGS },
			{ Items.IRON_BOOTS, Items.DIAMOND_BOOTS },
		};
		for (int i = 0; i < slots.length; i++) {
			player.setItemSlot(slots[i], new ItemStack(pieces[i][random.nextBoolean() ? 1 : 0]));
		}
		LOG.info("LethalCraft: dressed test guest {} in iron and diamond", player.getName().getString());
	}

	/** A new player's hotbar (the only inventory LethalCraft players have) and offhand. */
	private static void giveStarterKit(ServerPlayer player) {
		if (!player.getInventory().isEmpty()) {
			return;
		}
		var inventory = player.getInventory();
		inventory.add(new ItemStack(Items.DIAMOND_SWORD));
		inventory.add(new ItemStack(Items.DIAMOND_PICKAXE));
		inventory.add(new ItemStack(Items.BOW));
		inventory.add(new ItemStack(Items.ARROW, 64));
		inventory.add(new ItemStack(Items.COOKED_BEEF, 32));
		inventory.add(new ItemStack(Items.OAK_PLANKS, 64));
		inventory.add(new ItemStack(Items.COBBLESTONE, 64));
		inventory.add(new ItemStack(Items.TORCH, 32));
		inventory.add(new ItemStack(Items.LANTERN, 16));
		player.setItemSlot(net.minecraft.world.entity.EquipmentSlot.OFFHAND, new ItemStack(Items.SHIELD));
		LOG.info("LethalCraft: gave starter kit to {}", player.getName().getString());
	}

	/** Once per player: armour, since Lethal Company's monsters hit back. */
	private static void giveBuilderKit(ServerPlayer player) {
		if (player.entityTags().contains(KIT2_TAG)) {
			return;
		}
		equipIfEmpty(player, EquipmentSlot.HEAD, Items.IRON_HELMET);
		equipIfEmpty(player, EquipmentSlot.CHEST, Items.IRON_CHESTPLATE);
		equipIfEmpty(player, EquipmentSlot.LEGS, Items.IRON_LEGGINGS);
		equipIfEmpty(player, EquipmentSlot.FEET, Items.IRON_BOOTS);
		player.addTag(KIT2_TAG);
		LOG.info("LethalCraft: gave armour to {}", player.getName().getString());
	}

	private static void equipIfEmpty(ServerPlayer player, EquipmentSlot slot, net.minecraft.world.item.Item item) {
		if (player.getItemBySlot(slot).isEmpty()) {
			player.setItemSlot(slot, new ItemStack(item));
		} else {
			player.getInventory().add(new ItemStack(item));
		}
	}
}
