package dev.lethalcraft.item;

import com.mojang.serialization.Codec;
import dev.lethalcraft.LethalCraft;
import java.util.HashMap;
import java.util.HashSet;
import java.util.LinkedHashMap;
import java.util.List;
import java.util.Map;
import java.util.Set;
import java.util.UUID;
import net.fabricmc.fabric.api.event.lifecycle.v1.ServerTickEvents;
import net.minecraft.core.Registry;
import net.minecraft.core.component.DataComponentType;
import net.minecraft.core.component.DataComponents;
import net.minecraft.core.registries.BuiltInRegistries;
import net.minecraft.core.registries.Registries;
import net.minecraft.network.chat.Component;
import net.minecraft.network.codec.ByteBufCodecs;
import net.minecraft.resources.Identifier;
import net.minecraft.resources.ResourceKey;
import net.minecraft.server.MinecraftServer;
import net.minecraft.server.level.ServerPlayer;
import net.minecraft.world.entity.player.Inventory;
import net.minecraft.world.item.Item;
import net.minecraft.world.item.ItemStack;
import net.minecraft.world.item.component.ItemLore;
import org.jspecify.annotations.Nullable;

/**
 * Lethal Company's items in Minecraft's inventory. Each object the player holds in the game (scrap,
 * a flashlight, a shovel...) is one token item here, stack size 1, naming the object by its network
 * id. The game keeps the real object and its networking; the token is where it sits in Minecraft's
 * hotbar (or offhand), what selecting it means, and what dropping it means.
 *
 * <p>Also: the player only has the hotbar and the offhand. The 27 main inventory slots take nothing
 * (SlotMixin, InventoryMixin), and anything that ends up there anyway goes to the hotbar or is dropped.
 *
 * <p>Server side. Each player's client sends its game's list (LethalNet.HeldGameItems); every tick the
 * player's tokens are made to match it: tokens for objects no longer held go, objects without a token
 * get one (the selected slot if it's empty, else the first free hotbar slot, else the offhand).
 */
public final class GameItems {
	public static final ResourceKey<Item> KEY = ResourceKey.create(Registries.ITEM, Identifier.fromNamespaceAndPath(LethalCraft.MOD_ID, "game_item"));

	/** The game's network object id of the object this token stands for. */
	public static final DataComponentType<Integer> OBJECT = Registry.register(
		BuiltInRegistries.DATA_COMPONENT_TYPE,
		Identifier.fromNamespaceAndPath(LethalCraft.MOD_ID, "game_object"),
		new DataComponentType.Builder<Integer>().persistent(Codec.INT).networkSynchronized(ByteBufCodecs.VAR_INT).build()
	);

	/** The object takes both hands in the game: it can't be in the offhand, and holding it locks the hotbar. */
	public static final DataComponentType<Boolean> TWO_HANDED = Registry.register(
		BuiltInRegistries.DATA_COMPONENT_TYPE,
		Identifier.fromNamespaceAndPath(LethalCraft.MOD_ID, "two_handed"),
		new DataComponentType.Builder<Boolean>().persistent(Codec.BOOL).networkSynchronized(ByteBufCodecs.BOOL).build()
	);

	public static final Item TOKEN = Registry.register(BuiltInRegistries.ITEM, KEY, new Item(new Item.Properties().setId(KEY).stacksTo(1)));

	public static final int HOTBAR = Inventory.SELECTION_SIZE;  // 9
	public static final int MAIN_END = Inventory.INVENTORY_SIZE; // 36
	public static final int OFFHAND = Inventory.SLOT_OFFHAND;    // 40
	private static final int BATTERY_STEPS = 100;
	private static final int DROP_GRACE_TICKS = 60;

	/** What a player's game holds (from their client), and the drops it hasn't caught up with yet. */
	private static final class Held {
		boolean valid;
		Map<Integer, Entry> items = new LinkedHashMap<>();
		final Map<Integer, Integer> dropped = new HashMap<>(); // id -> server tick it was dropped
		boolean warnedFull;
	}

	public record Entry(int id, boolean twoHanded, int value, float battery, boolean hasBattery, String name) {
	}

	private static final Map<UUID, Held> HELD = new HashMap<>();

	private GameItems() {
	}

	public static void init() {
		ServerTickEvents.END_SERVER_TICK.register(GameItems::serverTick);
		LethalCraft.LOG.info("LethalCraft: game items registered ({})", KEY.identifier());
	}

	public static boolean isToken(ItemStack stack) {
		return !stack.isEmpty() && stack.is(TOKEN);
	}

	public static int objectOf(ItemStack stack) {
		Integer id = isToken(stack) ? stack.get(OBJECT) : null;
		return id != null ? id : 0;
	}

	public static boolean twoHanded(ItemStack stack) {
		return isToken(stack) && Boolean.TRUE.equals(stack.get(TWO_HANDED));
	}

	/** A player's client: what their game holds now. Server thread. */
	public static void update(ServerPlayer player, boolean valid, List<Entry> items) {
		Held held = HELD.computeIfAbsent(player.getUUID(), u -> new Held());
		held.valid = valid;
		Map<Integer, Entry> map = new LinkedHashMap<>();
		for (Entry e : items) {
			if (e.id() != 0) {
				map.put(e.id(), e);
			}
		}
		held.items = map;
		held.dropped.keySet().retainAll(map.keySet());
	}

	/**
	 * The player dropped a token (Q, or out of the inventory screen): no Minecraft item entity, the
	 * game drops the real object instead. Until it has, the token isn't handed back. Server thread.
	 */
	public static void dropped(ServerPlayer player, ItemStack stack) {
		int id = objectOf(stack);
		Held held = HELD.computeIfAbsent(player.getUUID(), u -> new Held());
		held.dropped.put(id, player.level().getServer().getTickCount());
		dev.lethalcraft.net.LethalNet.sendDroppedGameItem(player, id);
		LethalCraft.LOG.info("LethalCraft: {} dropped game item {} ({})", player.getPlainTextName(), id, stack.getHoverName().getString());
	}

	private static void serverTick(MinecraftServer server) {
		int tick = server.getTickCount();
		Set<UUID> online = new HashSet<>();
		for (ServerPlayer player : server.getPlayerList().getPlayers()) {
			online.add(player.getUUID());
			hotbarOnly(player);
			Held held = HELD.get(player.getUUID());
			if (held != null && held.valid) {
				held.dropped.values().removeIf(t -> tick - t > DROP_GRACE_TICKS);
				reconcile(player, held);
			}
		}
		HELD.keySet().retainAll(online);
	}

	/** Anything in the 27 locked main slots goes to a free hotbar slot, or is dropped at the player's feet. */
	private static void hotbarOnly(ServerPlayer player) {
		Inventory inventory = player.getInventory();
		for (int i = HOTBAR; i < MAIN_END; i++) {
			ItemStack stack = inventory.getItem(i);
			if (stack.isEmpty()) {
				continue;
			}
			inventory.setItem(i, ItemStack.EMPTY);
			int free = firstEmptyHotbar(inventory);
			if (free >= 0 && !isToken(stack)) {
				inventory.setItem(free, stack);
			} else if (!isToken(stack)) {
				player.drop(stack, false, net.minecraft.util.Prediction.SERVER_ONLY);
			}
		}
	}

	private static int firstEmptyHotbar(Inventory inventory) {
		for (int i = 0; i < HOTBAR; i++) {
			if (inventory.getItem(i).isEmpty()) {
				return i;
			}
		}
		return -1;
	}

	private static void reconcile(ServerPlayer player, Held held) {
		Inventory inventory = player.getInventory();
		Set<Integer> seen = new HashSet<>();
		for (int i = 0; i < inventory.getContainerSize(); i++) {
			ItemStack stack = inventory.getItem(i);
			if (!isToken(stack)) {
				continue;
			}
			Entry e = keep(held, stack, seen);
			if (e == null) {
				inventory.setItem(i, ItemStack.EMPTY);
			} else {
				refresh(stack, e);
			}
		}
		var menu = player.containerMenu;
		ItemStack carried = menu.getCarried();
		if (isToken(carried)) {
			Entry e = keep(held, carried, seen);
			if (e == null) {
				menu.setCarried(ItemStack.EMPTY);
			} else {
				refresh(carried, e);
			}
		}
		for (Entry e : held.items.values()) {
			if (seen.contains(e.id()) || held.dropped.containsKey(e.id())) {
				continue;
			}
			int slot = inventory.getItem(inventory.getSelectedSlot()).isEmpty() ? inventory.getSelectedSlot() : firstEmptyHotbar(inventory);
			if (slot < 0 && !e.twoHanded() && inventory.getItem(OFFHAND).isEmpty()) {
				slot = OFFHAND;
			}
			if (slot < 0) {
				if (!held.warnedFull) {
					held.warnedFull = true;
					LethalCraft.LOG.warn("LethalCraft: no free hotbar slot for {}'s game item {} ({})", player.getPlainTextName(), e.id(), e.name());
				}
				continue;
			}
			held.warnedFull = false;
			inventory.setItem(slot, create(e));
			seen.add(e.id());
		}
	}

	/** The entry a token still stands for, or null if it should go (not held any more, dropped, or a duplicate). */
	private static @Nullable Entry keep(Held held, ItemStack stack, Set<Integer> seen) {
		int id = objectOf(stack);
		Entry e = held.items.get(id);
		if (e == null || held.dropped.containsKey(id) || !seen.add(id)) {
			return null;
		}
		return e;
	}

	public static ItemStack create(Entry e) {
		ItemStack stack = new ItemStack(TOKEN);
		stack.set(OBJECT, e.id());
		refresh(stack, e);
		return stack;
	}

	/**
	 * The key an item's icon goes by: its name, lower case, letters and digits joined by underscores
	 * (Lethal Company's ItemIcons writes the icons under the same key).
	 */
	public static String iconKey(String name) {
		StringBuilder sb = new StringBuilder();
		boolean gap = false;
		for (char ch : (name == null ? "" : name).toLowerCase(java.util.Locale.ROOT).toCharArray()) {
			if (ch >= 'a' && ch <= 'z' || ch >= '0' && ch <= '9') {
				if (gap && !sb.isEmpty()) {
					sb.append('_');
				}
				sb.append(ch);
				gap = false;
			} else {
				gap = true;
			}
		}
		return sb.isEmpty() ? "item" : sb.toString();
	}

	/** Name, value, battery and hands as the game has them now (only what changed, so slots don't resend every tick). */
	private static void refresh(ItemStack stack, Entry e) {
		// The game's own icon (the item-icons resource pack; the plain token look where there isn't one).
		Identifier model = Identifier.fromNamespaceAndPath(LethalCraft.MOD_ID, "game/" + iconKey(e.name()));
		if (!model.equals(stack.get(DataComponents.ITEM_MODEL))) {
			stack.set(DataComponents.ITEM_MODEL, model);
		}
		Component name = Component.literal(e.name().isEmpty() ? "Item" : e.name());
		if (!name.equals(stack.get(DataComponents.ITEM_NAME))) {
			stack.set(DataComponents.ITEM_NAME, name);
		}
		ItemLore lore = e.value() > 0 ? new ItemLore(List.of(Component.literal("Value: $" + e.value()))) : ItemLore.EMPTY;
		if (!lore.equals(stack.get(DataComponents.LORE))) {
			stack.set(DataComponents.LORE, lore);
		}
		if (!Boolean.valueOf(e.twoHanded()).equals(stack.get(TWO_HANDED))) {
			stack.set(TWO_HANDED, e.twoHanded());
		}
		if (e.hasBattery()) {
			int damage = Math.clamp(Math.round((1.0F - e.battery()) * BATTERY_STEPS), 0, BATTERY_STEPS - 1);
			if (!Integer.valueOf(BATTERY_STEPS).equals(stack.get(DataComponents.MAX_DAMAGE))) {
				stack.set(DataComponents.MAX_DAMAGE, BATTERY_STEPS);
			}
			if (!Integer.valueOf(damage).equals(stack.get(DataComponents.DAMAGE))) {
				stack.set(DataComponents.DAMAGE, damage);
			}
		} else if (stack.has(DataComponents.MAX_DAMAGE)) {
			stack.remove(DataComponents.MAX_DAMAGE);
			stack.remove(DataComponents.DAMAGE);
		}
	}
}
