using System.Collections.Generic;
using LethalCraft.Link;
using UnityEngine;
using UnityEngine.Rendering;

namespace LethalCraft.Render
{
	/// <summary>
	/// RenVertex triangle lists (Minecraft space) -> Unity meshes. Positions mirror Z and scale by
	/// the block size, so every triangle's winding is reversed to stay front-facing.
	/// RenVertex: float x, y, z, u, v; u32 colour (RGBA8, r low); u32 light; u32 flags
	/// (bit0 cutout, bit1 translucent, bits 4-6 face normal as MC Direction ordinal + 1).
	///
	/// HDRP/Lit doesn't read vertex colour, so a section's triangles are grouped by (kind, tint), one
	/// submesh and one material each (Materials). The tint is the triangle's brightest vertex colour,
	/// coarsened so a section has a handful of groups; grey (ambient occlusion only) counts as white.
	/// </summary>
	internal static unsafe class MeshBuilder
	{
		// Minecraft's Direction order: DOWN, UP, NORTH, SOUTH, WEST, EAST; in Unity axes (Z mirrored).
		// No normal (plants, cross quads) is lit as if facing up, like Minecraft shades them.
		// 7: lit by the triangle's own normal (entities), filled in per triangle (zero until then).
		private static readonly Vector3[] Normals =
		{
			Vector3.up, Vector3.down, Vector3.up, new Vector3(0, 0, 1), new Vector3(0, 0, -1), Vector3.left, Vector3.right, Vector3.zero,
		};

		public struct Group
		{
			public Materials.Kind Kind;
			public uint Tint;
		}

		private static void FaceNormal(int a, int b, int c)
		{
			if (nrm[a] != Vector3.zero && nrm[b] != Vector3.zero && nrm[c] != Vector3.zero)
			{
				return;
			}
			Vector3 n = Vector3.Cross(pos[b] - pos[a], pos[c] - pos[a]);
			n = n.sqrMagnitude > 1e-12f ? n.normalized : Vector3.up;
			if (nrm[a] == Vector3.zero) nrm[a] = n;
			if (nrm[b] == Vector3.zero) nrm[b] = n;
			if (nrm[c] == Vector3.zero) nrm[c] = n;
		}

		private static List<Vector3> pos;
		private static List<Vector3> nrm;
		private static List<Vector2> uv;
		private static readonly Dictionary<ulong, int> groupIndex = new Dictionary<ulong, int>();
		private static readonly List<List<int>> groupTris = new List<List<int>>();

		private static void Begin(int capacity)
		{
			pos ??= new List<Vector3>(capacity);
			nrm ??= new List<Vector3>(capacity);
			uv ??= new List<Vector2>(capacity);
			pos.Clear();
			nrm.Clear();
			uv.Clear();
			groupIndex.Clear();
			foreach (var t in groupTris)
			{
				t.Clear();
			}
		}

		private static void AddVertex(byte* v, float k)
		{
			float x = *(float*)v, y = *(float*)(v + 4), z = *(float*)(v + 8);
			pos.Add(new Vector3(x / k, y / k, -z / k));
			uv.Add(new Vector2(*(float*)(v + 12), *(float*)(v + 16)));
			uint flags = *(uint*)(v + 28);
			nrm.Add(Normals[(flags >> 4) & 7]);
		}

		/// <summary>The tint a triangle gets: its brightest vertex colour, 4 bits a channel; grey is white.</summary>
		private static uint Tint(byte* v0)
		{
			uint best = 0;
			int bestSum = -1;
			for (int i = 0; i < 3; i++)
			{
				uint c = *(uint*)(v0 + i * Proto.RenVertexBytes + 20);
				int sum = (int)(c & 0xFF) + (int)((c >> 8) & 0xFF) + (int)((c >> 16) & 0xFF);
				if (sum > bestSum)
				{
					bestSum = sum;
					best = c;
				}
			}
			int r = (int)(best & 0xFF), g = (int)((best >> 8) & 0xFF), b = (int)((best >> 16) & 0xFF);
			int max = Mathf.Max(r, Mathf.Max(g, b)), min = Mathf.Min(r, Mathf.Min(g, b));
			if (max - min <= 12)
			{
				return Materials.White;
			}
			r = (r & 0xF0) | 0x08;
			g = (g & 0xF0) | 0x08;
			b = (b & 0xF0) | 0x08;
			return 0xFF000000u | (uint)(b << 16) | (uint)(g << 8) | (uint)r;
		}

		private static List<int> GroupFor(Materials.Kind kind, uint tint, List<Group> groups)
		{
			ulong key = ((ulong)kind << 32) | tint;
			if (!groupIndex.TryGetValue(key, out int index))
			{
				index = groups.Count;
				groupIndex[key] = index;
				groups.Add(new Group { Kind = kind, Tint = tint });
				while (groupTris.Count <= index)
				{
					groupTris.Add(new List<int>());
				}
			}
			return groupTris[index];
		}

		/// <summary>A block section; groups (filled in) says which kind and tint each submesh is.</summary>
		public static Mesh FromVertices(byte* data, int count, List<Group> groups)
		{
			groups.Clear();
			count -= count % 3;
			if (count <= 0)
			{
				return null;
			}
			Begin(count);
			float k = Coords.K;
			for (int i = 0; i < count; i += 3)
			{
				byte* v0 = data + i * Proto.RenVertexBytes;
				uint flags = *(uint*)(v0 + 28);
				var kind = (flags & 2) != 0 ? Materials.Kind.Translucent : (flags & 1) != 0 ? Materials.Kind.Cutout : Materials.Kind.Opaque;
				int b = pos.Count;
				AddVertex(v0, k);
				AddVertex(v0 + Proto.RenVertexBytes, k);
				AddVertex(v0 + 2 * Proto.RenVertexBytes, k);
				var list = GroupFor(kind, Tint(v0), groups);
				list.Add(b);
				list.Add(b + 2);
				list.Add(b + 1);
				FaceNormal(b, b + 2, b + 1);
			}
			var mesh = new Mesh();
			mesh.indexFormat = pos.Count > 65000 ? IndexFormat.UInt32 : IndexFormat.UInt16;
			mesh.SetVertices(pos);
			mesh.SetNormals(nrm);
			mesh.SetUVs(0, uv);
			mesh.subMeshCount = groups.Count;
			for (int s = 0; s < groups.Count; s++)
			{
				mesh.SetTriangles(groupTris[s], s, false);
			}
			mesh.RecalculateBounds();
			return mesh;
		}

		/// <summary>Entity batches: one submesh per (first, count, tint) range, tinted by its first vertex.</summary>
		public static void FillBatches(Mesh mesh, byte* verts, int vertexCount, List<(int first, int count)> ranges, List<uint> tints)
		{
			Begin(vertexCount);
			tints.Clear();
			float k = Coords.K;
			for (int i = 0; i < vertexCount; i++)
			{
				AddVertex(verts + i * Proto.RenVertexBytes, k);
			}
			foreach (var (first, count) in ranges)
			{
				for (int i = first; i + 2 < first + count; i += 3)
				{
					FaceNormal(i, i + 2, i + 1);
				}
				tints.Add(count >= 3 ? Tint(verts + first * Proto.RenVertexBytes) : Materials.White);
			}
			for (int i = 0; i < nrm.Count; i++)
			{
				if (nrm[i] == Vector3.zero)
				{
					nrm[i] = Vector3.up;
				}
			}
			mesh.Clear();
			mesh.indexFormat = vertexCount > 65000 ? IndexFormat.UInt32 : IndexFormat.UInt16;
			mesh.SetVertices(pos);
			mesh.SetNormals(nrm);
			mesh.SetUVs(0, uv);
			mesh.subMeshCount = ranges.Count;
			var tris = new List<int>();
			for (int s = 0; s < ranges.Count; s++)
			{
				tris.Clear();
				var (first, count) = ranges[s];
				count -= count % 3;
				for (int i = first; i < first + count; i += 3)
				{
					tris.Add(i);
					tris.Add(i + 2);
					tris.Add(i + 1);
				}
				mesh.SetTriangles(tris, s, false);
			}
			mesh.RecalculateBounds();
		}
	}
}
