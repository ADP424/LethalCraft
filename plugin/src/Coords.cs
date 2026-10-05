using UnityEngine;

namespace LethalCraft
{
	/// <summary>
	/// Unity (left-handed: X right, Y up, Z forward; metres) &lt;-&gt; Minecraft (right-handed: X east,
	/// Y up, Z south; blocks). Z is mirrored, so triangle winding flips between the two.
	/// Yaw: Unity 0 faces +Z, which is Minecraft north (yaw 180); both pitches are positive downward.
	///
	/// Minecraft's world is static, but Lethal Company's ship moves (landing, take off, orbit). So
	/// Minecraft's coordinates are relative to a <see cref="Frame"/>: the ship's transform while the
	/// player is aboard (the ship's own space is what its blocks and collision live in), or none (the
	/// world's own space). Everything here goes through the frame, so the rest of the plugin never
	/// has to know which one it is.
	/// </summary>
	internal static class Coords
	{
		/// <summary>Blocks per metre. Lethal Company's player is 2.5 m tall, Minecraft's 1.8 blocks.</summary>
		public static float K => Lc.Lobby.Scale > 0f ? Lc.Lobby.Scale : Config.BlocksPerMeter; // in the host's world: the host's scale

		/// <summary>Minecraft X of this frame's origin (blocks): a separate stretch of the world per frame.</summary>
		public static double OffsetX;

		/// <summary>The transform Minecraft's world is attached to; null: Unity's world space.</summary>
		public static Transform Frame;

		private static Vector3 Local(Vector3 world) => Frame != null ? Frame.InverseTransformPoint(world) : world;

		private static Vector3 LocalDir(Vector3 world) => Frame != null ? Frame.InverseTransformDirection(world) : world;

		public static void ToMc(Vector3 u, out double x, out double y, out double z)
		{
			Vector3 l = Local(u);
			x = l.x * (double)K + OffsetX;
			y = l.y * (double)K;
			z = -l.z * (double)K;
		}

		/// <summary>Single precision is fine for anything relative; positions near a far slot lose ~1 mm.</summary>
		public static Vector3 ToMcF(Vector3 u)
		{
			Vector3 l = Local(u);
			return new Vector3((float)(l.x * (double)K + OffsetX), l.y * K, -l.z * K);
		}

		/// <summary>Minecraft block coordinates to Unity world space.</summary>
		public static Vector3 ToUnity(double x, double y, double z)
		{
			var l = new Vector3((float)((x - OffsetX) / K), (float)(y / K), (float)(-z / K));
			return Frame != null ? Frame.TransformPoint(l) : l;
		}

		/// <summary>Minecraft block coordinates to the frame's own Unity space (what a child of the frame has as its local position).</summary>
		public static Vector3 ToLocal(double x, double y, double z) => new Vector3((float)((x - OffsetX) / K), (float)(y / K), (float)(-z / K));

		public static Vector3 ToUnity(Vector3 mc) => ToUnity(mc.x, mc.y, mc.z);

		/// <summary>A direction or offset (no scale, no offset).</summary>
		public static Vector3 DirToMc(Vector3 u)
		{
			Vector3 l = LocalDir(u);
			return new Vector3(l.x, l.y, -l.z);
		}

		public static Vector3 DirToUnity(Vector3 mc)
		{
			var l = new Vector3(mc.x, mc.y, -mc.z);
			return Frame != null ? Frame.TransformDirection(l) : l;
		}

		public static float YawToMc(float unityYaw) => Mathf.Repeat(unityYaw + 180f, 360f);

		public static float YawToUnity(float mcYaw) => mcYaw - 180f;

		/// <summary>The Unity yaw (degrees) a world direction has relative to the frame's own forward.</summary>
		public static float FrameYaw(Vector3 worldForward)
		{
			Vector3 l = LocalDir(worldForward);
			return Mathf.Atan2(l.x, l.z) * Mathf.Rad2Deg;
		}

		/// <summary>A Minecraft-axis-aligned box (blocks) as an oriented box in the world: centre, half extents, rotation.</summary>
		public static void RegionBox(float x0, float y0, float z0, float x1, float y1, float z1, out Vector3 centre, out Vector3 half, out Quaternion rotation)
		{
			// In frame space (Unity axes, metres): Z mirrored.
			float ax = (x0 - (float)OffsetX) / K, bx = (x1 - (float)OffsetX) / K;
			float ay = y0 / K, by = y1 / K;
			float az = -z0 / K, bz = -z1 / K;
			var l = new Vector3((ax + bx) * 0.5f, (ay + by) * 0.5f, (az + bz) * 0.5f);
			half = new Vector3(Mathf.Abs(bx - ax), Mathf.Abs(by - ay), Mathf.Abs(bz - az)) * 0.5f;
			centre = Frame != null ? Frame.TransformPoint(l) : l;
			rotation = Frame != null ? Frame.rotation : Quaternion.identity;
		}

		/// <summary>The Unity rotation of a Minecraft look (yaw, pitch in MC degrees).</summary>
		public static Quaternion Look(float mcYaw, float mcPitch, float rollDeg = 0f)
		{
			var local = Quaternion.Euler(mcPitch, YawToUnity(mcYaw), rollDeg);
			return Frame != null ? Frame.rotation * local : local;
		}
	}
}
