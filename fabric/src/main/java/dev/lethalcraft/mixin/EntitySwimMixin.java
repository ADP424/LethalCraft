package dev.lethalcraft.mixin;

import com.llamalad7.mixinextras.injector.wrapoperation.Operation;
import com.llamalad7.mixinextras.injector.wrapoperation.WrapOperation;
import dev.lethalcraft.world.HostWater;
import net.minecraft.core.BlockPos;
import net.minecraft.world.entity.Entity;
import net.minecraft.world.level.Level;
import net.minecraft.world.level.material.FluidState;
import org.spongepowered.asm.mixin.Mixin;
import org.spongepowered.asm.mixin.injection.At;

/** Sprint-swimming starts in Lethal Company's water too (see {@link HostWater}). */
@Mixin(Entity.class)
public abstract class EntitySwimMixin {
	@org.spongepowered.asm.mixin.injection.Inject(method = "updateSwimming", at = @At("HEAD"))
	private void lethalcraft$guestWaterBegin(org.spongepowered.asm.mixin.injection.callback.CallbackInfo ci) {
		HostWater.beginFor((Entity) (Object) this);
	}

	@org.spongepowered.asm.mixin.injection.Inject(method = "updateSwimming", at = @At("RETURN"))
	private void lethalcraft$guestWaterEnd(org.spongepowered.asm.mixin.injection.callback.CallbackInfo ci) {
		HostWater.endFor();
	}

	@WrapOperation(
		method = "updateSwimming",
		at = @At(value = "INVOKE", target = "Lnet/minecraft/world/level/Level;getFluidState(Lnet/minecraft/core/BlockPos;)Lnet/minecraft/world/level/material/FluidState;")
	)
	private FluidState lethalcraft$swimInHostWater(Level level, BlockPos pos, Operation<FluidState> original) {
		FluidState state = original.call(level, pos);
		if (state.isEmpty() && HostWater.active()) {
			FluidState water = HostWater.fluidAt(level, pos);
			if (water != null) {
				return water;
			}
		}
		return state;
	}
}
