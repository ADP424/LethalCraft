using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using UnityEngine;

namespace LethalCraft.Inventory
{
	/// <summary>
	/// The game's item icons for Minecraft's hotbar. Once a game is loaded, every item the game knows
	/// (its allItemsList: scrap, tools, modded items) is drawn to a 64x64 PNG in
	/// %LOCALAPPDATA%\LethalCraft\item-icons, named by its key (the item's name, lower case, letters
	/// and digits joined by underscores: the same key Minecraft's side derives from the name). A
	/// stamp file written last says the set is complete; Minecraft builds a resource pack from it
	/// (dev.lethalcraft.client.ItemIconPack) and its token items use those icons.
	/// </summary>
	internal sealed class ItemIcons
	{
		public static readonly ItemIcons Instance = new ItemIcons();

		private const int Size = 64;
		private const int PerFrame = 6;
		private const string Version = "1";

		public static string Folder => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "LethalCraft", "item-icons");

		private readonly Queue<Item> todo = new Queue<Item>();
		private readonly HashSet<string> keys = new HashSet<string>();
		private bool started, done;
		private string stamp;

		/// <summary>The key an item's icon goes by (Minecraft's GameItems.iconKey does the same).</summary>
		public static string Key(string name)
		{
			var sb = new StringBuilder();
			bool gap = false;
			foreach (char ch in (name ?? "").ToLowerInvariant())
			{
				if (ch >= 'a' && ch <= 'z' || ch >= '0' && ch <= '9')
				{
					if (gap && sb.Length > 0)
					{
						sb.Append('_');
					}
					sb.Append(ch);
					gap = false;
				}
				else
				{
					gap = true;
				}
			}
			return sb.Length > 0 ? sb.ToString() : "item";
		}

		/// <summary>Every frame in a session: a few icons at a time until all are written.</summary>
		public void Frame()
		{
			if (done)
			{
				return;
			}
			if (!started)
			{
				var list = StartOfRound.Instance != null ? StartOfRound.Instance.allItemsList : null;
				if (list == null || list.itemsList == null)
				{
					return;
				}
				started = true;
				var names = new List<string>();
				foreach (var item in list.itemsList)
				{
					if (item != null && item.itemIcon != null && keys.Add(Key(item.itemName)))
					{
						todo.Enqueue(item);
						names.Add(Key(item.itemName));
					}
				}
				names.Sort(StringComparer.Ordinal);
				stamp = Version + ":" + string.Join(",", names);
				string stampFile = Path.Combine(Folder, "stamp.txt");
				try
				{
					if (File.Exists(stampFile) && File.ReadAllText(stampFile) == stamp)
					{
						todo.Clear();
						done = true;
						Log.Info($"item icons: {names.Count} already in {Folder}");
						return;
					}
					Directory.CreateDirectory(Folder);
					if (File.Exists(stampFile))
					{
						File.Delete(stampFile);
					}
				}
				catch (Exception e)
				{
					Log.Warn($"item icons: can't use {Folder} ({e.Message})");
					done = true;
					return;
				}
			}
			for (int i = 0; i < PerFrame && todo.Count > 0; i++)
			{
				var item = todo.Dequeue();
				try
				{
					Write(item.itemIcon, Path.Combine(Folder, Key(item.itemName) + ".png"));
				}
				catch (Exception e)
				{
					Log.Warn($"item icons: {item.itemName}: {e.Message}");
				}
			}
			if (todo.Count == 0)
			{
				done = true;
				try
				{
					File.WriteAllText(Path.Combine(Folder, "stamp.txt"), stamp);
					Log.Info($"item icons: {keys.Count} written to {Folder}");
				}
				catch (Exception e)
				{
					Log.Warn($"item icons: {e.Message}");
				}
			}
		}

		/// <summary>The sprite's part of its texture, scaled to Size x Size through the GPU (its texture needn't be readable).</summary>
		private static void Write(Sprite sprite, string path)
		{
			var tex = sprite.texture;
			var r = sprite.textureRect;
			var rt = RenderTexture.GetTemporary(Size, Size, 0, RenderTextureFormat.ARGB32, RenderTextureReadWrite.sRGB);
			var prev = RenderTexture.active;
			try
			{
				Graphics.Blit(tex, rt, new Vector2(r.width / tex.width, r.height / tex.height), new Vector2(r.x / tex.width, r.y / tex.height));
				RenderTexture.active = rt;
				var copy = new Texture2D(Size, Size, TextureFormat.RGBA32, false, false);
				copy.ReadPixels(new Rect(0, 0, Size, Size), 0, 0);
				copy.Apply(false, false);
				File.WriteAllBytes(path, copy.EncodeToPNG());
				UnityEngine.Object.Destroy(copy);
			}
			finally
			{
				RenderTexture.active = prev;
				RenderTexture.ReleaseTemporary(rt);
			}
		}
	}
}
