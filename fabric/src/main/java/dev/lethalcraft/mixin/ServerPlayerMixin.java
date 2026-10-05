package dev.lethalcraft.mixin;

import dev.lethalcraft.LethalCraft;
import dev.lethalcraft.combat.HostCombat;
import dev.lethalcraft.combat.HostActorEntity;
import dev.lethalcraft.link.Proto;
import dev.lethalcraft.link.HostLink;
import net.minecraft.world.damagesource.DamageSource;
import net.minecraft.world.entity.Entity;
import net.minecraft.server.level.ServerPlayer;
import org.spongepowered.asm.mixin.Mixin;
import org.spongepowered.asm.mixin.injection.At;
import org.spongepowered.asm.mixin.injection.Inject;
import org.spongepowered.asm.mixin.injection.callback.CallbackInfo;
import org.spongepowered.asm.mixin.injection.callback.CallbackInfoReturnable;

@Mixin(ServerPlayer.class)
public abstract class ServerPlayerMixin {
	/** Critical hits on a Lethal Company actor are flagged so Lethal Company can play them up. */
	@Inject(method = "crit", at = @At("HEAD"))
	private void lethalcraft$critHost(Entity entity, CallbackInfo ci) {
		if (entity instanceof HostActorEntity proxy) {
			proxy.markCritical();
		}
	}

	/** Dying in Minecraft is dying in Lethal Company: the host's through the link, a guest's through theirs. */
	@Inject(method = "die", at = @At("HEAD"))
	private void lethalcraft$diesInHost(DamageSource source, CallbackInfo ci) {
		ServerPlayer self = (ServerPlayer) (Object) this;
		int attacker = HostCombat.attackerId(source);
		if (!dev.lethalcraft.net.LethalNet.isHost(self)) {
			if (net.fabricmc.fabric.api.networking.v1.ServerPlayNetworking.canSend(self, dev.lethalcraft.net.LethalNet.Died.TYPE)) {
				net.fabricmc.fabric.api.networking.v1.ServerPlayNetworking.send(self, new dev.lethalcraft.net.LethalNet.Died(attacker));
			}
			LethalCraft.LOG.info("LethalCraft: guest {} died ({}); telling their Lethal Company", self.getPlainTextName(), source.getMsgId());
			return;
		}
		if (HostLink.active()) {
			HostLink.pushEvent(Proto.EV_PLAYER_DIED, attacker, 0, 0, 0, 0, 0);
			LethalCraft.LOG.info("LethalCraft: player died ({}); telling Lethal Company", source.getMsgId());
		}
	}

	/** Dropping one of the game's items drops the real object in the game, not a Minecraft item. */
	@Inject(method = "drop(Lnet/minecraft/world/item/ItemStack;ZLnet/minecraft/util/Prediction;)Lnet/minecraft/world/entity/item/ItemEntity;", at = @At("HEAD"), cancellable = true)
	private void lethalcraft$dropGameItem(net.minecraft.world.item.ItemStack stack, boolean randomly, net.minecraft.util.Prediction prediction,
		CallbackInfoReturnable<net.minecraft.world.entity.item.ItemEntity> cir) {
		if (dev.lethalcraft.item.GameItems.isToken(stack)) {
			dev.lethalcraft.item.GameItems.dropped((ServerPlayer) (Object) this, stack);
			cir.setReturnValue(null);
		}
	}
}
