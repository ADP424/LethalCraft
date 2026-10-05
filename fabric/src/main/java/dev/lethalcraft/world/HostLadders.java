package dev.lethalcraft.world;

import dev.lethalcraft.link.HostLink;
import java.util.Map;
import java.util.UUID;
import java.util.concurrent.ConcurrentHashMap;
import net.minecraft.world.phys.AABB;

/**
 * Lethal Company's ladders are Minecraft ladders: inside one of the boxes Lethal Company sends (one
 * per ladder near the player, from its foot to just over its top), the player climbs the way
 * Minecraft's ladders work (LivingEntityClimbMixin): walk into it to go up, sneak to hold on, let go
 * to slide down.
 *
 * <p>The boxes come from this machine's Lethal Company (shared memory). In a shared world the server
 * also climbs guests, with the boxes their own Lethal Company sent (LethalNet.GuestSurroundings).
 */
public final class HostLadders {
	private static final long REFRESH_NANOS = 50_000_000L;
	private static volatile float[] boxes = new float[0];
	private static volatile long readAt;
	private static final Map<UUID, float[]> GUESTS = new ConcurrentHashMap<>();

	private HostLadders() {
	}

	/** A guest's ladders, as their Lethal Company sees them. */
	public static void setGuest(UUID guest, float[] guestBoxes) {
		GUESTS.put(guest, guestBoxes);
	}

	/** This machine's player (or anyone without boxes of their own): whose box touches one of the ladders. */
	public static boolean contains(AABB box) {
		if (!HostLink.active()) {
			return false;
		}
		long now = System.nanoTime();
		if (now - readAt > REFRESH_NANOS) {
			readAt = now;
			float[] read = HostLink.readLadders();
			if (read != null) {
				boxes = read;
			}
		}
		return touches(boxes, box);
	}

	/** A guest on the server: their own ladders. */
	public static boolean containsForGuest(UUID guest, AABB box) {
		float[] b = GUESTS.get(guest);
		return b != null && touches(b, box);
	}

	private static boolean touches(float[] b, AABB box) {
		for (int i = 0; i + 5 < b.length; i += 6) {
			if (box.maxX > b[i] && box.minX < b[i + 3] && box.maxY > b[i + 1] && box.minY < b[i + 4] && box.maxZ > b[i + 2] && box.minZ < b[i + 5]) {
				return true;
			}
		}
		return false;
	}
}
