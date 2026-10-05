package dev.lethalcraft.mixin;

import dev.lethalcraft.item.GameItems;
import net.minecraft.world.entity.player.Inventory;
import org.spongepowered.asm.mixin.Mixin;
import org.spongepowered.asm.mixin.injection.At;
import org.spongepowered.asm.mixin.injection.Inject;
import org.spongepowered.asm.mixin.injection.callback.CallbackInfoReturnable;

/** Picking things up only fills the hotbar: the 27 main slots are locked (SlotMixin). */
@Mixin(Inventory.class)
public abstract class InventoryMixin {
	@Inject(method = "getFreeSlot", at = @At("HEAD"), cancellable = true)
	private void lethalcraft$hotbarOnly(CallbackInfoReturnable<Integer> cir) {
		Inventory self = (Inventory) (Object) this;
		for (int i = 0; i < GameItems.HOTBAR; i++) {
			if (self.getItem(i).isEmpty()) {
				cir.setReturnValue(i);
				return;
			}
		}
		cir.setReturnValue(-1);
	}
}
