package dev.lethalcraft.mixin;

import dev.lethalcraft.world.HostLadders;
import net.minecraft.server.level.ServerPlayer;
import net.minecraft.world.entity.LivingEntity;
import net.minecraft.world.entity.player.Player;
import org.spongepowered.asm.mixin.Mixin;
import org.spongepowered.asm.mixin.injection.At;
import org.spongepowered.asm.mixin.injection.Inject;
import org.spongepowered.asm.mixin.injection.callback.CallbackInfoReturnable;

/** Players climb Lethal Company's ladders like Minecraft's (HostLadders): a guest on the server by their own ladders. */
@Mixin(LivingEntity.class)
public abstract class LivingEntityClimbMixin {
	@Inject(method = "onClimbable", at = @At("HEAD"), cancellable = true)
	private void lethalcraft$hostLadder(CallbackInfoReturnable<Boolean> cir) {
		LivingEntity self = (LivingEntity) (Object) this;
		if (!(self instanceof Player player) || player.isSpectator()) {
			return;
		}
		boolean climbs = self instanceof ServerPlayer server && !dev.lethalcraft.net.LethalNet.isHost(server)
			? HostLadders.containsForGuest(server.getUUID(), player.getBoundingBox())
			: HostLadders.contains(player.getBoundingBox());
		if (climbs) {
			cir.setReturnValue(true);
		}
	}
}
