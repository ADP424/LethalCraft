package dev.lethalcraft.client.mixin;

import dev.lethalcraft.client.HostClient;
import dev.lethalcraft.link.HostLink;
import dev.lethalcraft.link.Proto;
import net.minecraft.client.multiplayer.ClientPacketListener;
import org.spongepowered.asm.mixin.Mixin;
import org.spongepowered.asm.mixin.injection.At;
import org.spongepowered.asm.mixin.injection.Inject;
import org.spongepowered.asm.mixin.injection.callback.CallbackInfo;

/**
 * In a Lethal Company game, Minecraft's chat is the game's chat: a line sent from it goes to Lethal Company
 * (EV_CHAT_CHAR per UTF-16 unit, then EV_CHAT_END), which sends it to the lobby the game's way (its
 * range, walkie-talkies, the dead and the living apart); the lobby's lines come back into this chat
 * (InputBridge, IN_CHAT). Commands ("/...") stay Minecraft's.
 */
@Mixin(ClientPacketListener.class)
public abstract class ChatSendMixin {
	@Inject(method = "sendChat", at = @At("HEAD"), cancellable = true)
	private void lethalcraft$toTheGame(String message, CallbackInfo ci) {
		if (!HostLink.active() || !HostClient.sky().inGame() || message == null || message.isBlank()) {
			return;
		}
		for (int i = 0; i < message.length(); i++) {
			HostLink.pushEvent(Proto.EV_CHAT_CHAR, message.charAt(i), 0, 0, 0, 0, 0);
		}
		HostLink.pushEvent(Proto.EV_CHAT_END, 0, 0, 0, 0, 0, 0);
		ci.cancel();
	}
}
