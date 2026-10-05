package dev.lethalcraft.client.mixin;

import dev.lethalcraft.LethalCraft;
import dev.lethalcraft.client.ItemIconPack;
import net.minecraft.client.renderer.item.ItemModel;
import net.minecraft.client.resources.model.ModelManager;
import net.minecraft.resources.Identifier;
import org.spongepowered.asm.mixin.Mixin;
import org.spongepowered.asm.mixin.Shadow;
import org.spongepowered.asm.mixin.injection.At;
import org.spongepowered.asm.mixin.injection.Inject;
import org.spongepowered.asm.mixin.injection.callback.CallbackInfoReturnable;

/** A game item whose icon isn't in the item-icons pack (yet) looks like the plain token, not the missing model. */
@Mixin(ModelManager.class)
public abstract class ModelManagerMixin {
	private static final Identifier TOKEN = Identifier.fromNamespaceAndPath(LethalCraft.MOD_ID, "game_item");

	@Shadow
	public abstract ItemModel getItemModel(Identifier id);

	@Inject(method = "getItemModel", at = @At("HEAD"), cancellable = true)
	private void lethalcraft$fallbackIcon(Identifier id, CallbackInfoReturnable<ItemModel> cir) {
		if (LethalCraft.MOD_ID.equals(id.getNamespace()) && id.getPath().startsWith("game/") && !ItemIconPack.known(id.getPath().substring(5))) {
			cir.setReturnValue(this.getItemModel(TOKEN));
		}
	}
}
