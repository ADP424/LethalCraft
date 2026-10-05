package dev.lethalcraft.client;

import dev.lethalcraft.LethalCraft;
import net.minecraft.client.Minecraft;
import net.minecraft.client.gui.screens.TitleScreen;
import net.minecraft.core.registries.Registries;
import net.minecraft.resources.Identifier;
import net.minecraft.resources.ResourceKey;
import net.minecraft.world.Difficulty;
import net.minecraft.world.level.GameType;
import net.minecraft.world.level.LevelSettings;
import net.minecraft.world.level.WorldDataConfiguration;
import net.minecraft.world.level.levelgen.WorldOptions;
import net.minecraft.world.level.levelgen.presets.WorldPreset;

/** Opens (or creates) the void "mirror" world automatically once Lethal Company is connected. */
public final class MirrorWorld {
	private static final ResourceKey<WorldPreset> PRESET =
		ResourceKey.create(Registries.WORLD_PRESET, Identifier.fromNamespaceAndPath(LethalCraft.MOD_ID, "mirror"));
	private static boolean attempted;
	private static long lastLog;
	// /join: a friend's world for this session (the e4mc link their "Open to LAN" shows); null: our own.
	private static @org.jspecify.annotations.Nullable String sessionJoin;
	// Shown in chat once the player is in a world again (why they're back in their own, ...).
	private static @org.jspecify.annotations.Nullable String pendingNote;
	// The folder of our own world we're in (or opening), or null.
	private static @org.jspecify.annotations.Nullable String opened;

	/**
	 * Our own world: the one that goes with the Lethal Company save being played (each save has its own
	 * Minecraft player, inventory and builds), or, before a save is loaded, whichever is open already
	 * (the plain mirror world at first).
	 */
	private static String desiredWorld() {
		String key = dev.lethalcraft.link.HostLink.readSaveKey();
		if (key.isEmpty()) {
			return opened != null ? opened : LethalCraft.WORLD_NAME;
		}
		return LethalCraft.WORLD_NAME + "-" + key;
	}

	private MirrorWorld() {
	}

	/**
	 * The address in config/lethalcraft.properties ({@code join=abc-def.e4mc.link}), if any. Written
	 * with the template below the first time, so there's something to fill in.
	 */
	private static @org.jspecify.annotations.Nullable String joinAddress(Minecraft minecraft) {
		java.nio.file.Path file = minecraft.gameDirectory.toPath().resolve("config").resolve("lethalcraft.properties");
		java.util.Properties props = new java.util.Properties();
		try {
			if (!java.nio.file.Files.exists(file)) {
				java.nio.file.Files.createDirectories(file.getParent());
				java.nio.file.Files.writeString(file, """
					# LethalCraft
					# To play in a friend's world instead of your own: put their address after join=
					# (the link e4mc shows them when they open their world to LAN), then restart Minecraft.
					join=
					""");
			}
			try (var in = java.nio.file.Files.newBufferedReader(file)) {
				props.load(in);
			}
		} catch (java.io.IOException e) {
			LethalCraft.LOG.warn("LethalCraft: couldn't read {}", file, e);
			return null;
		}
		String join = props.getProperty("join", "").trim();
		return join.isEmpty() ? null : join;
	}

	/** People paste all sorts: "https://abc-def.e4mc.link/", " abc-def.e4mc.link ". */
	public static String cleanAddress(String link) {
		return link.trim().replaceFirst("^[A-Za-z]+://", "").replaceAll("/+$", "");
	}

	/** The address of the friend's world this session plays in (/join, or the Lethal Company lobby's), or null for our own. */
	public static @org.jspecify.annotations.Nullable String sessionAddress() {
		return sessionJoin;
	}

	/** /join: leave this world and play in a friend's (their e4mc link, or any server address). */
	public static void joinFriend(Minecraft minecraft, String link) {
		String address = cleanAddress(link);
		if (address.isEmpty()) {
			return;
		}
		LethalCraft.LOG.info("LethalCraft: /join {}", address);
		sessionJoin = address;
		leaveWorld(minecraft);
	}

	/** The friend's world we're in (the address we joined), or null in our own. */
	public static @org.jspecify.annotations.Nullable String friendAddress(Minecraft minecraft) {
		if (sessionJoin != null) {
			return sessionJoin;
		}
		var server = minecraft.isLocalServer() ? null : minecraft.getCurrentServer();
		return server != null ? server.ip : null;
	}

	/** /leave: back to our own world. */
	public static void leaveFriend(Minecraft minecraft) {
		if (sessionJoin == null) {
			minecraft.gui.hud.getChat().addClientSystemMessage(net.minecraft.network.chat.Component.literal("You're already in your own world."));
			return;
		}
		LethalCraft.LOG.info("LethalCraft: /leave {}", sessionJoin);
		sessionJoin = null;
		pendingNote = "Back in your own world.";
		leaveWorld(minecraft);
	}

	private static void leaveWorld(Minecraft minecraft) {
		attempted = false;
		minecraft.disconnectFromWorld(net.minecraft.client.multiplayer.ClientLevel.DEFAULT_QUIT_MESSAGE);
		minecraft.gui.setScreen(new TitleScreen());  // openWhenReady takes it from the title screen
	}

	/** Every client tick: a note for the player once they're in a world again; the save's own world when the save changes. */
	public static void tick(Minecraft minecraft) {
		if (sessionJoin == null && opened != null && minecraft.level != null && minecraft.isLocalServer() && dev.lethalcraft.link.HostLink.active()) {
			String want = desiredWorld();
			if (!want.equals(opened)) {
				LethalCraft.LOG.info("LethalCraft: Lethal Company is playing another save; switching to its world {}", want);
				pendingNote = null;
				opened = null;
				leaveWorld(minecraft);
				return;
			}
		}
		if (pendingNote != null && minecraft.player != null) {
			minecraft.gui.hud.getChat().addClientSystemMessage(net.minecraft.network.chat.Component.literal(pendingNote));
			pendingNote = null;
		}
	}

	public static void openWhenReady(Minecraft minecraft) {
		// Couldn't reach a friend's world, or it closed under us: back to our own, and say why.
		if (minecraft.gui.screen() instanceof net.minecraft.client.gui.screens.DisconnectedScreen && minecraft.level == null) {
			pendingNote = sessionJoin != null
				? "Couldn't stay in " + sessionJoin + " (check the link, and that your friend's world is still open to LAN). You're back in your own world."
				: "Disconnected. You're back in your own world.";
			LethalCraft.LOG.info("LethalCraft: disconnected; back to the mirror world");
			if (sessionJoin != null) {
				LobbyWorld.joinFailed(sessionJoin);
			}
			sessionJoin = null;
			attempted = false;
			minecraft.gui.setScreen(new TitleScreen());
			return;
		}
		if (attempted && minecraft.level == null && minecraft.gui.screen() != null && System.currentTimeMillis() - lastLog > 5000) {
			lastLog = System.currentTimeMillis();
			LethalCraft.LOG.info("LethalCraft: still not in the mirror world; current screen {}", minecraft.gui.screen().getClass().getName());
		}
		if (attempted || minecraft.level != null || minecraft.gui.overlay() != null) {
			return;
		}
		// Wait for the menu to settle on the title screen; skip any first-launch prompts in front of it.
		if (!(minecraft.gui.screen() instanceof TitleScreen)) {
			if (minecraft.gui.screen() != null && System.currentTimeMillis() - lastLog > 5000) {
				lastLog = System.currentTimeMillis();
				LethalCraft.LOG.info("LethalCraft: waiting on screen {} before opening the mirror world", minecraft.gui.screen().getClass().getName());
			}
			if (minecraft.gui.screen() == null || minecraft.gui.screen().getClass().getName().contains("Onboarding")) {
				minecraft.gui.setScreen(new TitleScreen());
			}
			return;
		}
		TitleScreen title = (TitleScreen) minecraft.gui.screen();
		attempted = true;
		// Multiplayer: join a friend's world (their e4mc link, or any server address) instead.
		String join = sessionJoin != null ? sessionJoin : joinAddress(minecraft);
		if (join != null) {
			LethalCraft.LOG.info("LethalCraft: joining {}", join);
			pendingNote = "Joined " + join + ". Type /leave to go back to your own world.";
			net.minecraft.client.gui.screens.ConnectScreen.startConnecting(title, minecraft, net.minecraft.client.multiplayer.resolver.ServerAddress.parseString(join),
				new net.minecraft.client.multiplayer.ServerData("LethalCraft", join, net.minecraft.client.multiplayer.ServerData.Type.OTHER), false, null);
			return;
		}
		String name = desiredWorld();
		opened = name;
		if (minecraft.getLevelSource().levelExists(name)) {
			LethalCraft.LOG.info("LethalCraft: opening mirror world {}", name);
			minecraft.createWorldOpenFlows().openWorld(name, () -> minecraft.gui.setScreen(title));
			return;
		}
		LethalCraft.LOG.info("LethalCraft: creating mirror world {}", name);
		LevelSettings settings = new LevelSettings(
			name,
			GameType.SURVIVAL,
			new LevelSettings.DifficultySettings(Difficulty.NORMAL, false, false),
			true,
			WorldDataConfiguration.DEFAULT
		);
		minecraft.createWorldOpenFlows().createFreshLevel(
			name,
			settings,
			new WorldOptions(0L, false, false),
			registries -> registries.lookupOrThrow(Registries.WORLD_PRESET).getOrThrow(PRESET).value().createWorldDimensions(),
			title
		);
	}
}
