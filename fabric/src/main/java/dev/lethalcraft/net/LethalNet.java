package dev.lethalcraft.net;

import dev.lethalcraft.LethalCraft;
import dev.lethalcraft.combat.HostCombat;
import dev.lethalcraft.world.HostDig;
import java.util.List;
import net.minecraft.core.BlockPos;
import net.fabricmc.fabric.api.networking.v1.PayloadTypeRegistry;
import net.fabricmc.fabric.api.networking.v1.ServerPlayNetworking;
import net.minecraft.network.RegistryFriendlyByteBuf;
import net.minecraft.network.codec.ByteBufCodecs;
import net.minecraft.network.codec.StreamCodec;
import net.minecraft.network.protocol.common.custom.CustomPacketPayload;
import net.minecraft.resources.Identifier;
import net.minecraft.server.level.ServerPlayer;

/**
 * Multiplayer: every player has their own Lethal Company, talking to their own Minecraft client. The host's
 * Lethal Company reaches the host's integrated server through shared memory; a guest's Lethal Company reaches the
 * host's server through these packets instead.
 */
public final class LethalNet {
	private LethalNet() {
	}

	/** Guest -> server: the guest's Lethal Company hit them (as proto::InputEvent kInHurt). */
	public record Hurt(int kind, float hostDamage, int attackerId, int flags) implements CustomPacketPayload {
		public static final Type<Hurt> TYPE = new Type<>(Identifier.fromNamespaceAndPath(LethalCraft.MOD_ID, "hurt"));
		public static final StreamCodec<RegistryFriendlyByteBuf, Hurt> CODEC = StreamCodec.composite(
			ByteBufCodecs.VAR_INT, Hurt::kind,
			ByteBufCodecs.FLOAT, Hurt::hostDamage,
			ByteBufCodecs.INT, Hurt::attackerId,
			ByteBufCodecs.VAR_INT, Hurt::flags,
			Hurt::new
		);

		@Override
		public Type<? extends CustomPacketPayload> type() {
			return TYPE;
		}
	}

	/** Server -> guest: the guest died in Minecraft, so their Lethal Company player dies too. */
	public record Died(int attackerId) implements CustomPacketPayload {
		public static final Type<Died> TYPE = new Type<>(Identifier.fromNamespaceAndPath(LethalCraft.MOD_ID, "died"));
		public static final StreamCodec<RegistryFriendlyByteBuf, Died> CODEC = StreamCodec.composite(ByteBufCodecs.INT, Died::attackerId, Died::new);

		@Override
		public Type<? extends CustomPacketPayload> type() {
			return TYPE;
		}
	}

	/** Client -> server: the player hit Lethal Company's geometry in this cell (HostDig.open). */
	public record DigOpen(int world, BlockPos pos, int material) implements CustomPacketPayload {
		public static final Type<DigOpen> TYPE = new Type<>(Identifier.fromNamespaceAndPath(LethalCraft.MOD_ID, "dig_open"));
		public static final StreamCodec<RegistryFriendlyByteBuf, DigOpen> CODEC = StreamCodec.composite(
			ByteBufCodecs.INT, DigOpen::world,
			BlockPos.STREAM_CODEC, DigOpen::pos,
			ByteBufCodecs.VAR_INT, DigOpen::material,
			DigOpen::new
		);

		@Override
		public Type<? extends CustomPacketPayload> type() {
			return TYPE;
		}
	}

	/** Client -> server: cells around a broken dug block that are inside Lethal Company's geometry (HostDig.reveal). */
	public record DigReveal(int world, List<BlockPos> cells, List<Integer> materials) implements CustomPacketPayload {
		public static final Type<DigReveal> TYPE = new Type<>(Identifier.fromNamespaceAndPath(LethalCraft.MOD_ID, "dig_reveal"));
		public static final StreamCodec<RegistryFriendlyByteBuf, DigReveal> CODEC = StreamCodec.composite(
			ByteBufCodecs.INT, DigReveal::world,
			BlockPos.STREAM_CODEC.apply(ByteBufCodecs.list(64)), DigReveal::cells,
			ByteBufCodecs.VAR_INT.apply(ByteBufCodecs.list(64)), DigReveal::materials,
			DigReveal::new
		);

		@Override
		public Type<? extends CustomPacketPayload> type() {
			return TYPE;
		}
	}

	/**
	 * Guest -> server: the guest's Lethal Company put them somewhere else (a new level, a respawn, a door).
	 * The server moves them itself: sent as an ordinary move, a jump of thousands of blocks is swept
	 * through the world from where the server last had them, and anything in the way sends them
	 * back there (for a new guest, the world spawn at the bottom of the void).
	 */
	public record Teleport(double x, double y, double z, float yaw, float pitch) implements CustomPacketPayload {
		public static final Type<Teleport> TYPE = new Type<>(Identifier.fromNamespaceAndPath(LethalCraft.MOD_ID, "teleport"));
		public static final StreamCodec<RegistryFriendlyByteBuf, Teleport> CODEC = StreamCodec.composite(
			ByteBufCodecs.DOUBLE, Teleport::x,
			ByteBufCodecs.DOUBLE, Teleport::y,
			ByteBufCodecs.DOUBLE, Teleport::z,
			ByteBufCodecs.FLOAT, Teleport::yaw,
			ByteBufCodecs.FLOAT, Teleport::pitch,
			Teleport::new
		);

		@Override
		public Type<? extends CustomPacketPayload> type() {
			return TYPE;
		}
	}

	/** Client -> server: the objects the player's game holds (dev.lethalcraft.item.GameItems). valid: a game is on. */
	public record HeldGameItems(boolean valid, List<dev.lethalcraft.item.GameItems.Entry> items) implements CustomPacketPayload {
		public static final StreamCodec<RegistryFriendlyByteBuf, dev.lethalcraft.item.GameItems.Entry> ENTRY = StreamCodec.composite(
			ByteBufCodecs.INT, dev.lethalcraft.item.GameItems.Entry::id,
			ByteBufCodecs.BOOL, dev.lethalcraft.item.GameItems.Entry::twoHanded,
			ByteBufCodecs.VAR_INT, dev.lethalcraft.item.GameItems.Entry::value,
			ByteBufCodecs.FLOAT, dev.lethalcraft.item.GameItems.Entry::battery,
			ByteBufCodecs.BOOL, dev.lethalcraft.item.GameItems.Entry::hasBattery,
			ByteBufCodecs.stringUtf8(64), dev.lethalcraft.item.GameItems.Entry::name,
			dev.lethalcraft.item.GameItems.Entry::new
		);
		public static final Type<HeldGameItems> TYPE = new Type<>(Identifier.fromNamespaceAndPath(LethalCraft.MOD_ID, "held_game_items"));
		public static final StreamCodec<RegistryFriendlyByteBuf, HeldGameItems> CODEC = StreamCodec.composite(
			ByteBufCodecs.BOOL, HeldGameItems::valid,
			ENTRY.apply(ByteBufCodecs.list(16)), HeldGameItems::items,
			HeldGameItems::new
		);

		@Override
		public Type<? extends CustomPacketPayload> type() {
			return TYPE;
		}
	}

	/** Host client -> server: Lethal Company's ship landed or took off; move its blocks (ShipBlocks). */
	public record MoveShipBlocks(BlockPos min, BlockPos max, int quarterTurns, BlockPos offset, int clearSlot, boolean clearFirst) implements CustomPacketPayload {
		public static final Type<MoveShipBlocks> TYPE = new Type<>(Identifier.fromNamespaceAndPath(LethalCraft.MOD_ID, "move_ship_blocks"));
		public static final StreamCodec<RegistryFriendlyByteBuf, MoveShipBlocks> CODEC = StreamCodec.composite(
			BlockPos.STREAM_CODEC, MoveShipBlocks::min,
			BlockPos.STREAM_CODEC, MoveShipBlocks::max,
			ByteBufCodecs.VAR_INT, MoveShipBlocks::quarterTurns,
			BlockPos.STREAM_CODEC, MoveShipBlocks::offset,
			ByteBufCodecs.VAR_INT, MoveShipBlocks::clearSlot,
			ByteBufCodecs.BOOL, MoveShipBlocks::clearFirst,
			MoveShipBlocks::new
		);

		@Override
		public Type<? extends CustomPacketPayload> type() {
			return TYPE;
		}
	}

	/** Server -> client: the player dropped this game object's token; their game drops the object. */
	public record DroppedGameItem(int id) implements CustomPacketPayload {
		public static final Type<DroppedGameItem> TYPE = new Type<>(Identifier.fromNamespaceAndPath(LethalCraft.MOD_ID, "dropped_game_item"));
		public static final StreamCodec<RegistryFriendlyByteBuf, DroppedGameItem> CODEC = StreamCodec.composite(ByteBufCodecs.INT, DroppedGameItem::id, DroppedGameItem::new);

		@Override
		public Type<? extends CustomPacketPayload> type() {
			return TYPE;
		}
	}

	/** Server -> guest: the guest hit a Lethal Company monster's stand-in; their own Lethal Company applies it. */
	public record HitActor(int formId, float damage, float pushX, float pushZ, float strength, int flags, int weapon) implements CustomPacketPayload {
		public static final Type<HitActor> TYPE = new Type<>(Identifier.fromNamespaceAndPath(LethalCraft.MOD_ID, "hit_actor"));
		public static final StreamCodec<RegistryFriendlyByteBuf, HitActor> CODEC = StreamCodec.composite(
			ByteBufCodecs.INT, HitActor::formId,
			ByteBufCodecs.FLOAT, HitActor::damage,
			ByteBufCodecs.FLOAT, HitActor::pushX,
			ByteBufCodecs.FLOAT, HitActor::pushZ,
			ByteBufCodecs.FLOAT, HitActor::strength,
			ByteBufCodecs.INT, HitActor::flags,
			ByteBufCodecs.INT, HitActor::weapon,
			HitActor::new
		);

		@Override
		public Type<? extends CustomPacketPayload> type() {
			return TYPE;
		}
	}

	public static void sendHitActor(ServerPlayer guest, int formId, float[] hit) {
		if (ServerPlayNetworking.canSend(guest, HitActor.TYPE)) {
			ServerPlayNetworking.send(guest, new HitActor(formId, hit[0], hit[1], hit[2], hit[3], Float.floatToRawIntBits(hit[4]), Float.floatToRawIntBits(hit[5])));
		}
	}

	/**
	 * Guest -> server: the guest's surroundings as their own Lethal Company sees them (its ladders as climbable
	 * boxes, its water surface around them), so the server climbs and drowns them like their client does.
	 */
	public record GuestSurroundings(List<Float> ladders, int waterX, int waterZ, List<Float> water) implements CustomPacketPayload {
		public static final Type<GuestSurroundings> TYPE = new Type<>(Identifier.fromNamespaceAndPath(LethalCraft.MOD_ID, "guest_surroundings"));
		public static final StreamCodec<RegistryFriendlyByteBuf, GuestSurroundings> CODEC = StreamCodec.composite(
			ByteBufCodecs.FLOAT.apply(ByteBufCodecs.list(6 * 24)), GuestSurroundings::ladders,
			ByteBufCodecs.INT, GuestSurroundings::waterX,
			ByteBufCodecs.INT, GuestSurroundings::waterZ,
			ByteBufCodecs.FLOAT.apply(ByteBufCodecs.list(256)), GuestSurroundings::water,
			GuestSurroundings::new
		);

		@Override
		public Type<? extends CustomPacketPayload> type() {
			return TYPE;
		}
	}

	public static void sendDroppedGameItem(ServerPlayer player, int id) {
		if (ServerPlayNetworking.canSend(player, DroppedGameItem.TYPE)) {
			ServerPlayNetworking.send(player, new DroppedGameItem(id));
		}
	}

	public static void init() {
		PayloadTypeRegistry.clientboundPlay().register(HitActor.TYPE, HitActor.CODEC);
		PayloadTypeRegistry.serverboundPlay().register(GuestSurroundings.TYPE, GuestSurroundings.CODEC);
		ServerPlayNetworking.registerGlobalReceiver(GuestSurroundings.TYPE, (payload, context) -> {
			ServerPlayer player = context.player();
			float[] ladders = new float[payload.ladders().size()];
			for (int i = 0; i < ladders.length; i++) {
				ladders[i] = payload.ladders().get(i);
			}
			float[] water = new float[payload.water().size()];
			for (int i = 0; i < water.length; i++) {
				water[i] = payload.water().get(i);
			}
			dev.lethalcraft.world.HostLadders.setGuest(player.getUUID(), ladders);
			dev.lethalcraft.world.HostWater.setGuest(player.getUUID(), payload.waterX(), payload.waterZ(), water);
		});
		PayloadTypeRegistry.serverboundPlay().register(MoveShipBlocks.TYPE, MoveShipBlocks.CODEC);
		ServerPlayNetworking.registerGlobalReceiver(MoveShipBlocks.TYPE, (payload, context) -> {
			ServerPlayer player = context.player();
			// The ship is the host's: only their Lethal Company moves it. Landing, the moon is cleared of what was left
			// on it before the ship's blocks arrive; taking off, after they've left.
			if (isHost(player)) {
				context.server().execute(() -> {
					var level = player.level();
					if (payload.clearFirst()) {
						dev.lethalcraft.world.MoonBlocks.clear(level, payload.clearSlot());
					}
					dev.lethalcraft.world.ShipBlocks.move(level, payload.min(), payload.max(), payload.quarterTurns(), payload.offset());
					if (!payload.clearFirst()) {
						dev.lethalcraft.world.MoonBlocks.clear(level, payload.clearSlot());
					}
				});
			}
		});
		PayloadTypeRegistry.serverboundPlay().register(HeldGameItems.TYPE, HeldGameItems.CODEC);
		PayloadTypeRegistry.clientboundPlay().register(DroppedGameItem.TYPE, DroppedGameItem.CODEC);
		ServerPlayNetworking.registerGlobalReceiver(HeldGameItems.TYPE, (payload, context) -> {
			ServerPlayer player = context.player();
			context.server().execute(() -> dev.lethalcraft.item.GameItems.update(player, payload.valid(), payload.items()));
		});
		PayloadTypeRegistry.serverboundPlay().register(Teleport.TYPE, Teleport.CODEC);
		ServerPlayNetworking.registerGlobalReceiver(Teleport.TYPE, (payload, context) -> {
			ServerPlayer player = context.player();
			double x = payload.x(), y = payload.y(), z = payload.z();
			if (!Double.isFinite(x) || !Double.isFinite(y) || !Double.isFinite(z) || Math.abs(x) > 2.0E7 || Math.abs(z) > 2.0E7 || Math.abs(y) > 2.0E4) {
				return;
			}
			context.server().execute(() -> {
				player.teleportTo(player.level(), x, y, z, java.util.Set.of(), payload.yaw(), payload.pitch(), false);
				player.resetFallDistance();
				LethalCraft.LOG.info("LethalCraft: {}'s Lethal Company moved them to {} {} {}", player.getPlainTextName(),
					String.format("%.1f", x), String.format("%.1f", y), String.format("%.1f", z));
			});
		});
		PayloadTypeRegistry.serverboundPlay().register(Hurt.TYPE, Hurt.CODEC);
		PayloadTypeRegistry.serverboundPlay().register(DigOpen.TYPE, DigOpen.CODEC);
		PayloadTypeRegistry.serverboundPlay().register(DigReveal.TYPE, DigReveal.CODEC);
		ServerPlayNetworking.registerGlobalReceiver(DigOpen.TYPE, (payload, context) -> {
			ServerPlayer player = context.player();
			context.server().execute(() -> HostDig.open(player, payload.world(), payload.pos(), payload.material()));
		});
		ServerPlayNetworking.registerGlobalReceiver(DigReveal.TYPE, (payload, context) -> {
			ServerPlayer player = context.player();
			int[] materials = payload.materials().stream().mapToInt(Integer::intValue).toArray();
			context.server().execute(() -> HostDig.reveal(player, payload.world(), payload.cells(), materials));
		});
		PayloadTypeRegistry.clientboundPlay().register(Died.TYPE, Died.CODEC);
		ServerPlayNetworking.registerGlobalReceiver(Hurt.TYPE, (payload, context) -> {
			ServerPlayer player = context.player();
			// A hit's worth of damage, whatever the guest's client claims (friends only, but still).
			float damage = Math.max(0.0F, Math.min(payload.hostDamage(), 10000.0F));
			context.server().execute(() -> HostCombat.hurtPlayer(player, payload.kind(), damage, payload.attackerId(), payload.flags()));
		});
	}

	/** True if this player plays on this machine (their Lethal Company is on the shared-memory link). */
	public static boolean isHost(ServerPlayer player) {
		var server = player.level().getServer();
		return server != null && server.isSingleplayerOwner(player.nameAndId());
	}
}
