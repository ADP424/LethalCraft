using System;
using System.Collections.Generic;
using LethalCraft.Link;
using UnityEngine;
using UnityEngine.Rendering;

namespace LethalCraft.Render
{
	/// <summary>
	/// Draws Minecraft's world inside Lethal Company's: the render ring's block meshes (built by
	/// Minecraft's own block renderer), its texture atlas and entity textures, the player's
	/// third-person body and every other entity and particle. All of it as ordinary Unity meshes, so
	/// the game's camera, lights, fog and depth handle it like the rest of the level.
	///
	/// Everything hangs off one root, which <see cref="Place"/> puts where Minecraft's origin is in the
	/// current frame each frame: on the ship (riding with it) or in the moon's world space, shifted by
	/// the frame's slot. Children use Minecraft coordinates scaled to metres (Z mirrored), no offset.
	/// </summary>
	internal sealed unsafe class BlockRenderer
	{
		public static readonly BlockRenderer Instance = new BlockRenderer();

		private GameObject root;
		private Texture2D atlas;
		private int atlasW, atlasH;
		private Texture2D regionScratch;
		private readonly Dictionary<uint, Texture2D> textures = new Dictionary<uint, Texture2D>();
		private readonly Dictionary<long, GameObject> sections = new Dictionary<long, GameObject>();
		private readonly List<MeshBuilder.Group> groups = new List<MeshBuilder.Group>();
		private DynamicMesh avatar, scene;
		private bool avatarVisible, avatarPlaced;

		public Texture2D Atlas => atlas;
		public int SectionCount => sections.Count;

		public GameObject Root
		{
			get
			{
				if (root == null)
				{
					root = new GameObject("LethalCraft World");
					UnityEngine.Object.DontDestroyOnLoad(root);
					sections.Clear();
					avatar = null;
					scene = null;
				}
				return root;
			}
		}

		/// <summary>Minecraft block coordinates to the root's own space.</summary>
		public static Vector3 Local(double x, double y, double z)
		{
			float k = Coords.K;
			return new Vector3((float)(x / k), (float)(y / k), (float)(-z / k));
		}

		/// <summary>Main thread, once a frame: everything Minecraft sent since last frame.</summary>
		public void Drain()
		{
			SharedLink.Instance.DrainRender(OnMessage, 48L << 20);
		}

		/// <summary>
		/// LateUpdate, once the ship has moved: Minecraft's origin in the current frame. Hidden outside
		/// a game (main menu), where there's nothing for it to be part of.
		/// </summary>
		public void Place(bool show)
		{
			var go = Root;
			if (go.activeSelf != show)
			{
				go.SetActive(show);
			}
			if (!show)
			{
				return;
			}
			var origin = new Vector3((float)(-Coords.OffsetX / Coords.K), 0f, 0f);
			var frame = Coords.Frame;
			var t = go.transform;
			if (frame != null)
			{
				t.SetPositionAndRotation(frame.TransformPoint(origin), frame.rotation);
				t.localScale = frame.lossyScale;
			}
			else
			{
				t.SetPositionAndRotation(origin, Quaternion.identity);
				t.localScale = Vector3.one;
			}
		}

		private void OnMessage(uint type, byte* data, uint bytes)
		{
			switch (type)
			{
				case Proto.RenAtlas:
					OnAtlas(data, bytes);
					break;
				case Proto.RenSection:
					OnSection(data, bytes);
					break;
				case Proto.RenClearAll:
					ClearAll();
					break;
				case Proto.RenTexture:
					OnTexture(data, bytes);
					break;
				case Proto.RenAvatar:
					OnAvatar(data, bytes);
					break;
				case Proto.RenScene:
					OnScene(data, bytes);
					break;
				case Proto.RenAtlasRegion:
					OnAtlasRegion(data, bytes);
					break;
				case Proto.RenLights:
					BlockLights.Instance.OnLights(data, bytes);
					break;
				case Proto.RenSolids:
					World.BlockSolids.Instance.OnSolids(data, bytes);
					break;
				case Proto.RenDug:
					World.DugBlocks.OnDug(data, bytes);
					break;
			}
		}

		public void ClearAll()
		{
			foreach (var go in sections.Values)
			{
				DestroySection(go);
			}
			sections.Clear();
			avatar?.Clear();
			scene?.Clear();
			BlockLights.Instance.Clear();
			World.BlockSolids.Instance.Clear();
			World.DugBlocks.Clear();
		}

		// ---- textures ------------------------------------------------------------------------------

		private void OnAtlas(byte* data, uint bytes)
		{
			int w = *(int*)data, h = *(int*)(data + 4);
			if (w <= 0 || h <= 0 || bytes < 8 + (long)w * h * 4)
			{
				return;
			}
			if (atlas == null || atlasW != w || atlasH != h)
			{
				if (atlas != null)
				{
					Materials.Forget(atlas);
					UnityEngine.Object.Destroy(atlas);
				}
				atlas = NewTexture(w, h, "Minecraft atlas");
				atlasW = w;
				atlasH = h;
			}
			// Minecraft's rows are top first and its v runs down from the top: loaded as-is, texture
			// row 0 is Minecraft's top row and v samples the same texel in both. No flip anywhere.
			atlas.LoadRawTextureData((IntPtr)(data + 8), w * h * 4);
			atlas.Apply(false, false);
			Log.Info($"atlas {w}x{h} from Minecraft");
		}

		private static Texture2D NewTexture(int w, int h, string name)
		{
			// sRGB (linear: false): Minecraft's textures are colours, and HDRP lights in linear space.
			return new Texture2D(w, h, TextureFormat.RGBA32, false, false)
			{
				name = name,
				filterMode = FilterMode.Point,
				wrapMode = TextureWrapMode.Clamp,
				anisoLevel = 0,
				hideFlags = HideFlags.DontSave,
			};
		}

		private void OnAtlasRegion(byte* data, uint bytes)
		{
			if (atlas == null || bytes < 16)
			{
				return;
			}
			int x = *(int*)data, y = *(int*)(data + 4), w = *(int*)(data + 8), h = *(int*)(data + 12);
			if (w <= 0 || h <= 0 || x < 0 || y < 0 || x + w > atlasW || y + h > atlasH || bytes < 16 + (long)w * h * 4)
			{
				return;
			}
			if (regionScratch == null || regionScratch.width != w || regionScratch.height != h)
			{
				if (regionScratch != null)
				{
					UnityEngine.Object.Destroy(regionScratch);
				}
				regionScratch = NewTexture(w, h, "Minecraft atlas region");
			}
			regionScratch.LoadRawTextureData((IntPtr)(data + 16), w * h * 4);
			regionScratch.Apply(false, false);
			// GPU copy into the atlas: re-uploading the whole atlas 20 times a second would cost far more.
			Graphics.CopyTexture(regionScratch, 0, 0, 0, 0, w, h, atlas, 0, 0, x, y);
		}

		private void OnTexture(byte* data, uint bytes)
		{
			uint id = *(uint*)data;
			int w = *(int*)(data + 4), h = *(int*)(data + 8);
			if (w <= 0 || h <= 0 || bytes < 16 + (long)w * h * 4)
			{
				return;
			}
			if (!textures.TryGetValue(id, out Texture2D tex) || tex == null || tex.width != w || tex.height != h)
			{
				if (tex != null)
				{
					Materials.Forget(tex);
					UnityEngine.Object.Destroy(tex);
				}
				tex = NewTexture(w, h, $"Minecraft texture {id}");
				textures[id] = tex;
			}
			tex.LoadRawTextureData((IntPtr)(data + 16), w * h * 4);
			tex.Apply(false, false);
		}

		public Texture TextureFor(uint id) => id == 0 ? (Texture)atlas : textures.TryGetValue(id, out Texture2D t) ? t : null;

		// ---- block sections ----------------------------------------------------------------------

		private void OnSection(byte* data, uint bytes)
		{
			int sx = *(int*)data, sy = *(int*)(data + 4), sz = *(int*)(data + 8);
			uint count = *(uint*)(data + 12);
			long key = World.Clip.Key(sx, sy, sz);
			var parent = Root.transform;
			if (sections.TryGetValue(key, out GameObject old))
			{
				DestroySection(old);
				sections.Remove(key);
			}
			if (count == 0 || bytes < 16 + (long)count * Proto.RenVertexBytes || atlas == null)
			{
				return;
			}
			var mesh = MeshBuilder.FromVertices(data + 16, (int)count, groups);
			if (mesh == null)
			{
				return;
			}
			mesh.name = $"section {sx} {sy} {sz}";
			var go = new GameObject(mesh.name);
			go.transform.SetParent(parent, false);
			go.transform.localPosition = Local(sx * 16.0, sy * 16.0, sz * 16.0);
			go.AddComponent<MeshFilter>().sharedMesh = mesh;
			var mr = go.AddComponent<MeshRenderer>();
			var mats = new Material[groups.Count];
			bool translucentOnly = true;
			for (int i = 0; i < groups.Count; i++)
			{
				mats[i] = Materials.Get(groups[i].Kind, atlas, groups[i].Tint);
				translucentOnly &= groups[i].Kind == Materials.Kind.Translucent;
			}
			mr.sharedMaterials = mats;
			mr.shadowCastingMode = translucentOnly ? ShadowCastingMode.Off : ShadowCastingMode.On;
			mr.receiveShadows = true;
			mr.lightProbeUsage = LightProbeUsage.Off;
			mr.reflectionProbeUsage = ReflectionProbeUsage.Off;
			sections[key] = go;
		}

		private static void DestroySection(GameObject go)
		{
			if (go == null)
			{
				return;
			}
			var mf = go.GetComponent<MeshFilter>();
			if (mf != null && mf.sharedMesh != null)
			{
				UnityEngine.Object.Destroy(mf.sharedMesh);
			}
			UnityEngine.Object.Destroy(go);
		}

		// ---- entities ----------------------------------------------------------------------------

		/// <summary>The player's own body (third person), relative to its feet; 0 batches = not shown.</summary>
		private void OnAvatar(byte* data, uint bytes)
		{
			if (avatar == null)
			{
				avatar = new DynamicMesh("Minecraft player", Root.transform);
			}
			uint batches = *(uint*)data, verts = *(uint*)(data + 4);
			avatarVisible = batches > 0 && avatar.Set(data + 8, batches, verts, bytes - 8, this);
			avatar.Visible = avatarVisible && avatarPlaced;
		}

		/// <summary>Every other entity and all particles this frame, relative to the scene origin.</summary>
		private void OnScene(byte* data, uint bytes)
		{
			if (scene == null)
			{
				scene = new DynamicMesh("Minecraft entities", Root.transform);
			}
			double ox = *(double*)data, oy = *(double*)(data + 8), oz = *(double*)(data + 16);
			uint batches = *(uint*)(data + 24), verts = *(uint*)(data + 28);
			bool any = batches > 0 && scene.Set(data + 32, batches, verts, bytes - 32, this);
			scene.Visible = any;
			scene.Transform.localPosition = Local(ox, oy, oz);
		}

		/// <summary>Each frame: where the player's feet are (Minecraft coordinates) and whether their body shows.</summary>
		public void PlaceAvatar(double x, double y, double z, bool show)
		{
			avatarPlaced = show;
			if (avatar == null)
			{
				return;
			}
			avatar.Transform.localPosition = Local(x, y, z);
			avatar.Transform.localRotation = Quaternion.identity;
			avatar.Visible = show && avatarVisible;
		}
	}

	/// <summary>A mesh rebuilt from render-ring batches (RenBatch[] + RenVertex[]) every time Minecraft sends one.</summary>
	internal sealed unsafe class DynamicMesh
	{
		private readonly GameObject go;
		private readonly Mesh mesh;
		private readonly MeshRenderer renderer;
		private readonly List<Material> materials = new List<Material>();
		private readonly List<(int first, int count)> ranges = new List<(int first, int count)>();
		private readonly List<Materials.Kind> kinds = new List<Materials.Kind>();
		private readonly List<Texture> rangeTextures = new List<Texture>();
		private readonly List<uint> tints = new List<uint>();

		public Transform Transform => go.transform;

		public bool Visible
		{
			get => renderer.enabled;
			set => renderer.enabled = value;
		}

		public DynamicMesh(string name, Transform parent)
		{
			go = new GameObject(name);
			go.transform.SetParent(parent, false);
			mesh = new Mesh { name = name };
			mesh.MarkDynamic();
			go.AddComponent<MeshFilter>().sharedMesh = mesh;
			renderer = go.AddComponent<MeshRenderer>();
			renderer.shadowCastingMode = ShadowCastingMode.On;
			renderer.receiveShadows = true;
			renderer.lightProbeUsage = LightProbeUsage.Off;
			renderer.enabled = false;
		}

		public void Clear()
		{
			mesh.Clear();
			renderer.enabled = false;
		}

		public bool Set(byte* data, uint batchCount, uint vertexCount, uint bytes, BlockRenderer textures)
		{
			if (bytes < batchCount * (ulong)Proto.RenBatchBytes + vertexCount * (ulong)Proto.RenVertexBytes)
			{
				return false;
			}
			byte* verts = data + batchCount * Proto.RenBatchBytes;
			ranges.Clear();
			kinds.Clear();
			rangeTextures.Clear();
			for (uint i = 0; i < batchCount; i++)
			{
				byte* b = data + i * Proto.RenBatchBytes;
				uint tex = *(uint*)b, first = *(uint*)(b + 4), count = *(uint*)(b + 8), flags = *(uint*)(b + 12);
				Texture t = textures.TextureFor(tex);
				if (t == null || first + count > vertexCount || count < 3)
				{
					continue;
				}
				ranges.Add(((int)first, (int)count));
				kinds.Add((flags & 1) != 0 ? Materials.Kind.Translucent : Materials.Kind.Cutout);
				rangeTextures.Add(t);
			}
			if (ranges.Count == 0)
			{
				return false;
			}
			MeshBuilder.FillBatches(mesh, verts, (int)vertexCount, ranges, tints);
			materials.Clear();
			for (int i = 0; i < ranges.Count; i++)
			{
				materials.Add(Materials.Get(kinds[i], rangeTextures[i], tints[i]));
			}
			renderer.sharedMaterials = materials.ToArray();
			return true;
		}
	}
}
