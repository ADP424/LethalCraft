package dev.lethalcraft.client.mixin;

import dev.lethalcraft.item.GameItems;
import net.minecraft.client.gui.GuiGraphicsExtractor;
import net.minecraft.client.gui.screens.inventory.AbstractContainerScreen;
import net.minecraft.world.entity.player.Inventory;
import net.minecraft.world.inventory.Slot;
import org.spongepowered.asm.mixin.Mixin;
import org.spongepowered.asm.mixin.injection.At;
import org.spongepowered.asm.mixin.injection.Inject;
import org.spongepowered.asm.mixin.injection.callback.CallbackInfo;

/** The 27 locked main inventory slots (SlotMixin) are drawn dark, so it's clear they take nothing. */
@Mixin(AbstractContainerScreen.class)
public abstract class LockedSlotsMixin {
	@Inject(method = "extractSlot", at = @At("TAIL"))
	private void lethalcraft$darkenLocked(GuiGraphicsExtractor graphics, Slot slot, int mouseX, int mouseY, CallbackInfo ci) {
		if (slot.container instanceof Inventory) {
			int index = slot.getContainerSlot();
			if (index >= GameItems.HOTBAR && index < GameItems.MAIN_END) {
				graphics.fill(slot.x, slot.y, slot.x + 16, slot.y + 16, 0xA0101010);
			}
		}
	}
}
