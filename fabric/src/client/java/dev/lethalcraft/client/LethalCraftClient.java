package dev.lethalcraft.client;

import dev.lethalcraft.combat.HostCombat;
import net.fabricmc.api.ClientModInitializer;
import net.fabricmc.fabric.api.client.event.lifecycle.v1.ClientTickEvents;
import net.fabricmc.fabric.api.client.rendering.v1.EntityRendererRegistry;
import net.minecraft.client.renderer.entity.NoopRenderer;

public final class LethalCraftClient implements ClientModInitializer {
	@Override
	public void onInitializeClient() {
		dev.lethalcraft.link.HostLink.announceRunning();
		DiscordPresence.start();
		DestructionToggle.register();
		// Multiplayer without editing files: the host opens their world to LAN (O, Open to LAN) and
		// e4mc gives them a link; friends type /join <link> in chat, and /leave to come back.
		net.fabricmc.fabric.api.client.command.v2.ClientCommandRegistrationCallback.EVENT.register((dispatcher, context) -> {
			dispatcher.register(net.fabricmc.fabric.api.client.command.v2.ClientCommands.literal("join")
				.then(net.fabricmc.fabric.api.client.command.v2.ClientCommands.argument("link", com.mojang.brigadier.arguments.StringArgumentType.greedyString())
					.executes(c -> {
						String link = com.mojang.brigadier.arguments.StringArgumentType.getString(c, "link");
						c.getSource().sendFeedback(net.minecraft.network.chat.Component.literal("Joining " + link.trim() + "..."));
						// After the chat screen has closed: this leaves the current world.
						LobbyWorld.playerChose();
						net.minecraft.client.Minecraft.getInstance().execute(() -> MirrorWorld.joinFriend(net.minecraft.client.Minecraft.getInstance(), link));
						return 1;
					})));
			dispatcher.register(net.fabricmc.fabric.api.client.command.v2.ClientCommands.literal("leave").executes(c -> {
				LobbyWorld.playerChose();
				net.minecraft.client.Minecraft.getInstance().execute(() -> MirrorWorld.leaveFriend(net.minecraft.client.Minecraft.getInstance()));
				return 1;
			}));
		});
		ClientTickEvents.END_CLIENT_TICK.register(HostClient::clientTick);
		GameItemsClient.register();
		ClientTickEvents.END_CLIENT_TICK.register(minecraft -> {
			if (HostClient.linked()) {
				GameItemsClient.tick(minecraft);
			}
			ItemIconPack.tick(minecraft);
		});
		// Multiplayer testing on one PC: LETHALCRAFT_LAN_PORT opens the world to LAN on that port as soon
		// as it's loaded, and LETHALCRAFT_LAN_OFFLINE lets offline (dev) clients join it.
		net.fabricmc.fabric.api.client.networking.v1.ClientPlayConnectionEvents.JOIN.register((handler, sender, minecraft) -> {
			String port = System.getenv("LETHALCRAFT_LAN_PORT");
			var server = minecraft.getSingleplayerServer();
			if (port == null || port.isBlank() || server == null || server.isPublished()) {
				return;
			}
			minecraft.execute(() -> {
				if (System.getenv("LETHALCRAFT_LAN_OFFLINE") != null) {
					server.setUsesAuthentication(false);
				}
				boolean ok = server.publishServer(net.minecraft.server.MinecraftServer.MultiplayerScope.LAN, false, Integer.parseInt(port.trim()));
				dev.lethalcraft.LethalCraft.LOG.info("LethalCraft: world opened to LAN on port {} ({}{})", port.trim(), ok ? "ok" : "FAILED",
					System.getenv("LETHALCRAFT_LAN_OFFLINE") != null ? ", offline logins allowed" : "");
			});
		});
		// A guest in a friend's world: dying there kills this player's own Lethal Company character.
		net.fabricmc.fabric.api.client.networking.v1.ClientPlayNetworking.registerGlobalReceiver(dev.lethalcraft.net.LethalNet.Died.TYPE, (payload, context) -> {
			if (dev.lethalcraft.link.HostLink.active()) {
				dev.lethalcraft.link.HostLink.pushEvent(dev.lethalcraft.link.Proto.EV_PLAYER_DIED, payload.attackerId(), 0, 0, 0, 0, 0);
			}
		});
		// Lethal Company draws the real enemy; its Minecraft stand-in is only a hitbox.
		EntityRendererRegistry.register(HostCombat.HOST_ACTOR, NoopRenderer::new);
		// Players (client-side movement AND the integrated server's re-check of it) use the smooth
		// triangle collider, never Lethal Company's voxels; otherwise the server sees the smooth position
		// dip into a voxel and teleports the player back every few ticks.
		dev.lethalcraft.world.HostCollision.setSmoothCollider(e -> e instanceof net.minecraft.world.entity.player.Player && HostClient.linked());
	}
}
