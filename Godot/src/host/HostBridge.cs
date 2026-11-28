using Godot;
using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;
using Refii.Core;
using Refii.Text;
using Refii.Ui;

using static Refii.Ui.Engine_UiModel;

namespace Refii.Host;

/// <summary>
/// P/Invoke over <c>refii_hostbridge</c>, the C ABI that hosts the recompiled guest in
/// Godot's own process: Godot renders, the guest is a simulation server behind a null D3D9 device.
/// </summary>
public static partial class HostBridge
{
	internal const string Library = "refii_hostbridge";

	/// <summary>
	/// ABI this binding expects; bump together with the native `RefiiHostBridgeAbiVersion`.
	/// The check is exact and `GameSession` treats a mismatch as fatal before the guest initialises.
	/// </summary>
	public const int ExpectedAbiVersion = 280;

	[DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
	private static extern uint RefiiHostGetSaveRoot(byte[] output, uint capacity);

	public static string? GetSaveRoot()
	{
		if (TryGetAbiVersion() is null || !IsGuestUp) return null;
		var buffer = new byte[32768];
		return RefiiHostGetSaveRoot(buffer, (uint)buffer.Length) != 0 ? Decode(buffer) : null;
	}

	private const int MessageCapacity = 512;

	static HostBridge() => Engine_NativeLibraries.Install();

	/// <summary>
	/// The bridge's version if it is usable, null otherwise (library missing, wrong ABI, or
	/// <see cref="MarkUnusable"/> latched). Guards use this; <see cref="TryGetRawAbiVersion"/> reports.
	/// </summary>
	public static int? TryGetAbiVersion() =>
		!Unusable &&
		TryGetRawAbiVersion() is { } version && version == ExpectedAbiVersion ? version : null;

	/// <summary>
	/// Why the bridge was given up on, or empty while it is usable. A matching ABI is not a
	/// running guest: a failed boot leaves the version answering correctly, so this latch exists.
	/// </summary>
	public static string UnusableReason { get; private set; } = string.Empty;

	private static bool Unusable => UnusableReason.Length != 0;

	/// <summary>
	/// Gives up on the bridge so every accessor guarded on <see cref="TryGetAbiVersion"/> refuses at
	/// once. Called by <c>GameSession</c> on an abandoned boot; cleared by a successful <see cref="Initialize"/>.
	/// </summary>
	public static void MarkUnusable(string reason)
	{
		if (Unusable)
		{
			return;
		}

		UnusableReason = reason.Length != 0 ? reason : "the session gave up on the guest";
		GD.PrintErr($"[host] the bridge is now REFUSING every read: {UnusableReason}. " +
			"Anything that polls the guest will behave as though there is none, which is the " +
			"honest answer.");
	}

	/// <summary>
	/// The loaded library's ABI version whatever it is, or null when it could not be loaded.
	/// For diagnostics only; guards use <see cref="TryGetAbiVersion"/>.
	/// </summary>
	public static int? TryGetRawAbiVersion()
	{
		// The loaded library cannot change under a running process: the first answer stands.
		// Every guarded accessor asks, thousands of times a frame.
		if (_rawAbiVersion is { } known)
		{
			return known;
		}
		try
		{
			_rawAbiVersion = RefiiHostBridgeAbiVersion();
			return _rawAbiVersion;
		}
		catch (DllNotFoundException)
		{
			return null;
		}
		catch (EntryPointNotFoundException)
		{
			return null;
		}
	}

	private static int? _rawAbiVersion;

	/// <summary>
	/// Hands one error Godot reported to the bridge, which writes the last of them into a crash
	/// record. Any thread; a no-op while the bridge is missing or of another ABI.
	/// </summary>
	public static void NoteEngineError(string text)
	{
		if (TryGetRawAbiVersion() != ExpectedAbiVersion)
		{
			return;
		}

		RefiiHostNoteEngineError(text);
	}

	/// <summary>
	/// States Godot's own condition to the bridge in one line, replacing the last; a crash
	/// record carries it. A no-op while the bridge is missing or of another ABI.
	/// </summary>
	public static void NoteEngineState(string text)
	{
		if (TryGetRawAbiVersion() != ExpectedAbiVersion)
		{
			return;
		}

		RefiiHostNoteEngineState(text);
	}

	/// <summary>Outcome of <see cref="ProbeGuestMemory"/>.</summary>
	public readonly struct ProbeResult
	{
		public readonly bool Success;
		public readonly string Message;

		public ProbeResult(bool success, string message)
		{
			Success = success;
			Message = message;
		}
	}

	/// <summary>
	/// Reserves the guest address space inside this process, reports where it landed, and releases it.
	/// </summary>
	public static ProbeResult ProbeGuestMemory()
	{
		var message = new byte[MessageCapacity];

		try
		{
			int ok = RefiiHostProbeGuestMemory(message, (nuint)message.Length);
			return new ProbeResult(ok == 1, Decode(message));
		}
		catch (DllNotFoundException)
		{
			return new ProbeResult(false,
				$"{Engine_NativeLibraries.FileNameFor(Library)} not found. Build it with " +
				"REFII_BUILD_HOST_BRIDGE=ON, or set REFII_NATIVE_DIR to its output directory.");
		}
		catch (EntryPointNotFoundException exception)
		{
			return new ProbeResult(false, $"stale host bridge: {exception.Message}");
		}
	}

	/// <summary>
	/// Boots the guest on its own thread in this process and returns immediately. True means the
	/// thread started, not that the guest is up; poll <see cref="IsRunning"/> and <see cref="Status"/>.
	/// One boot per process.
	/// </summary>
	/// <param name="arguments">Passed to the guest, joined with newlines (an argument may contain spaces).</param>
	public static ProbeResult Start(string gameDirectory, params string[] arguments)
	{
		var message = new byte[MessageCapacity];
		string packed = arguments is { Length: > 0 } ? string.Join('\n', arguments) : string.Empty;

		try
		{
			int ok = RefiiHostStart(gameDirectory, packed, message, (nuint)message.Length);
			return new ProbeResult(ok == 1, Decode(message));
		}
		catch (DllNotFoundException)
		{
			return new ProbeResult(false,
				$"{Engine_NativeLibraries.FileNameFor(Library)} not found. Build it with " +
				"REFII_BUILD_HOST_BRIDGE=ON, or set REFII_NATIVE_DIR to its output directory.");
		}
		catch (EntryPointNotFoundException exception)
		{
			return new ProbeResult(false, $"stale host bridge: {exception.Message}");
		}
	}

	/// <summary>
	/// True while the guest thread is alive — the <see cref="Start"/> path only; false throughout a
	/// normal session. Accessors use <see cref="IsGuestUp"/>.
	/// </summary>
	public static bool IsRunning => RefiiHostIsRunning() == 1;

	/// <summary>
	/// True while a guest is up by either boot path; the test every guest-memory accessor uses.
	/// Falls back to <see cref="IsRunning"/> against an older native library.
	/// </summary>
	public static bool IsGuestUp
	{
		get
		{
			// Probed once: this guards per-entity accessors that run thousands of times a frame,
			// and against an older library the P/Invoke throws.
			if (_hasGuestUpProbe is null)
			{
				try
				{
					_ = RefiiHostIsGuestUp();
					_hasGuestUpProbe = true;
				}
				catch (EntryPointNotFoundException)
				{
					_hasGuestUpProbe = false;
				}
			}

			// Once a frame: thousands of guarded accessors ask, and the answer moves only when the
			// guest starts or stops.
			long frame = Refii.Core.Engine_FramePhases.FrameIndex;
			if (frame == _guestUpFrame && _guestUp)
			{
				return _guestUp;
			}
			_guestUp = _hasGuestUpProbe == true ? RefiiHostIsGuestUp() == 1 : IsRunning;
			_guestUpFrame = frame;
			return _guestUp;
		}
	}

	private static long _guestUpFrame = -1;
	private static bool _guestUp;

	/// <summary>Whether the native library exports the liveness probe. Asked once — see above.</summary>
	private static bool? _hasGuestUpProbe;

	/// <summary>The guest's exit code, or -1 while running or never started.</summary>
	public static int ExitCode => RefiiHostExitCode();

	/// <summary>A human-readable status line from the bridge.</summary>
	public static string Status
	{
		get
		{
			var message = new byte[MessageCapacity];
			RefiiHostStatus(message, (nuint)message.Length);
			return Decode(message);
		}
	}

	/// <summary>Joins the guest thread once it has finished. False while still running.</summary>
	public static ProbeResult Join()
	{
		var message = new byte[MessageCapacity];
		int ok = RefiiHostJoin(message, (nuint)message.Length);
		return new ProbeResult(ok == 1, Decode(message));
	}

	/// <summary>
	/// Initialises the guest on this thread and returns once it is up; Godot then drives it with
	/// <see cref="Step"/>. Synchronous, takes seconds, must run on the main thread (SDL).
	/// </summary>
	public static ProbeResult Initialize(string gameDirectory, params string[] arguments)
	{
		var message = new byte[MessageCapacity];

		// THE SAVES, stated: the active profile's folder is the guest's user_data_root, and the
		// content manager is told each title's folder in it (Refii.Save.Engine_SaveLayout), so both
		// titles share one identity and Fable 3 enumerates Fable 2's saves as its alternate title.
		// The cache is the app's own container, never under the profile. A command-line value
		// outranks the config, so the guest writes there whatever its file says.
		var stated = new List<string>(arguments ?? Array.Empty<string>())
		{
			"--user_data_root=" + Refii.Core.Engine_AppRoot.SaveDirectory,
			"--content_title_folders=" + Refii.Save.Engine_SaveLayout.TitleFoldersArgument(),
			"--cache_root=" + Refii.Core.Engine_AppRoot.CacheDirectory,
		};
		string packed = string.Join('\n', stated);

		try
		{
			// Before the guest comes up: paths are resolved once during its startup, and the
			// guest would otherwise read a config beside the Godot binary. The config is the
			// running title's own (Engine_Title.ConfigFileName), never another title's.
			RefiiHostSetAppRoot(Refii.Core.Engine_AppRoot.Path);
			RefiiHostSetConfigPath(Refii.Core.Engine_AppRoot.ConfigPath);
			RefiiHostSetLogDirectory(Refii.Core.Engine_Title.Active.LogsDirectory);

			int ok = RefiiHostInitialize(gameDirectory, packed, message, (nuint)message.Length);
			if (ok == 1)
			{
				// Cleared on success only; a second failed boot must not un-refuse the first.
				UnusableReason = string.Empty;
			}

			return new ProbeResult(ok == 1, Decode(message));
		}
		catch (DllNotFoundException)
		{
			return new ProbeResult(false,
				$"{Engine_NativeLibraries.FileNameFor(Library)} not found. Build it with " +
				"REFII_BUILD_HOST_BRIDGE=ON, or set REFII_NATIVE_DIR to its output directory.");
		}
		catch (EntryPointNotFoundException exception)
		{
			return new ProbeResult(false, $"stale host bridge: {exception.Message}");
		}
	}

	/// <summary>
	/// Advances the guest by one frame. False once it has stopped.
	/// Call from <c>_Process</c>.
	/// </summary>
	public static bool Step() => RefiiHostStep() == 1;

	/// <summary>Status of the hosted guest.</summary>
	public static string HostedStatus
	{
		get
		{
			var message = new byte[MessageCapacity];
			RefiiHostHostedStatus(message, (nuint)message.Length);
			return Decode(message);
		}
	}

	/// <summary>
	/// Tears the guest down. Idempotent; call from <c>_ExitTree</c> so guest threads are not live at exit.
	/// </summary>
	/// <summary>One clip leaf of the guest's stated blend tree for a rig.</summary>
	/// <param name="Key">The animation TOC key (the guest's clip hash).</param>
	/// <param name="Time">The guest's current clip time, seconds, as of its last tick.</param>
	/// <param name="PreviousTime">The clip time at the tick before.</param>
	/// <param name="Weight">The product of the weights down the tree.</param>
	/// <param name="Overlay">
	/// From the controller's second list: composited per bone through <paramref name="Mask"/>
	/// onto the primaries' pose, never a primary itself.
	/// </param>
	/// <param name="Mask">The guest mask node above this leaf (0 = whole body); see GetAnimMask.</param>
	/// <param name="PriorityMask">The record's priority bitmask; an overlay plays only when it ANDs with the top primary's priority.</param>
	/// <remarks>
	/// <paramref name="Weight"/> is this leaf's share of
	/// its record, already scaled by the blend tree above it; <paramref name="RecordPrevious"/>
	/// and <paramref name="RecordCurrent"/> are the record's OWN weight pair, unscaled, latched
	/// at the tick start and live respectively — the same prev/cur contract as
	/// <paramref name="PreviousTime"/> and <paramref name="Time"/> beside them. The guest's
	/// crossfade factor is `(1 - alpha) * RecordPrevious + alpha * RecordCurrent`
	/// (`sub_82211660` 0x82211B08..0x82211B24, reading 0x18 and 0x14 off the winner record).
	/// </remarks>
	public readonly record struct GuestAnimLeaf(
		uint Key, float Time, float PreviousTime, float Weight, bool Loop, bool Additive,
		int Priority, int Track, uint Uid, float BlendIn, float BlendOut,
		bool Overlay, uint Mask, uint PriorityMask,
		float RecordPrevious, float RecordCurrent, bool Network = false,
		bool IsWinner = false, bool IsRunner = false);

	private const int AnimLeafWords = 12;

	/// <summary>
	/// One overlay's per-bone weights, 0..255 by LIVE (guest) bone index, from the mask node
	/// the leaf names. Empty when the node is not a mask.
	/// </summary>
	/// <summary>One item an entity wears: its GDB record hash, and whether the wearer is female
	/// and whether the appearance component's open byte is set (the open-coat variants).</summary>
	public readonly record struct GuestWornItem(uint Hash, bool Female, bool Open);

	/// <summary>
	/// The items the entity wears, from its appearance-modifier component. Empty when the
	/// entity wears nothing or carries no such component.
	/// </summary>
	public static List<GuestWornItem> GetEntityWornItems(uint entity)
	{
		var result = new List<GuestWornItem>();
		if (entity == 0 || TryGetAbiVersion() is null || !IsGuestUp)
		{
			return result;
		}

		var words = new uint[128];
		uint count;
		try
		{
			count = RefiiHostGetEntityWornItems(entity, words, (uint)words.Length);
		}
		catch (EntryPointNotFoundException)
		{
			return result;
		}

		for (uint i = 0; i < count && 2 * i + 1 < words.Length; i++)
		{
			uint flags = words[2 * i + 1];
			result.Add(new GuestWornItem(words[2 * i], (flags & 1) != 0, (flags & 2) != 0));
		}

		return result;
	}

	/// <summary>
	/// One post-fold aim or look op: the record it belongs to, which chain (0 aim, 1 look),
	/// the blend mode and weight, the record's weight, and one target rotation per chain bone.
	/// </summary>
	public readonly record struct GuestPostOp(uint RecordUid, int Kind, int Mode, float Weight,
		float RecordWeight, Quaternion[] Targets);

	/// <summary>The skeleton's aim and look chains plus every op the playing records carry.</summary>
	public readonly record struct GuestPostOps(bool AimOn, bool LookOn, int[] AimBones, int[] LookBones,
		List<GuestPostOp> Ops);

	/// <summary>
	/// The guest's post-fold aim and look records for the entity (`sub_822DC980` tail): per
	/// chain bone, a target rotation the folded pose is blended toward. Null without a skeleton.
	/// </summary>
	private static uint[]? _postOpWords;

	public static GuestPostOps? GetAnimPostOps(uint entity)
	{
		if (entity == 0 || TryGetAbiVersion() is null || !IsGuestUp)
		{
			return null;
		}

		uint[] words = _postOpWords ??= new uint[13 + 64 * 29];
		System.Array.Clear(words, 0, 13);
		uint count;
		try
		{
			count = RefiiHostGetAnimPostOps(entity, words, (uint)words.Length);
		}
		catch (EntryPointNotFoundException)
		{
			return null;
		}

		if (words[0] == 0 && words[1] == 0 && words[2] == 0 && words[7] == 0)
		{
			return null;
		}

		// One set of buffers, refilled per call: the result is read at once by its caller.
		int[] aim = _postAim;
		int[] look = _postLook;
		for (int i = 0; i < 5; i++)
		{
			aim[i] = (int)words[2 + i];
		}

		for (int i = 0; i < 6; i++)
		{
			look[i] = (int)words[7 + i];
		}

		List<GuestPostOp> ops = _postOps;
		ops.Clear();
		int at = 13;
		for (uint i = 0; i < count; i++)
		{
			if (at + 5 > words.Length)
			{
				break;
			}

			int kind = (int)words[at + 1];
			int bones = kind == 0 ? 5 : 6;
			if (at + 5 + 4 * bones > words.Length)
			{
				break;
			}

			Quaternion[] targets = ops.Count < _postTargets.Length
				? (_postTargets[ops.Count] ??= new Quaternion[6])
				: new Quaternion[6];
			for (int b = 0; b < bones; b++)
			{
				int q = at + 5 + 4 * b;
				targets[b] = new Quaternion(
					BitConverter.Int32BitsToSingle((int)words[q]),
					BitConverter.Int32BitsToSingle((int)words[q + 1]),
					BitConverter.Int32BitsToSingle((int)words[q + 2]),
					BitConverter.Int32BitsToSingle((int)words[q + 3]));
			}

			ops.Add(new GuestPostOp(words[at], kind, (int)words[at + 2],
				BitConverter.Int32BitsToSingle((int)words[at + 3]),
				BitConverter.Int32BitsToSingle((int)words[at + 4]), targets));
			at += 5 + 4 * bones;
		}

		return new GuestPostOps(words[0] != 0, words[1] != 0, aim, look, ops);
	}

	private static readonly int[] _postAim = new int[5];
	private static readonly int[] _postLook = new int[6];
	private static readonly List<GuestPostOp> _postOps = new();
	private static readonly Quaternion[]?[] _postTargets = new Quaternion[]?[64];

	/// <summary>
	/// An entity's stated draw distance D and fade factor, off its renderable (component 3): the
	/// definition's `MaxDrawDistance` times the guest's live class multiplier, and
	/// `DrawDistanceFadeRangeFactor`. The guest fades the instance out over the last factor of D
	/// and cuts its shadow hard at D. False when the entity carries no renderable.
	/// </summary>
	public static bool GetEntityDrawRange(uint entity, out float distance, out float fadeFactor)
	{
		distance = 0f;
		fadeFactor = 0f;
		if (entity == 0 || TryGetAbiVersion() is null || !IsGuestUp)
		{
			return false;
		}

		try
		{
			return RefiiHostGetEntityDrawRange(entity, out distance, out fadeFactor) != 0;
		}
		catch (EntryPointNotFoundException)
		{
			return false;
		}
	}

	/// <summary>
	/// An entity's stated cloth distance, off its renderable (component 3): the definition's
	/// `MaxClothDistance`, which the guest's twin carries at +0x128 and its cloth gate steps
	/// within. False when the entity carries no renderable or the title does not state it.
	/// </summary>
	public static bool GetEntityClothDistance(uint entity, out float distance)
	{
		distance = 0f;
		if (entity == 0 || TryGetAbiVersion() is null || !IsGuestUp)
		{
			return false;
		}

		try
		{
			return RefiiHostGetEntityClothDistance(entity, out distance) != 0;
		}
		catch (EntryPointNotFoundException)
		{
			return false;
		}
	}

	private static bool _lightZoneMissing;
	private static bool _casterFlagsMissing;

	/// <summary>
	/// An entity's stated sun-shadow casting: its renderable's flag word (0x40 casts nothing, 0x80
	/// no distant cascade) with 0x100 for a caster drawn with culling off. Null when the entity has
	/// no renderable or the bridge does not export it.
	/// </summary>
	public static uint? GetEntityCasterFlags(uint entity)
	{
		if (entity == 0 || _casterFlagsMissing || TryGetAbiVersion() is null || !IsGuestUp)
		{
			return null;
		}

		try
		{
			uint flags = RefiiHostGetEntityCasterFlags(entity);
			return flags == 0xFFFFFFFFu ? null : flags;
		}
		catch (EntryPointNotFoundException)
		{
			_casterFlagsMissing = true;
			return null;
		}
	}

	/// <summary>
	/// The lighting zone an entity's geometry is drawn in (a building's own geometry: its
	/// definition hash; anything else: its renderable's zone). 1, outdoors, when the guest is not
	/// up or the native library predates the call.
	/// </summary>
	public static uint GetEntityLightZone(uint entity)
	{
		if (entity == 0 || _lightZoneMissing || TryGetAbiVersion() is null || !IsGuestUp)
		{
			return 1u;
		}

		try
		{
			return RefiiHostGetEntityLightZone(entity);
		}
		catch (EntryPointNotFoundException)
		{
			_lightZoneMissing = true;
			return 1u;
		}
	}

	/// <summary>
	/// The entity's OWN lighting zone (RefiiHostGetEntityOwnLightZone): its renderable's zone
	/// word with no static-mesh building override — what a light hosted on the entity lights
	/// from. Falls back to <see cref="GetEntityLightZone"/> on a native library that predates
	/// the call.
	/// </summary>
	public static uint GetEntityOwnLightZone(uint entity)
	{
		if (entity == 0 || TryGetAbiVersion() is null || !IsGuestUp)
		{
			return 1u;
		}

		if (_ownLightZoneMissing)
		{
			return GetEntityLightZone(entity);
		}

		try
		{
			return RefiiHostGetEntityOwnLightZone(entity);
		}
		catch (EntryPointNotFoundException)
		{
			_ownLightZoneMissing = true;
			return GetEntityLightZone(entity);
		}
	}

	private static bool _ownLightZoneMissing;

	/// <summary>
	/// Whether the guest poses this entity's rig on its current tick (RefiiHostGetEntityPoseGate):
	/// true posed, false held, null when the title does not state it (Fable 2, no world, a native
	/// library that predates the call) and the shell's own view test decides.
	/// </summary>
	public static bool? GetEntityPoseGate(uint entity)
	{
		if (entity == 0 || _poseGateMissing || TryGetAbiVersion() is null || !IsGuestUp)
		{
			return null;
		}

		try
		{
			return RefiiHostGetEntityPoseGate(entity) switch
			{
				0u => false,
				1u => true,
				_ => null,
			};
		}
		catch (EntryPointNotFoundException)
		{
			_poseGateMissing = true;
			return null;
		}
	}

	private static bool _poseGateMissing;

	/// <summary>
	/// One cloth piece the guest states for an entity (RefiiHostGetEntityClothPieces): its key for
	/// <see cref="GetClothPieceVertices"/>, the model hash of the part it belongs to, its proxy's
	/// slot in that model's rigid list or, <see cref="Skinned"/>, skinned list, whether the guest
	/// drew it as cloth in its current or last render frame (otherwise the record draws its own
	/// skinned submesh), its vertex count, a sequence that moves with every publish, and how many
	/// poses are kept (<see cref="GetClothPieceVertices"/>' indices, newest first).
	/// </summary>
	public readonly record struct GuestClothPiece(
		uint Key, uint Model, int Slot, bool Skinned, bool Drawn, int VertexCount, uint Sequence,
		int PoseCount);

	/// <summary>Words per piece in RefiiHostGetEntityClothPieces' rows.</summary>
	public const int ClothPieceWords = 8;

	/// <summary>Floats per vertex in RefiiHostGetClothPieceVertices: position, normal, tangent.</summary>
	public const int ClothVertexFloats = 12;

	private const uint ClothNotStated = 0xFFFFFFFFu;

	private static bool? _clothStated;

	private static uint[] _clothWords = new uint[32 * ClothPieceWords];

	/// <summary>
	/// Whether the title's bridge states its guest's cloth (Fable 3) or leaves it to the shell's own
	/// solver (Fable 2 answers "not stated"). Asked once; a bridge that cannot be asked, or that
	/// predates the call, states nothing.
	/// </summary>
	public static bool ClothStated
	{
		get
		{
			if (_clothStated is { } known)
			{
				return known;
			}

			if (TryGetAbiVersion() is null)
			{
				return false;
			}

			try
			{
				_clothStated = RefiiHostGetEntityClothPieces(0u, _clothWords, 1u) != ClothNotStated;
			}
			catch (EntryPointNotFoundException)
			{
				_clothStated = false;
			}

			return _clothStated.Value;
		}
	}

	/// <summary>
	/// The cloth pieces the guest states for <paramref name="entity"/>, written into
	/// <paramref name="into"/>; returns how many. 0 when the title states none.
	/// </summary>
	public static int GetEntityClothPieces(uint entity, GuestClothPiece[] into)
	{
		if (entity == 0 || into.Length == 0 || !ClothStated || !IsGuestUp)
		{
			return 0;
		}

		if (_clothWords.Length < into.Length * ClothPieceWords)
		{
			_clothWords = new uint[into.Length * ClothPieceWords];
		}

		uint rows = RefiiHostGetEntityClothPieces(entity, _clothWords, (uint)into.Length);
		return rows == ClothNotStated ? 0 : DecodeClothPieces(_clothWords, (int)rows, into);
	}

	/// <summary>RefiiHostGetEntityClothPieces' rows, decoded. Pure: the unit tests drive it.</summary>
	public static int DecodeClothPieces(ReadOnlySpan<uint> words, int rows, Span<GuestClothPiece> into)
	{
		int count = Math.Min(Math.Min(rows, into.Length), words.Length / ClothPieceWords);
		for (int i = 0; i < count; i++)
		{
			ReadOnlySpan<uint> row = words.Slice(i * ClothPieceWords, ClothPieceWords);
			into[i] = new GuestClothPiece(
				Key: row[0],
				Model: row[1],
				Slot: (int)row[2],
				Skinned: (row[3] & 1u) != 0u,
				Drawn: (row[3] & 2u) != 0u,
				VertexCount: (int)row[4],
				Sequence: row[5],
				PoseCount: (int)row[6]);
		}

		return count;
	}

	/// <summary>
	/// One stated piece's vertices, <see cref="ClothVertexFloats"/> floats each, in the model's own
	/// space (the file's Z-up): the pose at <paramref name="index"/> (0 the newest), with the guest
	/// frame, fractional, of the instant the guest posed it for
	/// (<see cref="Refii.Core.Engine_TickClock.StampOfGuestFrame"/> puts it on the draw timeline).
	/// Returns the vertex count, 0 when the piece or pose is not there.
	/// </summary>
	public static int GetClothPieceVertices(uint key, int index, float[] into, out double frame)
	{
		frame = 0.0;
		if (key == 0 || index < 0 || !ClothStated || !IsGuestUp)
		{
			return 0;
		}

		return (int)RefiiHostGetClothPieceVertices(
			key, (uint)index, into, (uint)(into.Length / ClothVertexFloats), out frame);
	}

	/// <summary>
	/// The sphere around the guest's published pose of this entity's rig
	/// (RefiiHostGetEntityViewSphere): centre in the rig's model space, radius in metres, the model
	/// origin always inside. Null when the title states none (Fable 3, whose pose gate is stated
	/// instead), the rig has no published pose, or the native library predates the call.
	/// </summary>
	public static (Vector3 Centre, float Radius)? GetEntityViewSphere(uint entity)
	{
		if (entity == 0 || _viewSphereMissing || TryGetAbiVersion() is null || !IsGuestUp)
		{
			return null;
		}

		try
		{
			return RefiiHostGetEntityViewSphere(entity, _viewSphere) == 1u
				? (new Vector3(_viewSphere[0], _viewSphere[1], _viewSphere[2]), _viewSphere[3])
				: null;
		}
		catch (EntryPointNotFoundException)
		{
			_viewSphereMissing = true;
			return null;
		}
	}

	private static bool _viewSphereMissing;
	private static readonly float[] _viewSphere = new float[4];

	/// <summary>One built part of a composite character: its model hash, its cluster sort
	/// layer, whether it is a body part, and the cluster names it covers.</summary>
	public readonly record struct GuestPartHides(uint Hash, int Layer, bool Body, string[] Covers);

	/// <summary>
	/// The guest's per-part cluster sets and sort layers for a composite entity (component
	/// 17's built vector). A part hides the union of the sets of every other part with a
	/// strictly greater layer. Empty for a single-model entity.
	/// </summary>
	private static byte[]? _partHidesText;

	public static List<GuestPartHides> GetEntityPartHides(uint entity) =>
		ParsePartHides(GetEntityPartHidesText(entity));

	/// <summary>
	/// The guest's part-hides text for <paramref name="entity"/>, one line per part, in a buffer
	/// the next call overwrites; empty when there is none. A caller that keeps what it parsed can
	/// compare this first and skip the parse while it is unchanged.
	/// </summary>
	public static ReadOnlySpan<byte> GetEntityPartHidesText(uint entity)
	{
		if (entity == 0 || TryGetAbiVersion() is null || !IsGuestUp)
		{
			return ReadOnlySpan<byte>.Empty;
		}

		byte[] text = _partHidesText ??= new byte[8192];
		// Only the first byte, not the 8 KB: the bridge terminates its text at [0] before anything
		// else and again after every line it puts (both titles' RefiiHostGetEntityPartHides), so
		// the first zero below is where this call's text ends whatever an earlier call left past it.
		text[0] = 0;
		uint count;
		try
		{
			count = RefiiHostGetEntityPartHides(entity, text, (uint)text.Length);
		}
		catch (EntryPointNotFoundException)
		{
			return ReadOnlySpan<byte>.Empty;
		}

		if (count == 0)
		{
			return ReadOnlySpan<byte>.Empty;
		}

		int end = System.Array.IndexOf(text, (byte)0);
		return new ReadOnlySpan<byte>(text, 0, end < 0 ? text.Length : end);
	}

	/// <summary>Parses <see cref="GetEntityPartHidesText"/>'s lines.</summary>
	public static List<GuestPartHides> ParsePartHides(ReadOnlySpan<byte> text)
	{
		var result = new List<GuestPartHides>();
		if (text.IsEmpty)
		{
			return result;
		}

		string all = System.Text.Encoding.ASCII.GetString(text);
		foreach (string line in all.Split('\n', StringSplitOptions.RemoveEmptyEntries))
		{
			string[] fields = line.Split(' ');
			if (fields.Length < 3 ||
				!uint.TryParse(fields[0], System.Globalization.NumberStyles.HexNumber, null, out uint hash) ||
				!int.TryParse(fields[1], out int layer))
			{
				continue;
			}

			string[] covers = fields.Length >= 4 && fields[3].Length != 0
				? fields[3].Split(';', StringSplitOptions.RemoveEmptyEntries)
				: System.Array.Empty<string>();
			result.Add(new GuestPartHides(hash, layer, fields[2] == "1", covers));
		}

		return result;
	}

	/// <summary>
	/// The morph record's height factor, 0 when there is none: above 0 the guest scales bone 1's
	/// horizontal translation by it and adds only the offset's vertical lane.
	/// </summary>
	public static float GetBoneMorphHeight(uint entity)
	{
		if (entity == 0 || TryGetAbiVersion() is null || !IsGuestUp)
		{
			return 0f;
		}

		try
		{
			float h = BitConverter.Int32BitsToSingle((int)RefiiHostGetBoneMorphHeight(entity));
			return float.IsFinite(h) ? h : 0f;
		}
		catch (EntryPointNotFoundException)
		{
			return 0f;
		}
	}

	/// <summary>One part's morph targets: the models the guest blends into it and their weights.</summary>
	public readonly record struct GuestPartMorph(uint BaseModel, (uint Model, float Weight)[] Targets);

	private static readonly uint[] _partMorphWords = new uint[24 * (2 + 16 * 2)];

	/// <summary>Run total of part submissions the hook has seen; 0 means it never ran.</summary>
	public static ulong PartMorphStatements { get; private set; }

	/// <summary>Of those, the submissions that listed at least one morph target.</summary>
	public static ulong PartMorphsMorphed { get; private set; }

	/// <summary>
	/// The entity's morphed parts as the guest stated them to <c>sub_82A48B90</c>; a part not
	/// listed draws as its own model. <paramref name="generation"/> moves when the statement did.
	/// </summary>
	public static GuestPartMorph[] GetEntityPartMorphs(uint entity, out uint generation,
		uint? knownGeneration = null, GuestPartMorph[]? knownParts = null)
	{
		generation = 0;
		if (entity == 0 || TryGetAbiVersion() is null || !IsGuestUp)
		{
			return System.Array.Empty<GuestPartMorph>();
		}

		uint[] words = _partMorphWords;
		uint written;
		try
		{
			written = RefiiHostGetEntityPartMorphs(
				entity, words, (uint)words.Length, out generation, out ulong statements);
			PartMorphStatements = statements & 0xFFFFFFFFUL;
			PartMorphsMorphed = statements >> 32;
		}
		catch (EntryPointNotFoundException)
		{
			return System.Array.Empty<GuestPartMorph>();
		}

		if (knownParts is not null && knownGeneration == generation)
		{
			return knownParts;
		}

		if (written == 0)
		{
			return System.Array.Empty<GuestPartMorph>();
		}

		var parts = new List<GuestPartMorph>();
		int at = 0;
		while (at + 2 <= written)
		{
			uint model = words[at];
			int count = (int)words[at + 1];
			at += 2;
			if (count <= 0 || at + count * 2 > written)
			{
				break;
			}

			var targets = new (uint, float)[count];
			for (int i = 0; i < count; i++)
			{
				float weight = BitConverter.Int32BitsToSingle((int)words[at + i * 2 + 1]);
				targets[i] = (words[at + i * 2], float.IsFinite(weight) ? weight : 0f);
			}

			at += count * 2;
			parts.Add(new GuestPartMorph(model, targets));
		}

		return parts.ToArray();
	}

	/// <summary>
	/// One layer of a texture composite. Mode: 0 opaque, 1 alpha, 2 additive, 3 blend-factor,
	/// 4 masked. Tint is the guest's bytes B, G, R, A in memory order. An overlay entry (a scar)
	/// carries its own specular and bump maps beside the diffuse in <see cref="Texture"/>.
	/// </summary>
	public readonly record struct GuestTextureLayer(
		uint Texture, int Mode, float Weight, bool HasTint, uint Tint, float Brightness, uint Bump = 0,
		uint Spec = 0);

	/// <summary>
	/// An original texture and the layers the guest composites in its place, base first; or,
	/// with <paramref name="Overlay"/>, the scars and mode-0 morphs it draws over the finished
	/// material whose diffuse is that texture; or, with <paramref name="Wildcard"/>, a swap of
	/// EVERY map of one kind on the entity (`sub_825B7100`: the shadow creature's Spire Rock) —
	/// Original 0 diffuse, 1 specular, 2 normal, one layer.
	/// </summary>
	public readonly record struct GuestTextureComposite(
		uint Original, GuestTextureLayer[] Layers, bool Overlay = false, bool Wildcard = false);

	private const int TextureLayerWords = 7;

	internal static readonly uint[] _textureLayerWords = new uint[40 * (2 + 16 * TextureLayerWords)];

	/// <summary>Run total of composites the hook has seen stated; 0 means it never ran.</summary>
	public static ulong TextureLayerStatements { get; private set; }

	/// <summary>
	/// Every texture composite the guest states for the entity (<c>sub_825B38B0</c>).
	/// <paramref name="generation"/> moves when the statement did.
	/// </summary>
	public static GuestTextureComposite[] GetEntityTextureLayers(uint entity, out uint generation)
	{
		generation = 0;
		if (entity == 0 || TryGetAbiVersion() is null || !IsGuestUp)
		{
			return System.Array.Empty<GuestTextureComposite>();
		}

		uint[] words = _textureLayerWords;
		uint written;
		try
		{
			written = RefiiHostGetEntityTextureLayers(
				entity, words, (uint)words.Length, out generation, out ulong statements);
			TextureLayerStatements = statements;
		}
		catch (EntryPointNotFoundException)
		{
			return System.Array.Empty<GuestTextureComposite>();
		}

		// The generation moves when the statement did, so the same generation at the same
		// length is the same statement: the composites decoded for it last time are returned,
		// not decoded again — the entity reconcile asks every few ticks and held the answer
		// only after comparing lengths, so each ask was a fresh array per composite per entity.
		if (_entityTextureLayers.TryGetValue(entity, out (uint Generation, uint Written, GuestTextureComposite[] Composites) held) &&
			held.Generation == generation && held.Written == written)
		{
			return held.Composites;
		}

		GuestTextureComposite[] decoded = DecodeTextureLayers(words, written);
		_entityTextureLayers[entity] = (generation, written, decoded);
		return decoded;
	}

	private static readonly Dictionary<uint, (uint Generation, uint Written, GuestTextureComposite[] Composites)>
		_entityTextureLayers = new();

	/// <summary>
	/// Row kind 3, Fable 3's dye (`sub_827771B0`: command +4 original, +8 "ColourMask" texture,
	/// +12..15 colour 1, +16 strength 1, +20..23 colour 2, +24 strength 2). The hook stages it as
	/// two rows {mask, 0, strength, tinted, colour, 0, 0}; the decoder folds them into the
	/// original's composite as masked layers (mode 4: coverage = the mask's alpha x strength) whose
	/// colour is the dye. The colour word is the component's four bytes B, G, R, A in memory order
	/// (`sub_82632420` stores r8/r7/r6 at +0/+1/+2 and 0xFF at +3, 0x82632518..2C; the engine's
	/// colour serialiser `sub_82D343E0` writes Blue +0, Green +1, Red +2) — the <see cref="GuestTextureLayer.Tint"/>
	/// order already.
	/// </summary>
	private const uint ColourMaskKind = 3u;

	private const int ColourMaskMode = 4;

	/// <summary>
	/// [original, count | kind &lt;&lt; 16, count x layer words] per texture, as the tables write
	/// them. Kinds: 0 composite, 1 overlay, 2 wildcard swap, 3 colour mask (folded, see
	/// <see cref="ColourMaskKind"/>).
	/// </summary>
	internal static GuestTextureComposite[] DecodeTextureLayers(uint[] words, uint written)
	{
		if (written == 0)
		{
			return System.Array.Empty<GuestTextureComposite>();
		}

		// Counted first, allocated once: a list grown and then copied was two arrays per decode.
		int total = 0;
		int colourMasks = 0;
		for (int at = 0; at + 2 <= written;)
		{
			int count = (int)(words[at + 1] & 0xFFFF);
			uint kind = words[at + 1] >> 16;
			at += 2;
			if (count <= 0 || at + count * TextureLayerWords > written)
			{
				break;
			}

			at += count * TextureLayerWords;
			if (kind == ColourMaskKind)
			{
				++colourMasks;
			}
			else
			{
				++total;
			}
		}

		if (total == 0 && colourMasks == 0)
		{
			return System.Array.Empty<GuestTextureComposite>();
		}

		var composites = new GuestTextureComposite[total];
		List<(uint Original, GuestTextureLayer[] Masks)>? masks =
			colourMasks == 0 ? null : new List<(uint, GuestTextureLayer[])>(colourMasks);
		int filled = 0;
		for (int at = 0; filled + (masks?.Count ?? 0) < total + colourMasks;)
		{
			uint original = words[at];
			int count = (int)(words[at + 1] & 0xFFFF);
			uint kind = words[at + 1] >> 16;
			at += 2;

			var layers = new GuestTextureLayer[count];
			for (int i = 0; i < count; i++)
			{
				int w = at + i * TextureLayerWords;
				layers[i] = kind == 1u
					? new GuestTextureLayer(
						words[w], (int)words[w + 1], BitConverter.Int32BitsToSingle((int)words[w + 2]),
						false, 0u, 0f, words[w + 6], words[w + 4])
					: new GuestTextureLayer(
						words[w],
						kind == ColourMaskKind ? ColourMaskMode : (int)words[w + 1],
						BitConverter.Int32BitsToSingle((int)words[w + 2]),
						words[w + 3] != 0,
						words[w + 4],
						BitConverter.Int32BitsToSingle((int)words[w + 5]),
						words[w + 6]);
			}

			at += count * TextureLayerWords;
			if (kind == ColourMaskKind)
			{
				masks!.Add((original, layers));
			}
			else
			{
				composites[filled++] = new GuestTextureComposite(original, layers, kind == 1u, kind == 2u);
			}
		}

		return masks is null ? composites : FoldColourMasks(composites, masks);
	}

	/// <summary>
	/// Each colour mask goes over the composite that replaces its original (the guest sorts both
	/// commands under that original, `sub_8277EC88`), else over the original itself as a plain base.
	/// </summary>
	private static GuestTextureComposite[] FoldColourMasks(
		GuestTextureComposite[] composites, List<(uint Original, GuestTextureLayer[] Masks)> masks)
	{
		var folded = new List<GuestTextureComposite>(composites);
		foreach ((uint original, GuestTextureLayer[] maskLayers) in masks)
		{
			int target = -1;
			for (int i = 0; i < folded.Count; i++)
			{
				if (folded[i].Original == original && !folded[i].Overlay && !folded[i].Wildcard)
				{
					target = i;
					break;
				}
			}

			GuestTextureLayer[] under = target >= 0
				? folded[target].Layers
				: new[] { new GuestTextureLayer(original, 0, 1f, false, 0, 0) };
			var layers = new GuestTextureLayer[under.Length + maskLayers.Length];
			under.CopyTo(layers, 0);
			maskLayers.CopyTo(layers, under.Length);

			if (target >= 0)
			{
				folded[target] = folded[target] with { Layers = layers };
			}
			else
			{
				folded.Add(new GuestTextureComposite(original, layers));
			}
		}

		return folded.ToArray();
	}

	/// <summary>One bone's skeletal morph: a translation offset and a scale, guest bone index.</summary>
	public readonly record struct GuestBoneMorph(int Bone, Vector3 Offset, Vector3 Scale);

	private const int BoneMorphWords = 7;
	private static readonly uint[] _boneMorphWords = new uint[BoneMorphWords * 256];
	private static readonly List<GuestBoneMorph> _boneMorphs = new();

	/// <summary>
	/// The entity's combined skeletal morph record — what makes a fat villager fat in the rig.
	/// Empty for an entity without a mesh component or a record.
	/// </summary>
	/// <remarks>Fills a shared list, valid until the next call.</remarks>
	public static List<GuestBoneMorph> GetBoneMorph(uint entity)
	{
		List<GuestBoneMorph> result = _boneMorphs;
		result.Clear();
		if (entity == 0 || TryGetAbiVersion() is null || !IsGuestUp)
		{
			return result;
		}

		uint[] words = _boneMorphWords;
		uint count;
		try
		{
			count = RefiiHostGetBoneMorph(entity, words, (uint)words.Length);
		}
		catch (EntryPointNotFoundException)
		{
			return result;
		}

		for (int i = 0; i < count; i++)
		{
			int at = i * BoneMorphWords;
			result.Add(new GuestBoneMorph(
				(int)words[at],
				new Vector3(BitConverter.Int32BitsToSingle((int)words[at + 1]),
					BitConverter.Int32BitsToSingle((int)words[at + 2]),
					BitConverter.Int32BitsToSingle((int)words[at + 3])),
				new Vector3(BitConverter.Int32BitsToSingle((int)words[at + 4]),
					BitConverter.Int32BitsToSingle((int)words[at + 5]),
					BitConverter.Int32BitsToSingle((int)words[at + 6]))));
		}

		return result;
	}

	public static byte[] GetAnimMask(uint node, out uint skeletonId)
	{
		skeletonId = 0;
		if (node == 0 || TryGetAbiVersion() is null || !IsGuestUp)
		{
			return System.Array.Empty<byte>();
		}

		var bytes = new byte[MaxBones];
		uint count = RefiiHostGetAnimMask(node, bytes, (uint)bytes.Length, out skeletonId);
		if (count == 0)
		{
			return System.Array.Empty<byte>();
		}

		System.Array.Resize(ref bytes, (int)count);
		return bytes;
	}

	/// <summary>
	/// One attachment the guest has STATED: <paramref name="Child"/> hangs on
	/// <paramref name="Parent"/>'s dummy <paramref name="Dummy"/>.
	/// </summary>
	/// <param name="Index">The ordinal among equally-named dummies on the parent model.</param>
	/// <param name="Ticket">The attachment's id; it changes when the child is re-attached.</param>
	public readonly record struct GuestAttachment(
		uint Child, uint Parent, int Index, uint Ticket, string Dummy);

	/// <summary>How many attachments one read brings back. A busy street is well under this.</summary>
	private const int MaxAttachments = 512;

	private const int AttachmentWords = 4;

	private const int AttachmentNameBytes = 64;

	private static uint[]? _attachmentWords;

	private static byte[]? _attachmentNames;

	private static readonly ByteStrings AttachmentDummies = new(Encoding.ASCII);

	/// <summary>
	/// Strings decoded from a host byte buffer, kept across reads. The same bone name or sprite
	/// title arrives every frame, and a fresh string per row per frame was among the largest
	/// allocators in a live trace. One instance per caller: not thread-safe.
	/// </summary>
	private sealed class ByteStrings
	{
		private const int Capacity = 4096;

		private readonly Encoding _encoding;
		private readonly Dictionary<ulong, (byte[] Bytes, string Text)> _known = new();

		public ByteStrings(Encoding encoding) => _encoding = encoding;

		public string Get(byte[] buffer, int at, int length)
		{
			if (length == 0)
			{
				return string.Empty;
			}

			ReadOnlySpan<byte> bytes = buffer.AsSpan(at, length);
			ulong hash = 14695981039346656037UL;
			foreach (byte value in bytes)
			{
				hash = (hash ^ value) * 1099511628211UL;
			}

			if (_known.TryGetValue(hash, out (byte[] Bytes, string Text) known) &&
				bytes.SequenceEqual(known.Bytes))
			{
				return known.Text;
			}

			if (_known.Count >= Capacity)
			{
				_known.Clear();
			}

			string text = _encoding.GetString(bytes);
			_known[hash] = (bytes.ToArray(), text);
			return text;
		}
	}

	/// <summary>
	/// Every live attachment in the world (see RefiiHostGetAttachments). The guest places an
	/// attached entity from ITS OWN pose; the host reads the statement instead and hangs the
	/// child off its own bone.
	/// </summary>
	public static int GetAttachments(List<GuestAttachment> into)
	{
		into.Clear();
		if (TryGetAbiVersion() is null || !IsGuestUp)
		{
			return 0;
		}

		_attachmentWords ??= new uint[MaxAttachments * AttachmentWords];
		_attachmentNames ??= new byte[MaxAttachments * AttachmentNameBytes];
		uint count = RefiiHostGetAttachments(
			_attachmentWords, MaxAttachments, _attachmentNames);

		for (uint i = 0; i < count; ++i)
		{
			int at = (int)(i * AttachmentWords);
			int nameAt = (int)(i * AttachmentNameBytes);
			int length = 0;
			while (length < AttachmentNameBytes && _attachmentNames[nameAt + length] != 0)
			{
				++length;
			}

			into.Add(new GuestAttachment(
				_attachmentWords[at],
				_attachmentWords[at + 1],
				(int)_attachmentWords[at + 2],
				_attachmentWords[at + 3],
				AttachmentDummies.Get(_attachmentNames, nameAt, length)));
		}

		return into.Count;
	}

	private const int AttachmentGripWords = 18;

	private static uint[]? _gripWords;
	private static readonly List<float[]> _gripMatrices = new();

	private static bool _gripExportMissing;

	/// <summary>
	/// The offset each attached child hangs at, as the guest states it (see
	/// RefiiHostGetAttachmentGrips): record+0x50, the rigid inverse of the child's own grip
	/// dummy, by child, with the attachment's ticket. Row-major, row vectors, translation in row
	/// 3, in the child model's raw space. False when the library predates the export.
	/// </summary>
	public static bool GetAttachmentGrips(Dictionary<uint, (uint Ticket, float[] Matrix)> into)
	{
		into.Clear();
		if (_gripExportMissing || TryGetAbiVersion() is null || !IsGuestUp)
		{
			return false;
		}

		_gripWords ??= new uint[MaxAttachments * AttachmentGripWords];
		uint count;
		try
		{
			count = RefiiHostGetAttachmentGrips(_gripWords, MaxAttachments);
		}
		catch (EntryPointNotFoundException)
		{
			_gripExportMissing = true;
			return false;
		}

		for (uint i = 0; i < count; ++i)
		{
			int at = (int)(i * AttachmentGripWords);
			while (_gripMatrices.Count <= (int)i)
			{
				_gripMatrices.Add(new float[16]);
			}

			float[] matrix = _gripMatrices[(int)i];
			for (int k = 0; k < 16; ++k)
			{
				matrix[k] = System.BitConverter.Int32BitsToSingle(unchecked((int)_gripWords[at + 2 + k]));
			}

			into[_gripWords[at]] = (_gripWords[at + 1], matrix);
		}

		return true;
	}

	private static uint[]? _animWords;

	/// <summary>
	/// The guest's stated animation for one rig: the clip leaves of its controller's blend
	/// tree (see RefiiHostGetEntityAnimState). Empty when the entity has no active controller.
	/// <paramref name="flags"/>: bit0 controller active, bit1 a procedural IK component is
	/// present, bit2 the tree is netplay-replicated and was not walked. A leaf's
	/// <see cref="GuestAnimLeaf.Network"/> marks clip times extrapolated from a replica's packet
	/// alpha: their tick-to-tick continuity is not stated.
	/// </summary>
	public static int GetEntityAnimState(uint entity, List<GuestAnimLeaf> into, out uint flags)
	{
		into.Clear();
		flags = 0;
		if (TryGetAbiVersion() is null || !IsGuestUp)
		{
			return 0;
		}

		_animWords ??= new uint[AnimLeafWords * 64];
		uint leaves = RefiiHostGetEntityAnimState(entity, _animWords, (uint)_animWords.Length, out flags);
		for (int i = 0; i < leaves; ++i)
		{
			int at = i * AnimLeafWords;
			uint f = _animWords[at + 4];
			into.Add(new GuestAnimLeaf(
				_animWords[at],
				System.BitConverter.Int32BitsToSingle((int)_animWords[at + 1]),
				System.BitConverter.Int32BitsToSingle((int)_animWords[at + 2]),
				System.BitConverter.Int32BitsToSingle((int)_animWords[at + 3]),
				(f & 1) != 0, (f & 2) != 0, (int)((f >> 8) & 0xFF), (int)((f >> 16) & 0xFF),
				_animWords[at + 5],
				System.BitConverter.Int32BitsToSingle((int)_animWords[at + 6]),
				System.BitConverter.Int32BitsToSingle((int)_animWords[at + 7]),
				(f & 4) != 0,
				_animWords[at + 8],
				_animWords[at + 9],
				System.BitConverter.Int32BitsToSingle((int)_animWords[at + 10]),
				System.BitConverter.Int32BitsToSingle((int)_animWords[at + 11]),
				(f & 8) != 0,
				(f & 16) != 0,
				(f & 32) != 0));
		}

		return (int)leaves;
	}

	/// <summary>One effect's transform AS THE GUEST STATED IT, keyed by particle instance.</summary>
	/// <remarks>
	/// Position and orientation arrive as four lanes each because which lane holds x is a
	/// property of the recompiler's VMX layout, not of the guest. They are named here only once
	/// compared against the walk; do not assume an order.
	/// </remarks>
	public readonly record struct GuestStatedFx(
		uint Instance, uint Sequence,
		float P0, float P1, float P2, float P3,
		float Q0, float Q1, float Q2, float Q3);

	private const int StatedFxWords = 10;

	private static uint[]? _statedFxWords;

	/// <summary>The transforms the guest has stated through the particle hooks this frame.</summary>
	public static int GetStatedFx(List<GuestStatedFx> into)
	{
		into.Clear();
		if (TryGetAbiVersion() is null || !IsGuestUp)
		{
			return 0;
		}

		uint[] words = _statedFxWords ??= new uint[StatedFxWords * 512];
		uint rows;
		try
		{
			rows = RefiiHostGetStatedFx(words, (uint)words.Length);
		}
		catch (System.EntryPointNotFoundException)
		{
			return 0;
		}

		for (int i = 0; i < rows; ++i)
		{
			int at = i * StatedFxWords;
			float F(int k) => System.BitConverter.Int32BitsToSingle((int)words[at + k]);
			into.Add(new GuestStatedFx(
				words[at], words[at + 1],
				F(2), F(3), F(4), F(5), F(6), F(7), F(8), F(9)));
		}

		return (int)rows;
	}

	/// <summary>Transform statements the hook has seen; 0 means the hook is not loaded.</summary>
	public static uint StatedFxCount()
	{
		try
		{
			return TryGetAbiVersion() is null ? 0u : RefiiHostGetStatedFxCount();
		}
		catch (System.EntryPointNotFoundException)
		{
			return 0u;
		}
	}

	public static ProbeResult Shutdown()
	{
		var message = new byte[MessageCapacity];
		try
		{
			int ok = RefiiHostShutdown(message, (nuint)message.Length);
			return new ProbeResult(ok == 1, Decode(message));
		}
		catch (DllNotFoundException)
		{
			return new ProbeResult(false, "host bridge not loaded");
		}
	}

	private static string Decode(byte[] buffer)
	{
		int end = Array.IndexOf(buffer, (byte)0);
		return Encoding.UTF8.GetString(buffer, 0, end < 0 ? buffer.Length : end);
	}

	/// <summary>
	/// Publishes one controller's state in the guest's units: X_INPUT_GAMEPAD_* button bits,
	/// triggers 0..255, sticks -32768..32767 with +Y up. Callers convert; see GameInput.
	/// </summary>
	public static void SetControllerState(
		int userIndex, bool connected, uint buttons, uint leftTrigger, uint rightTrigger,
		int thumbLx, int thumbLy, int thumbRx, int thumbRy) =>
		RefiiHostSetControllerState(
			(uint)userIndex, connected ? 1 : 0, buttons, leftTrigger, rightTrigger,
			thumbLx, thumbLy, thumbRx, thumbRy);

	/// <summary>
	/// Copies the current Bink frame as RGBA8. Pass null to probe the size; width/height are
	/// reported either way.
	/// </summary>
	public static bool TryGetVideoFrame(byte[]? rgba, out int width, out int height)
	{
		uint w = 0;
		uint h = 0;
		int ok = RefiiHostGetVideoFrame(rgba, (uint)(rgba?.Length ?? 0), out w, out h);
		width = (int)w;
		height = (int)h;
		return ok != 0;
	}

	/// <summary>
	/// Copies the .bik buffer the guest handed to BinkOpen (the startup logos). Movies opened by
	/// name return null here; see <see cref="GetRequestedVideoName"/>.
	/// </summary>
	public static byte[]? GetRequestedVideo(out uint generation) =>
		GetRequestedVideo(out generation, uint.MaxValue);

	/// <summary>
	/// The requested movie's bytes, or null without a copy when its generation is still
	/// <paramref name="unlessGeneration"/>. The guest keeps stating the last request after the
	/// movie ends, and copying a whole .bik out of guest memory every frame cost 25 ms and tens
	/// of megabytes of garbage per frame in the world (measured 2026-09-04).
	/// </summary>
	public static byte[]? GetRequestedVideo(out uint generation, uint unlessGeneration)
	{
		uint size = 0;
		uint gen = 0;
		if (RefiiHostGetRequestedVideo(null, 0, out size, out gen) == 0 || size == 0)
		{
			generation = gen;
			return null;
		}

		if (gen == unlessGeneration)
		{
			generation = gen;
			return null;
		}

		var buffer = new byte[size];
		int ok = RefiiHostGetRequestedVideo(buffer, size, out size, out gen);
		generation = gen;
		return ok != 0 ? buffer : null;
	}

	/// <summary>
	/// The path the guest handed to BinkOpen when it opened a movie by name; empty when the
	/// request carried bytes. Generation is shared with <see cref="GetRequestedVideo"/>.
	/// </summary>
	public static string GetRequestedVideoName(out uint generation)
	{
		var buffer = new byte[512];
		uint length = RefiiHostGetRequestedVideoName(buffer, (uint)buffer.Length, out generation);
		return length == 0 ? string.Empty : System.Text.Encoding.ASCII.GetString(buffer, 0, (int)length);
	}

	/// <summary>
	/// The sound the guest states for the movie it last opened: whether it is audible (the
	/// opener's `sound` argument), which Bink track IDS play (its `BinkSetSoundTrack`), and the
	/// linear volume it states for each (`sub_82A41D88`), which can change during playback.
	/// </summary>
	public static bool GetMovieSound(uint[] ids, float[] volumes, out int count)
	{
		count = (int)RefiiHostGetMovieSound(
			out uint sound, ids, volumes, (uint)Math.Min(ids.Length, volumes.Length));
		return sound != 0;
	}

	/// <summary>
	/// Tells the guest the host's movie has finished. Returns the player address retired, or 0.
	/// </summary>
	public static uint FinishMovie() => RefiiHostFinishMovie();

	/// <summary>
	/// One of the guest's XAudio source voices. Layout mirrors <c>refii::vfs::VoiceRecord</c>
	/// (static_asserted natively).
	/// </summary>
	[StructLayout(LayoutKind.Sequential)]
	public struct GuestVoice
	{
		/// <summary>The guest's voice pointer. Unique only together with Generation.</summary>
		public uint Handle;

		/// <summary>Bumped per voice creation; the guest reuses handles.</summary>
		public uint Generation;

		public uint SampleRate;
		public uint Channels;

		/// <summary>Non-zero between the guest's Start and Stop.</summary>
		public uint Started;

		/// <summary>Set once the guest has destroyed the voice.</summary>
		public uint Retired;

		/// <summary>Linear amplitude, 1.0 unity.</summary>
		public float Volume;

		/// <summary>
		/// Frequency ratio (Godot's PitchScale). The guest states octaves; CSourceStream::SetPitch
		/// stores <c>powf(2.0f, arg)</c>, converted natively.
		/// </summary>
		public float PitchRatio;

		/// <summary>PCM bytes waiting to be drained for this voice.</summary>
		public uint QueuedBytes;

		/// <summary>Bumped per submit, so a consumer can spot buffers it missed.</summary>
		public uint SubmittedSeq;

		/// <summary>
		/// The guest's X3DAudio matrix (XAudioSetChannelVolumes) folded to a stereo balance.
		/// </summary>
		public float PanLeft;

		/// <inheritdoc cref="PanLeft"/>
		public float PanRight;
	}

	[DllImport(Library)]
	private static extern uint RefiiHostAudioSampleRate();

	[DllImport(Library)]
	private static extern uint RefiiHostAudioSamplesPerFrame();

	[DllImport(Library)]
	private static extern float RefiiHostAudioFrontGain(uint channels);

	[DllImport(Library)]
	private static extern void RefiiHostAudioOutputConfigure(uint speakerPairs, uint mixRate);

	[DllImport(Library)]
	private static extern void RefiiHostAudioOutputDetach();

	[DllImport(Library)]
	private static extern void RefiiHostAudioOutputStats(
		out ulong outFramesPulled, out ulong outUnderrunSteps, out uint outChannels);

	[DllImport(Library)]
	private static extern void RefiiHostAudioStats(
		out uint outQueuedFrames, out ulong outPushed, out ulong outPopped,
		out ulong outDropped, out int outDriverInstalled);

	// [Out] is required: a non-blittable array marshals as an in-only copy and the native
	// writes are discarded without it.
	[DllImport(Library)]
	private static extern uint RefiiHostGetVoices([Out] GuestVoice[]? outVoices, uint capacity);

	[DllImport(Library)]
	private static extern uint RefiiHostTakeVoicePcm(
		uint handle, uint generation, [Out] byte[] outPcm, uint capacity);

	[DllImport(Library)]
	private static extern int RefiiHostVoiceCaptureActive();

	[DllImport(Library)]
	private static extern void RefiiHostSetVoicePlaybackOwned(int owned);

	/// <summary>The guest's output rate — 48000, stated by its mastering voice.</summary>
	public static uint AudioSampleRate() => RefiiHostAudioSampleRate();

	/// <summary>256 — one frame is 5.333 ms, so 187.5 frames a second.</summary>
	public static uint AudioSamplesPerFrame() => RefiiHostAudioSamplesPerFrame();

	/// <summary>
	/// The linear weight the output stage gives the guest's front channels for a device of
	/// <paramref name="channels"/> (2 folds, 6 passes through). Sound the host decodes itself is
	/// scaled by this so it sits at the level of the guest's mix.
	/// </summary>
	public static float AudioFrontGain(uint channels) => RefiiHostAudioFrontGain(channels);

	/// <summary>
	/// Tells the output stage how many speaker pairs the bus has (Godot's bus channel count: 1
	/// stereo, 2 for 3.1, 3 for 5.1, 4 for 7.1) and the mix rate, and attaches it to the guest's
	/// frame ring. Call before the effect's first mix step, and again if the device changes.
	/// </summary>
	public static void AudioOutputConfigure(uint speakerPairs, uint mixRate) =>
		RefiiHostAudioOutputConfigure(speakerPairs, mixRate);

	/// <summary>Detaches the output stage; the guest's frames are then dropped, not waited for.</summary>
	public static void AudioOutputDetach() => RefiiHostAudioOutputDetach();

	/// <summary>
	/// Frames the effect has pulled, mix steps it found the ring empty on once the guest was
	/// producing, and the channel count it is producing (2 or 6).
	/// </summary>
	public static void AudioOutputStats(out ulong framesPulled, out ulong underrunSteps, out uint channels) =>
		RefiiHostAudioOutputStats(out framesPulled, out underrunSteps, out channels);

	/// <summary>
	/// Audio health. <paramref name="pushed"/> settles at 187.5/s when the clock is right;
	/// <paramref name="dropped"/> climbing means nothing is draining.
	/// </summary>
	public static void AudioStats(
		out uint queuedFrames, out ulong pushed, out ulong popped, out ulong dropped,
		out bool driverInstalled)
	{
		RefiiHostAudioStats(out queuedFrames, out pushed, out popped, out dropped,
			out int installed);
		driverInstalled = installed != 0;
	}

	/// <summary>
	/// Snapshots the guest's live voices. Returns the number that exist, which may exceed
	/// the buffer — grow and ask again rather than lose voices.
	/// </summary>
	public static uint GetVoices(GuestVoice[]? outVoices)
	{
		uint total = RefiiHostGetVoices(outVoices, outVoices is null ? 0u : (uint)outVoices.Length);

		// The native side never reports a null handle, so a zeroed first record with a non-zero
		// count is a marshalling fault. Reported once.
		if (!_reportedVoiceMarshalFault && total > 0 && outVoices is not null &&
			outVoices.Length > 0 && outVoices[0].Handle == 0)
		{
			_reportedVoiceMarshalFault = true;
			GD.PushError(
				$"[audio] the bridge reported {total} voices but the first record came back " +
				"zeroed — the array is not reaching native code writable. Check [Out] on " +
				"RefiiHostGetVoices.");
		}

		return total;
	}

	private static bool _reportedVoiceMarshalFault;

	/// <summary>
	/// Drains one voice's PCM as interleaved 16-bit samples. A generation mismatch returns 0.
	/// </summary>
	public static uint TakeVoicePcm(uint handle, uint generation, byte[] outPcm) =>
		RefiiHostTakeVoicePcm(handle, generation, outPcm, (uint)outPcm.Length);

	/// <summary>True once the voice hooks have seen any voice.</summary>
	public static bool VoiceCaptureActive() => RefiiHostVoiceCaptureActive() != 0;

	/// <summary>
	/// Hands per-voice playback to Godot: the guest's mixed frame is discarded rather than
	/// queued, but its mixer keeps running and being credited (it remains the clock).
	/// </summary>
	public static void SetVoicePlaybackOwned(bool owned) =>
		RefiiHostSetVoicePlaybackOwned(owned ? 1 : 0);

	/// <summary>One live guest entity, in guest world space.</summary>
	/// <summary>
	/// One entity as the guest states it — including the PREVIOUS transform it interpolates from.
	/// </summary>
	/// <remarks>
	/// `PreviousPosition`/`PreviousRotation` are the guest's own latch: the first act of every
	/// game tick copies each renderable's transform to `obj+0xA0`/`+0xB0` (`sub_8222B2E0`), the
	/// tick then moves it, and the guest draws `prev*(1-alpha) + live*alpha` with a slerp for the
	/// rotation (`sub_822112D8`).
	///
	/// `Interpolate` is `obj+0xC9`, and it is an ENABLE: non-zero means blend, zero means take
	/// the live transform as-is. Set to 1 at construction and cleared only by the Havok
	/// rigid-body component's init, so it reads "interpolate unless physics owns this".
	/// </remarks>
	public readonly record struct GuestEntity(
		Vector3 Position, Quaternion Rotation, uint ModelHash,
		uint Address, ulong UniqueId, uint RecordHash, bool Changed = true,
		Vector3 PreviousPosition = default, Quaternion PreviousRotation = default,
		bool Interpolate = false, uint Highlight = 0x000000FFu,
		EntityAppearance Appearance = default)
	{
		/// <summary>
		/// Whether the guest is drawing a targeting outline on this entity right now.
		///
		/// The off state is a colour, not a flag: `sub_82429BB0` clears the highlight by writing
		/// `{0,0,0,255}` and leaves the flag word alone, so the 0x20 bit at component+144 stays
		/// set after the first colour. `sub_82572C18` submits the shader constant only when the
		/// bytes differ from `dword_8331B158` = 00 00 00 FF; that comparison defines on.
		/// </summary>
		public bool Highlighted => Highlight != 0x000000FFu;

		/// <summary>
		/// The stated outline colour. Taken, never re-derived from the category tables. The lanes
		/// are the guest's: `sub_82572C18` packs `(+98, +97, +96, +99) / 255` (0x82572d8c..
		/// 0x82572e58), so red is the THIRD stated byte and blue the first.
		/// </summary>
		public Color HighlightColour => Color.Color8(
			(byte)(Highlight >> 8), (byte)(Highlight >> 16), (byte)(Highlight >> 24), (byte)Highlight);
	}

	/// <summary>
	/// A renderable's look as the guest's appearance setters stated it — CECGraphicAppearance
	/// (component 3), whose setters all end in `sub_82573318`, hooked in entity_appearance.cpp.
	/// </summary>
	/// <remarks>
	/// Alpha (+112) is what gameplay fades: every creature death of DeathType 5, 6 and 7 ramps it
	/// 1 → 0 before the body is destroyed, anim "ALPHA" tracks drive it, a banshee and a shadow
	/// creature set it. CameraAlpha (+116) is the camera-occlusion fade alone. The guest draws
	/// their product, and draws nothing at all when it is not above 1e-4 or when Drawable (+197)
	/// is clear (`sub_822112D8`, 0x82211364) — except a PRINCIPAL, whom camera fade never hides.
	///
	/// The default is the constructor's (`sub_82571DF8`), so an entity the bridge said nothing
	/// about is fully drawn: `default` of this struct is NOT that, hence <see cref="Constructed"/>.
	/// </remarks>
	public readonly record struct EntityAppearance(
		float Alpha, float CameraAlpha, float Scale, Vector4 TintLanes, uint Flags)
	{
		public static readonly EntityAppearance Constructed = new(1f, 1f, 1f, Vector4.One, DrawableFlag);

		public const uint DrawableFlag = 1u;
		public const uint SaturationFlag = 2u;
		public const uint NoAlphaDepthFlag = 4u;
		public const uint DeadCreatureFlag = 8u;

		/// <summary>
		/// Bit 4 (`16u`, hostcore `kAppearanceGuestCorpse`): <c>CECCreature_FinishDeath</c> ran on
		/// the entity's CECCreature since it was constructed — the guest leaves the body lying.
		/// Stated by the Fable 2 bridge only (see Engine_CorpsePolicy.LeavesCorpse).
		/// </summary>
		public const uint GuestCorpseFlag = 16u;

		/// <summary>The liveness bits: what <see cref="VisualOnly"/> strips.</summary>
		private const uint LivenessFlags = DeadCreatureFlag | GuestCorpseFlag;

		/// <summary>SetAsDrawable (+197). Clear: the guest releases the proxies — no draw, no shadow.</summary>
		public bool Drawable => (Flags & DrawableFlag) != 0u;

		/// <summary>SetSaturationMask (+196).</summary>
		public bool SaturationMask => (Flags & SaturationFlag) != 0u;

		/// <summary>
		/// The definition's DisableAlphaDepthWrites (+192, vf8 `sub_825720A8`): the twin's alpha
		/// reference is 255 (`sub_82572C18`), so its alpha-tested draws keyed to that reference
		/// pass nothing (see Engine_EntityAppearance).
		/// </summary>
		public bool NoAlphaDepthWrites => (Flags & NoAlphaDepthFlag) != 0u;

		/// <summary>Entity carries a <c>CECCreature</c> component whose <c>alive</c> byte (<c>+0x18</c>) is 0.</summary>
		public bool IsDeadCreature => (Flags & DeadCreatureFlag) != 0u;

		/// <summary>See <see cref="GuestCorpseFlag"/>.</summary>
		public bool IsGuestCorpse => (Flags & GuestCorpseFlag) != 0u;

		/// <summary>Appearance stripped of the liveness bits so shader/tint caches ignore liveness flips.</summary>
		public EntityAppearance VisualOnly =>
			(Flags & LivenessFlags) == 0u ? this : this with { Flags = Flags & ~LivenessFlags };

		/// <summary>The opacity the guest blends by: `+116 * +112` (`sub_82572A58`).</summary>
		public float Opacity => Alpha * CameraAlpha;

		/// <summary>
		/// Whether the guest draws it at all (`sub_822112D8`): drawable, and the product above
		/// 1e-4 — or, for a principal, the gameplay alpha alone at or above it.
		/// </summary>
		public bool Drawn(bool principal) =>
			Drawable && (Opacity > 1e-4f || (principal && Alpha >= 1e-4f));

		/// <summary>
		/// Whether a drawn entity's colour-pass draws are built at all: the product above 0
		/// (`sub_82219AB8` 0x82219CB0 `fcmpu cr6, f31, f29` / `ble`, f29 = 0.0; disc 0x82219DE0).
		/// At 0 nothing of the pass is built — the body, the fur shells and their prelit
		/// textures. Only a principal is drawn with the product at 0.
		/// </summary>
		public bool Submitted => Opacity > 0f;

		/// <summary>
		/// <c>this == Constructed</c>, written out: the record's equality goes through a comparer per
		/// field, and every entity the walk does not skip asks this several times.
		/// </summary>
		public bool IsConstructed =>
			Alpha == 1f && CameraAlpha == 1f && Scale == 1f && (Flags & ~LivenessFlags) == DrawableFlag &&
			TintLanes.X == 1f && TintLanes.Y == 1f && TintLanes.Z == 1f && TintLanes.W == 1f;
	}

	/// <summary>Entities the last walk found changed, and the walk's size; reported.</summary>
	public static int LastWalkChanged { get; private set; }

	public static int LastWalkCount { get; private set; }

	/// <summary>
	/// How many entities <see cref="GetEntities"/> reads in one call. Sized above the biggest
	/// region: the native walk truncates silently, and `Engine_StaticTwins.Reconcile` treats
	/// absence as "no such entity".
	/// </summary>
	private const int MaxEntities = 32768;

	/// <summary>
	/// The last <see cref="GetEntities"/> filled its buffer, so the list may be a prefix; nothing
	/// may conclude from an entity's absence while this is set.
	/// </summary>
	public static bool EntitiesTruncated { get; private set; }

	/// <summary>
	/// Words per entity: 12 stated, the guest's previous transform and its flag, the highlight,
	/// and eight of appearance (alpha, camera alpha, scale, four tint lanes, flags).
	/// </summary>
	private const int EntityStride = HostStatedWords.Stride;

	/// <summary>How many distinct unrecognised placement vtables to bring back.</summary>
	private const uint MaxUnknownVtables = 32;

	private static uint[]? _entityWords;

	/// <summary>The walk's rows and each address's stated words, for <see cref="GuestEntity.Changed"/>.</summary>
	private static HostStatedWords? _statedWords;

	private static bool _reportedTruncation;

	/// <summary>A placement vtable the bridge does not know, and how many live entities carry it.</summary>
	public readonly record struct UnknownPlacement(uint Vtable, uint Entities);

	/// <summary>
	/// Every live entity's transform and model from guest memory. One pass, no guest code, safe
	/// per frame (a torn read is one bad frame). Entities with an unrecognised placement vtable
	/// are skipped and counted in <paramref name="unknown"/>; <paramref name="unknownVtables"/>
	/// lists them (explicit length: a zero vtable word is a real observation).
	/// </summary>
	public static System.Collections.Generic.List<GuestEntity> GetEntities(
		out uint unknown, out UnknownPlacement[] unknownVtables) =>
		GetEntities(new System.Collections.Generic.List<GuestEntity>(), out unknown, out unknownVtables);

	/// <summary>The walk into a caller-owned list, cleared first: a per-frame walk keeps its list.</summary>
	public static System.Collections.Generic.List<GuestEntity> GetEntities(
		System.Collections.Generic.List<GuestEntity> entities,
		out uint unknown, out UnknownPlacement[] unknownVtables) =>
		Walk(entities, statesChanges: true, out unknown, out unknownVtables);

	/// <summary>
	/// The same walk for a reader that is not the entity feed (a report, a probe): every row reads
	/// as changed, and what each address last stated is left as the feed's last walk left it.
	/// </summary>
	/// <remarks>
	/// <see cref="GuestEntity.Changed"/> is an edge: the walk that sees a new statement reports it
	/// once. A second reader that walked through the same table took that edge from the feed, whose
	/// next walk then read the entity as unchanged and never moved its node — after a level change,
	/// an address the guest recycled kept the old region's entity standing where it stood.
	/// </remarks>
	public static System.Collections.Generic.List<GuestEntity> PeekEntities(
		out uint unknown, out UnknownPlacement[] unknownVtables) =>
		Walk(new System.Collections.Generic.List<GuestEntity>(), statesChanges: false, out unknown, out unknownVtables);

	private static System.Collections.Generic.List<GuestEntity> Walk(
		System.Collections.Generic.List<GuestEntity> entities, bool statesChanges,
		out uint unknown, out UnknownPlacement[] unknownVtables)
	{
		unknown = 0;
		unknownVtables = System.Array.Empty<UnknownPlacement>();
		entities.Clear();
		if (TryGetAbiVersion() is null)
		{
			return entities;
		}

		// One walk at a time: the buffers are shared, and HostEntityWalk calls this off the
		// main thread.
		lock (_entityWalkGate)
		{
		_statedWords ??= new HostStatedWords(MaxEntities);
		_entityWords = _statedWords.Words;
		uint[] pairs = _unknownPairs ??= new uint[MaxUnknownVtables * 2];
		uint count = RefiiHostGetEntities(
			_entityWords, MaxEntities, out unknown, pairs, MaxUnknownVtables,
			out uint distinct);

		EntitiesTruncated = count >= MaxEntities;
		if (EntitiesTruncated && !_reportedTruncation)
		{
			_reportedTruncation = true;
			GD.PrintErr($"[host] the entity walk filled its {MaxEntities}-entry buffer — the " +
				"list is a PREFIX of the guest's entities. Nothing may treat an entity's " +
				"absence from it as a statement that the guest does not have one.");
		}

		distinct = System.Math.Min(distinct, MaxUnknownVtables);
		unknownVtables = new UnknownPlacement[distinct];
		for (uint i = 0; i < distinct; ++i)
		{
			unknownVtables[i] = new UnknownPlacement(pairs[i * 2], pairs[(i * 2) + 1]);
		}

		// CHANGED = any stated word differs from the last walk, bit for bit. The feed skips the
		// placement of an entity whose statement did not move; the walk is where the comparison
		// costs nothing the main thread pays.
		int walkChanged = 0;
		if (statesChanges)
		{
			_statedWords.BeginWalk((int)count);
		}

		for (uint i = 0; i < count; ++i)
		{
			int at = (int)(i * EntityStride);
			uint address = _entityWords[at + 8];
			bool changed = !statesChanges || _statedWords.Changed((int)i);
			if (changed)
			{
				++walkChanged;
			}

			entities.Add(new GuestEntity(
				new Vector3(
					BitConverter.Int32BitsToSingle((int)_entityWords[at]),
					BitConverter.Int32BitsToSingle((int)_entityWords[at + 1]),
					BitConverter.Int32BitsToSingle((int)_entityWords[at + 2])),
				new Quaternion(
					BitConverter.Int32BitsToSingle((int)_entityWords[at + 3]),
					BitConverter.Int32BitsToSingle((int)_entityWords[at + 4]),
					BitConverter.Int32BitsToSingle((int)_entityWords[at + 5]),
					BitConverter.Int32BitsToSingle((int)_entityWords[at + 6])),
				_entityWords[at + 7],
				address,
				((ulong)_entityWords[at + 9] << 32) | _entityWords[at + 10],
				_entityWords[at + 11],
				changed,
				new Vector3(
					BitConverter.Int32BitsToSingle((int)_entityWords[at + 12]),
					BitConverter.Int32BitsToSingle((int)_entityWords[at + 13]),
					BitConverter.Int32BitsToSingle((int)_entityWords[at + 14])),
				new Quaternion(
					BitConverter.Int32BitsToSingle((int)_entityWords[at + 15]),
					BitConverter.Int32BitsToSingle((int)_entityWords[at + 16]),
					BitConverter.Int32BitsToSingle((int)_entityWords[at + 17]),
					BitConverter.Int32BitsToSingle((int)_entityWords[at + 18])),
				_entityWords[at + 19] != 0u,
				_entityWords[at + 20],
				new EntityAppearance(
					BitConverter.Int32BitsToSingle((int)_entityWords[at + 21]),
					BitConverter.Int32BitsToSingle((int)_entityWords[at + 22]),
					BitConverter.Int32BitsToSingle((int)_entityWords[at + 23]),
					new Vector4(
						BitConverter.Int32BitsToSingle((int)_entityWords[at + 24]),
						BitConverter.Int32BitsToSingle((int)_entityWords[at + 25]),
						BitConverter.Int32BitsToSingle((int)_entityWords[at + 26]),
						BitConverter.Int32BitsToSingle((int)_entityWords[at + 27])),
					_entityWords[at + 28])));
		}

		if (statesChanges)
		{
			LastWalkChanged = walkChanged;
			LastWalkCount = (int)count;
			_statedWords.EndWalk((int)count);
		}
		}

		return entities;
	}

	private static readonly object _entityWalkGate = new();
	private static uint[]? _unknownPairs;

	/// <summary>Resets cached per-region entity walk diffing state during region teardown.</summary>
	public static void ResetEntityWalkState()
	{
		lock (_entityWalkGate)
		{
			_statedWords?.Clear();
		}
	}

	/// <summary>The most bones a character rig is read at. Fable's rigs are well under this.</summary>
	public const int MaxBones = 256;

	/// <summary>
	/// A live character's skeleton pose. <see cref="BonePose.Space"/> is the guest's own statement:
	/// 2 model, 1 local. The update writes local then converts in place, so mid-update samples are local.
	/// </summary>
	public readonly record struct BonePose(
		int BoneCount, uint Space, bool Torn, float[] Transforms, int[] Parents)
	{
		/// <summary>True when the bones are already object-space — no parent walk needed.</summary>
		public bool IsModelSpace => Space == 2;

		/// <summary>Bone <paramref name="index"/>'s rotation.</summary>
		public Quaternion Rotation(int index) => new(
			Transforms[(index * 12) + 0], Transforms[(index * 12) + 1],
			Transforms[(index * 12) + 2], Transforms[(index * 12) + 3]);

		/// <summary>Bone <paramref name="index"/>'s translation.</summary>
		public Vector3 Translation(int index) => new(
			Transforms[(index * 12) + 4], Transforms[(index * 12) + 5],
			Transforms[(index * 12) + 6]);

		/// <summary>Bone <paramref name="index"/>'s scale.</summary>
		public Vector3 Scale(int index) => new(
			Transforms[(index * 12) + 8], Transforms[(index * 12) + 9],
			Transforms[(index * 12) + 10]);
	}

	private static float[]? _poseTransforms;

	private static int[]? _poseParents;

	/// <summary>
	/// The pose of one entity, or null when it carries no animated mesh — which most entities
	/// do not, so null is the ordinary answer and not a failure.
	///
	/// The buffers are reused across calls, so the returned arrays are only valid until the
	/// next call. Callers copy what they keep.
	/// </summary>
	public static BonePose? GetEntityPose(uint entity)
	{
		if (TryGetAbiVersion() is null || !IsGuestUp)
		{
			return null;
		}

		_poseTransforms ??= new float[MaxBones * 12];
		_poseParents ??= new int[MaxBones];

		uint bones = RefiiHostGetEntityPose(
			entity, _poseTransforms, _poseParents, MaxBones, out uint space, out uint torn);

		return bones == 0
			? null
			: new BonePose((int)bones, space, torn != 0, _poseTransforms, _poseParents);
	}

	/// <summary>
	/// A character's REST pose — the bind pose, read once per model rather than per frame.
	/// Unlike the live pose this is LOCAL and the caller composes it through the parent chain.
	/// Returns a copy, not the shared buffer, since it is kept for the life of the skeleton.
	/// Cheap to ask and usually null: the bridge answers from one bit at `entity+0x24`, and
	/// nothing is allocated until the entity turns out to have a rig.
	/// </summary>
	public static BonePose? GetEntityRestPose(uint entity)
	{
		if (TryGetAbiVersion() is null || !IsGuestUp)
		{
			return null;
		}

		_poseTransforms ??= new float[MaxBones * 12];
		_poseParents ??= new int[MaxBones];

		uint bones = RefiiHostGetEntityRestPose(entity, _poseTransforms, _poseParents, MaxBones);
		if (bones == 0)
		{
			return null;
		}

		var transforms = new float[bones * 12];
		var parents = new int[bones];
		System.Array.Copy(_poseTransforms, transforms, transforms.Length);
		System.Array.Copy(_poseParents, parents, parents.Length);

		// Space 1 — local, unlike the live pose. Never torn: this is the SKELETON's array,
		// written once at load, not the component's.
		return new BonePose((int)bones, 1, false, transforms, parents);
	}

	/// <summary>
	/// The guest's own name for each bone of a character's rig, or null when it has none. The
	/// name is the ONLY key the two skeletons share: a `.mdl` numbers its bones differently from
	/// the guest's runtime rig, so an index means different things on the two sides. An empty
	/// entry is a real answer (an unnamed bone), and names are interned, so duplicates occur.
	/// </summary>
	public static string[]? GetEntityBoneNames(uint entity)
	{
		if (TryGetAbiVersion() is null || !IsGuestUp)
		{
			return null;
		}

		_boneNames ??= new byte[MaxBones * BoneNameStride];
		uint bones = RefiiHostGetEntityBoneNames(entity, _boneNames, BoneNameStride, MaxBones);
		if (bones == 0)
		{
			return null;
		}

		var names = new string[bones];
		for (int i = 0; i < bones; ++i)
		{
			int start = i * (int)BoneNameStride;
			int length = 0;
			while (length < BoneNameStride && _boneNames[start + length] != 0)
			{
				++length;
			}

			names[i] = System.Text.Encoding.ASCII.GetString(_boneNames, start, length);
		}

		return names;
	}

	/// <summary>
	/// Each guest skeleton bone's parent index (-1 a root), in <see cref="GetEntityBoneNames"/>'s
	/// order; null when the bridge does not state them.
	/// </summary>
	public static int[]? GetEntityBoneParents(uint entity)
	{
		if (TryGetAbiVersion() is null || !IsGuestUp)
		{
			return null;
		}

		_boneParents ??= new int[MaxBones];
		uint bones;
		try
		{
			bones = RefiiHostGetEntityBoneParents(entity, _boneParents, MaxBones);
		}
		catch (EntryPointNotFoundException)
		{
			return null;
		}

		return bones == 0 ? null : _boneParents.AsSpan(0, (int)bones).ToArray();
	}

	private static int[]? _boneParents;

	/// <summary>
	/// Bytes per name in the bone-name buffer, passed to the bridge rather than agreed with it.
	/// The longest name in the shipped rigs is 40 (`Carry_SheathRangedWeaponBack_DummyObject`).
	/// </summary>
	private const uint BoneNameStride = 64;

	private static byte[]? _boneNames;

	/// <summary>
	/// The component ids one entity carries, in the order its own vector holds them. Whether an
	/// entity carries <c>CECGraphicAppearanceMorph</c> (id 17) — the parts list a character with
	/// a zero model hash is drawn from, see <see cref="GetEntityMorphParts"/> — is a live-memory
	/// question no decompile settles. The pointer half is dropped deliberately: existence is the
	/// question, and an address invites reading through an unverified layout.
	/// </summary>
	public static byte[] GetEntityComponents(uint entity)
	{
		if (TryGetAbiVersion() is null)
		{
			return System.Array.Empty<byte>();
		}

		_componentPairs ??= new uint[MaxComponents * 2];
		uint pairs = RefiiHostGetEntityComponents(entity, _componentPairs, MaxComponents);
		if (pairs == 0)
		{
			return System.Array.Empty<byte>();
		}

		var ids = new byte[pairs];
		for (int i = 0; i < pairs; ++i)
		{
			ids[i] = (byte)_componentPairs[i * 2];
		}

		return ids;
	}

	/// <summary>
	/// The locomotion state a character is in, as the guest itself names it.
	/// <see cref="State"/> is empty when the entity carries no locomotion mode, which most do
	/// not.
	/// </summary>
	public readonly record struct GuestLocomotion(
		uint Machine, uint StateAddress, uint ModeType, float Dwell,
		uint Stance, uint CombatState, uint Confinement, string State)
	{
		/// <summary>True when the guest is running a locomotion machine on this entity.</summary>
		public bool IsPresent => Machine != 0;

		/// <summary>Walk (2), strafe (3) or aiming (4), from `GetModeType()`.</summary>
		public bool IsStrafing => ModeType == 3;

		public bool IsAiming => ModeType == 4;
	}

	private const int LocomotionWords = 7;

	/// <summary>Longest state name the shipped script defines is 47 characters.</summary>
	private const int LocomotionNameCapacity = 128;

	private static uint[]? _locomotionWords;

	private static byte[]? _locomotionName;

	/// <summary>
	/// What the guest's own locomotion machine has selected for this entity — a read of its
	/// decision, not a re-derivation. The machine is an entity mode (component 21, whose
	/// `GetModeType()` is 2/3/4) holding its current state as a named object. The velocity and
	/// control magnitudes it tests are NOT available: the guest builds them into a stack
	/// temporary each tick and nothing resident holds them.
	/// </summary>
	public static GuestLocomotion GetEntityLocomotion(uint entity)
	{
		if (TryGetAbiVersion() is null || !IsGuestUp)
		{
			return default;
		}

		_locomotionWords ??= new uint[LocomotionWords];
		_locomotionName ??= new byte[LocomotionNameCapacity];

		uint written = RefiiHostGetEntityLocomotion(
			entity, _locomotionWords, LocomotionWords,
			_locomotionName, LocomotionNameCapacity);

		if (written < LocomotionWords)
		{
			return default;
		}

		uint[] w = _locomotionWords;

		int length = System.Array.IndexOf(_locomotionName, (byte)0);
		string state = length <= 0
			? string.Empty
			: System.Text.Encoding.ASCII.GetString(_locomotionName, 0, length);

		return new GuestLocomotion(
			w[0], w[1], w[2],
			System.BitConverter.Int32BitsToSingle(unchecked((int)w[3])),
			w[4], w[5], w[6], state);
	}

	/// <summary>
	/// Component pairs read per entity. The class registry in `sub_826399A8` holds 264 classes,
	/// so no entity can carry more than that and 128 is far past anything real.
	/// </summary>
	private const uint MaxComponents = 128;

	private static uint[]? _componentPairs;

	/// <summary>
	/// ONE SNAPSHOT PER GODOT FRAME. The guest step runs concurrently with `_Process`, so two
	/// reads in one frame could name two screens, and twenty-two call sites read this; every
	/// driver in a frame now sees the same statement, read once.
	/// </summary>
	public static PauseState? GetPauseState()
	{
		if (TryGetAbiVersion() is null)
		{
			return null;
		}

		ulong frame = Godot.Engine.GetProcessFrames();
		if (frame == _pauseStateFrame)
		{
			return _pauseState;
		}

		_pauseStateFrame = frame;
		_pauseState = RefiiHostGetPauseState(out uint screen, out uint modal, out uint mode, out uint tab) == 0
			? null
			: new PauseState(screen, modal, mode, tab);
		if (_pauseState is { } up && _screenOnPeer != 0u && up.ScreenId == _screenOnPeer)
		{
			_pauseState = null;
		}

		return _pauseState;
	}

	/// <summary>
	/// A SCREEN THE CO-OP PEER SHOWS FOR ITS PLAYER: this machine's guest has it up, but it is answered on
	/// the other machine — an online host's arrest of player two, whose crime page is the joiner's
	/// (GameUiCoopConsent). While set, this machine reports that screen as not up (the pause state and the
	/// screen manager's current screen and presentation), so nothing here draws it or takes input for it. 0 none.
	/// </summary>
	public static uint ScreenOnPeer
	{
		get => _screenOnPeer;
		set
		{
			if (_screenOnPeer != value)
			{
				_screenOnPeer = value;
				_screenCache = null;
				_pauseStateFrame = ulong.MaxValue;
			}
		}
	}

	private static uint _screenOnPeer;

	/// <summary>The screen the guest states as up, whatever <see cref="ScreenOnPeer"/> hides; null outside the game.</summary>
	public static uint? StatedScreenRaw()
	{
		if (TryGetAbiVersion() is null || !IsInGame)
		{
			return null;
		}

		try
		{
			return RefiiHostStatedScreen();
		}
		catch (EntryPointNotFoundException)
		{
			return null;
		}
	}

	private static PauseState? _pauseState;

	private static ulong _pauseStateFrame = ulong.MaxValue;

	/// <summary>
	/// Picks a pause tab: the guest's own tab push runs on its next tick (pump action
	/// <c>PauseTab:n</c>).
	/// </summary>
	/// <remarks>
	/// Tabs: 2 map, 3 abilities, 4 items, 5 weapons, 6 clothing, 7 logbook, 8 options. Refused
	/// off the pause root, under a box, or for the tab already shown. The guest's push does not
	/// re-check the unlock flags, so a ghosted entry must never reach this call.
	/// </remarks>
	public static bool PauseTabSelect(uint tab)
	{
		if (tab < 2u || tab > 8u || GetPauseState() is not { } pause ||
			pause.ScreenId != 0u || pause.ModalId != 0u || pause.CurrentTab == tab)
		{
			return false;
		}

		PostFrontEndAction("PauseTab:" + tab);
		return true;
	}

	/// <summary>The words a title's own calls state by key (payload_bridge.h).</summary>
	public enum StatedValue : uint
	{
		AbilitiesAssignMode = 1,
		FlowTopVtable = 2,
		FlowDepth = 3,
		LoadingFixedArt = 4,
		LoadingTextOnly = 5,
		LoadingQuitFade = 6,
		OptionGamma = 7,
		OptionSfx = 8,
		OptionMusic = 9,
		OptionSpeech = 10,
		OptionSpeaker = 11,
		OptionLevelsStated = 12,
		FrontEnd = 13,
		Rumble = 14,
		PendingMelee = 15,
		PendingRanged = 16,
		ExperienceShown = 17,
		FrontEndMode = 18,
		WorldHeld = 19,
		AbilityDiscardGate = 20,
		CoopTetherDistance = 21,
		CoopTetherSeparation = 22,
		CoopTetherSerial = 23,
		CoopLocalPartner = 24,
		PartnerPlayerIcons = 25,
	}

	/// <summary>The word the guest's own call last stated under <paramref name="key"/>; 0 until one is.</summary>
	public static uint Stated(StatedValue key) =>
		TryGetAbiVersion() is null ? 0u : RefiiHostStatedValue((uint)key);

	private static uint[] _partnerFadeEntities = new uint[64];
	private static float[] _partnerFadeAlphas = new float[64];

	/// <summary>
	/// Player two's camera's fade book (both titles): each entity player two's camera has faded, by guest address,
	/// and the camera alpha it gives it, as the core camera chunk stated it at the end of player two's last fade-up
	/// (Refii._partner_camera_alphas). The renderable's own camera alpha is player one's camera's. Fills
	/// <paramref name="into"/> (cleared first); returns the statement serial, 0 before the first or without a guest.
	/// </summary>
	public static uint PartnerCameraAlphas(System.Collections.Generic.Dictionary<uint, float> into)
	{
		into.Clear();
		if (TryGetAbiVersion() is null || !IsGuestUp)
		{
			return 0u;
		}

		uint held = RefiiHostGetPartnerCameraAlphas(_partnerFadeEntities, _partnerFadeAlphas, (uint)_partnerFadeEntities.Length, out uint serial);
		if (held > _partnerFadeEntities.Length)
		{
			int grown = (int)System.Math.Min(System.Numerics.BitOperations.RoundUpToPowerOf2(held), 4096u);
			_partnerFadeEntities = new uint[grown];
			_partnerFadeAlphas = new float[grown];
			held = RefiiHostGetPartnerCameraAlphas(_partnerFadeEntities, _partnerFadeAlphas, (uint)grown, out serial);
		}

		int n = (int)System.Math.Min(held, (uint)_partnerFadeEntities.Length);
		for (int i = 0; i < n; i++)
		{
			into[_partnerFadeEntities[i]] = _partnerFadeAlphas[i];
		}

		return serial;
	}



	/// <summary>All seven tab bits — what <see cref="PauseTabsAvailable"/> means by "everything".</summary>
	public const uint AllPauseTabs = 0x1FCu;

	/// <summary>The token names <see cref="GetMapMarkers"/> carries, by the bridge's index.</summary>
	private static readonly string[] MapMarkerTokens = { "Quest", "QuestExit", "QuestGiver" };

	/// <summary>
	/// The pause-root minimap's quest markers this open, as the guest names them to its placer
	/// (`sub_826A3FD8`): the token and the world point. Godot projects them like the hero's token.
	/// </summary>
	public static System.Collections.Generic.List<MapMarker> GetMapMarkers()
	{
		var markers = new System.Collections.Generic.List<MapMarker>();
		if (TryGetAbiVersion() is null)
		{
			return markers;
		}

		var tokens = new uint[8];
		var values = new float[80];
		uint count = RefiiHostGetMapMarkers(tokens, values, 8);
		for (uint i = 0; i < count && i < 8; ++i)
		{
			if (tokens[i] < MapMarkerTokens.Length)
			{
				markers.Add(new MapMarker(MapMarkerTokens[tokens[i]],
					values[i * 10], values[(i * 10) + 1], values[(i * 10) + 2]));
			}
		}

		return markers;
	}

	/// <summary>
	/// The subtitles setting the guest last stated: true/false, or null before the first
	/// sample. Sampled by the tick pump through the guest's own reader while the front end
	/// is up, so the first-boot default (non-en-uk forces subtitles on) is the guest's rule,
	/// not ours.
	/// </summary>
	public static bool? GetSubtitles()
	{
		if (TryGetAbiVersion() is null)
		{
			return null;
		}

		return RefiiHostGetSubtitles() switch { 1 => true, 0 => false, _ => null };
	}

	/// <summary>
	/// The UIDs the guest holds in its DeadEntities set — placements it will not respawn.
	/// </summary>
	/// <remarks>
	/// The same set the guest consults before spawning a placement, and persists to the save.
	/// Never cache it: deactivating a layer purges that layer's entries, and an entry vanishing
	/// means those props legitimately come back.
	/// </remarks>
	public static ulong[] GetDeadEntities()
	{
		if (TryGetAbiVersion() is null)
		{
			return System.Array.Empty<ulong>();
		}

		_deadUids ??= new ulong[MaxDeadEntities];
		uint written = RefiiHostGetDeadEntities(_deadUids, MaxDeadEntities);
		if (written == 0)
		{
			return System.Array.Empty<ulong>();
		}

		var uids = new ulong[written];
		System.Array.Copy(_deadUids, uids, written);
		return uids;
	}

	private const uint MaxDeadEntities = 4096;

	private static ulong[]? _deadUids;

	private static bool _spotFrustumsMissing;

	/// <summary>
	/// The spots' receive frustums: out[0] the count, then 66 floats each — the manager entry
	/// index, the plane count, 16 planes (n.xyz, w; inside is dot(n, p) + w &gt;= 0). Returns the
	/// floats written.
	/// </summary>
	public static uint GetSpotFrustums(float[] into)
	{
		if (_spotFrustumsMissing || TryGetAbiVersion() is null || !IsGuestUp)
		{
			return 0;
		}
		try
		{
			return RefiiHostGetSpotFrustums(into, (uint)into.Length);
		}
		catch (EntryPointNotFoundException)
		{
			_spotFrustumsMissing = true;
			return 0;
		}
	}

	private static bool _localLightsMissing;

	/// <summary>Floats per light of <see cref="GetClassicLocalLights"/>.</summary>
	public const int ClassicLocalLightStride = 16;

	/// <summary>
	/// The manager's local lights as their packer and shadow object state them, 16 floats each:
	/// entry index, position L+288, direction L+304, has a spot map, fovY, near, far, depth bias,
	/// slope-scale bias, three reserved. Returns the lights written.
	/// </summary>
	public static uint GetClassicLocalLights(float[] into)
	{
		if (_localLightsMissing || TryGetAbiVersion() is null || !IsGuestUp)
		{
			return 0;
		}
		try
		{
			return RefiiHostGetClassicLocalLights(into, (uint)into.Length);
		}
		catch (EntryPointNotFoundException)
		{
			_localLightsMissing = true;
			return 0;
		}
	}

	private static bool _shadowSpheresMissing;

	/// <summary>Floats per sphere of <see cref="GetClassicShadowSpheres"/>.</summary>
	public const int ClassicShadowSphereStride = 8;

	/// <summary>
	/// The render scene's shadow spheres, 8 floats each: previous centre and radius, newest
	/// centre and radius (guest axes). Returns the spheres written.
	/// </summary>
	public static uint GetClassicShadowSpheres(float[] into)
	{
		if (_shadowSpheresMissing || TryGetAbiVersion() is null || !IsGuestUp)
		{
			return 0;
		}
		try
		{
			return RefiiHostGetClassicShadowSpheres(into, (uint)into.Length);
		}
		catch (EntryPointNotFoundException)
		{
			_shadowSpheresMissing = true;
			return 0;
		}
	}

	private static bool _fxMeshesMissing;

	/// <summary>Floats per FX mesh in <see cref="GetFxMeshes"/>.</summary>
	public const int FxMeshFloats = 24;

	/// <summary>
	/// The effects' mesh particles the guest states: out[0] the count, then 24 floats each —
	/// descriptor and model hash (bits), flags (0x800 displacement), the twin's world rows (3 x
	/// vec4, translation in w), the tint and alpha the effect states to the twin (model commands
	/// 17 and 16), distance fade (full within, slope, gone beyond), twin type. Returns the floats
	/// written.
	/// </summary>
	public static uint GetFxMeshes(float[] into)
	{
		if (_fxMeshesMissing || TryGetAbiVersion() is null || !IsGuestUp)
		{
			return 0;
		}
		try
		{
			return RefiiHostGetFxMeshes(into, (uint)into.Length);
		}
		catch (EntryPointNotFoundException)
		{
			_fxMeshesMissing = true;
			return 0;
		}
	}

	private static bool _quadsMissing;

	/// <summary>
	/// The render scene's displacement quads (the drunk warp): out[0] the count, then 16 floats
	/// each — x0 y0 x1 y1 (pixels), z, rotation, the corners' uv, the guest's screen size. Returns
	/// the floats written, 0 without a scene or the export.
	/// </summary>
	public static uint GetDisplacementQuads(float[] into)
	{
		if (_quadsMissing || TryGetAbiVersion() is null || !IsGuestUp)
		{
			return 0;
		}
		try
		{
			return RefiiHostGetDisplacementQuads(into, (uint)into.Length);
		}
		catch (EntryPointNotFoundException)
		{
			_quadsMissing = true;
			return 0;
		}
	}

	private static uint[]? _displacementParts;
	private static bool _displacementPartsMissing;

	/// <summary>
	/// A composite character's displacement-only parts (component 17's built records with +0x15,
	/// from GDB DisplacementMeshes): drawn only as distortion. Empty when none.
	/// </summary>
	public static uint[] GetEntityDisplacementParts(uint entity)
	{
		if (_displacementPartsMissing || TryGetAbiVersion() is null)
		{
			return System.Array.Empty<uint>();
		}
		_displacementParts ??= new uint[64];
		uint written;
		try
		{
			written = RefiiHostGetEntityDisplacementParts(entity, _displacementParts, (uint)_displacementParts.Length);
		}
		catch (EntryPointNotFoundException)
		{
			_displacementPartsMissing = true;
			return System.Array.Empty<uint>();
		}
		if (written == 0)
		{
			return System.Array.Empty<uint>();
		}
		var hashes = new uint[written];
		System.Array.Copy(_displacementParts, hashes, written);
		return hashes;
	}

	private static uint[]? _interiorParts;
	private static bool _interiorMissing;

	/// <summary>
	/// The INTERIOR records of a multi-mesh appearance (component 5): the model hashes the guest
	/// marks 0x2000 from the record's GDB `Interior`, which its rain and snow treat as roofs.
	/// Empty when none, or the bridge does not export it.
	/// </summary>
	public static uint[] GetEntityInteriorMeshes(uint entity)
	{
		if (_interiorMissing || TryGetAbiVersion() is null)
		{
			return System.Array.Empty<uint>();
		}
		_interiorParts ??= new uint[64];
		uint written;
		try
		{
			written = RefiiHostGetEntityInteriorMeshes(entity, _interiorParts, (uint)_interiorParts.Length);
		}
		catch (EntryPointNotFoundException)
		{
			_interiorMissing = true;
			return System.Array.Empty<uint>();
		}
		if (written == 0)
		{
			return System.Array.Empty<uint>();
		}
		var hashes = new uint[written];
		System.Array.Copy(_interiorParts, hashes, written);
		return hashes;
	}

	/// <summary>
	/// The meshes a multi-mesh appearance component (id 5) draws, or empty.
	/// </summary>
	/// <remarks>
	/// `GetEntities` reports ONE model hash, from the single-slot appearance components;
	/// component 5 keeps a vector of mesh records instead, so an entity using one reports no
	/// model there. Buildings are the case in point — `… interior` / `… exterior` are two
	/// records of one `MeshRecords` list. The hashes are lower-cased resource-name hashes,
	/// identical in kind to `ModelFile`.
	/// </remarks>
	public static uint[] GetEntityMeshParts(uint entity, out int totalParts)
	{
		totalParts = 0;
		if (TryGetAbiVersion() is null)
		{
			return System.Array.Empty<uint>();
		}

		_meshParts ??= new uint[MaxMorphParts];
		uint written = RefiiHostGetEntityMeshParts(
			entity, _meshParts, MaxMorphParts, out uint total);
		totalParts = (int)total;

		if (written == 0)
		{
			return System.Array.Empty<uint>();
		}

		var hashes = new uint[written];
		System.Array.Copy(_meshParts, hashes, written);
		return hashes;
	}

	/// <summary>
	/// The model hashes a composite character is assembled from, or empty for anything else.
	/// A character whose <c>CECGraphicAppearanceAnimatedMesh</c> states a model hash of zero is
	/// drawn from a LIST on <c>CECGraphicAppearanceMorph</c> (component 17) instead. Returns
	/// that component's BUILT list, the only one carrying WORN CLOTHING — garments are appended
	/// to it from CECAppearanceModifierManager (component 15) and never appear in the source
	/// list. <paramref name="builtParts"/> is that list's length.
	/// </summary>
	public static uint[] GetEntityMorphParts(uint entity, out int builtParts)
	{
		builtParts = 0;
		if (TryGetAbiVersion() is null)
		{
			return System.Array.Empty<uint>();
		}

		_morphParts ??= new uint[MaxMorphParts];
		uint written = RefiiHostGetEntityMorphParts(
			entity, _morphParts, MaxMorphParts, out uint built);
		builtParts = (int)built;

		if (written == 0)
		{
			return System.Array.Empty<uint>();
		}

		var hashes = new uint[written];
		System.Array.Copy(_morphParts, hashes, written);
		return hashes;
	}

	/// <summary>
	/// Parts read per composite character. A layered appearance is a head, a torso, legs, eyes
	/// and accessories — 64 is far past anything the character creator can produce.
	/// </summary>
	private const uint MaxMorphParts = 64;

	private static uint[]? _morphParts;
	private static uint[]? _meshParts;


	/// <summary>The guest's camera, in guest world space.</summary>
	public readonly record struct GuestCamera(
		Vector3 Position, Vector3 Up, Vector3 Forward, float HorizontalFov, bool Flag)
	{
		/// <summary>
		/// True when this is the engine's "no camera" default rather than a live one. There is
		/// no validity bit; the tell is the exact signature the guest writes when it has no
		/// camera — position zero, up +Z, forward +Y, 1.2217305 rad (70°).
		/// </summary>
		public bool IsDefault =>
			Position == Vector3.Zero && Up == new Vector3(0f, 0f, 1f) &&
			Forward == new Vector3(0f, 1f, 0f) && Mathf.IsEqualApprox(HorizontalFov, 1.2217305f);

		/// <summary>
		/// The VERTICAL field of view in degrees, for Godot. The guest stores a HORIZONTAL fov,
		/// and both of its aspect branches collapse to the same vertical angle, so widescreen
		/// widens horizontally only and this needs no aspect input.
		/// </summary>
		public float VerticalFovDegrees =>
			Mathf.RadToDeg(2f * Mathf.Atan(0.75f * Mathf.Tan(HorizontalFov * 0.5f)));
	}

	/// <summary>
	/// Why rigs are not clip-driven: no component bit, component unresolved, controller found,
	/// controller found but empty — and rigs a linked animation posed. See RefiiHostGetAnimWalkStats.
	/// </summary>
	public static (uint NoBit, uint Unresolved, uint Found, uint Empty, uint Linked)? AnimWalkStats()
	{
		if (TryGetAbiVersion() is null || !IsGuestUp)
		{
			return null;
		}

		// Five words from a bridge that counts linked animations, four from one that does not.
		_animStatWords ??= new uint[5];
		uint words = RefiiHostGetAnimWalkStats(_animStatWords, 5u);
		return words >= 4u
			? (_animStatWords[0], _animStatWords[1], _animStatWords[2], _animStatWords[3],
				words >= 5u ? _animStatWords[4] : 0u)
			: null;
	}

	private static uint[]? _animStatWords;

	private static readonly float[] _statedCamera = new float[11];

	/// <summary>
	/// States the camera the shell draws back to the title's bridge, in the guest's block layout,
	/// for the hooks that read it natively (Fable 3's mouse look and <c>Refii.fov</c>).
	/// </summary>
	public static void StateCamera(GuestCamera camera)
	{
		if (TryGetAbiVersion() is null)
		{
			return;
		}

		float[] v = _statedCamera;
		(v[0], v[1], v[2]) = (camera.Position.X, camera.Position.Y, camera.Position.Z);
		(v[3], v[4], v[5]) = (camera.Up.X, camera.Up.Y, camera.Up.Z);
		(v[6], v[7], v[8]) = (camera.Forward.X, camera.Forward.Y, camera.Forward.Z);
		v[9] = camera.HorizontalFov;
		v[10] = camera.Flag ? 1f : 0f;
		RefiiHostStateCamera(v);
	}

	/// <summary>
	/// States the screen and the views drawn this frame to the title's bridge
	/// (<see cref="Refii.Camera.Engine_CameraViews.Words"/>, src/runtime/include/core/camera_views.h), for the
	/// camera mod API's read calls on the guest thread.
	/// </summary>
	public static void StateCameraViews(float[] words)
	{
		if (TryGetAbiVersion() is null)
		{
			return;
		}

		RefiiHostStateCameraViews(words, (uint)words.Length);
	}

	private static readonly float[] _cameraOffset = new float[7];

	/// <summary>
	/// The display offset a mod stated for <paramref name="player"/> (camera_offset): right, up, back in
	/// metres along the drawn camera's axes, then yaw, pitch, roll and fov in degrees; null with none.
	/// </summary>
	public static float[]? GetCameraOffset(int player)
	{
		if (TryGetAbiVersion() is null || RefiiHostGetCameraOffset(player, _cameraOffset) == 0)
		{
			return null;
		}

		return _cameraOffset;
	}

	/// <summary>
	/// The camera statements after sequence <paramref name="after"/>, oldest first, into
	/// <paramref name="words"/> (<see cref="Refii.Camera.Engine_CameraStatements.RecordWords"/> words
	/// each); returns how many records were written. 0 without a bridge.
	/// </summary>
	public static uint GetCameraStatements(uint after, uint[] words)
	{
		uint capacity = (uint)(words.Length / Refii.Camera.Engine_CameraStatements.RecordWords);
		if (capacity == 0u || TryGetAbiVersion() is null)
		{
			return 0u;
		}

		return RefiiHostGetCameraStatements(after, words, capacity);
	}


	private static readonly float[] _holdPromptAt = new float[3];
	private static readonly byte[] _holdPromptTitle = new byte[256];

	/// <summary>
	/// PLAYER TWO'S OWN HOLD PROMPT in local co-op: the guest keeps one hold prompt, player one's;
	/// player two's is kept beside it (coop_hero_hooks.cpp). Its guest world point and title, or
	/// null when they have none.
	/// </summary>
	public static (Vector3 Anchor, string Title)? GetPlayerTwoHoldPrompt()
	{
		if (TryGetAbiVersion() is null || !IsGuestUp)
		{
			return null;
		}

		try
		{
			if (RefiiHostGetPlayerTwoHoldPrompt(_holdPromptAt, _holdPromptTitle, (uint)_holdPromptTitle.Length) == 0)
			{
				return null;
			}
		}
		catch (EntryPointNotFoundException)
		{
			return null;
		}

		int length = Array.IndexOf(_holdPromptTitle, (byte)0);
		string title = Encoding.UTF8.GetString(_holdPromptTitle, 0, length < 0 ? _holdPromptTitle.Length : length);
		return (new Vector3(_holdPromptAt[0], _holdPromptAt[1], _holdPromptAt[2]), title);
	}

	private static readonly byte[] _promptTitles = new byte[8192];

	/// <summary>
	/// Local co-op: the use-prompt text the player the guest's prompt is NOT for would be shown,
	/// per thing — (thing, player 1 or 2, title). Read into <paramref name="into"/>, cleared first.
	/// </summary>
	public static void GetPromptTitles(Dictionary<uint, (int Player, string Title)> into)
	{
		into.Clear();
		if (TryGetAbiVersion() is null || !IsGuestUp)
		{
			return;
		}

		uint written;
		try
		{
			written = RefiiHostGetPromptTitles(_promptTitles, (uint)_promptTitles.Length);
		}
		catch (EntryPointNotFoundException)
		{
			return;
		}

		if (written == 0 || written >= _promptTitles.Length)
		{
			return;
		}

		foreach (string line in Encoding.UTF8.GetString(_promptTitles, 0, (int)written).Split('\n'))
		{
			string[] parts = line.Split('\t');
			if (parts.Length == 3 && uint.TryParse(parts[0], out uint thing) && int.TryParse(parts[1], out int player))
			{
				into[thing] = (player, parts[2]);
			}
		}
	}

	/// <summary>
	/// One of a player's properties: the building's UID, its BuildingName tag, flags (bit 0 marital
	/// home, 1 rented out, 2 dungeon, 3 home, 4 tenants, 5 workers, 6 shared with the holder's
	/// partner, 7 held by that partner), rent per drop, value, condition 0..1, a full repair's bill and what
	/// it was bought for — as the guest's own calls stated the owner's list (RefiiHostGetProperties).
	/// </summary>
	public readonly record struct Property(
		ulong Uid, string NameTag, uint Flags, int Income, int Value, float Condition, int RepairCost,
		int BoughtFor = 0)
	{
		public bool IsMarital => (Flags & 1u) != 0u;
		public bool HasWorkers => (Flags & 32u) != 0u;
		public bool IsHome => (Flags & 8u) != 0u;
		public bool IsRented => (Flags & 2u) != 0u;
		public bool HasTenants => (Flags & 16u) != 0u;
		public bool IsShared => (Flags & 64u) != 0u;

		/// <summary>The partner's own purchase, shared with this hero: its record is the partner's.</summary>
		public bool IsPartners => (Flags & 128u) != 0u;
	}



	/// <summary>A player's store value (the same on both co-op machines), or null when unset.</summary>
	public static float? GetPlayerValue(int player, string key)
	{
		if (TryGetAbiVersion() is null)
		{
			return null;
		}

		try
		{
			return RefiiHostGetPlayerValue(player, key, out float value) != 0 ? value : null;
		}
		catch (EntryPointNotFoundException)
		{
			return null;
		}
	}

	/// <summary>A player's store TEXT (at most 256 bytes, kept exactly), or null when the key holds none.</summary>
	public static string? GetPlayerText(int player, string key)
	{
		if (TryGetAbiVersion() is null)
		{
			return null;
		}

		try
		{
			byte[] text = new byte[260];
			int length = RefiiHostGetPlayerText(player, key, text, (uint)text.Length);
			return length < 0 ? null : Encoding.UTF8.GetString(text, 0, Math.Min(length, text.Length - 1));
		}
		catch (EntryPointNotFoundException)
		{
			return null;
		}
	}

	/// <summary>Writes a player's session TEXT (never saved; the same on both co-op machines).</summary>
	public static bool SetPlayerText(int player, string key, string text)
	{
		if (TryGetAbiVersion() is null)
		{
			return false;
		}

		try
		{
			return RefiiHostSetPlayerText(player, key, text) != 0;
		}
		catch (EntryPointNotFoundException)
		{
			return false;
		}
	}

	/// <summary>Writes a SAVED value into a player's store (kept with that player's save).</summary>
	public static bool SetPlayerValue(int player, string key, float value)
	{
		if (TryGetAbiVersion() is null)
		{
			return false;
		}

		try
		{
			return RefiiHostSetPlayerValue(player, key, value) != 0;
		}
		catch (EntryPointNotFoundException)
		{
			return false;
		}
	}

	/// <summary>The game's FNV-1 of a name: a GDB record's hash (refii_mod_fnv1).</summary>
	public static uint Fnv1(string name)
	{
		uint hash = 0x811C9DC5u;
		foreach (byte c in Encoding.UTF8.GetBytes(name))
		{
			hash = unchecked(hash * 0x01000193u) ^ c;
		}

		return hash;
	}

	/// <summary>An entity's network id (the guest's 64-bit UID), or 0.</summary>
	public static ulong GetEntityUid(uint entity)
	{
		if (entity == 0u || TryGetAbiVersion() is null || !IsGuestUp)
		{
			return 0ul;
		}

		try
		{
			return RefiiHostGetEntityUid(entity);
		}
		catch (EntryPointNotFoundException)
		{
			return 0ul;
		}
	}

	/// <summary>Whether the guest still wants the movie the host is showing.</summary>
	public enum MoviePlayback { None = 0, Wanted = 1, EndedByGuest = 2 }

	/// <summary>
	/// The guest's view of the movie in progress. Either side can end one: the host by running
	/// out of frames, the guest by finishing its own decode or by skipping.
	/// </summary>
	public static MoviePlayback MovieState() => (MoviePlayback)RefiiHostMovieState();

	/// <summary>
	/// One world-space icon the guest would have drawn through its renderer — property sign,
	/// income, gift gold, opinion splash, quest marker, dog point. `Anchor` is in the guest's
	/// world space; `Colour` carries the fade; `Scale` is the guest's own tween.
	/// </summary>
	/// <param name="Pooled">A pooled HUD-element icon (a prompt), not a renderer sprite: the HUD
	/// element draws it; only the world point is taken, into <c>WorldIcon{Slot+1}</c>.</param>
	public readonly record struct WorldSprite(
		uint Type, uint TextureHash, Color Colour, uint Entity, bool Persistent, bool Fading,
		Vector3 Anchor, float Scale, float SecondsLeft, float Age, string Title, string Opinion,
		bool Pooled = false, int Slot = -1, uint Object = 0, bool PlayerTwo = false);

	private const int SpriteWords = 8;

	/// <summary>A town of owned houses and admirers holds well over 64 at once (53 measured
	/// with four houses in view), and an icon past the cap is simply never drawn.</summary>
	private const int MaxSprites = 256;

	private static readonly uint[] _spriteWords = new uint[MaxSprites * SpriteWords];
	private static readonly float[] _spriteValues = new float[MaxSprites * 6];
	private static readonly byte[] _spriteText = new byte[32768];
	private static readonly List<WorldSprite> _sprites = new();
	private static readonly ByteStrings SpriteTexts = new(Encoding.UTF8);

	/// <remarks>The list is reused: it is valid until the next call.</remarks>
	public static List<WorldSprite> GetWorldSprites()
	{
		List<WorldSprite> sprites = _sprites;
		sprites.Clear();
		if (TryGetAbiVersion() is null || !IsGuestUp)
		{
			return sprites;
		}

		uint count;
		try
		{
			count = RefiiHostGetWorldSprites(
				_spriteWords, (uint)_spriteWords.Length, _spriteValues, (uint)_spriteValues.Length,
				_spriteText, (uint)_spriteText.Length, (uint)MaxSprites);
		}
		catch (EntryPointNotFoundException)
		{
			return sprites;
		}

		int textAt = 0;
		string Next()
		{
			int start = textAt;
			while (textAt < _spriteText.Length && _spriteText[textAt] != 0)
			{
				++textAt;
			}

			string value = SpriteTexts.Get(_spriteText, start, textAt - start);
			if (textAt < _spriteText.Length)
			{
				++textAt;
			}

			return value;
		}

		for (int i = 0; i < count; ++i)
		{
			int w = i * SpriteWords;
			uint rgba = _spriteWords[w + 2];
			var colour = new Color(
				((rgba >> 24) & 0xFF) / 255f, ((rgba >> 16) & 0xFF) / 255f,
				((rgba >> 8) & 0xFF) / 255f, (rgba & 0xFF) / 255f);
			string title = Next();
			string opinion = Next();
			uint flags = _spriteWords[w + 5];
			sprites.Add(new WorldSprite(
				_spriteWords[w], _spriteWords[w + 1], colour, _spriteWords[w + 3],
				_spriteWords[w + 4] != 0, (flags & 1) != 0,
				new Vector3(_spriteValues[i * 6], _spriteValues[i * 6 + 1], _spriteValues[i * 6 + 2]),
				_spriteValues[i * 6 + 3], _spriteValues[i * 6 + 4], _spriteValues[i * 6 + 5],
				title, opinion,
				Pooled: (flags & 2) != 0,
				Slot: _spriteWords[w + 6] == 0xFFFFFFFFu ? -1 : (int)_spriteWords[w + 6],
				Object: _spriteWords[w + 7],
				PlayerTwo: (flags & 4) != 0));
		}

		return sprites;
	}

	/// <summary>
	/// Where the guest wants the live movie drawn, in its own 2D screen space — the rect it
	/// states right after every open and builds its own quad from. Null when no movie is live.
	/// </summary>
	/// <remarks>
	/// The fullscreen-vs-embedded answer, stated at OPEN: the fullscreen owners state the screen
	/// rect, the UI sprite owner states its element's.
	/// </remarks>
	public static Rect2? GetMovieRect()
	{
		if (TryGetAbiVersion() is null ||
			RefiiHostGetMovieRect(out float x0, out float y0, out float x1, out float y1) == 0)
		{
			return null;
		}

		return new Rect2(x0, y0, x1 - x0, y1 - y0);
	}

	/// <summary>
	/// The guest's front-end (title/main-menu) state object, or 0 while it is still
	/// booting. The guest builds it only once every startup logo has played and its bank
	/// mounter has finished, so a non-zero value is the guest saying it has reached the
	/// front end — rather than the host inferring it from a movie ending.
	/// </summary>
	public static uint FrontEndState() => RefiiHostGetFrontEndState();

	/// <summary>Reads one NUL-terminated UTF-8 string out of a packed buffer.</summary>
	internal static string ReadUtf8(byte[] buffer, int start, out int next)
	{
		if (start < 0 || start >= buffer.Length)
		{
			next = buffer.Length;
			return string.Empty;
		}

		int end = start;
		while (end < buffer.Length && buffer[end] != 0)
		{
			++end;
		}

		next = end + 1;
		return end > start
			? System.Text.Encoding.UTF8.GetString(buffer, start, end - start)
			: string.Empty;
	}

	/// <summary>
	/// The screen the guest's screen setter says is up and the screen each live presentation was
	/// loaded for, as the guest states them. Null outside the game.
	/// </summary>
	/// <remarks>
	/// Asked several times a frame; rebuilt only when the stated list's generation moves.
	/// </remarks>
	public static InGameScreens? GetInGameScreens()
	{
		if (TryGetAbiVersion() is null || !IsInGame)
		{
			_screenCache = null;
			return null;
		}

		uint generation = PresentationsGeneration();
		if (_screenCache is { } cached && generation == _screenGeneration)
		{
			return cached;
		}

		var owned = new System.Collections.Generic.Dictionary<uint, int>();
		foreach (Presentation p in GetPresentations())
		{
			if (p.Screen >= 0 && (_screenOnPeer == 0u || (uint)p.Screen != _screenOnPeer))
			{
				owned[p.Address] = p.Screen;
			}
		}

		uint stated = RefiiHostStatedScreen();
		if (_screenOnPeer != 0u && stated == _screenOnPeer)
		{
			stated = NoStatedScreen;
		}

		var built = new InGameScreens(
			stated == NoStatedScreen ? InGameScreen.None : (InGameScreen)stated, owned);
		_screenGeneration = generation;
		_screenCache = built;
		return built;
	}

	/// <summary>What <c>RefiiHostStatedScreen</c> answers when the guest says no screen is up.</summary>
	private const uint NoStatedScreen = 0xFFFFFFFFu;

	[System.ThreadStatic]
	private static uint _screenGeneration;

	[System.ThreadStatic]
	private static InGameScreens? _screenCache;

	/// <summary>
	/// Whether the guest is in game, as the guest states it — <c>byte_83496A5E</c>, set by
	/// Game_InitDatabases (0x82311648) and cleared by the game mode's shutdown (0x82311860).
	/// Use this rather than inferring it from the front end disappearing.
	/// </summary>
	public static bool IsInGame => RefiiHostIsInGame() != 0;


	/// <summary>
	/// The two players' live entity addresses (key 1 and key 2: the entity manager's own slots first, the player map second), 0 for
	/// a slot that has no entity. ABI 133 — so the entity feed can name player two's rig.
	/// </summary>
	public static (uint One, uint Two) CoopPlayerEntities()
	{
		if (TryGetAbiVersion() is null || !IsGuestUp)
		{
			return (0u, 0u);
		}

		RefiiHostGetCoopPlayerEntities(out uint one, out uint two);
		return (one, two);
	}

	/// <summary>
	/// Sends one message to the co-op peer over Refii's own channel — port 0xEF11, carrying host
	/// state the title has no concept of. The guest wire is untouched.
	///
	/// Delivery is unreliable BY DESIGN, so send idempotent STATE ("player two's body is X"),
	/// restated while it matters, never a one-shot event. A lost datagram then costs one
	/// restatement instead of a desync.
	/// </summary>
	public static bool CoopSend(string message)
	{
		if (string.IsNullOrEmpty(message) || TryGetAbiVersion() is null || !IsGuestUp)
		{
			return false;
		}

		try
		{
			return RefiiNetCoopSend(message) != 0;
		}
		catch (DllNotFoundException)
		{
			return false;
		}
		catch (EntryPointNotFoundException)
		{
			return false;
		}
	}

	/// <summary>
	/// The players' mod values this machine owes the co-op peer (core/player_state.h), one channel
	/// message each; empty when nothing is due. Call every frame: the bridge also learns the save
	/// root here. Send them only inside a session.
	/// </summary>
	public static string[] PlayerStateOutgoing()
	{
		if (TryGetAbiVersion() is null || !IsGuestUp)
		{
			return System.Array.Empty<string>();
		}

		uint written;
		try
		{
			written = RefiiHostPlayerStateOutgoing(PlayerStateBuffer, (uint)PlayerStateBuffer.Length);
		}
		catch (DllNotFoundException)
		{
			return System.Array.Empty<string>();
		}
		catch (EntryPointNotFoundException)
		{
			return System.Array.Empty<string>();
		}

		return written == 0 || written > PlayerStateBuffer.Length
			? System.Array.Empty<string>()
			: System.Text.Encoding.UTF8.GetString(PlayerStateBuffer, 0, (int)written)
				.Split('\n', System.StringSplitOptions.RemoveEmptyEntries);
	}

	/// <summary>One co-op channel message offered to the player state; true when it was one (taken).</summary>
	public static bool PlayerStateReceive(string message)
	{
		if (!message.StartsWith("ps.", System.StringComparison.Ordinal) || TryGetAbiVersion() is null || !IsGuestUp)
		{
			return false;
		}

		byte[] bytes = System.Text.Encoding.UTF8.GetBytes(message);
		try
		{
			return RefiiHostPlayerStateReceive(bytes, (uint)bytes.Length) != 0;
		}
		catch (DllNotFoundException)
		{
			return false;
		}
		catch (EntryPointNotFoundException)
		{
			return false;
		}
	}

	/// <summary>Who this machine plays (core/player_state): 3 both, 1 player one (online host), 2 player two (online joiner); 3 without a payload.</summary>
	public static uint PlayerOwned()
	{
		if (TryGetAbiVersion() is null)
		{
			return 3u;
		}

		try
		{
			return RefiiHostPlayerOwned();
		}
		catch (DllNotFoundException)
		{
			return 3u;
		}
		catch (EntryPointNotFoundException)
		{
			return 3u;
		}
	}

	/// <summary>Both players' mod values and their owners as one line, or empty.</summary>
	public static string PlayerStateDescribe()
	{
		if (TryGetAbiVersion() is null || !IsGuestUp)
		{
			return string.Empty;
		}

		try
		{
			uint written = RefiiHostPlayerStateDescribe(PlayerStateBuffer, (uint)PlayerStateBuffer.Length);
			return written == 0 || written > PlayerStateBuffer.Length
				? string.Empty
				: System.Text.Encoding.UTF8.GetString(PlayerStateBuffer, 0, (int)written);
		}
		catch (DllNotFoundException)
		{
			return string.Empty;
		}
		catch (EntryPointNotFoundException)
		{
			return string.Empty;
		}
	}


	/// <summary>
	/// The silver keys in the world a player has already taken (core/coop_collectables.cpp): entity
	/// address → mask (bit 0 player one, bit 1 player two). Filled into <paramref name="into"/>.
	/// </summary>
	public static void CollectedKeys(System.Collections.Generic.Dictionary<uint, uint> into)
	{
		into.Clear();
		if (TryGetAbiVersion() is null || !IsGuestUp)
		{
			return;
		}

		try
		{
			uint pairs = RefiiHostGetCollectedKeys(CollectedKeysBuffer, (uint)CollectedKeysBuffer.Length);
			for (uint i = 0; i < pairs; ++i)
			{
				into[CollectedKeysBuffer[i * 2]] = CollectedKeysBuffer[(i * 2) + 1];
			}
		}
		catch (DllNotFoundException)
		{
		}
		catch (EntryPointNotFoundException)
		{
		}
	}

	private static readonly uint[] CollectedKeysBuffer = new uint[512];

	/// <summary>
	/// The live presentation the guest made from <paramref name="file"/> (a `.bgf` name without
	/// its extension), the newest when it made several; 0 when none is live. From the stated list.
	/// </summary>
	public static uint LivePresentation(string file)
	{
		if (TryGetAbiVersion() is null)
		{
			return 0;
		}

		uint generation = PresentationsGeneration();
		if (_liveByFile is null || generation != _liveByFileGeneration)
		{
			var built = new Dictionary<string, (uint Address, uint Serial)>(StringComparer.OrdinalIgnoreCase);
			foreach (Presentation p in GetPresentations())
			{
				string name = System.IO.Path.GetFileNameWithoutExtension(p.Name.Replace('\\', '/'));
				if (!built.TryGetValue(name, out var have) || p.Serial > have.Serial)
				{
					built[name] = (p.Address, p.Serial);
				}
			}

			_liveByFile = built;
			_liveByFileGeneration = generation;
		}

		return _liveByFile.TryGetValue(file, out var found) ? found.Address : 0u;
	}

	[ThreadStatic]
	private static Dictionary<string, (uint Address, uint Serial)>? _liveByFile;

	[ThreadStatic]
	private static uint _liveByFileGeneration;

	/// <summary>
	/// Takes the texts the guest wrote into its presentations' elements (`SetText` /
	/// `SetTextFromTag`, localised): four words each — presentation, root name hash, ordinal,
	/// UTF-16 length — the units back to back in <paramref name="units"/>. Returns how many.
	/// </summary>
	public static int TakeToldTexts(uint[] words, ushort[] units)
	{
		if (TryGetAbiVersion() is null || !IsGuestUp)
		{
			return 0;
		}

		try
		{
			return (int)RefiiHostTakeToldTexts(words, (uint)words.Length, units, (uint)units.Length);
		}
		catch (DllNotFoundException)
		{
			return 0;
		}
		catch (EntryPointNotFoundException)
		{
			return 0;
		}
	}

	/// <summary>
	/// A headless presentation's file, as its create named it (hooks/gui_headless_presentation.cpp):
	/// the key its statements carry is (presentation, <paramref name="serial"/> in the root-hash word,
	/// <c>0x80000000 | id</c>). Null when <paramref name="serial"/> is not the presentation there.
	/// </summary>
	public static string? ToldHeadlessFile(uint presentation, uint serial)
	{
		if (TryGetAbiVersion() is null || !IsGuestUp)
		{
			return null;
		}

		try
		{
			var buffer = new byte[512];
			uint length = RefiiHostToldHeadlessFile(presentation, serial, buffer, (uint)buffer.Length);
			return length == 0 ? null : Encoding.UTF8.GetString(buffer, 0, (int)length);
		}
		catch (DllNotFoundException)
		{
			return null;
		}
		catch (EntryPointNotFoundException)
		{
			return null;
		}
	}

	/// <summary>One element of a headless presentation by its id: its parent's id (0 the root) and name hash.</summary>
	public static bool ToldHeadlessElement(uint presentation, uint serial, uint id, out uint parentId, out uint nameHash)
	{
		parentId = 0;
		nameHash = 0;
		if (TryGetAbiVersion() is null || !IsGuestUp)
		{
			return false;
		}

		try
		{
			return RefiiHostToldHeadlessElement(presentation, serial, id, out parentId, out nameHash) != 0;
		}
		catch (DllNotFoundException)
		{
			return false;
		}
		catch (EntryPointNotFoundException)
		{
			return false;
		}
	}

	/// <summary>
	/// Takes the statements the guest told its presentations' elements (hooks/gui_element_hooks.cpp),
	/// oldest first, six words each — kind (1 slide entry, 2 paused, 3 attribute, 4 active),
	/// presentation, root name hash, ordinal, a, b. Returns how many records were written.
	/// </summary>
	public static int TakeToldStatements(uint[] into)
	{
		if (TryGetAbiVersion() is null || !IsGuestUp)
		{
			return 0;
		}

		try
		{
			return (int)RefiiHostTakeToldStatements(into, (uint)into.Length);
		}
		catch (DllNotFoundException)
		{
			return 0;
		}
		catch (EntryPointNotFoundException)
		{
			return 0;
		}
	}

	/// <summary>Staging for the player state's messages; main thread only.</summary>
	private static readonly byte[] PlayerStateBuffer = new byte[16384];

	/// <summary>
	/// Takes the oldest message the peer sent, or null when nothing is waiting. Drain it every
	/// frame — the inbox holds 32 and drops the oldest.
	/// </summary>
	public static string? CoopReceive()
	{
		if (TryGetAbiVersion() is null || !IsGuestUp)
		{
			return null;
		}

		uint written;
		try
		{
			written = RefiiNetCoopReceive(CoopInbox, (uint)CoopInbox.Length);
		}
		catch (DllNotFoundException)
		{
			return null;
		}
		catch (EntryPointNotFoundException)
		{
			return null;
		}

		return written == 0 || written > CoopInbox.Length
			? null
			: System.Text.Encoding.UTF8.GetString(CoopInbox, 0, (int)written);
	}

	/// <summary>The state of Refii's mod sync stream (ABI 217).</summary>
	public enum SyncStreamState
	{
		Idle = 0,
		Connecting = 1,
		Open = 2,
		ClosedByPeer = 3,
		ConnectFailed = 4,
		Overflowed = 5,
	}

	/// <summary>
	/// Opens the mod sync stream to the co-op peer — the guest TCP shim's reliable, ordered
	/// rexnet stream on Refii's own port — for bulk bytes the datagram channel is the wrong shape
	/// for. True when requested or already up; the outcome is <see cref="SyncStreamStatus"/>.
	/// </summary>
	public static bool SyncStreamConnect()
	{
		if (TryGetAbiVersion() is null || !IsGuestUp)
		{
			return false;
		}

		try
		{
			return RefiiNetSyncConnect() != 0;
		}
		catch (DllNotFoundException)
		{
			return false;
		}
		catch (EntryPointNotFoundException)
		{
			return false;
		}
	}

	public static SyncStreamState SyncStreamStatus()
	{
		if (TryGetAbiVersion() is null || !IsGuestUp)
		{
			return SyncStreamState.Idle;
		}

		try
		{
			return (SyncStreamState)RefiiNetSyncState();
		}
		catch (DllNotFoundException)
		{
			return SyncStreamState.Idle;
		}
		catch (EntryPointNotFoundException)
		{
			return SyncStreamState.Idle;
		}
	}

	/// <summary>Writes <paramref name="length"/> bytes of <paramref name="data"/> to the open stream.</summary>
	public static bool SyncStreamSend(byte[] data, int length)
	{
		if (length <= 0 || length > data.Length || TryGetAbiVersion() is null || !IsGuestUp)
		{
			return false;
		}

		try
		{
			return RefiiNetSyncSend(data, (uint)length) != 0;
		}
		catch (DllNotFoundException)
		{
			return false;
		}
		catch (EntryPointNotFoundException)
		{
			return false;
		}
	}

	/// <summary>Takes received bytes into <paramref name="buffer"/>, in order; the count taken.</summary>
	public static int SyncStreamReceive(byte[] buffer)
	{
		if (TryGetAbiVersion() is null || !IsGuestUp)
		{
			return 0;
		}

		try
		{
			uint taken = RefiiNetSyncReceive(buffer, (uint)buffer.Length);
			return taken > buffer.Length ? 0 : (int)taken;
		}
		catch (DllNotFoundException)
		{
			return 0;
		}
		catch (EntryPointNotFoundException)
		{
			return 0;
		}
	}

	public static void SyncStreamClose()
	{
		if (TryGetAbiVersion() is null)
		{
			return;
		}

		try
		{
			RefiiNetSyncClose();
		}
		catch (DllNotFoundException)
		{
		}
		catch (EntryPointNotFoundException)
		{
		}
	}

	/// <summary>
	/// The co-op inbox staging buffer. Must be LARGER THAN ANY MESSAGE THE KIT SENDS
	/// (WireLimit 1100): a truncated inventory chunk is refused by the decoder.
	/// </summary>
	/// <remarks>
	/// One buffer for the process, since the UI drive drains this every frame. Safe to share:
	/// the string is decoded before returning, and every caller is on Godot's main thread.
	/// </remarks>
	private static readonly byte[] CoopInbox = new byte[4096];

	/// <summary>
	/// The guest's simulation tick — <c>dword_83319650</c>, incremented by the world update.
	/// Compare successive values for INEQUALITY only; the difference is not a count of ticks.
	/// </summary>
	public static uint SimTick => RefiiHostGetSimTick();

	/// <summary>How the guest's time base is driven.</summary>
	public enum TimeBaseMode
	{
		/// <summary>The real host clock. Original behaviour, and it tears under load.</summary>
		Off = 0,

		/// <summary>Cap the advance between reads on a thread, so the guest never falls behind.</summary>
		Paced = 1,

		/// <summary>Time advances only when Godot grants it. The guest's rate becomes Godot's.</summary>
		Driven = 2,
	}

	/// <summary>
	/// Sets how the guest's time base is driven, and reads the counters back.
	/// </summary>
	/// <remarks>
	/// The guest's in-game loop runs its own wall-clock accumulator and, when it falls behind,
	/// advances ONE step and re-bases — discarding the backlog (0x82276FA4). Godot does not gate
	/// that loop. <see cref="TimeBaseMode.Driven"/> steps the guest at the host's rate,
	/// deterministically.
	///
	/// `Watchdog` counts lapses where no grant arrived and time resumed on its own so guest
	/// threads on timed waits did not freeze. It should stay flat while the game is stepping.
	/// </remarks>
	public static (TimeBaseMode Mode, ulong Capped, ulong Granted, ulong Watchdog)? TimeBase(
		TimeBaseMode? set = null)
	{
		try
		{
			int mode = RefiiHostSetTimeBaseMode(
				set is { } wanted ? (int)wanted : -1,
				out ulong capped, out ulong granted, out ulong watchdog);
			return ((TimeBaseMode)mode, capped, granted, watchdog);
		}
		catch (System.DllNotFoundException)
		{
			return null;
		}
		catch (System.EntryPointNotFoundException)
		{
			return null;
		}
	}

	/// <summary>
	/// Unpacks the packed 64-bit <c>out_watchdog</c> word returned by <c>RefiiHostSetTimeBaseMode</c>:
	/// bits [31:0] hold watchdog lapses, bits [62:32] hold unconsumed HF steps (<c>pending + in_flight</c>),
	/// and bit [63] indicates whether an HF step is currently executing on the guest thread.
	/// </summary>
	/// <param name="watchdog">Raw packed 64-bit watchdog telemetry word.</param>
	/// <param name="lapses">Watchdog lapse count.</param>
	/// <param name="unconsumedSteps">Authoritative queued plus in-flight HF step count.</param>
	/// <param name="stepInFlight">True when a released HF step has not yet finished its pass.</param>
	public static void UnpackTimeBaseWatchdog(
		ulong watchdog,
		out ulong lapses,
		out ulong unconsumedSteps,
		out bool stepInFlight)
	{
		lapses = watchdog & 0xFFFFFFFFUL;
		unconsumedSteps = (watchdog >> 32) & 0x7FFFFFFFUL;
		stepInFlight = (watchdog & (1UL << 63)) != 0UL;
	}

	private static bool TryQueryTimeBaseMode(int queryMode, out int activeMode, out ulong watchdog)
	{
		if (TryGetAbiVersion() is not null)
		{
			try
			{
				activeMode = RefiiHostSetTimeBaseMode(queryMode, out _, out _, out watchdog);
				return true;
			}
			catch (System.DllNotFoundException)
			{
			}
			catch (System.EntryPointNotFoundException)
			{
			}
		}

		activeMode = 0;
		watchdog = 0UL;
		return false;
	}

	/// <summary>
	/// Reads the native driven time-base's authoritative unconsumed high-frequency step depth
	/// (<c>pending + in_flight</c>) without allocating or boxing on the hot per-frame pacing path.
	/// </summary>
	/// <param name="unconsumedSteps">Current queued plus in-flight HF steps when Driven mode is active.</param>
	/// <returns>True when native Driven time-base telemetry is active and readable; otherwise false.</returns>
	public static bool TryGetUnconsumedTimeBaseSteps(out ulong unconsumedSteps)
	{
		if (TryQueryTimeBaseMode(-1, out int mode, out ulong watchdog) && mode == (int)TimeBaseMode.Driven)
		{
			UnpackTimeBaseWatchdog(watchdog, out _, out unconsumedSteps, out _);
			return true;
		}

		unconsumedSteps = 0UL;
		return false;
	}

	/// <summary>
	/// Enters the host per-frame guest-memory read window (<c>mode == -2</c>), preventing the
	/// native driven time base from popping queued multi-step grants during <c>_Process</c>, and
	/// reports whether an HF step is already executing on the guest thread.
	/// </summary>
	/// <param name="stepInFlight">True when a released HF step has not yet completed its pass.</param>
	/// <returns>True when native Driven time-base telemetry is active and readable; otherwise false.</returns>
	public static bool TryBeginTimeBaseReadWindow(out bool stepInFlight)
	{
		if (TryQueryTimeBaseMode(-2, out int mode, out ulong watchdog) && mode == (int)TimeBaseMode.Driven)
		{
			UnpackTimeBaseWatchdog(watchdog, out _, out _, out stepInFlight);
			return true;
		}

		stepInFlight = false;
		return false;
	}

	/// <summary>
	/// Exits the host per-frame guest-memory read window (<c>mode == -3</c>) so any queued HF
	/// steps held back across <c>_Process</c> can resume releasing cleanly.
	/// </summary>
	public static void EndTimeBaseReadWindow() => _ = TryQueryTimeBaseMode(-3, out _, out _);

	/// <summary>
	/// Grants the guest one or more high-frequency steps of time. Driven mode only.
	/// </summary>
	/// <remarks>
	/// The guest's loop takes exactly one step per grant and then yields, so this IS its tick.
	/// Called from the session's fixed-step accumulator.
	/// </remarks>
	public static void AdvanceTimeBase(uint steps)
	{
		try
		{
			RefiiHostAdvanceTimeBase(steps);
		}
		catch (System.DllNotFoundException)
		{
		}
		catch (System.EntryPointNotFoundException)
		{
		}
	}

	private static int _timeBaseFrozenState = -1;

	/// <summary>
	/// Freezes or unfreezes the driven guest time base during intentional host holds (pause, load cover)
	/// so the native grant-silence watchdog does not resume real-rate simulation ticks.
	/// </summary>
	public static void SetTimeBaseFrozen(bool frozen)
	{
		int wanted = frozen ? 1 : 0;
		if (_timeBaseFrozenState == wanted)
		{
			return;
		}

		try
		{
			RefiiHostSetTimeBaseFrozen((uint)wanted);
			_timeBaseFrozenState = wanted;
		}
		catch (System.DllNotFoundException)
		{
		}
		catch (System.EntryPointNotFoundException)
		{
		}
	}

	/// <summary>
	/// The guest's OWN simulation clock in seconds, and its per-tick counter — or null when the
	/// world is not up.
	/// </summary>
	/// <remarks>
	/// `sub_82276D10` runs a wall-clock accumulator and stores its clock at `game+0x110`
	/// (0x82276F18). It does NOT catch up: more than one step behind, it advances ONE step and
	/// re-bases to now, discarding the lost time (0x82276FA4..0x82276FC0). So the tick rate is
	/// 15/s while the guest keeps up, lower under load, and it resets discontinuously.
	/// </remarks>
	public static (double Seconds, uint Tick)? SimClock()
	{
		try
		{
			return RefiiHostGetSimClock(out double seconds, out uint tick) != 0u
				? (seconds, tick)
				: null;
		}
		catch (System.DllNotFoundException)
		{
			return null;
		}
		catch (System.EntryPointNotFoundException)
		{
			return null;
		}
	}

	/// <summary>
	/// The most recent simulation clock snapshot read during <see cref="SampleGuestFrame"/>,
	/// avoiding redundant P/Invoke crossings in the same frame-start pass.
	/// </summary>
	public static (double Seconds, uint Tick)? LastSampledSimClock { get; private set; }

	/// <summary>
	/// Maximum duration (in microseconds) a guest-frame sample may be deferred while an in-flight
	/// HF step completes before falling back to a live read.
	/// </summary>
	public const ulong MaxInFlightSampleDeferUsec = 40_000UL;

	private static ulong _lastQuiescentSampleUsec;

	/// <summary>
	/// Returns true when <see cref="SampleGuestFrame"/> should defer reading guest RAM for this
	/// host frame because a guest HF step is actively mutating placements mid-step in game,
	/// preserving the last quiescent snapshot until the step finishes.
	/// </summary>
	/// <param name="isInGame">True when the guest has an active in-game world.</param>
	/// <param name="stepInFlight">True when bit 63 indicates an HF step is currently executing.</param>
	/// <param name="publishedGuestFrame">Current published <see cref="Engine_GuestFrame.Frame"/>.</param>
	/// <param name="nowUsec">Current host monotonic timestamp in microseconds.</param>
	/// <param name="lastQuiescentSampleUsec">Timestamp of the last quiescent guest sample.</param>
	/// <param name="maxDeferUsec">Safety ceiling in microseconds before sampling anyway.</param>
	/// <returns>True if sampling should be deferred so readers interpolate smoothly on existing state.</returns>
	public static bool ShouldDeferInFlightGuestSample(
		bool isInGame,
		bool stepInFlight,
		uint publishedGuestFrame,
		ulong nowUsec,
		ulong lastQuiescentSampleUsec,
		ulong maxDeferUsec = MaxInFlightSampleDeferUsec) =>
		isInGame &&
		stepInFlight &&
		publishedGuestFrame != 0u &&
		lastQuiescentSampleUsec != 0UL &&
		nowUsec >= lastQuiescentSampleUsec &&
		(nowUsec - lastQuiescentSampleUsec) < maxDeferUsec;

	/// <summary>
	/// Samples the guest's render-frame counter into <see cref="Engine_GuestFrame"/>. Once per
	/// host frame, first thing. Without the export (an older native) the frame is synthesised
	/// from the game tick: the tick's change, then a second frame half a tick later, which is
	/// where the guest's second HF iteration falls.
	/// </summary>
	public static void SampleGuestFrame()
	{
		bool hasAbi = TryGetAbiVersion() is not null;
		ulong nowUsec = (ulong)((System.Diagnostics.Stopwatch.GetTimestamp() * 1_000_000.0) /
			System.Diagnostics.Stopwatch.Frequency);
		_ = TryBeginTimeBaseReadWindow(out bool stepInFlight);
		bool inGame = hasAbi && IsInGame;

		if (ShouldDeferInFlightGuestSample(inGame, stepInFlight, Engine_GuestFrame.Frame, nowUsec, _lastQuiescentSampleUsec))
		{
			// An HF step is mid-mutation on the guest thread: keep the last quiescent tick/pose
			// pair and re-publish Changed = false so timelines advance smoothly at display rate.
			Engine_GuestFrame.Publish(Engine_GuestFrame.Frame);
			return;
		}

		_lastQuiescentSampleUsec = inGame ? nowUsec : 0UL;

		uint frame = 0u;
		if (!_guestFrameExportMissing && hasAbi)
		{
			try
			{
				frame = RefiiHostGetGuestFrame();
			}
			catch (EntryPointNotFoundException)
			{
				_guestFrameExportMissing = true;
			}
		}

		// THE FRAME'S STAMP, read in the same instant as the frame counter: every sample this
		// frame reads is stamped from this one pair (Engine_TickClock.FrameStamp), never from a
		// read taken later while the guest may already be in its next step.
		(double Seconds, uint Tick)? simClock = SimClock();
		LastSampledSimClock = simClock;
		uint tick = simClock is { } clock ? clock.Tick : 0u;
		Engine_TickClock.PublishGuestGameTick(tick);
		if (!_guestFrameExportMissing)
		{
			Engine_TickClock.PublishGuestFrame(frame, tick);
		}

		if (_guestFrameExportMissing)
		{
			frame = SyntheticGuestFrame();
		}

		// REBASED TO ZERO WHILE THE GUEST IS OUT OF THE GAME. The counter is advanced by one
		// hook only — the 30 Hz HF pass `sub_821A61B0` of the in-game loop — and the front end
		// has no HF pass, so on a front end reached from a game it holds its last value forever
		// and `Changed` never fires again: every guest-frame-gated drive (the presentation
		// sampler and the backdrop with it, the owned books, the menu capture, the predicates)
		// stopped after Quit to Title. First boot only worked because the counter was still
		// zero, which Publish treats as "no guest state to wait for". Latching the base while
		// `byte_83496A5E` is clear gives the second front end exactly the first one's contract:
		// zero until the first HF pass of the next game, counting from there.
		if (!inGame)
		{
			_guestFrameBase = frame;
		}

		Engine_GuestFrame.Publish(unchecked(frame - _guestFrameBase));
	}

	private static bool _guestFrameExportMissing;
	private static uint _guestFrameBase;
	private static uint _syntheticTick;
	private static double _syntheticTickUsec;
	private static bool _syntheticHalf;

	private static uint SyntheticGuestFrame()
	{
		uint tick = SimTick;
		if (tick == 0u)
		{
			return 0u;
		}

		double now = Godot.Time.GetTicksUsec();
		if (tick != _syntheticTick)
		{
			_syntheticTick = tick;
			_syntheticTickUsec = now;
			_syntheticHalf = false;
		}
		else if (!_syntheticHalf && now - _syntheticTickUsec >= Engine_TickClock.TickUsec * 0.5)
		{
			_syntheticHalf = true;
		}

		return (tick * 2u) + (_syntheticHalf ? 1u : 0u);
	}

	/// <summary>
	/// Words per effect in <see cref="GetActiveFx"/>'s buffer. MUST match the native
	/// <c>kStride</c> — both sides index one flat buffer and a disagreement is an ABI break the
	/// compiler cannot see.
	/// </summary>
	public const int ActiveFxStride = 27;

	/// <summary>
	/// How many live particle NODES are read in one frame. The guest's vector holds every node,
	/// not every effect, so one spell is several entries.
	///
	/// The node count is `(dword_8349E494 - dword_8349E490)/8` and reaches ~500 in ordinary play.
	/// New effects are appended at the END (`sub_82450DB8` writes at `result[2]` and advances
	/// it) and the guest retires by swap-with-last, so a cap below the live count drops the
	/// newest effects and changes which survive every tick.
	///
	/// The native walk refuses a span above 0x40000 bytes (32768 nodes). The walk iterates the
	/// guest's own count, so the cap costs nothing when the guest holds fewer.
	/// </summary>
	private const uint MaxActiveFx = 4096;

	/// <summary>
	/// Whether the last walk filled the buffer, meaning the guest may have had more nodes than
	/// were read, since the walk stops writing at the cap. Callers must not treat an effect
	/// missing from a saturated walk as retired.
	/// </summary>
	public static bool ActiveFxSaturated { get; private set; }

	private static uint[]? _activeFx;

	/// <summary>One live particle node as the guest currently has it.</summary>
	/// <param name="Instance">
	/// The guest pointer, and the only STABLE identity. The guest retires effects by swapping
	/// with the last element and popping, so a node's index changes when an unrelated effect
	/// ends — keying a host node on the index makes effects swap places with each other.
	/// </param>
	/// <param name="NameHash">
	/// FNV-1 of the node's lowercased name, which is the same key the particle bank uses.
	/// </param>
	/// <param name="Alive">The guest's own has-work predicate, <c>sub_8322EB88</c>.</param>
	/// <param name="Emitting">Still spawning, as opposed to running its last particles out.</param>
	/// <param name="Stale">
	/// The guest has written the node's transform but not yet recomposed its cached matrix.
	/// At most one tick old.
	/// </param>
	public readonly record struct ActiveFx(
		uint Instance,

		/// <summary>
		/// The EFFECT INSTANCE this node belongs to. One effect is many nodes, and they all share
		/// this — group on it or the whole effect is drawn once per node.
		/// </summary>
		uint Handle,
		uint NameHash,
		bool Alive,
		bool Emitting,
		bool Stale,
		uint LiveParticles,
		Vector3 Position,
		Vector3[] Rows,

		/// <summary>
		/// The type-vector's synthetic root: its transform is the effect's PLACEMENT, and it
		/// belongs to no effect of its own.
		/// </summary>
		bool Root,

		/// <summary>
		/// The effect instance's intensity, <c>handle+52</c>: 1.0 untouched, driven every tick
		/// by the breadcrumber, baked by the guest into each particle at spawn.
		/// </summary>
		float Intensity,

		/// <summary>
		/// The element's size scale, <c>element+412</c> (Lua SetScale, the spell action's
		/// ParticleScale). Garbage on a node that is not a standard emitter; the consumer
		/// checks the type through the bank first.
		/// </summary>
		float Scale,

		/// <summary>
		/// The element's fade-out fraction, +196/+192 while a kill-with-fade runs, else 1.
		/// </summary>
		float Fade,

		/// <summary>
		/// The entity whose emitter component plays this effect (the vortex entity, the
		/// fireball's shot, a sword's effect entity); 0 for a spell-owned trail or an unknown.
		/// </summary>
		uint Owner = 0,

		/// <summary>The entity the effect's own entity was attached to, or 0.</summary>
		uint Attached = 0,

		/// <summary>
		/// Whether this node owns a guest render twin (<c>CParticleGroup</c> or <c>CTrailGroup</c>),
		/// even when its active quad count is currently 0.
		/// </summary>
		bool HasGuestTwin = false,

		/// <summary>
		/// <see cref="NameHash"/> is the EFFECT's own bank key, not a node type to join through
		/// <see cref="Refii.Fx.FxBank.EffectForType"/>: Fable 3's guest states the effect at its
		/// create (flag 64), where Fable 2's rows are the effect's nodes.
		/// </summary>
		bool NamesEffect = false,

		/// <summary>
		/// The effect's stated colour (red, green, blue, alpha), multiplied into every draw of its
		/// particles: Fable 3's SetColour / SetAlpha (inst+80, copied to each emitter's +400 and
		/// uploaded per draw). White where the title states none.
		/// </summary>
		Color? StatedColour = null,

		/// <summary>Particle flushes stated since the effect's create (Fable 3's opcode 22).</summary>
		uint StatedFlushes = 0,

		/// <summary>The saturation mask is on for the effect's standard emitters (flag 256).</summary>
		bool SaturationMask = false,

		/// <summary>A spline setter stated points for this effect (flag 128): read them with
		/// <see cref="GetFxSpline"/>.</summary>
		bool SplineStated = false,

		/// <summary>A Type 5 <c>CParticleSplineNode</c> carries runtime world-space spline override
		/// endpoints in <see cref="Rows"/>[0..count-1] with count in <see cref="Scale"/> (flag 32).</summary>
		bool SplineOverride = false,

		/// <summary>A <c>Mode == 3</c> double-trail emitter node carries its newest world-space
		/// <c>(EdgeA, EdgeB)</c> endpoints in <see cref="Rows"/>[0..1] (flag 512).</summary>
		bool DoubleTrailSpan = false,

		/// <summary>A guest-driven <c>Mode == 2</c> single-trail emitter node carries its newest
		/// world-space head point in <see cref="Rows"/>[0] (flag 1024).</summary>
		bool SingleTrailHead = false,

		/// <summary>A guest trail ring buffer was cleared/reset (<c>n &lt; 2 || cnt &lt; 2</c>, flag 2048).</summary>
		bool TrailReset = false,

		/// <summary>Packed trail ring metadata <c>(min(n, 0xFFFF) &lt;&lt; 16) | min(cnt, 0xFFFF)</c>.</summary>
		uint TrailRingMeta = 0u,

		/// <summary>Guest trail ring write cursor <c>(head + cnt) &amp; mask</c>.</summary>
		uint TrailRingTail = 0u,

		/// <summary>A <c>Mode == 3</c> double-trail emitter carries a valid guest ring readable via
		/// <see cref="GetFxDoubleTrailRing"/> (flag 4096).</summary>
		bool DoubleTrailRing = false);

	/// <summary>One raw guest-space entry from <see cref="GetFxDoubleTrailRing"/>.</summary>
	public readonly record struct GuestDoubleTrailPoint(Vector3 EdgeA, Vector3 EdgeB, float KeyTime);

	/// <summary>
	/// Every particle effect the guest is currently playing, in world space — the only route
	/// runtime effects have to the screen (level ambients come from the GDB). Safe per frame:
	/// the native side re-reads both ends of the guest's vector together and refuses a span
	/// that is not a whole number of elements.
	/// </summary>
	public static System.Collections.Generic.List<ActiveFx> GetActiveFx() =>
		GetActiveFx(new System.Collections.Generic.List<ActiveFx>());

	/// <summary>The live effects into a caller-owned list, cleared first.</summary>
	public static System.Collections.Generic.List<ActiveFx> GetActiveFx(
		System.Collections.Generic.List<ActiveFx> effects)
	{
		effects.Clear();
		if (TryGetAbiVersion() is null)
		{
			return effects;
		}

		_activeFx ??= new uint[MaxActiveFx * ActiveFxStride];
		uint written = RefiiHostGetActiveFx(_activeFx, MaxActiveFx);
		ActiveFxSaturated = written >= MaxActiveFx;
		if (written == 0)
		{
			return effects;
		}

		for (uint i = 0; i < written; i++)
		{
			int at = (int)i * ActiveFxStride;
			uint flags = _activeFx[at + 2];

			// Raw guest axes: a node standing BESIDE the region needs `(-x, z, y)` and one built
			// INSIDE it needs `(x, z, y)`, so the consumer applies its own map.
			float bx = Bits(_activeFx[at + 4]);
			float by = Bits(_activeFx[at + 5]);
			float bz = Bits(_activeFx[at + 6]);

			bool isRoot = (flags & 8u) != 0;
			bool isSplineOverride = (flags & 32u) != 0;
			bool hasDoubleSpan = (flags & 512u) != 0;
			bool hasSingleHead = (flags & 1024u) != 0;
			bool hasTrailReset = (flags & 2048u) != 0;
			bool hasDoubleRing = (flags & 4096u) != 0;
			effects.Add(new ActiveFx(
				_activeFx[at + 0],
				_activeFx[at + 16],
				_activeFx[at + 1],
				(flags & 1u) != 0,
				(flags & 4u) != 0,
				(flags & 2u) != 0,
				_activeFx[at + 3],
				new Vector3(bx, by, bz),
				GuestRows(_activeFx, at + 7, RowSlot((int)i), normalize: isRoot && !isSplineOverride),
				isRoot,
				Bits(_activeFx[at + 17]),
				Bits(_activeFx[at + 18]),
				Bits(_activeFx[at + 19]),
				_activeFx[at + 20],
				_activeFx[at + 21],
				(flags & 16u) != 0,
				(flags & 64u) != 0,
				new Color(Bits(_activeFx[at + 22]), Bits(_activeFx[at + 23]), Bits(_activeFx[at + 24]), Bits(_activeFx[at + 25])),
				_activeFx[at + 26],
				(flags & 256u) != 0,
				(flags & 128u) != 0,
				SplineOverride: isSplineOverride,
				DoubleTrailSpan: hasDoubleSpan,
				SingleTrailHead: hasSingleHead,
				TrailReset: hasTrailReset,
				TrailRingMeta: (hasDoubleSpan || hasSingleHead) ? _activeFx[at + 13] : 0u,
				TrailRingTail: (hasDoubleSpan || hasSingleHead) ? _activeFx[at + 14] : 0u,
				DoubleTrailRing: hasDoubleRing));
		}

		return effects;
	}

	private static float Bits(uint raw) => BitConverter.Int32BitsToSingle(unchecked((int)raw));

	private static readonly float[] _fxSpline = new float[1536];

	/// <summary>
	/// The points an effect's spline setter last stated (<see cref="ActiveFx.SplineStated"/>), raw
	/// guest world axes like <see cref="ActiveFx.Position"/>, into <paramref name="into"/>; returns
	/// how many (0 when none).
	/// </summary>
	public static int GetFxSpline(uint handle, Span<Vector3> into)
	{
		if (TryGetAbiVersion() is null || into.Length == 0)
		{
			return 0;
		}

		uint max = (uint)Math.Min(into.Length, _fxSpline.Length / 3);
		int count = (int)RefiiHostGetFxSpline(handle, _fxSpline, max);
		for (int i = 0; i < count; i++)
		{
			into[i] = new Vector3(_fxSpline[i * 3], _fxSpline[(i * 3) + 1], _fxSpline[(i * 3) + 2]);
		}

		return count;
	}

	private static readonly float[] _fxDoubleRing = new float[64 * 7];
	private static bool _hasFxDoubleTrailRing = true;

	/// <summary>
	/// Reads the complete guest <c>Mode == 3</c> double-trail ring (<c>(EdgeA, EdgeB, KeyTime)</c>,
	/// oldest-to-newest, raw guest world axes) for <paramref name="nodeOrHandle"/>. Returns how many
	/// valid slots were written (0 when unavailable or fewer than 2 valid slots).
	/// </summary>
	public static int GetFxDoubleTrailRing(uint nodeOrHandle, Span<GuestDoubleTrailPoint> into)
	{
		if (!_hasFxDoubleTrailRing || nodeOrHandle == 0u || TryGetAbiVersion() is null || into.Length < 2)
		{
			return 0;
		}

		uint max = (uint)Math.Min(into.Length, _fxDoubleRing.Length / 7);
		int count;
		try
		{
			count = (int)RefiiHostGetFxDoubleTrailRing(nodeOrHandle, _fxDoubleRing, max);
		}
		catch (EntryPointNotFoundException)
		{
			_hasFxDoubleTrailRing = false;
			return 0;
		}

		if (count < 2)
		{
			return 0;
		}

		if (count > (int)max)
		{
			count = (int)max;
		}

		for (int i = 0; i < count; i++)
		{
			int at = i * 7;
			into[i] = new GuestDoubleTrailPoint(
				new Vector3(_fxDoubleRing[at + 0], _fxDoubleRing[at + 1], _fxDoubleRing[at + 2]),
				new Vector3(_fxDoubleRing[at + 3], _fxDoubleRing[at + 4], _fxDoubleRing[at + 5]),
				_fxDoubleRing[at + 6]);
		}

		return count;
	}

	/// <summary>
	/// The rows array for list slot i, reused across reads: the record is consumed within the
	/// frame it was read and the array is overwritten at the next read of that slot.
	/// </summary>
	private static readonly List<Vector3[]> _rowSlots = new();

	private static Vector3[] RowSlot(int i)
	{
		while (_rowSlots.Count <= i)
		{
			_rowSlots.Add(new Vector3[3]);
		}

		return _rowSlots[i];
	}

	/// <summary>
	/// Rows 0..2 of the guest's world matrix, normalised when <paramref name="normalize"/> is true,
	/// in GUEST axes. Root nodes carry the node's scale (<c>sub_8222E678</c> pre-multiplies each
	/// basis row by its scale lane), hence the normalise; non-root spline override nodes carry
	/// world-space spline endpoints in <c>row[7..15]</c> and must not be normalised.
	/// </summary>
	public static Vector3[] GuestRows(uint[] words, int at, Vector3[] rows, bool normalize = true)
	{
		for (int r = 0; r < 3; r++)
		{
			var row = new Vector3(
				Bits(words[at + (r * 3) + 0]),
				Bits(words[at + (r * 3) + 1]),
				Bits(words[at + (r * 3) + 2]));
			rows[r] = !normalize
				? row
				: (row.LengthSquared() > 1e-8f ? row.Normalized() : Vector3.Zero);
		}

		return rows;
	}

	/// <summary>The guest's live time of day, or null while the world clock does not exist.</summary>
	/// <remarks>
	/// Null must not be substituted with a default: `GameTimeManager` is built at game-database
	/// init and destroyed with the world, so before a level exists there is no clock and callers
	/// hold their last good value. The fraction is in [0, 1) and is the same value the guest's
	/// own day-set selector blends on.
	/// </remarks>
	public static GuestClock? TimeOfDay()
	{
		try
		{
			// IsGuestUp, NOT IsRunning. IsRunning reports false for the whole of in-game play
			// (the debug overlay prints "guest running: False" beside "inGame=True"), so gating
			// on it made this return null every frame and the sky held its load-time sun for
			// the entire session. The native reader validates its own chain by vtable, so an
			// unbuilt world still answers 0.
			if (!IsGuestUp)
			{
				return null;
			}

			if (RefiiHostGetTimeOfDay(out float fraction, out int day, out int frozen,
				out int moonPhase) == 0)
			{
				return null;
			}

			return new GuestClock(fraction, day, frozen != 0, moonPhase);
		}
		catch (DllNotFoundException)
		{
			return null;
		}
	}

	/// <param name="Frozen">
	/// The guest's own pause: non-zero means something holds a stop-time request and the clock
	/// is not advancing.
	/// </param>
	/// <param name="MoonPhase">
	/// The phase the guest states (`Timing.GetMoonPhase`), 0..7 (a frame of the eight-frame
	/// atlas), or -1 when it could not be read.
	/// </param>
	public readonly record struct GuestClock(float Fraction, int DayCount, bool Frozen, int MoonPhase = -1);

	/// <summary>
	/// The guest's live precipitation: the four floats of its BLENDED environment theme that its
	/// own rain and snow draws gate on (0.05 in both). Null until the world is built, and null
	/// again while a value is not a sane float — hold what was last read in that case.
	/// </summary>
	public static GuestWeather? Weather()
	{
		try
		{
			if (!IsGuestUp)
			{
				return null;
			}

			if (RefiiHostGetWeather(out float snowSize, out float snowFallSpeed,
				out float rainSize, out float rainDensity) == 0)
			{
				return null;
			}

			return new GuestWeather(snowSize, snowFallSpeed, rainSize, rainDensity);
		}
		catch (DllNotFoundException)
		{
			return null;
		}
		catch (EntryPointNotFoundException)
		{
			return null;
		}
	}

	/// <summary>Blended theme slots T+0x480, +0x490, +0x4A0, +0x4B0 — see RefiiHostGetWeather.</summary>
	public readonly record struct GuestWeather(
		float SnowSize, float SnowFallSpeed, float RainSize, float RainDensity);

	/// <summary>
	/// One theme the guest has pushed on its environment theme stack: the FNV-1 of its name,
	/// which is its record hash in `environmentthemes.gdb`, and the weight the guest is blending
	/// it at this tick (0..1, rising over the push's blend-in, falling over its fade-out) with
	/// the weight it is heading for.
	/// </summary>
	public readonly record struct GuestThemeLayer(uint RecordHash, float Weight, float Target);

	/// <summary>
	/// The themes on the guest's environment theme stack right now — Time Control's
	/// `EnvThemeSlowTime2`, spell charging, the banshee — see RefiiHostGetThemeStack. Empty at
	/// rest, so a host blending these over its own tone map changes nothing until the guest
	/// states a push.
	/// </summary>
	public static void ThemeStack(System.Collections.Generic.List<GuestThemeLayer> into) =>
		ReadThemeLayers(into, stack: true);

	/// <summary>
	/// The themes scripts have blended in with `EnvironmentTheme.BlendToEnvironmentTheme` — the
	/// Cursed Snowglobe's monochrome, combat failure, XP, the balverine poison — see
	/// RefiiHostGetThemeBlends. The guest folds these as one layer BEFORE the stack. Empty unless
	/// a script states one.
	/// </summary>
	public static void ThemeBlends(System.Collections.Generic.List<GuestThemeLayer> into) =>
		ReadThemeLayers(into, stack: false);

	private static void ReadThemeLayers(System.Collections.Generic.List<GuestThemeLayer> into, bool stack)
	{
		into.Clear();
		try
		{
			if (TryGetAbiVersion() is null || !IsGuestUp)
			{
				return;
			}

			uint[] words = _themeStackWords ??= new uint[ThemeStackMax * 3];
			uint count = stack
				? RefiiHostGetThemeStack(words, ThemeStackMax)
				: RefiiHostGetThemeBlends(words, ThemeStackMax);
			for (int i = 0; i < count; ++i)
			{
				into.Add(new GuestThemeLayer(
					words[i * 3],
					System.BitConverter.Int32BitsToSingle(unchecked((int)words[(i * 3) + 1])),
					System.BitConverter.Int32BitsToSingle(unchecked((int)words[(i * 3) + 2]))));
			}
		}
		catch (DllNotFoundException)
		{
		}
		catch (EntryPointNotFoundException)
		{
		}
	}

	private const uint ThemeStackMax = 16;

	[System.ThreadStatic]
	private static uint[]? _themeStackWords;

	/// <summary>
	/// The address of one component on an entity, by id, or 0 when the entity does not carry it
	/// — the pointer half of <see cref="GetEntityComponents"/>, for the reads that have a
	/// verified layout to apply to it.
	/// </summary>
	public static uint EntityComponentAddress(uint entity, uint id)
	{
		if (entity == 0 || TryGetAbiVersion() is null || !IsGuestUp)
		{
			return 0;
		}

		_componentPairs ??= new uint[MaxComponents * 2];
		uint pairs = RefiiHostGetEntityComponents(entity, _componentPairs, MaxComponents);
		for (int i = 0; i < pairs; ++i)
		{
			if (_componentPairs[i * 2] == id)
			{
				return _componentPairs[(i * 2) + 1];
			}
		}

		return 0;
	}

	/// <summary>
	/// The hero's text suffix as `sub_8230FC40` states it: component 12's word at +12, 2 for
	/// <c>_HM</c> and 1 for <c>_HF</c> (0x8230FDD8..0x8230FE14); empty without a hero or a value.
	/// </summary>
	public static string HeroTextSuffix()
	{
		uint record = EntityComponentAddress(HeroEntityAddress(), 12);
		if (record == 0 || !TryPeekWord(record + 12, out uint sex))
		{
			return string.Empty;
		}

		return sex switch
		{
			2 => "_HM",
			1 => "_HF",
			_ => string.Empty,
		};
	}

	/// <summary>The engine-preloading gate state 7 of CmdLevelChange_Update polls.</summary>
	public readonly record struct RenderFence(
		uint Manager, double Submitted, double Deadline, bool Busy,
		uint ViewCursor, uint ViewSubmitted, uint PreloadState, uint TextureResidency,
		uint PreloadFlag)
	{
		/// <summary>
		/// What <c>sub_82387B38</c> (the guest's own <c>IsEnginePreLoading</c>) answers. The
		/// level load only advances past state 7 when this is false.
		/// </summary>
		public bool BlocksLevelLoad => Submitted <= Deadline || Busy;

		/// <summary>
		/// Which producer is holding the flag, in words. The flag itself carries no state — it
		/// is <c>byte_83496E20</c> re-copied every render-thread iteration — so "busy" alone
		/// names nothing.
		/// </summary>
		public string Holder =>
			ViewCursor == 0xFFFFFFFFu && ViewSubmitted == 0xFFFFFFFFu
				? "STALE — no view submitted, so nothing is recomputing the flag"
				: PreloadState is >= 1 and <= 4
					? $"resource preload (state {PreloadState})"
					: TextureResidency != 0
						? "streamed-texture residency"
						: PreloadFlag != 0
							? "scene walk (self-limiting) or a producer we cannot see"
							: "nothing — the mirror at +600 disagrees with 0x83496E20";
	}

	/// <summary>
	/// How far the guest has got into starting a game: the GDB manager, the in-game flag,
	/// and the two mode singletons. See RefiiHostGetInitMarkers.
	/// </summary>
	public readonly record struct InitMarkers(
		uint GdbManager, bool InGame, uint GameMode, uint FrontEndMode);

	public static InitMarkers GetInitMarkers()
	{
		uint[] words = new uint[4];
		RefiiHostGetInitMarkers(words);
		return new InitMarkers(words[0], words[1] != 0, words[2], words[3]);
	}

	/// <summary>One command-stream ring: the four numbers that decide whether a reserve blocks.</summary>
	public readonly record struct SceneStream(
		uint Object, uint Capacity, uint WriteCursor, uint WriteConsumed,
		uint ReaderConsumed, uint LastRead)
	{
		/// <summary>
		/// sub_8222ABE0's own condition: it spins while the reader is at or ahead of the
		/// writer AND the write would cross it.
		/// </summary>
		public bool WouldBlock(uint bytes) =>
			ReaderConsumed >= WriteCursor && WriteCursor + bytes > ReaderConsumed;
	}

	/// <summary>Where the guest said it is going. Level is empty until it has decided.</summary>
	public readonly record struct StartLevel(string World, string Level, string Entrance);

	/// <summary>
	/// The world/level/entrance the guest asked for, or null before it has asked.
	/// </summary>
	public static StartLevel? GetStartLevel()
	{
		var world = new byte[128];
		var level = new byte[128];
		var entrance = new byte[128];
		if (RefiiHostGetStartLevel(world, level, entrance, (uint)level.Length) == 0)
		{
			return null;
		}

		return new StartLevel(Decode(world), Decode(level), Decode(entrance));
	}

	/// <summary>
	/// The level the guest is standing in. <see cref="Key"/> is the guest's FNV-1 hash of
	/// lowercase "world/level" at Level+0xB4 — one aligned dword, the value to compare frame to
	/// frame, changing on exactly the transitions that reload a level.
	/// </summary>
	public readonly record struct CurrentLevel(
		string World, string Level, string Entrance, uint Key);

	/// <summary>
	/// <c>GetCurrentLevel</c> — where the guest actually is, or null when no level is standing
	/// (also before the first load, and during a transition). Use this, not
	/// <see cref="GetStartLevel"/>, to notice a level change: the start globals are written only
	/// by game-START paths, while a door, a teleporter, fast travel and `World.LoadLevel` all
	/// put their destination on the CmdLevelChange command object and touch no global.
	/// </summary>
	/// <summary>
	/// A level change the guest has queued, as its own command describes it.
	/// <see cref="State"/> is the command's state word (1..9, never 0); the entity teardown
	/// happens at state 2, so state >= 2 is the moment a host may free its scene. When
	/// <see cref="FullLoad"/> is false the guest tears NOTHING down — it is a reposition inside
	/// the resident level — so freeing a scene on it rebuilds a region still loaded.
	/// </summary>
	public readonly record struct LevelChange(
		string World, string Level, string Entrance, uint State, uint TravelType, bool FullLoad);

	/// <summary>
	/// <c>GetLevelChange</c> — the level change in flight, or null when none is. Prefer it to
	/// the falling edge of <see cref="WorldIsReady"/> for deciding when to take a world down:
	/// that byte is not cleared until state 3, after the blocking load has returned, while the
	/// entities were freed back in state 2.
	/// </summary>
	/// <summary>
	/// Holds the guest in its own loading screen while Godot builds a region, by answering its
	/// renderer's "IsEnginePreLoading" from state 7 of the level-change command. MUST BE CLEARED
	/// on every path that leaves a build, success or failure, or the guest parks at state 7.
	/// </summary>
	public static void SetWorldBuildPending(bool pending)
	{
		if (TryGetAbiVersion() is null)
		{
			return;
		}

		RefiiHostSetWorldBuildPending(pending ? 1u : 0u);
	}

	/// <summary>
	/// Whether the guest's world is activated and populated — `World+0xA8`, set by the entity
	/// activator and cleared by the world teardown, and the one signal both load paths write
	/// (travel from level-change state 5, load-from-save from its own state 2). Prefer this to
	/// <see cref="WorldIsReady"/>, which boots as true and is only set at the terminal state,
	/// behind render-fence and loading-screen gates a headless host may never satisfy.
	/// </summary>
	public static bool WorldIsActive()
	{
		if (TryGetAbiVersion() is null)
		{
			return false;
		}

		return RefiiHostWorldIsActive() != 0;
	}

	/// <summary>
	/// The gameflow command queue as text — what is actually in it, for when a walk of it comes
	/// back empty and the alternative is another guess at the layout.
	/// </summary>
	public static string DescribeGameFlowQueue()
	{
		if (TryGetAbiVersion() is null)
		{
			return "bridge unavailable";
		}

		var text = new byte[512];
		return RefiiHostDescribeGameFlowQueue(text, (uint)text.Length) == 0
			? "unreadable"
			: Decode(text);
	}

	/// <summary>
	/// Name scratch for the per-frame level polls below: three 128-byte names each, kept rather
	/// than allocated per call (the trace put them near the top of the host's steady churn).
	/// Cleared before each call so a shorter name never reads a stale tail. Main thread only,
	/// like every other kept buffer in this file.
	/// </summary>
	public static LevelChange? GetLevelChange()
	{
		if (TryGetAbiVersion() is null)
		{
			return null;
		}

		byte[] world = _levelWorld ??= new byte[128];
		byte[] level = _levelLevel ??= new byte[128];
		byte[] entrance = _levelEntrance ??= new byte[128];
		System.Array.Clear(world);
		System.Array.Clear(level);
		System.Array.Clear(entrance);
		if (RefiiHostGetLevelChange(world, level, entrance, (uint)level.Length,
				out uint state, out uint travel, out uint fullLoad) == 0)
		{
			return null;
		}

		return new LevelChange(
			Decode(world), Decode(level), Decode(entrance), state, travel, fullLoad != 0);
	}

	private static byte[]? _levelWorld;
	private static byte[]? _levelLevel;
	private static byte[]? _levelEntrance;
	private static byte[]? _currentWorld;
	private static byte[]? _currentLevel;
	private static byte[]? _currentEntrance;

	public static CurrentLevel? GetCurrentLevel()
	{
		if (TryGetAbiVersion() is null)
		{
			return null;
		}

		byte[] world = _currentWorld ??= new byte[128];
		byte[] level = _currentLevel ??= new byte[128];
		byte[] entrance = _currentEntrance ??= new byte[128];
		System.Array.Clear(world);
		System.Array.Clear(level);
		System.Array.Clear(entrance);
		if (RefiiHostGetCurrentLevel(world, level, entrance, (uint)level.Length, out uint key) == 0)
		{
			return null;
		}

		return new CurrentLevel(Decode(world), Decode(level), Decode(entrance), key);
	}

	/// <summary>
	/// The scenario the guest starts in, or null when it has not stated one yet — this is what
	/// picks the map. The guest builds
	/// <c>Data\Worlds\&lt;World&gt;\&lt;Level&gt;\&lt;Scenario&gt;\&lt;Scenario&gt;.list</c> and loads every
	/// heightfield it names, so the level name alone cannot choose between scenarios.
	/// </summary>
	public static string? GetStartScenario()
	{
		var scenario = new byte[128];
		return RefiiHostGetStartScenario(scenario, (uint)scenario.Length) == 0
			? null
			: Decode(scenario);
	}

	/// <summary>
	/// The scenario the guest is ACTUALLY in, or null when no level is loaded.
	///
	/// Prefer this over <see cref="GetStartScenario"/>: `Layers.ActivateScenario` rewrites it
	/// and reloads the level, so the start value goes stale the moment a quest advances.
	/// </summary>
	public static string? GetActiveScenario()
	{
		var scenario = new byte[128];
		return RefiiHostGetActiveScenario(scenario, (uint)scenario.Length) == 0
			? null
			: Decode(scenario);
	}

	/// <summary>
	/// FNV-1 hashes of the layers the guest has explicitly DEACTIVATED — worldMgr+72. This is
	/// the gate, NOT <see cref="GetActiveLayers"/>: `sub_823662C8` reads a container per the
	/// layer's `load` attribute — `AlwaysOn` consults neither list, `InitiallyOn` spawns iff
	/// ABSENT from +72, anything else spawns iff PRESENT in +60 — and the props build emits only
	/// the first two. Both lists are empty on a new game, so gating on the activated list would
	/// hide every `InitiallyOn` layer. Returns null, not empty, when the native library predates
	/// ABI 55; empty is a real answer meaning nothing is deactivated.
	/// </summary>
	public static uint[]? GetDeactivatedLayers()
	{
		if (_deactivatedLayersMissing)
		{
			return null;
		}

		var hashes = new uint[256];
		uint count;
		try
		{
			count = RefiiHostGetDeactivatedLayers(hashes, (uint)hashes.Length);
		}
		catch (EntryPointNotFoundException)
		{
			_deactivatedLayersMissing = true;
			GD.PrintErr("[host] the native bridge predates ABI 55 and cannot report the " +
				"deactivated layers — the layer gate is OFF, so nothing will be hidden. " +
				"Rebuild the native library.");
			return null;
		}

		if (count == 0)
		{
			return System.Array.Empty<uint>();
		}

		System.Array.Resize(ref hashes, (int)System.Math.Min(count, (uint)hashes.Length));
		return hashes;
	}

	private static bool _deactivatedLayersMissing;

	/// <summary>
	/// FNV-1 hashes of the layers the guest has explicitly ACTIVATED — worldMgr+60. Diagnostic
	/// only; the gate is <see cref="GetDeactivatedLayers"/>. The hash is over the layer name's
	/// EXACT bytes: no lowercasing, no separator normalisation, unlike the world+level key.
	/// </summary>
	public static uint[] GetActiveLayers()
	{
		var hashes = new uint[256];
		uint count = RefiiHostGetActiveLayers(hashes, (uint)hashes.Length);
		if (count == 0)
		{
			return System.Array.Empty<uint>();
		}

		System.Array.Resize(ref hashes, (int)System.Math.Min(count, (uint)hashes.Length));
		return hashes;
	}

	/// <summary>
	/// <c>PostFrontEndAction</c> — asks the guest to perform a front-end action by the NAME it
	/// dispatches on. Nothing happens on this thread: the name is queued and the guest picks it
	/// up inside its own tick. Returns the queue depth after posting; a climbing depth means the
	/// pump is holding the action because the front end is not up yet.
	/// </summary>
	/// <summary>
	/// Drains the <c>GUI.*</c> native calls the game VM has made since the last drain.
	/// </summary>
	/// <remarks>
	/// The UI cut's arrival point: the name the script used and the values it passed, before the
	/// guest's own implementation has done anything with them. Taking the call does not suppress
	/// it. The payload is NUL-separated fields plus a parallel kind array — one length byte per
	/// record, then one kind per argument. See docs/ui_native_cut.md.
	/// </remarks>
	public static System.Collections.Generic.List<Engine_UiModel.GuiNativeCall> TakeGuiNativeCalls()
	{
		var calls = new System.Collections.Generic.List<Engine_UiModel.GuiNativeCall>();
		if (TryGetAbiVersion() is null)
		{
			return calls;
		}

		// The export hands over what fits and puts the rest back at the front of its queue: drain
		// until none waits. A first record that fits no buffer makes these grow (to the native
		// ceiling, where the export drops that one record by name).
		for (int pass = 0; pass < DrainPasses; ++pass)
		{
			uint taken = TakeGuiNativeCallsOnce(calls);
			if (GuiNativeCounters().Pending == 0 || (taken == 0 && !GrowDrain(ref _guiText, ref _guiKinds)))
			{
				break;
			}
		}

		return calls;
	}

	private const int DrainPasses = 64;
	private const int DrainTextCeiling = 16 * 1024 * 1024;
	private const int DrainKindCeiling = 1024 * 1024;

	/// <summary>Quadruples a drain's buffers up to the native ceilings; false when both are there.</summary>
	private static bool GrowDrain(ref byte[]? text, ref byte[]? kinds)
	{
		if (text is null || kinds is null ||
			(text.Length >= DrainTextCeiling && kinds.Length >= DrainKindCeiling))
		{
			return false;
		}

		text = new byte[System.Math.Min(text.Length * 4, DrainTextCeiling)];
		kinds = new byte[System.Math.Min(kinds.Length * 4, DrainKindCeiling)];
		return true;
	}

	private static uint TakeGuiNativeCallsOnce(System.Collections.Generic.List<Engine_UiModel.GuiNativeCall> calls)
	{
		byte[] text = _guiText ??= new byte[64 * 1024];
		byte[] kinds = _guiKinds ??= new byte[4096];
		System.Array.Clear(text);
		System.Array.Clear(kinds);
		uint count;
		try
		{
			count = RefiiHostTakeGuiNativeCalls(
				text, (uint)text.Length, kinds, (uint)kinds.Length, 256u);
		}
		catch (EntryPointNotFoundException)
		{
			return 0;
		}

		int at = 0;
		int kindAt = 0;
		string Next()
		{
			int end = System.Array.IndexOf(text, (byte)0, at);
			if (end < 0)
			{
				end = text.Length;
			}

			string value = Encoding.UTF8.GetString(text, at, end - at);
			at = end + 1;
			return value;
		}

		for (uint record = 0; record < count && kindAt < kinds.Length; ++record)
		{
			string name = Next();
			int arity = kinds[kindAt++];
			var arguments = new System.Collections.Generic.List<Engine_UiModel.GuiNativeArgument>(arity);
			for (int i = 0; i < arity && kindAt < kinds.Length; ++i)
			{
				var kind = (Engine_UiModel.GuiNativeArgumentKind)kinds[kindAt++];
				if (kind != Engine_UiModel.GuiNativeArgumentKind.Table)
				{
					arguments.Add(new Engine_UiModel.GuiNativeArgument(kind, Next()));
					continue;
				}

				// A table spends a count field, then name/kind/value per entry (ABI 84). Its
				// own text field is the count.
				string countText = Next();
				int fields = int.TryParse(countText,
					System.Globalization.NumberStyles.Integer,
					System.Globalization.CultureInfo.InvariantCulture, out int parsedFields)
					? parsedFields
					: 0;

				var table = new System.Collections.Generic.Dictionary<
					string, Engine_UiModel.GuiNativeArgument>(fields, System.StringComparer.Ordinal);
				for (int f = 0; f < fields && kindAt < kinds.Length; ++f)
				{
					string fieldName = Next();
					var fieldKind = (Engine_UiModel.GuiNativeArgumentKind)kinds[kindAt++];
					table[fieldName] = new Engine_UiModel.GuiNativeArgument(fieldKind, Next());
				}

				arguments.Add(new Engine_UiModel.GuiNativeArgument(kind, countText, table));
			}

			calls.Add(new Engine_UiModel.GuiNativeCall(name, arguments));
		}

		return count;
	}

	/// <summary>
	/// How many `GUI` calls have been intercepted, how many wait, and how many names the
	/// registration hook resolved. <c>named</c> is 0 until `RegisterGUI` has run, which is what
	/// separates "intercepted nothing" from "never saw the namespace".
	/// </summary>
	public static (ulong Intercepted, uint Pending, uint Named) GuiNativeCounters()
	{
		if (TryGetAbiVersion() is null)
		{
			return (0UL, 0u, 0u);
		}

		try
		{
			RefiiHostGuiNativeCounters(out ulong intercepted, out uint pending, out uint named);
			return (intercepted, pending, named);
		}
		catch (EntryPointNotFoundException)
		{
			return (0UL, 0u, 0u);
		}
	}

	/// <summary>
	/// True while a menu population is in flight and the captured rows are not yet whole.
	/// </summary>
	/// <remarks>
	/// The host reads asynchronously and there is no end marker — the in-game lists never call
	/// `UpdateAfterSpawning` — so the window between `DeleteAllSpawnedMenuItems` and the last
	/// `SpawnButton` reads as an empty menu. Hold the last whole population while this is true.
	/// </remarks>
	public static bool MenuPopulating()
	{
		if (TryGetAbiVersion() is null)
		{
			return false;
		}

		try
		{
			return RefiiHostMenuPopulating() != 0;
		}
		catch (EntryPointNotFoundException)
		{
			return false;
		}
	}

	/// <summary>
	/// Drains the `g_Menu:` chunk calls the game's C++ has made since the last drain.
	/// </summary>
	/// <remarks>
	/// The whole menu contract: the C++ stages attributes onto the `Parameters` element then
	/// runs a one-line Lua chunk that reads them back, so `SpawnButton`, `SetItemGhosting`,
	/// `SetItemEmphasis`, `RefreshButton`, `DeleteAllSpawnedMenuItems` and the rest all arrive
	/// here — including the channels written AFTER the spawn. Each record's presentation is part
	/// of its identity: a second `SetItemEmphasis`/`SetItemGhosting` with different semantics
	/// exists for the non-expandable menu.
	/// </remarks>
	/// <summary>
	/// What the `g_Menu:` capture has actually SEEN — recorded, still pending, and dropped to
	/// the ceiling. Nothing read this until a run showed zero statements crossing in 6,777 log
	/// lines, which is indistinguishable from "the guest states no menus" without it: recorded
	/// climbing means the hook fires and the crossing is at fault, recorded stuck at zero means
	/// the guest's populations never reach `sub_82426470` and the hook is on the wrong function.
	/// </summary>
	public static (ulong Recorded, uint Pending, ulong Dropped) MenuChunkCounters()
	{
		if (TryGetAbiVersion() is null)
		{
			return (0UL, 0u, 0UL);
		}

		RefiiHostMenuChunkCounters(out ulong recorded, out uint pending, out ulong dropped);
		return (recorded, pending, dropped);
	}

	private static byte[]? _menuText;
	private static byte[]? _menuKinds;
	private static byte[]? _guiText;
	private static byte[]? _guiKinds;
	private static uint[]? _hudTextHashes;
	private static byte[]? _hudTextBytes;

	public static System.Collections.Generic.List<Engine_UiModel.MenuChunk> TakeMenuChunks()
	{
		var chunks = new System.Collections.Generic.List<Engine_UiModel.MenuChunk>();
		if (TryGetAbiVersion() is null)
		{
			return chunks;
		}

		// As TakeGuiNativeCalls: drained until none waits, the buffers grown for a record that fits none.
		for (int pass = 0; pass < DrainPasses; ++pass)
		{
			uint taken = TakeMenuChunksOnce(chunks);
			if (MenuChunkCounters().Pending == 0 || (taken == 0 && !GrowDrain(ref _menuText, ref _menuKinds)))
			{
				break;
			}
		}

		return chunks;
	}

	private static uint TakeMenuChunksOnce(System.Collections.Generic.List<Engine_UiModel.MenuChunk> chunks)
	{
		byte[] text = _menuText ??= new byte[128 * 1024];
		byte[] kinds = _menuKinds ??= new byte[8192];
		System.Array.Clear(text);
		System.Array.Clear(kinds);
		uint count;
		try
		{
			count = RefiiHostTakeMenuChunks(text, (uint)text.Length, kinds, (uint)kinds.Length, 1024u);
		}
		catch (EntryPointNotFoundException)
		{
			return 0;
		}

		int at = 0;
		int kindAt = 0;
		string Next()
		{
			int end = System.Array.IndexOf(text, (byte)0, at);
			if (end < 0)
			{
				end = text.Length;
			}

			string value = Encoding.UTF8.GetString(text, at, end - at);
			at = end + 1;
			return value;
		}

		for (uint record = 0; record < count && kindAt < kinds.Length; ++record)
		{
			uint presentation = uint.TryParse(Next(),
				System.Globalization.NumberStyles.HexNumber,
				System.Globalization.CultureInfo.InvariantCulture, out uint parsed) ? parsed : 0u;
			string chunk = Next();
			int staged = kinds[kindAt++];

			var attributes = new System.Collections.Generic.Dictionary<string, string>(
				staged, System.StringComparer.Ordinal);
			for (int i = 0; i < staged && kindAt < kinds.Length; ++i)
			{
				++kindAt;
				string name = Next();
				attributes[name] = Next();
			}

			chunks.Add(new Engine_UiModel.MenuChunk(presentation, chunk, attributes));
		}

		return count;
	}

	/// <summary>
	/// Answers one `GUI` predicate from the host INSTEAD of the guest — ABI 85.
	/// </summary>
	/// <remarks>
	/// The suppression lever: the thunk writes the value onto the Lua stack and returns without
	/// calling through. Opt-in by name — fifteen of the twenty predicates read state the guest's
	/// own setters maintain, so answering one whose setters the host has not taken over replaces
	/// a correct answer with a guess.
	/// </remarks>
	public static void SetGuiPredicate(string name, bool value)
	{
		if (TryGetAbiVersion() is not null)
		{
			try
			{
				RefiiHostSetGuiPredicate(name, 1u, value ? 1u : 0u);
			}
			catch (EntryPointNotFoundException)
			{
			}
		}
	}

	/// <summary>Answers a float-returning predicate — `GetFillPercent` is the only one.</summary>
	public static void SetGuiPredicate(string name, float value)
	{
		if (TryGetAbiVersion() is not null)
		{
			try
			{
				RefiiHostSetGuiPredicate(name, 2u, BitConverter.SingleToUInt32Bits(value));
			}
			catch (EntryPointNotFoundException)
			{
			}
		}
	}

	/// <summary>
	/// Stops the guest running a native this host owns outright — ABI 85.
	/// </summary>
	/// <remarks>
	/// Only for a native whose side effects are inert. Owning the family is necessary and not
	/// sufficient: opening a display box pushes a GameFlow command that suspends gameplay, the
	/// minigame tally opens a screen, the golf meter plays sounds.
	/// </remarks>
	public static void SuppressGuiNative(string name)
	{
		if (TryGetAbiVersion() is not null)
		{
			try
			{
				RefiiHostSetGuiPredicate(name, 3u, 0u);
			}
			catch (EntryPointNotFoundException)
			{
			}
		}
	}

	/// <summary>Hands a predicate back to the guest.</summary>
	public static void ClearGuiPredicate(string name)
	{
		if (TryGetAbiVersion() is not null)
		{
			try
			{
				RefiiHostSetGuiPredicate(name, 0u, 0u);
			}
			catch (EntryPointNotFoundException)
			{
			}
		}
	}

	public static uint PostFrontEndAction(string name) => RefiiHostPostFrontEndAction(name);

	/// <summary>
	/// Adds the mouse's camera turn since the last post — degrees at the profile's middle camera
	/// sensitivity, right and up positive. The guest hands all of it to the local player's active
	/// camera, through that camera's own ProcessGameAction, and the camera turns it whole in its
	/// next update.
	/// </summary>
	public static void PostMouseLook(float yawDegrees, float pitchDegrees)
	{
		if (TryGetAbiVersion() is null || !IsGuestUp)
		{
			return;
		}

		RefiiHostPostMouseLook(yawDegrees, pitchDegrees);
	}

	/// <summary>
	/// States whether the mouse is the player's look device (the device switch). While it is, the
	/// guest's camera it drives keeps its heading as the hero moves and a target lock takes the mouse
	/// as flicks between targets; a pad keeps the shipped camera. Once per frame.
	/// </summary>
	public static void SetMouseLookDevice(bool mouse)
	{
		if (TryGetAbiVersion() is null || !IsGuestUp)
		{
			return;
		}

		RefiiHostSetMouseLookDevice(mouse ? 1 : 0);
	}

	private static bool _lastHostAimActive;

	/// <summary>
	/// States or clears the host-authoritative ranged aim forward vector (unit vector in guest world
	/// space). Redundant inactive writes are suppressed once cleared.
	/// </summary>
	public static void SetHostAim(bool active, Vector3 guestForward)
	{
		if (!active && !_lastHostAimActive)
		{
			return;
		}

		if (TryGetAbiVersion() is null || !IsGuestUp)
		{
			_lastHostAimActive = false;
			return;
		}

		if (active && guestForward.IsFinite() && guestForward.LengthSquared() > 1e-8f)
		{
			RefiiHostSetHostAim(1, guestForward.X, guestForward.Y, guestForward.Z);
			_lastHostAimActive = true;
		}
		else
		{
			RefiiHostSetHostAim(0, 0f, 0f, 0f);
			_lastHostAimActive = false;
		}
	}

	/// <summary>
	/// Adds mouse-wheel notches made while the mouse is the camera's, rightward positive: a notch
	/// down +1, up -1. Under a target lock a mod states switches by the wheel, each notch is one
	/// switch to the next target that way; otherwise the guest drops them.
	/// </summary>
	public static void PostMouseWheel(int notches)
	{
		if (notches == 0 || TryGetAbiVersion() is null || !IsGuestUp)
		{
			return;
		}

		RefiiHostPostMouseWheel(notches);
	}

	/// <summary>The mouse's account as of the guest's last camera update; see <see cref="GetMouseLook"/>.</summary>
	/// <param name="Sequence">Odd while an update is between taking and stating.</param>
	/// <param name="Tick">The game tick (`game+0xFC`) of the last take.</param>
	/// <param name="Posted">Yaw posted, cumulative, in posted degrees — the same origin as <paramref name="Taken"/>.</param>
	/// <param name="Taken">Posted yaw the camera updates have taken, cumulative.</param>
	/// <param name="Absorbed">Yaw the camera turned for it, cumulative, in the camera's degrees, rightward.</param>
	/// <param name="PostedPitch">Pitch posted, cumulative, posted degrees, upward.</param>
	/// <param name="TakenPitch">Posted pitch the camera updates have taken, cumulative.</param>
	/// <param name="AbsorbedPitch">Pitch the camera turned for it, cumulative, its degrees, upward.</param>
	/// <param name="Factor">The camera's degrees of turn per posted degree.</param>
	/// <param name="Pivot">
	/// Where a turn not yet taken is drawn about: for <see cref="Refii.Core.Engine_MouseLook.ModeOrbit"/>
	/// X is the focus's distance along the level line of sight; for
	/// <see cref="Refii.Core.Engine_MouseLook.ModeAim"/> the point X metres along the camera's level
	/// forward, Y along its level right, Z up.
	/// </param>
	/// <param name="Mode">How a turn not yet taken is drawn (<see cref="Refii.Core.Engine_MouseLook"/> modes).</param>
	/// <param name="Flicks">Target switches the mouse handed to a target lock (flicks and wheel notches), run total.</param>
	/// <param name="PitchLimit">For the aim mode: the camera's elevation bound, degrees.</param>
	/// <param name="Aim">For the aim mode: the ranged aim object whose reticule turns with the view.</param>
	/// <param name="HeadGuest">For the aim mode: hero head pivot in guest world space.</param>
	/// <param name="Flags">Flag bits: bit 0 LookInverted, bit 1 HostAimApplied, bit 2 HasHead.</param>
	public readonly record struct MouseLookState(
		uint Sequence, uint Tick, double Posted, double Taken, double Absorbed,
		double PostedPitch, double TakenPitch, double AbsorbedPitch, float Factor,
		Vector3 Pivot, int Mode, uint Flicks, float PitchLimit, uint Aim,
		Vector3 HeadGuest = default, uint Flags = 0u)
	{
		/// <summary>The focus distance along the level line of sight (the orbit mode's pivot).</summary>
		public float PivotMetres => Pivot.X;

		public bool LookInverted => (Flags & 1u) != 0u;
		public bool HostAimApplied => (Flags & 2u) != 0u;
		public bool HasHead => (Flags & 4u) != 0u;
	}

	private const int MouseLookValues = 19;

	[ThreadStatic]
	private static double[]? _mouseLookScratch;

	/// <summary>The mouse's account as of the guest's last camera update, or null without a guest.</summary>
	public static MouseLookState? GetMouseLook()
	{
		if (TryGetAbiVersion() is null || !IsGuestUp)
		{
			return null;
		}

		// Read every frame by the camera and UI: one buffer per thread, overwritten completely by native.
		double[] v = _mouseLookScratch ??= new double[MouseLookValues];
		uint sequence = RefiiHostGetMouseLook(v, MouseLookValues);
		return new MouseLookState(
			sequence, (uint)v[0], v[1], v[2], v[3], v[4], v[5], v[6], (float)v[7],
			new Vector3((float)v[8], (float)v[9], (float)v[10]), (int)v[11], (uint)v[12],
			(float)v[13], (uint)v[14], new Vector3((float)v[15], (float)v[16], (float)v[17]),
			(uint)v[18]);
	}

	[ThreadStatic]
	private static uint[]? _unlockIds, _unlockUsers;

	[ThreadStatic]
	private static ulong[]? _unlockSequences, _unlockBindings;

	/// <summary>The guest's achievement writes newer than <paramref name="afterSequence"/>, oldest first.</summary>
	public static AchievementUnlock[] GetAchievementUnlocks(ulong afterSequence, int max = 16)
	{
		if (TryGetAbiVersion() is null || max <= 0)
		{
			return System.Array.Empty<AchievementUnlock>();
		}

		// Polled on every guest frame and nearly always empty: the buffers are kept per thread.
		if (_unlockIds is null || _unlockIds.Length < max)
		{
			_unlockIds = new uint[max];
			_unlockUsers = new uint[max];
			_unlockSequences = new ulong[max];
			_unlockBindings = new ulong[max];
		}

		uint[] ids = _unlockIds;
		uint[] users = _unlockUsers!;
		ulong[] sequences = _unlockSequences!;
		ulong[] bindings = _unlockBindings!;
		uint count = RefiiHostGetAchievementUnlocks(afterSequence, ids, users, sequences, bindings, (uint)max);
		if (count == 0)
		{
			return System.Array.Empty<AchievementUnlock>();
		}

		var unlocks = new AchievementUnlock[System.Math.Min(count, (uint)max)];
		for (int i = 0; i < unlocks.Length; ++i)
		{
			unlocks[i] = new AchievementUnlock(users[i], ids[i], sequences[i], bindings[i]);
		}

		return unlocks;
	}

	/// <summary>The title's XDBF resource — sized with a null buffer first — or null before the module has launched.</summary>
	public static byte[]? GetTitleXdbf()
	{
		if (TryGetAbiVersion() is null)
		{
			return null;
		}

		uint size = RefiiHostGetTitleXdbf(null, 0);
		if (size == 0)
		{
			return null;
		}

		var bytes = new byte[size];
		uint written = RefiiHostGetTitleXdbf(bytes, size);
		return written == 0 ? null : bytes;
	}

	/// <summary>
	/// States every save's unlocked set for the guest's per-user achievement enumerator: one save per
	/// line, <c>save\tid:filetime,...</c> (<see cref="Refii.Achievements.Engine_AchievementCharacters.SaveTable"/>).
	/// </summary>
	public static void SetSaveAchievementUnlocks(string table)
	{
		if (TryGetAbiVersion() is null)
		{
			return;
		}

		RefiiHostSetSaveAchievementUnlocks(table);
	}

	/// <summary>
	/// The guest's adoption points' bindings newer than <paramref name="afterSerial"/>: <c>(serial, user,
	/// save)</c>, save empty for a hero no save holds yet.
	/// </summary>
	public static List<Engine_UiModel.AchievementBinding> GetAchievementBindings(ulong afterSerial)
	{
		var bindings = new List<Engine_UiModel.AchievementBinding>();
		if (TryGetAbiVersion() is null)
		{
			return bindings;
		}

		uint size = RefiiHostGetAchievementBindings(afterSerial, null, 0);
		if (size == 0)
		{
			return bindings;
		}

		var text = new byte[size + 1];
		if (RefiiHostGetAchievementBindings(afterSerial, text, (uint)text.Length) == 0)
		{
			return bindings;
		}

		foreach (string line in System.Text.Encoding.UTF8.GetString(text, 0, (int)size).Split('\n'))
		{
			string[] fields = line.Split('\t');
			if (fields.Length == 3 && ulong.TryParse(fields[0], out ulong serial) && uint.TryParse(fields[1], out uint user))
			{
				bindings.Add(new Engine_UiModel.AchievementBinding(serial, user, fields[2]));
			}
		}

		return bindings;
	}

	/// <summary>How many times the guest has called `XamShowAchievementsUI` this session.</summary>
	public static uint GetAchievementsUiRequests() =>
		TryGetAbiVersion() is null ? 0u : RefiiHostGetAchievementsUiRequests();

	/// <summary>
	/// Feeds one action code to the current in-game screen's own <c>vtable+24</c> handler.
	///
	/// 36 select (the uniform <c>sub_826D58F0</c> "OnPressA"), 37 back, 39 the per-screen Y
	/// action, 59 inspect, 72/73 tab prev/next, 28..31 the directions and 32..35 their releases.
	/// The press carries the acting player (<see cref="Refii.Ui.Engine_UiSource.ActingPlayer"/>), the
	/// handler's r5. A handler that reads the highlighted row reads the row stated by
	/// <see cref="SetHighlightedRow"/>.
	/// </summary>
	/// <remarks>B's root test (`IsHighlightedItemAtRoot`) is answered from <see cref="SetListFoldDepth"/>,
	/// not carried with the press.</remarks>
	public static uint ScreenAction(uint code) =>
		PostFrontEndAction(Refii.Ui.Engine_UiSource.PlayerLever("ScreenAction", Refii.Ui.Engine_UiSource.Current.ActingPlayer, $"{code}"));

	/// <summary>
	/// Commits a menu row BY IDENTITY, the way the shipped script does: `g_Menu:SelectMenuItem`
	/// sends `GUI:PushButtonEvent(item.Name, item.EventType, item.IDLow, item.IDHigh)` — never
	/// an index — and `PushButtonEvent` is Lua, pushing onto four parallel arrays the guest's
	/// C++ drain (`sub_82370450`) pops one per tick into the current screen's `vtable+20`.
	/// Naming the row is what takes the cursor off the guest, since every list screen otherwise
	/// reads `g_Menu:GetHighlightedMenuItemData()`.
	/// <paramref name="id"/> is the FULL 32-bit row id — the halves the guest split into
	/// `Float1`/`Float2` at spawn, recombined here and split again on the way out.
	/// A ghosted row is the caller's to refuse: the script's early return is in `SelectMenuItem`,
	/// which this bypasses.
	/// </summary>
	public static uint MenuEvent(string name, int eventType, uint id) =>
		PostFrontEndAction($"MenuEvent:{name}:{eventType}:{id}");

	/// <summary>
	/// Every row the guest has spawned into the current list, in spawn order: the identity —
	/// name, parent link and the `(EventType, id)` pair a commit is made of — which lives only
	/// in a Lua table and is captured at `sub_826CE8D0` on its way there. The labels come off
	/// the element tree instead. <paramref name="generation"/> is bumped on every spawn, so an
	/// unchanged value means the same list.
	/// </summary>
	/// <summary>
	/// The screen the captured rows belong to, or `Engine_UiMenuSources.AnyScreen` when none —
	/// the front end, or before the guest is up. See the native side for why the key exists.
	/// </summary>

	/// <summary>Raw guest words — the debug channel's microscope. Empty when unreadable.</summary>
	/// <summary>
	/// The running build's address for one this source names by its TU1 value.
	/// </summary>
	/// <remarks>
	/// TWO EXECUTABLES ARE SUPPORTED and no guest address is shared between them. Source keeps
	/// the TU1 address as the name; the table for the other build is the native side's
	/// (Fable2/versions/Fable2/guest_addresses.inc). Apply this to LITERALS only: a pointer read from
	/// the guest is already the running build's.
	/// </remarks>
	public static uint GuestAddress(uint tu1Address)
	{
		if (TryGetAbiVersion() is null)
		{
			return tu1Address;
		}

		try
		{
			return RefiiHostGuestAddress(tu1Address);
		}
		catch (EntryPointNotFoundException)
		{
			return tu1Address;
		}
	}

	/// <summary>
	/// The TU1 address for a value READ from the guest, so it can be matched against the TU1
	/// constants this source holds — a vtable, a handler.
	/// </summary>
	public static uint GuestAddressToTu1(uint liveAddress)
	{
		if (TryGetAbiVersion() is null)
		{
			return liveAddress;
		}

		try
		{
			return RefiiHostGuestAddressToTu1(liveAddress);
		}
		catch (EntryPointNotFoundException)
		{
			return liveAddress;
		}
	}

	/// <summary>True when the running game was installed without the title update.</summary>
	public static bool IsUnpatchedBuild
	{
		get
		{
			if (TryGetAbiVersion() is null)
			{
				return false;
			}

			try
			{
				return RefiiHostGuestBuild() == 1;
			}
			catch (EntryPointNotFoundException)
			{
				return false;
			}
		}
	}

	/// <summary>A value that is not an address and differs between the two builds.</summary>
	public static T PickByBuild<T>(T tu1, T unpatched) => IsUnpatchedBuild ? unpatched : tu1;

	[ThreadStatic]
	private static uint[]? _peekScratch;

	public static int Peek(uint addr, Span<uint> into)
	{
		if (TryGetAbiVersion() is null || !IsGuestUp || into.IsEmpty || into.Length > 64)
		{
			return 0;
		}

		uint[] scratch = _peekScratch ??= new uint[64];
		try
		{
			uint got = RefiiHostPeek(addr, scratch, (uint)into.Length);
			int count = (int)Math.Min(got, (uint)into.Length);
			scratch.AsSpan(0, count).CopyTo(into);
			return count;
		}
		catch (EntryPointNotFoundException)
		{
			return 0;
		}
	}

	public static bool TryPeekWord(uint addr, out uint value)
	{
		Span<uint> one = stackalloc uint[1];
		if (Peek(addr, one) == 1)
		{
			value = one[0];
			return true;
		}

		value = 0u;
		return false;
	}

	public static uint[] Peek(uint addr, uint count)
	{
		if (TryGetAbiVersion() is null || !IsGuestUp || count == 0 || count > 64)
		{
			return System.Array.Empty<uint>();
		}

		var words = new uint[count];
		try
		{
			uint got = RefiiHostPeek(addr, words, count);
			if (got < count)
			{
				System.Array.Resize(ref words, (int)got);
			}
		}
		catch (EntryPointNotFoundException)
		{
			return System.Array.Empty<uint>();
		}

		return words;
	}


	// =================== ReXGlue runtime introspection (debug channel) ===================

	/// <summary>One kernel-registered thread, with the live r1/lr its backtrace starts from.</summary>
	public readonly record struct GuestThreadInfo(
		uint Tid, bool IsGuestThread, bool IsMainThread, bool Running,
		int Priority, uint Cpu, uint R1, uint Lr, string Name);

	private const uint IntrospectStride = 96;
	private const uint IntrospectCapacity = 128;

	private static string SlotString(byte[] buffer, int index, uint stride)
	{
		int start = index * (int)stride;
		int length = 0;
		while (length < stride && buffer[start + length] != 0)
		{
			++length;
		}

		return Encoding.UTF8.GetString(buffer, start, length);
	}

	/// <summary>
	/// Every thread the kernel knows, guest-created and host-side both. r1/lr are read from the
	/// live context without stopping the thread — exact for a parked thread, torn at worst for a
	/// running one, and the parked ones are the ones a freeze asks about.
	/// </summary>
	public static System.Collections.Generic.List<GuestThreadInfo> GetGuestThreads()
	{
		var threads = new System.Collections.Generic.List<GuestThreadInfo>();
		if (TryGetAbiVersion() is null || !IsGuestUp)
		{
			return threads;
		}

		var tids = new uint[IntrospectCapacity];
		var flags = new uint[IntrospectCapacity];
		var priority = new int[IntrospectCapacity];
		var cpu = new uint[IntrospectCapacity];
		var r1 = new uint[IntrospectCapacity];
		var lr = new uint[IntrospectCapacity];
		var names = new byte[IntrospectCapacity * IntrospectStride];
		uint count = RefiiHostGetGuestThreads(
			tids, flags, priority, cpu, r1, lr, names, IntrospectStride, IntrospectCapacity);
		for (int i = 0; i < count; ++i)
		{
			threads.Add(new GuestThreadInfo(
				tids[i], (flags[i] & 1u) != 0, (flags[i] & 2u) != 0, (flags[i] & 4u) != 0,
				priority[i], cpu[i], r1[i], lr[i], SlotString(names, i, IntrospectStride)));
		}

		return threads;
	}

	/// <summary>
	/// Guest return addresses for one thread, innermost first. Frame 0 is the context's LR —
	/// a leaf's return address exists nowhere else — and the rest walk the back chain the
	/// title's own ABI maintains (32-bit back chain at [r1], saved LR at back-chain − 8;
	/// verified in the binary, see RefiiHostGuestBacktrace).
	/// </summary>
	public static uint[] GetGuestBacktrace(uint tid, int max = 48)
	{
		if (TryGetAbiVersion() is null || !IsGuestUp || max < 1)
		{
			return System.Array.Empty<uint>();
		}

		var frames = new uint[max];
		uint count = RefiiHostGuestBacktrace(tid, frames, (uint)max);
		System.Array.Resize(ref frames, (int)count);
		return frames;
	}

	/// <summary>One entry of the kernel handle table. Type is rex XObject::Type.</summary>
	public readonly record struct KernelObjectInfo(uint Handle, uint Type, string Name);

	public static System.Collections.Generic.List<KernelObjectInfo> GetKernelObjects(
		out uint total)
	{
		total = 0;
		var objects = new System.Collections.Generic.List<KernelObjectInfo>();
		if (TryGetAbiVersion() is null || !IsGuestUp)
		{
			return objects;
		}

		const uint Capacity = 512;
		var handles = new uint[Capacity];
		var types = new uint[Capacity];
		var names = new byte[Capacity * IntrospectStride];
		uint count = RefiiHostGetKernelObjects(
			handles, types, names, IntrospectStride, Capacity, out total);
		for (int i = 0; i < count; ++i)
		{
			objects.Add(new KernelObjectInfo(
				handles[i], types[i], SlotString(names, i, IntrospectStride)));
		}

		return objects;
	}

	/// <summary>
	/// Every rex::perf counter, LIVE values — run totals or gauges, never per-frame rates:
	/// the snapshot rotation lives in the renderer, which this build compiles out. Rates are
	/// deltas between two reads.
	/// </summary>
	public static System.Collections.Generic.List<(string Name, long Value)> GetPerfCounters()
	{
		var counters = new System.Collections.Generic.List<(string, long)>();
		if (TryGetAbiVersion() is null)
		{
			return counters;
		}

		const uint Capacity = 64;
		var values = new long[Capacity];
		var names = new byte[Capacity * IntrospectStride];
		uint count = RefiiHostGetPerfCounters(values, names, IntrospectStride, Capacity);
		for (int i = 0; i < count; ++i)
		{
			counters.Add((SlotString(names, i, IntrospectStride), values[i]));
		}

		return counters;
	}

	/// <summary>One kernel export, named "module!Name". No call counts: the runtime's
	/// call_count field is dead in recompiled builds.</summary>
	public readonly record struct KernelExportInfo(
		string Name, uint Ordinal, bool Implemented, bool IsVariable);

	public static System.Collections.Generic.List<KernelExportInfo> GetKernelExports(
		uint first, bool onlyUnimplemented, out uint total)
	{
		total = 0;
		var exports = new System.Collections.Generic.List<KernelExportInfo>();
		if (TryGetAbiVersion() is null || !IsGuestUp)
		{
			return exports;
		}

		const uint Capacity = 256;
		var ordinals = new uint[Capacity];
		var flags = new uint[Capacity];
		var names = new byte[Capacity * IntrospectStride];
		uint count = RefiiHostGetKernelExports(
			first, onlyUnimplemented ? 1u : 0u, ordinals, flags, names, IntrospectStride,
			Capacity, out total);
		for (int i = 0; i < count; ++i)
		{
			exports.Add(new KernelExportInfo(
				SlotString(names, i, IntrospectStride), ordinals[i],
				(flags[i] & 1u) != 0, (flags[i] & 2u) != 0));
		}

		return exports;
	}

	/// <summary>One cvar. Type is rex FlagType (Boolean..Command), Lifecycle 0=init-only
	/// 1=hot-reload 2=requires-restart.</summary>
	public readonly record struct CvarInfo(
		string Name, string Value, string Default, string Category,
		uint Type, uint Lifecycle, bool Modified, bool DebugOnly);

	public static int GetCvarCount() =>
		TryGetAbiVersion() is null ? 0 : RefiiHostGetCvarCount();

	public static CvarInfo? GetCvar(int index)
	{
		if (TryGetAbiVersion() is null || index < 0)
		{
			return null;
		}

		var name = new byte[128];
		var value = new byte[256];
		var fallback = new byte[256];
		var category = new byte[64];
		if (RefiiHostGetCvarInfo((uint)index, name, (uint)name.Length, value,
				(uint)value.Length, fallback, (uint)fallback.Length, category,
				(uint)category.Length, out uint type, out uint lifecycle, out uint flags) == 0)
		{
			return null;
		}

		return new CvarInfo(
			Decode(name), Decode(value), Decode(fallback), Decode(category),
			type, lifecycle, (flags & 1u) != 0, (flags & 2u) != 0);
	}

	/// <summary>Set one cvar through the registry's own validator; false means it refused.</summary>
	public static bool SetCvar(string name, string value) =>
		TryGetAbiVersion() is not null && RefiiHostSetCvar(name, value) != 0;

	/// <summary>Where one guest path actually lands in the live VFS.</summary>
	public readonly record struct VfsResolution(
		bool Found, bool RootIsLink, string AbsolutePath, string LinkTarget,
		ulong Size, uint Attributes);

	public static VfsResolution? VfsResolve(string guestPath)
	{
		if (TryGetAbiVersion() is null || !IsGuestUp || string.IsNullOrEmpty(guestPath))
		{
			return null;
		}

		var absolute = new byte[1024];
		var link = new byte[256];
		uint result = RefiiHostVfsResolve(
			guestPath, absolute, (uint)absolute.Length, link, (uint)link.Length,
			out ulong size, out uint attributes);
		return new VfsResolution(
			(result & 1u) != 0, (result & 2u) != 0, Decode(absolute), Decode(link),
			size, attributes);
	}

	/// <summary>Every socket in the kernel handle table.</summary>
	public static System.Collections.Generic.List<(uint Handle, uint Port, ulong NativeHandle)>
		GetGuestSockets()
	{
		var sockets = new System.Collections.Generic.List<(uint, uint, ulong)>();
		if (TryGetAbiVersion() is null || !IsGuestUp)
		{
			return sockets;
		}

		const uint Capacity = 64;
		var handles = new uint[Capacity];
		var ports = new uint[Capacity];
		var native = new ulong[Capacity];
		uint count = RefiiHostGetGuestSockets(handles, ports, native, Capacity);
		for (int i = 0; i < count; ++i)
		{
			sockets.Add((handles[i], ports[i], native[i]));
		}

		return sockets;
	}


	// ======================= Debug instrumentation batch (ABI 113) =======================

	/// <summary>One Tracy FrameMark + perf counter flip per presented frame. Also rotates the
	/// per-frame counter snapshots and feeds the perf_log_csv writer when that cvar is set.</summary>
	public static void FrameMark()
	{
		if (TryGetAbiVersion() is not null)
		{
			RefiiHostFrameMark();
		}
	}

	/// <summary>Per-frame counter snapshots — the LAST frame's accumulators and gauges.
	/// Meaningful only while FrameMark runs each frame.</summary>
	public static System.Collections.Generic.List<(string Name, long Value)> GetPerfSnapshots()
	{
		var counters = new System.Collections.Generic.List<(string, long)>();
		if (TryGetAbiVersion() is null)
		{
			return counters;
		}

		const uint Capacity = 64;
		var values = new long[Capacity];
		var names = new byte[Capacity * IntrospectStride];
		uint count = RefiiHostGetPerfSnapshots(values, names, IntrospectStride, Capacity);
		for (int i = 0; i < count; ++i)
		{
			counters.Add((SlotString(names, i, IntrospectStride), values[i]));
		}

		return counters;
	}

	/// <summary>A message on the Tracy timeline. No-op in unprofiled builds.</summary>
	public static void TraceMessage(string text)
	{
		if (TryGetAbiVersion() is not null && !string.IsNullOrEmpty(text))
		{
			RefiiHostTraceMessage(text);
		}
	}

	/// <summary>A named Tracy zone; dispose ends it. Zero-cost when no profiler is attached.</summary>
	public readonly struct TraceZone : System.IDisposable
	{
		private readonly ulong _zone;
		public TraceZone(string name) =>
			_zone = TryGetAbiVersion() is not null ? RefiiHostTraceZoneBegin(name) : 0;
		public void Dispose()
		{
			if (_zone != 0)
			{
				RefiiHostTraceZoneEnd(_zone);
			}
		}
	}

	/// <summary>
	/// Brings the profiler up. True when it is running and a profiler may connect.
	/// </summary>
	/// <remarks>
	/// REQUIRED BEFORE ANY ZONE MEANS ANYTHING: the runtime is built with
	/// `TRACY_MANUAL_LIFETIME`, so the client is inert until it is started explicitly. With
	/// `TRACY_ON_DEMAND` also set it collects nothing until the game's own in-process capture
	/// connects, so starting it costs its idle threads and no more.
	/// </remarks>
	public static bool TraceStartup() =>
		TryGetAbiVersion() is not null && RefiiHostTraceStartup() != 0;

	/// <summary>Opens a profiler zone by name, or 0 when the bridge is not up.</summary>
	/// <remarks>
	/// The loose pair behind <see cref="Refii.Core.Engine_FramePhases.ZoneBegin"/>. `TraceZone`
	/// stays for a call site that wants a `using` and nothing else.
	/// </remarks>
	public static ulong TraceZoneBegin(string name) =>
		TryGetAbiVersion() is not null ? RefiiHostTraceZoneBegin(name) : 0UL;

	// ---- hang watchdog ----
	//
	// The bridge's watchdog thread watches the MAIN thread's frame stamp and, when it stops for
	// 8 s, writes exe_dir/logs/refii_hang_<utc>.txt: the main thread's native backtrace, every
	// thread's /proc state, the guest thread table, the frame phase, the last host log lines and
	// the memory numbers — without ptrace, without the debug channel (which that thread serves)
	// and without Godot's logger (whose mutex it may hold). The feeds below are the stamps.

	/// <summary>
	/// False once a watchdog export proved missing from the loaded bridge: the feeds go quiet for
	/// the rest of the run instead of throwing every frame. A diagnostic never takes the game down.
	/// </summary>
	private static bool _watchdogUsable = true;

	/// <remarks>
	/// On the library's own version, not <see cref="TryGetAbiVersion"/>: that refuses once the
	/// session gives up on the guest, and the main thread is still drawing frames then. Gated on it,
	/// the stamp stopped with a failed boot and the watchdog reported a stall every 30 s over a
	/// game that was showing its reason for stopping.
	/// </remarks>
	private static bool WatchdogReady =>
		_watchdogUsable && TryGetRawAbiVersion() is { } version && version == ExpectedAbiVersion;

	private static void WatchdogMissing(EntryPointNotFoundException e)
	{
		_watchdogUsable = false;
		GD.PushWarning($"[hang-watchdog] off for this run: the bridge lacks its exports ({e.Message})");
	}

	/// <summary>The main thread's per-frame stamp; the first call starts the watchdog.</summary>
	public static void FrameAlive(long frame)
	{
		if (!WatchdogReady)
		{
			return;
		}

		try
		{
			RefiiHostFrameAlive((ulong)frame);
		}
		catch (EntryPointNotFoundException e)
		{
			WatchdogMissing(e);
		}
	}

	/// <summary>Registers a frame-phase name; its index (1-based), or 0 when the table is full or the bridge is down.</summary>
	public static uint RegisterFramePhase(string name)
	{
		if (!WatchdogReady)
		{
			return 0u;
		}

		try
		{
			return RefiiHostRegisterFramePhase(name);
		}
		catch (EntryPointNotFoundException e)
		{
			WatchdogMissing(e);
			return 0u;
		}
	}

	/// <summary>The main thread entered the phase with this registered index.</summary>
	public static void SetFramePhase(uint index)
	{
		if (index == 0u || !WatchdogReady)
		{
			return;
		}

		try
		{
			RefiiHostSetFramePhase(index);
		}
		catch (EntryPointNotFoundException e)
		{
			WatchdogMissing(e);
		}
	}

	/// <summary>
	/// The host's log file, stated once so a hang report can name it. The watchdog never takes
	/// a per-print feed: Godot's logger chain dispatches into a managed Logger through the C#
	/// glue, which looks StringNames up — a hang vector on the StringName mutex.
	/// </summary>
	public static void SetHostLogPath(string path)
	{
		if (!WatchdogReady)
		{
			return;
		}

		try
		{
			RefiiHostSetHostLogPath(path);
		}
		catch (EntryPointNotFoundException e)
		{
			WatchdogMissing(e);
		}
	}

	/// <summary>What the watchdog has written this run, or null when the bridge is down.</summary>
	public static (uint Reports, string LastPath, double LastStallSeconds, bool Stalled)? HangReport()
	{
		if (!WatchdogReady)
		{
			return null;
		}

		var path = new byte[4096];
		try
		{
			uint reports = RefiiHostHangReport(path, (uint)path.Length, out double seconds, out int stalled);
			return (reports, Decode(path), seconds, stalled != 0);
		}
		catch (EntryPointNotFoundException e)
		{
			WatchdogMissing(e);
			return null;
		}
	}

	public static void TraceZoneEnd(ulong zone)
	{
		if (zone != 0UL)
		{
			RefiiHostTraceZoneEnd(zone);
		}
	}

	public static void TracePlot(string name, double value)
	{
		if (TryGetAbiVersion() is not null)
		{
			RefiiHostTracePlot(name, value);
		}
	}

	/// <summary>
	/// One frame of Godot's GPU pass timestamps onto the Tracy timeline, as GPU zones and
	/// per-pass plots. The sink behind <see cref="Refii.Core.Engine_GpuPasses.Sink"/>;
	/// <paramref name="packedNames"/> is <paramref name="count"/> entries of
	/// <paramref name="nameStride"/> bytes; the GPU times are nanoseconds.
	/// </summary>
	public static void TraceGpuPasses(byte[] packedNames, uint nameStride, long[] cpuTimes, long[] gpuNs, int count)
	{
		if (count >= 2 && TryGetAbiVersion() is not null)
		{
			RefiiHostTraceGpuPasses(packedNames, nameStride, cpuTimes, gpuNs, (uint)count);
		}
	}

	/// <summary>Which heap owns an address and its protection — the pointer-validity check.</summary>
	public static (uint HeapBase, uint HeapSize, uint PageSize, uint Protect)? HeapLookup(uint addr)
	{
		if (TryGetAbiVersion() is null || !IsGuestUp)
		{
			return null;
		}

		if (RefiiHostHeapLookup(addr, out uint heapBase, out uint heapSize, out uint page,
				out uint protect) == 0)
		{
			return null;
		}

		return (heapBase, heapSize, page, protect);
	}

	/// <summary>One deliberate guest word write. Refused on unwritable or untracked pages.</summary>
	public static bool Poke(uint addr, uint value) =>
		TryGetAbiVersion() is not null && IsGuestUp && RefiiHostPoke(addr, value) != 0;

	/// <summary>Installs the remaster hook on guest <c>CAugmentationComponent</c> locomotion check (<c>sub_825A0098</c>)
	/// so weapon sparkles and augmentation effects remain active while walking and running.</summary>
	public static bool InstallWeaponAugmentHook() =>
		TryGetAbiVersion() is not null && IsGuestUp && RefiiHostInstallWeaponAugmentHook() != 0;

	/// <summary>Arms a recording probe on a guest function start. Slot, or a negative refusal:
	/// -1 all slots used, -2 not a recompiled function start, -3 no runtime.</summary>
	public static int ProbeSet(uint addr) =>
		TryGetAbiVersion() is null || !IsGuestUp ? -3 : RefiiHostProbeSet(addr);

	public static bool ProbeClear(int slot) =>
		TryGetAbiVersion() is not null && RefiiHostProbeClear(slot) != 0;

	public readonly record struct ProbeState(
		uint Addr, ulong Hits, uint LastLr, ulong[] LastArgs);

	public static ProbeState? ProbeRead(int slot)
	{
		if (TryGetAbiVersion() is null)
		{
			return null;
		}

		var args = new ulong[6];
		if (RefiiHostProbeRead(slot, out uint addr, out ulong hits, out uint lastLr, args,
				(uint)args.Length) == 0)
		{
			return null;
		}

		return new ProbeState(addr, hits, lastLr, args);
	}

	/// <summary>Arms a one-shot snapshot of the device constant shadows on a probe's next return.</summary>
	public static bool ProbeSnapshotArm(int slot)
	{
		try
		{
			return TryGetAbiVersion() is not null && RefiiHostProbeSnapshotArm(slot) != 0;
		}
		catch (EntryPointNotFoundException)
		{
			return false;
		}
	}

	/// <summary>The snapshot: VS c0..c255 then PS c0..c255 (2048 floats), or null before one is taken.</summary>
	public static float[]? ProbeSnapshotRead(int slot)
	{
		var values = new float[2048];
		try
		{
			return TryGetAbiVersion() is not null && RefiiHostProbeSnapshotRead(slot, values, (uint)values.Length) != 0
				? values
				: null;
		}
		catch (EntryPointNotFoundException)
		{
			return null;
		}
	}

	/// <summary>The last debug-call/debug-lua outcome the tick pump recorded, with the
	/// sequence number a poster compares to know its own call finished.</summary>
	public static (ulong Seq, uint Addr, ulong R3, string Note)? DebugCallResult()
	{
		if (TryGetAbiVersion() is null)
		{
			return null;
		}

		var note = new byte[192];
		RefiiHostGetDebugCallResult(out ulong seq, out uint addr, out ulong r3, note,
			(uint)note.Length);
		return (seq, addr, r3, Decode(note));
	}

	/// <summary>
	/// The guest arena's identity for OUT-OF-PROCESS readers: the shared-memory object backing
	/// all of guest memory (bare name — /dev/shm/&lt;name&gt; on Linux, Local\&lt;name&gt; on
	/// Windows) and the host virtual base its views are mapped at (what ReadProcessMemory
	/// needs). Published into the handshake so a FROZEN run — the one state the debug channel
	/// cannot answer from — can still be read.
	/// </summary>
	public static (string Name, ulong Membase)? GetGuestArenaInfo()
	{
		if (TryGetAbiVersion() is null || !IsGuestUp)
		{
			return null;
		}

		var name = new byte[256];
		if (RefiiHostGetGuestArenaInfo(name, (uint)name.Length, out ulong membase) == 0)
		{
			return null;
		}

		return (Decode(name), membase);
	}

	public static uint GetMenuScreenId()
	{
		if (TryGetAbiVersion() is null || !IsGuestUp)
		{
			return Refii.Ui.Engine_UiMenuSources.AnyScreen;
		}

		try
		{
			return RefiiHostGetMenuScreenId();
		}
		catch (EntryPointNotFoundException)
		{
			return Refii.Ui.Engine_UiMenuSources.AnyScreen;
		}
	}

	public static System.Collections.Generic.List<MenuRow> GetMenuRows(out ulong generation)
	{
		var rows = new System.Collections.Generic.List<MenuRow>();
		generation = 0;
		if (TryGetAbiVersion() is null || !IsGuestUp)
		{
			return rows;
		}

		byte[] name = new byte[128];
		byte[] parent = new byte[128];
		byte[] label = new byte[192];
		byte[] icon = new byte[160];
		uint[] words = new uint[5];
		uint total;
		try
		{
			total = RefiiHostGetMenuRow(0, name, (uint)name.Length, parent,
				(uint)parent.Length, out uint firstId, out int firstType, out generation,
				out int firstRating);

			if (total == 0)
			{
				return rows;
			}

			RefiiHostGetMenuRowLabel(0, label, (uint)label.Length);
			rows.Add(WithItem(0, new MenuRow(
				Text(name), Text(parent), firstId, firstType, firstRating, Text(label))));
			for (uint i = 1; i < total; ++i)
			{
				RefiiHostGetMenuRow(i, name, (uint)name.Length, parent, (uint)parent.Length,
					out uint id, out int type, out _, out int rating);
				RefiiHostGetMenuRowLabel(i, label, (uint)label.Length);
				rows.Add(WithItem(i, new MenuRow(Text(name), Text(parent), id, type, rating, Text(label))));
			}
		}
		catch (EntryPointNotFoundException)
		{
			return rows;
		}

		return rows;

		// The row's icon, mark and item (RefiiHostGetMenuRowItem); a title that states none answers no row.
		MenuRow WithItem(uint index, MenuRow row)
		{
			if (RefiiHostGetMenuRowItem(index, icon, (uint)icon.Length, out int emphasis, words, (uint)words.Length) <= index)
			{
				return row;
			}

			return row with
			{
				Icon = Text(icon),
				Emphasis = emphasis,
				Item = Refii.Ui.Engine_UiModel.MenuRowItem.FromWords(words[0], words[1], words[2], words[3], words[4]),
			};
		}

		static string Text(byte[] buffer)
		{
			int end = System.Array.IndexOf(buffer, (byte)0);
			return Encoding.UTF8.GetString(buffer, 0, end < 0 ? buffer.Length : end);
		}
	}

	/// <summary>
	/// The hero's vitals, read straight off its components — where a menu's contents come from.
	/// The `GUI` natives open screens but never populate them.
	/// </summary>
	/// <param name="MoralityBase">
	/// Base, never the number to display: the guest's accessor adds real-estate and worship
	/// bonuses plus an Evil/Holy term held on another component and a global config string.
	/// Same for <paramref name="PurityBase"/> and <paramref name="RenownBase"/>.
	/// </param>
	/// <param name="Strength">
	/// A BODY-MORPH percentage in [0,1] — how the hero LOOKS, not what they have spent. The
	/// experience arrays are the progression. Same for skill and will.
	/// </param>
	/// <param name="AbilityLevels">
	/// Raw levels for ability ids 1..14, indexed by id. Ids 2, 3 and 6 are BUFFED by the guest's
	/// accessor and clamped against a ceiling from the game defs, so those three are the raw
	/// slot rather than the displayed number.
	/// </param>
	public readonly record struct HeroVitals(
		uint Hero,
		int Gold,
		float Health, float BaseHealth, float MaxHealth,
		bool Invulnerable, bool CantDie,
		float MoralityBase, float PurityBase, int RenownBase,
		float MoralityMin, float MoralityMax, float PurityMin, float PurityMax,
		float Strength, float Skill, float Will,
		float[] ExperienceAvailable, float[] ExperienceEarned,
		int[] AbilityLevels)
	{
		/// <summary>True once a hero exists — false for every moment before a save is loaded.</summary>
		public bool Known => Hero != 0u;

		/// <summary>The pools, in the order the guest stores them.</summary>
		public enum Pool { General = 0, Strength = 1, Skill = 2, Will = 3 }

		/// <summary>Spent in a pool. The guest keeps no spent counter; this is the derivation.</summary>
		public float Spent(Pool pool) =>
			ExperienceEarned[(int)pool] - ExperienceAvailable[(int)pool];
	}

	/// <summary>
	/// The guest game loop's own fixed-step arithmetic — whether it is ticking, and if not, why.
	/// A spin sample at <c>0x82277348</c> is the ordinary "no tick is due yet" yield of a 15 Hz
	/// pacer and says nothing about health; <see cref="SimTime"/> is what separates the cases,
	/// because `rex_lhGameLoop_Tick` rewrites <c>game+0x110</c> on EVERY iteration including the
	/// yielding arm, so a live thread with a frozen sim time means the clock stopped.
	/// <see cref="Completed"/> is read from the record the frame-sync index names; the word at
	/// 0x83497124 is a 0/1 slot selector, not a completion count.
	/// </summary>
	public readonly record struct GameLoopPacer(
		uint Game, bool SimulationGate, bool WorldGate, uint QuitByte,
		uint Presented, uint Completed, uint SyncSlot, uint ReaderLock,
		double SimTime, double TimebaseEpoch, double TimebaseTicksPerSecond,
		uint GuiRegisteredPlayers = 0, uint GuiUpdateEnabled = 0, uint GuiDoStringWindow = 0,
		double HudPresentationTime = 0.0, double ScreenPresentationTime = 0.0)
	{
		/// <summary>True once the walk found a game object at all.</summary>
		public bool Known => Game != 0;

		/// <summary>Frames presented and not yet completed — the term the render deadline grows by.</summary>
		public int InFlight => (int)(Presented - Completed);
	}

	/// <summary>Reads the game loop's pacer. See <see cref="GameLoopPacer"/>.</summary>
	public static GameLoopPacer GetGameLoopPacer()
	{
		if (TryGetAbiVersion() is null || !IsGuestUp)
		{
			return default;
		}

		var words = new uint[11];
		var values = new double[5];
		uint game;
		try
		{
			game = RefiiHostGetGameLoopPacer(
				words, (uint)words.Length, values, (uint)values.Length);
		}
		catch (EntryPointNotFoundException)
		{
			return default;
		}

		return new GameLoopPacer(
			game, words[1] != 0u, words[2] != 0u, words[7],
			words[3], words[5], words[4], words[6],
			values[0], values[1], values[2],
			words[8], words[9], words[10], values[3], values[4]);
	}

	/// <summary>
	/// The hero's DERIVED state — the guest accessors' own answers, which add GDB script
	/// constants, real-estate and worship terms and a renown modifier to the base values
	/// <see cref="HeroVitals"/> carries. The logbook's personality bands are tested at 0.375 and
	/// 0.625 of the normalised range, so the base names the wrong title near every threshold.
	/// Sampled by the pump on the guest's own thread, the only one allowed to call them.
	/// </summary>
	public readonly record struct HeroDerived(
		bool Known,
		float Morality, float MoralityMin, float MoralityMax,
		float Purity, float PurityMin, float PurityMax,
		int Renown,
		string TitleTag,
		float[] Attractiveness, float AttractivenessFraction,
		bool DogKnown, int DogCombat, int DogSearching, int DogState, string DogName,
		uint DogNameKey,
		uint[] DogTrickIds,
		int[] Stats, bool StatsKnown,
		float[] SpellDamage,
		uint DogDefinition = 0u,
		uint DogEntity = 0u)
	{
		/// <summary>Morality as a 0..1 position in its own range — what the bands are tested on.</summary>
		public float MoralityFraction => Span(Morality, MoralityMin, MoralityMax);

		/// <summary>Purity as a 0..1 position in its own range.</summary>
		public float PurityFraction => Span(Purity, PurityMin, PurityMax);

		/// <summary>
		/// Whether the dog has learned the trick with this GDB record id — a RECORD ID, not a
		/// name hash: the guest runs `FNV1(name)` through its hash-to-id table before storing.
		/// </summary>
		public bool DogKnowsTrick(uint recordId)
		{
			if (DogTrickIds is null || recordId == 0u)
			{
				return false;
			}

			foreach (uint id in DogTrickIds)
			{
				if (id == recordId)
				{
					return true;
				}
			}

			return false;
		}

		/// <summary>One logbook stat counter by id, or 0 when out of range.</summary>
		public int Stat(int id) =>
			Stats is not null && id >= 0 && id < Stats.Length ? Stats[id] : 0;

		/// <summary>
		/// A degenerate range answers 0.5, not a divide-by-zero.
		/// </summary>
		private static float Span(float value, float min, float max) =>
			max - min <= float.Epsilon ? 0.5f : (value - min) / (max - min);
	}

	/// <summary>
	/// The option LEVELS — gamma, the three volumes and the speaker mode — as the title's own calls
	/// state them; `Known` false until they have. Every level is 0..1. `Speaker` is 0 TV, 1 hi-fi,
	/// 2 surround.
	/// </summary>
	public readonly record struct OptionSliders(
		bool Known, float Gamma, float Sfx, float Music, float Speech, int Speaker);

	/// <summary>
	/// The signature the guest calls back on. See `core/host_asset_reader.h`. `IntPtr` rather
	/// than `byte*` throughout, so none of this needs `unsafe`.
	/// </summary>
	[UnmanagedFunctionPointer(CallingConvention.Cdecl)]
	private delegate int AssetReaderDelegate(
		IntPtr path, IntPtr buffer, uint capacity, IntPtr outSize);

	/// <summary>
	/// Held for the process's lifetime, which is the whole reason this field exists:
	/// `GetFunctionPointerForDelegate` does not keep the delegate alive, and the guest would
	/// then be calling a pointer into reclaimed memory.
	/// </summary>
	private static AssetReaderDelegate? _assetReader;

	/// <summary>
	/// The hash side of the same boundary. See `core/host_asset_reader.h`. Returns the length
	/// written, 0 for "no such hash"; a name that would be truncated is refused, not shortened.
	/// </summary>
	[UnmanagedFunctionPointer(CallingConvention.Cdecl)]
	private delegate uint AssetNameForHashDelegate(uint hash, IntPtr buffer, uint capacity);

	/// <summary>Held for the process's lifetime, for the reason on <see cref="_assetReader"/>.</summary>
	private static AssetNameForHashDelegate? _assetNameResolver;

	/// <summary>Where a hash becomes a path — Godot's index over its own mount.</summary>
	private static Func<uint, string?>? _assetNameSource;

	/// <summary>Where the reader gets its bytes — Godot's own mount.</summary>
	private static Func<string, byte[]?>? _assetSource;

	/// <summary>
	/// Where it gets a LENGTH without reading — see `Engine_GameAssets.FileSize`. Separate from
	/// the byte source: answering a size probe with a read decompresses a whole bank entry.
	/// </summary>
	private static Func<string, long>? _assetSize;

	/// <summary>
	/// Where it writes bytes into the CALLER's buffer — see `Engine_GameAssets.ReadInto`: the first
	/// `destination.Length` bytes and the count, -1 for no such asset. With it a read neither builds a
	/// managed copy of the asset nor copies one, and a short buffer inflates only its front.
	/// </summary>
	private static Func<string, Memory<byte>, int>? _assetReadInto;

	[UnmanagedFunctionPointer(CallingConvention.Cdecl)]
	private delegate int AssetIsOverrideDelegate(IntPtr path, UIntPtr length);

	private static AssetIsOverrideDelegate? _assetIsOverride;
	private static Func<string, bool>? _assetModOwnedSource;
	private static Func<string, bool>? _assetOverrideSource;

	/// <summary>
	/// Points the guest's loaders at Godot's. Call after the mount succeeds, with the SAME reader
	/// every Godot loader uses. Pass null on the way out, so a guest read arriving after teardown
	/// gets "no reader" instead of a torn-down runtime.
	/// </summary>
	public static void SetAssetReader(
		Func<string, byte[]?>? source,
		Func<string, long>? size = null,
		Func<string, Memory<byte>, int>? readInto = null)
	{
		_assetSource = source;
		_assetSize = size;
		_assetReadInto = source is null ? null : readInto;

		if (TryGetAbiVersion() is null)
		{
			return;
		}

		try
		{
			if (source is null)
			{
				RefiiHostSetAssetReader(IntPtr.Zero);
				_assetReader = null;
				return;
			}

			_assetReader ??= ReadAssetForGuest;
			RefiiHostSetAssetReader(Marshal.GetFunctionPointerForDelegate(_assetReader));
		}
		catch (EntryPointNotFoundException)
		{
		}
	}

	[UnmanagedFunctionPointer(CallingConvention.Cdecl)]
	private delegate int ModTextDelegate(IntPtr text, uint length, IntPtr buffer, uint capacity);

	/// <summary>Held for the process's lifetime, for the reason on <see cref="_assetReader"/>.</summary>
	private static ModTextDelegate? _modTextLookup;

	private static Func<string, string>? _modTextSource;

	/// <summary>
	/// Installs the mods' text lookup (<c>Refii.Mods.ModText.Text</c>) for Lua's <c>Refii.text</c> and a
	/// native mod's <c>refii_mod_text</c> (<c>RefiiHostSetModTextLookup</c>, <c>refii_bridge_mod_text</c>).
	/// Called on the GUEST's thread; null unregisters. Tolerates a library without the export.
	/// </summary>
	public static void SetModTextLookup(Func<string, string>? source)
	{
		_modTextSource = source;
		if (TryGetAbiVersion() is null)
		{
			return;
		}

		try
		{
			if (source is null)
			{
				RefiiHostSetModTextLookup(IntPtr.Zero);
				_modTextLookup = null;
				return;
			}

			_modTextLookup ??= ModTextForGuest;
			RefiiHostSetModTextLookup(Marshal.GetFunctionPointerForDelegate(_modTextLookup));
		}
		catch (EntryPointNotFoundException)
		{
		}
	}

	/// <summary>One lookup for the guest: the answer as UTF-8, NUL-terminated and cut to the buffer; -1 on any failure. Never throws.</summary>
	private static int ModTextForGuest(IntPtr text, uint length, IntPtr buffer, uint capacity)
	{
		try
		{
			if (_modTextSource is not { } source || text == IntPtr.Zero || buffer == IntPtr.Zero || capacity == 0u)
			{
				return -1;
			}

			string asked = Marshal.PtrToStringUTF8(text, (int)Math.Min(length, 4096u)) ?? string.Empty;
			byte[] bytes = Encoding.UTF8.GetBytes(source(asked));
			int written = (int)Math.Min((uint)bytes.Length, capacity - 1u);

			// Never split a UTF-8 sequence: back off to the start of the last whole character.
			while (written > 0 && written < bytes.Length && (bytes[written] & 0xC0) == 0x80)
			{
				written--;
			}

			Marshal.Copy(bytes, 0, buffer, written);
			Marshal.WriteByte(buffer, written, 0);
			return written;
		}
		catch (Exception)
		{
			return -1;
		}
	}

	/// <summary>
	/// Points the guest VFS at Godot's asset override predicate.
	/// When Godot serves a virtual mod overlay (e.g. globals.gdb or book.babel), this answers 1
	/// so native VFS prioritizes Godot's virtual asset over loose files on disk; 2 when
	/// <paramref name="modOwned"/> states a mod contributed or composed the path, which a title that
	/// must tell a mod's file from the game's own loose file reads (core/host_asset_reader.h
	/// HostAssetOverrideKind).
	/// </summary>
	public static void SetAssetOverrideQuery(Func<string, bool>? source, Func<string, bool>? modOwned = null)
	{
		// Preserve the predicate callback so HostBridge.Initialize() can bind it once ABI is ready.
		_assetOverrideSource = source;
		_assetModOwnedSource = modOwned;

		if (TryGetAbiVersion() is null)
		{
			return;
		}

		try
		{
			if (source is null)
			{
				RefiiHostSetAssetOverrideQuery(IntPtr.Zero);
				_assetIsOverride = null;
				return;
			}

			_assetIsOverride ??= IsAssetOverrideForGuest;
			RefiiHostSetAssetOverrideQuery(Marshal.GetFunctionPointerForDelegate(_assetIsOverride));
		}
		catch (EntryPointNotFoundException)
		{
		}
	}

	private static int IsAssetOverrideForGuest(IntPtr path, UIntPtr length)
	{
		try
		{
			if (_assetOverrideSource is not { } source || path == IntPtr.Zero || length == UIntPtr.Zero)
			{
				return 0;
			}

			string name = Marshal.PtrToStringUTF8(path, (int)length);
			if (string.IsNullOrEmpty(name))
			{
				return 0;
			}

			bool isOverride = source(name);
			bool modOwned = isOverride && _assetModOwnedSource is { } owned && owned(name);
			if (isOverride || name.EndsWith(".gdb", StringComparison.OrdinalIgnoreCase) || name.EndsWith(".babel", StringComparison.OrdinalIgnoreCase))
			{
				GD.Print($"[host-bridge] IsAssetOverride('{name}') => {isOverride}{(modOwned ? " (mod-owned)" : string.Empty)}");
			}
			return modOwned ? 2 : isOverride ? 1 : 0;
		}
		catch (Exception e)
		{
			GD.PushWarning($"[host-assets] the guest's asset override check threw: {e.Message}");
			return 0;
		}
	}

	/// <summary>
	/// Points the guest's HASH lookups at Godot's index — the other half of
	/// <see cref="SetAssetReader"/>, registered separately. Pass null on the way out.
	/// </summary>
	public static void SetAssetNameResolver(Func<uint, string?>? source)
	{
		// Preserve the resolver delegate unconditionally.
		_assetNameSource = source;

		if (TryGetAbiVersion() is null)
		{
			return;
		}

		try
		{
			if (source is null)
			{
				RefiiHostSetAssetNameResolver(IntPtr.Zero);
				_assetNameResolver = null;
				return;
			}

			_assetNameResolver ??= AssetNameForHashForGuest;
			RefiiHostSetAssetNameResolver(
				Marshal.GetFunctionPointerForDelegate(_assetNameResolver));
		}
		catch (EntryPointNotFoundException)
		{
		}
	}

	/// <summary>
	/// One hash, resolved on the GUEST's thread. No exception may escape — it is reached through
	/// a C function pointer, so every failure leaves as a 0 return. ASCII, one byte per
	/// character; a name that would not fit is reported as a miss rather than truncated.
	/// </summary>
	private static uint AssetNameForHashForGuest(uint hash, IntPtr buffer, uint capacity)
	{
		try
		{
			if (_assetNameSource is not { } source || buffer == IntPtr.Zero || capacity == 0u)
			{
				return 0;
			}

			if (source(hash) is not { Length: > 0 } name)
			{
				return 0;
			}

			if ((uint)name.Length >= capacity)
			{
				return 0;
			}

			for (int i = 0; i < name.Length; i++)
			{
				Marshal.WriteByte(buffer, i, (byte)name[i]);
			}

			Marshal.WriteByte(buffer, name.Length, 0);
			return (uint)name.Length;
		}
		catch (Exception e)
		{
			GD.PushWarning($"[host-assets] the guest's hash lookup threw: {e.Message}");
			return 0;
		}
	}

	/// <summary>
	/// One asset, read on the GUEST's thread. No exception may escape — it is called through a
	/// C function pointer, so every failure leaves as a 0 return. Asked twice per asset: once
	/// with `buffer` null to state the size, once to fill it.
	/// </summary>
	private static int ReadAssetForGuest(IntPtr path, IntPtr buffer, uint capacity, IntPtr outSize)
	{
		long allocFrom = GC.GetAllocatedBytesForCurrentThread();
		try
		{
			if (outSize != IntPtr.Zero)
			{
				Marshal.WriteInt32(outSize, 0);
			}

			if (_assetSource is not { } source || path == IntPtr.Zero)
			{
				return 0;
			}

			if (Marshal.PtrToStringUTF8(path) is not { Length: > 0 } name)
			{
				return 0;
			}

			// A null buffer is the caller learning the length, not a failure — and nearly every
			// ask is this one, so it is served from the entry rather than a decompressed copy.
			if (buffer == IntPtr.Zero || capacity == 0u)
			{
				long known = _assetSize?.Invoke(name) ?? -1;
				if (known < 0)
				{
					return 0;
				}

				if (outSize != IntPtr.Zero)
				{
					Marshal.WriteInt32(outSize, (int)known);
				}

				return 1;
			}

			if (_assetReadInto is { } readInto)
			{
				var destination = new Refii.Core.Engine_NativeMemory(buffer, (int)Math.Min(capacity, int.MaxValue));
				int written = readInto(name, destination.Memory);
				if (written < 0)
				{
					return 0;
				}

				if (outSize != IntPtr.Zero)
				{
					Marshal.WriteInt32(outSize, written);
				}

				return 1;
			}

			if (source(name) is not { } bytes)
			{
				return 0;
			}

			if (outSize != IntPtr.Zero)
			{
				Marshal.WriteInt32(outSize, bytes.Length);
			}

			int copied = bytes.Length < (int)capacity ? bytes.Length : (int)capacity;
			Marshal.Copy(bytes, 0, buffer, copied);
			if (outSize != IntPtr.Zero)
			{
				Marshal.WriteInt32(outSize, copied);
			}

			return 1;
		}
		catch (Exception e)
		{
			GD.PushWarning($"[host-assets] the guest's asset read threw: {e.Message}");
			return 0;
		}
		finally
		{
			Refii.Core.Engine_GcFrames.Note(ref Refii.Core.Engine_GcFrames.GuestAssetReadBytes,
				GC.GetAllocatedBytesForCurrentThread() - allocFrom);
		}
	}

	/// <summary>The stated option levels. See <see cref="OptionSliders"/>.</summary>
	public static OptionSliders GetOptionSliders()
	{
		if (TryGetAbiVersion() is null || !IsGuestUp)
		{
			return default;
		}

		try
		{
			if (RefiiHostGetOptionsSliders(
					out float gamma, out float sfx, out float music, out float speech,
					out uint speaker) == 0)
			{
				return default;
			}

			return new OptionSliders(true, gamma, sfx, music, speech, unchecked((int)speaker));
		}
		catch (EntryPointNotFoundException)
		{
			return default;
		}
	}

	/// <summary>
	/// Moves whenever something an open menu lists changes (an ability level, experience, an item,
	/// gold, a drawn weapon, or the pump's own item/ability/shop/repair actions): the open list
	/// re-reads its sources when it does. 0 before the bridge answers.
	/// </summary>
	public static uint UiDataGeneration()
	{
		if (TryGetAbiVersion() is null)
		{
			return 0u;
		}

		try
		{
			return RefiiHostUiDataGeneration();
		}
		catch (EntryPointNotFoundException)
		{
			return 0u;
		}
	}

	/// <summary>
	/// The crime screen as its opener stated it: `Charge` is the charge text it was given
	/// (localised), `Service` the community-service text key.
	/// </summary>
	public readonly record struct CrimeScreen(
		bool Known, int Fine, bool CanPayFine, bool CanServe, string Charge, string Service);

	/// <summary>One completed quest, as the logbook's page shows it.</summary>
	public readonly record struct CompletedQuest(uint Record, int Money, int Renown, string Conclusion);

	/// <summary>
	/// The guest's expression/job timing meter, as its own methods state it (`@Hud.ExpressionMeter`)
	/// — the guest runs it and judges it. <see cref="Known"/> is false until it is stated.
	/// </summary>
	public readonly record struct ExpressionMeter(
		bool Known, bool On, int Mode, bool ArmedA, bool ArmedB, bool Latched,
		float Position, float Speed, bool HitA, bool HitB, bool Live, int Band)
	{
		/// <summary>
		/// Whether the meter is UP. Not the ON byte alone: `sub_823D1A48`, the path a plain
		/// expression takes, zeroes the mode at its head (0x823d1a70), calls
		/// `TurnOnExpressionMeter` at 0x823d1c1c and then clears that very byte one instruction
		/// later — `*(_BYTE *)a1 = 0` at 0x823d1c20 — before arming through `sub_823D2120`.
		/// So an emote runs with On and Mode both 0 while the guest's own registry-9 toggle says
		/// the meter is open, and every host test on On alone folded it shut.
		///
		/// These four are exactly what the teardown clears together, so their union cannot
		/// outlive the meter: `sub_823D1458` writes +0, +4 and +38 to 0 and drops the latch at
		/// +14 through its two armed bytes (0x823d1474..0x823d157c).
		///
		/// <see cref="Live"/> is `+38` = `+0x26`, the meter's ACTIVE LATCH: raised by
		/// `sub_823D1F98` at 0x823D1FE0 on every fill start, cleared by `TurnOffExpressionMeter`
		/// at 0x823D147C, and read by `sub_823D1A48` at 0x823D1BBC as the gate that skips its own
		/// reset block. On its own it is the most direct answer to "is the meter up".
		/// </summary>
		public bool Up => Known && (On || Mode != 0 || Latched || Live);

		/// <summary>
		/// Whether a pip is travelling. The flags say so; the mode does not.
		/// </summary>
		/// <remarks>
		/// Never gate this on the mode. `sub_823D1A48` — the path a plain expression takes —
		/// ZEROES `meter+4` at 0x823D1A70 and never writes it again. The only writers of the mode
		/// are the Lua natives: `StartExpressionMeterPip` sets 1 or 2 (`sub_82307050`,
		/// `*(meter+4) = (arg3 == 0) + 1`) and `StartBarJobFill` sets 3, used only by
		/// `qj001_jobwoodsman`, `qj002_jobblacksmith` and `qj003_jobbartender`. For the same
		/// reason `IsExpressionMeterActive`, which tests `meter+4 != 0`, reads false for a C++
		/// expression; its active latch is `+0x26` (<see cref="Live"/>) instead.
		///
		/// `sub_823D2120` raises `+0x0C` for the hero and `+0x0D` for a henchman, and seeds the
		/// position and speed behind the `+0x0E` latch.
		///
		/// The latch is part of the gate: `sub_822ACBF8` tests both terms and DESTROYS its draw
		/// item when the pair goes false:
		///
		///     822acc94  if ( *(_BYTE *)a1 || *(_BYTE *)(a1 + 1) ) v8 = 1;   ; +12 || +13
		///     822accb4  if ( v8 && *(_BYTE *)(a1 + 2) )                     ; && +14
		///     822ace74  sub_82A97368(...)                                   ; else destroy
		///
		/// (`a1` is `meter+12`, so `a1+2` is `meter+14`.) `sub_823D1D40` drops the arm bytes one
		/// at a time and only clears the latch once neither remains, so the two disagree for the
		/// window between one player finishing and the other.
		/// </remarks>
		public bool PipRunning => Known && (ArmedA || ArmedB) && Latched;
	}



	/// <summary>
	/// The targeted-citizen readout's own record, as `sub_82176808` rebuilds it each HUD tick.
	///
	/// <see cref="Present"/> is the guest's raise BYTE, not a channel heuristic: `sub_823FD8E8`
	/// sets it and `sub_823FD9B0` clears it. <see cref="SlideSet"/> is the opinion axis + 1,
	/// stated VERBATIM on registry 10 — 1 love, 2 fear, 3 ugly.
	///
	/// MARRY AND DIVORCE ARE NOT IN THE ORDER THEY LOOK: the record's +36/+40 publish under
	/// `TargetedDivorce` and +44/+48 under `TargetedMarry` (0x823fd884 / 0x823fd8c4). Both
	/// toggles always publish; only one of the two VALUES does.
	/// </summary>
	public readonly record struct TargetedQuery(
		bool Known, bool Present, uint Entity, bool Gift, float GiftValue, bool Button,
		int SlideSet, bool Divorce, float DivorceValue, bool Marry, float MarryValue,
		float Opinion);




	private static ExpressionMeter _expressionMeter;

	/// <summary>The meter as the title last stated it; unknown until it does.</summary>
	public static ExpressionMeter GetExpressionMeter() => _expressionMeter;

	/// <summary>A title states its timing meter: the eleven values in the record's order, or none
	/// to say there is no meter.</summary>
	public static void StateExpressionMeter(float[]? values) =>
		_expressionMeter = values is { Length: >= 11 }
			? new ExpressionMeter(
				true, values[0] != 0f, (int)values[1], values[2] != 0f, values[3] != 0f,
				values[4] != 0f, values[5], values[6], values[7] != 0f, values[8] != 0f,
				values[9] != 0f, (int)values[10])
			: default;

	/// <summary>
	/// A hard mod's published value by key (<c>combat.stamina</c>), or false when its hook has
	/// published none. The hook states the value; this never samples the guest.
	/// </summary>
	public static bool TryGetModValue(string key, out float value)
	{
		value = 0f;
		if (TryGetAbiVersion() is null || string.IsNullOrEmpty(key))
		{
			return false;
		}

		byte[] bytes = Utf8Scratch(key, out int length);
		try
		{
			return RefiiHostGetModValue(bytes, (uint)length, out value) != 0;
		}
		catch (EntryPointNotFoundException)
		{
			return false;
		}
	}

	/// <summary>Hands a hard mod a setting by key; its hook reads it with refii::mods::Setting.</summary>
	public static void SetModSetting(string key, float value)
	{
		if (TryGetAbiVersion() is null || string.IsNullOrEmpty(key))
		{
			return;
		}

		byte[] bytes = System.Text.Encoding.UTF8.GetBytes(key);
		try
		{
			RefiiHostSetModSetting(bytes, (uint)bytes.Length, value);
		}
		catch (EntryPointNotFoundException)
		{
		}
	}

	/// <summary>
	/// <paramref name="text"/> as UTF-8 in a kept buffer, valid until this thread's next call: for a
	/// native call that copies what it needs, on a path a mod or the HUD takes every frame.
	/// </summary>
	private static byte[] Utf8Scratch(string text, out int length)
	{
		int needed = System.Text.Encoding.UTF8.GetByteCount(text);
		if (_utf8Scratch is null || _utf8Scratch.Length < needed)
		{
			_utf8Scratch = new byte[Math.Max(needed, 256)];
		}

		length = System.Text.Encoding.UTF8.GetBytes(text, _utf8Scratch);
		return _utf8Scratch;
	}

	[System.ThreadStatic]
	private static byte[]? _utf8Scratch;

	private static byte[]? _modEventBuffer;

	private static char[]? _modEventChars;

	/// <summary>
	/// Every mod event the payload posted since the last drain, oldest first: the kind
	/// (<c>Refii.Mods.ModApi.Events[kind]</c>) and the fields as the bus states them for Lua
	/// (<c>victim=123,damage=4.5,name='x'</c>). Only kinds in the mask set by
	/// <see cref="SetModEventMask"/> are ever posted.
	/// </summary>
	public static List<(int Kind, string Fields)> DrainModEvents()
	{
		var events = new List<(int, string)>();
		DrainModEvents(events);
		return events;
	}

	/// <summary><see cref="DrainModEvents()"/> into a kept list, replacing what it held. Main thread only.</summary>
	public static void DrainModEvents(List<(int Kind, string Fields)> events)
	{
		events.Clear();
		if (TryGetAbiVersion() is null || !IsGuestUp)
		{
			return;
		}

		_modEventBuffer ??= new byte[64 * 1024];
		_modEventChars ??= new char[System.Text.Encoding.UTF8.GetMaxCharCount(_modEventBuffer.Length)];
		try
		{
			for (int round = 0; round < 64; round++)
			{
				uint written = RefiiHostDrainModEvents(_modEventBuffer, (uint)_modEventBuffer.Length);
				if (written == 0)
				{
					break;
				}

				int count = System.Text.Encoding.UTF8.GetChars(_modEventBuffer, 0, (int)written, _modEventChars, 0);
				ReadModEventLines(_modEventChars.AsSpan(0, count), events);
			}
		}
		catch (EntryPointNotFoundException)
		{
		}
	}

	/// <summary>The drained records, <c>&lt;kind&gt;\t&lt;fields&gt;\n</c>: only the fields become a string.</summary>
	internal static void ReadModEventLines(ReadOnlySpan<char> text, List<(int Kind, string Fields)> events)
	{
		while (text.Length != 0)
		{
			int end = text.IndexOf('\n');
			ReadOnlySpan<char> line = end < 0 ? text : text[..end];
			text = end < 0 ? default : text[(end + 1)..];
			int tab = line.IndexOf('\t');
			if (tab > 0 && int.TryParse(line[..tab], out int kind))
			{
				events.Add((kind, new string(line[(tab + 1)..])));
			}
		}
	}

	/// <summary>
	/// Which event kinds the payload posts for <see cref="DrainModEvents"/>; an empty mask posts
	/// nothing. Words 0 and 1 go through the two mask exports, the words past them through the
	/// tick's <c>mask_word</c> command (mod_commands.cpp), so a kind at or past 128 takes effect
	/// on the next guest tick.
	/// </summary>
	public static void SetModEventMask(in Refii.Mods.ModEventMask mask)
	{
		if (TryGetAbiVersion() is null)
		{
			return;
		}

		try
		{
			RefiiHostSetModEventMaskHigh(mask.Word(1));
			RefiiHostSetModEventMask(mask.Word(0));
		}
		catch (EntryPointNotFoundException)
		{
		}

		for (int word = 2; word < Refii.Mods.ModEventMask.Words; word++)
		{
			PostModCommand($"mask_word event {word} {mask.Word(word):x}");
		}
	}

	/// <summary>
	/// One edge of a mod's input action ("mod.action", player 1 or 2, pressed or released); the
	/// guest raises the InputAction event on its next tick. Edges only, never per-frame state.
	/// </summary>
	public static void PostModInput(string action, int player, bool pressed)
	{
		if (TryGetAbiVersion() is null || string.IsNullOrEmpty(action) || (player != 1 && player != 2))
		{
			return;
		}

		byte[] bytes = System.Text.Encoding.UTF8.GetBytes(action);
		try
		{
			RefiiHostPostModInput(bytes, (uint)bytes.Length, player, pressed ? 1 : 0);
		}
		catch (EntryPointNotFoundException)
		{
		}
	}

	/// <summary>
	/// A helper call or a query for the guest tick, one line: <c>give_gold 123 50</c>,
	/// <c>query 7 health 123</c> (the grammar is Fable2/src/vfs/src/hooks/mod_commands.cpp). A query's
	/// reply arrives through <see cref="DrainModEvents"/> under kind 1000 as
	/// <c>id=7,ok=true,current=80,max=120</c>.
	/// </summary>
	public static void PostModCommand(string line)
	{
		if (TryGetAbiVersion() is null || string.IsNullOrEmpty(line))
		{
			return;
		}

		byte[] bytes = Utf8Scratch(line, out int length);
		try
		{
			RefiiHostPostModCommand(bytes, (uint)length);
		}
		catch (EntryPointNotFoundException)
		{
		}
	}

	private static byte[]? _modErrorBuffer;

	/// <summary>The mods' Lua errors (init and handlers) since the last drain, oldest first.</summary>
	public static List<string> DrainModErrors()
	{
		var errors = new List<string>();
		if (TryGetAbiVersion() is null || !IsGuestUp)
		{
			return errors;
		}

		_modErrorBuffer ??= new byte[32 * 1024];
		try
		{
			for (int round = 0; round < 8; round++)
			{
				uint written = RefiiHostDrainModErrors(_modErrorBuffer, (uint)_modErrorBuffer.Length);
				if (written == 0)
				{
					break;
				}

				string text = System.Text.Encoding.UTF8.GetString(_modErrorBuffer, 0, (int)written);
				errors.AddRange(text.Split('\n', StringSplitOptions.RemoveEmptyEntries));
			}
		}
		catch (EntryPointNotFoundException)
		{
		}

		return errors;
	}

	/// <summary>
	/// A Godot script verdict handler: called by the payload ON THE GUEST THREAD, inside a seam,
	/// with the event's kind and table text; writes "cancel=1,&lt;field&gt;=&lt;number&gt;,…" into
	/// <c>output</c> and returns its length (0 for no change).
	/// </summary>
	[UnmanagedFunctionPointer(CallingConvention.Cdecl)]
	public delegate int ModVerdictFn(int kind, IntPtr fields, UIntPtr length, IntPtr output, UIntPtr capacity);

	/// <summary>Installs (or with null removes) the verdict handler; keep the delegate alive while installed.</summary>
	public static void SetModVerdictHandler(ModVerdictFn? handler)
	{
		if (TryGetAbiVersion() is null)
		{
			return;
		}

		try
		{
			RefiiHostSetModVerdictHandler(handler);
		}
		catch (EntryPointNotFoundException)
		{
		}
	}

	/// <summary>
	/// A read-only query ("VERB ARGS...") answered at once from INSIDE the verdict handler, on the
	/// guest thread while the payload waits in the seam: the answer text ("ok=…,<fields>") and the
	/// native result — its length, or REFII_MOD_READ_OUTSIDE (-1) anywhere else, REFII_MOD_READ_REFUSED
	/// (-2) for a verb that is not a read.
	/// </summary>
	public static (int Result, string Answer) ModVerdictRead(string line)
	{
		if (TryGetAbiVersion() is null)
		{
			return (-1, string.Empty);
		}

		try
		{
			byte[] bytes = Encoding.UTF8.GetBytes(line);
			byte[] answer = new byte[2048];
			int result = RefiiHostModVerdictRead(bytes, (UIntPtr)bytes.Length, answer, (UIntPtr)answer.Length);
			return (result, result > 0 ? Encoding.UTF8.GetString(answer, 0, Math.Min(result, answer.Length - 1)) : string.Empty);
		}
		catch (EntryPointNotFoundException)
		{
			return (-1, string.Empty);
		}
	}

	/// <summary>Which event kinds the verdict handler is asked about; an empty mask asks about none. The words cross as <see cref="SetModEventMask"/>'s do.</summary>
	public static void SetModVerdictMask(in Refii.Mods.ModEventMask mask)
	{
		if (TryGetAbiVersion() is null)
		{
			return;
		}

		try
		{
			RefiiHostSetModVerdictMaskHigh(mask.Word(1));
			RefiiHostSetModVerdictMask(mask.Word(0));
		}
		catch (EntryPointNotFoundException)
		{
		}

		for (int word = 2; word < Refii.Mods.ModEventMask.Words; word++)
		{
			PostModCommand($"mask_word verdict {word} {mask.Word(word):x}");
		}
	}

	/// <summary>The guest entity a mod HANDLE stands for (what Lua and Godot scripts hold), or 0.</summary>
	public static uint ModHandleAddress(uint handle)
	{
		if (TryGetAbiVersion() is null || handle == 0)
		{
			return 0;
		}

		try
		{
			return RefiiHostModHandleAddress(handle);
		}
		catch (EntryPointNotFoundException)
		{
			return 0;
		}
	}

	/// <summary>The used prefix of a fixed buffer — the bridge reports how many it filled.</summary>
	internal static uint[] Trim(uint[] buffer, uint count)
	{
		uint used = count < (uint)buffer.Length ? count : (uint)buffer.Length;
		var trimmed = new uint[used];
		System.Array.Copy(buffer, trimmed, (int)used);
		return trimmed;
	}

	/// <summary>A NUL-terminated buffer from the bridge as a string.</summary>
	internal static string Utf8(byte[] buffer)
	{
		int length = System.Array.IndexOf(buffer, (byte)0);
		return length <= 0
			? string.Empty
			: System.Text.Encoding.UTF8.GetString(buffer, 0, length);
	}

	/// <summary>Reads the hero's vitals. See <see cref="HeroVitals"/>.</summary>
	/// <summary>
	/// The player hero's entity address, or 0 — the handle every hero read resolves first.
	/// </summary>
	public static uint HeroEntityAddress()
	{
		if (TryGetAbiVersion() is null || !IsGuestUp)
		{
			return 0;
		}

		// The two buffers are write-only here: this call wants the return value. Per-thread
		// scratch, because two threads asking at once must not share one; see `_poseTransforms`.
		uint[] words = _heroVitalWords ??= new uint[17];
		float[] values = _heroVitalValues ??= new float[20];
		try
		{
			return RefiiHostGetHeroVitals(words, (uint)words.Length, values, (uint)values.Length);
		}
		catch (EntryPointNotFoundException)
		{
			return 0;
		}
	}



	[System.ThreadStatic]
	private static uint[]? _heroVitalWords;

	[System.ThreadStatic]
	private static float[]? _heroVitalValues;

	public static HeroVitals GetHeroVitals()
	{
		if (TryGetAbiVersion() is null || !IsGuestUp)
		{
			return default;
		}

		// Per-thread scratch, not fresh: the HUD asks every frame, and the record built below
		// copies out of the buffers before returning. Matches `_poseTransforms`.
		uint[] words = _heroVitalWords ??= new uint[17];
		float[] values = _heroVitalValues ??= new float[20];
		uint hero;
		try
		{
			hero = RefiiHostGetHeroVitals(
				words, (uint)words.Length, values, (uint)values.Length);
		}
		catch (EntryPointNotFoundException)
		{
			return default;
		}

		if (hero == 0)
		{
			return default;
		}

		var abilities = new int[15];
		for (int id = 1; id <= 14; ++id)
		{
			abilities[id] = (int)words[2 + id];
		}

		var available = new float[4];
		var earned = new float[4];
		for (int pool = 0; pool < 4; ++pool)
		{
			available[pool] = values[12 + pool];
			earned[pool] = values[16 + pool];
		}

		return new HeroVitals(
			hero, (int)words[0],
			values[0], values[1], values[2],
			(words[2] & 1u) != 0u, (words[2] & 2u) != 0u,
			values[3], values[4], (int)words[1],
			values[5], values[6], values[7], values[8],
			values[9], values[10], values[11],
			available, earned, abilities);
	}

	/// <summary>
	/// The hero's inventory, one entry per RECORD, which is not one item: the guest keeps an
	/// `std::list` of records each carrying a `Count`, and the item count is their sum (what
	/// `GetNumberOfItems` returns). `RecordId` is not unique either — two records share one id
	/// whenever the first still holds instance data.
	/// </summary>
	/// <summary>
	/// The inventory screen's mode — 1 Items, 3 Weapons, 0 when unknown.
	///
	/// Screen 15 is one screen with two lists. Its enter shows the tab strip on the tab of the
	/// list it builds (`sub_826B8348(strip, 4)` items, `5` weapons, in `sub_826D13D0`), and that
	/// stated tab is the mode.
	/// </summary>
	public static uint GetInventoryScreenMode() =>
		GetPauseState() is { } pause ? pause.CurrentTab switch { 4u => 1u, 5u => 3u, _ => 0u } : 0u;




	/// <summary>Reads the live fade. See <see cref="ScreenFade"/>.</summary>
	public static ScreenFade GetScreenFade()
	{
		if (TryGetAbiVersion() is null || !IsGuestUp)
		{
			return default;
		}

		float[] rgba = new float[4];
		try
		{
			uint address = RefiiHostGetScreenFade(rgba, out float progress, out uint locked);
			return new ScreenFade(
				address, new Color(rgba[0], rgba[1], rgba[2], rgba[3]), progress, locked != 0u);
		}
		catch (EntryPointNotFoundException)
		{
			return default;
		}
	}

	/// <summary>Reads the will assignment state. Never null; unknown reads as an empty state.</summary>
	public static SpellAssignState GetSpellAssign()
	{
		var assigned = new int[SpellAssignState.Slots];
		var offered = new uint[SpellAssignState.Slots];
		for (int i = 0; i < assigned.Length; ++i)
		{
			assigned[i] = -1;
		}

		if (TryGetAbiVersion() is null || !IsGuestUp)
		{
			return new SpellAssignState(0u, assigned, offered, -1, false, false);
		}

		uint[] words = new uint[14];
		try
		{
			RefiiHostGetSpellAssign(words);
		}
		catch (EntryPointNotFoundException)
		{
			return new SpellAssignState(0u, assigned, offered, -1, false, false);
		}

		for (int i = 0; i < SpellAssignState.Slots; ++i)
		{
			assigned[i] = words[1 + i] == uint.MaxValue ? -1 : (int)words[1 + i];
			offered[i] = words[6 + i];
		}

		return new SpellAssignState(
			words[0], assigned, offered,
			words[11] == uint.MaxValue ? -1 : (int)words[11],
			GetSpellWheel(1).Known, GetSpellWheel(2).Known);
	}

	/// <summary>One spell assignment widget as its own methods stated it (`@Spell.Widget`).</summary>
	private sealed record SpellWidget(int Player, bool Blade, bool Shown, Engine_UiModel.SpellWheelState State);

	private static readonly Dictionary<uint, SpellWidget> _spellWidgets = new();

	/// <summary>
	/// A title states a spell assignment widget. <paramref name="words"/> is the widget's 53:
	/// address, row, highest row, five counts, five cells, five lists of eight; null on a close.
	/// A widget is shown from its "show" to its "close"; <paramref name="blade"/> is the show's
	/// own mode argument, null on every other op.
	/// </summary>
	public static void StateSpellWidget(string op, uint widget, int player, bool? blade, uint[]? words)
	{
		if (op == "close" || words is null)
		{
			_spellWidgets.Remove(widget);
			return;
		}

		_spellWidgets.TryGetValue(widget, out SpellWidget? before);
		_spellWidgets[widget] = new SpellWidget(
			player, blade ?? before?.Blade ?? false, op == "show" || (op == "move" && before is { Shown: true }),
			DecodeSpellWidget(words));
	}

	/// <summary>Forgets every stated spell widget: the HUD and its screens were rebuilt.</summary>
	public static void ForgetSpellWidgets() => _spellWidgets.Clear();

	/// <summary>
	/// The quick spell wheel's cursor for <paramref name="player"/> (1 or 2), as the widget's own
	/// methods stated it. <see cref="Engine_UiModel.SpellWheelState.Known"/> is false while the
	/// wheel is closed.
	/// </summary>
	public static Engine_UiModel.SpellWheelState GetSpellWheel(int player)
	{
		foreach (SpellWidget widget in _spellWidgets.Values)
		{
			if (widget.Shown && !widget.Blade && widget.Player == player)
			{
				return widget.State;
			}
		}

		return default;
	}

	/// <summary>The abilities book's own spell blade, while it is shown.</summary>
	public static Engine_UiModel.SpellWheelState GetSpellBlade()
	{
		foreach (SpellWidget widget in _spellWidgets.Values)
		{
			if (widget.Shown && widget.Blade)
			{
				return widget.State;
			}
		}

		return default;
	}

	private static Engine_UiModel.SpellWheelState DecodeSpellWidget(uint[] words)
	{
		var counts = new int[Engine_UiModel.SpellAssignState.Slots];
		var cells = new int[Engine_UiModel.SpellAssignState.Slots];
		var lists = new int[Engine_UiModel.SpellAssignState.Slots][];
		for (int r = 0; r < counts.Length; ++r)
		{
			counts[r] = (int)words[3 + r];
			cells[r] = (int)words[8 + r];
			lists[r] = new int[counts[r]];
			for (int c = 0; c < counts[r]; ++c)
			{
				lists[r][c] = (int)words[13 + (8 * r) + c];
			}
		}

		return new Engine_UiModel.SpellWheelState(
			words[0], (int)words[1], unchecked((int)words[2]), counts, cells, lists);
	}


	/// <summary>
	/// Puts a will on a d-pad slot, through the guest's own committing wrapper. Commit on
	/// LEFT/RIGHT, not on A: the guest's widget writes the moment the carousel moves
	/// (`sub_826B5330` / `sub_826B5240`), while A copies the current row's will down into every
	/// later row that also offers it.
	///
	/// The writer validates nothing, so only send a pair
	/// <see cref="SpellAssignState.Offers"/> admits.
	/// </summary>
	public static uint AssignSpell(int slot, int spell, int player = 1) =>
		PostFrontEndAction(player == 2 ? $"SpellAssign:{slot}:{spell}:2" : $"SpellAssign:{slot}:{spell}");

	/// <summary>
	/// What the guest would gate its own New Game and Load Game entries on. Two of the three
	/// numbers are plain guest memory; the count is a guest CALL and so arrives from the tick
	/// pump's sample rather than live. See RefiiHostGetSaveState.
	/// </summary>
	public static SaveState GetSaveState()
	{
		try
		{
			RefiiHostGetSaveState(out uint signedIn, out int count, out uint limit);
			return new SaveState(signedIn != 0, count, (int)limit);
		}
		catch (EntryPointNotFoundException)
		{
			// An older native library. Unknown rather than empty, so the caller holds the entry
			// back instead of asserting there are no saves.
			return new SaveState(false, -1, 0);
		}
	}

	/// <summary>Asks the pump to re-take the saved-game count on its next front-end tick.</summary>
	public static void RefreshSaveState()
	{
		try
		{
			RefiiHostRefreshSaveState();
		}
		catch (EntryPointNotFoundException)
		{
		}
	}

	/// <summary>
	/// Loads the save at <paramref name="index"/> in the guest's own enumerated list — an INDEX,
	/// not a name: the guest identifies a save by the address of its handle slot inside its own
	/// vector (`Frontend_SetStartGameTarget` takes `*(screen+84) + 4*i` and
	/// `*(screen+148) + 40*i + 12`). Posted, not called: the commit allocates, rewrites
	/// `dword_83496AB8` and mutates refcounts non-atomically, so it must run on the tick thread.
	/// </summary>
	public static uint LoadSave(int index) => PostFrontEndAction($"LoadSave:{index}");

	/// <summary>
	/// The languages the guest has installed, in its own table order.
	/// <see cref="Language.Name"/> is the DISPLAY NAME the guest spawns its menu entry with, not
	/// the locale code — `sub_826E1C98` hands it to the spawner and the front end resolves it
	/// through Babel, so a caller should use the same book. The INDEX is the row: language
	/// entries are dispatched by command 302 with that index, never by name.
	/// </summary>
	public static System.Collections.Generic.List<Language> GetLanguages()
	{
		var languages = new System.Collections.Generic.List<Language>();

		// IsGuestUp, NOT IsRunning: IsRunning is false once the guest is past its boot, which
		// is precisely when the front end asks.
		if (TryGetAbiVersion() is null || !IsGuestUp)
		{
			return languages;
		}

		var names = new byte[2048];
		uint count;
		uint current;

		try
		{
			count = RefiiHostGetLanguages(out current, names, (uint)names.Length);
		}
		catch (EntryPointNotFoundException)
		{
			// An older native library. Degrade to "no languages" rather than throwing: a menu
			// that throws while being built takes the whole front end with it.
			return languages;
		}

		int at = 0;
		for (uint i = 0; i < count; ++i)
		{
			int end = System.Array.IndexOf(names, (byte)0, at);
			if (end < 0)
			{
				break;
			}

			languages.Add(new Language(
				(int)i,
				System.Text.Encoding.UTF8.GetString(names, at, end - at),
				i == current));
			at = end + 1;
		}

		return languages;
	}

	/// <summary>
	/// The locale code of the language the guest is running in ("en-uk", "hu-hu", ...): the
	/// current row of its installed-language table, the code it hands XLip when it switches.
	/// Null when no guest is up, the table is not built yet, or the native library predates it —
	/// the caller keeps the culture it has rather than guessing one.
	/// </summary>
	public static string? GetCurrentCulture()
	{
		if (TryGetAbiVersion() is null || !IsGuestUp)
		{
			return null;
		}

		var code = new byte[32];
		uint length;
		try
		{
			length = RefiiHostGetCurrentCulture(code, (uint)code.Length);
		}
		catch (EntryPointNotFoundException)
		{
			return null;
		}

		return length == 0 || length >= code.Length
			? null
			: System.Text.Encoding.ASCII.GetString(code, 0, (int)length);
	}

	/// <summary>
	/// Pump health. Three numbers because they fail differently: zero ticks is a hook that
	/// never landed, ticks climbing with a non-empty queue is the guest not yet ready, and
	/// ticks climbing with nothing drained is nobody posting.
	/// </summary>
	public static (ulong Ticks, ulong Drained, uint Queued) GuestTickPump()
	{
		RefiiHostGuestTickPump(out ulong ticks, out ulong drained, out uint queued);
		return (ticks, drained, queued);
	}

	/// <summary>The live rings, empty slots dropped.</summary>
	public static List<SceneStream> GetSceneStreams()
	{
		uint[] words = new uint[24];
		RefiiHostGetSceneStreams(words);

		var streams = new List<SceneStream>(4);
		for (int slot = 0; slot < 4; ++slot)
		{
			int at = slot * 6;
			if (words[at] == 0)
			{
				continue;
			}

			streams.Add(new SceneStream(words[at], words[at + 1], words[at + 2],
				words[at + 3], words[at + 4], words[at + 5]));
		}

		return streams;
	}

	/// <summary>The chosen loading art, or null when there is no loading screen.</summary>
	public static LoadingArt? GetLoadingScreenArt()
	{
		var buffer = new byte[128];
		try
		{
			uint ok = RefiiHostGetLoadingScreenArt(
				out uint type, out uint body, buffer, (uint)buffer.Length);
			if (body == 0 && ok == 0)
			{
				return null;
			}

			int end = System.Array.IndexOf(buffer, (byte)0);
			return new LoadingArt(type, body,
				System.Text.Encoding.UTF8.GetString(buffer, 0, end < 0 ? 0 : end));
		}
		catch (EntryPointNotFoundException)
		{
			return null;
		}
	}

	/// <summary>
	/// The guest's own "the world is loaded and ready" byte — `game + 0x105`, cleared by
	/// `CmdLevelChange_Update` when the load begins and set when its nine-state machine
	/// finishes. Not the same as the loading screen coming down.
	/// </summary>
	public static bool WorldIsReady()
	{
		try
		{
			return RefiiHostWorldIsReady() != 0;
		}
		catch (EntryPointNotFoundException)
		{
			return false;
		}
	}

	/// <summary>The handshake, or null when no loading screen object exists.</summary>
	public static LoadingScreen? GetLoadingScreen()
	{
		if (TryGetAbiVersion() is null || !IsGuestUp)
		{
			return null;
		}

		return RefiiHostGetLoadingScreen(out uint manager, out uint screen, out float fade,
			out uint requested, out uint done) != 0
			? new LoadingScreen(manager, screen, fade, requested != 0, done != 0)
			: null;
	}

	private static uint[]? _fenceArms;

	/// <summary>
	/// The engine-preloading gate the level load waits on (state 7), or null when the chain did
	/// not resolve. The object is the guest's "3D Engine" and the predicate its own
	/// `IsEnginePreLoading`. `Deadline` is the render deadline barrier at +176, NOT a
	/// retired-frame count: standing still while `Submitted` climbs past it is the resting
	/// state. The arms say which of the three producers is holding the flag.
	/// </summary>
	public static RenderFence? GetRenderFence()
	{
		_fenceArms ??= new uint[5];
		return RefiiHostGetRenderFence(out uint manager, out double submitted,
			out double deadline, out uint busy, _fenceArms) != 0
			? new RenderFence(manager, submitted, deadline, busy != 0,
				_fenceArms[0], _fenceArms[1], _fenceArms[2], _fenceArms[3], _fenceArms[4])
			: null;
	}

	/// <summary>Whether the title's front-end GUI exists, as its own construction and destruction state it.</summary>
	public static bool FrontEndGuiIsUp => (Stated(StatedValue.FrontEnd) & 1u) != 0u;

	/// <summary>
	/// The front end as the title's own calls state it (payload_bridge.h, stated value 13): the
	/// active screen, that screen's mode, whether the title takes A, and the Load Game card the
	/// guest's cursor is on. False, and every value zero, while the front-end GUI does not exist.
	/// On Fable 2's title the modes are 9 legal sequence, 0 idle, 8 the idle attract movie (A only
	/// skips it, the latch stays), 10 start sequence, 11 handing over to the menu, 12 dialog; on its
	/// main menu 0 the list, 1 locked through a transition,
	/// 2 to 4 the hero card, 5 the Load Game cards, 6 a warning, 7 leaving for the title.
	/// </summary>
	public static bool TryGetFrontEnd(
		out FrontEndScreen screen, out uint state, out bool canPressA, out uint cursor)
	{
		uint word = Stated(StatedValue.FrontEnd);
		bool up = (word & 1u) != 0u;
		screen = up ? (FrontEndScreen)((word >> 1) & 3u) : FrontEndScreen.None;
		state = screen switch
		{
			FrontEndScreen.Title => (word >> 8) & 0xFFu,
			FrontEndScreen.MainMenu => (word >> 16) & 0xFFu,
			_ => 0u,
		};
		canPressA = up && (word & 8u) != 0u;
		cursor = up ? word >> 24 : 0u;
		return up;
	}

	/// <summary>Fable 2's title mode while its idle attract movie plays (frontend_stated.cpp).</summary>
	public const uint TitleAttractMode = 8u;

	/// <summary>
	/// The pad Fable 2's title took A from (stated value 13, bits 6-7: r5 of the title's input slot
	/// on the arm that writes the session's pad, frontend_stated.cpp) — the only pad the main menu
	/// answers. 0 until a title has taken A.
	/// </summary>
	public static int FrontEndPad => (int)((Stated(StatedValue.FrontEnd) >> 6) & 3u);

	/// <summary>
	/// Whether the front end has a warning raised — from the title's OpenDialog until that
	/// dialog's UNFREEZE (stated value 13, bits 4-5: the screen that raised it) — and whether its
	/// owner is taking the answer yet: the title in mode 12, the main menu in mode 6. <paramref
	/// name="title"/> is set when the title raised it (it takes A from any pad).
	/// </summary>
	public static bool TryGetFrontEndDialog(out bool accepting, out bool title)
	{
		uint word = Stated(StatedValue.FrontEnd);
		FrontEndScreen owner = (word & 1u) != 0u ? (FrontEndScreen)((word >> 4) & 3u) : FrontEndScreen.None;
		title = owner == FrontEndScreen.Title;
		accepting = owner switch
		{
			FrontEndScreen.Title => ((word >> 8) & 0xFFu) == 12u,
			FrontEndScreen.MainMenu => ((word >> 16) & 0xFFu) == 6u,
			_ => false,
		};
		return owner != FrontEndScreen.None;
	}

	[System.ThreadStatic]
	private static uint[]? _presentationWords;

	[System.ThreadStatic]
	private static byte[]? _presentationNames;

	/// <summary>
	/// A count that moves whenever <see cref="GetPresentations"/> would answer differently: a
	/// presentation made or destroyed, or one of its gates written with another value.
	/// </summary>
	public static uint PresentationsGeneration()
	{
		if (TryGetAbiVersion() is null)
		{
			return 0;
		}

		try
		{
			return RefiiHostPresentationsGeneration();
		}
		catch (EntryPointNotFoundException)
		{
			return 0;
		}
	}

	/// <summary>
	/// Every presentation the guest has live — the HUD, whichever menus are open, and the front
	/// end — as its own create and destroy state them, with the two gates as it last wrote them.
	/// </summary>
	public static List<Presentation> GetPresentations()
	{
		uint count = RefiiHostGetPresentations(null, 0, null, 0);
		if (count == 0)
		{
			return new List<Presentation>();
		}

		// Headroom: the guest makes presentations while this reads, so the second call can find
		// more than the probe. Stride 5: address, creation serial, name offset, state, screen + 1.
		int neededWords = (int)(count * 5 + 32);
		int neededNames = (int)(count * 96 + 512);
		if (_presentationWords is null || _presentationWords.Length < neededWords)
		{
			_presentationWords = new uint[Math.Max(neededWords, 128)];
		}
		if (_presentationNames is null || _presentationNames.Length < neededNames)
		{
			_presentationNames = new byte[Math.Max(neededNames, 2048)];
		}
		uint[] words = _presentationWords;
		byte[] names = _presentationNames;
		uint written = RefiiHostGetPresentations(
			words, (uint)neededWords, names, (uint)neededNames);

		int usable = (int)Math.Min(written, (uint)(neededWords / 5));
		var live = new List<Presentation>(usable);
		for (int i = 0; i < usable; ++i)
		{
			uint offset = words[i * 5 + 2];
			string name = string.Empty;
			if (offset != 0xFFFFFFFFu && offset < neededNames)
			{
				int end = (int)offset;
				while (end < neededNames && names[end] != 0)
				{
					++end;
				}

				name = Encoding.ASCII.GetString(names, (int)offset, end - (int)offset);
			}

			live.Add(new Presentation(
				words[i * 5], words[i * 5 + 1], name, words[i * 5 + 3], (int)words[i * 5 + 4] - 1));
		}

		return live;
	}

	/// <summary>Describes the current video frame's plane geometry.</summary>
	public readonly record struct VideoPlanes(
		int LumaWidth, int LumaHeight, int ChromaWidth, int ChromaHeight,
		int LumaBytes, int ChromaBytes);

	/// <summary>
	/// Copies the current frame's three planes, tightly packed, for GPU conversion. Pass
	/// nulls to probe the geometry without copying.
	/// </summary>
	public static bool TryGetVideoPlanes(
		byte[]? y, byte[]? cr, byte[]? cb, out VideoPlanes planes)
	{
		uint[] desc = new uint[6];
		int ok = RefiiHostGetVideoPlanes(
			y, (uint)(y?.Length ?? 0),
			cr, (uint)(cr?.Length ?? 0),
			cb, (uint)(cb?.Length ?? 0),
			desc);

		planes = new VideoPlanes(
			(int)desc[0], (int)desc[1], (int)desc[2], (int)desc[3],
			(int)desc[4], (int)desc[5]);
		return ok != 0;
	}

	/// <summary>The rumble the guest last asked for, 0..65535 per motor.</summary>
	public static bool TryGetVibration(int userIndex, out uint left, out uint right)
	{
		left = 0;
		right = 0;
		return RefiiHostGetVibration((uint)userIndex, out left, out right) != 0;
	}

	[DllImport(Library)]
	private static extern int RefiiHostGetRequestedVideo(
		byte[]? outBuffer, uint capacity, out uint outSize, out uint outGeneration);

	[DllImport(Library)]
	private static extern uint RefiiHostFinishMovie();

	[DllImport(Library)]
	private static extern uint RefiiHostGetMovieSound(
		out uint sound, [Out] uint[] ids, [Out] float[] volumes, uint capacity);

	[DllImport(Library)]
	private static extern uint RefiiHostMovieState();

	[DllImport(Library)]
	private static extern uint RefiiHostGetWorldSprites(
		[Out] uint[] words, uint wordCapacity, [Out] float[] values, uint valueCapacity,
		[Out] byte[] text, uint textCapacity, uint maxIcons);


	[DllImport(Library)]
	private static extern int RefiiHostGetMovieRect(
		out float x0, out float y0, out float x1, out float y1);

	[DllImport(Library)]
	private static extern void RefiiHostPostMouseLook(float yawDegrees, float pitchDegrees);

	[DllImport(Library)]
	private static extern void RefiiHostSetMouseLookDevice(int mouse);

	[DllImport(Library)]
	private static extern void RefiiHostSetHostAim(int active, float fwdX, float fwdY, float fwdZ);

	[DllImport(Library)]
	private static extern void RefiiHostPostMouseWheel(int notches);

	[DllImport(Library)]
	private static extern uint RefiiHostGetMouseLook(double[] outValues, uint count);

	[DllImport(Library)]
	private static extern uint RefiiHostGetCameraStatements(uint after, [Out] uint[] outWords, uint maxRecords);

	[DllImport(Library)]
	private static extern void RefiiHostStateCamera(float[] eleven);

	[DllImport(Library)]
	private static extern void RefiiHostStateCameraViews(float[] words, uint count);

	[DllImport(Library)]
	private static extern int RefiiHostGetCameraOffset(int player, [Out] float[] seven);

	[DllImport(Library)]
	private static extern int RefiiHostGetPlayerTwoHoldPrompt([Out] float[] at, [Out] byte[] title, uint capacity);

	[DllImport(Library)]
	private static extern uint RefiiHostGetPromptTitles([Out] byte[] text, uint capacity);

	[DllImport(Library)]
	private static extern int RefiiHostGetPlayerValue(int player, [MarshalAs(UnmanagedType.LPUTF8Str)] string key, out float value);

	[DllImport(Library)]
	private static extern int RefiiHostGetPlayerText(int player, [MarshalAs(UnmanagedType.LPUTF8Str)] string key, [Out] byte[] text, uint capacity);

	[DllImport(Library)]
	private static extern uint RefiiHostGetClassicSceneLights([Out] float[] values, uint capacity);

	[DllImport(Library)]
	private static extern ulong RefiiHostGetEntityUid(uint entity);

	[DllImport(Library)]
	private static extern uint RefiiHostGetAnimWalkStats(uint[] outWords, uint capacity);


	[DllImport(Library)]
	private static extern uint RefiiHostGetFrontEndState();

	[DllImport(Library)]
	private static extern uint RefiiHostIsInGame();

	[DllImport(Library)]
	private static extern uint RefiiHostGetCoopPlayerEntities(out uint one, out uint two);

	[DllImport(Library)]
	private static extern int RefiiNetCoopSend([MarshalAs(UnmanagedType.LPUTF8Str)] string text);

	[DllImport(Library)]
	private static extern uint RefiiNetCoopReceive(byte[] outText, uint capacity);

	[DllImport(Library)]
	private static extern int RefiiNetSyncConnect();

	[DllImport(Library)]
	private static extern int RefiiNetSyncState();

	[DllImport(Library)]
	private static extern int RefiiNetSyncSend(byte[] data, uint length);

	[DllImport(Library)]
	private static extern uint RefiiNetSyncReceive(byte[] outBytes, uint capacity);

	[DllImport(Library)]
	private static extern void RefiiNetSyncClose();

	[DllImport(Library)]
	private static extern uint RefiiHostGetSimTick();

	[System.Runtime.InteropServices.DllImport(Library, CallingConvention = System.Runtime.InteropServices.CallingConvention.Cdecl)]
	private static extern uint RefiiHostGetSimClock(out double seconds, out uint tick);

	[DllImport(Library)]
	private static extern uint RefiiHostGetStatedFx(uint[] words, uint capacity);

	[DllImport(Library)]
	private static extern uint RefiiHostGetStatedFxCount();

	[System.Runtime.InteropServices.DllImport(Library, CallingConvention = System.Runtime.InteropServices.CallingConvention.Cdecl)]
	private static extern int RefiiHostSetTimeBaseMode(int mode, out ulong capped, out ulong granted, out ulong watchdog);

	[System.Runtime.InteropServices.DllImport(Library, CallingConvention = System.Runtime.InteropServices.CallingConvention.Cdecl)]
	private static extern void RefiiHostAdvanceTimeBase(uint steps);

	[System.Runtime.InteropServices.DllImport(Library, CallingConvention = System.Runtime.InteropServices.CallingConvention.Cdecl)]
	private static extern void RefiiHostSetTimeBaseFrozen(uint frozen);

	[DllImport(Library)]
	private static extern uint RefiiHostGetGuestFrame();

	[DllImport(Library)]
	private static extern uint RefiiHostGetActiveFx(uint[]? outWords, uint maxEffects);

	[DllImport(Library)]
	private static extern uint RefiiHostGetFxSpline(uint handle, float[] outPoints, uint maxPoints);

	[DllImport(Library)]
	private static extern uint RefiiHostGetFxDoubleTrailRing(uint nodeOrHandle, float[] outPoints, uint maxPoints);

	[DllImport(Library)]
	private static extern uint RefiiHostGetHudMeters([Out] uint[]? hashes, [Out] float[]? values, uint capacity);


	[DllImport(Library)]
	private static extern uint RefiiHostGetDrunkPlayers();

	/// <summary>Which heroes are past the drunk warp's threshold: bit 0 hero one, bit 1 hero two.</summary>
	public static uint GetDrunkPlayers()
	{
		try
		{
			return RefiiHostGetDrunkPlayers();
		}
		catch (EntryPointNotFoundException)
		{
			return 0u;
		}
		catch (DllNotFoundException)
		{
			return 0u;
		}
	}

	[DllImport(Library)]
	private static extern uint RefiiHostGetHudPulsers([Out] uint[]? hashes, [Out] float[]? phases, uint capacity);

	[DllImport(Library)]
	private static extern uint RefiiHostGetHudPulserStates([Out] uint[]? hashes, [Out] float[]? phases, [Out] uint[]? modes, uint capacity);


	[DllImport(Library)]
	private static extern uint RefiiHostGetHudTexts([Out] uint[]? hashes, uint capacity, [Out] byte[]? text, uint textCapacity);

	[DllImport(Library)]
	private static extern int RefiiHostGetSpellAssign([Out] uint[] outWords);



	[DllImport(Library)]
	private static extern uint RefiiHostGetMenuRow(
		uint index, [Out] byte[]? name, uint nameCapacity, [Out] byte[]? parent,
		uint parentCapacity, out uint id, out int eventType, out ulong generation,
		out int rating);

	[DllImport(Library)]
	private static extern void RefiiHostSetGuiPredicate([MarshalAs(UnmanagedType.LPUTF8Str)] string name, uint kind, uint bits);

	[DllImport(Library)]
	private static extern uint RefiiHostTakeMenuChunks(
		byte[] outUtf8, uint utf8Capacity, byte[] outKinds, uint kindsCapacity, uint maxRecords);


	[DllImport(Library)]
	private static extern void RefiiHostMenuChunkCounters(
		out ulong outRecorded, out uint outPending, out ulong outDropped);

	[DllImport(Library)]
	private static extern int RefiiHostMenuPopulating();

	[DllImport(Library)]
	private static extern uint RefiiHostTakeGuiNativeCalls(
		byte[] outUtf8, uint utf8Capacity, byte[] outKinds, uint kindsCapacity, uint maxRecords);

	[DllImport(Library)]
	private static extern void RefiiHostGuiNativeCounters(
		out ulong outIntercepted, out uint outPending, out uint outNamed);

	[DllImport(Library)]
	private static extern uint RefiiHostGetMenuRowLabel(
		uint index, [Out] byte[]? outLabel, uint labelCapacity);

	/// <summary>RefiiHostGetMenuRowItem (ABI 270): row `index`'s icon, mark and five item words; the row count.</summary>
	[DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
	private static extern uint RefiiHostGetMenuRowItem(
		uint index, byte[] outIcon, uint iconCapacity, out int outEmphasis, uint[] outWords, uint wordCapacity);

	[DllImport(Library)]
	private static extern uint RefiiHostGetMenuScreenId();

	[DllImport(Library)]
	private static extern uint RefiiHostPeek(uint addr, [Out] uint[] outWords, uint count);

	[DllImport(Library)]
	private static extern uint RefiiHostGetGuestThreads(
		[Out] uint[] outTids, [Out] uint[] outFlags, [Out] int[] outPriority, [Out] uint[] outCpu,
		[Out] uint[] outR1, [Out] uint[] outLr, [Out] byte[] outNames, uint nameStride,
		uint capacity);

	[DllImport(Library)]
	private static extern uint RefiiHostGuestBacktrace(uint tid, [Out] uint[] outFrames,
		uint capacity);

	[DllImport(Library)]
	private static extern uint RefiiHostGetKernelObjects(
		[Out] uint[] outHandles, [Out] uint[] outTypes, [Out] byte[] outNames, uint nameStride,
		uint capacity, out uint total);

	[DllImport(Library)]
	private static extern uint RefiiHostGetPerfCounters(
		[Out] long[] outValues, [Out] byte[] outNames, uint nameStride, uint capacity);

	[DllImport(Library)]
	private static extern uint RefiiHostGetKernelExports(
		uint first, uint onlyUnimplemented, [Out] uint[] outOrdinals, [Out] uint[] outFlags,
		[Out] byte[] outNames, uint nameStride, uint capacity, out uint total);

	[DllImport(Library)]
	private static extern int RefiiHostGetCvarCount();

	[DllImport(Library)]
	private static extern uint RefiiHostGetCvarInfo(
		uint index, [Out] byte[] outName, uint nameCapacity, [Out] byte[] outValue,
		uint valueCapacity, [Out] byte[] outDefault, uint defaultCapacity,
		[Out] byte[] outCategory, uint categoryCapacity, out uint type, out uint lifecycle,
		out uint flags);

	[DllImport(Library)]
	private static extern uint RefiiHostSetCvar([MarshalAs(UnmanagedType.LPUTF8Str)] string name, [MarshalAs(UnmanagedType.LPUTF8Str)] string value);

	[DllImport(Library)]
	private static extern uint RefiiHostVfsResolve(
		string guestPath, [Out] byte[] outAbsolute, uint absoluteCapacity, [Out] byte[] outLink,
		uint linkCapacity, out ulong size, out uint attributes);

	[DllImport(Library)]
	private static extern uint RefiiHostGetGuestSockets(
		[Out] uint[] outHandles, [Out] uint[] outPorts, [Out] ulong[] outNative, uint capacity);

	[DllImport(Library)]
	private static extern uint RefiiHostGetGuestArenaInfo(
		[Out] byte[] outName, uint nameCapacity, out ulong membase);

	[DllImport(Library)]
	private static extern void RefiiHostFrameMark();

	[DllImport(Library)]
	private static extern uint RefiiHostGetPerfSnapshots(
		[Out] long[] outValues, [Out] byte[] outNames, uint nameStride, uint capacity);

	[DllImport(Library)]
	private static extern void RefiiHostTraceMessage([MarshalAs(UnmanagedType.LPUTF8Str)] string text);

	[DllImport(Library)]
	private static extern int RefiiHostTraceStartup();

	[DllImport(Library)]
	private static extern ulong RefiiHostTraceZoneBegin([MarshalAs(UnmanagedType.LPUTF8Str)] string name);

	[DllImport(Library)]
	private static extern void RefiiHostTraceZoneEnd(ulong zone);

	[DllImport(Library)]
	private static extern void RefiiHostTracePlot([MarshalAs(UnmanagedType.LPUTF8Str)] string name, double value);

	[DllImport(Library)]
	private static extern void RefiiHostTraceGpuPasses(
		byte[] names, uint nameStride, long[] cpuUs, long[] gpuUs, uint count);

	// ---- hang watchdog (hang_watchdog.cpp) ----

	[DllImport(Library)]
	private static extern void RefiiHostFrameAlive(ulong frame);

	[DllImport(Library)]
	private static extern uint RefiiHostRegisterFramePhase([MarshalAs(UnmanagedType.LPUTF8Str)] string name);

	[DllImport(Library)]
	private static extern void RefiiHostSetFramePhase(uint index);

	[DllImport(Library)]
	private static extern void RefiiHostSetHostLogPath([MarshalAs(UnmanagedType.LPUTF8Str)] string path);

	[DllImport(Library)]
	private static extern uint RefiiHostHangReport(
		[Out] byte[] outPath, uint pathCapacity, out double lastStallSeconds, out int stalled);

	[DllImport(Library)]
	private static extern uint RefiiHostHeapLookup(
		uint addr, out uint heapBase, out uint heapSize, out uint pageSize, out uint protect);

	[DllImport(Library)]
	private static extern uint RefiiHostPoke(uint addr, uint value);

	[DllImport(Library)]
	private static extern uint RefiiHostInstallWeaponAugmentHook();

	[DllImport(Library)]
	private static extern int RefiiHostProbeSet(uint addr);

	[DllImport(Library)]
	private static extern uint RefiiHostProbeClear(int slot);

	[DllImport(Library)]
	private static extern uint RefiiHostProbeSnapshotArm(int slot);

	[DllImport(Library)]
	private static extern uint RefiiHostProbeSnapshotRead(int slot, [Out] float[] outValues, uint capacity);

	[DllImport(Library)]
	private static extern uint RefiiHostProbeRead(
		int slot, out uint addr, out ulong hits, out uint lastLr, [Out] ulong[] outArgs,
		uint argsCapacity);

	[DllImport(Library)]
	private static extern uint RefiiHostGetDebugCallResult(
		out ulong seq, out uint addr, out ulong r3, [Out] byte[] outNote, uint noteCapacity);

	[DllImport(Library)]
	private static extern uint RefiiHostGetTimeOfDay(
		out float fraction, out int dayCount, out int frozen, out int moonPhase);

	[DllImport(Library)]
	private static extern uint RefiiHostGetWeather(
		out float snowSize, out float snowFallSpeed, out float rainSize, out float rainDensity);

	[DllImport(Library)]
	private static extern uint RefiiHostGetThemeStack(uint[] outWords, uint maxEntries);

	[DllImport(Library)]
	private static extern uint RefiiHostGetThemeBlends(uint[] outWords, uint maxEntries);

	[DllImport(Library)]
	private static extern int RefiiHostGetRenderFence(
		out uint manager, out double submitted, out double deadline, out uint busy,
		uint[] outArms);

	[DllImport(Library)]
	private static extern void RefiiHostGetInitMarkers(uint[] outWords);

	[DllImport(Library)]
	private static extern uint RefiiHostGetEntities(
		uint[] outWords, uint maxEntities, out uint outUnknown,
		uint[] outUnknownVtables, uint maxUnknownVtables, out uint outUnknownVtableCount);

	[DllImport(Library)]
	private static extern uint RefiiHostGetEntityPose(
		uint entity, float[] outTransforms, int[] outParents, uint maxBones, out uint outSpace,
		out uint outTorn);

	[DllImport(Library)]
	private static extern uint RefiiHostGetEntityRestPose(
		uint entity, float[] outTransforms, int[] outParents, uint maxBones);

	[DllImport(Library)]
	private static extern uint RefiiHostGetEntityBoneNames(
		uint entity, byte[] outNames, uint stride, uint maxBones);

	[DllImport(Library)]
	private static extern uint RefiiHostGetEntityBoneParents(uint entity, int[] outParents, uint maxBones);

	[DllImport(Library)]
	private static extern uint RefiiHostGetEntityComponents(
		uint entity, uint[] outPairs, uint maxPairs);

	[DllImport(Library)]
	private static extern uint RefiiHostGetEntityMorphParts(
		uint entity, uint[] outHashes, uint maxHashes, out uint outBuilt);

	[DllImport(Library)]
	private static extern uint RefiiHostGetEntityMeshParts(
		uint entity, uint[] outHashes, uint maxHashes, out uint outTotal);

	[DllImport(Library)]
	private static extern uint RefiiHostGetDeadEntities(ulong[] outUids, uint maxUids);

	[DllImport(Library)]
	private static extern int RefiiHostGetSubtitles();

	[DllImport(Library)]
	private static extern int RefiiHostGetPauseState(
		out uint screenId, out uint modalId, out uint modalMode, out uint currentTab);


	[DllImport(Library)]
	private static extern uint RefiiHostGetMapMarkers(uint[] outHashes, float[] outValues, uint cap);



	[DllImport(Library)]
	private static extern void RefiiHostGetSceneStreams(uint[] outWords);

	[DllImport(Library)]
	private static extern uint RefiiHostGetClassicFrame(float[] outValues, uint capacity);

	[DllImport(Library)]
	private static extern uint RefiiHostGetClassicLights(float[] outValues, uint capacity);

	[DllImport(Library)]
	private static extern uint RefiiHostGetClassicMists(float[] outValues, uint capacity);

	[DllImport(Library)]
	private static extern uint RefiiHostGetClassicParticles(float[] outValues, uint capacity);

	[DllImport(Library)]
	private static extern void RefiiHostSetGuestRender(int on);

	/// <summary>
	/// Runs (true) or skips (false) the guest's own scene draw, or hands it back to
	/// REFII_GUEST_RENDER (null): a reference instrument for the classic renderer, whose constant
	/// setters then fill the device shadow. False when the native build predates the export.
	/// </summary>
	public static bool SetGuestRender(bool? on)
	{
		if (TryGetAbiVersion() is null)
		{
			return false;
		}
		try
		{
			RefiiHostSetGuestRender(on is null ? -1 : on.Value ? 1 : 0);
			return true;
		}
		catch (EntryPointNotFoundException)
		{
			return false;
		}
	}

	/// <summary>Floats in a particle group's header, and per record (see the native export).</summary>
	public const int ClassicParticleHeader = 340;

	/// <summary>Floats of a trail group's header (RefiiHostGetClassicTrails); its points follow, 8 floats each.</summary>
	public const int ClassicTrailHeader = 288;

	private static bool _classicTrailsMissing;

	/// <summary>
	/// The render scene's trails and double trails: out[0] groups, out[1] floats used, then per group
	/// a <see cref="ClassicTrailHeader"/>-float header and (c + 3) x 8 control-point floats.
	/// </summary>
	public static uint GetClassicTrails(float[] values)
	{
		if (_classicTrailsMissing || TryGetAbiVersion() is null || !IsGuestUp)
		{
			return 0;
		}
		try
		{
			return RefiiHostGetClassicTrails(values, (uint)values.Length);
		}
		catch (EntryPointNotFoundException)
		{
			_classicTrailsMissing = true;
			return 0;
		}
	}

	[DllImport(Library)]
	private static extern uint RefiiHostGetClassicTrails([Out] float[] outValues, uint capacity);

	/// <summary>Words per decal in <see cref="GetDecals"/>: 15 stated, 12 of the owner's matrix.</summary>
	public const int DecalWords = 27;

	private static bool _decalsMissing;

	/// <summary>
	/// The decals the guest stated, oldest first (RefiiHostGetDecals): per decal [0] serial, [1]
	/// owner twin, [2] texture name hash, [3..6] position, [7..10] direction, [11]/[12] half sizes,
	/// [13] life, [14] flag (float bits where a float), [15..26] the owner's matrix rows.
	/// <paramref name="serial"/> moves when the set changed.
	/// </summary>
	public static uint GetDecals(uint[] words, out uint serial)
	{
		serial = 0;
		if (_decalsMissing || TryGetAbiVersion() is null || !IsGuestUp)
		{
			return 0;
		}
		try
		{
			return RefiiHostGetDecals(words, (uint)words.Length, out serial);
		}
		catch (EntryPointNotFoundException)
		{
			_decalsMissing = true;
			return 0;
		}
	}

	[DllImport(Library)]
	private static extern uint RefiiHostGetDecals([Out] uint[] outWords, uint capacity, out uint outSerial);

	/// <summary>Floats before the lights in <see cref="GetClassicWeather"/>; each light is 8.</summary>
	public const int ClassicWeatherHeader = 80;

	private static bool _classicWeatherMissing;

	/// <summary>
	/// What the title's rain and snow draws read past the theme and wind: [0] a light zone is up,
	/// [1..3] its colour, [4] the guest clock, [5] n lights, then per light position xyz, radius,
	/// colour rgb, 0 — guest axes (RefiiHostGetClassicWeather).
	/// </summary>
	public static uint GetClassicWeather(float[] values)
	{
		if (_classicWeatherMissing || TryGetAbiVersion() is null || !IsGuestUp)
		{
			return 0;
		}
		try
		{
			return RefiiHostGetClassicWeather(values, (uint)values.Length);
		}
		catch (EntryPointNotFoundException)
		{
			_classicWeatherMissing = true;
			return 0;
		}
	}

	[DllImport(Library)]
	private static extern uint RefiiHostGetClassicWeather([Out] float[] outValues, uint capacity);
	public const int ClassicParticleRecord = 12;

	/// <summary>
	/// The render scene's particle groups as their twins hold them: [0] groups, [1] floats
	/// used, then per group a header and its records. Returns the floats used, 0 when none.
	/// </summary>
	public static uint GetClassicParticles(float[] values)
	{
		if (TryGetAbiVersion() is null || !IsGuestUp)
		{
			return 0;
		}
		return RefiiHostGetClassicParticles(values, (uint)values.Length);
	}

	/// <summary>Floats per mist in <see cref="GetClassicMists"/>.</summary>
	public const int ClassicMistStride = 36;

	/// <summary>The render scene's mist volumes, 36 floats each (see the native export).</summary>
	public static uint GetClassicMists(float[] values)
	{
		if (TryGetAbiVersion() is null || !IsGuestUp)
		{
			return 0;
		}
		return RefiiHostGetClassicMists(values, (uint)values.Length);
	}

	/// <summary>Floats per light in <see cref="GetClassicLights"/>.</summary>
	public const int ClassicLightStride = 20;

	/// <summary>
	/// The render scene's active lights as the light manager holds them, 20 floats each (see
	/// the native export for the layout). Returns the lights written, or 0 before the scene is up.
	/// </summary>
	public static uint GetClassicLights(float[] values)
	{
		if (TryGetAbiVersion() is null || !IsGuestUp)
		{
			return 0;
		}
		return RefiiHostGetClassicLights(values, (uint)values.Length);
	}

	/// <summary>Floats per light in <see cref="GetClassicSceneLights"/>.</summary>
	public const int ClassicSceneLightStride = 36;

	/// <summary>
	/// Every light of the render scene, not only the manager's active list (see the native export
	/// for the 36-float layout). Returns the lights written.
	/// </summary>
	public static uint GetClassicSceneLights(float[] values)
	{
		if (TryGetAbiVersion() is null || !IsGuestUp)
		{
			return 0;
		}
		try
		{
			return RefiiHostGetClassicSceneLights(values, (uint)values.Length);
		}
		catch (EntryPointNotFoundException)
		{
			return 0;
		}
	}

	/// <summary>
	/// The frame-constant sources the title's render passes read — ambient, sun direction,
	/// lightmap factor, normal-map darkening, the five-row atmosphere block, the scene's theme
	/// block, the sun light and the wind — as 148 floats (see the native export for the layout). Returns
	/// the floats written, or 0 before the lighting object exists.
	/// </summary>
	public static uint GetClassicFrame(float[] values)
	{
		if (TryGetAbiVersion() is null || !IsGuestUp)
		{
			return 0;
		}
		return RefiiHostGetClassicFrame(values, (uint)values.Length);
	}

	private static bool _classicThemeMissing;

	/// <summary>
	/// The scene's whole theme block (194 parameters x 4) and the sky object's sun colour and
	/// direction, moon direction and phase, as 789 floats (see the native export). 0 before the
	/// scene is up or when the native library predates the call.
	/// </summary>
	public static uint GetClassicTheme(float[] values)
	{
		if (_classicThemeMissing || TryGetAbiVersion() is null || !IsGuestUp)
		{
			return 0;
		}
		try
		{
			return RefiiHostGetClassicTheme(values, (uint)values.Length);
		}
		catch (EntryPointNotFoundException)
		{
			_classicThemeMissing = true;
			return 0;
		}
	}

	[DllImport(Library)]
	private static extern uint RefiiHostGetClassicTheme([Out] float[] outValues, uint capacity);

	private static bool _classicThemeTexturesMissing;

	/// <summary>
	/// The theme's texture slots, raw: per slot 17 words, the element count then up to eight
	/// (weight bits, hash) pairs in the vector's order (RefiiHostGetClassicThemeTextures). Words
	/// written, or 0 when the scene is not up.
	/// </summary>
	public static uint GetClassicThemeTextures(uint[] words)
	{
		if (_classicThemeTexturesMissing || TryGetAbiVersion() is null || !IsGuestUp)
		{
			return 0;
		}
		try
		{
			return RefiiHostGetClassicThemeTextures(words, (uint)words.Length);
		}
		catch (EntryPointNotFoundException)
		{
			_classicThemeTexturesMissing = true;
			return 0;
		}
	}

	[DllImport(Library)]
	private static extern uint RefiiHostGetClassicThemeTextures([Out] uint[] outWords, uint capacity);

	private static bool _lightHalosMissing;

	/// <summary>
	/// The light halos the guest draws this frame (RefiiHostGetLightHalos), 12 floats each:
	/// centre (guest axes), half-extent, colour, water factor and aspect, texture hash bits, light.
	/// Halos written, or 0.
	/// </summary>
	public static uint GetLightHalos(float[] values)
	{
		if (_lightHalosMissing || TryGetAbiVersion() is null || !IsGuestUp)
		{
			return 0;
		}
		try
		{
			return RefiiHostGetLightHalos(values, (uint)values.Length);
		}
		catch (EntryPointNotFoundException)
		{
			_lightHalosMissing = true;
			return 0;
		}
	}

	[DllImport(Library)]
	private static extern uint RefiiHostGetLightHalos([Out] float[] outValues, uint capacity);

	[DllImport(Library)]
	private static extern uint RefiiHostPostFrontEndAction([MarshalAs(UnmanagedType.LPUTF8Str)] string name);

	[DllImport(Library)]
	private static extern uint RefiiHostGetLanguages(
		out uint current, byte[] names, uint capacity);

	[DllImport(Library)]
	private static extern uint RefiiHostGetCurrentCulture(byte[] code, uint capacity);

	[DllImport(Library)]
	private static extern uint RefiiHostGetLoadingScreenArt(
		out uint type, out uint body, byte[] name, uint capacity);

	[DllImport(Library)]
	private static extern uint RefiiHostWorldIsReady();

	[DllImport(Library)]
	private static extern uint RefiiHostGetSaveState(
		out uint signedIn, out int count, out uint limit);

	[DllImport(Library)]
	private static extern void RefiiHostRefreshSaveState();

	[DllImport(Library)]
	private static extern void RefiiHostGuestTickPump(
		out ulong ticks, out ulong drained, out uint queued);

	[DllImport(Library)]
	private static extern void RefiiHostSetWorldBuildPending(uint pending);

	[DllImport(Library)]
	private static extern uint RefiiHostDescribeGameFlowQueue(byte[] outText, uint capacity);

	[DllImport(Library)]
	private static extern uint RefiiHostWorldIsActive();

	[DllImport(Library)]
	private static extern uint RefiiHostGetLevelChange(
		byte[] world, byte[] level, byte[] entrance, uint capacity,
		out uint outState, out uint outTravelType, out uint outFullLoad);

	[DllImport(Library)]
	private static extern uint RefiiHostGetCurrentLevel(
		byte[] world, byte[] level, byte[] entrance, uint capacity, out uint outKey);

	[DllImport(Library)]
	private static extern int RefiiHostGetStartLevel(
		byte[] world, byte[] level, byte[] entrance, uint capacity);

	[DllImport(Library)]
	private static extern int RefiiHostGetStartScenario(byte[] scenario, uint capacity);

	[DllImport(Library)]
	private static extern int RefiiHostGetActiveScenario(byte[] scenario, uint capacity);

	[DllImport(Library)]
	private static extern uint RefiiHostGetActiveLayers(uint[] outHashes, uint capacity);

	[DllImport(Library)]
	private static extern uint RefiiHostGetDeactivatedLayers(uint[] outHashes, uint capacity);

	[DllImport(Library)]
	private static extern int RefiiHostGetLoadingScreen(
		out uint manager, out uint screen, out float fade, out uint requested, out uint done);

	[DllImport(Library)]
	private static extern uint RefiiHostGetPresentations(
		uint[]? outWords, uint wordCapacity, byte[]? outNames, uint nameCapacity);

	[DllImport(Library)]
	private static extern uint RefiiHostPresentationsGeneration();

	[DllImport(Library)]
	private static extern uint RefiiHostStatedScreen();

	[DllImport(Library)]
	private static extern uint RefiiHostStatedValue(uint key);

	[DllImport(Library)]
	private static extern uint RefiiHostGetPartnerCameraAlphas(
		uint[] outEntities, float[] outAlphas, uint capacity, out uint outSerial);

	[DllImport(Library)]
	private static extern uint RefiiHostGetScreenFade(
		float[]? outRgba, out float outProgress, out uint outLocked);

	[DllImport(Library)]
	private static extern void RefiiHostSetHighlightedRow(
		uint screen, int eventType, int id,
		[MarshalAs(UnmanagedType.LPUTF8Str)] string name, [MarshalAs(UnmanagedType.LPUTF8Str)] string label);

	[DllImport(Library)]
	private static extern void RefiiHostSetListFoldDepth(uint screen, uint depth);

	/// <summary>The front end's main menu as a <see cref="SetListFoldDepth"/> key; in-game screens
	/// use their TU1 id, all below 64.</summary>
	public const uint FrontEndMainMenuList = 1010;

	/// <summary>States how deep into open folders the highlighted row of Godot's list for
	/// <paramref name="screen"/> sits (0 at the root, which also clears). The guest's
	/// `IsHighlightedItemAtRoot` readers — `sub_826C1D98` in game, `sub_826DED60` on the front end —
	/// answer from it (gui_menu_hooks.cpp).</summary>
	public static void SetListFoldDepth(uint screen, uint depth)
	{
		if (TryGetAbiVersion() is null)
		{
			return;
		}

		try
		{
			RefiiHostSetListFoldDepth(screen, depth);
		}
		catch (System.EntryPointNotFoundException)
		{
		}
	}

	/// <summary>States the highlighted row of an in-game screen's Godot list — what the guest's
	/// highlighted-row reader answers with (gui_menu_hooks.cpp). Screen 0 clears it.</summary>
	public static void SetHighlightedRow(uint screen, int eventType, int id, string name, string label)
	{
		if (TryGetAbiVersion() is null)
		{
			return;
		}

		try
		{
			RefiiHostSetHighlightedRow(screen, eventType, id, name, label);
		}
		catch (System.EntryPointNotFoundException)
		{
		}
	}




	[DllImport(Library)]
	private static extern uint RefiiHostUiDataGeneration();

	[DllImport(Library)]
	private static extern int RefiiHostSetPlayerText(int player, [MarshalAs(UnmanagedType.LPUTF8Str)] string key,
		[MarshalAs(UnmanagedType.LPUTF8Str)] string text);

	[DllImport(Library)]
	private static extern int RefiiHostSetPlayerValue(
		int player, [MarshalAs(UnmanagedType.LPUTF8Str)] string key, float value);

	[DllImport(Library)]
	private static extern void RefiiHostSetAssetReader(IntPtr reader);

	[DllImport(Library)]
	private static extern void RefiiHostSetModTextLookup(IntPtr lookup);

	[DllImport(Library)]
	private static extern void RefiiHostSetAssetOverrideQuery(IntPtr query);

	[DllImport(Library)]
	private static extern void RefiiHostSetAssetNameResolver(IntPtr resolver);

	[DllImport(Library)]
	private static extern int RefiiHostGetOptionsSliders(
		out float outGamma, out float outSfx, out float outMusic, out float outSpeech,
		out uint outSpeaker);

	[DllImport(Library)]
	private static extern int RefiiHostGetModValue(byte[] key, uint keyLength, out float value);

	[DllImport(Library)]
	private static extern void RefiiHostSetModSetting(byte[] key, uint keyLength, float value);

	[DllImport(Library)]
	private static extern uint RefiiHostDrainModEvents(byte[] outBuffer, uint capacity);

	[DllImport(Library)]
	private static extern void RefiiHostSetModEventMask(ulong mask);

	[DllImport(Library)]
	private static extern void RefiiHostSetModEventMaskHigh(ulong mask);

	[DllImport(Library)]
	private static extern void RefiiHostPostModInput(byte[] action, uint actionLength, int player, int pressed);

	[DllImport(Library)]
	private static extern void RefiiHostPostModCommand(byte[] line, uint lineLength);

	[DllImport(Library)]
	private static extern uint RefiiHostDrainModErrors(byte[] outBuffer, uint capacity);

	[DllImport(Library)]
	private static extern uint RefiiHostPlayerStateOutgoing(byte[] outBuffer, uint capacity);

	[DllImport(Library)]
	private static extern uint RefiiHostGetCollectedKeys(uint[] outPairs, uint capacity);

	[DllImport(Library)]
	private static extern uint RefiiHostTakeToldStatements(uint[] outWords, uint capacity);

	[DllImport(Library)]
	private static extern uint RefiiHostTakeToldTexts(uint[] outWords, uint wordCapacity, ushort[] units, uint unitCapacity);

	[DllImport(Library)]
	private static extern uint RefiiHostToldHeadlessFile(uint presentation, uint serial, [Out] byte[] output, uint capacity);

	[DllImport(Library)]
	private static extern int RefiiHostToldHeadlessElement(uint presentation, uint serial, uint id, out uint parentId, out uint nameHash);

	[DllImport(Library)]
	private static extern int RefiiHostPlayerStateReceive(byte[] message, uint length);

	[DllImport(Library)]
	private static extern uint RefiiHostPlayerStateDescribe(byte[] outBuffer, uint capacity);
	[DllImport(Library)]
	private static extern uint RefiiHostPlayerOwned();

	[DllImport(Library)]
	private static extern uint RefiiHostModHandleAddress(uint handle);

	[DllImport(Library)]
	private static extern void RefiiHostSetModVerdictHandler(ModVerdictFn? handler);

	[DllImport(Library)]
	private static extern void RefiiHostSetModVerdictMask(ulong mask);

	[DllImport(Library)]
	private static extern int RefiiHostModVerdictRead(byte[] line, UIntPtr lineLength, [Out] byte[] output, UIntPtr capacity);

	[DllImport(Library)]
	private static extern void RefiiHostSetModVerdictMaskHigh(ulong mask);

	[DllImport(Library)]
	private static extern uint RefiiHostGetHeroVitals(
		uint[]? outWords, uint wordCapacity, float[]? outValues, uint valueCapacity);

	[DllImport(Library)]
	private static extern int RefiiHostGuestBuild();

	[DllImport(Library)]
	private static extern uint RefiiHostGuestAddress(uint tu1Address);

	[DllImport(Library)]
	private static extern uint RefiiHostGuestAddressToTu1(uint liveAddress);

	[DllImport(Library)]
	private static extern uint RefiiHostGetGameLoopPacer(
		uint[]? outWords, uint wordCapacity, double[]? outValues, uint valueCapacity);

	[DllImport(Library)]
	private static extern uint RefiiHostGetRequestedVideoName(
		byte[] outBuffer, uint capacity, out uint outGeneration);

	[DllImport(Library)]
	private static extern int RefiiHostGetVideoPlanes(
		byte[]? outY, uint yCapacity, byte[]? outCr, uint crCapacity,
		byte[]? outCb, uint cbCapacity, uint[] outDesc);

	[DllImport(Library)]
	private static extern int RefiiHostGetVideoFrame(
		byte[]? outRgba, uint capacity, out uint outWidth, out uint outHeight);

	[DllImport(Library)]
	private static extern void RefiiHostSetControllerState(
		uint userIndex, int connected, uint buttons, uint leftTrigger, uint rightTrigger,
		int thumbLx, int thumbLy, int thumbRx, int thumbRy);

	[DllImport(Library)]
	private static extern int RefiiHostGetVibration(
		uint userIndex, out uint outLeft, out uint outRight);

	internal static string Ascii(byte[] buffer)
	{
		int length = System.Array.IndexOf(buffer, (byte)0);
		return length <= 0
			? string.Empty
			: System.Text.Encoding.ASCII.GetString(buffer, 0, length);
	}

	[DllImport(Library)]

	private static extern int RefiiHostBridgeAbiVersion();

	[DllImport(Library)]
	private static extern int RefiiHostProbeGuestMemory(byte[] message, nuint capacity);

	[DllImport(Library)]
	private static extern int RefiiHostStart(
		[MarshalAs(UnmanagedType.LPUTF8Str)] string gameDirectory,
		[MarshalAs(UnmanagedType.LPUTF8Str)] string arguments,
		byte[] message, nuint capacity);

	[DllImport(Library)]
	private static extern int RefiiHostIsRunning();

	[DllImport(Library)]
	private static extern int RefiiHostExitCode();

	[DllImport(Library)]
	private static extern int RefiiHostStatus(byte[] message, nuint capacity);

	[DllImport(Library)]
	private static extern int RefiiHostJoin(byte[] message, nuint capacity);

	[DllImport(Library)]
	private static extern void RefiiHostSetAppRoot(
		[MarshalAs(UnmanagedType.LPUTF8Str)] string root);

	[DllImport(Library)]
	private static extern void RefiiHostSetConfigPath(
		[MarshalAs(UnmanagedType.LPUTF8Str)] string path);

	[DllImport(Library)]
	private static extern void RefiiHostSetLogDirectory(
		[MarshalAs(UnmanagedType.LPUTF8Str)] string path);

	[DllImport(Library)]
	private static extern void RefiiHostNoteEngineError(
		[MarshalAs(UnmanagedType.LPUTF8Str)] string text);

	[DllImport(Library)]
	private static extern void RefiiHostNoteEngineState(
		[MarshalAs(UnmanagedType.LPUTF8Str)] string text);

	[DllImport(Library)]
	private static extern int RefiiHostInitialize(
		[MarshalAs(UnmanagedType.LPUTF8Str)] string gameDirectory,
		[MarshalAs(UnmanagedType.LPUTF8Str)] string arguments,
		byte[] message, nuint capacity);

	[DllImport(Library)]
	private static extern int RefiiHostStep();

	[DllImport(Library)]
	private static extern int RefiiHostHostedStatus(byte[] message, nuint capacity);

	[DllImport(Library)]
	private static extern uint RefiiHostGetAchievementUnlocks(
		ulong afterSequence, [Out] uint[] ids, [Out] uint[] users, [Out] ulong[] sequences,
		[Out] ulong[] bindings, uint maxUnlocks);

	[DllImport(Library)]
	private static extern uint RefiiHostGetAchievementBindings(ulong afterSerial, [Out] byte[]? text, uint capacity);

	[DllImport(Library)]
	private static extern void RefiiHostSetSaveAchievementUnlocks([MarshalAs(UnmanagedType.LPUTF8Str)] string table);

	[DllImport(Library)]
	private static extern uint RefiiHostGetTitleXdbf([Out] byte[]? bytes, uint capacity);


	[DllImport(Library)]
	private static extern uint RefiiHostGetAchievementsUiRequests();

	[DllImport(Library)]
	private static extern int RefiiHostShutdown(byte[] message, nuint capacity);

	// The members below were recovered by decompiling `refii-launcher.dll`. The bodies are the
	// original logic; decompilation carried no comments, so an offset here is undocumented
	// rather than self-evident.

		[System.ThreadStatic]
		private static uint[]? _hudMeterHashes;

		[System.ThreadStatic]
		private static float[]? _hudMeterValues;

		public static Dictionary<uint, float> GetHudMeters()
		{
			var dictionary = new Dictionary<uint, float>();
			FillHudMeters(dictionary);
			return dictionary;
		}

		/// <summary><see cref="GetHudMeters"/> into a dictionary the caller reuses (cleared first).</summary>
		public static void FillHudMeters(Dictionary<uint, float> dictionary)
		{
			dictionary.Clear();
			if (!TryGetAbiVersion().HasValue || !IsGuestUp)
			{
				return;
			}
			uint[] array = _hudMeterHashes ??= new uint[256];
			float[] array2 = _hudMeterValues ??= new float[256];
			const uint cap = 128u;
			uint num;
			try
			{
				num = RefiiHostGetHudMeters(array, array2, cap);
			}
			catch (EntryPointNotFoundException)
			{
				return;
			}
			if (num > cap)
			{
				GD.PushWarning($"[host] the guest holds {num} HUD meters, more than the {cap} asked for — the tail was not read");
				num = cap;
			}
			for (uint num2 = 0u; num2 < num; num2++)
			{
				dictionary[array[num2]] = array2[num2];
			}
		}

		private static uint[]? _hudPulserModes;

		/// <summary>
		/// The HUD's pulsers — registry 4 — keyed by `FNV1(PulserName)`: each one's loop PHASE 0..1
		/// (`rec+20`) and whether it RUNS (`rec+12`: 0 looping, 1 finishing its loop, 2 stopped; the
		/// stop zeroes the phase and applies tick 0). The guest advances a running one; the host
		/// runs it on its own clock between statements and holds a stopped one still.
		/// </summary>
		public static Dictionary<uint, Refii.Ui.Engine_UiSource.HudPulser> GetHudPulsers()
		{
			var dictionary = new Dictionary<uint, Refii.Ui.Engine_UiSource.HudPulser>();
			FillHudPulsers(dictionary);
			return dictionary;
		}

		/// <summary><see cref="GetHudPulsers"/> into a dictionary the caller reuses (cleared first).</summary>
		public static void FillHudPulsers(Dictionary<uint, Refii.Ui.Engine_UiSource.HudPulser> dictionary)
		{
			dictionary.Clear();
			if (!TryGetAbiVersion().HasValue || !IsGuestUp)
			{
				return;
			}
			uint[] array = _hudMeterHashes ??= new uint[256];
			float[] array2 = _hudMeterValues ??= new float[256];
			uint[] modes = _hudPulserModes ??= new uint[256];
			const uint cap = 64u;
			uint num;
			bool stated = true;
			try
			{
				num = RefiiHostGetHudPulserStates(array, array2, modes, cap);
			}
			catch (EntryPointNotFoundException)
			{
				// A bridge before 223 states the phase alone; a pulser it lists is taken as running.
				stated = false;
				try
				{
					num = RefiiHostGetHudPulsers(array, array2, cap);
				}
				catch (EntryPointNotFoundException)
				{
					return;
				}
			}
			if (num > cap)
			{
				num = cap;
			}
			for (uint i = 0u; i < num; i++)
			{
				dictionary[array[i]] = new Refii.Ui.Engine_UiSource.HudPulser(array2[i], !stated || modes[i] != 2u);
			}
		}

		public static Dictionary<uint, string> GetHudTexts()
		{
			var dictionary = new Dictionary<uint, string>();
			FillHudTexts(dictionary);
			return dictionary;
		}

		/// <summary>The last string decoded for each HUD text, with the bytes it came from.</summary>
		[System.ThreadStatic]
		private static Dictionary<uint, (byte[] Bytes, string Text)>? _hudTextDecoded;

		/// <summary>
		/// <see cref="GetHudTexts"/> into a dictionary the caller reuses (cleared first). A text
		/// whose bytes are unchanged since the last read is the same string, not a new decode.
		/// </summary>
		public static void FillHudTexts(Dictionary<uint, string> dictionary)
		{
			dictionary.Clear();
			if (!TryGetAbiVersion().HasValue || !IsGuestUp)
			{
				return;
			}
			uint[] array = _hudTextHashes ??= new uint[64];
			byte[] array2 = _hudTextBytes ??= new byte[8192];
			System.Array.Clear(array);
			System.Array.Clear(array2);
			uint num;
			try
			{
				num = RefiiHostGetHudTexts(array, (uint)array.Length, array2, (uint)array2.Length);
			}
			catch (EntryPointNotFoundException)
			{
				return;
			}
			if (num > array.Length)
			{
				GD.PushWarning($"[host] the guest holds {num} HUD texts, more than the {array.Length} asked for — the tail was not read");
				num = (uint)array.Length;
			}
			int num2 = 0;
			for (uint num3 = 0u; num3 < num; num3++)
			{
				if (num2 >= array2.Length)
				{
					break;
				}
				int num4 = Array.IndexOf(array2, (byte)0, num2);
				if (num4 < 0)
				{
					break;
				}
				dictionary[array[num3]] = DecodeHudText(array[num3], array2.AsSpan(num2, num4 - num2));
				num2 = num4 + 1;
			}
		}

		private static string DecodeHudText(uint hash, ReadOnlySpan<byte> bytes)
		{
			Dictionary<uint, (byte[] Bytes, string Text)> decoded = _hudTextDecoded ??= new();
			if (decoded.TryGetValue(hash, out (byte[] Bytes, string Text) last) && bytes.SequenceEqual(last.Bytes))
			{
				return last.Text;
			}

			string text = Encoding.UTF8.GetString(bytes);
			decoded[hash] = (bytes.ToArray(), text);
			return text;
		}

		[DllImport("refii_hostbridge")]
		private static extern uint RefiiHostGetEntityLocomotion(uint entity, uint[] outWords, uint wordCount, byte[] outName, uint nameCapacity);

	[DllImport(Library)]
	private static extern uint RefiiHostGetEntityAnimState(uint entity, [Out] uint[] outWords, uint wordCapacity, out uint outFlags);

	[DllImport(Library)]
	private static extern uint RefiiHostGetBoneMorphHeight(uint entity);

	[DllImport(Library)]
	private static extern uint RefiiHostGetEntityTextureLayers(
		uint entity, [Out] uint[] outWords, uint wordCapacity, out uint generation, out ulong statements);

	[DllImport(Library)]
	private static extern uint RefiiHostGetEntityPartMorphs(
		uint entity, [Out] uint[] outWords, uint wordCapacity, out uint generation, out ulong statements);

	[DllImport(Library)]
	private static extern uint RefiiHostGetBoneMorph(uint entity, [Out] uint[] outWords, uint wordCapacity);

	[DllImport(Library)]
	private static extern uint RefiiHostGetAnimPostOps(uint entity, [Out] uint[] outWords, uint wordCapacity);

	[DllImport(Library)]
	private static extern uint RefiiHostGetEntityPartHides(uint entity, [Out] byte[] outText, uint textCapacity);

	[DllImport(Library)]
	private static extern int RefiiHostGetEntityDrawRange(uint entity, out float distance, out float fadeFactor);

	[DllImport(Library)]
	private static extern int RefiiHostGetEntityClothDistance(uint entity, out float distance);

	[DllImport(Library)]
	private static extern uint RefiiHostGetEntityLightZone(uint entity);

	[DllImport(Library)]
	private static extern uint RefiiHostGetEntityOwnLightZone(uint entity);

	[DllImport(Library)]
	private static extern uint RefiiHostGetEntityPoseGate(uint entity);

	[DllImport(Library)]
	private static extern uint RefiiHostGetEntityClothPieces(uint entity, [Out] uint[] outWords, uint maxPieces);

	[DllImport(Library)]
	private static extern uint RefiiHostGetClothPieceVertices(
		uint key, uint index, [Out] float[] outFloats, uint maxVertices, out double outFrame);

	[DllImport(Library)]
	private static extern uint RefiiHostGetEntityViewSphere(uint entity, [Out] float[] sphere);

	[DllImport(Library)]
	private static extern uint RefiiHostGetEntityCasterFlags(uint entity);

	[DllImport(Library)]
	private static extern uint RefiiHostGetEntityInteriorMeshes(uint entity, [Out] uint[] outHashes, uint maxHashes);

	[DllImport(Library)]
	private static extern uint RefiiHostGetDisplacementQuads([Out] float[] outFloats, uint capacity);

	[DllImport(Library)]
	private static extern uint RefiiHostGetFxMeshes([Out] float[] outFloats, uint capacity);

	[DllImport(Library)]
	private static extern uint RefiiHostGetSpotFrustums([Out] float[] outFloats, uint capacity);

	[DllImport(Library)]
	private static extern uint RefiiHostGetClassicLocalLights([Out] float[] outFloats, uint capacity);

	[DllImport(Library)]
	private static extern uint RefiiHostGetClassicShadowSpheres([Out] float[] outFloats, uint capacity);

	[DllImport(Library)]
	private static extern uint RefiiHostGetEntityDisplacementParts(uint entity, [Out] uint[] outHashes, uint maxHashes);

	[DllImport(Library)]
	private static extern uint RefiiHostGetEntityWornItems(uint entity, [Out] uint[] outWords, uint wordCapacity);

	[DllImport(Library)]
	private static extern uint RefiiHostGetAnimMask(
		uint node, [Out] byte[] outBytes, uint capacity, out uint outSkeletonId);

	[DllImport(Library)]
	private static extern uint RefiiHostGetAttachmentGrips([Out] uint[] outWords, uint maxRecords);

	[DllImport(Library)]
	private static extern uint RefiiHostGetAttachments(
		[Out] uint[] outWords, uint maxRecords, [Out] byte[] outNames);




		[DllImport("refii_hostbridge")]
		private static extern int RefiiHostIsGuestUp();

	/// <summary>
	/// The loading screen's take-down handshake — see RefiiHostGetLoadingScreen. <c>Fade</c>
	/// counts 1.0 down to ~0 at 1/15 per render-thread tick; <c>Requested</c> is set by the game
	/// thread just before it blocks on an INFINITE wait; <c>Done</c> is latched by the render
	/// thread when it signals. Fade pinned at 1.0 with Requested and no Done is the stall.
	/// </summary>
	public readonly record struct LoadingScreen(
		uint Manager, uint Screen, float Fade, bool Requested, bool Done);


	/// <summary>
	/// Which loading screen the guest picked — its type and the texture name it chose. The
	/// loading screen is NOT a Gameface presentation: `sub_8235F108` submits direct textured
	/// quads, so this pair is the whole interface. The name is read through an LHString handle
	/// — two dereferences from `screen+272`; every writer is `sub_822651A0(a1 + 272, ...)`.
	/// `Body` is reported so a wrong indirection shows an address rather than an empty name.
	/// </summary>
	public readonly record struct LoadingArt(uint Type, uint Body, string Name);
}
