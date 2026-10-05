using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.HighDefinition;

namespace LethalCraft.Render
{
	/// <summary>
	/// Materials for Minecraft's geometry in Lethal Company's HDRP: HDRP/Lit, so the game's own lights
	/// (sun, facility lamps, flashlights), fog and volumetrics light the blocks like the rest of the level.
	///
	/// HDRP/Lit has no vertex colour, which Minecraft uses for biome tint (grass, leaves, water) and
	/// ambient occlusion. The tint comes back per material instead: MeshBuilder groups triangles by
	/// their tint and each group gets a material with that base colour. Ambient occlusion is lost.
	/// </summary>
	internal static class Materials
	{
		public enum Kind { Opaque, Cutout, Translucent }

		private static readonly Dictionary<(Kind, Texture, uint), Material> Cache = new Dictionary<(Kind, Texture, uint), Material>();
		private static Shader lit;
		private static bool picked;

		public static int Count => Cache.Count;

		private static void Pick()
		{
			if (picked)
			{
				return;
			}
			picked = true;
			lit = Shader.Find("HDRP/Lit");
			if (lit == null || !lit.isSupported)
			{
				lit = Shader.Find("HDRP/Unlit");
			}
			Log.Info($"shaders: blocks use {(lit != null ? lit.name : "(none)")}");
		}

		/// <summary>tint: RGBA8, red in the low byte.</summary>
		public static Material Get(Kind kind, Texture texture, uint tint)
		{
			Pick();
			var key = (kind, texture, tint);
			if (Cache.TryGetValue(key, out Material m) && m != null)
			{
				return m;
			}
			m = Create(kind, texture, tint);
			Cache[key] = m;
			return m;
		}

		public static Material Get(Kind kind, Texture texture) => Get(kind, texture, White);

		private static Material lines;

		/// <summary>The targeted block's outline: unlit lines, black at 45% (Minecraft's).</summary>
		public static Material Lines()
		{
			if (lines != null)
			{
				return lines;
			}
			var unlit = Shader.Find("HDRP/Unlit");
			lines = new Material(unlit != null ? unlit : lit) { name = "LethalCraft outline", hideFlags = HideFlags.DontSave };
			var color = new Color(0f, 0f, 0f, 0.45f);
			Set(lines, "_UnlitColor", color);
			Set(lines, "_BaseColor", color);
			HDMaterial.SetSurfaceType(lines, true);
			Set(lines, "_BlendMode", 0f);
			HDMaterial.ValidateMaterial(lines);
			return lines;
		}

		public const uint White = 0xFFFFFFFF;

		private static Material Create(Kind kind, Texture texture, uint rgba)
		{
			var tint = new Color32((byte)rgba, (byte)(rgba >> 8), (byte)(rgba >> 16), 255);
			var m = new Material(lit) { name = $"LethalCraft {kind}", hideFlags = HideFlags.DontSave };
			Set(m, "_BaseColorMap", texture);
			Set(m, "_UnlitColorMap", texture);
			Set(m, "_BaseColor", (Color)tint);
			Set(m, "_UnlitColor", (Color)tint);
			Set(m, "_Metallic", 0f);
			Set(m, "_Smoothness", 0f);
			Set(m, "_SpecularOcclusionMode", 0f);
			switch (kind)
			{
				case Kind.Opaque:
					HDMaterial.SetSurfaceType(m, false);
					HDMaterial.SetAlphaClipping(m, false);
					break;
				case Kind.Cutout:
					HDMaterial.SetSurfaceType(m, false);
					HDMaterial.SetAlphaClipping(m, true);
					HDMaterial.SetAlphaCutoff(m, 0.1f);
					break;
				case Kind.Translucent:
					HDMaterial.SetSurfaceType(m, true);
					Set(m, "_BlendMode", 0f); // alpha
					Set(m, "_ZWrite", 0f);
					Set(m, "_TransparentZWrite", 0f);
					break;
			}
			// Single-sided, as Minecraft draws them (plants are two quads facing both ways).
			Set(m, "_DoubleSidedEnable", 0f);
			Set(m, "_CullMode", (float)CullMode.Back);
			HDMaterial.ValidateMaterial(m);
			if (Cache.Count == 0 || kind != Kind.Opaque && !Logged.Contains(kind))
			{
				Logged.Add(kind);
				Log.Info($"material {kind}: queue {m.renderQueue}, keywords [{string.Join(" ", m.shaderKeywords)}]");
			}
			return m;
		}

		private static readonly HashSet<Kind> Logged = new HashSet<Kind>();

		private static void Set(Material m, string name, float value)
		{
			if (m.HasProperty(name))
			{
				m.SetFloat(name, value);
			}
		}

		private static void Set(Material m, string name, Color value)
		{
			if (m.HasProperty(name))
			{
				m.SetColor(name, value);
			}
		}

		private static void Set(Material m, string name, Texture value)
		{
			if (m.HasProperty(name))
			{
				m.SetTexture(name, value);
			}
		}

		public static void Forget(Texture texture)
		{
			var stale = new List<(Kind, Texture, uint)>();
			foreach (var key in Cache.Keys)
			{
				if (key.Item2 == texture)
				{
					stale.Add(key);
				}
			}
			foreach (var key in stale)
			{
				Object.Destroy(Cache[key]);
				Cache.Remove(key);
			}
		}
	}
}
