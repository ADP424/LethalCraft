package dev.lethalcraft.client;

import dev.lethalcraft.link.Proto;
import dev.lethalcraft.world.HostCollision;
import dev.lethalcraft.world.HostDig;
import dev.lethalcraft.world.HostTri;
import java.util.ArrayList;
import java.util.List;
import net.minecraft.client.Minecraft;
import net.minecraft.client.player.LocalPlayer;
import net.minecraft.core.BlockPos;
import net.minecraft.sounds.SoundSource;
import net.minecraft.world.level.block.SoundType;
import net.minecraft.world.level.block.state.BlockState;
import net.minecraft.world.phys.AABB;
import net.minecraft.world.phys.Vec3;
import org.jspecify.annotations.Nullable;

/**
 * Minecraft's footsteps on Lethal Company's ground. Minecraft only plays step sounds for a real block
 * under the feet; here there is only Lethal Company's geometry (air, to Minecraft). So the steps are
 * played here the way Minecraft paces them (every 1/0.6 blocks walked on the ground, none while
 * sneaking), with the sound of the block that ground is made of (the triangle's material, from the
 * game's own footstep tags). A real block under the feet (one the player placed) plays its own.
 */
public final class HostFootsteps {
	private static @Nullable Vec3 last;
	private static double walked;
	private static double nextStep = 1.0;
	private static final List<HostTri> SCRATCH = new ArrayList<>();

	private HostFootsteps() {
	}

	/** Every client tick. */
	static void tick(Minecraft minecraft) {
		LocalPlayer player = minecraft.player;
		if (player == null || minecraft.level == null || !HostClient.linked()) {
			last = null;
			return;
		}
		Vec3 pos = player.position();
		Vec3 before = last;
		last = pos;
		if (before == null || !player.onGround() || player.isShiftKeyDown() || player.isPassenger()) {
			return;
		}
		double dx = pos.x - before.x, dz = pos.z - before.z;
		double moved = Math.sqrt(dx * dx + dz * dz);
		if (moved > 2.0) {
			return; // a teleport
		}
		walked += moved * 0.6;
		if (walked < nextStep) {
			return;
		}
		nextStep = Math.floor(walked) + 1.0;
		if (!minecraft.level.getBlockState(BlockPos.containing(pos.x, pos.y - 0.2, pos.z)).isAir()) {
			return; // a Minecraft block: Minecraft plays its own step
		}
		int material = materialUnder(pos);
		if (material < 0) {
			return;
		}
		BlockState state = HostDig.materialState(material == 0 ? Proto.DIG_STONE : material);
		SoundType sound = state.getSoundType();
		minecraft.level.playLocalSound(pos.x, pos.y, pos.z, sound.getStepSound(), SoundSource.PLAYERS, sound.getVolume() * 0.15F, sound.getPitch(), false);
	}

	/** The material of the highest walkable Lethal Company surface just under the feet, or -1 for none. */
	private static int materialUnder(Vec3 pos) {
		SCRATCH.clear();
		HostCollision.trianglesNear(new AABB(pos.x - 0.3, pos.y - 0.6, pos.z - 0.3, pos.x + 0.3, pos.y + 0.1, pos.z + 0.3), SCRATCH);
		int material = -1;
		double top = Double.NEGATIVE_INFINITY;
		for (HostTri t : SCRATCH) {
			if (t.walkable && t.maxY <= pos.y + 0.1 && t.maxY > top) {
				top = t.maxY;
				material = t.material;
			}
		}
		return material;
	}
}
