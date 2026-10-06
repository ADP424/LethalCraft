package dev.lethalcraft.mixin;

import dev.lethalcraft.world.MoonBlocks;
import net.minecraft.core.BlockPos;
import net.minecraft.server.level.ServerLevel;
import net.minecraft.world.level.block.state.BlockState;
import net.minecraft.world.level.chunk.LevelChunk;
import org.spongepowered.asm.mixin.Mixin;
import org.spongepowered.asm.mixin.injection.At;
import org.spongepowered.asm.mixin.injection.Inject;
import org.spongepowered.asm.mixin.injection.callback.CallbackInfoReturnable;

/** A block changed on a moon: its chunk is noted, to be cleared when the ship leaves (MoonBlocks). */
@Mixin(LevelChunk.class)
public abstract class LevelChunkMoonMixin {
	@Inject(method = "setBlockState", at = @At("RETURN"))
	private void lethalcraft$noteMoonChunk(BlockPos pos, BlockState state, int flags, CallbackInfoReturnable<BlockState> cir) {
		LevelChunk self = (LevelChunk) (Object) this;
		if (cir.getReturnValue() != null && self.getLevel() instanceof ServerLevel level) {
			MoonBlocks.touched(level, self.getPos().x(), self.getPos().z());
		}
	}
}
