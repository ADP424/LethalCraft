package dev.lethalcraft.world;

import dev.lethalcraft.LethalCraft;
import java.util.ArrayList;
import java.util.List;
import net.minecraft.core.BlockPos;
import net.minecraft.nbt.CompoundTag;
import net.minecraft.server.level.ServerLevel;
import net.minecraft.world.level.block.Block;
import net.minecraft.world.level.block.Blocks;
import net.minecraft.world.level.block.Rotation;
import net.minecraft.world.level.block.entity.BlockEntity;
import net.minecraft.world.level.block.state.BlockState;

/**
 * Blocks built on Lethal Company's ship go where the ship goes. While it's landed the ship is part
 * of the moon's stretch of the Minecraft world; in flight it has its own. When it lands or takes
 * off, Lethal Company asks for the blocks around the ship to move from one stretch to the other:
 * a box, a number of quarter turns about Y, and an offset. Block entities (chests, signs...) go
 * with their blocks. No neighbour updates while moving, so doors, beds and torches arrive whole.
 */
public final class ShipBlocks {
	private static final int MAX_BLOCKS = 200_000;
	private static final int FLAGS = Block.UPDATE_CLIENTS | Block.UPDATE_KNOWN_SHAPE;

	private ShipBlocks() {
	}

	private record Moved(BlockPos to, BlockState state, CompoundTag entity) {
	}

	/** Server thread. Every block in [min, max] goes to rotate(pos, quarterTurns) + offset. */
	public static void move(ServerLevel level, BlockPos min, BlockPos max, int quarterTurns, BlockPos offset) {
		long volume = (long) (max.getX() - min.getX() + 1) * (max.getY() - min.getY() + 1) * (max.getZ() - min.getZ() + 1);
		if (volume <= 0 || volume > MAX_BLOCKS) {
			LethalCraft.LOG.warn("LethalCraft: ship blocks: box {} .. {} is too big to move ({} blocks)", min, max, volume);
			return;
		}
		int q = Math.floorMod(quarterTurns, 4);
		Rotation rotation = switch (q) {
			case 1 -> Rotation.CLOCKWISE_90;
			case 2 -> Rotation.CLOCKWISE_180;
			case 3 -> Rotation.COUNTERCLOCKWISE_90;
			default -> Rotation.NONE;
		};
		var registries = level.registryAccess();
		List<BlockPos> from = new ArrayList<>();
		List<Moved> moved = new ArrayList<>();
		for (BlockPos pos : BlockPos.betweenClosed(min, max)) {
			BlockState state = level.getBlockState(pos);
			if (state.isAir()) {
				continue;
			}
			BlockEntity be = level.getBlockEntity(pos);
			CompoundTag tag = be != null ? be.saveWithFullMetadata(registries) : null;
			BlockPos p = pos.immutable();
			from.add(p);
			moved.add(new Moved(rotate(p, q).offset(offset), state.rotate(rotation), tag));
		}
		if (moved.isEmpty()) {
			return;
		}
		for (BlockPos pos : from) {
			level.removeBlockEntity(pos);
			level.setBlock(pos, Blocks.AIR.defaultBlockState(), FLAGS);
		}
		for (Moved m : moved) {
			level.setBlock(m.to(), m.state(), FLAGS);
			if (m.entity() != null) {
				BlockEntity be = BlockEntity.loadStatic(m.to(), m.state(), m.entity(), registries);
				if (be != null) {
					level.setBlockEntity(be);
				}
			}
		}
		LethalCraft.LOG.info("LethalCraft: ship blocks: moved {} blocks from {} .. {} ({} quarter turns, offset {})", moved.size(), min, max, q, offset);
	}

	/** Quarter turns about Y the way Minecraft's Rotation turns blocks (clockwise seen from above: east to south). */
	private static BlockPos rotate(BlockPos p, int q) {
		return switch (q) {
			case 1 -> new BlockPos(-p.getZ(), p.getY(), p.getX());
			case 2 -> new BlockPos(-p.getX(), p.getY(), -p.getZ());
			case 3 -> new BlockPos(p.getZ(), p.getY(), -p.getX());
			default -> p;
		};
	}
}
