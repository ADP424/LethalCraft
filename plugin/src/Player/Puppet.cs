using GameNetcodeStuff;
using LethalCraft.Link;
using UnityEngine;
using UnityEngine.InputSystem;

namespace LethalCraft.Player
{
	/// <summary>
	/// Lethal Company's local player as Minecraft's puppet. Minecraft is authoritative for how the
	/// player moves (its keys, speeds, jumping, sneaking, sprinting, collision) and where they stand;
	/// Lethal Company keeps everything else: its look (mouse, camera, the rotation sync to the
	/// others), items, and the position RPCs, which read the transform this class puts at Minecraft's
	/// feet. Its animator is made to follow Minecraft's movement, so the other players see it.
	///
	/// Lethal Company's own movement is neutralised, not skipped (Patches.PlayerPatches): its single
	/// CharacterController.Move call becomes a no-op, so the long Update method runs as usual.
	/// </summary>
	internal sealed class Puppet
	{
		public static readonly Puppet Instance = new Puppet();

		/// <summary>Minecraft drives the player (read by the Harmony patches).</summary>
		public static bool Active;

		/// <summary>The game teleported the player (an entrance, a ladder's end...): Minecraft follows it there (read by the driver).</summary>
		public static bool TeleportedByGame;

		/// <summary>Minecraft says it is standing on something (what Lethal Company's controller should feel).</summary>
		public static bool OnGround;

		// This frame's pose: the frame-local Unity position, and the same in world space.
		public Vector3 FeetLocal { get; private set; }
		public Vector3 Feet { get; private set; }

		/// <summary>Where we last put the body, in Minecraft's coordinates: Lethal Company moving it further than this is a teleport.</summary>
		public Vector3 LastSetMc { get; private set; }
		public bool HaveLastSet { get; private set; }

		// Minecraft's movement state, for Lethal Company's animator.
		private Vector3 lastLocal;
		private bool haveLastLocal;
		private Vector3 velocity;       // frame-local, metres per second

		/// <summary>The body's velocity in world space (metres per second): what a ragdoll flies off with.</summary>
		public Vector3 Velocity => Coords.Frame != null ? Coords.Frame.TransformDirection(velocity) : velocity;
		private bool sneaking, sprinting, wasOnGround = true;

		// Minecraft's camera: look (MC degrees, in the frame), eye height (blocks), view bob, FOV, mouse sensitivity.
		public float Yaw, Pitch;
		private float eyeHeight = 1.62f, bobPhase, bobAmount, fov, sensitivity = 0.5f;
		private uint cameraMode;        // Minecraft's F5: 0 first person, 1 behind, 2 in front
		private float cameraDistance;   // blocks from the eye, already clipped by Minecraft

		/// <summary>Minecraft can have the player right now: free walking, nothing of Lethal Company's own is moving them.</summary>
		public static bool CanDrive(PlayerControllerB p)
		{
			if (p == null || !p.isPlayerControlled || p.isPlayerDead || !p.IsOwner)
			{
				return false;
			}
			var round = StartOfRound.Instance;
			// The game's water "sinks" a player standing on the bottom too, at speed 0: swimming is
			// Minecraft's (World.WaterSurface), so only quicksand, which really sinks, is the game's.
			bool sinking = p.isSinking && p.sinkingSpeedMultiplier > 0f;
			return !p.inSpecialInteractAnimation && !p.isClimbingLadder && !p.inVehicleAnimation && !p.jetpackControls && !p.disablingJetpackControls
				&& !p.isFreeCamera && !p.inTerminalMenu && !p.inShockingMinigame && !sinking && !p.enteringSpecialAnimation
				&& !p.teleportingThisFrame && p.inAnimationWithEnemy == null && !round.suckingPlayersOutOfShip && !round.firingPlayersCutsceneRunning;
		}

		public void Release()
		{
			if (!Active && !HaveLastSet)
			{
				return;
			}
			if (Active)
			{
				Log.Info("puppet off: Lethal Company drives the player again");
				var p = Lc.Game.Player;
				if (p != null && p.playerBodyAnimator != null)
				{
					p.playerBodyAnimator.SetBool("Jumping", false);
				}
			}
			Active = false;
			HaveLastSet = false;
			haveLastLocal = false;
			velocity = Vector3.zero;
			wasOnGround = true;
		}

		/// <summary>Update: the interpolated Minecraft pose for this frame.</summary>
		public void Frame(McState mc, TickInterpolator ticks, float dt)
		{
			if (!Active)
			{
				Log.Info("puppet on: Minecraft drives the player");
				// Minecraft looks where the game's player was looking.
				var p = Lc.Game.Player;
				if (p != null)
				{
					Yaw = Coords.YawToMc(Coords.FrameYaw(p.transform.forward));
					Pitch = Mathf.Clamp(p.cameraUp, -90f, 90f);
				}
			}
			Active = true;
			eyeHeight = (float)ticks.EyeHeight;
			bobPhase = ticks.BobPhase;
			bobAmount = ticks.BobAmount;
			fov = mc.FovDeg;
			cameraMode = mc.CameraMode;
			cameraDistance = mc.CameraDistance;
			if (mc.Sensitivity > 0f)
			{
				sensitivity = mc.Sensitivity;
			}
			OnGround = mc.Has(Proto.McOnGround);
			sneaking = mc.Has(Proto.McSneaking);
			sprinting = mc.Has(Proto.McSprinting);
			FeetLocal = Coords.ToLocal(ticks.FeetX, ticks.FeetY, ticks.FeetZ);
			Feet = Coords.Frame != null ? Coords.Frame.TransformPoint(FeetLocal) : FeetLocal;
			if (haveLastLocal && dt > 0f)
			{
				velocity = Vector3.Lerp(velocity, (FeetLocal - lastLocal) / dt, 1f - Mathf.Exp(-dt / 0.06f));
			}
			lastLocal = FeetLocal;
			haveLastLocal = true;
		}

		/// <summary>Before Lethal Company's Update: its stamina has no say while Minecraft moves the player.</summary>
		public void BeforeUpdate(PlayerControllerB p)
		{
			p.sprintMeter = 1f;
			p.isExhausted = false;
		}

		/// <summary>After Lethal Company's Update: the body goes to Minecraft's feet, and the animator follows Minecraft.</summary>
		public void AfterUpdate(PlayerControllerB p)
		{
			if (!Active || p == null || p.thisController == null)
			{
				return;
			}
			Place(p);
			Present(p);
		}

		private void Place(PlayerControllerB p)
		{
			// Fall damage and fall animations belong to Minecraft's physics now.
			p.takingFallDamage = false;
			p.fallValueUncapped = Mathf.Max(p.fallValueUncapped, -20f);
			var t = p.transform;
			var frame = Coords.Frame;
			// On the ship, in the ship's own space: the ship moves later in the frame than we run, and a
			// world position worked out now would trail it.
			bool local = frame != null && t.parent == frame;
			bool changed = local ? (t.localPosition - FeetLocal).sqrMagnitude > 1e-8f : (t.position - Feet).sqrMagnitude > 1e-8f;
			if (changed)
			{
				// The CharacterController overrides transform writes unless it's off (TeleportPlayer does the same).
				bool enabled = p.thisController.enabled;
				p.thisController.enabled = false;
				if (local)
				{
					t.localPosition = FeetLocal;
				}
				else
				{
					t.position = Feet;
				}
				p.thisController.enabled = enabled;
			}
			Coords.ToMc(t.position, out double x, out double y, out double z);
			LastSetMc = new Vector3((float)x, (float)y, (float)z);
			HaveLastSet = true;
		}

		/// <summary>Walking, sprinting, sneaking and jumping, from Minecraft, onto Lethal Company's animator and state (which syncs them to the others).</summary>
		private void Present(PlayerControllerB p)
		{
			var anim = p.playerBodyAnimator;
			if (anim == null)
			{
				return;
			}
			Vector3 worldVelocity = Coords.Frame != null ? Coords.Frame.TransformDirection(velocity) : velocity;
			Vector3 rel = p.transform.InverseTransformDirection(worldVelocity);
			float speed = new Vector2(rel.x, rel.z).magnitude;
			bool moving = speed > 0.5f;
			bool sprint = sprinting && moving;

			p.isWalking = moving;
			p.isSprinting = sprint;
			p.sprintMeter = 1f;
			p.isExhausted = false;
			anim.SetBool("Walking", moving);
			anim.SetBool("Sprinting", sprint);
			bool sideways = moving && Mathf.Abs(rel.z) < 0.25f * speed;
			anim.SetBool("Sideways", sideways);
			p.isSidling = sideways;
			p.movingForward = rel.z > -0.1f;
			if (!p.inVehicleAnimation)
			{
				anim.SetFloat("animationSpeed", p.movingForward ? 1f : -1f);
			}

			if (sneaking != p.isCrouching)
			{
				p.Crouch(sneaking);
			}

			// A jump: Lethal Company's own jump logic is off, so its animation and RPC (the others see and
			// hear it) are done here. The local sound is Minecraft's.
			if (wasOnGround && !OnGround && velocity.y > 3f)
			{
				anim.SetBool("Jumping", true);
				StartOfRound.Instance.PlayerJumpEvent.Invoke(p);
				if (StartOfRound.Instance.connectedPlayersAmount != 0)
				{
					p.PlayerJumpedServerRpc();
				}
			}
			if (OnGround && !wasOnGround)
			{
				anim.SetBool("Jumping", false);
			}
			wasOnGround = OnGround;
		}

		/// <summary>
		/// In place of the game's PlayerLookInput: Minecraft's mouse look (its sensitivity and formula,
		/// raw mouse counts, pitch to straight up and down), applied to the game's body yaw and camera
		/// pitch, and synced to the others the way the game does it. False: the game's own look runs
		/// (menus, inspecting an item).
		/// </summary>
		public bool Look(PlayerControllerB p)
		{
			if (!Active || p.quickMenuManager.isMenuOpen || p.inSpecialMenu || p.IsInspectingItem || p.disableLookInput || StartOfRound.Instance.newGameIsLoading)
			{
				return false;
			}
			if (Input.InputBridge.ScreenOpen)
			{
				return true; // a Minecraft screen has the mouse: its cursor moves, the view doesn't
			}
			var mouse = Mouse.current;
			Vector2 delta = mouse != null ? mouse.delta.ReadValue() : Vector2.zero;
			float s = sensitivity * 0.6f + 0.2f;
			float factor = s * s * s * 8f * 0.15f;
			Yaw = Mathf.Repeat(Yaw + delta.x * factor, 360f);
			Pitch = Mathf.Clamp(Pitch - delta.y * factor, -90f, 90f);

			var body = Quaternion.Euler(0f, Coords.YawToUnity(Yaw), 0f);
			p.thisPlayerBody.rotation = Coords.Frame != null ? Coords.Frame.rotation * body : body;
			p.cameraUp = Pitch;
			var e = p.gameplayCamera.transform.localEulerAngles;
			p.gameplayCamera.transform.localEulerAngles = new Vector3(Pitch, e.y, e.z);
			StartOfRound.Instance.playerLookMagnitudeThisFrame = delta.magnitude * factor * Time.deltaTime;

			// The game's own rotation sync (PlayerLookInput's tail).
			if (p.IsServer && p.playersManager.connectedPlayersAmount < 1)
			{
				return true;
			}
			if (p.updatePlayerLookInterval > 0.05f)
			{
				p.updatePlayerLookInterval = 0f;
				if (Mathf.Abs(p.oldCameraUp + p.previousYRot - (p.cameraUp + p.thisPlayerBody.eulerAngles.y)) > 1.5f)
				{
					p.UpdatePlayerRotationServerRpc((short)p.cameraUp, (short)p.thisPlayerBody.localEulerAngles.y);
					p.oldCameraUp = p.cameraUp;
					p.previousYRot = p.thisPlayerBody.localEulerAngles.y;
				}
			}
			return true;
		}

		/// <summary>
		/// After the game's LateUpdate (its animation and camera rig have run): the camera is
		/// Minecraft's, at its eye (sneaking lowers it), with its view bob and FOV (sprinting widens it).
		/// </summary>
		public void Camera(PlayerControllerB p)
		{
			var cam = p.gameplayCamera;
			if (!Active || cam == null)
			{
				return;
			}
			float k = Coords.K;
			Vector3 eyeLocal = FeetLocal + Vector3.up * (eyeHeight / k);
			Vector3 eye = Coords.Frame != null ? Coords.Frame.TransformPoint(eyeLocal) : eyeLocal;

			// Minecraft's walk bob (GameRenderer.bobView): sway sideways, lift, and dip and roll the view.
			float phase = bobPhase * Mathf.PI;
			float side = -Mathf.Sin(phase) * bobAmount * 0.5f;
			float lift = Mathf.Abs(Mathf.Cos(phase) * bobAmount);
			float bobPitch = Mathf.Abs(Mathf.Cos(phase - 0.2f) * bobAmount) * 5f;
			float bobRoll = Mathf.Sin(phase) * bobAmount * 3f;

			Quaternion look = Coords.Look(Yaw, Pitch);
			if (cameraMode != 0 && cameraDistance > 0.01f)
			{
				// Third person (F5): behind the head, or in front looking back, at Minecraft's distance.
				bool front = cameraMode == 2;
				Quaternion view = front ? Coords.Look(Yaw + 180f, -Pitch) : look;
				cam.transform.position = eye - view * Vector3.forward * (cameraDistance / k);
				cam.transform.rotation = view;
			}
			else
			{
				cam.transform.position = eye + look * (Vector3.right * side + Vector3.up * lift) / k;
				cam.transform.rotation = look * Quaternion.Euler(bobPitch, 0f, bobRoll);
			}
			if (fov > 1f)
			{
				cam.fieldOfView = fov;
			}
			// The helmet visor HUD follows the camera; the game placed it before we moved the camera.
			if (p.localVisor != null && p.localVisorTargetPoint != null)
			{
				p.localVisor.position = p.localVisorTargetPoint.position;
				p.localVisor.rotation = p.localVisorTargetPoint.rotation;
			}
		}

		/// <summary>Lethal Company put the body somewhere other than where we left it (a teleport, a push): its position wins.</summary>
		public bool MovedByGame(PlayerControllerB p)
		{
			if (!Active || !HaveLastSet || p == null)
			{
				return false;
			}
			Coords.ToMc(p.transform.position, out double x, out double y, out double z);
			var now = new Vector3((float)x, (float)y, (float)z);
			return (now - LastSetMc).magnitude > 1.5f; // blocks
		}
	}
}
