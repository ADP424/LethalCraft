package dev.lethalcraft.client.mixin;

import dev.lethalcraft.client.HostClient;
import net.minecraft.client.Minecraft;
import org.spongepowered.asm.mixin.Mixin;
import org.spongepowered.asm.mixin.injection.At;
import org.spongepowered.asm.mixin.injection.Inject;
import org.spongepowered.asm.mixin.injection.callback.CallbackInfo;

@Mixin(Minecraft.class)
public abstract class MinecraftMixin {
	@Inject(method = "runTick", at = @At("HEAD"))
	private void lethalcraft$beginFrame(boolean advanceGameTime, CallbackInfo ci) {
		HostClient.beginFrame();
	}

	@Inject(
		method = "renderFrame",
		at = @At(value = "INVOKE", target = "Lnet/minecraft/client/renderer/GameRenderer;render()V", shift = At.Shift.AFTER)
	)
	private void lethalcraft$afterRender(boolean advanceGameTime, CallbackInfo ci) {
		HostClient.afterRender();
	}

	@Inject(method = "renderFrame", at = @At("TAIL"))
	private void lethalcraft$pace(boolean advanceGameTime, CallbackInfo ci) {
		HostClient.paceFrame();
	}
}
