package dev.lethalcraft.client;

import dev.lethalcraft.LethalCraft;
import java.io.IOException;
import java.nio.charset.StandardCharsets;
import java.nio.file.Files;
import java.nio.file.Path;
import java.util.Comparator;
import java.util.Set;
import java.util.concurrent.ConcurrentHashMap;
import java.util.stream.Stream;
import net.minecraft.SharedConstants;
import net.minecraft.client.Minecraft;

/**
 * Lethal Company's item icons in Minecraft: its plugin draws every item's icon to
 * %LOCALAPPDATA%\LethalCraft\item-icons (one PNG per key, then stamp.txt), and this builds a resource
 * pack from them (resourcepacks/lethalcraft-items): per key an item model like the token's (empty
 * in first person, where the game's own object shows) with that icon. Tokens name their model by
 * key (GameItems.refresh); keys without an icon fall back to the token's own look
 * (ModelManagerMixin). The pack is rebuilt and resources reloaded only when the set of icons
 * changes (new items, a new mod), so normally once ever.
 */
public final class ItemIconPack {
	private static final String PACK = "lethalcraft-items";
	private static final String PACK_ID = "file/" + PACK;
	private static final int CHECK_TICKS = 40;

	/** Keys that have an icon model in the loaded pack. */
	private static final Set<String> KNOWN = ConcurrentHashMap.newKeySet();
	private static int ticks = CHECK_TICKS - 1;
	private static String built;
	private static boolean loadedKnown;

	private ItemIconPack() {
	}

	public static boolean known(String key) {
		return KNOWN.contains(key);
	}

	private static Path icons() {
		String local = System.getenv("LOCALAPPDATA");
		return local == null ? null : Path.of(local, "LethalCraft", "item-icons");
	}

	private static Path pack(Minecraft minecraft) {
		return minecraft.gameDirectory.toPath().resolve("resourcepacks").resolve(PACK);
	}

	/** Client tick: now and then, see whether Lethal Company has written a new set of icons. */
	public static void tick(Minecraft minecraft) {
		if (!loadedKnown) {
			loadedKnown = true;
			readKnown(pack(minecraft));
		}
		if (++ticks < CHECK_TICKS || minecraft.gui.overlay() != null) {
			return;
		}
		ticks = 0;
		Path icons = icons();
		if (icons == null) {
			return;
		}
		try {
			Path stampFile = icons.resolve("stamp.txt");
			if (!Files.exists(stampFile)) {
				return;
			}
			String stamp = Files.readString(stampFile, StandardCharsets.UTF_8);
			Path pack = pack(minecraft);
			if (built == null && Files.exists(pack.resolve("stamp.txt"))) {
				built = Files.readString(pack.resolve("stamp.txt"), StandardCharsets.UTF_8);
			}
			if (stamp.equals(built)) {
				ensureSelected(minecraft, false);
				return;
			}
			build(icons, pack, stamp);
			built = stamp;
			readKnown(pack);
			ensureSelected(minecraft, true);
		} catch (IOException | RuntimeException e) {
			LethalCraft.LOG.warn("LethalCraft: item icons: {}", e.toString());
			ticks = -20 * 60; // try again in a minute
		}
	}

	private static void build(Path icons, Path pack, String stamp) throws IOException {
		if (Files.exists(pack)) {
			try (Stream<Path> old = Files.walk(pack)) {
				old.sorted(Comparator.reverseOrder()).forEach(p -> p.toFile().delete());
			}
		}
		Path assets = pack.resolve("assets").resolve(LethalCraft.MOD_ID);
		Path items = assets.resolve("items").resolve("game");
		Path models = assets.resolve("models").resolve("item").resolve("game");
		Path textures = assets.resolve("textures").resolve("item").resolve("game");
		Files.createDirectories(items);
		Files.createDirectories(models);
		Files.createDirectories(textures);
		int count = 0;
		try (Stream<Path> pngs = Files.list(icons)) {
			for (Path png : (Iterable<Path>) pngs::iterator) {
				String file = png.getFileName().toString();
				if (!file.endsWith(".png")) {
					continue;
				}
				String key = file.substring(0, file.length() - 4);
				if (!key.matches("[a-z0-9_]+")) {
					continue;
				}
				Files.copy(png, textures.resolve(file));
				Files.writeString(models.resolve(key + ".json"),
					"{\"parent\":\"lethalcraft:item/game_item\",\"textures\":{\"layer0\":\"lethalcraft:item/game/" + key + "\"}}", StandardCharsets.UTF_8);
				Files.writeString(items.resolve(key + ".json"),
					"{\"model\":{\"type\":\"minecraft:model\",\"model\":\"lethalcraft:item/game/" + key + "\"}}", StandardCharsets.UTF_8);
				count++;
			}
		}
		int major = SharedConstants.RESOURCE_PACK_FORMAT_MAJOR, minor = SharedConstants.RESOURCE_PACK_FORMAT_MINOR;
		Files.writeString(pack.resolve("pack.mcmeta"), "{\"pack\":{\"description\":\"Lethal Company item icons (LethalCraft, made automatically)\",\"min_format\":["
			+ major + "," + minor + "],\"max_format\":[" + major + "," + minor + "]}}", StandardCharsets.UTF_8);
		Files.writeString(pack.resolve("stamp.txt"), stamp, StandardCharsets.UTF_8);
		LethalCraft.LOG.info("LethalCraft: item icons: built resource pack with {} icons", count);
	}

	private static void readKnown(Path pack) {
		KNOWN.clear();
		Path models = pack.resolve("assets").resolve(LethalCraft.MOD_ID).resolve("items").resolve("game");
		if (!Files.isDirectory(models)) {
			return;
		}
		try (Stream<Path> files = Files.list(models)) {
			files.forEach(f -> {
				String n = f.getFileName().toString();
				if (n.endsWith(".json")) {
					KNOWN.add(n.substring(0, n.length() - 5));
				}
			});
		} catch (IOException ignored) {
		}
	}

	/** The pack on (and, if it changed, resources reloaded): Minecraft's own pack switch, saved with its options. */
	private static void ensureSelected(Minecraft minecraft, boolean changed) {
		var repo = minecraft.getResourcePackRepository();
		if (!repo.getSelectedIds().contains(PACK_ID)) {
			repo.reload();
			if (!repo.isAvailable(PACK_ID) || !repo.addPack(PACK_ID)) {
				LethalCraft.LOG.warn("LethalCraft: item icons: resource pack {} didn't load", PACK_ID);
				ticks = -20 * 60;
				return;
			}
			minecraft.options.updateResourcePacks(repo);
			LethalCraft.LOG.info("LethalCraft: item icons: resource pack on");
			return;
		}
		if (changed) {
			repo.reload();
			minecraft.reloadResourcePacks();
			LethalCraft.LOG.info("LethalCraft: item icons: reloading resources for the new icons");
		}
	}
}
