using System;
using LethalCraft.Link;
using UnityEngine;
using UnityEngine.UI;

namespace LethalCraft.Render
{
	/// <summary>
	/// Minecraft's own picture on top of Lethal Company's: its first-person hand and held item, hotbar,
	/// hearts, crosshair and every open Minecraft screen, rendered by the hidden Minecraft at the game's
	/// resolution on a transparent background (premultiplied alpha) and shipped through the overlay
	/// triple buffer. Drawn by a screen-space overlay canvas, which HDRP leaves alone (IMGUI's
	/// Graphics.DrawTexture with a built-in shader doesn't survive HDRP).
	/// </summary>
	internal sealed unsafe class Overlay : MonoBehaviour
	{
		public static Overlay Instance { get; private set; }

		private Texture2D frame;
		private Texture2D cursorTex;
		private Canvas canvas;
		private RawImage image;
		private RawImage cursorImage;
		private RectTransform cursorRect;
		private long lastFrameId;
		private bool show;

		public bool ShowCursor { get; set; }
		public Vector2Int Cursor { get; set; }
		public long FramesShown { get; private set; }

		public bool Show
		{
			get => show;
			set
			{
				show = value;
				if (canvas != null)
				{
					canvas.enabled = value && frame != null;
				}
			}
		}

		private void Awake()
		{
			Instance = this;
			cursorTex = MakeCursor();
			var go = new GameObject("LethalCraft overlay");
			go.transform.SetParent(transform, false);
			canvas = go.AddComponent<Canvas>();
			canvas.renderMode = RenderMode.ScreenSpaceOverlay;
			canvas.sortingOrder = 32000;
			canvas.enabled = false;

			image = NewImage(go.transform, "frame", MakeMaterial());
			image.rectTransform.anchorMin = Vector2.zero;
			image.rectTransform.anchorMax = Vector2.one;
			image.rectTransform.offsetMin = Vector2.zero;
			image.rectTransform.offsetMax = Vector2.zero;

			cursorImage = NewImage(go.transform, "cursor", null);
			cursorImage.texture = cursorTex;
			cursorRect = cursorImage.rectTransform;
			cursorRect.anchorMin = cursorRect.anchorMax = Vector2.zero;
			cursorRect.pivot = new Vector2(0f, 1f);
			cursorRect.sizeDelta = new Vector2(cursorTex.width, cursorTex.height);
			cursorImage.enabled = false;
		}

		private static RawImage NewImage(Transform parent, string name, Material material)
		{
			var go = new GameObject(name);
			go.transform.SetParent(parent, false);
			var img = go.AddComponent<RawImage>();
			img.raycastTarget = false;
			if (material != null)
			{
				img.material = material;
			}
			return img;
		}

		/// <summary>
		/// The frame is premultiplied. The legacy premultiplied particle shader is in the game's build
		/// (UI canvases are drawn outside HDRP's passes); if it isn't, UI/Default still draws it
		/// (translucent edges come out a little dark).
		/// </summary>
		private static Material MakeMaterial()
		{
			var shader = Shader.Find("Legacy Shaders/Particles/Alpha Blended Premultiply");
			if (shader == null)
			{
				Log.Warn("overlay: premultiplied shader missing, falling back to UI/Default");
				shader = Shader.Find("UI/Default");
			}
			return shader != null ? new Material(shader) { hideFlags = HideFlags.DontSave } : null;
		}

		/// <summary>Picks up the newest frame Minecraft published (call once per frame).</summary>
		public void Pull()
		{
			var link = SharedLink.Instance;
			if (!link.Valid || !link.AcquireOverlayFrame())
			{
				return;
			}
			link.FrontHeader(out int w, out int h, out bool bottomUp, out long frameId);
			if (w <= 0 || h <= 0 || w > Proto.MaxOverlayW || h > Proto.MaxOverlayH || frameId == lastFrameId)
			{
				return;
			}
			lastFrameId = frameId;
			if (frame == null || frame.width != w || frame.height != h)
			{
				if (frame != null)
				{
					Destroy(frame);
				}
				frame = new Texture2D(w, h, TextureFormat.RGBA32, false, false)
				{
					name = "Minecraft overlay",
					filterMode = FilterMode.Point,
					wrapMode = TextureWrapMode.Clamp,
					hideFlags = HideFlags.DontSave,
				};
				image.texture = frame;
				Log.Info($"overlay {w}x{h} ({(bottomUp ? "bottom-up" : "top-down")})");
			}
			// Bottom-up rows are exactly Unity's texture order.
			frame.LoadRawTextureData((IntPtr)link.FrontPixels, w * h * 4);
			frame.Apply(false, false);
			// A top-down frame is flipped by the UV rect instead of on the CPU.
			image.uvRect = bottomUp ? new Rect(0, 0, 1, 1) : new Rect(0, 1, 1, -1);
			FramesShown++;
			canvas.enabled = show;
		}

		public void Clear()
		{
			lastFrameId = 0;
			Show = false;
		}

		private void LateUpdate()
		{
			if (canvas == null || !canvas.enabled)
			{
				return;
			}
			cursorImage.enabled = ShowCursor && frame != null;
			if (cursorImage.enabled)
			{
				// The cursor lives in overlay pixels; scale if the overlay and screen differ.
				float sx = (float)Screen.width / frame.width, sy = (float)Screen.height / frame.height;
				cursorRect.anchoredPosition = new Vector2(Cursor.x * sx, Screen.height - Cursor.y * sy);
			}
		}

		/// <summary>An arrow pointer, white with a black edge (Minecraft's window cursor isn't in its frame).</summary>
		private static Texture2D MakeCursor()
		{
			const int w = 12, h = 19;
			var tex = new Texture2D(w, h, TextureFormat.RGBA32, false) { filterMode = FilterMode.Point, hideFlags = HideFlags.DontSave };
			var px = new Color32[w * h];
			for (int y = 0; y < h; y++)
			{
				for (int x = 0; x < w; x++)
				{
					int i = (h - 1 - y) * w + x;
					bool inside = y < 18 && x <= y * 0.6f;
					bool edge = inside && (x < 1.5f || x > y * 0.6f - 1.5f || y > 16.5f);
					px[i] = inside ? (edge ? new Color32(0, 0, 0, 255) : new Color32(255, 255, 255, 255)) : new Color32(0, 0, 0, 0);
				}
			}
			tex.SetPixels32(px);
			tex.Apply();
			return tex;
		}
	}
}
