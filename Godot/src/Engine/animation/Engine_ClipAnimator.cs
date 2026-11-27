using System;
using System.Collections.Generic;
using Godot;
using Refii.Host;

namespace Refii.Animations;

/// <summary>
/// Plays the guest's STATED clips on a rig, on Godot's clock. The guest keeps a blend tree
/// per rig (component 19) whose leaves are clip key, time and loop, folded by weights; the
/// bridge flattens it to <see cref="HostBridge.GuestAnimLeaf"/>s each frame. Each leaf's clip
/// is built once from the animation data (<see cref="Engine_AnimBuilder"/>, the same clips
/// the game ships) and sampled here per bone at the leaf's time, advanced on the host clock
/// between the guest's ticks; the leaves are blended by their weights and written as local
/// bone poses. No bone is copied from the guest. When a leaf's clip is unknown, the tree is
/// replicated, or the rig carries a procedural layer (dog paw IK), <see cref="Apply"/> answers
/// false and the caller keeps the pose feed for that frame — a correctness fallback, not a mode.
/// </summary>
public sealed class Engine_ClipAnimator : System.Threading.IThreadPoolWorkItem, Engine_ClipAnimator.IPoseBatchItem
{
	/// <summary>The main thread's pose write (clip.publish), summed across all rigs once per entity
	/// frame; the other three phases are not timed yet and report 0.</summary>
	internal static readonly Refii.Core.Engine_FrameWork FrameWork = new("clip.prepare", "clip.sampleblend", "clip.post", "clip.publish");
	private sealed class Built
	{
		/// <summary>Only on the resource path (<see cref="ManagedClipSampling"/> off).</summary>
		public Animation? Animation;
		public int[] RotationTrack = System.Array.Empty<int>();
		public int[] PositionTrack = System.Array.Empty<int>();
		public float Length;
		public bool Failed;

		/// <summary>The clip's keys, lifted out of the `Animation` resource ONCE.</summary>
		/// <remarks>
		/// Reading a key through `RotationTrackInterpolate` costs an interop crossing and a binary
		/// search (`Animation::_find`) per bone per rig per step; held here, a sample is a cursor
		/// step and a lerp in managed code.
		///
		/// Exactly Godot's arithmetic. Every track is built `InterpolationType.Linear`, and
		/// Godot's linear case is
		/// `_interpolate(keys[idx], keys[next], c)`, which for a Quaternion is
		/// `p_a.slerp(p_b, p_c)` (`scene/resources/animation.cpp:2456`). Slerp and lerp below
		/// reproduce that; anything that is NOT Linear must keep using the resource.
		/// </remarks>
		public Keys[] RotationKeys = System.Array.Empty<Keys>();

		public Keys[] PositionKeys = System.Array.Empty<Keys>();

		public bool KeysLifted;

		/// <summary>
		/// True when every bone track mapped to this rig contains at most one keyframe (e.g., static
		/// prop Idle or marker poses), so advancing playhead time never changes the sampled bone pose.
		/// </summary>
		public bool IsStatic;
	}

	/// <summary>
	/// A clip's keys, decoded once and shared by every rig. They depend only on the clip and the
	/// decoder (<see cref="Engine_AnimBuilder.DecodeKeys"/>); the skeleton only picks which bone
	/// each clip track lands on, and almost no two rigs share a bone list (clothing and hair).
	/// </summary>
	private sealed class SharedClip
	{
		public float Length;
		public Keys[] Rotations = System.Array.Empty<Keys>();
		public Keys[] Positions = System.Array.Empty<Keys>();
		public long LastTaken;
		public bool IsStatic;
	}

	private readonly record struct SharedKey(Engine_AnimDecoder Decoder, uint Clip);

	private static readonly Dictionary<SharedKey, SharedClip?> SharedClips = new();

	/// <summary>Above this many shared clips the least recently taken half is dropped.</summary>
	private const int SharedClipCap = 2048;

	/// <summary>The shared clips held and the bytes of their key arrays, for the heap census.</summary>
	/// <remarks>A clip's tracks share one times array, counted once. Clips dropped from the table
	/// but still held by a rig are not counted.</remarks>
	internal static (long Clips, long Bytes) SharedCensus()
	{
		lock (SharedClips)
		{
			long clips = 0, bytes = 0;
			foreach (SharedClip? clip in SharedClips.Values)
			{
				if (clip is null)
				{
					continue;
				}

				++clips;
				var times = new HashSet<float[]>(ReferenceEqualityComparer.Instance);
				for (int i = 0; i < clip.Rotations.Length; i++)
				{
					Keys rotation = clip.Rotations[i];
					Keys position = clip.Positions[i];
					bytes += rotation.Rotations.LongLength * 16 + position.Positions.LongLength * 12;
					if (times.Add(rotation.Times))
					{
						bytes += rotation.Times.LongLength * 4;
					}

					if (times.Add(position.Times))
					{
						bytes += position.Times.LongLength * 4;
					}
				}
			}

			return (clips, bytes);
		}
	}

	/// <summary>One track's keys, plus the cursor that makes a monotonic playhead O(1).</summary>
	private sealed class Keys
	{
		public float[] Times = System.Array.Empty<float>();
		public Quaternion[] Rotations = System.Array.Empty<Quaternion>();
		public Vector3[] Positions = System.Array.Empty<Vector3>();
		public int Cursor;

		/// <summary>The track was decoded with more than one key, before any collapse: what
		/// makes a clip static is its decoded key count, not how it is stored.</summary>
		public bool Animated;

		/// <summary>The key index whose span contains <paramref name="t"/>, and the blend.</summary>
		/// <remarks>
		/// A playhead mostly moves forward by one span, so the cursor is stepped and only falls
		/// back to a search when overtaken (a wrap or a re-seek).
		/// </remarks>
		public int Span(float t, out float c)
		{
			c = 0f;
			int n = Times.Length;
			if (n <= 1)
			{
				return 0;
			}

			if (t <= Times[0])
			{
				return 0;
			}

			if (t >= Times[n - 1])
			{
				return n - 1;
			}

			// The cursor is shared by every rig and every leaf sampling this clip, so a miss
			// is common: step at most one span forward, otherwise binary-search.
			int i = Cursor;
			if (i < 0 || i >= n - 1 || Times[i] > t)
			{
				i = LastAtOrBefore(t);
			}
			else if (i < n - 2 && Times[i + 1] <= t)
			{
				++i;
				if (i < n - 2 && Times[i + 1] <= t)
				{
					i = LastAtOrBefore(t);
				}
			}

			Cursor = i;
			float span = Times[i + 1] - Times[i];
			c = span > 0f ? (t - Times[i]) / span : 0f;
			return i;
		}

		/// <summary>The last key index whose time is at or before <paramref name="t"/>, below n-1.</summary>
		private int LastAtOrBefore(float t)
		{
			int lo = 0, hi = Times.Length - 2;
			while (lo < hi)
			{
				int mid = (lo + hi + 1) >> 1;
				if (Times[mid] <= t)
				{
					lo = mid;
				}
				else
				{
					hi = mid - 1;
				}
			}

			return lo;
		}
	}

	/// <summary>Whether clips are sampled from the managed key cache instead of the resource.</summary>
	/// <remarks>A lever, not a setting: it exists so the two paths can be A/B'd on one run.</remarks>
	public static bool ManagedClipSampling = true;

	/// <summary>Copies a clip's keys out of the `Animation` resource, once.</summary>
	/// <remarks>
	/// Only linear tracks are lifted: the managed sample reproduces Godot's linear case and
	/// nothing else, so any other interpolation keeps using the resource.
	/// </remarks>
	private static void LiftKeys(Built built)
	{
		if (built.KeysLifted)
		{
			return;
		}

		built.KeysLifted = true;
		if (built.Animation is null)
		{
			return;
		}

		built.RotationKeys = new Keys[built.RotationTrack.Length];
		built.PositionKeys = new Keys[built.PositionTrack.Length];
		for (int b = 0; b < built.RotationTrack.Length; ++b)
		{
			int rt = built.RotationTrack[b];
			if (rt < 0 ||
				built.Animation.TrackGetInterpolationType(rt) != Animation.InterpolationType.Linear)
			{
				continue;
			}

			int n = built.Animation.TrackGetKeyCount(rt);
			var keys = new Keys { Times = new float[n], Rotations = new Quaternion[n], Animated = n > 1 };
			for (int i = 0; i < n; ++i)
			{
				keys.Times[i] = (float)built.Animation.TrackGetKeyTime(rt, i);
				keys.Rotations[i] = built.Animation.TrackGetKeyValue(rt, i).AsQuaternion();
			}

			built.RotationKeys[b] = keys;
		}

		for (int b = 0; b < built.PositionTrack.Length; ++b)
		{
			int pt = built.PositionTrack[b];
			if (pt < 0 ||
				built.Animation.TrackGetInterpolationType(pt) != Animation.InterpolationType.Linear)
			{
				continue;
			}

			int n = built.Animation.TrackGetKeyCount(pt);
			var keys = new Keys { Times = new float[n], Positions = new Vector3[n], Animated = n > 1 };
			for (int i = 0; i < n; ++i)
			{
				keys.Times[i] = (float)built.Animation.TrackGetKeyTime(pt, i);
				keys.Positions[i] = built.Animation.TrackGetKeyValue(pt, i).AsVector3();
			}

			built.PositionKeys[b] = keys;
		}
	}

	/// <summary>The clip's rotation for one bone, from the key cache when it has one.</summary>
	private static Quaternion SampleRotation(Built built, int bone, int track, float time)
	{
		Keys? keys = ManagedClipSampling && bone < built.RotationKeys.Length
			? built.RotationKeys[bone]
			: null;
		if (keys is null || keys.Times.Length == 0)
		{
			return built.Animation?.RotationTrackInterpolate(track, time) ?? Quaternion.Identity;
		}

		int i = keys.Span(time, out float c);
		return i >= keys.Times.Length - 1
			? keys.Rotations[i]
			: keys.Rotations[i].Slerp(keys.Rotations[i + 1], c);
	}

	/// <summary>The clip's translation for one bone, from the key cache when it has one.</summary>
	private static Vector3 SamplePosition(Built built, int bone, int track, float time)
	{
		Keys? keys = ManagedClipSampling && bone < built.PositionKeys.Length
			? built.PositionKeys[bone]
			: null;
		if (keys is null || keys.Times.Length == 0)
		{
			return built.Animation?.PositionTrackInterpolate(track, time) ?? Vector3.Zero;
		}

		int i = keys.Span(time, out float c);
		return i >= keys.Times.Length - 1
			? keys.Positions[i]
			: keys.Positions[i].Lerp(keys.Positions[i + 1], c);
	}

	/// <summary>One clip playing on this rig. The guest's leaf states the record's clip time
	/// and weight once per game tick; both are kept as tick-stamped scalars and drawn at the
	/// shared clock's draw point, which is the guest's own `lerp(prev, cur, alpha)` render rule.
	/// A leaf that vanishes leaves its channel ramping out before it is dropped.</summary>
	private sealed class Channel
	{
		public HostBridge.GuestAnimLeaf Leaf;
		public Built Built = null!;
		public float StatedTime;
		public float Time;
		public float Weight;
		public float TargetWeight;

		/// <summary>The clip's playhead, on Godot's clock, steered by the guest's stated pair.</summary>
		public readonly Engine_ClipPlayhead Playhead = new();

		/// <summary>
		/// The record's OWN weight pair: joined continuously across ticks so steady-state
		/// civilian mood overlays (`MoodPose*Loop`, whose guest `record+24u` stays parked one
		/// ramp step below `record+28u`) never sawtooth at 15 Hz.
		/// </summary>
		public float RecordPrevious;

		public float RecordCurrent;

		/// <summary>
		/// The leaf's intra-record blend-tree share (<c>TargetWeight / RecordCurrent</c>), smoothed
		/// continuously on the Host clock so locomotion footstep transitions (<c>WalkFootstep</c> +
		/// <c>WalkFootstepMirror</c> / <c>WalkFootstepTurn*</c>) never lag behind the playhead or step
		/// at 15 Hz.
		/// </summary>
		public float SmoothedShare = 1f;

		public float TargetShare = 1f;
		public float ShareStepRate = 4f;

		/// <summary>
		/// The record's weight pair one tick earlier, and the tick the current pair was read on.
		/// </summary>
		public float OlderPrevious;

		public float OlderCurrent;
		public bool HasOlder;
		public uint WeightPairTick = uint.MaxValue;

		/// <summary>The tick the playhead was last stated on; MaxValue before the first.</summary>
		public uint PlayheadTick = uint.MaxValue;

		public float BlendIn = 0.2f;
		public float BlendOut = 0.2f;
		public float OverlayFade = 1f;
		public ulong LastUsec;
		public bool Seen;

		/// <summary>Position in this tick's export, front to back: the guest's list order.</summary>
		public int Order;

		/// <summary>State last committed in <see cref="Write"/>, for static/clamped prop culling.</summary>
		public float WrittenTime = float.NaN;
		public float WrittenWeight = float.NaN;
		public int WrittenOrder = -1;
		public bool HasWritten;
	}

	private readonly Skeleton3D _skeleton;

	/// <summary>The shadow skeleton missed a bone write since it was last mirrored, so the next mirror writes every bone.</summary>
	private bool _shadowStale = true;

	/// <summary>Number of channels present when <see cref="Write"/> last committed a pose.</summary>
	private int _writtenChannelCount = -1;

	/// <summary>
	/// True when every active channel's sampled pose and weight are identical to the pose already
	/// written to the skeleton (e.g., stationary doors, chests, signs, or 1-keyframe prop idles).
	/// When true, <see cref="GameEntities"/> omits this rig from per-frame <c>_animating</c>
	/// resampling until the guest changes its animation state on a subsequent walk.
	/// </summary>
	public bool IsSettled { get; private set; }
	private readonly string _skeletonName;
	private readonly Dictionary<uint, Built> _clips = new();
	private Dictionary<string, List<int>>? _boneCandidates;
	/// <summary>
	/// One channel per stated leaf, not per clip. The guest's type-2 weighted node samples a
	/// single clip at several times and blends them by each child's weight — a viseme is four
	/// samples of the clip "Phonemes" (`sub_822B0618`), and a record crossfading a clip into
	/// itself states the same shape. The bridge lists leaves in a stable tree order, so the
	/// occurrence index names each sample.
	/// </summary>
	private readonly record struct ChannelKey(uint Uid, uint Clip, int At);

	private readonly Dictionary<ChannelKey, Channel> _channels = new();

	/// <summary>How many times each (record, clip) has been seen so far in the leaf walk.</summary>
	private readonly Dictionary<ulong, int> _keyOccurrences = new();
	private readonly HashSet<uint> _activeRecordUids = new();
	private readonly HashSet<uint> _seenRecordUids = new();
	private readonly List<ChannelKey> _dropped = new();
	public int Driven;
	public string LastFallback = string.Empty;

	private static ulong PlayheadKey(HostBridge.GuestAnimLeaf leaf) => ((ulong)leaf.Uid << 32) | leaf.Key;
	private readonly List<HostBridge.GuestAnimLeaf> _leaves = new();

	/// <summary>The guest's leaves as last read, the guest frame and entity they were read on, and the read's answers.</summary>
	private readonly List<HostBridge.GuestAnimLeaf> _read = new();

	private uint _readFrame;
	private uint _readEntity;
	private int _readCount;
	private uint _readFlags;
	private Quaternion[] _rotation = System.Array.Empty<Quaternion>();
	private Vector3[] _position = System.Array.Empty<Vector3>();
	private float[] _rotationWeight = System.Array.Empty<float>();
	private float[] _positionWeight = System.Array.Empty<float>();
	private Quaternion[] _restRotation = System.Array.Empty<Quaternion>();
	private Vector3[] _restPosition = System.Array.Empty<Vector3>();

	public static int ClipDrivenFrames;
	public static int FallbackUnknownClip;
	public static int SkippedNonFiniteLeaves;
	public static int FallbackProcedural;
	public static int FallbackNoLeaves;

	/// <summary>Frames a rig yielded to the guest's physics-written pose (flag 8).</summary>
	public static int FallbackRagdoll;

	private readonly int[] _modelToGuest;
	private readonly Dictionary<ulong, byte[]> _masks = new();
	private Quaternion[] _layerRotation = System.Array.Empty<Quaternion>();
	private Vector3[] _layerPosition = System.Array.Empty<Vector3>();
	private float[] _layerRotationWeight = System.Array.Empty<float>();
	private float[] _layerPositionWeight = System.Array.Empty<float>();
	private readonly Dictionary<uint, float> _recordWeight = new();

	/// <summary>Each record's position in the guest's list — the runner-up's tie-break.</summary>
	private readonly Dictionary<uint, int> _recordOrder = new();

	/// <summary>
	/// Partner records beyond the heaviest, when the guest's partner is a SET (Fable 3,
	/// `sub_826BC358`: every primary below the top takes a greedy share, normalised). Empty for
	/// Fable 2, whose partner is one record.
	/// </summary>
	private readonly HashSet<uint> _extraRunners = new();
	private readonly List<Channel> _overlays = new();

	/// <summary>The title's rigs: bones by role, the face, the pick's rules. Read once, at construction.</summary>
	private readonly Engine_TitleRig _titleRig;

	/// <summary>The title's hip bone (<see cref="Engine_TitleRig.RigRole.Hip"/>), or -1.</summary>
	private int _hipBone = -1;

	/// <summary>The guest's ready flag for hip compensation (see <see cref="HipCompensation"/>); false compensates nothing.</summary>
	private readonly bool _hipCompensates;

	/// <summary>The hip children an overlay rewrites, at most <see cref="HipChildSlots"/>, in the guest's bone order.</summary>
	private readonly int[] _hipChildren = System.Array.Empty<int>();

	private int _headBone = -1;
	private int _faceRootBone = -1;
	private int _jawBone = -1;
	private int _leftEyeBone = -1;
	private int _rightEyeBone = -1;
	private readonly bool _hasFacialRig;
	private readonly bool[] _isFacialBone;
	private ulong _lastFrameUsec;

	/// <summary>True when this animator drives a character or creature rig rather than a simple prop (door, chest, clock, sign).</summary>
	/// <remarks>
	/// Decided once, at construction: the bone count is the skeleton's topology, which every
	/// per-bone array here is sized from, and asking the engine for it again was a crossing per
	/// leaf and per channel of every apply.
	/// </remarks>
	public bool IsCharacterRig => _isCharacterRig;

	/// <summary>
	/// The facial bones of a rig, as <paramref name="rig"/>'s face names them
	/// (<see cref="Engine_TitleRig.FaceRig"/>): every bone under the face root, else the head, that
	/// carries the face's prefix and hangs off no non-facial branch. All false, every bone -1, when
	/// the title has no face.
	/// </summary>
	public static bool[] ComputeFacialBoneMask(
		Engine_TitleRig rig,
		ReadOnlySpan<string> boneNames,
		ReadOnlySpan<int> boneParents,
		out int headBone,
		out int faceRootBone,
		out int jawBone,
		out int leftEyeBone,
		out int rightEyeBone)
	{
		int bones = boneNames.Length;
		var mask = new bool[bones];
		headBone = -1;
		faceRootBone = -1;
		jawBone = -1;
		leftEyeBone = -1;
		rightEyeBone = -1;
		if (rig.Face is not { } face)
		{
			return mask;
		}

		for (int b = 0; b < bones; ++b)
		{
			string name = boneNames[b];
			if (rig.Is(name, Engine_TitleRig.RigRole.Head))
			{
				headBone = b;
			}
			else if (rig.Is(name, Engine_TitleRig.RigRole.FaceRoot))
			{
				faceRootBone = b;
			}
			else if (rig.Is(name, Engine_TitleRig.RigRole.Jaw))
			{
				jawBone = b;
			}
			else if (rig.Is(name, Engine_TitleRig.RigRole.LeftEye))
			{
				leftEyeBone = b;
			}
			else if (rig.Is(name, Engine_TitleRig.RigRole.RightEye))
			{
				rightEyeBone = b;
			}
		}

		int facialAnchor = faceRootBone >= 0 ? faceRootBone : headBone;
		if (facialAnchor < 0)
		{
			return mask;
		}

		for (int b = 0; b < bones; ++b)
		{
			if (b == headBone)
			{
				continue;
			}

			string boneName = boneNames[b];
			if (!boneName.StartsWith(face.BonePrefix, StringComparison.OrdinalIgnoreCase) &&
				b != faceRootBone)
			{
				continue;
			}

			bool underAnchor = false;
			bool nonFacialBranch = false;
			int p = b;
			while (p >= 0 && p < boneParents.Length)
			{
				if (p == facialAnchor)
				{
					underAnchor = true;
					break;
				}

				string pName = boneNames[p];
				foreach (string branch in face.NonFacialBranches)
				{
					if (pName.Contains(branch, StringComparison.OrdinalIgnoreCase))
					{
						nonFacialBranch = true;
						break;
					}
				}

				if (nonFacialBranch)
				{
					break;
				}

				p = boneParents[p];
			}

			if (underAnchor && !nonFacialBranch)
			{
				mask[b] = true;
			}
		}

		return mask;
	}

	private readonly bool _isCharacterRig;

	/// <summary>A character or creature rig: one with a face, or with 20 bones or more.</summary>
	public static bool IsCharacterRigShape(bool hasFace, int boneCount) => hasFace || boneCount >= 20;

	public Engine_ClipAnimator(Skeleton3D skeleton, string skeletonName, int[] modelToGuest)
	{
		_skeleton = skeleton;
		_skeletonName = skeletonName;
		_modelToGuest = modelToGuest;
		_titleRig = Refii.Core.Engine_TitleGame.Active.Rig;
		int bones = skeleton.GetBoneCount();
		_finalPosition = new Vector3[bones];
		_finalRotation = new Quaternion[bones];
		_finalScale = new Vector3[bones];
		System.Array.Fill(_finalScale, Vector3.One);
		int guestCount = 0;
		foreach (int g in modelToGuest)
		{
			guestCount = Math.Max(guestCount, g + 1);
		}

		_guestToModel = new int[guestCount];
		System.Array.Fill(_guestToModel, -1);
		for (int b = 0; b < modelToGuest.Length; ++b)
		{
			if (modelToGuest[b] >= 0)
			{
				_guestToModel[modelToGuest[b]] = b;
			}
		}

		_rotation = new Quaternion[bones];
		_position = new Vector3[bones];
		_rotationWeight = new float[bones];
		_positionWeight = new float[bones];
		_layerRotation = new Quaternion[bones];
		_layerPosition = new Vector3[bones];
		_layerRotationWeight = new float[bones];
		_layerPositionWeight = new float[bones];
		_restRotation = new Quaternion[bones];
		_restPosition = new Vector3[bones];
		_parents = new int[bones];
		var boneNames = new string[bones];
		for (int b = 0; b < bones; ++b)
		{
			Transform3D rest = skeleton.GetBoneRest(b);
			_restRotation[b] = rest.Basis.GetRotationQuaternion();
			_restPosition[b] = rest.Origin;
			_parents[b] = skeleton.GetBoneParent(b);
			string boneName = skeleton.GetBoneName(b);
			boneNames[b] = boneName;
		}

		_isFacialBone = ComputeFacialBoneMask(
			_titleRig,
			boneNames,
			_parents,
			out _headBone,
			out _faceRootBone,
			out _jawBone,
			out _leftEyeBone,
			out _rightEyeBone);
		_hasFacialRig = _jawBone >= 0 && (_leftEyeBone >= 0 || _rightEyeBone >= 0);
		_isCharacterRig = IsCharacterRigShape(_hasFacialRig, bones);

		_hipBone = _titleRig.Bone(boneNames, Engine_TitleRig.RigRole.Hip);
		_hipCompensates = HipCompensation(
			_hipBone, _titleRig.Bone(boneNames, Engine_TitleRig.RigRole.SpineControl), _parents, modelToGuest,
			out _hipChildren);

		_boneCandidates = Engine_AnimBuilder.BoneCandidates(skeleton);
	}

	/// <summary>The guest's hip-child slots: six words from skeleton+116 (Fable 2) / +128 (Fable 3).</summary>
	public const int HipChildSlots = 6;

	/// <summary>
	/// The hip compensation the guest's skeleton states, resolved once per rig.
	/// </summary>
	/// <remarks>
	/// Fable 2 `sub_8229CD30` / Fable 3 `sub_82272918`, identical: resolve the hip and the spine
	/// control by name; if EITHER is missing the ready flag (+105 / +117) is cleared and the
	/// overlay blend (`sub_8220CB18` 0x8220CC14, `sub_82206348` 0x822067A8) compensates nothing —
	/// not even the hip's own translation. Otherwise the six slots are filled with -1 and the
	/// skeleton's parent table is walked in bone order (0x8229CE10 / 0x822729F8), taking each bone
	/// whose parent is the hip and that is not the spine control, stopping at the sixth
	/// (`cmpwi r7, 6; beq` 0x8229CE3C / 0x82272A24). The blend's loop rewrites the slots up to the
	/// first -1, at most six (0x8220CDC4..0x8220CE24, 0x8220697C..0x82206B1C).
	///
	/// "Bone order" is the GUEST's skeleton: children are ordered by <paramref name="modelToGuest"/>,
	/// and a bone the guest's skeleton lacks is neither a hip, a spine control, nor a slot.
	/// </remarks>
	/// <returns>The ready flag; <paramref name="children"/> is empty when it is false.</returns>
	public static bool HipCompensation(
		int hip, int spineControl, IReadOnlyList<int> parents, IReadOnlyList<int> modelToGuest, out int[] children)
	{
		children = System.Array.Empty<int>();
		static int Guest(IReadOnlyList<int> map, int bone) => bone >= 0 && bone < map.Count ? map[bone] : -1;
		if (Guest(modelToGuest, hip) < 0 || Guest(modelToGuest, spineControl) < 0)
		{
			return false;
		}

		var found = new List<(int Guest, int Bone)>();
		for (int b = 0; b < parents.Count; ++b)
		{
			int g = Guest(modelToGuest, b);
			if (parents[b] == hip && b != spineControl && g >= 0)
			{
				found.Add((g, b));
			}
		}

		found.Sort((x, y) => x.Guest.CompareTo(y.Guest));
		int count = Math.Min(found.Count, HipChildSlots);
		children = new int[count];
		for (int i = 0; i < count; ++i)
		{
			children[i] = found[i].Bone;
		}

		return true;
	}

	/// <summary>Whether this animator drives that skeleton — the identity a re-created rig fails.</summary>
	public bool Drives(Skeleton3D skeleton) => ReferenceEquals(_skeleton, skeleton) && Alive;

	/// <summary>False once the skeleton has been freed; nothing here may touch it then.</summary>
	public bool Alive => GodotObject.IsInstanceValid(_skeleton);

	/// <summary>Whether the guest states clips for this rig and every one of them is playable
	/// here; when true the bones were written and the pose feed must not run this frame.</summary>
	/// <summary>The skeleton's parent table, read once — see Apply.</summary>
	private int[]? _parents;

	/// <summary>
	/// Advances this rig's clips and, when <paramref name="writeBones"/>, poses the skeleton.
	/// </summary>
	/// <remarks>
	/// THE ADVANCE IS NOT OPTIONAL; THE WRITE IS. Channels advance by wall-clock delta and
	/// self-correct, so a rig whose bones are not written still holds the right time and poses
	/// correctly on the first frame it is written again. That is the distinction the frustum gate
	/// this file used to have got wrong: it skipped BOTH, so a villager culled from birth never
	/// advanced and slid down the street in its bind pose.
	///
	/// Everything after the advance is per bone — sampling every clip into accumulation buffers,
	/// folding them, and two engine crossings each to write the pose — and that is what a rig
	/// nobody can see does not need.
	/// </remarks>
	public bool Apply(uint entity, bool writeBones = true)
	{
		bool clips = Begin(entity, writeBones, out bool fold);
		if (fold)
		{
			Fold();
			Write();
		}

		return clips;
	}

	/// <summary>
	/// <see cref="Apply"/>'s first part, on the main thread: reads the guest's records, steps the
	/// channels and answers exactly as Apply does. <paramref name="fold"/> says a pose is due:
	/// then <see cref="Fold"/> (any thread) and <see cref="Write"/> (main thread) finish it, and
	/// everything the fold reads from the guest has been read here.
	/// </summary>
	public bool Begin(uint entity, bool writeBones, out bool fold)
	{
		fold = false;
		IsSettled = false;
		// Already in this pass's batch (its fold may be running): the same pose is on its way.
		if (_inBatch)
		{
			return true;
		}

		_entity = entity;
		if (!Alive)
		{
			LastFallback = "skeleton freed";
			return false;
		}

		// READ ONCE PER GUEST FRAME. Between two guest frames the guest's records read the same —
		// unless its next step lands part-way through this host frame, and then the weight pair
		// read here belongs to a tick the frame's stamp (Engine_TickClock) has not seen. The read
		// taken on the frame the guest frame changed is the one that matches its stamp.
		uint guestFrame = Refii.Core.Engine_GuestFrame.Frame;
		if (guestFrame == 0u || guestFrame != _readFrame || entity != _readEntity)
		{
			_readCount = HostBridge.GetEntityAnimState(entity, _read, out _readFlags);
			_readFrame = guestFrame;
			_readEntity = entity;
		}

		_leaves.Clear();
		_leaves.AddRange(_read);
		uint flags = _readFlags;
		int count = DelayNetworkLeaves(_readCount);
		// Only a procedural layer the clips cannot express (dog paw/look IK, component 97)
		// keeps the pose feed; the byte at +576 reads 1 on ordinary rigs and means nothing here.
		if ((flags & 2) != 0)
		{
			++FallbackProcedural;
			LastFallback = "procedural";
			return false;
		}

		// A RAGDOLL IS THE GUEST'S POSE. Flag 8 is the ragdoll object in mode 3 or 4, the modes
		// `sub_8257DBC0` copies the Havok bodies back over the bones for; the record list is no
		// longer what the body does. The pose feed carries that pose; clips must not fold over it.
		if ((flags & 8) != 0)
		{
			++FallbackRagdoll;
			LastFallback = "ragdoll";
			return false;
		}

		if (count == 0 || !Engine_AnimCatalog.EnsureLoaded())
		{
			if (count == 0 && !IsCharacterRig && (_hasWritten || Rig is { HasAppliedPose: true, IsSettled: true }))
			{
				IsSettled = true;
				LastMovedBones = 0;
				LastFallback = string.Empty;
				_wroteFrame = Refii.Core.Engine_ProcessFrame.Current;
				return true;
			}
			++FallbackNoLeaves;
			LastFallback = count == 0 ? "no leaves" : "catalog not loaded";
			return false;
		}

		// The frame's instant, not this rig's, so every rig samples at the same time.
		ulong now = Refii.Entities.Engine_DrawnFrame.NowUsec;
		float frameDt = _lastFrameUsec == 0UL || now <= _lastFrameUsec
			? 0f
			: Mathf.Min((now - _lastFrameUsec) * 1e-6f, 0.25f);
		_lastFrameUsec = now;
		// One counter for every stamp: the clip playhead rides the same game tick the poses and
		// placements do. `dword_83319650` skips (`821d46e8` branches past its own increment).
		uint tick = Refii.Core.Engine_TickClock.GuestGameTick;
		int bones = _parents?.Length ?? _skeleton.GetBoneCount();

		// THE PARENT TABLE IS TOPOLOGY, NOT STATE. `GetBoneParent` is a crossing into the engine
		// and it was called once per bone in four separate loops of this method, every apply, for
		// every rig — hundreds of calls per character per frame to read a table that cannot change
		// while the skeleton exists. Read once, held here.
		if (_parents is null || _parents.Length != bones)
		{
			_parents = new int[bones];
			for (int b = 0; b < bones; ++b)
			{
				_parents[b] = _skeleton.GetBoneParent(b);
			}
		}

		// EVENTS IN: every stated leaf finds or starts its channel and sets its target.
		_activeRecordUids.Clear();
		_seenRecordUids.Clear();
		foreach (Channel c in _channels.Values)
		{
			if (c.Weight > 1e-4f || c.Seen)
			{
				_activeRecordUids.Add(c.Leaf.Uid);
			}
			c.Seen = false;
		}

		// The world's draw point, read once: every clip on the rig is steered toward it.
		double drawTick = Refii.Entities.Engine_DrawnFrame.DrawTick;
		int order = -1;
		bool hadPendingClip = false;
		_keyOccurrences.Clear();
		foreach (HostBridge.GuestAnimLeaf leaf in _leaves)
		{
			++order;

			// The pump does not check the guest's floats: a leaf read while its record is torn
			// down can carry NaN weight or time, and MoveToward throws on NaN. Such a leaf is
			// not seen this frame, so its channel ramps out.
			if (!float.IsFinite(leaf.Weight) || !float.IsFinite(leaf.Time))
			{
				++SkippedNonFiniteLeaves;
				continue;
			}
			Built? built = Ensure(leaf.Key, out bool pendingClip);
			if (built is null)
			{
				if (pendingClip)
				{
					hadPendingClip = true;
					continue;
				}
				++FallbackUnknownClip;
				LastFallback = $"unknown clip {leaf.Key:X8}";
				return false;
			}

			_seenRecordUids.Add(leaf.Uid);
			ulong baseKey = PlayheadKey(leaf);
			int occ = _keyOccurrences.TryGetValue(baseKey, out int seen) ? seen : 0;
			_keyOccurrences[baseKey] = occ + 1;
			var key = new ChannelKey(leaf.Uid, leaf.Key, occ);
			if (_channels.TryGetValue(key, out Channel? channel) &&
				(channel.Seen || !CanReuseUnseenChannel(
					channel.StatedTime, leaf.PreviousTime, leaf.Time,
					built.Length, leaf.Loop, built.IsStatic)))
			{
				channel = null;
			}

			if (channel is null)
			{
				float bestDiff = float.MaxValue;
				foreach ((ChannelKey ck, Channel cand) in _channels)
				{
					if (ck.Uid == leaf.Uid && ck.Clip == leaf.Key && !cand.Seen &&
						cand.Leaf.Additive == leaf.Additive && cand.Leaf.Overlay == leaf.Overlay &&
						CanReuseUnseenChannel(
							cand.StatedTime, leaf.PreviousTime, leaf.Time,
							built.Length, leaf.Loop, built.IsStatic))
					{
						float diff = Mathf.Abs(cand.StatedTime - leaf.PreviousTime);
						if (diff < bestDiff)
						{
							bestDiff = diff;
							channel = cand;
						}
					}
				}

				if (channel is null)
				{
					int freeAt = occ;
					while (_channels.ContainsKey(new ChannelKey(leaf.Uid, leaf.Key, freeAt)))
					{
						++freeAt;
					}
					key = new ChannelKey(leaf.Uid, leaf.Key, freeAt);
					channel = new Channel
					{
						Leaf = leaf, Built = built, StatedTime = leaf.Time, Time = leaf.Time,
						Weight = 0f, LastUsec = now,
					};
					_channels[key] = channel;
				}
			}
			else
			{
				// Check if another unseen slot for the same (Uid, Key) is a much closer playhead match
				// (happens when the guest inserts a new self-crossfade leaf at index 0 and shifts the
				// older playing leaf to index 1).
				float curDiff = Mathf.Abs(channel.StatedTime - leaf.PreviousTime);
				if (curDiff > 0.1f)
				{
					foreach ((ChannelKey ck, Channel cand) in _channels)
					{
						if (ck.Uid == leaf.Uid && ck.Clip == leaf.Key && !cand.Seen &&
							cand.Leaf.Additive == leaf.Additive && cand.Leaf.Overlay == leaf.Overlay &&
							CanReuseUnseenChannel(
								cand.StatedTime, leaf.PreviousTime, leaf.Time,
								built.Length, leaf.Loop, built.IsStatic))
						{
							float diff = Mathf.Abs(cand.StatedTime - leaf.PreviousTime);
							if (diff + 0.05f < curDiff)
							{
								curDiff = diff;
								channel = cand;
							}
						}
					}
				}
			}

			// A second statement of the same occurrence cannot happen with the occurrence index
			// in the key; if it does, two playheads of one clip under one record carry weights
			// that sum to the record's, so sum them.
			if (channel.Seen)
			{
				channel.TargetWeight += leaf.Weight;

				// Assigned, not summed: both leaves belong to the same record and carry the same
				// pair. Summing it would double the denominator and halve every share.
				channel.RecordCurrent = leaf.RecordCurrent;
				channel.TargetShare = channel.RecordCurrent > 1e-5f
					? channel.TargetWeight / channel.RecordCurrent
					: 1f;
				if (leaf.Weight > channel.Leaf.Weight)
				{
					channel.Leaf = leaf;
				}

				continue;
			}

			channel.Leaf = leaf;
			channel.Seen = true;
			channel.Order = order;
			channel.TargetWeight = leaf.Weight;
			if (leaf.BlendIn > 0f) channel.BlendIn = leaf.BlendIn;
			if (leaf.BlendOut > 0f) channel.BlendOut = Mathf.Max(leaf.BlendOut, 0.15f);
			float leafShare = leaf.RecordCurrent > 1e-5f ? leaf.Weight / leaf.RecordCurrent : 1f;

			// CONTINUOUS WEIGHT & HOST-DRIVEN INTRA-RECORD SHARE INTERPOLATION (DO NOT REVERT TO RAW leaf.RecordPrevious):
			//
			// 1. Civilian NPC MoodPose*Loop Stale `record+24u` Bug in Guest (`default.xex`):
			//    Every civilian NPC carries a 160-bone `MoodPose*Loop` overlay (`MoodPoseScared1Loop`,
			//    `MoodPoseAngry1Loop`, `MoodPoseHappy1Loop`) on `controller+108u` (Track 3), whereas
			//    Guards and the Hero do not. In the Xbox 360 engine, when a mood overlay's ramp object
			//    (`step = 2/15 = 0.133333`) reaches its steady-state target weight (e.g. `record+28u =
			//    0.387514`), the guest stops stepping the ramp and leaves `record+24u` (`leaf.RecordPrevious`)
			//    permanently parked one step lower (`0.254181`) on every single subsequent game tick.
			//    Joining `channel.RecordPrevious = channel.RecordCurrent` on each new tick enforces C0
			//    continuity across tick boundaries and completely eliminates the 15 Hz sawtooth snap.
			//
			// 2. Host-Driven Intra-Record Locomotion Share (`SmoothedShare -> TargetShare`):
			//    When a character walks or steers, multiple clips inside the same primary record
			//    (`WalkFootstep` + `WalkFootstepMirror` / `WalkFootstepTurn*`, blend node types 1/2/6)
			//    trade `leaf.Weight` while the parent record's weight stays `1.0`. Stepping `SmoothedShare`
			//    toward `TargetShare` directly on the Host clock (`dt`) keeps intra-record footstep
			//    crossfades in phase with the Host-advanced playheads without 2-tick `pairAt` lag.
			if (channel.WeightPairTick == uint.MaxValue)
			{
				channel.WeightPairTick = tick;
				channel.HasOlder = false;
				bool recordAlreadyActive = _activeRecordUids.Contains(leaf.Uid);
				(channel.RecordPrevious, channel.SmoothedShare) = InitialChannelPair(
					recordAlreadyActive, leaf.RecordPrevious, leaf.RecordCurrent, leafShare);
				channel.RecordCurrent = leaf.RecordCurrent;
				channel.TargetShare = leafShare;
				channel.ShareStepRate = ComputeShareStepRate(channel.SmoothedShare, leafShare, channel.BlendIn);
			}
			else if (tick != channel.WeightPairTick)
			{
				channel.HasOlder = tick == channel.WeightPairTick + 1u;
				channel.OlderPrevious = channel.RecordPrevious;
				channel.OlderCurrent = channel.RecordCurrent;
				channel.RecordPrevious = channel.RecordCurrent;
				channel.RecordCurrent = leaf.RecordCurrent;
				channel.TargetShare = leafShare;
				channel.ShareStepRate = ComputeShareStepRate(channel.SmoothedShare, leafShare, channel.BlendIn);
				channel.WeightPairTick = tick;
			}
			else
			{
				channel.RecordCurrent = leaf.RecordCurrent;
				channel.TargetShare = leafShare;
			}

			ObservePlayhead(channel, leaf, tick, drawTick, !IsCharacterRig);
		}

		// On the guest's ticks: a seen channel's weight and playhead are the stated per-tick
		// values drawn at the world's draw point; a vanished channel ramps out on the host clock
		// and drops. Every record's weight pair was read on `tick` — previous at tick − 1,
		// current at tick — so one alpha serves the rig. Clamped: past the pair a weight holds,
		// it never over-weights its channel.
		double pairAt = double.IsNaN(drawTick) ? 1.0 : drawTick - (tick - 1.0);
		float alpha = (float)System.Math.Clamp(pairAt, 0.0, 1.0);
		_dropped.Clear();
		float totalWeight = 0f;
		foreach ((ChannelKey key, Channel c) in _channels)
		{
			float dt = Mathf.Min((now - c.LastUsec) * 1e-6f, 0.25f);
			c.LastUsec = now;
			if (c.Seen)
			{
				float drawn = RecordWeightAt(c, pairAt);
				bool hasSibling = HasSiblingInRecord(c.Leaf.Uid, c);
				c.SmoothedShare = !hasSibling && c.TargetShare >= 0.999f
					? 1f
					: Mathf.MoveToward(c.SmoothedShare, c.TargetShare, c.ShareStepRate * dt);
				c.Weight = c.SmoothedShare * drawn;
			}
			else if (_seenRecordUids.Contains(c.Leaf.Uid) && !hadPendingClip)
			{
				// If this channel's parent record Uid is STILL active in `_seenRecordUids`, the guest
				// pruned this leaf inside the record's internal blend tree (e.g. a `Type-1` footstep
				// lerp reached `t = 1.0`). Step this unseen sibling's target share to `0f` on the Host
				// clock so it finishes ramping out smoothly while its surviving sibling ramps to `1.0`.
				if (tick != c.WeightPairTick)
				{
					c.HasOlder = tick == c.WeightPairTick + 1u;
					c.OlderPrevious = c.RecordPrevious;
					c.OlderCurrent = c.RecordCurrent;
					c.RecordPrevious = c.RecordCurrent;
					c.TargetShare = 0f;
					c.ShareStepRate = ComputeShareStepRate(c.SmoothedShare, 0f, c.BlendOut);
					c.WeightPairTick = tick;
				}
				float drawn = RecordWeightAt(c, pairAt);
				bool hasSibling = HasSiblingInRecord(c.Leaf.Uid, c);
				c.SmoothedShare = hasSibling
					? Mathf.MoveToward(c.SmoothedShare, 0f, c.ShareStepRate * dt)
					: 0f;
				c.Weight = c.SmoothedShare * drawn;
				if (ShouldDropUnseenChannelInActiveRecord(
					recordStillSeen: true, hadPendingClip: false,
					c.Weight, c.SmoothedShare))
				{
					_dropped.Add(key);
					continue;
				}
			}
			else
			{
				if (ShouldDropUnseenChannelInActiveRecord(_seenRecordUids.Contains(c.Leaf.Uid), hadPendingClip))
				{
					_dropped.Add(key);
					continue;
				}
				float rate = 1f / Mathf.Max(c.BlendOut, 0.15f);
				c.Weight = Mathf.MoveToward(c.Weight, 0f, rate * dt);
				if (c.Weight <= 1e-4f)
				{
					_dropped.Add(key);
					continue;
				}
			}

			Advance(c, dt, drawTick, !IsCharacterRig);
			if (!c.Leaf.Additive)
			{
				totalWeight += c.Weight;
			}
		}

		foreach (ChannelKey key in _dropped)
		{
			_channels.Remove(key);
		}

		if (totalWeight <= 1e-5f && hadPendingClip && _hasWritten)
		{
			LastFallback = string.Empty;
			return true;
		}

		// The clips are current now; the pose is the expensive half and the caller may not want
		// it. Answering true keeps the caller on this branch — the rig IS clip-driven — so the
		// pose feed does not write over a skeleton the clips own.
		if (!writeBones)
		{
			return true;
		}

		if (totalWeight <= 1e-5f)
		{
			if (!IsCharacterRig && (_hasWritten || Rig is { HasAppliedPose: true, IsSettled: true }))
			{
				IsSettled = true;
				LastMovedBones = 0;
				LastFallback = string.Empty;
				_wroteFrame = Refii.Core.Engine_ProcessFrame.Current;
				return true;
			}
			++FallbackNoLeaves;
			LastFallback = "zero weight";
			return false;
		}

		// THE GUEST'S FOLD, IN TWO PARTS.
		//
		// PRIMARIES (`sub_82211660`, `sub_822013A8`): one record WINS by priority — first in the
		// list on a tie — and is crossfaded against the highest-weighted other primary by the
		// winner's OWN weight, with the guest's short-circuits at 0.0167 and 0.9833. It is not a
		// normalised sum. Every bone the winner's clip lacks is the skeleton rest pose
		// (`sub_822351B0` copies skel+144), which is what a primary is for: the whole body.
		//
		// OVERLAYS (`sub_82232BF8`, `sub_8220CB18`): the second list, sorted by track then
		// priority, each gated by its priority bitmask against the winner's priority, and
		// composited PER BONE: `wb = mask[bone]/255 * weight`, rotation slerp, translation lerp.
		// A zero mask byte leaves the bone as the primaries left it. Folding these as primaries
		// was the T pose: a wave or a speech clip covers the arms or the face, and every other
		// bone went to rest while the placement kept walking.
		// A HARD CUT SNAPS. A primary that appears in the same tick its predecessor vanished
		// has nothing to crossfade from; its channel is born at weight 0 and eases up, and for
		// that first frame no primary carries weight — so no top, no overlays, and every bone
		// written as rest. That was the one-frame T-pose flash on each such cut (`[rig-rest]`
		// logged `w 0.00->1.00 PRI` on every one). The guest's record is at its full weight on
		// that tick, so the host's channel is too.
		bool anyPrimaryWeight = false;
		Channel? loudest = null;
		foreach (Channel c in _channels.Values)
		{
			if (c.Leaf.Additive || c.Leaf.Overlay)
			{
				continue;
			}

			if (c.Weight > 1e-5f)
			{
				anyPrimaryWeight = true;
				break;
			}

			if (c.Seen && c.TargetWeight > 1e-5f &&
				(loudest is null || c.Leaf.Priority > loudest.Leaf.Priority
				 || (c.Leaf.Priority == loudest.Leaf.Priority && c.Leaf.Uid < loudest.Leaf.Uid)))
			{
				loudest = c;
			}
		}

		if (!anyPrimaryWeight && loudest is not null)
		{
			loudest.Weight = loudest.TargetWeight;
			anyPrimaryWeight = true;
		}

		// NO PRIMARY, NO TREE. With nothing on the first list the guest builds no blend tree
		// (`sub_82211660` needs a top record) and its pose buffer keeps whatever it held; an
		// overlay alone — a mood pose ramping while the idle is between records — is not a
		// pose. Writing rest here was the last of the T-pose flashes. Hold the skeleton as it is.
		if (!anyPrimaryWeight)
		{
			++HeldNoPrimaryFrames;
			LastFallback = string.Empty;
			return true;
		}

		// SETTLED PROP & ONE-SHOT CLIP FAST PATH:
		// In crowded regions like Bowerstone Market, ~55 of the ~69 active animators are stationary
		// 4–8 bone props (doors, chests, shop signs, market stalls) holding either a non-looping
		// `Open`/`Close` clip clamped at its final keyframe or a 1-keyframe static `Idle` clip.
		// Once their pose has been written to both the live skeleton and (when present) its shadow
		// skeleton, and no channel's sampled time, weight, or list order has changed, folding on the
		// ThreadPool and calling Write() at display rate produces zero bone changes.
		if (!_hasFacialRig && _hasWritten && !_scaleDirty && !_hasMorph && _postKinds == 0 &&
			_channels.Count == _writtenChannelCount && _channels.Count > 0 &&
			!(Refii.Entities.Engine_ShadowSkeletons.WriteNow(_skeleton, out _) && _shadowStale))
		{
			bool allSettled = true;
			foreach (Channel c in _channels.Values)
			{
				if (!c.HasWritten || c.WrittenOrder != c.Order ||
					Mathf.Abs(c.Weight - c.WrittenWeight) > 1e-5f ||
					(!c.Built.IsStatic && Mathf.Abs(c.Time - c.WrittenTime) > 1e-5f))
				{
					allSettled = false;
					break;
				}
			}

			if (allSettled)
			{
				IsSettled = true;
				LastMovedBones = 0;
				_wroteFrame = Refii.Core.Engine_ProcessFrame.Current;
				return true;
			}
		}

		// What the fold reads from the guest, read now: the reads share static buffers. The mask
		// cache empties itself past its cap, so it is emptied first when this rig's would not fit.
		if (_masks.Count + _channels.Count > 64)
		{
			_masks.Clear();
		}

		foreach (Channel c in _channels.Values)
		{
			if (c.Leaf.Mask != 0u)
			{
				Mask(c.Leaf.Uid, c.Leaf.Mask);
			}
		}

		if (IsCharacterRig)
		{
			RefreshMorph(bones);
			TakePostOps(frameDt);
		}
		else
		{
			_hasMorph = false;
			_postKinds = 0;
		}

		TakeStatedPose(frameDt, bones);
		_foldBones = bones;
		_foldPairAt = pairAt;
		_foldAlpha = alpha;
		_foldOnMain = false;
		foreach (Channel c in _channels.Values)
		{
			// A clip sampled through its Animation resource crosses into the engine.
			_foldOnMain |= c.Built.Animation is not null;
		}

		fold = true;
		return true;
	}

	internal interface IPoseBatchItem
	{
		bool FoldOnMain { get; }
		uint Address { get; }
		bool InBatch { get; set; }
		System.Exception? FoldError { get; set; }
		void Fold();
		void Write();
	}

	uint IPoseBatchItem.Address => _entity;
	bool IPoseBatchItem.InBatch { get => _inBatch; set => _inBatch = value; }
	System.Exception? IPoseBatchItem.FoldError { get => _foldError; set => _foldError = value; }

	/// <summary>
	/// The rigs posed in one pass of the entity loop: each rig's fold is offered to the thread pool as
	/// soon as it is added, while <see cref="Finish"/> actively steals any unstarted rigs on the main
	/// thread so a busy/starved thread pool (e.g. background load workers waiting on GPU upload fences)
	/// can never stall the main thread. Each pose is the one <see cref="Apply"/> would have computed;
	/// only the thread differs. Main thread.
	/// </summary>
	public sealed class PoseBatch
	{
		private sealed class WorkerToken : System.Threading.IThreadPoolWorkItem
		{
			public readonly PoseBatch Batch;
			public int Queued;

			public WorkerToken(PoseBatch batch) => Batch = batch;

			public void Execute()
			{
				try
				{
					Batch.RunWorkerSlice();
				}
				finally
				{
					System.Threading.Volatile.Write(ref Queued, 0);
				}
			}
		}

		private readonly List<IPoseBatchItem> _rigs = new();
		private readonly object _gate = new();
		private readonly WorkerToken[] _workers;
		private int _nextIndex;
		private int _activeWorkers;
		private int _epoch;
		private bool _closed = true;

		/// <summary>Test hook: when false, workers are not dispatched so <see cref="Finish"/> exercises 100% main-thread work-stealing.</summary>
		internal bool DispatchToThreadPool { get; set; } = true;

		/// <summary>Rigs stolen and folded on the main thread during the most recent <see cref="Finish"/>.</summary>
		internal int LastStolenCount { get; private set; }

		public PoseBatch()
		{
			int maxWorkers = System.Math.Clamp(System.Environment.ProcessorCount - 1, 1, 8);
			_workers = new WorkerToken[maxWorkers];
			for (int i = 0; i < maxWorkers; i++)
			{
				_workers[i] = new WorkerToken(this);
			}
		}

		public void Add(Engine_ClipAnimator rig)
		{
			rig._batch = this;
			AddItem(rig);
		}

		internal void AddItem(IPoseBatchItem rig)
		{
			if (_rigs.Count == 0)
			{
				lock (_gate)
				{
					_epoch++;
					_nextIndex = 0;
					_activeWorkers = 0;
					_closed = false;
				}
			}

			rig.InBatch = true;
			rig.FoldError = null;
			lock (_gate)
			{
				_rigs.Add(rig);
			}

			if (rig.FoldOnMain || !DispatchToThreadPool)
			{
				return;
			}

			for (int i = 0; i < _workers.Length; i++)
			{
				WorkerToken token = _workers[i];
				if (System.Threading.Interlocked.CompareExchange(ref token.Queued, 1, 0) == 0)
				{
					System.Threading.ThreadPool.UnsafeQueueUserWorkItem(token, preferLocal: false);
					break;
				}
			}
		}

		private void RunWorkerSlice()
		{
			long allocFrom = System.GC.GetAllocatedBytesForCurrentThread();
			try
			{
				while (true)
				{
					IPoseBatchItem? rig = null;
					int claimedEpoch;
					lock (_gate)
					{
						if (_closed || _nextIndex >= _rigs.Count)
						{
							return;
						}

						int index = _nextIndex++;
						rig = _rigs[index];
						if (rig.FoldOnMain)
						{
							continue;
						}

						claimedEpoch = _epoch;
						_activeWorkers++;
					}

					try
					{
						rig.Fold();
					}
					catch (System.Exception error)
					{
						rig.FoldError = error;
					}
					finally
					{
						lock (_gate)
						{
							if (claimedEpoch == _epoch && --_activeWorkers == 0 && _closed)
							{
								System.Threading.Monitor.PulseAll(_gate);
							}
						}
					}
				}
			}
			finally
			{
				Refii.Core.Engine_GcFrames.Note(ref Refii.Core.Engine_GcFrames.PoseBatchBytes,
					System.GC.GetAllocatedBytesForCurrentThread() - allocFrom);
			}
		}

		public void Finish()
		{
			if (_rigs.Count == 0)
			{
				LastStolenCount = 0;
				return;
			}

			for (int i = 0; i < _rigs.Count; i++)
			{
				IPoseBatchItem mainRig = _rigs[i];
				if (mainRig.FoldOnMain)
				{
					mainRig.Fold();
				}
			}

			// Steal any remaining unclaimed folds directly on the main thread so ThreadPool queue delays
			// never block entity pose updates.
			int stolen = 0;
			while (true)
			{
				IPoseBatchItem? rig = null;
				lock (_gate)
				{
					if (_nextIndex >= _rigs.Count)
					{
						break;
					}

					rig = _rigs[_nextIndex++];
				}

				if (rig.FoldOnMain)
				{
					continue;
				}

				stolen++;
				try
				{
					rig.Fold();
				}
				catch (System.Exception error)
				{
					rig.FoldError = error;
				}
			}

			LastStolenCount = stolen;

			lock (_gate)
			{
				_closed = true;
				if (_activeWorkers > 0)
				{
					long waitFrom = System.Diagnostics.Stopwatch.GetTimestamp();
					while (_activeWorkers > 0)
					{
						System.Threading.Monitor.Wait(_gate);
					}
					Refii.Core.Engine_SpikeNotes.Since(Refii.Core.Engine_SpikeNotes.Kind.PoseWait, waitFrom);
				}
			}

			foreach (IPoseBatchItem rig in _rigs)
			{
				rig.InBatch = false;
				if (rig.FoldError is { } error)
				{
					rig.FoldError = null;
					if (!Refii.Core.Engine_AppRoot.IsTestHost)
					{
						GD.PushError($"[anim] fold failed for 0x{rig.Address:X8}: {error}");
					}
					continue;
				}

				rig.Write();
			}

			lock (_gate)
			{
				_rigs.Clear();
			}
		}
	}

	private PoseBatch? _batch;
	private bool _inBatch;
	private System.Exception? _foldError;

	/// <summary>The thread pool's entry (unused directly now that <see cref="PoseBatch"/> owns worker tokens).</summary>
	void System.Threading.IThreadPoolWorkItem.Execute()
	{
		Fold();
	}

	/// <summary>Whether <see cref="Fold"/> must run on the main thread (a clip read through its resource).</summary>
	public bool FoldOnMain => _foldOnMain;

	private int _foldBones;
	private double _foldPairAt;
	private float _foldAlpha;
	private bool _foldOnMain;
	private float _foldTopShare;
	private float _foldShareStep;
	private uint _foldTopKey;
	private int _foldRestBones;

	/// <summary>
	/// The pose from the channels <see cref="Begin"/> left: the fold, overlays, morph and post-ops
	/// into the final arrays. Touches only this animator, the shared clip keys (read-only; the
	/// span cursor is a hint any value of which gives the same answer) and counters, so rigs
	/// fold in parallel.
	/// </summary>
	public void Fold()
	{
		int bones = _foldBones;
		double pairAt = _foldPairAt;
		Channel? top = null;
		float topShare = 1f;

		if (_channels.Count == 0)
		{
			_foldRestBones = bones;
			return;
		}

		if (_channels.Count == 1)
		{
			var e = _channels.GetEnumerator();
			if (e.MoveNext())
			{
				Channel only = e.Current.Value;
				if (!only.Leaf.Additive && !only.Leaf.Overlay && only.Weight > 1e-5f)
				{
					top = only;
					topShare = 1f;
					LastTopUid = only.Leaf.Uid;
					LastTopKey = only.Leaf.Key;
					LastTopShare = 1f;
					_lastRunnerUid = 0;
					_foldTopShare = 1f;
					_foldTopKey = only.Leaf.Key;
					_foldShareStep = 0f;
					_lastShare = 1f;
					_haveLastShare = true;
					System.Array.Clear(_rotationWeight, 0, bones);
					System.Array.Clear(_positionWeight, 0, bones);
					_overlays.Clear();
					Sample(only.Built, only.Time, 1f, false, _rotation, _rotationWeight, _position, _positionWeight, bones);
					PostOpsApplied = ApplyPostOpsForRecord(only.Leaf.Uid, _rotation, _rotationWeight, bones);
					goto FinalizePose;
				}
			}
		}

		_recordWeight.Clear();
		_recordOrder.Clear();
		uint topUid = 0;
		int topOrder = -1;
		int topPriority = int.MinValue;
		bool haveTop = false;
		bool topSeen = false;
		foreach (Channel c in _channels.Values)
		{
			if (c.Leaf.Additive || c.Leaf.Overlay)
			{
				continue;
			}
			if (c.Weight <= 1e-5f && !c.Seen)
			{
				continue;
			}

			_recordWeight[c.Leaf.Uid] = _recordWeight.GetValueOrDefault(c.Leaf.Uid) + c.Weight;
			if (_recordOrder.TryGetValue(c.Leaf.Uid, out int seen))
			{
				_recordOrder[c.Leaf.Uid] = Mathf.Min(seen, c.Order);
			}
			else
			{
				_recordOrder[c.Leaf.Uid] = c.Order;
			}

			// TIES GO TO THE NEWEST RECORD. `sub_822013A8` walks the list back to front with a
			// strict `>` (0x82201434..7c), and records are appended at the tail, so among equal
			// priorities the record nearest the tail — the one created last — wins. The export
			// walks front to back, so "newest" is the highest list position.
			//
			// Prefer live channels seen this tick over unseen channels ramping out on the host.
			bool isBetter = false;
			if (!haveTop)
			{
				isBetter = true;
			}
			else if (c.Leaf.IsWinner != top!.Leaf.IsWinner)
			{
				isBetter = c.Leaf.IsWinner;
			}
			else if (c.Seen != topSeen)
			{
				isBetter = c.Seen;
			}
			else if (c.Leaf.Priority > topPriority || (c.Leaf.Priority == topPriority && c.Order > topOrder))
			{
				isBetter = true;
			}

			if (isBetter)
			{
				haveTop = true;
				topSeen = c.Seen;
				topUid = c.Leaf.Uid;
				topOrder = c.Order;
				topPriority = c.Leaf.Priority;
				top = c;
			}
		}

		int Order(uint uid) => _recordOrder.GetValueOrDefault(uid, int.MaxValue);

		// A TITLE THAT STATES ITS WHOLE PICK (Engine_TitleRig.StatesPartnerSet) names the top, every
		// member of its partner SET, and each one's share of the fold as its weight pair.
		bool statesPartnerSet = _titleRig.StatesPartnerSet;

		// The runner is picked deterministically: if an explicit guest IsRunner leaf exists, elect it;
		// otherwise select by weight and list order.
		//
		// Ties go to the oldest record — the guest's rule, the opposite of the winner's.
		// `sub_82237F90` scans the list head to tail with a strict `>` (0x8223803C), so among
		// equal weights the record nearest the head survives; the winner scan runs backward.
		uint runnerUid = 0;
		float runnerWeight = 0f;
		_extraRunners.Clear();
		foreach (Channel c in _channels.Values)
		{
			if (c.Leaf.Uid == topUid || c.Leaf.Additive || c.Leaf.Overlay)
			{
				continue;
			}
			if (c.Leaf.IsRunner && c.Weight > 1e-4f)
			{
				if (!statesPartnerSet)
				{
					runnerUid = c.Leaf.Uid;
					runnerWeight = _recordWeight.GetValueOrDefault(runnerUid);
					break;
				}

				// THE PARTNER IS A SET: the heaviest member names it, every member samples into it
				// at its share, and its weight is the set's sum.
				if (runnerUid == 0 || _recordWeight.GetValueOrDefault(c.Leaf.Uid) > _recordWeight.GetValueOrDefault(runnerUid))
				{
					if (runnerUid != 0)
					{
						_extraRunners.Add(runnerUid);
					}
					runnerUid = c.Leaf.Uid;
				}
				else if (c.Leaf.Uid != runnerUid)
				{
					_extraRunners.Add(c.Leaf.Uid);
				}
			}
		}

		if (statesPartnerSet && runnerUid != 0)
		{
			runnerWeight = _recordWeight.GetValueOrDefault(runnerUid);
			foreach (uint uid in _extraRunners)
			{
				runnerWeight += _recordWeight.GetValueOrDefault(uid);
			}
		}

		// A title that states its whole pick names every primary it draws; a record it did not
		// name is not drawn, so nothing is elected in its place.
		if (runnerUid == 0 && !statesPartnerSet)
		{
			foreach ((uint uid, float w) in _recordWeight)
			{
				if (uid == topUid)
				{
					continue;
				}

				if (w > runnerWeight || (w == runnerWeight && runnerUid != 0u && Order(uid) < Order(runnerUid)))
				{
					runnerUid = uid;
					runnerWeight = w;
				}
			}
		}

		if (runnerUid != _lastRunnerUid && _lastRunnerUid != 0u && runnerUid != 0u)
		{
			System.Threading.Interlocked.Increment(ref RunnerFlips);
			++RigRunnerFlips;
		}

		_lastRunnerUid = runnerUid;
		LastTopUid = topUid;

		// Normalized crossfade ratio between runnerUid and topUid:
		// Evaluated continuously as topWeight / (topWeight + runnerWeight), matching `sub_82232BF8`.
		float topWeight = 0f;
		if (haveTop && top is not null)
		{
			topWeight = RecordWeightAt(top, pairAt);
		}

		topShare = 1f;
		if (runnerWeight > 1e-4f)
		{
			float sum = topWeight + runnerWeight;
			topShare = sum > 1e-4f ? Mathf.Clamp(topWeight / sum, 0f, 1f) : 1f;
		}
		else
		{
			topShare = 1f;
		}

		// The guest's short-circuits, the title's (Engine_TitleRig.CrossfadeTopAlone / PartnerAlone).
		if (topShare >= _titleRig.CrossfadeTopAlone) topShare = 1f;
		if (topShare <= _titleRig.CrossfadePartnerAlone) topShare = 0f;

		// How far the crossfade ratio moved in one frame; a step moves every bone by that
		// fraction of the distance between two clips. Reported.
		_foldShareStep = 0f;
		if (_haveLastShare)
		{
			float step = Mathf.Abs(topShare - _lastShare);
			_foldShareStep = step;
			if (step > 0.1f)
			{
				System.Threading.Interlocked.Increment(ref ShareSteps);
				++RigShareSteps;
			}
		}

		_lastShare = topShare;
		_haveLastShare = true;

		System.Array.Clear(_rotationWeight, 0, bones);
		System.Array.Clear(_positionWeight, 0, bones);
		if (runnerUid != 0 && topShare < 1f)
		{
			System.Array.Clear(_layerRotationWeight, 0, bones);
			System.Array.Clear(_layerPositionWeight, 0, bones);
		}
		_overlays.Clear();
		bool partnerSampled = false;
		float topRecordWeight = _recordWeight.GetValueOrDefault(topUid);
		float runnerRecordWeight = runnerWeight;

		// Active priorities for primary and runner to drive continuous overlay masking:
		int runnerPriority = int.MinValue;
		if (runnerUid != 0)
		{
			foreach (Channel c in _channels.Values)
			{
				if (c.Leaf.Uid == runnerUid && !c.Leaf.Overlay && !c.Leaf.Additive)
				{
					runnerPriority = c.Leaf.Priority;
					break;
				}
			}
		}

		// SAME-CLIP RE-QUEUE SMOOTHING:
		// If top and runner have the identical clip key (e.g. companion dog idle re-queueing Pose),
		// synchronize their playheads ONLY when the clip is static or near zero length.
		// For animated cyclical loops (such as bar drinking/idle loops), allowing the crossfade
		// to blend loop-end into loop-start is required to prevent visual snapping.
		if (runnerUid != 0 && haveTop && top is not null && (top.Built.IsStatic || top.Built.Length <= 0.05f))
		{
			foreach (Channel c in _channels.Values)
			{
				if (c.Leaf.Uid == runnerUid && !c.Leaf.Overlay && !c.Leaf.Additive)
				{
					if (c.Leaf.Key == top.Leaf.Key)
					{
						top.Time = c.Time;
					}
					break;
				}
			}
		}

		foreach (Channel c in _channels.Values)
		{
			HostBridge.GuestAnimLeaf leaf = c.Leaf;
			if (leaf.Overlay)
			{
				bool matchesTop = haveTop && (leaf.PriorityMask & (uint)topPriority) != 0;
				bool matchesRunner = runnerUid != 0 && runnerPriority != int.MinValue && (leaf.PriorityMask & (uint)runnerPriority) != 0;
				if (c.Weight > 1e-4f && (matchesTop || matchesRunner))
				{
					// Smooth, continuous crossfade for overlays between runner and winner:
					// Persistent overlays (matching both) stay at full weight; incoming overlays fade in with topShare;
					// outgoing overlays fade out with (1 - topShare).
					if (matchesTop && matchesRunner)
					{
						c.OverlayFade = 1f;
					}
					else if (matchesTop)
					{
						c.OverlayFade = topShare;
					}
					else
					{
						c.OverlayFade = 1f - topShare;
					}

					if (c.OverlayFade > 1e-4f)
					{
						_overlays.Add(c);
					}
				}
				continue;
			}
			if (leaf.Additive)
			{
				// A TYPE-3 CHILD THAT IS A CLIP IS A MASKED OVERLAY, NOT A DELTA. A clip leaf's
				// delta flag is always 0 (every loader passes 0 to `sub_82B77C00`; only a type-5
				// difference node sets it), so `sub_8220CB18` takes the masked path: per bone a
				// slerp toward the child by mask × weight, whole-body when no mask node sits
				// above it. That is the overlay path here; the first-frame delta this once
				// applied was an invention.
				if (c.Weight > 1e-4f && haveTop && (leaf.Uid == topUid || leaf.Uid == runnerUid || _extraRunners.Contains(leaf.Uid)))
				{
					c.OverlayFade = 1f;
					_overlays.Add(c);
				}
				continue;
			}
			Built built = c.Built;
			float time = c.Time;
			if (leaf.Uid == topUid)
			{
				float weight = topRecordWeight > 1e-5f ? c.Weight / topRecordWeight : c.SmoothedShare;
				if (weight > 1e-5f)
				{
					Sample(built, time, weight, false, _rotation, _rotationWeight, _position, _positionWeight, bones);
				}
			}
			else if ((leaf.Uid == runnerUid || _extraRunners.Contains(leaf.Uid)) && topShare < 1f)
			{
				// THE PARTNER, kept apart: the guest's type-1 node SLERPs partner against top by the
				// top's own weight (`sub_82293278`, w > 0.5), which an nlerp of the two into one
				// accumulator only approximates.
				float weight = runnerRecordWeight > 1e-5f ? c.Weight / runnerRecordWeight : c.SmoothedShare;
				if (weight > 1e-5f)
				{
					Sample(built, time, weight, false, _layerRotation, _layerRotationWeight, _layerPosition, _layerPositionWeight, bones);
					partnerSampled = true;
				}
			}
			// a third primary never reaches the pose in the guest either
		}

		// Apply the primary record's aim and look post-ops (`+8` aim, `+12` look) to the inner
		// primary pose BEFORE crossfading with `runnerUid` and BEFORE layering upper-body overlays.
		// In `Engine_Animation_WrapPrimaryRecordWithOverlays` (`0x82229960`), the guest attaches
		// `aimRec` and `lookRec` (`mode = 2: t + w * (q - t)` with `w ~ 2.2`) to the primary walk
		// record's root node and wraps overlays (`MoodPoseScared1Loop`, `MoodPoseAngry1Loop`)
		// around it as outer `Type-3` / `Type-1` nodes; running `lookRec` after overlays multiplied
		// the mood overlay forearm bend by `~2.2x`.
		PostOpsApplied = ApplyPostOpsForRecord(topUid, _rotation, _rotationWeight, bones);

		if (partnerSampled)
		{
			PostOpsApplied += ApplyPostOpsForRecord(runnerUid, _layerRotation, _layerRotationWeight, bones);
			// crossfade(partner = A, top = B, t = topShare): result = slerp(A, B, t).
			for (int b = 0; b < bones; ++b)
			{
				bool hasTop = _rotationWeight[b] > 0f;
				bool hasPartner = _layerRotationWeight[b] > 0f;
				if (!hasTop && !hasPartner)
				{
					continue;
				}

				_rotation[b] = CrossfadePrimaryRotation(
					hasTop, _rotation[b], hasPartner, _layerRotation[b], _restRotation[b], topShare);
				_rotationWeight[b] = 1f;
				if (_parents![b] >= 0)
				{
					_position[b] = CrossfadePrimaryPosition(
						_positionWeight[b], _position[b],
						_layerPositionWeight[b], _layerPosition[b],
						_restPosition[b], topShare);
					_positionWeight[b] = 1f;
				}
			}
		}

		if (_overlays.Count > 1)
		{
			_overlays.Sort((a, b) =>
				a.Leaf.Track != b.Leaf.Track ? a.Leaf.Track.CompareTo(b.Leaf.Track)
				: a.Leaf.Priority != b.Leaf.Priority ? a.Leaf.Priority.CompareTo(b.Leaf.Priority)
				: a.Leaf.Uid.CompareTo(b.Leaf.Uid));
		}

		// One overlay RECORD at a time: its leaves fold into a layer pose by their weights,
		// then the layer blends onto the body per bone at mask[bone]/255 times the record's
		// weight. Bones the layer's clips do not cover are its rest pose, as the guest samples
		// them; the mask is what keeps them off the body.
		for (int i = 0; i < _overlays.Count;)
		{
			uint uid = _overlays[i].Leaf.Uid;
			int j = i;
			float layerWeight = 0f;
			float recordCap = 0f;
			uint maskNode = 0;
			while (j < _overlays.Count && _overlays[j].Leaf.Uid == uid)
			{
				Channel ov = _overlays[j];
				layerWeight += ov.Weight * ov.OverlayFade;
				float cap = RecordWeightAt(ov, pairAt) * ov.OverlayFade;
				if (cap > recordCap)
				{
					recordCap = cap;
				}
				if (maskNode == 0)
				{
					maskNode = ov.Leaf.Mask;
				}

				++j;
			}

			System.Array.Clear(_layerRotationWeight, 0, bones);
			System.Array.Clear(_layerPositionWeight, 0, bones);
			for (int k = i; k < j; ++k)
			{
				Channel c = _overlays[k];
				Sample(c.Built, c.Time, (c.Weight * c.OverlayFade) / Mathf.Max(layerWeight, 1e-5f), false,
					_layerRotation, _layerRotationWeight, _layerPosition, _layerPositionWeight, bones);
			}

			byte[] mask = MaskTaken(uid, maskNode);
			float effectiveLayerWeight = recordCap > 1e-5f ? Mathf.Min(layerWeight, recordCap) : layerWeight;
			float w = Mathf.Clamp(effectiveLayerWeight, 0f, 1f);

			// THE HIP'S CHILDREN STAY PUT. When an overlay's mask covers the hip the guest
			// (Fable 2 `sub_8220CB18`, 0x8220CC20..; Fable 3 `sub_82206348`, 0x822067A8..)
			// remembers the hip's local transform, applies the blend, keeps the OLD translation on
			// the new hip, and rewrites every hip child but the spine control so its world
			// placement is unchanged — the spine follows the overlay's hip rotation, the legs do
			// not swing with it. Gated on the guest's ready flag and limited to its child slots
			// (see HipCompensation).
			int hip = _hipBone;
			bool compensateHip = _hipCompensates && (mask.Length == 0 || (hip < _modelToGuest.Length &&
				_modelToGuest[hip] >= 0 && _modelToGuest[hip] < mask.Length && mask[_modelToGuest[hip]] != 0));
			Transform3D hipOld = compensateHip ? PoseOf(hip) : Transform3D.Identity;

			for (int b = 0; b < bones; ++b)
			{
				float wb = w;
				if (mask.Length != 0)
				{
					int g = b < _modelToGuest.Length ? _modelToGuest[b] : -1;
					wb = g >= 0 && g < mask.Length ? w * (mask[g] / 255f) : 0f;
				}

				if (wb <= 1e-4f)
				{
					continue;
				}

				Quaternion current = _rotationWeight[b] > 0f ? _rotation[b].Normalized() : _restRotation[b];
				Quaternion layer = _layerRotationWeight[b] > 0f ? _layerRotation[b].Normalized() : _restRotation[b];
				_rotation[b] = current.Slerp(layer, wb);
				_rotationWeight[b] = 1f;

				if (_parents![b] >= 0)
				{
					Vector3 currentPosition = _positionWeight[b] > 1e-5f ? _position[b] / _positionWeight[b] : _restPosition[b];
					Vector3 layerPosition = _layerPositionWeight[b] > 1e-5f ? _layerPosition[b] / _layerPositionWeight[b] : _restPosition[b];
					_position[b] = currentPosition.Lerp(layerPosition, wb);
					_positionWeight[b] = 1f;
				}
			}

			if (compensateHip)
			{
				Transform3D hipNew = PoseOf(hip);
				hipNew.Origin = hipOld.Origin;
				// child_local' · hipNew == child_local · hipOld  (row vectors, the guest's
				// convention); in Godot's column-major terms: local' = hipNew⁻¹ · hipOld · local.
				Transform3D fix = hipNew.AffineInverse() * hipOld;
				foreach (int child in _hipChildren)
				{
					Transform3D local = fix * PoseOf(child);
					_rotation[child] = local.Basis.GetRotationQuaternion().Normalized();
					_rotationWeight[child] = 1f;
					_position[child] = local.Origin;
					_positionWeight[child] = 1f;
				}

				_rotation[hip] = hipNew.Basis.GetRotationQuaternion().Normalized();
				_position[hip] = hipNew.Origin;
			}
			i = j;
		}

	FinalizePose:
		LastOverlays = _overlays.Count;

		int restBones = 0;
		for (int b = 0; b < bones; ++b)
		{
			if (_rotationWeight[b] <= 0f)
			{
				++restBones;
			}

			Quaternion rotation = _rotationWeight[b] > 0f ? _rotation[b].Normalized() : _restRotation[b];
			if (_faceStated && _isFacialBone[b] && _faceHas[b])
			{
				rotation = _faceRotation[b];
			}

			Vector3 position = _positionWeight[b] > 1e-5f ? _position[b] / _positionWeight[b] : _restPosition[b];
			_finalRotation[b] = rotation;
			_finalPosition[b] = position;
		}

		ApplyMorph(bones);
		_foldTopShare = topShare;
		_foldTopKey = top?.Leaf.Key ?? 0u;
		_foldRestBones = restBones;
	}

	/// <summary><see cref="Apply"/>'s last part, on the main thread: the folded pose onto the skeleton.</summary>
	public void Write()
	{
		// Fold may run off the main thread, and FrameWork is main-thread owned: only the write is timed.
		long workAt = System.Diagnostics.Stopwatch.GetTimestamp();
		int bones = _foldBones;
		float alpha = _foldAlpha;
		float topShare = _foldTopShare;
		int restBones = _foldRestBones;
		if (_foldShareStep > LargestShareStep)
		{
			LargestShareStep = _foldShareStep;
		}

		// One crossing per bone, and only for bones that moved. The pose is handed over whole
		// (Godot decomposes it back; `ScaledLocal` is its rotation-then-scale composition). A
		// write is a marshalled call that also dirties the skeleton for the render thread, so
		// an unchanged bone is skipped.
		if (_written.Length != bones)
		{
			_written = new Transform3D[bones];
			_writtenRotation = new Quaternion[bones];
			_writtenPosition = new Vector3[bones];
			_hasWritten = false;
		}

		int movedBones = 0;
		float moved = 0f;
		bool watched = Address != 0u && Address == WatchAddress;

		// THE SHADOW SKELETON TAKES THE SAME BONES, ON GUEST FRAMES (every frame for a principal).
		// Between guest frames the body takes writes the shadow does not, so the first mirror
		// after any of those writes every bone, not only the ones that moved this frame.
		bool shadowNow = Refii.Entities.Engine_ShadowSkeletons.WriteNow(_skeleton, out Skeleton3D? shadow);
		bool shadowAll = shadowNow && _shadowStale;
		// The skeleton takes rotation and translation; the scale binds through the skin (see
		// ApplyMorph), and only when the record was re-read. See SkinScale for which of the
		// morph factor (`_finalScale`) and the guest's stated lane (Engine_EntitySkeleton.GuestScale)
		// a bone binds with.
		if (Rig is { } rig && (_scaleDirty || rig.FoldedCaptures != _laneCapturesSeen))
		{
			_skinScale ??= new Vector3[bones];
			if (_skinScale.Length != bones)
			{
				_skinScale = new Vector3[bones];
			}

			// The record each lane was folded with is the rig's, paired when the lane was taken;
			// no lane yet binds the record alone.
			_laneCapturesSeen = rig.FoldedCaptures;
			for (int b = 0; b < bones; ++b)
			{
				_skinScale[b] = _laneCapturesSeen == 0
					? _finalScale[b]
					: SkinScale(rig.FoldedLane(b), rig.FoldedRecord(b), _finalScale[b]);
			}

			rig.SetBoneScales(_skinScale);
			_scaleDirty = false;
		}

		for (int b = 0; b < bones; ++b)
		{
			// The same rotation and translation the written pose was built from build the same
			// pose, which IsEqualApprox would find unchanged: skipped without building it.
			if (_hasWritten && !shadowAll &&
				SameRawPose(_finalRotation[b], _finalPosition[b], _writtenRotation[b], _writtenPosition[b]))
			{
				++SkippedBoneWrites;
				continue;
			}

			var pose = new Transform3D(new Basis(_finalRotation[b]), _finalPosition[b]);
			bool unchanged = _hasWritten && pose.IsEqualApprox(_written[b]);
			if (unchanged && !shadowAll)
			{
				++SkippedBoneWrites;
				continue;
			}

			if (!unchanged)
			{
				if (watched && _hasWritten)
				{
					moved += _written[b].Origin.DistanceTo(pose.Origin);
				}

				_written[b] = pose;
				_writtenRotation[b] = _finalRotation[b];
				_writtenPosition[b] = _finalPosition[b];
				++movedBones;
				_skeleton.SetBonePose(b, pose);
			}

			if (shadowNow)
			{
				shadow!.SetBonePose(b, pose);
			}
		}

		if (shadowNow)
		{
			_shadowStale = false;
		}
		else if (movedBones != 0)
		{
			_shadowStale = true;
		}

		if (watched && _hasWritten)
		{
			NoteWatch(moved, alpha, topShare, _channels.Count, movedBones);
		}

		FrameWork.Lap(3, ref workAt);
		_hasWritten = true;
		_writtenChannelCount = _channels.Count;
		foreach (Channel c in _channels.Values)
		{
			c.WrittenTime = c.Time;
			c.WrittenWeight = c.Weight;
			c.WrittenOrder = c.Order;
			c.HasWritten = true;
		}

		LastMovedBones = movedBones;
		_wroteFrame = Refii.Core.Engine_ProcessFrame.Current;
		BoneWrites += movedBones;
		if (movedBones != 0)
		{
			SkeletonsWritten++;
		}

		// The readout that says whether a rig is in bind pose BY CONSTRUCTION: bones no live
		// channel covered are written as rest, so a frame with most bones here is a T pose.
		LastRestBones = restBones;
		LastTopKey = _foldTopKey;
		LastTopShare = topShare;
		LastChannels = _channels.Count;
		if (restBones * 2 > bones)
		{
			++MostlyRestFrames;
			ulong nowMs = Godot.Time.GetTicksMsec();
			if (RestLogs < 24 && nowMs - _lastRestLogMs > 3000)
			{
				++RestLogs;
				_lastRestLogMs = nowMs;
				var table = new System.Text.StringBuilder();
				foreach (Channel c in _channels.Values)
				{
					table.Append($" [{c.Leaf.Key:X8} uid {c.Leaf.Uid} pri {c.Leaf.Priority} trk {c.Leaf.Track}" +
						$" w {c.Weight:0.00}->{c.TargetWeight:0.00} {(c.Leaf.Overlay ? "OVL" : "PRI")}" +
						$"{(c.Leaf.Additive ? " add" : "")} mask {c.Leaf.Mask:X8} pm {c.Leaf.PriorityMask:X}" +
						$" seen {c.Seen} tracks {c.Built.Animation?.GetTrackCount() ?? c.Built.RotationTrack.Length}]");
				}

				GD.Print($"[rig-rest] '{_skeleton.Name}' {restBones}/{bones} rest, top {LastTopKey:X8} share {topShare:0.00} overlays {_overlays.Count}:{table}");
			}
		}

		++ClipDrivenFrames;
		++Driven;
		LastFallback = string.Empty;
	}

	/// <summary>Bones written as rest on the last applied frame — no live channel covered them.</summary>
	public int LastRestBones { get; private set; }

	/// <summary>The clip key of the top (highest-priority) channel on the last frame, or 0.</summary>
	public uint LastTopKey { get; private set; }

	public float LastTopShare { get; private set; }

	public int LastChannels { get; private set; }

	/// <summary>Frames on this rig where more than half the bones were written as rest.</summary>
	public int MostlyRestFrames { get; private set; }

	public int BoneCount => _restRotation.Length;

	public int LastOverlays { get; private set; }

	/// <summary>
	/// Frames the crossfade partner changed identity. A flip moves `topShare`, and therefore the
	/// drawn pose; on a rig standing still this should not climb.
	/// </summary>
	public static long RunnerFlips;

	private uint _lastRunnerUid;

	/// <summary>
	/// This rig's own flip count and its current crossfade partner.
	/// </summary>
	public long RigRunnerFlips { get; private set; }

	public uint LastTopUid { get; private set; }

	public uint LastRunnerUid => _lastRunnerUid;

	/// <summary>Whether this rig's bones were written in the current process frame.</summary>
	public bool WroteThisFrame => _wroteFrame == Refii.Core.Engine_ProcessFrame.Current;

	private ulong _wroteFrame = ulong.MaxValue;

	/// <summary>The guest entity this animator drives; 0 until it is adopted.</summary>
	public uint Address;

	/// <summary>
	/// The rig being watched, set from the debug channel. Pinned to a single rig so consecutive
	/// rows are that rig on consecutive frames; the other alpha rings mix subjects and cannot
	/// show a per-subject phase (clip time, blend weight, bone).
	/// </summary>
	/// <remarks>
	/// Setting this clears the ring, so no row from the previously watched rig survives.
	/// </remarks>
	public static uint WatchAddress
	{
		get => _watchAddress;
		set
		{
			_watchAddress = value;
			System.Array.Clear(Watch, 0, Watch.Length);
			_watchAt = 0;
		}
	}

	private static uint _watchAddress;

	private static readonly (ulong Usec, float Moved, float Alpha, float TopShare, int Channels, int Bones)[]
		Watch = new (ulong, float, float, float, int, int)[256];

	private static int _watchAt;

	private static void NoteWatch(float moved, float alpha, float topShare, int channels, int bones)
	{
		int at = System.Threading.Interlocked.Increment(ref _watchAt);
		Watch[at & 255] = (Godot.Time.GetTicksUsec(), moved, alpha, topShare, channels, bones);
	}

	/// <summary>One rig's drawn motion, frame by frame, oldest first; for the debug channel.</summary>
	public static List<Dictionary<string, object?>> WatchTrace()
	{
		var rows = new List<Dictionary<string, object?>>();
		int at = _watchAt;
		for (int back = 255; back >= 0; --back)
		{
			(ulong usec, float moved, float alpha, float share, int channels, int bones) =
				Watch[(at - back) & 255];
			if (usec == 0UL)
			{
				continue;
			}

			rows.Add(new Dictionary<string, object?>
			{
				["usec"] = usec,
				["moved"] = moved,
				["alpha"] = alpha,
				["topShare"] = share,
				["channels"] = channels,
				["bones"] = bones,
			});
		}

		return rows;
	}

	/// <summary>Frames the crossfade ratio moved more than a tenth in a single frame.</summary>
	public static long ShareSteps;

	/// <summary>The largest single-frame move of the crossfade ratio seen on any rig.</summary>
	public static float LargestShareStep;

	public long RigShareSteps { get; private set; }

	private float _lastShare;

	private bool _haveLastShare;

	/// <summary>Frames a rig kept its last pose because the guest listed no primary record.</summary>
	public static int HeldNoPrimaryFrames;

	private static int RestLogs;

	private ulong _lastRestLogMs;

	/// <summary>One clip's contribution at one weight into an accumulation buffer.</summary>
	private void Sample(Built built, float time, float weight, bool additive,
		Quaternion[] rotation, float[] rotationWeight, Vector3[] position, float[] positionWeight, int bones)
	{
		for (int b = 0; b < bones; ++b)
		{
			// BONE 0 IS IDENTITY IN THE GUEST'S POSE — rotation, translation AND scale. After
			// sampling, `sub_82208040` (0x82208140..78) overwrites bone 0 with (0,0,0,1) / zero /
			// one unconditionally, and `sub_821F57D0` does it again before composing model
			// space; the root's own sampled transform is parked at comp19+48 / comp6+64 and read
			// by nothing (whole-binary search, 2026-09-05). Every other bone keeps its sampled
			// LOCAL transform. Keeping the root's rotation here, as this once did, turned or
			// tilted a whole rig by a track the guest never applies.
			bool isRoot = _parents![b] < 0;
			int rt = built.RotationTrack[b];
			if (rt >= 0 && !isRoot)
			{
				Quaternion q = SampleRotation(built, b, rt, time);
				if (additive)
				{
					Quaternion first = SampleRotation(built, b, rt, 0f);
					Quaternion delta = q * first.Inverse();
					rotation[b] = rotationWeight[b] > 0f
						? (Quaternion.Identity.Slerp(delta, weight) * rotation[b]).Normalized()
						: rotation[b];
				}
				else
				{
					Accumulate(ref rotation[b], ref rotationWeight[b], q, weight);
				}
			}

			// NO ROOT TRANSLATION EITHER — the same rule; sampled here too it walked the body
			// away from its placement each loop and snapped it back at the wrap.
			int pt = built.PositionTrack[b];
			if (pt >= 0 && !additive && !isRoot)
			{
				Vector3 p = SamplePosition(built, b, pt, time);
				position[b] = positionWeight[b] > 0f ? position[b] + p * weight : p * weight;
				positionWeight[b] += weight;
			}
		}
	}

	/// <summary>
	/// The per-bone bytes of an overlay's mask node, by guest bone index; empty for a whole-body
	/// overlay. Cached by record uid AND node address: the node lives as long as the record, and
	/// a recycled address under a new uid is a different mask.
	/// </summary>
	private byte[] Mask(uint uid, uint maskNode)
	{
		if (maskNode == 0)
		{
			return System.Array.Empty<byte>();
		}

		ulong key = ((ulong)uid << 32) | maskNode;
		if (!_masks.TryGetValue(key, out byte[]? bytes))
		{
			bytes = HostBridge.GetAnimMask(maskNode, out _);
			_masks[key] = bytes;
			if (_masks.Count > 64)
			{
				_masks.Clear();
				_masks[key] = bytes;
			}
		}

		return bytes;
	}

	/// <summary>A mask <see cref="Begin"/> already read; the fold never reads the guest.</summary>
	private byte[] MaskTaken(uint uid, uint maskNode) =>
		maskNode == 0 || !_masks.TryGetValue(((ulong)uid << 32) | maskNode, out byte[]? bytes)
			? System.Array.Empty<byte>()
			: bytes;

	/// <summary>Seconds between re-reads of the morph record; it moves with the character, slowly.</summary>
	private const double MorphRefreshSeconds = 1.0;

	private double _morphReadAt = double.NegativeInfinity;

	private Vector3[] _morphOffset = System.Array.Empty<Vector3>();

	private Vector3[] _morphScale = System.Array.Empty<Vector3>();

	private Vector3[] _finalPosition = System.Array.Empty<Vector3>();

	/// <summary>The pose being assembled this frame; written to the skeleton once per bone at the end.</summary>
	private Quaternion[] _finalRotation = System.Array.Empty<Quaternion>();

	private Vector3[] _finalScale = System.Array.Empty<Vector3>();

	private bool _hasMorph;

	/// <summary>
	/// THE BODY'S PROPORTIONS, after the fold: the guest's `sub_822BF9B8` adds each morphed
	/// bone's offset (pre-divided by the parent's scale) and multiplies its scale, and its
	/// model-space compose carries NO scale — a bone's scale is its own, never inherited —
	/// while `sub_821E5AC0` scales each child's translation by the parent's. Godot inherits
	/// scale down the chain, which gives the translation scaling for free and would also scale
	/// every descendant's geometry; dividing each bone's scale by its parent's leaves every
	/// bone at exactly its own scale, the guest's result.
	/// </summary>
	/// <summary>Ops applied on the last frame, for the readout.</summary>
	public int PostOpsApplied { get; private set; }

	/// <summary>
	/// THE GUEST'S AIM AND LOOK, after the fold. `sub_822DC980` ends every node's evaluation
	/// by rewriting the skeleton's aim chain (5 bones) and look chain (6 bones) from the
	/// per-bone 4-vectors its IK stated on the node's records (+8 aim, +12 look), through
	/// `sub_822D9738`: with q the bone's folded local rotation and t the stated vector, q is
	/// negated when q·t &lt; 0, then by the record's mode — 1: q + w·t, 2: t + w·(q − t),
	/// 3: q + w·(t − q), 4: q + w·(q⊗t − q), otherwise t — and the result normalised (vmaddfp is A×C+B).
	/// Component-wise, so the Y-up conjugation commutes with it and the host applies the same
	/// arithmetic to its own local pose. Only the strongest record's ops count per chain: the
	/// guest applies each record's inside that record's evaluation, and stacking two here
	/// would double a head turn during a crossfade.
	/// </summary>
	/// <summary>
	/// Copies each active record's aim and look post-ops on the main thread and smoothly filters
	/// target quaternions and weights on the Host clock (<paramref name="dt"/>) so 15 Hz guest
	/// updates to upper-body <c>lookRec</c> (<c>mode = 2</c>, <c>w ~ 2.22</c> on the 6 arm/shoulder
	/// bones) never snap at 15 Hz tick boundaries.
	/// </summary>
	private void TakePostOps(float dt)
	{
		_postKinds = 0;
		for (int i = 0; i < MaxTakenPostOps; i++)
		{
			_postOpSeen[i] = false;
		}

		// READ ONCE PER GUEST FRAME, as the leaves are (Begin): between two guest frames the
		// records read the same. The ramps below still step every frame, on the held copy.
		uint guestFrame = Refii.Core.Engine_GuestFrame.Frame;
		if (guestFrame == 0u || guestFrame != _postOpsFrame || _entity != _postOpsEntity)
		{
			_heldPostOps.Take(HostBridge.GetAnimPostOps(_entity));
			_postOpsFrame = guestFrame;
			_postOpsEntity = _entity;
		}

		HostBridge.GuestPostOps? ops = _heldPostOps.Value;
		if (ops is not { } post || post.Ops.Count == 0)
		{
			for (int i = 0; i < MaxTakenPostOps; i++)
			{
				_postOpActive[i] = false;
			}
			_takenPostOpCount = 0;
			return;
		}

		for (int kind = 0; kind < 2; kind++)
		{
			int[] srcChain = kind == 0 ? post.AimBones : post.LookBones;
			int n = System.Math.Min(srcChain.Length, 6);
			int[] dstChain = _postChain[kind] ??= new int[6];
			System.Array.Copy(srcChain, dstChain, n);
			_postChainLen[kind] = n;
		}

		foreach (HostBridge.GuestPostOp op in post.Ops)
		{
			if (op.Mode == 0 || op.RecordWeight <= 1e-4f || (uint)op.Kind >= 2u)
			{
				continue;
			}

			int slot = -1;
			for (int i = 0; i < MaxTakenPostOps; i++)
			{
				if ((_postOpSeen[i] || _postOpActive[i]) &&
					_postOpUid[i] == op.RecordUid && _postOpKind[i] == op.Kind)
				{
					slot = i;
					break;
				}
			}

			if (slot >= 0)
			{
				if (_postOpSeen[slot] && op.RecordWeight <= _postOpRecordWeight[slot])
				{
					continue;
				}
			}
			else
			{
				for (int i = 0; i < MaxTakenPostOps; i++)
				{
					if (!_postOpSeen[i] && !_postOpActive[i])
					{
						slot = i;
						break;
					}
				}
				if (slot < 0)
				{
					for (int i = 0; i < MaxTakenPostOps; i++)
					{
						if (!_postOpSeen[i])
						{
							slot = i;
							break;
						}
					}
				}
				if (slot < 0)
				{
					continue;
				}
				_postOpActive[slot] = false;
			}

			bool wasActive = _postOpActive[slot] && !_postOpSeen[slot];
			float blend = !wasActive || dt <= 0f
				? 1f
				: Mathf.Clamp(1f - Mathf.Exp(-18f * dt), 0f, 1f);

			int chainLen = _postChainLen[op.Kind];
			int count = System.Math.Min(chainLen, System.Math.Min(op.Targets.Length, 6));
			Quaternion[] dstTarget = _postOpTargets[slot] ??= new Quaternion[6];
			for (int b = 0; b < count; b++)
			{
				Quaternion next = op.Targets[b];
				dstTarget[b] = !wasActive || b >= _postOpTargetCount[slot]
					? next
					: SmoothPostOpTarget(dstTarget[b], next, blend);
			}

			_postOpUid[slot] = op.RecordUid;
			_postOpKind[slot] = op.Kind;
			_postOpMode[slot] = op.Mode;
			_postOpWeight[slot] = wasActive ? Mathf.Lerp(_postOpWeight[slot], op.Weight, blend) : op.Weight;
			_postOpRecordWeight[slot] = op.RecordWeight;
			_postOpTargetCount[slot] = count;
			_postOpSeen[slot] = true;
			_postKinds |= 1 << op.Kind;
		}

		int maxSlot = 0;
		for (int i = 0; i < MaxTakenPostOps; i++)
		{
			_postOpActive[i] = _postOpSeen[i];
			if (_postOpSeen[i])
			{
				maxSlot = i + 1;
			}
		}
		_takenPostOpCount = maxSlot;
	}

	/// <summary>
	/// Smoothly interpolates a post-op bone target quaternion toward <paramref name="target"/>
	/// on the Host clock with hemisphere alignment.
	/// </summary>
	public static Quaternion SmoothPostOpTarget(Quaternion current, Quaternion target, float blend)
	{
		if (!target.IsFinite() || target.LengthSquared() < 1e-8f)
		{
			return target;
		}
		if (!current.IsFinite() || current.LengthSquared() < 1e-8f || blend >= 0.9999f)
		{
			return target.Normalized();
		}

		Quaternion c = current.Normalized();
		Quaternion t = target.Normalized();
		if (c.Dot(t) < 0f)
		{
			t = -t;
		}

		return c.Slerp(t, Mathf.Clamp(blend, 0f, 1f));
	}

	/// <summary>
	/// One read of <see cref="HostBridge.GetAnimPostOps"/>, copied: the bridge refills one set of
	/// shared buffers on every call, for every entity, so a read kept past the call is copied out.
	/// </summary>
	internal sealed class HeldPostOps
	{
		private int[] _aim = System.Array.Empty<int>();
		private int[] _look = System.Array.Empty<int>();
		private readonly List<HostBridge.GuestPostOp> _ops = new();
		private readonly List<Quaternion[]> _targets = new();

		/// <summary>The copy of the last read, or null when that read was null.</summary>
		public HostBridge.GuestPostOps? Value { get; private set; }

		/// <summary>Replaces the held read with a copy of <paramref name="source"/>.</summary>
		public void Take(HostBridge.GuestPostOps? source)
		{
			if (source is not { } post)
			{
				Value = null;
				return;
			}

			_aim = CopyInto(_aim, post.AimBones);
			_look = CopyInto(_look, post.LookBones);
			_ops.Clear();
			for (int i = 0; i < post.Ops.Count; i++)
			{
				HostBridge.GuestPostOp op = post.Ops[i];
				if (_targets.Count <= i)
				{
					_targets.Add(System.Array.Empty<Quaternion>());
				}

				_targets[i] = CopyInto(_targets[i], op.Targets);
				_ops.Add(op with { Targets = _targets[i] });
			}

			Value = new HostBridge.GuestPostOps(post.AimOn, post.LookOn, _aim, _look, _ops);
		}

		private static T[] CopyInto<T>(T[] held, T[] source)
		{
			if (held.Length != source.Length)
			{
				held = new T[source.Length];
			}

			System.Array.Copy(source, held, source.Length);
			return held;
		}
	}

	private readonly HeldPostOps _heldPostOps = new();
	private uint _postOpsFrame;
	private uint _postOpsEntity;

	private const int MaxTakenPostOps = 8;
	private int _postKinds;
	private int _takenPostOpCount;
	private readonly int[]?[] _postChain = new int[]?[2];
	private readonly int[] _postChainLen = new int[2];
	private readonly uint[] _postOpUid = new uint[MaxTakenPostOps];
	private readonly int[] _postOpKind = new int[MaxTakenPostOps];
	private readonly int[] _postOpMode = new int[MaxTakenPostOps];
	private readonly float[] _postOpWeight = new float[MaxTakenPostOps];
	private readonly float[] _postOpRecordWeight = new float[MaxTakenPostOps];
	private readonly int[] _postOpTargetCount = new int[MaxTakenPostOps];
	private readonly bool[] _postOpActive = new bool[MaxTakenPostOps];
	private readonly bool[] _postOpSeen = new bool[MaxTakenPostOps];
	private readonly Quaternion[]?[] _postOpTargets = new Quaternion[]?[MaxTakenPostOps];

	/// <summary>
	/// Evaluates a single bone's post-op rotation according to `sub_822D9738` (`0x822D9738`):
	/// aligns hemisphere (`q · t &lt; 0` negates `q`), applies mode 1..4 component-wise, and
	/// normalizes the result.
	/// </summary>
	public static Quaternion EvaluatePostOpBone(Quaternion currentPose, Quaternion targetPose, int mode, float weight)
	{
		if (!targetPose.IsFinite() || targetPose.LengthSquared() < 1e-8f)
		{
			return currentPose;
		}

		Quaternion q = currentPose;
		if (q.Dot(targetPose) < 0f)
		{
			q = -q;
		}

		// Mode 4's helper (`sub_82233200`) is a quaternion product: the stated vector is a
		// DELTA applied in the bone's own frame, and the pose moves toward q⊗t by w.
		Quaternion r = mode switch
		{
			1 => q + targetPose * weight,
			2 => targetPose + (q - targetPose) * weight,
			3 => q + (targetPose - q) * weight,
			4 => q + (q * targetPose - q) * weight,
			_ => targetPose,
		};
		if (!r.IsFinite() || r.LengthSquared() < 1e-8f)
		{
			return currentPose;
		}

		return r.Normalized();
	}

	/// <summary>
	/// THE GUEST'S AIM AND LOOK, applied per primary record BEFORE overlays (`sub_82229960` /
	/// `sub_822DC980`). `sub_822DC980` ends the primary record's root node evaluation by rewriting
	/// the skeleton's aim chain (5 bones) and look chain (6 bones) from the per-bone 4-vectors its
	/// IK stated on the node's records (+8 aim, +12 look), through `sub_822D9738`, before the outer
	/// overlay `Type-3` / `Type-1` wrappers blend upper-body overlays on top.
	/// </summary>
	private int ApplyPostOpsForRecord(uint recordUid, Quaternion[] rotation, float[] rotationWeight, int bones)
	{
		if (_postKinds == 0 || _takenPostOpCount == 0)
		{
			return 0;
		}

		int applied = 0;
		for (int kind = 0; kind < 2; kind++)
		{
			if ((_postKinds & (1 << kind)) == 0)
			{
				continue;
			}

			int matchSlot = -1;
			for (int s = 0; s < _takenPostOpCount; s++)
			{
				if (_postOpActive[s] && _postOpKind[s] == kind && (_postOpUid[s] == recordUid || _postOpUid[s] == 0u))
				{
					matchSlot = s;
					break;
				}
			}

			if (matchSlot < 0)
			{
				continue;
			}

			int[] chain = _postChain[kind]!;
			Quaternion[] targets = _postOpTargets[matchSlot]!;
			float w = _postOpWeight[matchSlot];
			int mode = _postOpMode[matchSlot];
			int count = _postOpTargetCount[matchSlot];
			for (int i = 0; i < count; i++)
			{
				int guest = chain[i];
				int b = guest >= 0 && guest < _guestToModel.Length ? _guestToModel[guest] : -1;
				if (b < 0 || b >= bones)
				{
					continue;
				}

				Quaternion t = targets[i];
				if (!t.IsFinite() || t.LengthSquared() < 1e-8f)
				{
					continue;
				}

				Quaternion current = rotationWeight[b] > 0f ? rotation[b].Normalized() : _restRotation[b];
				rotation[b] = EvaluatePostOpBone(current, Engine_AnimSkeleton.ToYUp(t), mode, w);
				rotationWeight[b] = 1f;
				applied++;
			}
		}

		return applied;
	}

	/// <summary>Each facial bone's local rotation as the guest's published pose states it.</summary>
	private Quaternion[] _faceRotation = System.Array.Empty<Quaternion>();

	/// <summary>The latest stated rotation each <see cref="_faceRotation"/> entry eases to.</summary>
	private Quaternion[] _faceTarget = System.Array.Empty<Quaternion>();

	private bool[] _faceHas = System.Array.Empty<bool>();
	private bool _faceStated;
	private uint _faceFrame;
	private uint _faceEntity;

	/// <summary>
	/// WHAT THE GUEST'S PUBLISHED POSE STATES THAT NO CLIP HERE CARRIES, read once per guest frame
	/// on the main thread (<see cref="HostBridge.GetEntityPose"/>):
	/// <para>THE SCALE LANES, on every rig. A clip's scale is not sampled here; the bone's scale is
	/// the pose block's own lane (+0x20), which the skin binds with (<see cref="SkinScale"/>). The
	/// rig pairs it with its record (<see cref="Refii.Entities.Engine_EntitySkeleton.TakeStatedLanes"/>),
	/// so a lane the pose feed captured before the clips took the rig over is not kept after the
	/// guest's has moved on — a Demon Door's head is 0.1 deep asleep and 1 awake.</para>
	/// <para>THE FACE, on a facial rig. The block is what the mesh is skinned with and what the eye
	/// models ride (`sub_82209480` reads the posed `Shadow_L_Eye` / `Shadow_R_Eye`), so a facial
	/// bone's local rotation is that block's: conj(parent) ⊗ bone, eased on the host clock between
	/// guest frames.</para>
	/// A torn or absent block keeps the last statement.
	/// </summary>
	private void TakeStatedPose(float dt, int bones)
	{
		if (!_hasFacialRig)
		{
			_faceStated = false;
		}
		else if (_faceRotation.Length != bones)
		{
			_faceRotation = new Quaternion[bones];
			_faceTarget = new Quaternion[bones];
			_faceHas = new bool[bones];
			_faceStated = false;
		}

		uint guestFrame = Refii.Core.Engine_GuestFrame.Frame;
		if (guestFrame == 0u || guestFrame != _faceFrame || _entity != _faceEntity)
		{
			_faceFrame = guestFrame;
			if (_entity != _faceEntity)
			{
				_faceEntity = _entity;
				_faceStated = false;
				System.Array.Fill(_faceHas, false);
			}

			HostBridge.BonePose? stated = HostBridge.GetEntityPose(_entity);
			if (stated is { Torn: false } lanes)
			{
				Rig?.TakeStatedLanes(_entity, lanes);
			}

			if (_hasFacialRig && stated is { Torn: false, IsModelSpace: true } pose)
			{
				bool first = !_faceStated;
				for (int b = 0; b < bones; ++b)
				{
					if (!_isFacialBone[b])
					{
						continue;
					}

					int g = b < _modelToGuest.Length ? _modelToGuest[b] : -1;
					if (g <= 0 || g >= pose.BoneCount)
					{
						continue;
					}

					int parent = pose.Parents[g];
					if (parent < 0 || parent >= pose.BoneCount)
					{
						continue;
					}

					Quaternion own = pose.Rotation(g);
					Quaternion above = pose.Rotation(parent);
					if (!own.IsFinite() || !above.IsFinite() || own.LengthSquared() < 1e-8f || above.LengthSquared() < 1e-8f)
					{
						continue;
					}

					Quaternion local = Refii.Entities.Engine_EntitySkeleton.GuestRotation(
						above.Normalized().Inverse() * own.Normalized());
					_faceTarget[b] = local;
					if (first || !_faceHas[b])
					{
						_faceRotation[b] = local;
					}

					_faceHas[b] = true;
				}

				_faceStated = true;
			}
		}

		if (!_hasFacialRig || !_faceStated || dt <= 0f)
		{
			return;
		}

		float blend = Mathf.Clamp(1f - Mathf.Exp(-18f * dt), 0f, 1f);
		for (int b = 0; b < bones; ++b)
		{
			if (_faceHas[b])
			{
				_faceRotation[b] = _faceRotation[b].Slerp(_faceTarget[b], blend).Normalized();
			}
		}
	}

	/// <summary>The morph record, re-read every <see cref="MorphRefreshSeconds"/> on the main thread.</summary>
	private void RefreshMorph(int bones)
	{
		double now = Time.GetTicksMsec() / 1000.0;
		if (now - _morphReadAt >= MorphRefreshSeconds)
		{
			_morphReadAt = now;
			if (_morphOffset.Length != bones)
			{
				_morphOffset = new Vector3[bones];
				_morphScale = new Vector3[bones];
			}

			System.Array.Fill(_morphOffset, Vector3.Zero);
			System.Array.Fill(_morphScale, Vector3.One);
			_hasMorph = false;
			_scaleDirty = true;
			_morphHeight = HostBridge.GetBoneMorphHeight(_entity);
			_morphHeightBone = -1;
			foreach (HostBridge.GuestBoneMorph m in HostBridge.GetBoneMorph(_entity))
			{
				int model = m.Bone >= 0 && m.Bone < _guestToModel.Length ? _guestToModel[m.Bone] : -1;
				if (model < 0 || model >= bones)
				{
					continue;
				}

				if (m.Bone == 1 && _morphHeight > 0f)
				{
					_morphHeightBone = model;
					_hasMorph = true;
				}

				_morphOffset[model] = Engine_AnimSkeleton.ToYUp(m.Offset);
				_morphScale[model] = Engine_AnimSkeleton.ToYUp(m.Scale).Abs();
				if (m.Offset.LengthSquared() > 1e-10f || (m.Scale - Vector3.One).LengthSquared() > 1e-10f)
				{
					_hasMorph = true;
				}
			}
		}
	}

	public void InvalidateMorph()
	{
		_morphReadAt = double.NegativeInfinity;
		_scaleDirty = true;
	}

	private void ApplyMorph(int bones)
	{
		if (!_hasMorph)
		{
			if (_morphWasApplied)
			{
				System.Array.Fill(_finalScale, Vector3.One);
				_scaleDirty = true;
			}

			_morphWasApplied = false;
			return;
		}

		// THE GUEST'S ORDER (`sub_821F2C78`): the record first — `t += off / parent.s` and
		// `s *= sc` per entry (`sub_822BF9B8`) — then the pre-pass that multiplies every
		// child's LOCAL translation by its parent's own scale, lane-wise (`sub_821E5AC0`), and
		// only then the compose, which carries rotation and translation and never a scale
		// (`sub_821F58D8`). Net translation `t * s_parent + off`; the bone's scale is its OWN
		// and reaches the skin matrix alone (`sub_8222E1C0`), so it goes to the skin bind
		// through the rig (`Engine_EntitySkeleton.SetBoneScales`), never into the skeleton,
		// whose parent chain would inherit it. Model order is parent-before-child, so a
		// parent's scale is final before its children read it.
		for (int b = 0; b < bones; ++b)
		{
			int parent = _parents![b];
			Vector3 parentScale = parent >= 0 ? _morphScale[parent] : Vector3.One;
			Vector3 offset = _morphOffset[b];
			Vector3 t = _finalPosition[b];
			if (b == _morphHeightBone)
			{
				// Guest (t.x*h, t.y*h, t.z + off.z), no parent divide; its z is up, Y here.
				t = new Vector3(t.X * _morphHeight, t.Y + offset.Y, t.Z * _morphHeight);
				_finalPosition[b] = t * parentScale;
			}
			else if (parent >= 0)
			{
				_finalPosition[b] = (t * parentScale) + offset;
			}

			_finalScale[b] = _morphScale[b];
		}

		_morphWasApplied = true;
	}

	private bool _morphWasApplied;

	/// <summary>
	/// One bone's skin scale: the guest's stated pose lane with the record it was folded with
	/// swapped for the record in force now. The lane ALREADY CARRIES the record on both titles —
	/// Fable 2 `sub_822BF9B8` (0x822bfb20 the lane, 0x822bfb34 `vmulfp128` by the record's `sc`,
	/// 0x822bfb3c stored back to +0x20; bitwise `sc` on all 74 scaled bones of a live hero, One
	/// on the other 86) and Fable 3 `sub_8222AB68` (0x8222ac80, stored at 0x8222ac88) — so
	/// `lane * record` bound `sc²`. Dividing out the record at capture keeps the clip's own part
	/// of the lane: One on Fable 2, a Fable 3 helper's collapse (3e-5) or 4.5x even where that
	/// title's strong/tall records also scale the bone (hat, ponytail, pigtail chains).
	/// A lane of exactly One beside a record that is not is a pose the fold never reached — a
	/// load's pose stated before the guest's first update of the body — and its clip part is One:
	/// a folded clip part of One is the record itself.
	/// </summary>
	internal static Vector3 SkinScale(Vector3 guestLane, Vector3 recordAtCapture, Vector3 recordNow) =>
		new(Swap(guestLane.X, recordAtCapture.X, recordNow.X),
			Swap(guestLane.Y, recordAtCapture.Y, recordNow.Y),
			Swap(guestLane.Z, recordAtCapture.Z, recordNow.Z));

	private static float Swap(float lane, float atCapture, float now) =>
		lane == 1f ? now : Mathf.Abs(atCapture) > 1e-6f ? lane / atCapture * now : lane;

	/// <summary>The rig's <c>FoldedCaptures</c> the skin was last composed from.</summary>
	private int _laneCapturesSeen;

	/// <summary>The skin's scale per bone, composed by <see cref="SkinScale"/> on each push.</summary>
	private Vector3[]? _skinScale;

	/// <summary>Set when <see cref="_finalScale"/> was re-read, so the rig's skin takes it once.</summary>
	private bool _scaleDirty;

	/// <summary>
	/// The rig this animator poses, when it has one: the bone scales go to its skin. Null
	/// when the animator drives a bare skeleton (the editor), where the scale has nowhere to go.
	/// </summary>
	public Refii.Entities.Engine_EntitySkeleton? Rig { get; set; }

	/// <summary>The record's height factor (rec+36) and the model bone it rewrites, -1 for none.</summary>
	private float _morphHeight;

	private int _morphHeightBone = -1;

	private uint _entity;

	/// <summary>Guest bone index → model bone, the inverse of the map the rig was built with.</summary>
	private int[] _guestToModel = System.Array.Empty<int>();

	/// <summary>A bone's current accumulated pose as a local transform (rest where unwritten).</summary>
	private Transform3D PoseOf(int b)
	{
		Quaternion q = _rotationWeight[b] > 0f ? _rotation[b].Normalized() : _restRotation[b];
		Vector3 p = _positionWeight[b] > 1e-5f ? _position[b] / _positionWeight[b] : _restPosition[b];
		return new Transform3D(new Basis(q), p);
	}

	private bool HasSiblingInRecord(uint uid, Channel self)
	{
		foreach (Channel other in _channels.Values)
		{
			if (!ReferenceEquals(other, self) && other.Leaf.Uid == uid &&
				other.Leaf.Overlay == self.Leaf.Overlay && other.Leaf.Additive == self.Leaf.Additive)
			{
				return true;
			}
		}

		return false;
	}

	/// <summary>
	/// Computes the Host-clock step rate (<c>share / second</c>) for an intra-record blend-tree
	/// share transition so 15 Hz footstep crossfades track the guest's target share rate while
	/// honoring a minimum blend rate from <paramref name="blendSeconds"/>.
	/// </summary>
	public static float ComputeShareStepRate(float currentShare, float targetShare, float blendSeconds) =>
		Mathf.Max(Mathf.Abs(targetShare - currentShare) * 15f, 1f / Mathf.Max(blendSeconds, 0.133f));

	/// <summary>
	/// Crossfades one bone's primary rotation between partner and top records. A primary record
	/// that omits a bone evaluates to the skeleton rest pose (guest <c>sub_822351B0</c>), so
	/// bones keyed in locomotion and omitted in idle (such as back-sheath dummies) blend smoothly
	/// across <paramref name="topShare"/> instead of snapping at the end of the crossfade.
	/// </summary>
	public static Quaternion CrossfadePrimaryRotation(
		bool hasTop, Quaternion topRotation,
		bool hasPartner, Quaternion partnerRotation,
		Quaternion restRotation, float topShare)
	{
		Quaternion topQ = hasTop ? topRotation.Normalized() : restRotation;
		Quaternion partQ = hasPartner ? partnerRotation.Normalized() : restRotation;
		return partQ.Slerp(topQ, topShare);
	}

	/// <summary>
	/// Crossfades one bone's primary local position between partner and top records, falling back
	/// to <paramref name="restPosition"/> for any side that omits a position track.
	/// </summary>
	public static Vector3 CrossfadePrimaryPosition(
		float topWeight, Vector3 topAccum,
		float partnerWeight, Vector3 partnerAccum,
		Vector3 restPosition, float topShare)
	{
		Vector3 topP = topWeight > 1e-5f ? topAccum / topWeight : restPosition;
		Vector3 partP = partnerWeight > 1e-5f ? partnerAccum / partnerWeight : restPosition;
		return partP.Lerp(topP, topShare);
	}

	private static void Accumulate(ref Quaternion sum, ref float sumWeight, Quaternion q, float weight)
	{
		if (sumWeight <= 0f)
		{
			sum = q.Normalized();
			sumWeight = weight;
			return;
		}

		// Normalized spherical interpolation with hemisphere alignment, matching the guest's
		// `sub_82293278` quaternion blend across 2-leaf and 3-leaf locomotion trees.
		Quaternion baseQ = sum.Normalized();
		Quaternion unitQ = q.Normalized();
		Quaternion aligned = baseQ.Dot(unitQ) < 0f ? new Quaternion(-unitQ.X, -unitQ.Y, -unitQ.Z, -unitQ.W) : unitQ;
		float total = sumWeight + weight;
		float t = Mathf.Clamp(weight / total, 0f, 1f);
		sum = baseQ.Slerp(aligned, t);
		sumWeight = total;
	}

	/// <summary>
	/// Drops the written-pose cache, so the next pose writes every bone. Must be called
	/// whenever something else writes this skeleton — the pose feed takes over whenever
	/// <see cref="Apply"/> answers false — or the cache would claim bones the feed has since
	/// overwritten are still ours, and the rig would keep the feed's pose in those bones.
	/// </summary>
	public void ForgetWrittenPose()
	{
		// Only mark the shadow skeleton stale if this animator actually wrote to the live skeleton;
		// rigs with zero clip leaves (which immediately fall back to the raw bone feed) never wrote
		// here and should not force a full shadow catch-up.
		if (_hasWritten)
		{
			_shadowStale = true;
		}
		_hasWritten = false;
		_writtenChannelCount = -1;
		IsSettled = false;
	}

	/// <summary>The last pose written per bone, so an unchanged bone is not written again.</summary>
	private Transform3D[] _written = System.Array.Empty<Transform3D>();

	/// <summary>The rotation and translation each <see cref="_written"/> pose was built from.</summary>
	private Quaternion[] _writtenRotation = System.Array.Empty<Quaternion>();

	private Vector3[] _writtenPosition = System.Array.Empty<Vector3>();

	/// <summary>
	/// Whether a bone's rotation and translation are the ones its written pose was built from,
	/// compared with <c>==</c>: equal values build an equal pose, and NaN never matches, as
	/// IsEqualApprox never matches it.
	/// </summary>
	public static bool SameRawPose(Quaternion rotation, Vector3 position, Quaternion writtenRotation, Vector3 writtenPosition) =>
		rotation.X == writtenRotation.X && rotation.Y == writtenRotation.Y &&
		rotation.Z == writtenRotation.Z && rotation.W == writtenRotation.W &&
		position.X == writtenPosition.X && position.Y == writtenPosition.Y && position.Z == writtenPosition.Z;

	private bool _hasWritten;

	/// <summary>Bones actually written on the last pose; reported.</summary>
	public int LastMovedBones { get; private set; }

	/// <summary>Bone writes skipped because the pose was unchanged; reported.</summary>
	public static long SkippedBoneWrites;

	/// <summary>Bone writes actually made, and how many skeletons they touched. Against
	/// <see cref="SkippedBoneWrites"/> these say whether the frustum gate is doing anything in
	/// a given scene, and whether dirty skeletons are what `frameSetupCpuMs` is spending on.</summary>
	public static long BoneWrites;

	public static long SkeletonsWritten;

	/// <summary>How far short of a one-shot clip's end its playhead may rest: one sample under
	/// the last key, so the sample never lands on the wrap.</summary>
	private const float EndMargin = 1e-3f;

	/// <summary>
	/// Hands the stated pair to the channel's playhead. The clip time advances
	/// by `worldScale × entityScale / 15` per game tick through the clip's warp table
	/// (`sub_82200F60`, which shifts the current time into the leaf's previous slot), so the pair
	/// states both the position and the rate. Its twin `sub_822B4970` writes both slots the same
	/// value: a hard cut. A network replica's pair is the sender's packet extrapolated, so equal
	/// halves there are a stall, not a cut.
	/// </summary>
	/// <summary>
	/// Computes the initial `(RecordPrevious, SharePrevious)` pair for a newly created channel
	/// on its first observed tick. When <paramref name="recordAlreadyActive"/> is true (a new
	/// footstep or variation leaf was inserted into an already-playing record Uid), the record's
	/// previous weight is already at `recordCurrent` and the new leaf's share ramps smoothly from
	/// `0 -> leafShare` across the tick instead of jumping to `leafShare` at `pairAt = 0`.
	/// </summary>
	public static (float RecordPrevious, float SharePrevious) InitialChannelPair(
		bool recordAlreadyActive, float leafRecordPrevious, float leafRecordCurrent, float leafShare)
	{
		float recPrev = recordAlreadyActive ? leafRecordCurrent : leafRecordPrevious;
		float sharePrev = (recordAlreadyActive && leafShare < 0.999f) ? 0f : leafShare;
		return (recPrev, sharePrev);
	}

	/// <summary>
	/// Returns true when an unseen channel (`!c.Seen`) should be dropped immediately rather than
	/// ramping out via `BlendOut`: if its parent record Uid is still active in the current guest
	/// snapshot (<paramref name="recordStillSeen"/>), no clip in the snapshot was skipped due to
	/// async background decoding (<paramref name="hadPendingClip"/>), and its Host-smoothed
	/// intra-record share (<paramref name="smoothedShare"/>) has reached `0`.
	/// </summary>
	public static bool ShouldDropUnseenChannelInActiveRecord(
		bool recordStillSeen,
		bool hadPendingClip,
		float interpolatedWeight = 0f,
		float smoothedShare = 0f) =>
		recordStillSeen && !hadPendingClip &&
		interpolatedWeight <= 1e-4f &&
		smoothedShare <= 1e-4f;

	/// <summary>
	/// Returns true when an unseen channel in `_channels` (`!cand.Seen`) is a valid continuation
	/// of <paramref name="leafTime"/> / <paramref name="leafPreviousTime"/> rather than a dying
	/// channel from an earlier stride (e.g. an outgoing `WalkFootstep` at `0.522s` when a new
	/// `WalkFootstep` spawns at `0.207s` with `PreviousTime = 0.0s`).
	/// </summary>
	public static bool CanReuseUnseenChannel(
		float statedTime, float leafPreviousTime, float leafTime,
		float clipLength, bool loop, bool isStatic)
	{
		if (isStatic || clipLength <= 0.05f || leafPreviousTime == leafTime)
		{
			return true;
		}
		float diffPrev = Mathf.Abs(statedTime - leafPreviousTime);
		float diffCur = Mathf.Abs(statedTime - leafTime);
		if (loop && clipLength > 0f)
		{
			diffPrev = Mathf.Min(diffPrev, Mathf.Abs(diffPrev - clipLength));
			diffCur = Mathf.Min(diffCur, Mathf.Abs(diffCur - clipLength));
		}
		float limit = (float)Engine_ClipPlayhead.RestartSeconds;
		return diffPrev < limit || diffCur < 1e-3f;
	}

	private static void ObservePlayhead(Channel c, HostBridge.GuestAnimLeaf leaf, uint tick, double drawTick, bool isPropRig = false)
	{
		// Every read goes to the playhead, which tells a new tick from the same pair read again.
		c.PlayheadTick = tick;
		c.StatedTime = leaf.Time;
		if (isPropRig && !leaf.Network)
		{
			c.Playhead.ObserveProp(leaf.PreviousTime, leaf.Time, tick, c.Built.Length, leaf.Loop, drawTick);
			return;
		}
		bool cut = !leaf.Network && leaf.PreviousTime == leaf.Time;
		c.Playhead.Observe(leaf.PreviousTime, leaf.Time, tick, c.Built.Length, leaf.Loop, cut, drawTick);
	}

	/// <summary>
	/// The guest's transform playback delay for a replica: `sub_8239E9E0` stamps each received
	/// sample `arrival + flt_8209B650` (0.1 s) and the replica tick plays it then.
	/// </summary>
	private const double NetworkPlaybackDelayUsec = 100_000.0;

	/// <summary>Leaf sets of a replica rig, newest last, with the frame instant each was read.</summary>
	private readonly List<(ulong Usec, List<HostBridge.GuestAnimLeaf> Leaves)> _networkHistory = new();

	private readonly Stack<List<HostBridge.GuestAnimLeaf>> _spareLeafLists = new();

	/// <summary>
	/// ONE TIMELINE FOR A REPLICA'S BODY AND ITS LIMBS. The guest times the two streams from their
	/// own arrivals and neither carries a sender time: the animation tree (messages 174/175) plays
	/// from its receipt (`sub_825657C8` stores the arrival at comp19+164, `sub_82563B78` samples
	/// from it), the transform (messages 5/6) from arrival + 0.1 s (`sub_8239E9E0`). So the limbs
	/// ran a tenth of a second ahead of the body: measured 2026-10-08, the idle clip began while
	/// player two's replica glided on for 0.45 s — feet sliding. A modern client plays both from
	/// one buffered timeline; here the replica's leaf sets are held back by the same 0.1 s, so the
	/// clip changes land when the body does. Only a rig whose leaves are network-sampled is
	/// delayed; everything the guest animates locally is drawn as read.
	/// </summary>
	private int DelayNetworkLeaves(int count)
	{
		bool network = false;
		foreach (HostBridge.GuestAnimLeaf leaf in _leaves)
		{
			if (leaf.Network)
			{
				network = true;
				break;
			}
		}

		if (!network)
		{
			foreach ((ulong _, List<HostBridge.GuestAnimLeaf> spare) in _networkHistory)
			{
				_spareLeafLists.Push(spare);
			}

			_networkHistory.Clear();
			return count;
		}

		ulong now = Refii.Entities.Engine_DrawnFrame.NowUsec;
		List<HostBridge.GuestAnimLeaf> copy = _spareLeafLists.Count > 0 ? _spareLeafLists.Pop() : new();
		copy.Clear();
		copy.AddRange(_leaves);
		_networkHistory.Add((now, copy));

		// The newest set read at least the delay ago; until one exists, the oldest held.
		double due = now - NetworkPlaybackDelayUsec;
		int pick = 0;
		for (int i = _networkHistory.Count - 1; i >= 0; --i)
		{
			if (_networkHistory[i].Usec <= due)
			{
				pick = i;
				break;
			}
		}

		// Everything older than the pick is spent.
		for (int i = 0; i < pick; ++i)
		{
			_spareLeafLists.Push(_networkHistory[i].Leaves);
		}

		_networkHistory.RemoveRange(0, pick);
		_leaves.Clear();
		_leaves.AddRange(_networkHistory[0].Leaves);
		return _leaves.Count;
	}

	/// <summary>
	/// The record's weight at the draw point, as the guest draws it — `lerp(prev, cur, alpha)`
	/// (0x82211B08..0x82211B24) — with <paramref name="pairAt"/> 0 at the tick before the pair's
	/// and 1 at its own. Below 0 the draw point is still in the previous tick's pair; past 1 it
	/// holds, since a weight carried past its pair over-weights its channel.
	/// </summary>
	private static float RecordWeightAt(Channel c, double pairAt) =>
		pairAt < 0.0 && c.HasOlder
			? Mathf.Lerp(c.OlderPrevious, c.OlderCurrent, (float)System.Math.Clamp(pairAt + 1.0, 0.0, 1.0))
			: Mathf.Lerp(c.RecordPrevious, c.RecordCurrent, (float)System.Math.Clamp(pairAt, 0.0, 1.0));

	/// <summary>The clip time to sample now: the playhead moved by this frame's real time, wrapped
	/// for a looping clip and clamped short of the end otherwise.</summary>
	private static void Advance(Channel c, double seconds, double drawTick, bool isPropRig = false)
	{
		float length = c.Built.Length;
		if (length <= 0f)
		{
			c.Time = 0f;
			return;
		}

		// A leaf the guest no longer states has no position left to follow: it runs on at its
		// last rate while it ramps out. Steered toward its last pair, which stops moving, it was
		// held back at the speed limit and then snapped — a pop on every locomotion change.
		// Non-character prop rigs (doors, chests, clocks, shop signs) also advance autonomously
		// via Freewheel on the Host clock once triggered.
		double t = (c.Seen && !isPropRig) ? c.Playhead.Advance(seconds, drawTick) : c.Playhead.Freewheel(seconds, drawTick);
		if (c.Leaf.Loop)
		{
			t %= length;
			if (t < 0.0)
			{
				t += length;
			}

			c.Time = (float)t;
			return;
		}

		c.Time = Mathf.Clamp((float)t, 0f, length - EndMargin);
	}

	/// <summary>Built clips of this rig with their track coverage, for the debug channel.</summary>
	public List<Dictionary<string, object?>> DescribeClips()
	{
		var rows = new List<Dictionary<string, object?>>();
		foreach ((uint key, Built built) in _clips)
		{
			int found = 0;
			foreach (int t in built.RotationTrack)
			{
				if (t >= 0) ++found;
			}

			rows.Add(new Dictionary<string, object?>
			{
				["key"] = $"{key:X8}",
				["failed"] = built.Failed,
				["tracks"] = built.Failed ? 0 : built.Animation?.GetTrackCount() ?? -1,
				["shared"] = built.Animation is null,
				["bonesWithRotation"] = found,
				["bones"] = _skeleton.GetBoneCount(),
				["firstTrackPath"] = built.Animation is { } a && a.GetTrackCount() > 0 ? a.TrackGetPath(0).ToString() : null,
				["skeletonName"] = _skeletonName,
				["length"] = built.Length,
			});
		}

		return rows;
	}

	/// <summary>The rig's leaves as the guest states them, for the debug channel.</summary>
	public List<Dictionary<string, object?>> Describe(uint entity)
	{
		var rows = new List<Dictionary<string, object?>>();
		HostBridge.GetEntityAnimState(entity, _leaves, out uint flags);
		foreach (HostBridge.GuestAnimLeaf leaf in _leaves)
		{
			AnimClip? clip = Engine_AnimCatalog.Find(leaf.Key);
			rows.Add(new Dictionary<string, object?>
			{
				["clip"] = clip?.Name ?? $"{leaf.Key:X8}",
				["time"] = leaf.Time,
				["prev"] = leaf.PreviousTime,
				["weight"] = leaf.Weight,
				["loop"] = leaf.Loop,
				["additive"] = leaf.Additive,
				["priority"] = leaf.Priority,
				["uid"] = leaf.Uid,
				["playhead"] = _channels.TryGetValue(new ChannelKey(leaf.Uid, leaf.Key, 0), out Channel? ch) ? ch.Time : null,
				["hostWeight"] = ch?.Weight,
				["rate"] = ch?.Playhead.Rate,
				["error"] = ch?.Playhead.Error,
				["stamp"] = ch?.Playhead.Stamp,
				["target"] = ch?.TargetWeight,
				["overlay"] = leaf.Overlay,
				["mask"] = $"{leaf.Mask:X8}",
				["maskBytes"] = leaf.Mask != 0 ? Mask(leaf.Uid, leaf.Mask).Length : 0,
				["prioMask"] = $"{leaf.PriorityMask:X}",
				["track"] = leaf.Track,
				["blendIn"] = leaf.BlendIn,
				["blendOut"] = leaf.BlendOut,
				["driven"] = Driven,
				["lastFallback"] = LastFallback,
				["length"] = clip?.DurationSeconds,
			});
		}

		return rows;
	}

	private Built? Ensure(uint key, out bool pending)
	{
		pending = false;
		if (_clips.TryGetValue(key, out Built? built))
		{
			// The A/B lever turned off after a shared build: that build has no resource to read.
			if (ManagedClipSampling || built.Failed || built.Animation is not null)
			{
				return built.Failed ? null : built;
			}
		}

		if (ManagedClipSampling)
		{
			built = BuildShared(key, out pending);
			if (pending)
			{
				return null;
			}
		}
		else
		{
			built = BuildFromResource(key);
		}

		_clips[key] = built;
		return built.Failed ? null : built;
	}

	/// <summary>This rig's view of a shared clip: its bones pointed at the clip's tracks.</summary>
	/// <remarks>
	/// The same assignment <see cref="Engine_AnimBuilder.Build"/> + <c>FindTrack</c> made: a bone
	/// takes the FIRST clip track mapped to it, and only a bone with a parent takes translation.
	/// </remarks>
	private Built BuildShared(uint key, out bool pending)
	{
		pending = false;
		var built = new Built { KeysLifted = true };
		AnimClip? clip = Engine_AnimCatalog.Find(key);
		SharedClip? shared = clip is null ? null : SharedFor(clip, key, out pending);
		if (pending || clip is null || shared is null)
		{
			built.Failed = true;
			return built;
		}

		_boneCandidates ??= Engine_AnimBuilder.BoneCandidates(_skeleton);
		int[] map = Engine_AnimBuilder.MapTracks(clip, _skeleton, _boneCandidates);
		if (map.Length == 0)
		{
			built.Failed = true;
			return built;
		}

		int bones = _parents?.Length ?? _skeleton.GetBoneCount();
		built.Length = shared.Length;
		built.IsStatic = shared.IsStatic;
		built.RotationTrack = new int[bones];
		built.PositionTrack = new int[bones];
		built.RotationKeys = new Keys[bones];
		built.PositionKeys = new Keys[bones];
		System.Array.Fill(built.RotationTrack, -1);
		System.Array.Fill(built.PositionTrack, -1);
		bool mappedStatic = true;
		for (int track = 0; track < map.Length; ++track)
		{
			int b = map[track];
			if (b < 0 || b >= bones || built.RotationTrack[b] >= 0)
			{
				continue;
			}

			built.RotationTrack[b] = track;
			built.RotationKeys[b] = shared.Rotations[track];
			if (shared.Rotations[track].Animated)
			{
				mappedStatic = false;
			}

			int parent = _parents is not null && b < _parents.Length ? _parents[b] : _skeleton.GetBoneParent(b);
			if (parent >= 0)
			{
				built.PositionTrack[b] = track;
				built.PositionKeys[b] = shared.Positions[track];
				if (shared.Positions[track].Animated)
				{
					mappedStatic = false;
				}
			}
		}

		built.IsStatic |= mappedStatic;
		return built;
	}

	/// <summary>
	/// `anim_collapse_constant_tracks` (default off): a decoded track whose keys are all exactly
	/// equal is kept as its first key alone. Read once, at the first clip decoded.
	/// </summary>
	/// <remarks>
	/// The decoder resamples every track at every frame, so a two-second clip of an 80-bone rig
	/// holds 60 keys per bone whether the bone moves or not: 28 bytes a key, a few hundred KB a
	/// clip, and up to <see cref="SharedClipCap"/> clips shared, all in gen2. Most bones keep
	/// their bind translation and many (fingers, face, held props) keep their rotation for the
	/// whole clip. A one-key track samples as that key; the full track interpolated between equal
	/// keys gave the same translation exactly and the same rotation to the last bit of a slerp.
	/// The clip's static flag still comes from the decoded key counts (<see cref="Keys.Animated"/>).
	/// </remarks>
	private static readonly bool CollapseConstantTracks = ReadCollapseConstantTracks();

	private static bool ReadCollapseConstantTracks()
	{
		try
		{
			return Refii.Core.Engine_RefiiConfig.Bool(
				Refii.Core.Engine_RefiiConfig.Read(out _), "anim_collapse_constant_tracks", false);
		}
		catch (System.Exception)
		{
			return false;
		}
	}

	private static bool Constant(Quaternion[] keys)
	{
		for (int k = 1; k < keys.Length; k++)
		{
			if (keys[k] != keys[0])
			{
				return false;
			}
		}

		return keys.Length > 1;
	}

	private static bool Constant(Vector3[] keys)
	{
		for (int k = 1; k < keys.Length; k++)
		{
			if (keys[k] != keys[0])
			{
				return false;
			}
		}

		return keys.Length > 1;
	}

	private static readonly HashSet<SharedKey> SharedPending = new();
	private static long _decodeBudgetFrame = -1L;
	private static long _decodeBudgetTicks;
	private static readonly long MaxSyncDecodeTicksPerFrame =
		(long)(0.00035 * System.Diagnostics.Stopwatch.Frequency);

	private static SharedClip? DecodeToSharedClip(AnimClip clip, Engine_AnimDecoder decoder)
	{
		Engine_AnimBuilder.DecodedKeys? decoded = Engine_AnimBuilder.DecodeKeys(clip, decoder);
		if (decoded is null)
		{
			return null;
		}

		int tracks = decoded.Times.Length;
		bool isStatic = true;
		bool collapse = CollapseConstantTracks;
		float[]? firstTime = null;
		var shared = new SharedClip
		{
			Length = decoded.Length,
			Rotations = new Keys[tracks],
			Positions = new Keys[tracks],
			LastTaken = System.Environment.TickCount64,
		};
		for (int i = 0; i < tracks; ++i)
		{
			float[] times = decoded.Times[i];
			bool animated = times.Length > 1;
			if (animated)
			{
				isStatic = false;
			}

			Quaternion[] rotations = decoded.Rotations[i];
			Vector3[] positions = decoded.Positions[i];
			if (collapse && animated && Constant(rotations))
			{
				shared.Rotations[i] = new Keys
				{
					Times = firstTime ??= new[] { times[0] }, Rotations = new[] { rotations[0] }, Animated = true,
				};
			}
			else
			{
				shared.Rotations[i] = new Keys { Times = times, Rotations = rotations, Animated = animated };
			}

			if (collapse && animated && Constant(positions))
			{
				shared.Positions[i] = new Keys
				{
					Times = firstTime ??= new[] { times[0] }, Positions = new[] { positions[0] }, Animated = true,
				};
			}
			else
			{
				shared.Positions[i] = new Keys { Times = times, Positions = positions, Animated = animated };
			}
		}

		shared.IsStatic = isStatic;
		return shared;
	}

	private static SharedClip? SharedFor(AnimClip clip, uint key, out bool pending)
	{
		pending = false;
		var id = new SharedKey(Engine_AnimCatalog.ActiveDecoder, key);
		lock (SharedClips)
		{
			if (SharedClips.TryGetValue(id, out SharedClip? shared))
			{
				if (shared is not null)
				{
					shared.LastTaken = System.Environment.TickCount64;
				}

				return shared;
			}

			if (SharedPending.Contains(id))
			{
				pending = true;
				return null;
			}

			long frame = Refii.Entities.Engine_DrawnFrame.Frames;
			if (frame != _decodeBudgetFrame)
			{
				_decodeBudgetFrame = frame;
				_decodeBudgetTicks = 0;
			}

			if (_decodeBudgetTicks >= MaxSyncDecodeTicksPerFrame)
			{
				SharedPending.Add(id);
				pending = true;
				Refii.Core.Engine_SpikeNotes.Note(Refii.Core.Engine_SpikeNotes.Kind.ClipDecodeQueued);
				_ = System.Threading.Tasks.Task.Run(() =>
				{
					SharedClip? asyncShared = DecodeToSharedClip(clip, id.Decoder);
					lock (SharedClips)
					{
						if (SharedClips.Count >= SharedClipCap)
						{
							TrimSharedClips();
						}

						SharedClips[id] = asyncShared;
						SharedPending.Remove(id);
					}
				});
				return null;
			}
		}

		long t0 = System.Diagnostics.Stopwatch.GetTimestamp();
		SharedClip? builtShared = DecodeToSharedClip(clip, id.Decoder);
		long elapsed = System.Diagnostics.Stopwatch.GetTimestamp() - t0;

		Refii.Core.Engine_SpikeNotes.Note(Refii.Core.Engine_SpikeNotes.Kind.ClipDecode, elapsed);
		lock (SharedClips)
		{
			_decodeBudgetTicks += elapsed;
			if (SharedClips.Count >= SharedClipCap)
			{
				TrimSharedClips();
			}

			// A decode failure is the clip's, not the rig's: it is cached too.
			SharedClips[id] = builtShared;
			return builtShared;
		}
	}

	/// <summary>The per-rig resource path, kept for <see cref="ManagedClipSampling"/> = false.</summary>
	private Built BuildFromResource(uint key)
	{
		var built = new Built();
		AnimClip? clip = Engine_AnimCatalog.Find(key);
		Animation? animation = clip is null
			? null
			: Engine_AnimBuilder.Build(clip, Engine_AnimCatalog.ActiveDecoder, _skeleton, _skeletonName);
		if (animation is null)
		{
			built.Failed = true;
			return built;
		}

		int bones = _skeleton.GetBoneCount();
		built.Animation = animation;
		built.Length = (float)animation.Length;
		built.RotationTrack = new int[bones];
		built.PositionTrack = new int[bones];
		for (int b = 0; b < bones; ++b)
		{
			string path = $"{_skeletonName}:{_skeleton.GetBoneName(b)}";
			built.RotationTrack[b] = animation.FindTrack(path, Animation.TrackType.Rotation3D);
			built.PositionTrack[b] = animation.FindTrack(path, Animation.TrackType.Position3D);
		}

		LiftKeys(built);
		return built;
	}

	/// <summary>Drops the least recently taken half; rigs holding a dropped clip keep it.</summary>
	private static void TrimSharedClips()
	{
		var stamps = new List<long>(SharedClips.Count);
		foreach (SharedClip? c in SharedClips.Values)
		{
			stamps.Add(c?.LastTaken ?? 0);
		}

		stamps.Sort();
		long cut = stamps[stamps.Count / 2];
		var drop = new List<SharedKey>();
		foreach ((SharedKey k, SharedClip? c) in SharedClips)
		{
			if ((c?.LastTaken ?? 0) <= cut)
			{
				drop.Add(k);
			}
		}

		foreach (SharedKey k in drop)
		{
			SharedClips.Remove(k);
		}
	}
}
