package dev.lethalcraft.mixin;

import dev.lethalcraft.item.GameItems;
import net.minecraft.world.Container;
import net.minecraft.world.entity.player.Inventory;
import net.minecraft.world.inventory.Slot;
import net.minecraft.world.item.ItemStack;
import org.spongepowered.asm.mixin.Final;
import org.spongepowered.asm.mixin.Mixin;
import org.spongepowered.asm.mixin.Shadow;
import org.spongepowered.asm.mixin.injection.At;
import org.spongepowered.asm.mixin.injection.Inject;
import org.spongepowered.asm.mixin.injection.callback.CallbackInfoReturnable;

/**
 * LethalCraft's inventory is the hotbar and the offhand: the 27 main slots take nothing. The game's
 * items (tokens) only live there too, never in a chest, crafting grid or furnace, since the real
 * object is in the player's hands in the game; and two-handed ones never in the offhand.
 */
@Mixin(Slot.class)
public abstract class SlotMixin {
	@Shadow
	@Final
	public Container container;

	@Shadow
	public abstract int getContainerSlot();

	@Inject(method = "mayPlace", at = @At("HEAD"), cancellable = true)
	private void lethalcraft$hotbarOnly(ItemStack stack, CallbackInfoReturnable<Boolean> cir) {
		if (this.container instanceof Inventory) {
			int index = this.getContainerSlot();
			if (index >= GameItems.HOTBAR && index < GameItems.MAIN_END || index == GameItems.OFFHAND && GameItems.twoHanded(stack)) {
				cir.setReturnValue(false);
			}
		} else if (GameItems.isToken(stack)) {
			cir.setReturnValue(false);
		}
	}
}
