package dev.lethalcraft.client;

import dev.lethalcraft.LethalCraft;
import dev.lethalcraft.client.render.WorldExporter;
import dev.lethalcraft.link.Proto;
import dev.lethalcraft.link.HostLink;
import dev.lethalcraft.world.HostCollision;
import net.minecraft.client.Camera;
import net.minecraft.client.Minecraft;
import net.minecraft.client.player.LocalPlayer;
import net.minecraft.server.level.ServerPlayer;
import net.minecraft.world.phys.Vec3;
import org.lwjgl.sdl.SDLVideo;

/**
 * Per-frame glue between the Minecraft client and Lethal Company. Everything here runs on the render
 * thread, called from MinecraftMixin.
 */
public final class HostClient {
	private static final boolean SHOW_WINDOW = Boolean.getBoolean("lethalcraft.showWindow");
	// Started by Lethal Company (LethalCraft's bundled instance passes -Dlethalcraft.startHidden=true): no window and
	// no title-screen music from the first frame, even while Lethal Company is paused (Alt-Tabbed) and the
	// two haven't linked up yet. Otherwise the window only goes once Lethal Company is there.
	private static final boolean START_HIDDEN = Boolean.getBoolean("lethalcraft.startHidden");
	private static boolean startedHidden;

	private static final HostLink.HostState sky = new HostLink.HostState();
	private static final HostLink.McState mc = new HostLink.McState();
	private static volatile boolean linked;
	private static boolean tookOver;
	private static boolean windowHidden;
	private static int appliedViewportW, appliedViewportH;

	// Teleport / hold state: Lethal Company decides where the player is after loads, doors and respawns.
	private static int lastTeleportSeq = -1;
	private static int teleportAck;
	private static boolean teleportPending;
	private static LocalPlayer lastPlayer;
	private static Vec3 holdPos;
	private static Vec3 unlinkedHold;
	private static long holdSince;
	private static long qpcFreq;
	private static LocalPlayer eyePlayer;
	private static float eyeSmoothed;
	private static long frameCounter;
	private static int lastPacedSeq;
	private static boolean hostStalled;
	private static int exporterErrors;

	private HostClient() {
	}

	public static boolean linked() {
		return linked;
	}

	/**
	 * True once Lethal Company has connected in this session. From then on Minecraft never touches the
	 * real mouse or keyboard again (even if Lethal Company closes), since its window is hidden.
	 */
	public static boolean tookOver() {
		return tookOver;
	}

	public static HostLink.HostState sky() {
		return sky;
	}

	/** Start of Minecraft.runTick: pull state and input from Lethal Company before anything else runs. */
	public static void beginFrame() {
		HostLink.poll();
		quitWithHost(Minecraft.getInstance());
		if (START_HIDDEN && !startedHidden) {
			startedHidden = true;
			Minecraft minecraft = Minecraft.getInstance();
			hideWindowOnce(minecraft);
			minecraft.options.getSoundSourceOptionInstance(net.minecraft.sounds.SoundSource.MUSIC).set(0.0);
			minecraft.getMusicManager().stopPlaying();
		}
		boolean nowLinked = HostLink.active();
		if (nowLinked) {
			HostLink.readSkyState(sky); // on a torn read we simply keep last frame's state
			dev.lethalcraft.world.HostWater.refresh();
		} else {
			dev.lethalcraft.world.HostWater.clear();
		}
		if (nowLinked != linked) {
			linked = nowLinked;
			LethalCraft.LOG.info("LethalCraft: Lethal Company link {}", linked ? "up" : "down");
			if (linked) {
				tookOver = true;
				unlinkedHold = null;
				HostCollision.startConsumer();
				applyLinkedOptions();
			} else {
				InputBridge.releaseAll();
				LocalPlayer player = Minecraft.getInstance().player;
				unlinkedHold = player != null ? player.position() : null;
			}
		}
		if (!linked) {
			return;
		}

		Minecraft minecraft = Minecraft.getInstance();
		applyWindowVisibility(minecraft);
		applyViewportSize(minecraft);
		MirrorWorld.openWhenReady(minecraft);

		if (sky.menuOpen() || sky.loading()) {
			InputBridge.releaseAll();
		}
		InputBridge.drain(minecraft);
		ProxySync.frame(minecraft);

		LocalPlayer player = minecraft.player;
		if (player == null) {
			lastPlayer = null;
			return;
		}

		// A new player object means we just joined or respawned: put it where Lethal Company's player is
		// (and any hold from before is the old player's: a respawn after falling out of the world
		// would be held right back where it died).
		if (player != lastPlayer) {
			lastPlayer = player;
			teleportPending = true;
			holdPos = null;
		}
		if (sky.teleportSeq != lastTeleportSeq) {
			lastTeleportSeq = sky.teleportSeq;
			teleportPending = true;
		}
		if (teleportPending && sky.inGame() && !sky.loading()) {
			requestTeleport(minecraft, sky.x, sky.y, sky.z, sky.yaw, sky.pitch);
			teleportAck = sky.teleportSeq;
			teleportPending = false;
			holdPos = new Vec3(sky.x, sky.y, sky.z);
		}

		// Look direction is driven by Lethal Company (zero-latency camera); MC uses it for everything else.
		if (minecraft.gui.screen() == null) {
			player.setYRot(sky.yaw);
			player.setXRot(sky.pitch);
			player.yRotO = sky.yaw;
			player.xRotO = sky.pitch;
		}
	}

	// Minecraft is started with Lethal Company (the BepInEx plugin launches it), so it goes when that Lethal Company has
	// closed for good: saved and shut down the normal way. -Dlethalcraft.quitWithHost=false keeps it
	// running instead (development: restarting Lethal Company without restarting Minecraft).
	private static final boolean QUIT_WITH_HOST = Boolean.parseBoolean(System.getProperty("lethalcraft.quitWithHost", "true"));
	private static long hostGoneSince;
	private static long nextHostCheck;
	// Started hidden by Lethal Company but never connected: nobody can see or use this Minecraft, and it
	// would stop the next Lethal Company from starting a fresh one ("already running"). It goes after this.
	private static final long NEVER_CONNECTED_QUIT_MS = 10 * 60 * 1000;
	private static final long STARTED_AT = System.currentTimeMillis();
	private static boolean gaveUpWaiting;

	private static void quitWithHost(Minecraft minecraft) {
		int pid = HostLink.hostPid();
		long now = System.currentTimeMillis();
		if (QUIT_WITH_HOST && START_HIDDEN && pid == 0 && !tookOver && !gaveUpWaiting && now - STARTED_AT > NEVER_CONNECTED_QUIT_MS) {
			gaveUpWaiting = true;
			LethalCraft.LOG.warn("LethalCraft: started hidden but Lethal Company never connected in {} minutes; quitting", NEVER_CONNECTED_QUIT_MS / 60000);
			minecraft.stop();
			return;
		}
		if (!QUIT_WITH_HOST || pid == 0 || now < nextHostCheck) {
			return;
		}
		nextHostCheck = now + 1000;
		if (ProcessHandle.of(pid).map(ProcessHandle::isAlive).orElse(false)) {
			hostGoneSince = 0;
			return;
		}
		if (hostGoneSince == 0) {
			hostGoneSince = now;
		} else if (now - hostGoneSince > 5000) {
			LethalCraft.LOG.info("LethalCraft: Lethal Company (pid {}) has closed; saving and quitting", pid);
			minecraft.stop();
		}
	}

	/** Called at the end of every client tick. */
	public static void clientTick(Minecraft minecraft) {
		MirrorWorld.tick(minecraft);
		DiscordPresence.tick(minecraft);
		HostDigClient.tick(minecraft);
		HostFootsteps.tick(minecraft);
		freezeWhileUnlinked(minecraft);
		holdUntilReady(minecraft);
		publishTick(minecraft);
		syncTimeOfDay(minecraft);
		if (linked) {
			LobbyWorld.tick(minecraft);
		}
	}

	private static long nextTimeSync;

	/**
	 * Minecraft's clock follows the host's hour (HostState.gameHour, 0-24): its own lighting of the
	 * first-person hand and held item then matches the host's mood (a dark Lethal Company facility is night,
	 * the truck and shop are day). Only the integrated server's world, and only when it's off by more
	 * than a few minutes of game time.
	 */
	private static void syncTimeOfDay(Minecraft minecraft) {
		var server = minecraft.getSingleplayerServer();
		long now = System.currentTimeMillis();
		if (!linked || server == null || now < nextTimeSync || !sky.inGame()) {
			return;
		}
		nextTimeSync = now + 1000;
		float hour = sky.gameHour;
		if (!(hour >= 0.0F && hour <= 24.0F)) {
			return;
		}
		long target = Math.floorMod((long) ((hour - 6.0F) * 1000.0F), 24000L);
		server.execute(() -> {
			var level = minecraft.player != null ? server.getLevel(minecraft.player.level().dimension()) : null;
			if (level == null) {
				return;
			}
			long current = Math.floorMod(level.getDefaultClockTime(), 24000L);
			long diff = Math.abs(current - target);
			if (Math.min(diff, 24000L - diff) > 200L) {
				server.getCommands().performPrefixedCommand(server.createCommandSourceStack().withSuppressedOutput(), "time set " + target);
			}
		});
	}

	/**
	 * Lethal Company went quiet (a long loading screen, a stall, or it closed). Its collision around the
	 * player may be about to change (interior doors), so keep the player exactly where they were
	 * instead of letting them fall; Lethal Company puts them where they belong when it's back.
	 */
	private static void freezeWhileUnlinked(Minecraft minecraft) {
		LocalPlayer player = minecraft.player;
		if (linked || !tookOver || player == null) {
			return;
		}
		if (unlinkedHold == null) {
			unlinkedHold = player.position();
		}
		player.setDeltaMovement(Vec3.ZERO);
		player.setPos(unlinkedHold.x, unlinkedHold.y, unlinkedHold.z);
		player.xo = unlinkedHold.x;
		player.yo = unlinkedHold.y;
		player.zo = unlinkedHold.z;
		player.resetFallDistance();
	}

	/**
	 * Hands Lethal Company the raw physics tick (previous + latest feet, smoothed eye height, walk bob) with a
	 * QueryPerformanceCounter timestamp. Lethal Company interpolates between them on its own frame clock,
	 * exactly like Minecraft's renderer does with partial ticks.
	 */
	private static void publishTick(Minecraft minecraft) {
		LocalPlayer player = minecraft.player;
		if (!linked || player == null) {
			return;
		}
		if (qpcFreq == 0) {
			qpcFreq = HostLink.qpcFrequency();
		}
		float tickMs = minecraft.level != null ? minecraft.level.tickRateManager().millisecondsPerTick() : 50.0F;
		// The tick really "happened" partial ticks ago (DeltaTracker keeps the remainder).
		float remainder = minecraft.getDeltaTracker().getGameTimeDeltaPartialTick(false);
		mc.tickQpc = HostLink.qpc() - (long) (remainder * tickMs * qpcFreq / 1000.0);
		mc.tickMs = tickMs;
		mc.prevX = player.xo;
		mc.prevY = player.yo;
		mc.prevZ = player.zo;
		mc.curX = player.getX();
		mc.curY = player.getY();
		mc.curZ = player.getZ();
		// Same smoothing as Camera.tick(): eye height eases halfway toward the target each tick.
		if (player != eyePlayer) {
			eyePlayer = player;
			eyeSmoothed = player.getEyeHeight();
		}
		mc.eyeHeightO = eyeSmoothed;
		eyeSmoothed += (player.getEyeHeight() - eyeSmoothed) * 0.5F;
		mc.eyeHeightT = eyeSmoothed;
		boolean bob = minecraft.options.bobView().get();
		var avatar = player.avatarState();
		mc.walkDistO = bob ? avatar.getInterpolatedWalkDistance(0.0F) : 0.0F;
		mc.walkDist = bob ? avatar.getInterpolatedWalkDistance(1.0F) : 0.0F;
		mc.bobO = bob ? avatar.getInterpolatedBob(0.0F) : 0.0F;
		mc.bob = bob ? avatar.getInterpolatedBob(1.0F) : 0.0F;
		HostLink.writeMcState(mc);
	}

	/** Freeze the player until Lethal Company's collision around them has arrived. */
	private static void holdUntilReady(Minecraft minecraft) {
		LocalPlayer player = minecraft.player;
		if (!linked || player == null) {
			return;
		}
		if (!sky.inGame() || sky.loading()) {
			// Lethal Company is on its main menu or a loading screen: park the player where they are.
			if (holdPos == null) {
				holdPos = player.position();
				// Never park them in the void below the world (or at its very bottom, where a new
				// world puts its spawn): Minecraft kills anyone down there, over and over.
				if (minecraft.level != null && holdPos.y < minecraft.level.getMinY() + 16) {
					holdPos = new Vec3(holdPos.x, 0.0, holdPos.z);
				}
			}
			teleportPending = true;
		}
		if (holdPos == null) {
			holdSince = 0;
			return;
		}
		if (holdSince == 0) {
			holdSince = System.currentTimeMillis();
		}
		int bx = (int) Math.floor(holdPos.x), by = (int) Math.floor(holdPos.y), bz = (int) Math.floor(holdPos.z);
		boolean known = HostCollision.isKnown(bx, by - 1, bz) && HostCollision.isKnown(bx, by, bz)
			&& HostCollision.isKnown(bx, by - HostCollision.REGION_SIZE, bz);
		// Release once there is actual ground below (or after a timeout, e.g. when mid-air on purpose).
		boolean ready = known && (HostCollision.hasSolidBelow(bx, by, bz, 12) || System.currentTimeMillis() - holdSince > 6000);
		if (ready && sky.inGame() && !sky.loading()) {
			// Lethal Company's feet can sit a fraction of a voxel inside our ground layer. Minecraft's
			// collision never pushes you out of a shape, so you'd drop through: lift out first.
			Vec3 safe = liftOutOfGeometry(player, holdPos);
			if (safe.y != holdPos.y) {
				player.setPos(safe.x, safe.y, safe.z);
				player.yo = safe.y;
				LethalCraft.LOG.info("LethalCraft: lifted player {} blocks out of the ground", String.format("%.3f", safe.y - holdPos.y));
			}
			holdPos = null;
			return;
		}
		player.setDeltaMovement(Vec3.ZERO);
		player.setPos(holdPos.x, holdPos.y, holdPos.z);
		player.xo = holdPos.x;
		player.yo = holdPos.y;
		player.zo = holdPos.z;
		player.resetFallDistance();
	}

	private static Vec3 liftOutOfGeometry(LocalPlayer player, Vec3 pos) {
		// Stand on the exact Lethal Company ground if it is slightly above the feet (up to 2.5 blocks).
		double ground = HostCollider.groundAt(pos.x, pos.y, pos.z, 2.5);
		return !Double.isNaN(ground) && ground > pos.y ? new Vec3(pos.x, ground, pos.z) : pos;
	}

	private static void requestTeleport(Minecraft minecraft, double x, double y, double z, float yaw, float pitch) {
		LocalPlayer player = minecraft.player;
		player.setPos(x, y, z);
		player.setDeltaMovement(Vec3.ZERO);
		player.resetFallDistance();
		var server = minecraft.getSingleplayerServer();
		if (server != null) {
			var uuid = player.getUUID();
			server.execute(() -> {
				ServerPlayer sp = server.getPlayerList().getPlayer(uuid);
				if (sp != null) {
					sp.teleportTo(x, y, z);
					sp.setYRot(yaw);
					sp.setXRot(pitch);
					sp.resetFallDistance();
				}
			});
		} else if (net.fabricmc.fabric.api.client.networking.v1.ClientPlayNetworking.canSend(dev.lethalcraft.net.LethalNet.Teleport.TYPE)) {
			// In a friend's world: their server moves us (see LethalNet.Teleport).
			net.fabricmc.fabric.api.client.networking.v1.ClientPlayNetworking.send(new dev.lethalcraft.net.LethalNet.Teleport(x, y, z, yaw, pitch));
		}
		LethalCraft.LOG.info("LethalCraft: teleported to {} {} {}", x, y, z);
	}

	/** After GameRenderer.render(): report the player to Lethal Company and ship the overlay frame. */
	public static void afterRender() {
		if (!linked) {
			return;
		}
		Minecraft minecraft = Minecraft.getInstance();
		LocalPlayer player = minecraft.player;
		int flags = 0;
		// Still loading (the player is in the level before its chunks are): not in the world yet, so
		// Lethal Company neither shows the loading screen nor lets Minecraft drive.
		boolean loading = minecraft.gui.screen() instanceof net.minecraft.client.gui.screens.LevelLoadingScreen;
		if (player != null && minecraft.level != null && !loading) {
			float partial = minecraft.getDeltaTracker().getGameTimeDeltaPartialTick(false);
			Vec3 feet = player.getPosition(partial);
			Camera camera = minecraft.gameRenderer.mainCamera();
			flags |= Proto.MC_IN_WORLD;
			if (player.onGround()) {
				flags |= Proto.MC_ON_GROUND;
			}
			if (player.isShiftKeyDown()) {
				flags |= Proto.MC_SNEAKING;
			}
			if (player.isSprinting()) {
				flags |= Proto.MC_SPRINTING;
			}
			if (player.isDeadOrDying()) {
				flags |= Proto.MC_DEAD;
			}
			if (player.isSwimming()) {
				flags |= Proto.MC_SWIMMING;
			}
			if (player.getAbilities().flying) {
				flags |= Proto.MC_FLYING;
			}
			mc.x = feet.x;
			mc.y = feet.y;
			mc.z = feet.z;
			mc.yaw = player.getYRot();
			mc.pitch = player.getXRot();
			// The eye, not the camera: in third person Minecraft's camera sits behind or in front.
			Vec3 eye = camera.isDetached() ? player.getEyePosition(partial) : camera.position();
			mc.eyeHeight = (float) (eye.y - feet.y);
			mc.eyeX = eye.x;
			mc.eyeY = eye.y;
			mc.eyeZ = eye.z;
			mc.fov = camera.getFov();
			// Minecraft's F5 camera: Lethal Company puts its camera where Minecraft's would be.
			mc.cameraMode = minecraft.options.getCameraType().ordinal();
			mc.health = player.getHealth();
			mc.selectedObject = GameItemsClient.selectedObject(player);
			mc.freeSlots = GameItemsClient.freeSlots(player);
			mc.maxHealth = player.getMaxHealth();
			mc.cameraDistance = camera.isDetached() ? (float) camera.position().distanceTo(player.getEyePosition(partial)) : 0.0F;
			// Walk bob, exactly what GameRenderer.bobView() uses this frame.
			var entityState = minecraft.gameRenderer.gameRenderState().levelRenderState.cameraRenderState.entityRenderState;
			boolean bob = minecraft.options.bobView().get() && entityState.isPlayer;
			mc.bobPhase = bob ? entityState.backwardsInterpolatedWalkDistance : 0.0F;
			mc.bobAmount = bob ? entityState.bob : 0.0F;
		}
		if (minecraft.gui.screen() != null) {
			flags |= Proto.MC_SCREEN_OPEN;
		}
		mc.flags = flags;
		mc.sensitivity = minecraft.options.sensitivity().get().floatValue();
		mc.teleportAck = holdPos == null ? teleportAck : teleportAck - 1; // not "arrived" until we are released
		mc.guiScale = minecraft.getWindow().getGuiScale();
		mc.frameCounter = ++frameCounter;
		HostLink.writeMcState(mc);

		if ((flags & Proto.MC_IN_WORLD) != 0) {
			try {
				WorldExporter.frame(minecraft, minecraft.getDeltaTracker().getGameTimeDeltaPartialTick(false));
			} catch (RuntimeException e) {
				if (exporterErrors++ < 5) {
					LethalCraft.LOG.error("LethalCraft: world export failed", e);
				}
			}
			FrameExporter.capture(minecraft);
		}
	}

	/** End of the frame: render at most once per Lethal Company frame instead of spinning freely. */
	public static void paceFrame() {
		if (!linked) {
			return;
		}
		if (hostStalled && (HostLink.skyStateSeq() >>> 1) == lastPacedSeq) {
			return; // Lethal Company is paused (menu / alt-tab): don't block every frame waiting for it
		}
		hostStalled = false;
		long deadline = System.nanoTime() + 25_000_000L;
		// HostState.seq advances by 2 per Lethal Company frame (odd while writing).
		while ((HostLink.skyStateSeq() >>> 1) == lastPacedSeq && System.nanoTime() < deadline) {
			Thread.onSpinWait();
			if (deadline - System.nanoTime() > 2_000_000L) {
				Thread.yield();
			}
		}
		int seqNow = HostLink.skyStateSeq() >>> 1;
		hostStalled = seqNow == lastPacedSeq;
		lastPacedSeq = seqNow;
	}

	private static void applyLinkedOptions() {
		Minecraft minecraft = Minecraft.getInstance();
		var options = minecraft.options;
		options.pauseOnLostFocus = false;
		options.vignette().set(false);
		options.enableVsync().set(false);
		options.framerateLimit().set(260);
		// Minecraft doesn't draw the world itself; these only decide how far out placed blocks,
		// arrows and Lethal Company enemy stand-ins stay loaded and simulated.
		options.renderDistance().set(8);
		options.simulationDistance().set(8);
		options.autoJump().set(false);
		options.onboardAccessibility = false;
		if (options.tutorialStep != net.minecraft.client.tutorial.TutorialSteps.NONE) {
			minecraft.getTutorial().setStep(net.minecraft.client.tutorial.TutorialSteps.NONE);
		}
		options.getSoundSourceOptionInstance(net.minecraft.sounds.SoundSource.MUSIC).set(0.0);
		options.save();
	}

	/** Before linking: hidden unless -Dlethalcraft.showWindow=true. */
	private static void hideWindowOnce(Minecraft minecraft) {
		if (windowHidden || SHOW_WINDOW) {
			return;
		}
		windowHidden = true;
		SDLVideo.SDL_HideWindow(minecraft.getWindow().handle());
		LethalCraft.LOG.info("LethalCraft: game window hidden (run with -Dlethalcraft.showWindow=true to keep it)");
	}

	/** Once linked: Lethal Company's [Debug] ShowMinecraftWindow setting (or -Dlethalcraft.showWindow) decides. */
	private static void applyWindowVisibility(Minecraft minecraft) {
		boolean hide = !(SHOW_WINDOW || sky.showWindow());
		if (hide == windowHidden) {
			return;
		}
		windowHidden = hide;
		if (hide) {
			SDLVideo.SDL_HideWindow(minecraft.getWindow().handle());
			LethalCraft.LOG.info("LethalCraft: game window hidden");
		} else {
			SDLVideo.SDL_ShowWindow(minecraft.getWindow().handle());
			LethalCraft.LOG.info("LethalCraft: game window shown (Lethal Company's [Debug] ShowMinecraftWindow)");
		}
	}

	private static void applyViewportSize(Minecraft minecraft) {
		int w = Math.min(sky.viewportW, Proto.MAX_OVERLAY_W);
		int h = Math.min(sky.viewportH, Proto.MAX_OVERLAY_H);
		if (w <= 0 || h <= 0 || (w == appliedViewportW && h == appliedViewportH)) {
			return;
		}
		appliedViewportW = w;
		appliedViewportH = h;
		minecraft.getWindow().setWindowed(w, h);
		LethalCraft.LOG.info("LethalCraft: sizing overlay to Lethal Company viewport {}x{}", w, h);
	}
}
