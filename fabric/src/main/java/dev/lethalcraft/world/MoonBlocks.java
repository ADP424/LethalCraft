package dev.lethalcraft.world;

import com.mojang.serialization.Codec;
import dev.lethalcraft.LethalCraft;
import dev.lethalcraft.combat.HostActorEntity;
import java.util.HashSet;
import java.util.List;
import java.util.Set;
import net.fabricmc.fabric.api.attachment.v1.AttachmentRegistry;
import net.fabricmc.fabric.api.attachment.v1.AttachmentType;
import net.minecraft.core.BlockPos;
import net.minecraft.resources.Identifier;
import net.minecraft.server.level.ServerLevel;
import net.minecraft.world.entity.Entity;
import net.minecraft.world.entity.player.Player;
import net.minecraft.world.level.ChunkPos;
import net.minecraft.world.level.block.Block;
import net.minecraft.world.level.block.Blocks;
import net.minecraft.world.level.chunk.LevelChunk;
import net.minecraft.world.level.chunk.LevelChunkSection;
import net.minecraft.world.phys.AABB;

/**
 * What players leave on a moon doesn't stay there: Lethal Company makes the moon (and its facility) anew every
 * day, so when the ship leaves, the blocks built on the moon and in the facility, the holes dug into it and the
 * items and arrows lying about go. The ship's own blocks have gone back to the ship's stretch by then (ShipBlocks).
 *
 * <p>Each moon has its own stretch of the Minecraft world along X (a slot, SLOT_BLOCKS wide; slot 0 is the ship's
 * own space in flight). Every chunk in a moon's slot that has a block changed or a cell dug is noted (saved with
 * the world), so clearing a slot only has to look at those.
 */
public final class MoonBlocks {
	/** As the plugin's Driver.SlotBlocks: slot n is centred on x = n * SLOT_BLOCKS. */
	public static final int SLOT_BLOCKS = 4096;
	private static final int FLAGS = Block.UPDATE_CLIENTS | Block.UPDATE_KNOWN_SHAPE | Block.UPDATE_SUPPRESS_DROPS;

	/** The chunks (ChunkPos.pack) in moon slots that players changed. Immutable: changes set a new one. */
	private static final AttachmentType<Set<Long>> TOUCHED = AttachmentRegistry.<Set<Long>>builder()
		.persistent(Codec.LONG.listOf().xmap(Set::copyOf, List::copyOf))
		.buildAndRegister(Identifier.fromNamespaceAndPath(LethalCraft.MOD_ID, "moon_chunks"));

	private static boolean clearing;

	private MoonBlocks() {
	}

	/** Loads the class (registers the attachment) at mod start. */
	public static void init() {
		LethalCraft.LOG.info("LethalCraft: moon clean-up registered ({})", TOUCHED.identifier());
	}

	public static int slotOf(int blockX) {
		return Math.floorDiv(blockX + SLOT_BLOCKS / 2, SLOT_BLOCKS);
	}

	/** Server: something changed in this chunk (a block, a dug cell). Noted if it's on a moon. */
	public static void touched(ServerLevel level, int chunkX, int chunkZ) {
		if (clearing || slotOf(chunkX << 4) < 1) {
			return;
		}
		long key = ChunkPos.pack(chunkX, chunkZ);
		Set<Long> touched = level.getAttached(TOUCHED);
		if (touched != null && touched.contains(key)) {
			return;
		}
		Set<Long> more = touched != null ? new HashSet<>(touched) : new HashSet<>();
		more.add(key);
		level.setAttached(TOUCHED, Set.copyOf(more));
	}

	/** Server thread: everything players left in this moon slot goes. */
	public static void clear(ServerLevel level, int slot) {
		Set<Long> touched = level.getAttached(TOUCHED);
		if (slot < 1 || touched == null || touched.isEmpty()) {
			return;
		}
		Set<Long> keep = new HashSet<>();
		int chunks = 0, blocks = 0, entities = 0;
		clearing = true;
		try {
			for (long key : touched) {
				int cx = ChunkPos.getX(key), cz = ChunkPos.getZ(key);
				if (slotOf(cx << 4) != slot) {
					keep.add(key);
					continue;
				}
				LevelChunk chunk = level.getChunk(cx, cz);
				blocks += clearBlocks(level, chunk);
				chunk.removeAttached(HostDig.DUG);
				entities += clearEntities(level, cx, cz);
				chunks++;
			}
		} finally {
			clearing = false;
		}
		level.setAttached(TOUCHED, Set.copyOf(keep));
		LethalCraft.LOG.info("LethalCraft: moon slot {} cleared: {} blocks, {} entities in {} chunks", slot, blocks, entities, chunks);
	}

	private static int clearBlocks(ServerLevel level, LevelChunk chunk) {
		int n = 0;
		LevelChunkSection[] sections = chunk.getSections();
		int x0 = chunk.getPos().getMinBlockX(), z0 = chunk.getPos().getMinBlockZ();
		BlockPos.MutableBlockPos pos = new BlockPos.MutableBlockPos();
		for (int i = 0; i < sections.length; i++) {
			LevelChunkSection section = sections[i];
			if (section == null || section.hasOnlyAir()) {
				continue;
			}
			int y0 = level.getSectionYFromSectionIndex(i) << 4;
			for (int y = 0; y < 16; y++) {
				for (int z = 0; z < 16; z++) {
					for (int x = 0; x < 16; x++) {
						if (section.getBlockState(x, y, z).isAir()) {
							continue;
						}
						pos.set(x0 + x, y0 + y, z0 + z);
						// No spilling: a chest's contents go with it.
						level.removeBlockEntity(pos);
						level.setBlock(pos, Blocks.AIR.defaultBlockState(), FLAGS);
						n++;
					}
				}
			}
		}
		return n;
	}

	/** Dropped items, arrows, falling blocks...: not players, and not the monsters' stand-ins (Lethal Company's to remove). */
	private static int clearEntities(ServerLevel level, int cx, int cz) {
		AABB box = new AABB(cx << 4, level.getMinY(), cz << 4, (cx << 4) + 16, level.getMaxY() + 1, (cz << 4) + 16);
		List<Entity> left = level.getEntitiesOfClass(Entity.class, box, e -> !(e instanceof Player) && !(e instanceof HostActorEntity));
		for (Entity e : left) {
			e.discard();
		}
		return left.size();
	}
}
