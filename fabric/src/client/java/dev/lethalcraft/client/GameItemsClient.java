package dev.lethalcraft.client;

import dev.lethalcraft.item.GameItems;
import dev.lethalcraft.link.HostLink;
import dev.lethalcraft.link.Proto;
import dev.lethalcraft.net.LethalNet;
import java.util.ArrayList;
import java.util.List;
import net.fabricmc.fabric.api.client.networking.v1.ClientPlayNetworking;
import net.minecraft.client.Minecraft;
import net.minecraft.client.player.LocalPlayer;
import net.minecraft.world.entity.player.Inventory;

/**
 * Client side of the game's items in Minecraft's inventory (GameItems): passes the game's held list
 * (shared memory) to the server this player is on, tells the game when one of its tokens was
 * dropped, and keeps the hotbar on a two-handed object while the game holds it (the game's rule:
 * both hands are full until you put it down).
 */
public final class GameItemsClient {
	private static final HostLink.GameItems READ = new HostLink.GameItems();
	private static final int RESEND_TICKS = 20;
	private static int sentVersion = -1;
	private static boolean sentValid;
	private static int sinceSent;
	private static int lockedObject;

	private GameItemsClient() {
	}

	public static void register() {
		// In the host's world: our hits on the monsters' stand-ins land through our own Lethal Company.
		ClientPlayNetworking.registerGlobalReceiver(LethalNet.HitActor.TYPE, (payload, context) -> {
			if (HostLink.active()) {
				HostLink.pushEvent(Proto.EV_HIT_ACTOR, payload.formId(), payload.damage(), payload.pushX(), payload.pushZ(), payload.strength(), payload.flags(),
					payload.weapon());
			}
		});
		ClientPlayNetworking.registerGlobalReceiver(LethalNet.DroppedGameItem.TYPE, (payload, context) -> {
			if (HostLink.active()) {
				HostLink.pushEvent(Proto.EV_DROP_GAME_ITEM, payload.id(), 0, 0, 0, 0, 0);
			}
		});
	}

	private static int movedSeq = -1;

	/** Lethal Company's ship landed or took off: pass its block move on to the server (once per request). */
	private static void forwardBlockMove() {
		HostLink.BlockMove m = HostLink.readBlockMove();
		if (m == null) {
			movedSeq = 0;
			return;
		}
		if (movedSeq < 0) {
			movedSeq = m.seq(); // linked mid-session: what's there was already done
			return;
		}
		if (m.seq() == movedSeq || !ClientPlayNetworking.canSend(LethalNet.MoveShipBlocks.TYPE)) {
			return;
		}
		movedSeq = m.seq();
		ClientPlayNetworking.send(new LethalNet.MoveShipBlocks(new net.minecraft.core.BlockPos(m.minX(), m.minY(), m.minZ()),
			new net.minecraft.core.BlockPos(m.maxX(), m.maxY(), m.maxZ()), m.quarterTurns(), new net.minecraft.core.BlockPos(m.dx(), m.dy(), m.dz()),
			m.clearSlot(), m.clearFirst()));
	}

	private static int surroundingsTicks;

	/**
	 * A guest in the host's world: the server climbs and drowns us too, so it gets our own Lethal Company's
	 * ladders and water around us (the host's server knows only the host's).
	 */
	private static void sendSurroundings(Minecraft minecraft) {
		if (minecraft.isLocalServer() || ++surroundingsTicks < 5 || !ClientPlayNetworking.canSend(LethalNet.GuestSurroundings.TYPE)) {
			return;
		}
		surroundingsTicks = 0;
		float[] ladders = HostLink.readLadders();
		List<Float> l = new ArrayList<>();
		if (ladders != null) {
			for (float f : ladders) {
				l.add(f);
			}
		}
		HostLink.WaterGrid water = HostLink.readWaterGrid();
		List<Float> w = new ArrayList<>();
		int wx = 0, wz = 0;
		if (water != null) {
			wx = water.originX;
			wz = water.originZ;
			for (float f : water.surface) {
				w.add(f);
			}
		}
		ClientPlayNetworking.send(new LethalNet.GuestSurroundings(l, wx, wz, w));
	}

	/** End of each client tick, while linked. */
	public static void tick(Minecraft minecraft) {
		LocalPlayer player = minecraft.player;
		if (player != null) {
			forwardBlockMove();
			sendSurroundings(minecraft);
		}
		if (player == null || !HostLink.readGameItems(READ)) {
			lockedObject = 0;
			return;
		}
		sinceSent++;
		if ((READ.version != sentVersion || READ.valid != sentValid || sinceSent >= RESEND_TICKS) && ClientPlayNetworking.canSend(LethalNet.HeldGameItems.TYPE)) {
			List<GameItems.Entry> entries = new ArrayList<>(READ.items.size());
			for (HostLink.GameItem g : READ.items) {
				entries.add(new GameItems.Entry(g.id(), g.twoHanded(), g.value(), g.battery(), g.hasBattery(), g.name()));
			}
			ClientPlayNetworking.send(new LethalNet.HeldGameItems(READ.valid, entries));
			sentVersion = READ.version;
			sentValid = READ.valid;
			sinceSent = 0;
		}
		// Two hands full: the hotbar stays on that object until the game lets go of it.
		lockedObject = 0;
		for (HostLink.GameItem g : READ.items) {
			if (g.twoHanded() && g.inHand()) {
				lockedObject = g.id();
			}
		}
		if (lockedObject != 0) {
			Inventory inventory = player.getInventory();
			for (int i = 0; i < GameItems.HOTBAR; i++) {
				if (GameItems.objectOf(inventory.getItem(i)) == lockedObject) {
					if (inventory.getSelectedSlot() != i) {
						inventory.setSelectedSlot(i);
					}
					break;
				}
			}
		}
	}

	/** The game object selected in the main hand (0: a Minecraft item or nothing). */
	public static int selectedObject(LocalPlayer player) {
		return GameItems.objectOf(player.getMainHandItem());
	}

	/** Where a newly picked-up object could go: empty hotbar slots, plus the offhand if it's empty. */
	public static int freeSlots(LocalPlayer player) {
		Inventory inventory = player.getInventory();
		int free = 0;
		for (int i = 0; i < GameItems.HOTBAR; i++) {
			if (inventory.getItem(i).isEmpty()) {
				free++;
			}
		}
		if (inventory.getItem(GameItems.OFFHAND).isEmpty()) {
			free++;
		}
		return free;
	}
}
