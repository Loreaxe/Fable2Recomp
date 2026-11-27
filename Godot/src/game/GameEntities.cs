using System.Collections.Generic;
using Godot;
using Refii.Core;
using Refii.Host;

namespace Refii.Game;

/// <summary>
/// Draws the guest's LIVE entities — the plan's phase 1, on screen.
///
/// The world Godot builds from the banks is static: it is where the buildings are, and it
/// never changes. Everything that moves — the hero, NPCs, creatures, doors, anything a quest
/// touches — exists only inside the guest, which owns the simulation and tells us where each
/// thing is. This node is the join: read the transforms each frame, keep one node per entity,
/// and let Godot draw them.
///
/// IT IS A SIBLING OF THE REGION, not a child, so it carries the whole guest-to-Godot map
/// itself rather than inheriting the level root's mirror. See <see cref="GameWorld.ToGodot"/>
/// — the same reasoning, and the same trap: matching only the per-placement half puts
/// everything in the region's internal space, which is the level reflected through X.
///
/// Entities the bridge could not place are NOT drawn. It reports their placement vtables
/// instead, because an entity at a plausible wrong position is worse than a missing one — it
/// looks like a physics bug rather than a gap in a table.
/// </summary>
public partial class GameEntities : Node3D
{
    /// <summary>
    /// The region's build-time copy of everything the guest places, indexed by `.save` record.
    ///
    /// Set by <see cref="GameWorld"/>. Removing the whole static build instead emptied the
    /// street: only 1030 of the guest's 1437 live entities name a model, and the props
    /// container also holds foliage and level geometry the guest never states. Retiring ONE
    /// RECORD AT A TIME, and only once a live node has actually been created, removes the
    /// duplicate without betting on a count.
    /// </summary>
    public Refii.Entities.Engine_StaticTwins? Twins { get; set; }

    /// <summary>
    /// The entities that hang off another entity's bone. Placed in a second pass, after every
    /// parent has been given its placement and its pose this frame — a child drawn from last
    /// frame's parent is the lag this exists to remove.
    /// </summary>
    private readonly Refii.Entities.Engine_EntityAttachments _attachments = new();

    /// <summary>Children waiting for their parent: address, node, and the guest's own placement.</summary>
    private readonly List<(uint Child, MeshInstance3D Node, Transform3D Stated)> _attached = new();


    /// <summary>Nodes by guest entity address, so a moved entity is updated rather than rebuilt.</summary>
    /// <summary>The live feed node, for the debug channel's levers; retired when that owner leaves the tree.</summary>
    public static GameEntities? Current;

    /// <summary>When true (set via <c>dx12_skip_3d=hero</c>), keeps the principal hero hidden for D3D12 bisection.</summary>
    public static bool SkipHeroRendering { get; set; }

    private bool _heroTraceLogged;

    /// <summary>
    /// Whether the feed re-walks the guest every drawn frame. Off: it walks once per guest
    /// frame (<c>Engine_GuestFrame.Changed</c>, 30 Hz) and redraws the pairs in between.
    /// </summary>
    public static bool FeedEveryFrame = false;

    public static int FeedFramesSkipped;
    private uint _feedTickApplied = uint.MaxValue;

    private readonly Dictionary<uint, MeshInstance3D> _nodes = new();

    /// <summary>
    /// The node built for one live entity, by guest address, or null. For a consumer that draws
    /// an entity somewhere OTHER than where the feed placed it — the co-op screen's mannequin
    /// stands in an empty front-end world and belongs in a panel. Read its mesh; do not re-parent
    /// the node, the feed still drives it.
    /// </summary>
    public MeshInstance3D? NodeFor(uint address) =>
        _nodes.TryGetValue(address, out MeshInstance3D? node) && GodotObject.IsInstanceValid(node) ? node : null;

    /// <summary>
    /// Where the body of one live entity is, for the consumer <see cref="NodeFor"/> answered null:
    /// not walked, walked with nothing stated, stated and building, or built.
    /// </summary>
    public Refii.Entities.Engine_FigureBody.State BodyStateOf(uint address) =>
        Refii.Entities.Engine_FigureBody.Of(
            _lastModelHash.TryGetValue(address, out uint statedModel),
            _compositeParts.TryGetValue(address, out uint[]? statedParts) ? statedParts.Length : 0,
            statedModel != 0u,
            NodeFor(address) is not null);

    public bool TryDecorationGeometry(uint address, out Mesh mesh, out Transform3D transform, out uint layers)
    {
        mesh = null!; transform = default; layers = 0;
        if (_adoptions.TryGetValue(address, out var adopted) && IsInstanceValid(adopted.Twin.Node) &&
            adopted.Twin.Node.IsInsideTree() && adopted.Twin.Node.IsVisibleInTree())
        {
            if (adopted.Twin.Node is MultiMeshInstance3D { Multimesh: { Mesh: { } batchedMesh } instances } batch &&
                adopted.Twin.Instance >= 0 && adopted.Twin.Instance < instances.InstanceCount)
            {
                mesh = batchedMesh;
                transform = batch.GlobalTransform * instances.GetInstanceTransform(adopted.Twin.Instance);
                layers = batch.Layers;
                return true;
            }
            if (adopted.Twin.Node is MeshInstance3D { Mesh: { } drawnMesh } drawn)
            {
                mesh = drawnMesh; transform = drawn.GlobalTransform; layers = drawn.Layers;
                return true;
            }
        }
        if (NodeFor(address) is not { Mesh: { } body } node || !node.IsInsideTree() ||
            !node.IsVisibleInTree() && !(_batches?.Contains(address) ?? false)) return false;
        mesh = body; transform = node.GlobalTransform; layers = node.Layers;
        return true;
    }

    public bool TryDecorationInterior(uint address, out Mesh mesh, out Transform3D transform)
    {
        mesh = null!; transform = default;
        if (NodeFor(address) is not { } node || !node.IsInsideTree()) return false;
        if (_interiorMeshesPending.TryGetValue(address, out var interior)) mesh = interior;
        else if (_multiMeshSplit.TryGetValue(address, out var parts) && parts.Exterior.Length == 0 &&
            parts.Interior.Length > 0 && node.Mesh is { } body) mesh = body;
        else return false;
        transform = node.GlobalTransform;
        return true;
    }

    /// <summary>
    /// How far an entity is DRAWN from where the guest last stated it — the playhead's blend,
    /// as a world offset. Anything the guest anchors on an entity at its tick (a heart over a
    /// walking villager) adds this to ride the body that is on screen.
    /// </summary>
    public Vector3 DrawnLead(uint address) =>
        _written.TryGetValue(address, out (MeshInstance3D Node, Transform3D Drawn) w)
            ? w.Drawn.Origin - StatedOf(address, w.Drawn).Origin
            : NodeFor(address) is { } node
                ? node.Transform.Origin - StatedOf(address, node.Transform).Origin
                : Vector3.Zero;

    /// <summary>
    /// Computes the exact interpolated world anchor for a HUD marker or title over an entity.
    /// Uses the entity's live node position plus the relative guest anchor offset, anchoring
    /// smoothly at display rate (120-240 Hz) without tick desynchronization.
    /// </summary>
    public Vector3 WorldAnchorFor(uint address, Vector3 guestAnchor)
    {
        if (_written.TryGetValue(address, out (MeshInstance3D Node, Transform3D Drawn) w))
        {
            Transform3D toWorld = IsInsideTree() ? GlobalTransform : Transform;
            Transform3D worldTransform = toWorld * w.Drawn;
            if (TryGuestPosition(address, out Vector3 guestPos))
            {
                Vector3 godotOffset = GameWorld.GuestToGodot(guestAnchor - guestPos);
                Transform3D stated = StatedOf(address, w.Drawn);
                Vector3 localOffset = stated.Basis.Inverse() * godotOffset;
                return worldTransform.Origin + worldTransform.Basis * localOffset;
            }

            return worldTransform.Origin + (w.Drawn.Origin - StatedOf(address, w.Drawn).Origin);
        }

        // A node already lifted out of the tree (a level going down) has no world transform.
        if (NodeFor(address) is { } node && node.IsInsideTree())
        {
            if (TryGuestPosition(address, out Vector3 guestPos))
            {
                Vector3 godotOffset = GameWorld.GuestToGodot(guestAnchor - guestPos);
                Transform3D stated = StatedOf(address, node.Transform);
                Vector3 localOffset = stated.Basis.Inverse() * godotOffset;
                return node.GlobalTransform.Origin + node.GlobalTransform.Basis * localOffset;
            }

            return node.GlobalTransform.Origin + DrawnLead(address);
        }

        return GameWorld.GuestToGodot(guestAnchor);
    }

    /// <summary>
    /// A helper point of the entity's drawn model, by its gmd name
    /// (<see cref="Refii.Entities.Engine_EntityAttachments.TryResolveHelper"/>): the skeleton the
    /// bone belongs to when the helper hangs on a live bone (<paramref name="skeleton"/>,
    /// <paramref name="bone"/>), else null and -1; <paramref name="mount"/> the helper relative to
    /// that bone or, with none, in the node's own space; <paramref name="rest"/> its rest transform
    /// in the node's space either way. False with no drawn node, or a name no part gmd has.
    /// </summary>
    public bool TryHelper(uint address, string helper, out Skeleton3D? skeleton, out int bone,
        out Transform3D mount, out Transform3D rest)
    {
        skeleton = null;
        bone = -1;
        mount = Transform3D.Identity;
        rest = Transform3D.Identity;
        if (NodeFor(address) is not { } node)
        {
            return false;
        }

        _skeletons.TryGetValue(address, out Refii.Entities.Engine_EntitySkeleton? rig);
        if (!Refii.Entities.Engine_EntityAttachments.TryResolveHelper(helper, rig, PartPaths(node), out bone, out mount, out rest))
        {
            return false;
        }

        if (bone >= 0 && rig is not null && GodotObject.IsInstanceValid(rig.Node) && rig.Node.IsInsideTree())
        {
            skeleton = rig.Node;
        }
        else
        {
            bone = -1;
        }

        return true;
    }

    /// <summary>
    /// How tall the entity is drawn, in metres, for a mark hung over it: the drawn mesh's bounds
    /// at the node's scale (a rigged body's bind bounds). 1.8 for an entity with no drawn node.
    /// </summary>
    public float DrawnHeight(uint address)
    {
        if (NodeFor(address) is not { } node || node.Mesh is null)
        {
            return 1.8f;
        }

        Aabb box = node.GetAabb();
        float top = box.End.Y * node.Scale.Y;
        return float.IsFinite(top) ? Mathf.Clamp(top, 0.3f, 6f) : 1.8f;
    }

    private Refii.Entities.Engine_EntitySkeleton? _headPositionRig;
    private int _headPositionBone = -1;

    public bool TryHeroHeadPosition(out Vector3 position)
    {
        position = default;
        if (!_skeletons.TryGetValue(_principals.Hero, out var rig) ||
            NodeFor(_principals.Hero) is not { } node || !GodotObject.IsInstanceValid(rig.Node)) return false;
        if (_headPositionRig != rig)
        {
            _headPositionRig = rig;
            rig.TryBone(Refii.Animations.Engine_TitleRig.RigRole.Head, out _headPositionBone);
        }
        if (_headPositionBone < 0) return false;
        position = (node.GlobalTransform * rig.Node.GetBoneGlobalPose(_headPositionBone)).Origin;
        return position.IsFinite();
    }

    /// <summary>Meshes by model hash. Resolving and loading is the expensive half.</summary>
    private readonly Dictionary<uint, Mesh?> _meshes = new();

    /// <summary>Rigs by guest entity address, for the entities that turned out to be characters.</summary>
    private readonly Dictionary<uint, Refii.Entities.Engine_EntitySkeleton> _skeletons = new();

    private readonly Dictionary<uint, uint> _lastModelHash = new();
    private readonly Dictionary<uint, Refii.Physics.Engine_DisarticulatedRig> _disarticulated = new();
    private readonly Dictionary<string, (Vector3 Pos, Quaternion Rot)> _capturedBonesScratch =
        new(System.StringComparer.OrdinalIgnoreCase);
    private readonly Vector3[] _guestBonesScratch = new Vector3[HostBridge.MaxBones];
    private Transform3D[] _restGlobalsScratch = new Transform3D[HostBridge.MaxBones];
    private float _lastFrameDelta = 1f / 60f;
    private double _guestWalkElapsed;
    private bool _shuttingDown;
    private readonly Refii.Entities.Engine_CorpseManager _corpses = new();
    private readonly HashSet<uint> _sawEtherealAlive = new();
    private readonly Dictionary<uint, DeadCorpseTrack> _deadCorpseState = new();
    private readonly Dictionary<uint, PendingCrumble> _pendingCrumble = new();

    private sealed class DeadCorpseTrack
    {
        public uint RecordHash;
        public string? ModelPath;
        public Refii.Entities.Engine_CorpsePolicy.CorpseDeathMode DeathMode;
        public bool RecordClassified;
        public bool ClassifiedFromGdb;

        /// <summary>The bridge stated CECCreature_FinishDeath for it (HostBridge GuestCorpseFlag).</summary>
        public bool GuestCorpse;
        public bool SawVisibleWhileDead;
        public bool SawPosedWhileDead;
        public bool SawRagdollWhileDead;
        public bool SawDisarticulatedWhileDead;
        public bool SawCollapsedPoseWhileDead;
        public bool FadePinned;
        public bool HasPinnedPos;
        public Vector3 PinnedPos;
        public HostBridge.EntityAppearance LastDeadLook = HostBridge.EntityAppearance.Constructed;
        public float DeathAge;
    }

    private sealed class PendingCrumble
    {
        public ulong UniqueId;
        public uint SourceModelHash;
        public uint TargetModelHash;
        public Transform3D EntityWorld;
        public Vector3 InheritedVelocity;
        public readonly Dictionary<string, (Vector3 Pos, Quaternion Rot)> Bones =
            new(System.StringComparer.OrdinalIgnoreCase);
    }

    private readonly HashSet<uint> _seen = new();

    private int _ticks;

    private int _reports;

    /// <summary>The set of unplaceable placement classes as last reported.</summary>
    private string _unknownSignature = string.Empty;

    /// <summary>
    /// Meshes are built OFF THIS THREAD, and land in the caches above a frame or two later.
    /// </summary>
    /// <remarks>
    /// THE ENTITY LOOP USED TO BUILD SYNCHRONOUSLY, and a cache miss is the whole asset chain:
    /// mesh decode, material create, then an ORM whose height channel is a 24-pass least-squares
    /// solve over the entire texture (<see cref="Refii.Textures.Engine_TexHeight"/>, Passes = 24).
    /// One batch of new NPCs was therefore one frame's work. Measured on the 2026-08-19 freeze:
    /// the Godot main thread sat at 98% of a core in userspace for over four minutes with RSS
    /// flat to the byte, `godot.log` silent, while the guest stayed healthy and the last frame
    /// stayed on screen. The same mechanism at a smaller batch size is the stutter.
    ///
    /// The seam is that <see cref="Refii.Models.Engine_MdlComposite.Build"/> takes nothing but a
    /// list of PATHS. Reading which parts an entity wears is a live guest read and stays here;
    /// turning those paths into a mesh is pure asset work and does not.
    ///
    /// LEGAL FOR THE SAME REASON THE REGION BUILD IS: this creates RESOURCES, never nodes, and
    /// touches no scene tree. The caches it reaches through — the bank reader, the material
    /// texture cache, the model index — were already locked when Engine_LevelScene.Build moved
    /// to a worker, and a level change already puts two builders in them at once.
    /// </remarks>
    private readonly System.Collections.Concurrent.ConcurrentQueue<System.Action> _completions =
        new();

    /// <summary>Composite keys currently being built, so a miss queues once and not per frame.</summary>
    private readonly HashSet<string> _compositeJobs = new(System.StringComparer.Ordinal);

    /// <summary>Model hashes currently being built, for the same reason.</summary>
    private readonly HashSet<uint> _meshJobs = new();

    /// <summary>
    /// Builds allowed in flight at once.
    /// </summary>
    /// <remarks>
    /// Small on purpose. These threads meet in the bank reader, whose file position and read are
    /// one locked operation, so past a handful they queue on that lock instead of working — and
    /// every one of them is competing with the guest, which is the simulation.
    /// </remarks>
    private const int MaxInFlightBuilds = 3;

    /// <summary>Under the loading screen the builds are all that is left to wait for.</summary>
    private int InFlightLimit => LoadHold ? EffectiveMaxInFlightLoadBuilds : MaxInFlightBuilds;

    /// <summary>The walk begins.</summary>
    private void BeginLoadWalk() => Refii.Entities.Engine_LoadEntityBuilds.Collecting = LoadHold;

    /// <summary>The hold ended: this load's builds, once, on one line.</summary>
    public void ReportLoadBuilds()
    {
        Refii.Entities.Engine_LoadEntityBuilds.Collecting = false;
        GD.Print($"[load] entity builds: {Refii.Entities.Engine_LoadEntityBuilds.Take()}");
    }

    private int _inFlight;
    private List<HostBridge.GuestEntity> _lastEntities = new();
    private uint _lastUnknown;
    private HostBridge.UnknownPlacement[] _lastUnknownVtables = System.Array.Empty<HostBridge.UnknownPlacement>();
    private int _postLoadScreenWalks;

    /// <summary>Tracks producers independently of this departing node and closes its completion channel on exit.</summary>
    private readonly Refii.Core.Engine_RegionWorkers _retirementWorkers = new();

    /// <summary>Releases the static owner without invalidating meshes borrowed by another consumer.</summary>
    /// <remarks>Region teardown detaches this node before queuing its deletion. Leaving the
    /// managed instance in Current would root its mesh/composite dictionaries through the next
    /// build, or indefinitely at the title screen. A departing older feed must not clear a newer
    /// one. In-flight workers may retain this instance until their asset work completes; they do
    /// not publish Current. Android also closes and drains this owner's completion channel;
    /// workers remain tracked through their final publication attempt, without disposing shared results.</remarks>
    public override void _ExitTree()
    {
        _shuttingDown = true;
        ClearCorpseState();
        GameCurrentOwner.Retire(ref Current, this);
        if (Refii.Core.Engine_Platform.IsAndroid)
            _retirementWorkers.Retire(() => { while (_completions.TryDequeue(out _)) { } });
    }

    /// <summary>The walk's refii.toml levers follow a live `cvar-set` from the first feed on.</summary>
    public override void _EnterTree()
    {
        // The bodies' and the camera's timeline rule is read with the walk's levers.
        Refii.Core.Engine_TickTimeline.Configure(Refii.Core.Engine_RefiiConfig.Read(out _));
        if (!_walkConfigHooked)
        {
            _walkConfigHooked = true;
            Refii.Core.Engine_RefiiConfig.OnConfigChanged += (name, _) => ReloadWalkConfig(name);
        }
    }

    private static bool _walkConfigHooked;

    /// <summary>Re-reads one of the walk's own refii.toml levers after a live change to it.</summary>
    private static void ReloadWalkConfig(string name)
    {
        switch (name)
        {
            case "entity_skip_index":
                SkipIndexEnabled = Refii.Core.Engine_RefiiConfig.Bool(
                    Refii.Core.Engine_RefiiConfig.Read(out _), name, false);
                break;
            case Refii.Core.Engine_TickTimeline.ArrivalFitKey:
                Refii.Core.Engine_TickTimeline.Configure(Refii.Core.Engine_RefiiConfig.Read(out _));
                break;
            case "entity_still_reconcile_scale":
                StillReconcileScale = ReadStillReconcileScale();
                break;
            case "far_rig_pose_hz":
                FarRigPoseHz = Refii.Entities.Engine_FarRigPoseRate.Clamp(Refii.Core.Engine_RefiiConfig.Float(
                    Refii.Core.Engine_RefiiConfig.Read(out _), name, 0f));
                break;
            case "entity_create_budget_ms":
                EntityCreateBudgetMs = ReadEntityCreateBudgetMs();
                break;
            case "far_rig_pose_spread":
                FarRigPoseSpread = Refii.Core.Engine_RefiiConfig.Bool(
                    Refii.Core.Engine_RefiiConfig.Read(out _), name, true);
                break;
        }
    }

    /// <summary>Feeds entities and publishes successful clip work summed across this frame.</summary>
    public override void _Process(double delta)
	{
		_lastFrameDelta = (float)delta;
		_guestWalkElapsed += delta;
		using var clipFrame = Refii.Animations.Engine_ClipAnimator.FrameWork.BeginFrame();
		// One read of the engine's frame counter for the walk: every rig stamps and compares it.
		using var processFrame = Refii.Core.Engine_ProcessFrame.Begin();
		using (Refii.Core.Engine_FramePhases.Measure("entities_process"))
		{
			RefreshCollectedKeys(delta);
			entities_processBody(delta);
			if (_shuttingDown || (HostBridge.IsInGame && !HostBridge.WorldIsActive()))
			{
				ClearCorpseState();
			}
			else if (_corpses.Count > 0)
			{
				_corpses.Step((float)delta, CurrentEyePosition(), SampleActiveGround);
			}
		}
	}

	/// <summary>What the last entity walk's loop cost, in milliseconds.</summary>
	private double LastLoopMs;

	/// <summary>Feeds since the last cost report; the report is every 120th.</summary>
	private int _costReports;

	/// <summary>The walks whose sections are timed: one in this many; divides the report's 120.</summary>
	private const int SectionWalks = 8;

	private bool _timeSections;

	/// <summary>The clock of the walk's per-entity sections: 0 on a walk that does not time them.</summary>
	private long SectionClock() => _timeSections ? System.Diagnostics.Stopwatch.GetTimestamp() : 0L;

	// THE ROWS PAST THE SKIP INDEX, BY KIND (debug timers). On a timed walk each such row is timed
	// from its prologue to the next row's start and summed into its kind: a character's rig, a
	// rigged prop (a door, a sign: a skeleton and no character rig), any other entity whose
	// statement changed, and the unchanged rest (a still entity on its reconcile walk, or one the
	// index does not hold). The skip-index rows and the off-screen deferrals are not timed: they
	// land in "ent.rest". Recorded as "ent.rows.*"; their counts go to the metrics' entityWalk.
	private const int RowCharacter = 0;
	private const int RowRigProp = 1;
	private const int RowChanged = 2;
	private const int RowStill = 3;
	private static readonly string[] RowPhaseNames = { "ent.rows.character", "ent.rows.rigprop", "ent.rows.changed", "ent.rows.still" };
	private readonly long[] _rowTicks = new long[4];
	private readonly int[] _rowCounts = new int[4];
	private int _rowOpen = -1;
	private long _rowOpenFrom;

	/// <summary>The last timed walk's rows past the skip index, by kind (character, rigged prop, changed, still).</summary>
	public static readonly int[] TimedRowCounts = new int[4];

	/// <summary>The last walk's entities, rows posed, rigs written and rows past the prologue.</summary>
	public static int LastWalkEntities, LastWalkPosed, LastWalkPosedWritten, LastWalkPastPrologue;

	/// <summary>Movers, rigs, nodes and attached children held after the last walk.</summary>
	public static int LastWalkMovers, LastWalkRigs, LastWalkNodes, LastWalkAttached;

	private void BeginRowTiming()
	{
		System.Array.Clear(_rowTicks);
		System.Array.Clear(_rowCounts);
		System.Array.Clear(_stillRows);
		_rowOpen = -1;
	}

	/// <summary>Starts timing a row past the skip index, on a timed walk only.</summary>
	private void OpenRow(in HostBridge.GuestEntity entity, long from)
	{
		if (!_timeSections)
		{
			return;
		}

		int kind;
		if (_skeletons.ContainsKey(entity.Address))
		{
			kind = _clipAnimators.TryGetValue(entity.Address, out Refii.Animations.Engine_ClipAnimator? animator) &&
				!animator.IsCharacterRig ? RowRigProp : RowCharacter;
		}
		else
		{
			kind = entity.Changed ? RowChanged : RowStill;
			if (kind == RowStill)
			{
				int still = Due(entity.Address, _stillTicksThisWalk) ? 0
					: !_nodes.ContainsKey(entity.Address) ? 1
					: entity.ModelHash == 0u && _reports < 10 ? 2
					: 3;
				++_stillRows[still];
				OpenDueRow(still == 0, from);
			}
		}

		_rowOpen = kind;
		_rowOpenFrom = from;
		++_rowCounts[kind];
	}

	/// <summary>Ends the open row's timing, if a row is open.</summary>
	private void CloseRow()
	{
		if (_rowOpen < 0)
		{
			return;
		}

		CloseDueRow();
		_rowTicks[_rowOpen] += System.Diagnostics.Stopwatch.GetTimestamp() - _rowOpenFrom;
		_rowOpen = -1;
	}

	/// <summary>A timed walk's sections as frame phases, beside "ent.pose" and "ent.place".</summary>
	private void RecordSections(long prologueTicks, long skipTicks, long clipTicks, long drawnTicks, double restMs, double toMs)
	{
		Refii.Core.Engine_FramePhases.Record("ent.prologue", prologueTicks * toMs, plot: false);
		Refii.Core.Engine_FramePhases.Record("ent.skip", skipTicks * toMs, plot: false);
		Refii.Core.Engine_FramePhases.Record("ent.clip", clipTicks * toMs, plot: false);
		Refii.Core.Engine_FramePhases.Record("ent.drawn", drawnTicks * toMs, plot: false);
		Refii.Core.Engine_FramePhases.Record("ent.rest", System.Math.Max(0.0, restMs), plot: false);
		for (int kind = 0; kind < RowPhaseNames.Length; ++kind)
		{
			Refii.Core.Engine_FramePhases.Record(RowPhaseNames[kind], _rowTicks[kind] * toMs, plot: false);
			TimedRowCounts[kind] = _rowCounts[kind];
		}

		System.Array.Copy(_stillRows, TimedStillRows, _stillRows.Length);
		RecordDuePhases(toMs);
	}

	/// <summary>The last walk's counts, for the metrics' entityWalk.</summary>
	private void PublishRowCounts(int entities, int posed, int posedWritten, int pastPrologue)
	{
		LastWalkEntities = entities;
		LastWalkPosed = posed;
		LastWalkPosedWritten = posedWritten;
		LastWalkPastPrologue = pastPrologue;
		LastWalkMovers = _movers.Count;
		LastWalkRigs = _skeletons.Count;
		LastWalkNodes = _nodes.Count;
		LastWalkAttached = _attached.Count;
		OffscreenDeferred = _offscreenDeferredThisWalk;
		LastWalkRigPropsHeld = _rigPropsHeld;
		LastWalkBuildingsHeld = _buildingsHeld;
		LastWalkReparked = _reparked;
		PublishRigPropMiss();
	}

	/// <summary>
	/// The last walk's rigged props answered from the skip index (their pose gate asked there), its
	/// buildings answered there while the reports count them, and the verdicts parked at the end of
	/// the row that took the full path rather than on the walk after it.
	/// </summary>
	public static int LastWalkRigPropsHeld, LastWalkBuildingsHeld, LastWalkReparked;

	private int _rigPropsHeld, _buildingsHeld, _reparked;

	/// <summary>
	/// The last timed walk's still rows (<see cref="TimedRowCounts"/>[3]) by why they were past the
	/// index: due on the still re-check (the reconcile cadence times `entity_still_reconcile_scale`),
	/// no node (nothing drawn yet, or a model that has none), a multi-mesh appearance on its full
	/// prologue while the reports count it, and the rest (re-parks, movers, attached, faded).
	/// </summary>
	public static readonly int[] TimedStillRows = new int[4];

	private readonly int[] _stillRows = new int[4];

	/// <summary>This walk's still re-check cadence, for the still-row census.</summary>
	private int _stillTicksThisWalk = ReconcileTicks;

	private void entities_processBody(double delta)
	{
        Current = this;
        long lap = System.Diagnostics.Stopwatch.GetTimestamp();
        if (HostBridge.TryGetAbiVersion() is null || !Visible)
        {
            return;
        }

        // BEFORE the early-out, because the clocks beat whether or not this frame had anything
        // to draw, and a sampler that only runs on populated frames measures the population.
        SampleSimClock();

        // BEFORE the early-out on an empty entity list, and before anything that can return.
        // A finished build releases its in-flight slot here; draining only on populated frames
        // would leave the slots held through a level change and stall every later build.
        // Draining is a dictionary store apiece, so it needs no budget — the cost this used to
        // have is now on the worker.
        while (_completions.TryDequeue(out System.Action? apply))
        {
            apply();
        }

        Refii.Core.Engine_FramePhases.Lap("ent.pre", ref lap);

        // ONCE PER GUEST TICK. The guest steps at 30 Hz or less and the display can draw
        // faster; between two ticks every placement and pose reads identically, so the walk
        // and the loop are skipped and the scene keeps what the last tick placed.
        // Once per guest frame (30 Hz, Engine_GuestFrame), not per 15 Hz game tick: the guest
        // writes two poses per tick. Between guest frames its memory reads identically, so the
        // walk and the loop are skipped and the pairs are redrawn.
        uint feedTick = Refii.Core.Engine_TickClock.GuestGameTick;
        bool guestFrameChanged = Refii.Core.Engine_GuestFrame.Changed;
        bool loadHoldPump = LoadHold && !ReadyForRelease;
        if (!guestFrameChanged && !FeedEveryFrame && !loadHoldPump)
        {
            ++FeedFramesSkipped;

            // THE TIMELINE STILL HAS TO BE SAMPLED. Skipping the walk between ticks is right —
            // the guest states nothing new — but the SMOOTHING is what turns those ticks into
            // motion, and it lives inside the walk. The camera samples its own timeline every
            // frame (GameWorld), so with this loop skipped the camera glided at display rate
            // while every entity stepped at tick rate: the hero trailed the camera and snapped
            // back when he stopped.
            //
            // Only the movers. A rock's timeline holds one sample and answers the same
            // transform for ever, so re-applying it for 2900 static props would cost the walk
            // we just skipped, to no effect.
            ApplyDrawn();
            return;
        }

        float walkDelta = (float)System.Math.Min(_guestWalkElapsed, 0.25);
        _guestWalkElapsed = 0.0;

        // The guest frame that completes a game tick: the sub-step on which the guest poses its
        // far rigs (FarRigPosesThisStep).
        _tickCompletedThisWalk = feedTick != _feedTickApplied;
        _feedTickApplied = feedTick;

        // The far rigs' real-time rate (`far_rig_pose_hz`), measured between tick steps: the only
        // walks a far rig writes on either way.
        _farRigHz = FarRigPoseHz;
        if (_tickCompletedThisWalk)
        {
            _farRigFromSeconds = _farRigToSeconds;
            _farRigToSeconds = Time.GetTicksUsec() * 1e-6;
        }

        // The walk kicked at frame start (GameWindow) is taken here; without one — the lever
        // off, or no kick this frame — it is walked synchronously. On non-guest frames pumped
        // under LoadHold, reuse the last entity list once a post-activation walk has landed so
        // completed worker meshes can be turned into nodes and reconciled every frame without
        // re-walking guest memory.
        List<HostBridge.GuestEntity> entities;
        uint unknown;
        HostBridge.UnknownPlacement[] unknownVtables;
        bool worldActiveNow = !HostBridge.IsInGame || HostBridge.WorldIsActive();
        if (!guestFrameChanged && _lastEntities.Count > 0 && (!LoadHold || _walkedWhileWorldActive))
        {
            entities = _lastEntities;
            unknown = _lastUnknown;
            unknownVtables = _lastUnknownVtables;
        }
        else if (HostEntityWalk.TryTake(out HostEntityWalk.Walk walk))
        {
            entities = walk.Entities;
            unknown = walk.Unknown;
            unknownVtables = walk.UnknownVtables;
            _lastEntities = entities;
            _lastUnknown = unknown;
            _lastUnknownVtables = unknownVtables;
            if (worldActiveNow)
            {
                _walkedWhileWorldActive = true;
            }
        }
        else
        {
            entities = HostBridge.GetEntities(out unknown, out unknownVtables);
            _lastEntities = entities;
            _lastUnknown = unknown;
            _lastUnknownVtables = unknownVtables;
            if (worldActiveNow)
            {
                _walkedWhileWorldActive = true;
            }
        }

        if (guestFrameChanged && entities.Count > 0)
        {
            ++_postLoadScreenWalks;
        }

        Refii.Core.Engine_FramePhases.Lap("ent.walk", ref lap);

        // "ent.setup": from here to the loop's first row, on its own clock so "ent.loop" keeps
        // measuring what it always has.
        long setupFrom = lap;

        // Entities a mod's FX recipe dresses (the Blades' swords): placed from this walk's
        // records, dropped when they leave it.
        Refii.Fx.Engine_FxRecipes.ApplyEntities(entities, e => ToGodot(e.Position, e.Rotation), (Node3D?)GameFx.Current ?? this);

        if (entities.Count == 0)
        {
            return;
        }

        // WHERE THE LOOP'S TIME GOES, accumulated per section rather than lapped per entity: a
        // lap inside a 300-entity loop records 300 values a frame and says nothing. These are
        // summed across the walk and recorded once, so they read as "pose cost this frame".
        // One walk in SectionWalks, and every walk under a profiler: the loop's own clocks were
        // over a thousand reads a walk, to measure it. A report walk is always one of those.
        _timeSections = Refii.Core.Engine_FramePhases.Plot is not null ||
            (_costReports + 1) % SectionWalks == 0;
        long poseTicks = 0;
        long placeTicks = 0;
        long clipTicks = 0;
        int posedWritten = 0;
        int posedHalfRate = 0;
        int farHeld = 0;
        long prologueTicks = 0;
        long skipTicks = 0;
        int pastPrologue = 0;
        Refii.Entities.Engine_EntitySkeleton.ReadMs = 0.0;
        Refii.Entities.Engine_EntitySkeleton.WriteMs = 0.0;
        Refii.Entities.Engine_EntitySkeleton.WritesSkipped = 0;
        _reblending.Clear();
        _animating.Clear();
        int posed = 0;

        // ONE CAMERA READ FOR THE WHOLE WALK. `GetCamera3D` is a viewport query; asking it per
        // entity would be 2900 crossings to answer the same question. Every view is read: on
        // split screen a rig in player two's view is in view.
        bool haveEye = ReadPoseViews() > 0;
        ulong nowMs = Time.GetTicksMsec();

        _seen.Clear();
        _drawnRecords.Clear();
        _drawnPlaces.Clear();
        _drawnKeys.Clear();

        // Cleared at the START of the walk: `PlaceAttached` iterates this list on the frames
        // between walks too, so attachments keep up with bones that move every frame.
        _attached.Clear();
        Refii.Entities.Engine_EntityHighlight.BeginWalk();
        Refii.Entities.Engine_EntityAppearance.BeginWalk();

        // The half-res surface the highlight silhouettes are drawn into. Idempotent; builds
        // nothing until the post chain is up, since the layer reaches the screen only through it.
        Refii.Entities.Engine_EntityHighlightLayer.Ensure(this);

        // PER FRAME, because that is what the report claims to describe. A counter that
        // accumulates for the life of the run answers a different question than its name asks,
        // and printed beside a live entity count it reads as a per-frame ratio that nothing
        // divides into.
        _withModel = 0;
        _noMesh = 0;
        _prologueSkipped = 0;
        BeginLoadWalk();
        _awaitingBuild = 0;
        _awaitingStub = 0;
        _awaitingCreate = 0;

        // Reset for the same reason, and beside the two above so the whole report is on one
        // clock — a mix of run totals and per-frame counts in one line multiplies the totals
        // by however many frames have passed.
        _composites = 0;
        _multiMesh = 0;
        _multiMeshParts = 0;
        _builtParts = 0;
        _compositePartsUnresolved = 0;
        Refii.Entities.Engine_EntitySkeleton.BeginFrame();

        _batches?.Refresh();
        if (_batches is not null && !Refii.Entities.Engine_EntityBatches.Enabled && Refii.Entities.Engine_EntityBatches.Batched != 0)
        {
            UnbatchAll();
        }

        // THE SKIP INDEX (`entity_skip_index`): the last walk's skip verdicts answer this one's
        // unchanged entities without the lookups their paths are made of. Started afresh when the
        // lever or a global the paths read moves; a parent bone taken since the last walk undoes
        // its child's verdict, the one input that changes outside the child's own path every walk.
        bool skipIndex = SkipIndexEnabled;
        int skipEpoch = !skipIndex ? 0 : 1 |
            (Refii.Entities.Engine_EntityBatches.Enabled ? 2 : 0) |
            (Refii.Lighting.Engine_LightingFrame.ModernModel ? 4 : 0);
        if (skipEpoch != _skipEpoch)
        {
            _skipEpoch = skipEpoch;
            _skipIndex.Clear();
        }

        if (skipIndex && _attachments.Version != _skipAttachmentsVersion)
        {
            _skipAttachmentsVersion = _attachments.Version;
            foreach (uint child in _attachments.Children)
            {
                _skipIndex.Invalidate(child);
            }
        }

        _skipIndex.BeginWalk();

        // An indexed verdict's re-check, on the reconcile cadence or a multiple of it
        // (`entity_still_reconcile_scale`). A multiple: every walk due on it is due on the cadence
        // too, so the walk that re-checks takes the full path exactly as a reconcile would.
        int stillReconcileTicks = ReconcileTicks * StillReconcileScale;

        Transform3D toWorld = GlobalTransform;
        uint heroAddress = HostBridge.HeroEntityAddress();
        Engine_TitleGame title = Engine_TitleGame.Active;
        if (_morphPreviewGeneration != title.HeroMorphPreviewGeneration)
        {
            _morphPreviewGeneration = title.HeroMorphPreviewGeneration;
            _morphPreviewUntilTick = _ticks + ReconcileTicks * 2;
        }
        (uint playerOne, uint playerTwo) = HostBridge.CoopPlayerEntities();
        _morphPreviewHero = !title.HeroMorphPreviewHeld && _ticks >= _morphPreviewUntilTick ? 0u
            : title.HeroMorphPreviewPlayer switch { 1 => playerOne, 2 => playerTwo, _ => heroAddress };
        uint dogAddress = Engine_TitleGame.Active.CompanionEntityAddress;
        _principals = (heroAddress, playerOne, playerTwo, dogAddress);
        bool heroZoneDue = heroAddress != _lastHeroZoneAddress ||
            _heroZone == 0u ||
            (_ticks % ReconcileTicks == 0) ||
            (HeroPosition is { } hp && (!_lastHeroZonePos.IsFinite() || hp.DistanceSquaredTo(_lastHeroZonePos) > 0.0625f));
        if (heroAddress == 0u)
        {
            _heroZone = Refii.Models.Engine_ClassicZones.Outdoors;
            _lastHeroZoneAddress = 0u;
        }
        else if (heroZoneDue)
        {
            uint queriedZone = HostBridge.GetEntityLightZone(heroAddress);
            if (queriedZone != 0u)
            {
                _heroZone = queriedZone;
                Refii.Lighting.Engine_LightZones.HeroZone = queriedZone;
            }
            _lastHeroZoneAddress = heroAddress;
            if (HeroPosition is { } curHp)
            {
                _lastHeroZonePos = curHp;
            }
        }
        uint heroZone = _heroZone != 0u ? _heroZone : Refii.Models.Engine_ClassicZones.Outdoors;
        _secondViewZone = Refii.Core.Engine_Views.Split && playerTwo != 0u
            ? HostBridge.GetEntityLightZone(playerTwo)
            : heroZone;
        SecondViewZone = _secondViewZone;
        if (playerTwo == 0u)
        {
            PlayerTwoPosition = null;
        }
        _createdThisFrame = 0;
        _createTicksThisFrame = 0;
        _dynamicCasterPositions.Clear();
        _dynamicCasterAddresses.Clear();
        _createFrom = System.Diagnostics.Stopwatch.GetTimestamp();

        // The rows by reference: a row is over a hundred bytes, and most of them end at the skip
        // index having read four of its fields. The list is this walk's until the next walk's take.
        System.ReadOnlySpan<HostBridge.GuestEntity> rows = System.Runtime.InteropServices.CollectionsMarshal.AsSpan(entities);
        bool restated = title.RestatesEntities;
        HostBridge.GuestEntity restatedEntity = default;
        BeginRowTiming();
        _offscreenDeferredThisWalk = 0;
        _rigPropsHeld = 0;
        _buildingsHeld = 0;
        _reparked = 0;
        BeginRigPropHolds(haveEye, toWorld, nowMs);
        _stillTicksThisWalk = stillReconcileTicks;
        int offscreenWalks = OffscreenRigWalks;
        Refii.Core.Engine_FramePhases.Lap("ent.setup", ref setupFrom);
        for (int row = 0; row < rows.Length; ++row)
        {
            CloseRow();
            ref readonly HostBridge.GuestEntity entity = ref restatedEntity;
            if (restated)
            {
                restatedEntity = title.Appearance(rows[row]);
            }
            else
            {
                entity = ref rows[row];
            }

            if (title.ObserveEntity(entity)) continue;
            if (entity.Address == heroAddress && heroAddress != 0u && IsUsablePlacement(entity.Position))
            {
                // THE HERO'S STATED POSITION, in Godot space, for whatever samples the world at
                // the hero the way the guest does (the painted day-set grid).
                HeroPosition = ToGodot(entity.Position, entity.Rotation).Origin;
                HeroGuestPosition = entity.Position;
                HeroGuestRotation = entity.Rotation;
                title.ObserveHero(entity, Refii.Core.Engine_GuestFrame.Frame);
            }
            else if (entity.Address == playerTwo && playerTwo != 0u && IsUsablePlacement(entity.Position))
            {
                PlayerTwoPosition = ToGodot(entity.Position, entity.Rotation).Origin;
            }

            // A RECYCLED ADDRESS IS A NEW ENTITY. The guest frees a level's entities and makes the
            // next level's at the same addresses; an entity is its address AND its unique id
            // (entity+0x14). The node, its twin marriage, its rig and every record keyed by the
            // address belong to the entity that held it, so they go before this row decides
            // anything, and the row builds the new entity from nothing. A unique id that moved is
            // always a stated change (HostStatedWords compares it), so only those rows ask.
            if (entity.Changed && _nodeUid.TryGetValue(entity.Address, out ulong drawnId) &&
                drawnId != entity.UniqueId)
            {
                if (_recycledReports < 20)
                {
                    ++_recycledReports;
                    GD.Print($"[game-entities] 0x{entity.Address:X8} recycled: unique id " +
                        $"{drawnId:X16} -> {entity.UniqueId:X16} (record {entity.RecordHash:X8}) — " +
                        "its node is retired and the new entity built");
                }

                ++RecycledAddresses;
                RetireAddress(entity.Address);
            }

            ObserveCorpseLiveness(in entity, walkDelta);

            // ANSWERED BY THE LAST WALK'S VERDICT. Unchanged, off its reconcile cadence and no
            // principal is everything the three skip paths below ask of the statement (the empty
            // path's longer cadence divides by this one); what else they read is the verdict's to
            // keep (Engine_EntitySkipIndex). Its counts are the path's; what it owns and draws is
            // asked of the index by Retire and the reconcile. Untimed: it lands in "rest".
            // Each kind on the cadence its own path next does something on (Engine_EntitySkipIndex.
            // RecheckTicks): the empty path's longer one, a rigged prop's settled-prop check, the
            // still paths' reconcile. A held rigged prop's pose gate is still asked here, as its
            // row would ask it; a held building replays what its full prologue repeats.
            if (skipIndex && !entity.Changed && entity.Address != _morphPreviewHero &&
                !IsPrincipal(entity.Address) && !entity.Appearance.IsDeadCreature &&
                !_disarticulated.ContainsKey(entity.Address) &&
                !_pendingCrumble.ContainsKey(entity.Address) &&
                _skipIndex.TryFind(entity.Address, entity.UniqueId, row,
                    out Refii.Entities.Engine_EntitySkipIndex.Kind verdict, out int verdictTag) &&
                HeldThisWalk(entity.Address, verdict, verdictTag))
            {
                _skipIndex.Take();
                if (verdict == Refii.Entities.Engine_EntitySkipIndex.Kind.RigProp)
                {
                    CountHeldRigProp(entity.Address, verdictTag, haveEye, toWorld, nowMs, ref posed, ref farHeld);
                    continue;
                }

                ++_prologueSkipped;
                if (verdict != Refii.Entities.Engine_EntitySkipIndex.Kind.Nothing)
                {
                    ++_withModel;
                }

                if (verdict == Refii.Entities.Engine_EntitySkipIndex.Kind.Still)
                {
                    ++_unchangedSkipped;
                    if (verdictTag != 0)
                    {
                        ReplayHeldBuilding(entity, verdictTag);
                    }
                }

                continue;
            }

            // Android's off-screen rigs (`android_offscreen_rig_walks`, GameEntities.Android.cs):
            // visited one walk in N, and taken as changed on the walk that visits one again.
            if (offscreenWalks > 1 && TryDeferOffscreen(entity, offscreenWalks, toWorld, nowMs, haveEye)) continue;
            if (_offscreenDeferred.Count != 0 && ResumesOffscreen(entity)) { restatedEntity = entity with { Changed = true }; entity = ref restatedEntity; }

            long prologueFrom = SectionClock();
            OpenRow(entity, prologueFrom);

            // NOTHING TO DRAW, ALREADY KNOWN. Most entities are triggers, markers and
            // sound emitters: no model hash, no composite parts, no mesh records. The two part
            // resolvers below only re-read the guest on the reconcile cadence and answer from a
            // cache between, so for such an entity the prologue was three dictionary lookups per
            // guest frame to reach the same `continue`. The
            // verdict is keyed on the entity's unique id (an address recycles) and lasts until the
            // same cadence re-asks the guest, when it is confirmed or dropped.
            if (entity.ModelHash == 0 &&
                _nothingToDraw.TryGetValue(entity.Address, out ulong knownId) &&
                knownId == entity.UniqueId &&
                !((entity.Changed || IsPrincipal(entity.Address)) && Due(entity.Address, ReconcileTicks)) &&
                !Due(entity.Address, ReconcileTicks * 8))
            {
                if (entity.Changed && IsUsablePlacement(entity.Position))
                {
                    _guestPlacements[entity.Address] = (entity.Position, entity.Rotation);
                }
                if (skipIndex)
                {
                    _skipIndex.Park(entity.Address, entity.UniqueId,
                        Refii.Entities.Engine_EntitySkipIndex.Kind.Nothing, 0u, null);
                }
                ++_prologueSkipped;
                skipTicks += SectionClock() - prologueFrom;
                continue;
            }

            // STILL AND ADOPTED. Many entities are static props adopted into the level's
            // batches: a hidden node here, a slot in a batch there. Unchanged (the walk compares
            // every stated word, placement and look alike) and not due for a reconcile, the whole
            // prologue below is lookups that end at the still-entity `continue`; the twin slot
            // keeps the transform it has. The node stays owned (`_seen`) so Retire keeps it.
            // The same for a still prop already promoted into an entity batch: hidden node, slot
            // in a batch, nothing to re-apply while unchanged. A change of placement or look
            // (both in `Changed`) takes the full path, which unbatches it.
            // A batched prop is still DRAWN by us: its record and place go into the drawn sets
            // exactly as on the full path, or the reconcile reads the record as undrawn and
            // stands the region's copy back up inside it.
            bool adoptedStill = _adoptions.ContainsKey(entity.Address);
            if (!_justDetached.Contains(entity.Address) &&
                !entity.Changed && !Due(entity.Address, ReconcileTicks) &&
                (adoptedStill || (_batches?.Contains(entity.Address) ?? false)))
            {
                string? stillKey = null;
                if (!adoptedStill)
                {
                    if (entity.RecordHash != 0)
                    {
                        _drawnRecords.Add(entity.RecordHash);
                    }
                    stillKey = ModelKeyFor(entity.ModelHash);
                    if (stillKey is not null)
                    {
                        _drawnKeys.Add((entity.Address, stillKey));
                    }
                }

                if (skipIndex)
                {
                    _skipIndex.Park(entity.Address, entity.UniqueId,
                        adoptedStill ? Refii.Entities.Engine_EntitySkipIndex.Kind.Adopted : Refii.Entities.Engine_EntitySkipIndex.Kind.Batched,
                        adoptedStill ? 0u : entity.RecordHash, stillKey);
                }

                _seen.Add(entity.Address);
                ++_withModel;
                ++_prologueSkipped;
                skipTicks += SectionClock() - prologueFrom;
                continue;
            }

            // Past the skips that need nothing of the node: the still-node skip below writes its
            // own verdict, and anything else has none.
            if (skipIndex)
            {
                _skipIndex.Unpark(entity.Address);
            }

            Mesh mesh = null!;
            MeshInstance3D node = null!;
            bool adopted = adoptedStill;
            bool created = false;
            // The row took the fast path below (a rigged prop off it parks only what its next fast row
            // would); the full prologue resolved a multi-mesh appearance.
            bool fastRow = false;
            bool multiRoute = false;
            bool holdingForPendingCrumbleBuild = false;
            if (entity.Address != _morphPreviewHero && !entity.Changed && !Due(entity.Address, ReconcileTicks) &&
                entity.Appearance.IsConstructed && !entity.Highlighted &&
                !_disarticulated.ContainsKey(entity.Address) &&
                !_pendingCrumble.ContainsKey(entity.Address) &&
                (_reports >= 10 || entity.ModelHash != 0u) &&
                _nodes.TryGetValue(entity.Address, out MeshInstance3D? fastNode) &&
                _nodeMesh.TryGetValue(entity.Address, out Mesh? fastMesh) && fastMesh is not null &&
                _lastLook.TryGetValue(entity.Address, out HostBridge.EntityAppearance prevLook) &&
                prevLook.IsConstructed)
            {
                node = fastNode;
                mesh = fastMesh;
                fastRow = true;

                // Unchanged, this is what the walk that last changed it stored; stored here only when
                // that walk's placement was not usable.
                if (!_guestPlacements.ContainsKey(entity.Address) && IsUsablePlacement(entity.Position))
                {
                    _guestPlacements[entity.Address] = (entity.Position, entity.Rotation);
                }
                string? fastKey = null;
                if (!adopted)
                {
                    if (entity.RecordHash != 0)
                    {
                        _drawnRecords.Add(entity.RecordHash);
                    }
                    fastKey = ModelKeyFor(entity.ModelHash);
                    if (fastKey is not null)
                    {
                        _drawnKeys.Add((entity.Address, fastKey));
                    }
                }

                _seen.Add(entity.Address);
                ++_withModel;
                if (!_justDetached.Contains(entity.Address) &&
                    !_skeletons.ContainsKey(entity.Address) &&
                    !_movers.Contains(entity.Address) &&
                    _attachments.ParentOf(entity.Address) == 0u)
                {
                    ++_unchangedSkipped;
                    ++_prologueSkipped;
                    // A verdict only while NoteStill has nothing to count: a prop walking toward its
                    // batch is counted every walk, and is indexed once it is batched.
                    if (NoteStill(entity, node, mesh, adopted) && skipIndex)
                    {
                        _skipIndex.Park(entity.Address, entity.UniqueId, Refii.Entities.Engine_EntitySkipIndex.Kind.Still,
                            adopted ? 0u : entity.RecordHash, fastKey);
                    }
                    skipTicks += SectionClock() - prologueFrom;
                    continue;
                }

                skipTicks += SectionClock() - prologueFrom;
            }
            else
            {
            Mesh? resolvedMesh;
            if (entity.RecordHash != 0)
            {
                Refii.Models.Engine_ClassicZones.NoteEntity(entity.RecordHash, entity.Address);
            }
            if (IsUsablePlacement(entity.Position))
            {
                _guestPlacements[entity.Address] = (entity.Position, entity.Rotation);
            }

            uint prevModelHashForMesh = entity.ModelHash;
            bool holdingForReassembly = false;
            if (_lastModelHash.TryGetValue(entity.Address, out uint knownModelHash))
            {
                prevModelHashForMesh = knownModelHash;
                if (knownModelHash != entity.ModelHash)
                {
                    _compositeParts.Remove(entity.Address);
                    _eyes.Remove(entity.Address);
                    _bodyAndEyes.Remove(entity.Address);
                    _skipIndex.Invalidate(entity.Address);
                    if (entity.ModelHash != 0u &&
                        !_disarticulated.ContainsKey(entity.Address) &&
                        _skeletons.TryGetValue(entity.Address, out Refii.Entities.Engine_EntitySkeleton? liveRig) &&
                        _nodes.TryGetValue(entity.Address, out MeshInstance3D? liveNode))
                    {
                        Transform3D entityWorld = toWorld * ToGodot(entity.Position, entity.Rotation);
                        Transform3D captureWorld = liveNode.IsInsideTree() ? liveNode.GlobalTransform : entityWorld;
                        TryCapturePendingCrumble(
                            entity.Address,
                            entity.UniqueId,
                            knownModelHash,
                            entity.ModelHash,
                            captureWorld,
                            liveRig,
                            ComputeInheritedVelocity(in entity));
                    }
                    else if (_disarticulated.TryGetValue(entity.Address, out Refii.Physics.Engine_DisarticulatedRig? activeShatter))
                    {
                        // Living creature recovering from crumble (e.g. Hollowman SetCrumbled(0) flipping
                        // ModelHash back from Skeleton_Base.mdl to 0): fly all scattered bones back to the
                        // entity's spawn/rise pose before swapping back to the living composite mesh.
                        _pendingCrumble.Remove(entity.Address);
                        if (!entity.Appearance.IsDeadCreature && !activeShatter.ReassemblyFinished)
                        {
                            activeShatter.BeginReassembly();
                        }
                        else
                        {
                            _disarticulated.Remove(entity.Address);
                            if (_skeletons.TryGetValue(entity.Address, out Refii.Entities.Engine_EntitySkeleton? activeRig))
                            {
                                activeRig.IsDisarticulated = false;
                            }
                        }
                    }
                    else if (entity.ModelHash == 0u)
                    {
                        _pendingCrumble.Remove(entity.Address);
                    }
                }
            }
            _lastModelHash[entity.Address] = entity.ModelHash;

            if (_disarticulated.TryGetValue(entity.Address, out Refii.Physics.Engine_DisarticulatedRig? existingShatter))
            {
                if (entity.Appearance.IsDeadCreature && existingShatter.IsReassembling)
                {
                    _disarticulated.Remove(entity.Address);
                    if (_skeletons.TryGetValue(entity.Address, out Refii.Entities.Engine_EntitySkeleton? deadRig))
                    {
                        deadRig.IsDisarticulated = false;
                    }
                }
                else if (existingShatter.IsReassembling && !existingShatter.ReassemblyFinished)
                {
                    holdingForReassembly = true;
                }
                else if (existingShatter.ReassemblyFinished)
                {
                    // Reassembly flight finished: release disarticulated override so the walk swaps
                    // straight onto the already-warmed living mesh in this tick.
                    _disarticulated.Remove(entity.Address);
                    if (_skeletons.TryGetValue(entity.Address, out Refii.Entities.Engine_EntitySkeleton? doneRig))
                    {
                        doneRig.IsDisarticulated = false;
                    }
                }
            }

            if (entity.ModelHash == 0)
            {
                // A COMPOSITE CHARACTER, or nothing at all.
                //
                // Most entities with no model hash are triggers, markers and sound emitters and
                // were never going to draw. But 29 of BWSSlums's 138 characters are people: a
                // live 160-bone skeleton and `comp6+0x20 == 0`, which the guest's own mesh block
                // branches straight past. Their geometry is layered on component 17 instead —
                // a head, a torso, legs, eyes and accessories — so it is assembled here rather
                // than resolved from one hash.
                // TWO ROUTES, NOT ONE. Component 17 layers a character; component 5
                // (`CECGraphicAppearanceStaticMultipleMesh`) holds a VECTOR of mesh records and
                // is what buildings and multi-part scenery use. Neither states a hash in the
                // single-slot appearance components the entity walk reads, so both arrive here
                // as `ModelHash == 0`. Missing the second is why a region's build-time copy
                // looked load-bearing: its standing records were named `… interior` / `…
                // exterior`, which are two records of ONE MeshRecords list.
                if (ResolveComposite(entity.Address, out bool hasCompositeParts) is { } composite)
                {
                    resolvedMesh = composite;
                    ++_withModel;
                    ++_composites;
                    _nothingToDraw.Remove(entity.Address);
                }
                else if (ResolveMultiMesh(entity.Address, out bool hasMultiParts) is { } multiple)
                {
                    resolvedMesh = multiple;
                    multiRoute = true;
                    ++_withModel;
                    ++_multiMesh;
                    _nothingToDraw.Remove(entity.Address);
                }
                else if (holdingForReassembly && _nodeMesh.TryGetValue(entity.Address, out Mesh? reassemblyHeldMesh) && reassemblyHeldMesh is not null)
                {
                    // Composite is warming up on a worker while bones are still flying back together:
                    // keep the bone-pile mesh alive so Retire() never frees the entity mid-reassembly.
                    resolvedMesh = reassemblyHeldMesh;
                    ++_withModel;
                    _nothingToDraw.Remove(entity.Address);
                }
                else
                {
                    if (!hasCompositeParts && !hasMultiParts)
                    {
                        _nothingToDraw[entity.Address] = entity.UniqueId;

                        // What the empty path parks on the next walk, parked now: its next walk
                        // reads this same verdict (the empty path re-asks only on RecheckTicks).
                        if (skipIndex)
                        {
                            _skipIndex.Park(entity.Address, entity.UniqueId,
                                Refii.Entities.Engine_EntitySkipIndex.Kind.Nothing, 0u, null);
                            ++_reparked;
                        }
                    }
                    else
                    {
                        _nothingToDraw.Remove(entity.Address);
                    }
                    prologueTicks += SectionClock() - prologueFrom;
                    continue;
                }
            }
            else if (EyesOf(entity.Address, entity.UniqueId) is { Length: > 0 } eyes)
            {
                // EYES ARE COMPONENT 7'S OWN, NOT THE MORPH LIST'S. `CreatureHeroChild` states
                // its whole body as one ModelFile on component 6 and its eyes on
                // GraphicAppearanceEyesComponent, and the guest hangs them on that same rig's
                // Shadow_R_Eye/Shadow_L_Eye (`sub_82209480`) — so a single-model character with
                // eyes is assembled like a composite: the body first, then the two eyes, which
                // the build anchors to the eye bones. Cached by the part list like every
                // composite. This is why the child hero had no eyes: nothing on the single-model
                // route ever asked for them.
                ++_withModel;
                resolvedMesh = ComposeParts(BodyAndEyes(entity.Address, entity.ModelHash, eyes));
                if (resolvedMesh is null)
                {
                    if (holdingForReassembly && _nodeMesh.TryGetValue(entity.Address, out Mesh? heldEyeMesh) && heldEyeMesh is not null)
                    {
                        resolvedMesh = heldEyeMesh;
                    }
                    else
                    {
                        prologueTicks += SectionClock() - prologueFrom;
                        continue;
                    }
                }

                ++_composites;
            }
            else
            {
                ++_withModel;

                // A miss returns null and queues; the entity draws once the build lands. Counted
                // apart from _noMesh, which is the asset-fault signal ("a hash that resolves to a
                // PATH is not a model that loads") and must not absorb entities that are merely
                // early.
                if (!_meshes.TryGetValue(entity.ModelHash, out Mesh? builtMesh))
                {
                    // A model that has come back as a stub StubGiveUp times is a body no mounted
                    // bank has yet: it stops counting as pending (the release gate would wait on
                    // it forever) and is re-asked every StubRetryTicks in case a bank paged it in.
                    if (_stubReturns.TryGetValue(entity.ModelHash, out int stubs) && stubs >= StubGiveUp)
                    {
                        ++_awaitingStub;
                        if (_ticks % StubRetryTicks == 0)
                        {
                            Resolve(entity.ModelHash);
                        }

                        prologueTicks += SectionClock() - prologueFrom;
                        continue;
                    }

                    ++_awaitingBuild;
                    Resolve(entity.ModelHash);
                    if (_pendingCrumble.TryGetValue(entity.Address, out PendingCrumble? waitingCrumble) &&
                        waitingCrumble.TargetModelHash == entity.ModelHash &&
                        _nodeMesh.TryGetValue(entity.Address, out Mesh? heldPreCrumbleMesh) &&
                        heldPreCrumbleMesh is not null)
                    {
                        // Hold the pre-crumble node & frozen pose while Skeleton_Base.mdl decodes on the
                        // worker so Retire() does not destroy the entity or wipe _pendingCrumble.
                        resolvedMesh = heldPreCrumbleMesh;
                        holdingForPendingCrumbleBuild = true;
                    }
                    else if (holdingForReassembly && _nodeMesh.TryGetValue(entity.Address, out Mesh? heldSingleMesh) && heldSingleMesh is not null)
                    {
                        resolvedMesh = heldSingleMesh;
                    }
                    else
                    {
                        prologueTicks += SectionClock() - prologueFrom;
                        continue;
                    }
                }
                else
                {
                    // What Resolve answers for a hash it holds, without its second lookup.
                    resolvedMesh = builtMesh;
                }
            }

            // While bones are flying back together during reassembly, keep drawing the bone-pile
            // mesh until ReassemblyFinished while the living target mesh warms up in the background.
            if (holdingForReassembly && _nodeMesh.TryGetValue(entity.Address, out Mesh? activeBoneMesh) && activeBoneMesh is not null)
            {
                resolvedMesh = activeBoneMesh;
            }

            if (resolvedMesh is null)
            {
                // THE COUNT THAT MATTERS. A model hash that resolves to a PATH is not a model
                // that loads: creature meshes are header-only stubs in the resident banks and
                // page in from streaming ones. Reading path resolution as "the guest can draw
                // this" is what emptied the street of everything but chickens.
                ++_noMesh;
                prologueTicks += SectionClock() - prologueFrom;
                continue;
            }
            mesh = resolvedMesh;
            DueLap(DueResolve);

            _seen.Add(entity.Address);
            created = false;
            bool hasExistingNode = _nodes.TryGetValue(entity.Address, out MeshInstance3D? existingNode);
            if (!hasExistingNode)
            {
                // Node creation is budgeted per frame by time, bounded by a minimum count (so a
                // region cannot stall on one slow creation) and a maximum; the rest wait a frame.
                // Under the load hold the larger load budget applies: nothing is on screen, and
                // a deferred node would appear after the player has control.
                //
                // Defer BEFORE Wait/Adopt/_drawnRecords so static build-time twins stay visible
                // until the exact frame the live entity node is actually created.
                int maxCreations = LoadHold ? MaxCreationsPerLoadFrame : MaxCreationsPerFrame;
                double createMs = LoadHold ? CreateMillisecondsPerLoadFrame : CreateMillisecondsPerFrame;
                long spentTicks = LoadHold
                    ? _createTicksThisFrame
                    : System.Diagnostics.Stopwatch.GetTimestamp() - _createFrom;
                double budgetMs = EntityCreateBudgetMs;
                bool defer = !LoadHold && budgetMs > 0.0
                    ? Refii.Entities.Engine_EntityCreateBudget.Defer(_createdThisFrame, maxCreations,
                        _createTicksThisFrame * 1000.0 / System.Diagnostics.Stopwatch.Frequency, budgetMs)
                    : _createdThisFrame >= MinCreationsPerFrame &&
                        (_createdThisFrame >= maxCreations ||
                            spentTicks * 1000.0 / System.Diagnostics.Stopwatch.Frequency >= createMs);
                if (defer)
                {
                    ++_creationsDeferred;
                    ++_awaitingCreate;
                    prologueTicks += SectionClock() - prologueFrom;
                    continue;
                }
            }

            adopted = _adoptions.ContainsKey(entity.Address);
            // UNRIGGED MODELS ONLY, for now. A rigged twin would have to follow its dominant
            // bone's pose relative to its rest, and the first cut of that displaced the
            // market's double doors; until that delta is verified against a door offline, a
            // rigged entity keeps the hide-and-draw path, which is right but for the seam.
            if (!adopted && !hasExistingNode && Twins is { } adoptable &&
                Refii.Models.Engine_MdlRig.TryFrom(mesh) is null &&
                !MeshHasCloth(mesh) &&
                (EntityLayers(entity) & (Refii.Lighting.Engine_LightZones.IndoorMask | Refii.Lighting.Engine_LightZones.IndoorLampHostMask)) == 0u &&
                ModelKeyFor(entity.ModelHash) is { } adoptKey && IsUsablePlacement(entity.Position) &&
                adoptable.TryAdopt(entity.RecordHash, adoptKey,
                    ToGlobal(ToGodot(entity.Position, entity.Rotation).Origin))
                    is { } adoption)
            {
                _adoptions[entity.Address] = (adoption, entity.UniqueId);
                adopted = true;
            }

            if (!adopted)
            {
                if (entity.RecordHash != 0)
                {
                    _drawnRecords.Add(entity.RecordHash);
                }
                if (ModelKeyFor(entity.ModelHash) is { } modelKey)
                {
                    _drawnKeys.Add((entity.Address, modelKey));
                }
                else if (entity.ModelHash == 0u && _meshParts.TryGetValue(entity.Address, out uint[]? partHashes))
                {
                    for (int p = 0; p < partHashes.Length; p++)
                    {
                        if (ModelKeyFor(partHashes[p]) is { } partKey)
                        {
                            _drawnKeys.Add((entity.Address, partKey));
                        }
                    }
                }
            }

            if (!hasExistingNode)
            {
                ++_createdThisFrame;
                created = true;
                long createFrom = System.Diagnostics.Stopwatch.GetTimestamp();
                if (entity.Address == heroAddress && heroAddress != 0u)
                {
                    GD.Print($"[game-entities] hero {heroAddress:X8} node created on tick {_ticks}" +
                        (LoadHold ? " (under the load hold)" : " (AFTER the load hold)"));
                }
                _nodeMesh[entity.Address] = mesh;
                uint layers = EntityLayers(entity);
                _entityLayers[entity.Address] = layers;
                node = new MeshInstance3D
                {
                    Name = $"Entity_{entity.Address:X8}",
                    Mesh = mesh,
                    // Layer 1 draws it and the second bit is what a fixture's shadow map takes; a
                    // lamp host takes neither, as its static twin would not.
                    Layers = layers,
                    // It moves: it takes the GI from the probes and is never voxelised into it.
                    GIMode = GeometryInstance3D.GIModeEnum.Dynamic,
                };
                node.SetMeta(MetaEntityAddress, entity.Address);
                node.SetMeta(MetaEntityUniqueId, entity.UniqueId);
                _nodeUid[entity.Address] = entity.UniqueId;
                node.SetMeta(MetaEntityRecord, entity.RecordHash);
                node.SetMeta(MetaEntityModel, entity.ModelHash);

                // HERE, on the frame the live copy is created, and not merely when it is
                // reported — hiding on the report would take the region's copy away for an
                // entity whose mesh never loaded, turning a duplicate into a hole. The periodic
                // reconcile below enforces the same invariant for everything else.
                if (adopted)
                {
                    // The region's copy is the drawable; this node only carries the rig.
                    node.Visible = false;
                }
                else
                {
                    Twins?.Claim(entity.RecordHash);
                    _dirtyPlaceHides = true;
                }
                if (IsUsablePlacement(entity.Position) && _attachments.ParentOf(entity.Address) == 0u)
                {
                    Transform3D initialTransform = Refii.Entities.Engine_EntityAppearance.Scaled(
                        ToGodot(entity.Position, entity.Rotation), entity.Appearance);
                    node.Transform = initialTransform;
                    _written[entity.Address] = (node, initialTransform);
                }
                AddChild(node);
                _nodes[entity.Address] = node;
                if (entity.ModelHash == 0u)
                {
                    // A multi-mesh appearance: a building whose interior records are the rain's
                    // and snow's roofs.
                    Refii.Level.Engine_ClassicRoofs.Track(entity.Address, node);
                }

                // Light it. These entities are created at runtime and hang BESIDE the region
                // rather than inside it, so neither the level scene's baked pass nor the prop
                // streamer ever touches them — without this they run on the shader's bare
                // defaults, which is no bake and no ambient, and every character in the world
                // renders as a solid black silhouette in a correctly lit street.
                //
                // They carry no probe metadata: the guest places them, so there was no build
                // step to sample the field at. The theme's flat ambient is what they get.
                LightEntity(node);

                bool ranged = StateDrawRange(entity.Address, node);
                // The proxy is synced once, at the end: a sync here would build it before the
                // range, cloth and hidden clusters below change its state, and build it again there.
                if (!TryRig(entity.Address, node, mesh, syncProxy: false))
                {
                    // No skeleton = dressing: it gets the region's prop policies — its stated
                    // range, else a screen-size cull — and a shadow reach keyed on its size.
                    _unrigged.Add(entity.Address);
                    if (Refii.Models.Engine_MdlRig.TryFrom(mesh) is not null)
                    {
                        _awaitingRig.Add(entity.Address);
                    }
                    Refii.AndroidRuntime.AndroidEntityProps.ApplyVisibilityRange(node, mesh);
                    Refii.Lighting.Engine_ShadowReach.Adopt(node);
                }
                else
                {
                    if (ranged)
                    {
                        Refii.Level.Engine_PropDrawRange.Apply(node);
                    }
                    TryStartPendingCrumble(entity.Address, node, mesh, toWorld, entity.ModelHash, entity.UniqueId);
                }

                // The cloth body goes on BEFORE the cluster pass: it hides the surfaces it
                // draws through the same pass, which then presents the body once.
                if (Refii.Physics.Engine_ClothBody.Attach(node, mesh, PartPathsOf(mesh)) is { } cloth)
                {
                    LightEntity(cloth);
                }

                if (!adopted)
                {
                    Refii.Entities.Engine_HiddenClusters.Apply(node, entity.Address);
                }
                AttachDisplacementParts(entity.Address, node, mesh);
                if (Refii.Models.Engine_ClassicMaterial.Active)
                {
                    Refii.Models.Engine_ClassicDisplacement.AdoptRecord(entity.RecordHash, node, mesh);
                }
                else
                {
                    Refii.Fx.Engine_DisplacementMeshes.Adopt(entity.RecordHash, node, mesh);
                }
                Refii.Entities.Engine_ShadowProxy.Sync(node);
                SyncInteriorMesh(entity, node, adopted);
                long createTicks = System.Diagnostics.Stopwatch.GetTimestamp() - createFrom;
                _createTicksThisFrame += createTicks;
                Refii.Core.Engine_LoadTimeline.Add(Refii.Core.Engine_LoadTimeline.Category.EntityCreate,
                    createTicks);
                Refii.Core.Engine_SpikeNotes.Since(Refii.Core.Engine_SpikeNotes.Kind.EntityCreate, createFrom);
            }
            else
            {
                node = existingNode!;
                DueLap(DueDrawn);
                SyncInteriorMesh(entity, node, adopted);
                DueLap(DueSync);
                if (!_nodeMesh.TryGetValue(entity.Address, out Mesh? drawnMesh) || !ReferenceEquals(drawnMesh, mesh))
                {
                    // An entity can change model — a door opening, a prop breaking, a body that
                    // dresses after it was rigged. Compared against what this feed assigned, not the
                    // node's property: that read is a crossing per entity per frame.
                    _dirtyPlaceHides = true;
                    _extentHeldStreak.Remove(entity.Address);
                    Unbatch(entity.Address, node);
                    node.Mesh = mesh;
                    node.CustomAabb = new Aabb();
                    _nodeMesh[entity.Address] = mesh;

                    // The rig is the mesh's: a composite's bone table is the union of its parts, and
                    // the skeleton and skin are built from it, so a new mesh needs a new rig (and must
                    // be bound before cloth attaches) or its vertices/cloth address the wrong bones.
                    Refii.Models.Engine_MdlRig? nextMdlRig = Refii.Models.Engine_MdlRig.TryFrom(mesh);
                    bool sameBinding = Refii.Models.Engine_MdlRig.TryFrom(drawnMesh) is { } previousRig &&
                        nextMdlRig is { } nextRig && previousRig.HasSameBinding(nextRig);
                    if (!sameBinding && _skeletons.TryGetValue(entity.Address, out Refii.Entities.Engine_EntitySkeleton? stale))
                    {
                        Refii.Models.Engine_MdlDisarticulation.DisarticulatedMeshProfile nextProfile =
                            Refii.Models.Engine_MdlDisarticulation.GetProfile(mesh);
                        uint sourceHash = _pendingCrumble.TryGetValue(entity.Address, out PendingCrumble? captured)
                            ? captured.SourceModelHash
                            : prevModelHashForMesh;
                        bool enteringCrumble = entity.ModelHash != 0u &&
                            nextMdlRig is { } crumbleRig &&
                            Refii.Core.Engine_TitleGame.Active.Rig.IsDisarticulatedCrumble(
                                sourceHash,
                                entity.ModelHash,
                                Refii.Level.Engine_PropModelIndex.Resolve(entity.ModelHash, System.Array.Empty<string>()),
                                crumbleRig.Parents,
                                nextProfile.IsRigidPieceMesh);

                        Transform3D entityWorld = toWorld * ToGodot(entity.Position, entity.Rotation);
                        if (enteringCrumble && !_pendingCrumble.ContainsKey(entity.Address))
                        {
                            Transform3D captureWorld = node.IsInsideTree() ? node.GlobalTransform : entityWorld;
                            TryCapturePendingCrumble(
                                entity.Address,
                                entity.UniqueId,
                                prevModelHashForMesh,
                                entity.ModelHash,
                                captureWorld,
                                stale,
                                ComputeInheritedVelocity(in entity));
                        }

                        node.Skeleton = new NodePath();
                        node.Skin = null;
                        Refii.Entities.Engine_ShadowProxy.Sync(node);
                        if (GodotObject.IsInstanceValid(stale.Node))
                        {
                            stale.Node.GetParent()?.RemoveChild(stale.Node);
                            stale.Node.QueueFree();
                        }

                        _skeletons.Remove(entity.Address);
                        _clipAnimators.Remove(entity.Address);
                        _boneWriteAskedMs.Remove(entity.Address);
                        _disarticulated.Remove(entity.Address);
                        _attachments.Forget(entity.Address);
                        node.VisibilityRangeEnd = 0f;
                        if (!TryRig(entity.Address, node, mesh))
                        {
                            _unrigged.Add(entity.Address);
                            if (nextMdlRig is not null)
                            {
                                _awaitingRig.Add(entity.Address);
                            }
                            Refii.AndroidRuntime.AndroidEntityProps.ApplyVisibilityRange(node, mesh);
                            Refii.Lighting.Engine_ShadowReach.Adopt(node);
                        }
                        else
                        {
                            _awaitingRig.Remove(entity.Address);
                            Refii.Level.Engine_PropDrawRange.Apply(node);
                            TryStartPendingCrumble(entity.Address, node, mesh, toWorld, entity.ModelHash, entity.UniqueId);
                        }

                        GD.Print($"[rig] entity 0x{entity.Address:X8} changed mesh — re-rigged " +
                            $"({(_skeletons.ContainsKey(entity.Address) ? "new skeleton" : "no rig yet, retrying")})");
                    }
                    else if (_skeletons.TryGetValue(entity.Address, out Refii.Entities.Engine_EntitySkeleton? keptRig))
                    {
                        keptRig.IsRigidPieceMesh = Refii.Models.Engine_MdlDisarticulation.GetProfile(mesh).IsRigidPieceMesh;
                        if (_pendingCrumble.ContainsKey(entity.Address))
                        {
                            TryStartPendingCrumble(entity.Address, node, mesh, toWorld, entity.ModelHash, entity.UniqueId);
                        }
                    }

                    Refii.Physics.Engine_ClothBody.Attach(node, mesh, PartPathsOf(mesh));
                    LightEntity(node);
                    Refii.Entities.Engine_HiddenClusters.Apply(node, entity.Address);
                    if (_unrigged.Contains(entity.Address))
                    {
                        // The cull range and the shadow reach are the mesh's extent: a new mesh,
                        // a new range, and the reach measured again from scratch.
                        node.VisibilityRangeEnd = 0f;
                        Refii.AndroidRuntime.AndroidEntityProps.ApplyVisibilityRange(node, mesh);
                        Refii.Lighting.Engine_ShadowReach.Release(node);
                        Refii.Lighting.Engine_ShadowReach.Adopt(node);
                        if (nextMdlRig is not null)
                        {
                            _awaitingRig.Add(entity.Address);
                        }
                        else
                        {
                            _awaitingRig.Remove(entity.Address);
                        }
                    }

                    // Last: the cull range, the shadow reach and the rig above are what it copies.
                    Refii.Entities.Engine_ShadowProxy.Sync(node);
                }
                else if (!adopted && Due(entity.Address, ReconcileTicks) &&
                         !(_batches?.Contains(entity.Address) ?? false))
                {
                    // Clothes change without the model changing: the hero dresses, an NPC's outfit
                    // is re-committed. The garment set is re-read on the reconcile cadence.
                    Refii.Entities.Engine_HiddenClusters.Apply(node, entity.Address);
                    Refii.Entities.Engine_ShadowProxy.SyncHidden(node);
                }

                DueLap(DueHidden);
            }

            // THE STATED LOOK — alpha, drawable — also ABOVE the skip: an entity can fade without
            // moving (a dying hollow man). A look other than the constructor's needs the
            // node itself: a batch draws every instance alike, and a married twin may be one
            // instance of a MultiMesh. The marriage ends for good; the region's copy goes, as
            // it would for an entity destroyed, and this node draws from now on.
            HostBridge.EntityAppearance effectiveAppearance =
                ResolveEffectiveAppearance(in entity, title, out DeadCorpseTrack? corpseTrack);

            if (Refii.Entities.Engine_EntityAppearance.NeedsOwnNode(effectiveAppearance) || Engine_TitleGame.Active.WantsOwnNode(entity))
            {
                if (adopted && _adoptions.TryGetValue(entity.Address, out var marriage))
                {
                    Twins?.Release(marriage.Twin, hide: true);
                    _adoptions.Remove(entity.Address);
                    adopted = false;
                    node.Visible = true;
                }

                Unbatch(entity.Address, node);
            }

            Refii.Entities.Engine_EntityAppearance.Apply(node, effectiveAppearance, IsPrincipal(entity.Address));
            if (SkipHeroRendering && IsHero(entity.Address) && node.Visible)
            {
                node.Visible = false;
                Refii.Entities.Engine_ShadowProxy.SyncHidden(node);
            }
            if (!_heroTraceLogged && entity.Address == _principals.Hero && entity.Address != 0u && node.Mesh is { } heroMesh)
            {
                _heroTraceLogged = true;
                int surfCount = heroMesh.GetSurfaceCount();
                bool hasOverlay = node.MaterialOverlay is not null;
                GD.Print($"[dx12-trace] hero-ready addr=0x{entity.Address:X8} surfaces={surfCount} overlay={hasOverlay} visible={node.Visible}");
            }
            _lastLook[entity.Address] = effectiveAppearance;

            // A hero's head goes while its player's stated camera is first person, for a hold that
            // leaves the game's body fade out (Engine_FirstPersonHead). Created once per skeleton;
            // the player (its player-map key) is restated every pass.
            if (IsHero(entity.Address) && _skeletons.TryGetValue(entity.Address, out var heroRig))
            {
                Refii.Entities.Engine_FirstPersonHead.Ensure(heroRig.Node,
                    Refii.Entities.Engine_FirstPersonView.PlayerOf(entity.Address, _principals.One, _principals.Two));
            }

            // A mod's hidden parts (Engine_HiddenParts), on any rig, by the entity's handle: a rebuilt
            // node gets them again here. Nothing to do while nothing is hidden.
            if (Refii.Entities.Engine_HiddenParts.Count > 0 && _skeletons.TryGetValue(entity.Address, out var partsRig))
            {
                Refii.Entities.Engine_HiddenPartModifier.EnsureFor(partsRig.Node, entity.Address);
            }

            // The stated targeting highlight goes ABOVE the unchanged-entity skip: the guest
            // ramps its colour over several ticks on a target that may not move. Cheap when
            // unchanged: the word is stored per mesh and compared first.
            Refii.Entities.Engine_EntityHighlight.Apply(
                node, entity.Highlighted, entity.HighlightColour, entity.Highlight, entity.Address);

            DueLap(DueLook);

            // Everything above runs for every entity, every walk, and is timed as one span
            // (`prologue` in the log line).
            // Skipped below when unchanged since the last walk and nothing would act: no rig to
            // pose, no parent bone to hang from, the node already on the same mesh, off the
            // reconcile cadence; a married twin keeps the transform it was last driven to. The
            // walk compared the stated words itself.
            prologueTicks += SectionClock() - prologueFrom;

            if (!_justDetached.Contains(entity.Address) &&
                !entity.Changed && !created && !Due(entity.Address, ReconcileTicks) &&
                entity.Appearance.IsConstructed &&
                !_skeletons.ContainsKey(entity.Address) &&
                !_movers.Contains(entity.Address) &&
                _attachments.ParentOf(entity.Address) == 0u)
            {
                ++_unchangedSkipped;
                if (NoteStill(entity, node!, mesh!, adopted) && skipIndex && multiRoute && !adopted && HoldBuildings)
                {
                    ParkHeldBuilding(entity);
                }
                continue;
            }
            }

            // The guest has stated something new, or the prop has become more than a prop: it
            // is a node again before anything below places or poses it.
            // Asked once for the rest of this entity's pass: nothing below changes its rig or its
            // parent, and the mover set only in `Drawn`, after its one reader here.
            bool hasSkeleton = _skeletons.TryGetValue(entity.Address, out Refii.Entities.Engine_EntitySkeleton? rig);
            bool isMover = _movers.Contains(entity.Address);
            uint parentAddress = _attachments.ParentOf(entity.Address);
            if (entity.Changed || created || entity.Highlighted ||
                !entity.Appearance.IsConstructed ||
                hasSkeleton || isMover ||
                parentAddress != 0u ||
                _justDetached.Contains(entity.Address))
            {
                Unbatch(entity.Address, node!);
            }

            ++pastPrologue;

            // A NON-FINITE ORIGIN TAKES GODOT'S RENDERER DOWN, not just this entity.
            //
            // The render list is sorted by depth with std::sort, whose final pass in libstdc++
            // is __unguarded_linear_insert — no bounds check, relying on the minimum already
            // sitting in the first partition. EVERY comparison against NaN is false, so one NaN
            // depth destroys that invariant: the insert walks off the FRONT of the array,
            // dereferences whatever lies below it and faults. The fault re-executes the same
            // instruction forever, so the process pins a core, stops drawing, ignores SIGTERM
            // and has to be SIGKILLed.
            //
            // Caught live under gdb (2026-08-19): 437 surfaces, key bytes all 128 so the
            // tie-break float decided every comparison, slots 0..30 correctly ordered
            // 346.3 -> 300.0, and slot 33 carrying depth=nan.
            //
            // The ROTATION above was hardened against exactly this class and the position was
            // left open. The guest publishes both by writing in place with no publish step, so
            // a read landing mid-update is the same fault in a different field.
            //
            // The bound is not decoration. A depth is a squared distance, and 1e20 squared is
            // +inf, which subtracts to NaN a step later with every component still "finite".
            //
            // KEEPS THE LAST GOOD TRANSFORM rather than zeroing: a torn read is one frame's
            // noise and the next read is usually clean, where snapping to the origin would be
            // a visible teleport.
            long placeFrom = SectionClock();
            bool moved = false;
            bool wasJustDetached = false;
            bool? rowDynamic = null;
            if (IsUsablePlacement(entity.Position))
            {
                // An attached child is placed by PlaceAttached under its parent's bone, which
                // also drops its pair; it is not blended here.
                bool attachedChild = parentAddress != 0u;

                // Static objects and stationary entities whose placement and appearance have not changed
                // and whose timeline is settled already have their node transform set.
                bool justDetached = _justDetached.Remove(entity.Address);
                wasJustDetached = justDetached;
                moved = entity.Changed || created || justDetached || isMover || !_written.ContainsKey(entity.Address);
                Transform3D stated = default;
                if (moved || attachedChild)
                {
                    stated = ToGodot(entity.Position, entity.Rotation);
                }

                if (!attachedChild && moved)
                {
                    if (created && _written.ContainsKey(entity.Address))
                    {
                        _ = Drawn(entity, stated);
                        Refii.Entities.Engine_ShadowSkeletons.Placed(node!);
                    }
                    else
                    {
                        DeadCorpseTrack? corpseTrack = entity.Appearance.IsDeadCreature &&
                            _deadCorpseState.TryGetValue(entity.Address, out DeadCorpseTrack? pinnedTrack) &&
                            pinnedTrack.FadePinned
                            ? pinnedTrack
                            : null;
                        HostBridge.EntityAppearance placedLook = corpseTrack is not null &&
                            _lastLook.TryGetValue(entity.Address, out HostBridge.EntityAppearance cachedLook)
                            ? cachedLook
                            : entity.Appearance;
                        Transform3D placed = Refii.Entities.Engine_EntityAppearance.Scaled(Drawn(entity, stated), placedLook);
                        if (corpseTrack is not null)
                        {
                            if (!corpseTrack.HasPinnedPos)
                            {
                                corpseTrack.PinnedPos = placed.Origin;
                                corpseTrack.HasPinnedPos = true;
                            }
                            else
                            {
                                float dx = placed.Origin.X - corpseTrack.PinnedPos.X;
                                float dz = placed.Origin.Z - corpseTrack.PinnedPos.Z;
                                if (((dx * dx) + (dz * dz)) < 0.0625f && placed.Origin.Y < corpseTrack.PinnedPos.Y - 0.01f)
                                {
                                    placed.Origin = new Vector3(placed.Origin.X, corpseTrack.PinnedPos.Y, placed.Origin.Z);
                                }
                            }
                        }

                        Place(entity.Address, node!, placed);
                    }
                }

                bool dueReconcile = Due(entity.Address, ReconcileTicks);
                bool zoneChanged = false;
                if (entity.Changed || created || IsPrincipal(entity.Address) ||
                    (attachedChild && IsPrincipal(_attachments.ParentOf(entity.Address))) || dueReconcile)
                {
                    uint newZone = RefreshEntityLightZone(entity.Address, out uint prevZone);
                    zoneChanged = newZone != prevZone;
                    if (adopted && _adoptions.TryGetValue(entity.Address, out var marriage) &&
                        (EntityLayers(entity) & (Refii.Lighting.Engine_LightZones.IndoorMask | Refii.Lighting.Engine_LightZones.IndoorLampHostMask)) != 0u)
                    {
                        Twins?.Release(marriage.Twin, hide: true);
                        _adoptions.Remove(entity.Address);
                        adopted = false;
                        node.Visible = true;
                        if (entity.RecordHash != 0)
                        {
                            _drawnRecords.Add(entity.RecordHash);
                            Twins?.Claim(entity.RecordHash);
                        }
                    }
                }

                DueLap(DueZone);

                // LIT FROM THE LEVEL'S PROBE FIELD where it stands, as the guest lights every
                // dynamic object; re-sampled on the reconcile cadence (it moves) and at once
                // when it has no sample yet.
                // And only when the answer can have changed: the sample depends on position and
                // theme, so it is redone when the entity has moved half a metre or the region's
                // lighting generation has changed. Each sample allocates Godot wrappers.
                if (!adopted && (zoneChanged || dueReconcile || _relit.Add(entity.Address)))
                {
                    // A region's lights can spawn after the entity that hosts one, or its building
                    // zone can resolve after the entity was promoted to a batch.
                    // Its dynamic-caster standing is asked once for the row: nothing below changes
                    // what it reads (the movers were stated above), and the pose section asks it too.
                    rowDynamic = IsDynamicShadowEntity(entity.Address);
                    uint layers = EntityLayers(entity, rowDynamic.Value);
                    if (!_entityLayers.TryGetValue(entity.Address, out uint curLayers) || curLayers != layers)
                    {
                        _entityLayers[entity.Address] = layers;
                        // A batch is keyed by its layers; the prop rejoins one under the new ones.
                        Unbatch(entity.Address, node);
                        node.Layers = layers;
                        Refii.Entities.Engine_ShadowProxy.Sync(node);
                    }

                    if (!(_batches?.Contains(entity.Address) ?? false))
                    {
                        Vector3 at = Vector3.Zero;
                        if (_written.TryGetValue(entity.Address, out (MeshInstance3D Node, Transform3D Drawn) written))
                        {
                            at = toWorld * written.Drawn.Origin;
                        }
                        else if (node.IsInsideTree())
                        {
                            at = toWorld * node.Transform.Origin;
                        }
                        long generation = Refii.Lighting.Engine_LevelBakedLighting.LightingGeneration;
                        if (!_lastLit.TryGetValue(entity.Address, out (Vector3 At, long Gen) last) ||
                            last.Gen != generation ||
                            last.At.DistanceSquaredTo(at) > 0.25f)
                        {
                            _lastLit[entity.Address] = (at, generation);
                            RelightEntity(node);
                        }
                    }
                }

                DueLap(DueRelight);

                // AN ATTACHED CHILD IS NOT PLACED BY ITS OWN TRANSFORM. The guest wrote that
                // transform from ITS pose, which is not the pose being drawn; the child belongs
                // on the host's bone. Deferred so the parent has already been placed and posed.
                if (attachedChild)
                {
                    _attached.Add((entity.Address, node, stated));
                }
            }
            else
            {
                ++NonFinitePlacements;
            }

            // The rig, after the placement: the pose is relative to the entity, so the
            // skeleton rides whatever transform the placement just set.
            long poseFrom = SectionClock();
            placeTicks += poseFrom - placeFrom;
            bool writeBones = false;
            if (hasSkeleton && rig is not null)
            {
                // HOST ANIMATION AND THE POSE FEED ARE MUTUALLY EXCLUSIVE. Both write every
                // bone of the same skeleton, so running them together is not a blend — it is
                // whichever wrote last this frame, per bone, which looks like a rig tearing
                // rather than like a configuration mistake. Following the guest's bones is
                // still the default; host animation is opted into per run.
                if (HostAnimation)
                {
                    writeBones = true;
                    DriveLocomotion(entity.Address, node, rig);
                }
                else
                {
                    // GODOT PLAYS THE CLIPS the guest states for this rig; the pose feed is
                    // the fallback only for a frame whose clips cannot be played here.
                    //
                    // EVERY RIG, EVERY FRAME. A frustum gate used to skip rigs the camera
                    // could not see, and the pose it saved was the pose those rigs kept: a
                    // villager culled from the moment it spawned was never posed at all, so
                    // it slid down the street in its bind pose. Driving the timeline is 3.4 ms
                    // of a 50 ms frame for a full street, and a character that animates only
                    // once the camera has already framed it is the defect the gate created.
                    ++posed;

                    // POSE WHAT CAN BE SEEN; ADVANCE EVERYTHING. Beyond the radius the clips
                    // still run — the rig keeps its place in the animation — but the per-bone
                    // sampling and the two engine writes per bone are skipped. A rig that walks
                    // back into range poses correctly on its first written frame, which is what
                    // the old frustum gate could not do because it skipped the advance too.
                    // IN THE TREE FIRST. `GlobalTransform` on a node that is not yet parented
                    // logs an error and answers IDENTITY, which reads as the world origin and
                    // therefore as "far away" — so a character would stop being posed for
                    // exactly the frames it is being built. Unmeasurable means write it.
                    // The guest's own gate (sub_8220D1B8 / sub_822142E8): a rig is posed when
                    // it is in the view frustum, and for 30 render frames after it leaves;
                    // otherwise its pose is HELD and its clips keep running. The first write is
                    // unconditional: a held pose needs a pose to hold.
                    bool wasWritten = _written.TryGetValue(entity.Address, out (MeshInstance3D Node, Transform3D Drawn) written);
                    bool measurable = haveEye && (wasWritten || node.IsInsideTree());
                    Transform3D rigWorld = Transform3D.Identity;
                    if (measurable)
                    {
                        rigWorld = toWorld * (wasWritten ? written.Drawn : node.Transform);
                    }
                    Vector3 rigAt = measurable ? rigWorld.Origin : Vector3.Zero;
                    float eyeDistanceSq = measurable ? NearestEyeDistanceSq(rigAt) : 0f;

                    // Distance is a RATE, never a cutoff. `sub_822142E8` gates on the view cone
                    // and on `MaxDrawDistance`, whose default is `flt_82096028` = 10000.0 times a
                    // class multiplier that is 1.0 for all four classes (flt_83319464/68/6C/70),
                    // so the guest does not distance-cull animation. It has a 2:1 divider at
                    // 0x8220D67C instead: inside BoneLODDistance a rig poses on both HF sub-steps,
                    // outside it only on the sub-step that completes a tick (30 Hz / 15 Hz).
                    // A principal off screen still throws a shadow onto it.
                    // Past its stated reach the rig is not drawn and casts nothing, so its pose is
                    // held like one off screen; the clips run on and the next write is correct.
                    // A principal found after its node was stamped loses the stamp here.
                    bool principal = IsPrincipal(entity.Address) || IsPrincipal(parentAddress);
                    if (principal && _drawRanges.Remove(entity.Address))
                    {
                        node.RemoveMeta(Refii.Level.Engine_PropDrawRange.Meta);
                        node.VisibilityRangeEnd = 0f;
                        node.VisibilityRangeEndMargin = 0f;
                        if (Refii.Entities.Engine_ShadowProxy.ProxyOf(node) is { } caster)
                        {
                            caster.VisibilityRangeEnd = 0f;
                            caster.VisibilityRangeEndMargin = 0f;
                        }
                    }

                    bool inView = principal || PosedThisTick(entity.Address, measurable, rigWorld, nowMs);

                    if ((inView || principal) && IsDynamicShadowEntity(entity.Address))
                    {
                        _dynamicCasterPositions.Add(rigAt);
                        _dynamicCasterAddresses.Add(entity.Address);
                    }

                    // Principals and actively moving full-rate rigs keep their shadow skeleton
                    // locked to their visible skeleton every frame so the shadow proxy never lags
                    // into the visible body and self-shadows it; stationary rigs follow on the
                    // guest's 15 Hz HalfRate cadence.
                    bool fullRate = !measurable || principal ||
                        eyeDistanceSq <= PoseFullRateRadius * PoseFullRateRadius;
                    bool disarticulated = _disarticulated.ContainsKey(entity.Address);
                    Refii.Entities.Engine_ShadowSkeletons.SetEveryFrame(
                        rig.Node, principal || disarticulated || (_movers.Contains(entity.Address) && fullRate));
                    bool neverPosed = !_boneWriteAskedMs.TryGetValue(entity.Address, out ulong askedMs);
                    bool settledStaticProp = !principal && !disarticulated && !entity.Changed && !created && !Due(entity.Address, 6) &&
                        !_movers.Contains(entity.Address) &&
                        !_animating.Contains(entity.Address) &&
                        !_reblending.Contains(entity.Address) &&
                        _clipAnimators.TryGetValue(entity.Address, out Refii.Animations.Engine_ClipAnimator? existingAnim) &&
                        !existingAnim.IsCharacterRig && existingAnim.IsSettled;
                    writeBones = !holdingForPendingCrumbleBuild && (disarticulated || (!settledStaticProp && ((inView && (fullRate || FarRigPosesThisStep(entity.Address, nowMs - askedMs))) || neverPosed)));
                    if (inView && !writeBones)
                    {
                        ++farHeld;
                    }

                    if (writeBones)
                    {
                        _boneWriteAskedMs[entity.Address] = nowMs;
                        ++posedWritten;
                        if (!fullRate && !neverPosed)
                        {
                            ++posedHalfRate;
                        }
                    }

                    if (writeBones)
                    {
                        DeadCorpseTrack? deadPoseTrack = entity.Appearance.IsDeadCreature &&
                            _deadCorpseState.TryGetValue(entity.Address, out DeadCorpseTrack? foundDead)
                            ? foundDead
                            : null;
                        if (deadPoseTrack is not null)
                        {
                            deadPoseTrack.SawPosedWhileDead = true;
                        }

                        if (disarticulated &&
                            _disarticulated.TryGetValue(entity.Address, out Refii.Physics.Engine_DisarticulatedRig? shatter))
                        {
                            if (deadPoseTrack is not null)
                            {
                                deadPoseTrack.SawDisarticulatedWhileDead = true;
                            }
                            Transform3D entityWorld = node.IsInsideTree()
                                ? node.GlobalTransform
                                : toWorld * ToGodot(entity.Position, entity.Rotation);
                            int boneCap = System.Math.Min(rig.BoneCount, _guestBonesScratch.Length);
                            bool hasGuest = rig.TrySampleGuestWorldBones(
                                entity.Address,
                                entityWorld,
                                new System.Span<Vector3>(_guestBonesScratch, 0, boneCap),
                                out int validCount);
                            if (shatter.LastSteppedFrame != Refii.Core.Engine_ProcessFrame.Current)
                            {
                                shatter.Step(
                                    _lastFrameDelta,
                                    entityWorld,
                                    SampleActiveGround,
                                    hasGuest ? new System.ReadOnlySpan<Vector3>(_guestBonesScratch, 0, boneCap) : default,
                                    validCount);
                                shatter.LastSteppedFrame = Refii.Core.Engine_ProcessFrame.Current;
                            }

                            rig.ApplyModelSpacePoses(shatter.ModelPoses);
                            Refii.Entities.Engine_CorpseManager.SyncDisarticulatedBounds(node, shatter, entityWorld);
                        }
                        else
                        {
                            long clipFrom = SectionClock();
                            Refii.Animations.Engine_ClipAnimator animator = ClipAnimatorFor(entity.Address, rig);
                            if (!animator.Begin(entity.Address, writeBones: true, out bool fold))
                            {
                                // Self-start fallback: if a rigid-piece bone-pile mesh (e.g. Skeleton_Base.mdl)
                                // spawned already crumbled or entered Havok ragdoll / death without a prior
                                // PendingCrumble capture, hand it directly to Engine_DisarticulatedRig.
                                if (rig.IsRigidPieceMesh &&
                                    (entity.Appearance.IsDeadCreature || animator.LastFallback == "ragdoll") &&
                                    TryAutoStartRigidPieceCrumble(entity, node, mesh, rig, toWorld, out Refii.Physics.Engine_DisarticulatedRig? autoShatter))
                                {
                                    animator.ForgetWrittenPose();
                                    Transform3D entityWorld = node.IsInsideTree()
                                        ? node.GlobalTransform
                                        : toWorld * ToGodot(entity.Position, entity.Rotation);
                                    autoShatter.Step(_lastFrameDelta, entityWorld, SampleActiveGround, default, 0);
                                    autoShatter.LastSteppedFrame = Refii.Core.Engine_ProcessFrame.Current;
                                    rig.ApplyModelSpacePoses(autoShatter.ModelPoses);
                                    Refii.Entities.Engine_CorpseManager.SyncDisarticulatedBounds(node, autoShatter, entityWorld);
                                }
                                else
                                {
                                    if (deadPoseTrack is not null && animator.LastFallback == "ragdoll")
                                    {
                                        deadPoseTrack.SawRagdollWhileDead = true;
                                    }

                                    // The feed is about to write this skeleton, so the animator's
                                    // written-pose cache no longer describes it.
                                    animator.ForgetWrittenPose();
                                    rig.Follow(entity.Address, true);
                                    if (!rig.IsSettled)
                                    {
                                        _reblending.Add(entity.Address);
                                        if (fullRate)
                                        {
                                            Refii.Entities.Engine_ShadowSkeletons.SetEveryFrame(rig.Node, true);
                                        }
                                    }
                                }
                            }
                            else
                            {
                                rig.ForgetLastPose();
                                if (!animator.IsSettled)
                                {
                                    _animating.Add(entity.Address);
                                    if (fullRate)
                                    {
                                        Refii.Entities.Engine_ShadowSkeletons.SetEveryFrame(rig.Node, true);
                                    }
                                }
                                if (fold)
                                {
                                    // Folds on the thread pool while the loop goes on; written after it, in this order.
                                    _poseBatch.Add(animator);
                                }
                            }

                            clipTicks += SectionClock() - clipFrom;
                        }
                    }
                }

                if (_reports < 10)
                {
                    SampleFacing(entity.Address, node, entity.Rotation);
                }
                if (writeBones)
                {
                    // The pose's readers run once the queued poses are written (after the loop).
                    _afterPose.Add((entity.Address, node, rig));
                }
            }
            else if (!adopted && (rowDynamic ?? IsDynamicShadowEntity(entity.Address)))
            {
                bool principal = IsPrincipal(entity.Address) || IsPrincipal(parentAddress);
                bool wasWritten = _written.TryGetValue(entity.Address, out (MeshInstance3D Node, Transform3D Drawn) written);
                bool measurable = haveEye && (wasWritten || node.IsInsideTree());
                Transform3D rigWorld = measurable
                    ? toWorld * (wasWritten ? written.Drawn : node.Transform)
                    : Transform3D.Identity;
                bool inView = principal || PosedThisTick(entity.Address, measurable, rigWorld, nowMs);
                if (inView && measurable)
                {
                    _dynamicCasterPositions.Add(rigWorld.Origin);
                    _dynamicCasterAddresses.Add(entity.Address);
                }
                Refii.Entities.Engine_ShadowSkeletons.SetFollowerEveryFrame(
                    node, principal || _movers.Contains(entity.Address));
            }

            long afterPoseEntity = SectionClock();
            poseTicks += afterPoseEntity - poseFrom;

            if ((moved || writeBones) && _adoptions.TryGetValue(entity.Address, out var married))
            {
                long twinFrom = SectionClock();
                Refii.Entities.Engine_StaticTwins.Drive(married.Twin, AdoptedWorld(entity.Address, node));
                placeTicks += SectionClock() - twinFrom;
            }

            // THE VERDICT THE NEXT WALK WOULD PARK, parked now (Engine_EntitySkipIndex): a still
            // entity on its reconcile walk takes the full path and parks again only on the walk
            // after, which repeated its skip path once per period for nothing; a settled rigged
            // prop is held until its settled-prop check. Timed with its row ("ent.rows.*").
            bool parkable = skipIndex && !entity.Changed && !created && !wasJustDetached && parentAddress == 0u &&
                entity.Address != _morphPreviewHero && !IsPrincipal(entity.Address) &&
                IsUsablePlacement(entity.Position);
            if (hasSkeleton)
            {
                if (skipIndex && rig is not null)
                {
                    ParkRigProp(entity, parkable, fastRow, writeBones);
                }
            }
            else if (parkable)
            {
                ParkAsNextWalk(entity, node!, multiRoute);
            }
        }

        CloseRow();

        // THE WALK'S POSES, TOGETHER. Each rig's fold is independent of every other's, so the
        // rigs folded on the thread pool during the loop and are written now, in walk order; then what reads a finished
        // pose — the culling box, the trace, the heading statistic — reads it, in walk order.
        long batchFrom = System.Diagnostics.Stopwatch.GetTimestamp();
        _poseBatch.Finish();
        long batchTicks = System.Diagnostics.Stopwatch.GetTimestamp() - batchFrom;
        clipTicks += batchTicks;
        long afterPoseFrom = System.Diagnostics.Stopwatch.GetTimestamp();
        foreach ((uint address, MeshInstance3D posedNode, Refii.Entities.Engine_EntitySkeleton posedRig) in _afterPose)
        {
            StateVisibleExtent(address, posedNode, posedRig);

            if (_deadCorpseState.Count > 0 &&
                _deadCorpseState.TryGetValue(address, out DeadCorpseTrack? deadTrack) &&
                !deadTrack.SawCollapsedPoseWhileDead &&
                !deadTrack.SawDisarticulatedWhileDead &&
                ((deadTrack.DeathMode == Refii.Entities.Engine_CorpsePolicy.CorpseDeathMode.PhysicalRagdoll && !deadTrack.SawRagdollWhileDead) ||
                 (deadTrack.DeathMode == Refii.Entities.Engine_CorpsePolicy.CorpseDeathMode.CrumbleSplode && deadTrack.SawRagdollWhileDead)) &&
                posedRig.IsPoseCollapsed())
            {
                deadTrack.SawCollapsedPoseWhileDead = true;
            }

            if (_reports < 10)
            {
                float poseYaw = posedRig.PoseYawDegrees();
                if (!float.IsNaN(poseYaw))
                {
                    ++_poseYawSamples;
                    _poseYawSum += Mathf.Abs(poseYaw);
                    _poseYawWorst = Mathf.Max(_poseYawWorst, Mathf.Abs(poseYaw));
                }
            }
        }

        _afterPose.Clear();
        long afterPoseTicks = System.Diagnostics.Stopwatch.GetTimestamp() - afterPoseFrom;
        poseTicks += batchTicks + afterPoseTicks;

        long drawnFrom = System.Diagnostics.Stopwatch.GetTimestamp();
        ApplyDrawn(fromWalk: true);
        long flushFrom = System.Diagnostics.Stopwatch.GetTimestamp();
        long drawnTicks = flushFrom - drawnFrom;
        Refii.Level.Engine_PropShadow.FlushDeferred();
        Refii.Core.Engine_FramePhases.Lap("ent.flush", ref flushFrom);

        // The loop's own breakdown, in the same units as the laps around it.
        double toMs = 1000.0 / System.Diagnostics.Stopwatch.Frequency;
        double poseMs = poseTicks * toMs;
        double placeMs = placeTicks * toMs;
        LastLoopMs = (System.Diagnostics.Stopwatch.GetTimestamp() - lap) * toMs;
        if (_timeSections)
        {
            Refii.Core.Engine_FramePhases.Record("ent.pose", poseMs, plot: true);
            Refii.Core.Engine_FramePhases.Record("ent.place", placeMs, plot: true);
            RecordSections(prologueTicks, skipTicks, clipTicks - batchTicks, drawnTicks,
                LastLoopMs - poseMs - placeMs - (prologueTicks + skipTicks + drawnTicks) * toMs, toMs);
        }

        // Every walk: the pose batch (the folds still running, then every rig's bone writes) and
        // what reads the finished poses (the culling boxes).
        Refii.Core.Engine_FramePhases.Record("ent.batch", batchTicks * toMs, plot: false);
        Refii.Core.Engine_FramePhases.Record("ent.afterpose", afterPoseTicks * toMs, plot: false);
        PublishRowCounts(entities.Count, posed, posedWritten, pastPrologue);

        Refii.Core.Engine_FramePhases.Lap("ent.loop", ref lap);
        _unchangedSkippedTotal += _unchangedSkipped;
        _unchangedSkipped = 0;

        // THE BREAKDOWN, IN THE LOG. The phases are readable over the debug channel and as
        // profiler plots, and neither is available on an ordinary run — so the one number this
        // walk exists to explain is printed where every run already writes. Every 120th feed,
        // which is a few seconds, so it is a trace rather than a spew.
        if (++_costReports % 120 == 0)
        {
            // The prologue, skip and drawn spans are timed too; "rest" is only what no section covers.
            double loopMs = poseMs + placeMs + (prologueTicks + skipTicks + drawnTicks) * toMs;
            GD.Print($"[entities] {entities.Count} entity(s), {posed} posed ({posedWritten} written, {posedHalfRate} of them at the " +
                (_farRigHz > 0f ? $"far rate of {_farRigHz:0.#} Hz{(FarRigPoseSpread ? ", spread" : string.Empty)}" : "guest's half rate") +
                $", {farHeld} far in view held): " +
                $"pose {poseMs:0.00}ms (read {Refii.Entities.Engine_EntitySkeleton.ReadMs:0.00}, " +
                $"write {Refii.Entities.Engine_EntitySkeleton.WriteMs:0.00}, " +
                $"{Refii.Entities.Engine_EntitySkeleton.WritesSkipped} settled), " +
                $"place {placeMs:0.00}ms, rest {System.Math.Max(0.0, LastLoopMs - loopMs):0.00}ms " +
                $"— of {LastLoopMs:0.00}ms in the loop | " +
                $"clip {clipTicks * toMs:0.00} | " +
                $"prologue {prologueTicks * toMs:0.00} ({entities.Count - _prologueSkipped} walked) + {skipTicks * toMs:0.00} ({_prologueSkipped} skipped: nothing to draw, or still and adopted/batched) " +
                $"{pastPrologue} past it | drawn {drawnTicks * toMs:0.00}" +
                (skipIndex ? $" | skip index {_skipIndex.Hits} of the skipped ({_skipIndex.Count} held), in rest" : string.Empty));
        }
        FarRigsWritten = posedHalfRate;
        FarRigsHeld = farHeld;
        SkipIndexHits = _skipIndex.Hits;
        SkipIndexHeld = _skipIndex.Count;
        _skipIndex.EndWalk();
        Retire();
        Refii.Core.Engine_FramePhases.Lap("ent.retire", ref lap);
        RetryRigs();

        // SETTLED, not merely periodic, and this is the second time the sampling point has
        // been the thing at fault rather than the code under test.
        //
        // The region mounts long before the guest has finished populating it: props stream in
        // over many seconds, entities keep arriving, and rigs are built as their meshes land.
        // A report on an early tick describes a half-built world — the first version fired
        // once, on the frame the node was created, and said "drawing 0 of 1 live entities".
        // Making it periodic only moved the problem, because the first ten reports still all
        // landed inside the first twenty seconds.
        //
        // So nothing is reported until the live entity count has held steady for a while.
        // That is the guest telling us it has finished, rather than us guessing a delay.
        ++_ticks;
        // The placement stamp: must be the ungated game tick, or two distinct placements share
        // one tick.
        _tickNow = Refii.Core.Engine_TickClock.GuestGameTick;
        // EVERY WALK. A weapon drawn from the back moves to the hand on one of the guest's
        // frames, as a re-attach with a new ticket; read every fifteenth walk, it hung on the back
        // for up to half a second and then snapped into the hand.
        _attachments.Refresh();

        if (entities.Count != _lastCount)
        {
            _lastCount = entities.Count;
            _settledFor = 0;
        }
        else
        {
            ++_settledFor;
        }

        // A count that holds still for SettleTicks, OR twenty seconds of ticks: Bowerstone
        // Market never holds still (NPCs come and go every few ticks), and gating the reconcile
        // on stillness alone left every record-less static twin standing beside its live twin for a whole
        // session in the first region of a run.
        _everSettled |= _settledFor >= SettleTicks || _ticks >= SettleFallbackTicks;

        // THE INVARIANT, RE-APPLIED FOR THE WHOLE RUN.
        //
        // `Claim` above handles the common case the instant it happens, but it only ever fires
        // for an entity the guest reports. Two things it cannot see: a record the guest never
        // instantiated (a layer whose `load` is `InitiallyOff`, a quest that has not run), and
        // a record the guest LATER gives up or takes over. `Layers.ActivateLayer` spawns a
        // gated layer mid-game and `DeactivateLayer` destroys it again, so the right answer
        // moves while you play — twice a second is far finer than a door can open.
        //
        // Latched on having settled ONCE, not on being settled now. A region populates over
        // many seconds and reconciling into a half-built world would read it as "the guest has
        // nothing"; but once it has filled, entity churn is normal and must not stop this.
        // The `_settledFor` clause is not redundant: the report below fires on exactly that tick,
        // and its counts come from here. Without it the first report can land on a tick the
        // reconcile skipped and read "0 hidden" over a region that is fully reconciled.
        // ON THE CADENCE FROM THE FIRST TICK, not after the pass settles: reconciling early
        // is safe (a record not yet reported keeps its copy standing, which is the no-hole
        // direction), and waiting for stillness left two door sets up for twenty seconds.
        if (Twins is not null && _ticks > 1 &&
            (_ticks % ReconcileTicks == 0 || _settledFor == SettleTicks || !_reconciledOnce))
        {
            _reconciledOnce = true;
            _liveRecords.Clear();
            foreach (HostBridge.GuestEntity entity in entities)
            {
                if (entity.RecordHash != 0)
                {
                    _liveRecords.Add(entity.RecordHash);
                }
            }

            // AND THE GUEST'S OWN LAYER STATE, which is what decides visibility now.
            //
            // THE DEACTIVATED LIST, not the active one. Every prop the region built came from a
            // layer whose `load` is `AlwaysOn` or `InitiallyOn`, and the guest gates exactly one
            // of those: `InitiallyOn` spawns unless its hash is in worldMgr+72. The activated
            // list at +60 governs the `InitiallyOff` layers we never build, and it is EMPTY on a
            // new game — gating on it would have hidden the whole level.
            //
            // Rebuilt per reconcile rather than cached: `Layers.ActivateLayer` and
            // `DeactivateLayer` move layers while you play, and a set sampled once at mount would
            // freeze the region into whatever the world looked like on that frame.
            //
            // Null is "cannot ask" and is NOT the same as empty, which is the ordinary answer for
            // a world where nothing has been deactivated. `Reconcile` gates nothing on null.
            uint[]? deactivated = HostBridge.GetDeactivatedLayers();
            if (deactivated is not null)
            {
                _deactivatedLayers.Clear();
                foreach (uint layer in deactivated)
                {
                    _deactivatedLayers.Add(layer);
                }
            }

            // The gate is a method group over a set whose contents change, not the field, so
            // one cached delegate serves the whole run. The skip index's hits are drawn too.
            _skipIndex.AddDrawn(_drawnRecords, _drawnKeys);
            Twins.Reconcile(
                _liveRecords, _drawnRecords, !HostBridge.EntitiesTruncated,
                deactivated is null ? null : _layerIsDeactivated ??= _deactivatedLayers.Contains);
            // RECORD-LESS TWINS — companion props and engine_level placements have no .save
            // record of their own, so only the model and the place can name them.
            if (_dirtyPlaceHides || !_everSettled)
            {
                _dirtyPlaceHides = false;
                FillDrawnPlaces();
                Twins.HideAtPlaces(_drawnPlaces);
            }
        }

        // "ent.tail": the rig retries, the attachment refresh and the reconcile, after "ent.retire".
        Refii.Core.Engine_FramePhases.Lap("ent.tail", ref lap);

        bool settled = _settledFor == SettleTicks ||
            (_everSettled && _ticks % 600 == 0);
        if (!settled)
        {
            return;
        }

        // A change in the set of unplaceable classes is reported even past the report cap.
        var seen = new System.Text.StringBuilder();
        foreach (HostBridge.UnknownPlacement v in unknownVtables)
        {
            seen.Append(v.Vtable.ToString("X8")).Append(';');
        }

        string signature = seen.ToString();
        bool changed = signature != _unknownSignature;
        _unknownSignature = signature;
        if (_reports >= 10 && !changed)
        {
            return;
        }

        ++_reports;
        string vtables = unknownVtables.Length == 0
            ? string.Empty
            : "  unplaceable vtables: " +
              string.Join(", ", System.Array.ConvertAll(
                  unknownVtables, v => $"0x{v.Vtable:X8}x{v.Entities}"));

        GD.Print($"[game-entities] settled after {_ticks} frames: drawing {_nodes.Count} of " +
            $"{entities.Count} live entities ({unknown} unplaceable), " +
            $"{_skeletons.Count} of them rigged{vtables}");

        // Path resolution and mesh loading are DIFFERENT NUMBERS: a hash that resolves to a
        // path still has to find a mesh, so "resolved 1030/1030" is not "1030 drawable".
        GD.Print($"[game-entities] {_awaitingBuild} entity(s) waiting on an off-thread build; " +
            $"of {_withModel} with a model hash, {_noMesh} loaded NO MESH " +
            "(header-only stub or not in a mounted bank)" +
            // SPLIT BY WHY, from the loader that actually knows. A stub is a body in an unmounted
            // bank and should drain as streaming banks come up; an undecodable model never will.
            // Both used to return an empty mesh in silence and be counted as a success here.
            $" — the loader turned back {Refii.Models.Engine_BnkMeshLoader.EmptyStubs} stub(s) " +
            $"and {Refii.Models.Engine_BnkMeshLoader.EmptyUndecodable} undecodable model(s) " +
            "in this region");

        // THE COMPOSITE CHARACTERS — the ones with no model hash of their own.
        //
        // `built` is the cross-check: the guest fills its 24-byte part records in the DRAW
        // virtual, and this guest never draws, so it should be zero and the source list at
        // comp17+0x28 should be the only one populated. Non-zero means the draw path runs here.
        //
        // THIS FRAME for the entity counts, TOTALS for the cache, because they answer different
        // questions. `_composites` is how many composite characters were drawn on this tick;
        // `_compositePartsDrawn` is how much geometry the shared cache has assembled over the
        // run, which is per distinct body and not per character.
        GD.Print($"[game-entities] composite characters: {_composites} drawn this frame from " +
            $"{_composed.Count} distinct body(s); the cache assembled {_compositePartsDrawn} " +
            $"part(s) ({_compositePartsMissing} would not load, {_compositePartsUnresolved} " +
            $"hash(es) not in the index this frame); guest built-part records this frame = " +
            $"{_builtParts} — non-zero means the guest's DRAW path runs here and comp17+0x38 " +
            "is populated, which was NOT the assumption");

        GD.Print($"[game-entities] morphs: {_compositesMorphed} composite(s) built with morph " +
            $"targets (run total); the guest stated {HostBridge.PartMorphStatements} part(s) " +
            $"with target records, {HostBridge.PartMorphsMorphed} of them morphed, and " +
            $"{HostBridge.TextureLayerStatements} texture composite(s), " +
            $"{Refii.Hero.Engine_HeroTextureMorph.CompositesBuilt} built here " +
            $"(saved {Refii.Hero.Engine_HeroTextureMorph.SavedWork}: joined, past the limit, from disk, decodes joined, sources decoded; " +
            $"{Refii.Textures.AndroidCompositeCache.SourcesHashed} sources read to name one)");

        GD.Print($"[game-entities] multi-mesh appearances: {_multiMesh} entity(s) drawn this " +
            $"frame from component 5, {_multiMeshParts} mesh record(s) stated — these used to " +
            "read as \"names no model\" and were drawn only by the region's static copy");

        // DO THE PARTS AGREE ON THE SKELETON THEY SHARE?
        //
        // The union keeps the first part's rest for a bone two parts both carry, which is only
        // lossless if they quote the same one. Across the shipped models they often do not —
        // `Shadow_L_Shin` is 0.26 apart between a male coat and the male body — but most of
        // that spread is child-versus-adult rigs that never appear on one character. Only the
        // guest's own part lists settle it, and they are only known here.
        //
        // A non-zero disagreement count means the losing part's geometry is displaced by up to
        // the distance shown, which reads as a garment hanging off a body rather than on it.
        int shared = Refii.Models.Engine_MdlComposite.SharedBonesAgreeing +
            Refii.Models.Engine_MdlComposite.SharedBonesDisagreeing;
        if (shared != 0)
        {
            GD.Print($"[game-entities] composite rigs: {shared} shared bone(s) across the " +
                $"parts, {Refii.Models.Engine_MdlComposite.SharedBonesDisagreeing} of them " +
                $"quoting a DIFFERENT rest" +
                (Refii.Models.Engine_MdlComposite.SharedBonesDisagreeing == 0
                    ? " — the parts agree, first-wins is lossless"
                    : $", worst '{Refii.Models.Engine_MdlComposite.WorstRestBone}' at " +
                      $"{Refii.Models.Engine_MdlComposite.WorstRestDisagreement:0.####} apart " +
                      "— that part's vertices are displaced by exactly that"));
        }

        // A part that could not be remapped was binding through ANOTHER part's numbering.
        if (Refii.Models.Engine_MdlComposite.UnremappableSurfaces != 0)
        {
            GD.Print($"[game-entities] composite rigs: " +
                $"{Refii.Models.Engine_MdlComposite.UnremappableSurfaces} skinned surface(s) " +
                $"({Refii.Models.Engine_MdlComposite.UnremappableVertices} vertices) shipped no " +
                "usable bone table, so their indices addressed the UNIONED table raw — they " +
                "were binding to whatever bone sat at that index. Collapsed to the root");
        }

        // THE FACING, CHECKED AGAINST WALKING — the one reference the conversion chain cannot
        // manufacture. See SampleFacing: the offset's VALUE names the fix.
        if (_facingSamples != 0)
        {
            // A MAJORITY, NOT MERELY THE LARGEST BUCKET. The first version called 234 of 464 a
            // pass because 0° was the biggest pile, and reported the facing RIGHT while half
            // the samples disagreed with it. A verdict that cannot fail is not a verdict.
            int onTarget = _facingBuckets[0];
            int total = onTarget + _facingBuckets[1] + _facingBuckets[2] + _facingBuckets[3];
            int worst = 1;
            for (int i = 2; i < 4; ++i)
            {
                if (_facingBuckets[i] > _facingBuckets[worst])
                {
                    worst = i;
                }
            }

            GD.Print($"[game-entities] facing vs travel: {_facingSamples} walking sample(s), " +
                $"mean {_facingSum / _facingSamples:0.#}° off; {Quadrants(_facingBuckets)}" +
                (onTarget >= (int)(0.9f * total)
                    ? " — characters walk the way they face, the facing is RIGHT"
                    : $" — NOT clean; the largest error pile is {worst * 90}°") +
                $"\n    composite (morph) characters: {Quadrants(_facingBucketsComposite)}" +
                $"\n    single-model characters:      {Quadrants(_facingBucketsNamed)}" +
                (_poseYawSamples == 0
                    ? "\n    pose yaw: no hip bone to measure"
                    : $"\n    pose yaw: the POSE turns the body {_poseYawSum / _poseYawSamples:0.#}° " +
                      $"from the model's own forward on average (worst {_poseYawWorst:0.#}°) — " +
                      (_poseYawSum / _poseYawSamples < 15f
                          ? "small, so the heading is NOT in the pose and any facing error is the entity rotation"
                          : "LARGE, so the pose carries the heading and composing the entity rotation on top double-counts it")) +
                $"\n    culling box: stated on {_extentStated} rig(s) ({_extentHeld} held), {_extentOverflow} of " +
                "those had a live pose reaching OUTSIDE the mesh's own box" +
                (_extentOverflow == 0
                    ? " — the bind box already covered them, so culling was not the vanishing"
                    : " — those are the ones the renderer could drop while they were on screen") +
                $"\n    position coherence: {_positionJumps} of {_positionSamples} frame(s) " +
                $"moved an entity more than {TeleportThreshold} in one frame " +
                $"(worst {_worstJump:0.#})" +
                (_positionJumps == 0
                    ? " — reads are coherent, so jitter is the two clocks beating, not tearing"
                    : " — that is either a scripted teleport or a TORN READ, and tearing would " +
                      "explain both the jitter and the vanishing") +
                SimClockReport() +
                $"\n    poses refused as TORN: {Refii.Entities.Engine_EntitySkeleton.TornPoses}" +
                (Refii.Entities.Engine_EntitySkeleton.TornPoses == 0
                    ? " — every rig read came back coherent"
                    : " — those rigs held last frame's pose instead of a half-written one; " +
                      "each would otherwise have been a character exploding for one frame") +
                $"\n    bone JUMPS over 1.5 m between accepted poses: {Refii.Entities.Engine_EntitySkeleton.PoseJumps}" +
                $"\n    most active rigs (changed/applied): {MostActiveRigs()}" +
                $"\n    pose samples that CHANGED: {Refii.Entities.Engine_EntitySkeleton.PoseChanges} of {Refii.Entities.Engine_EntitySkeleton.PoseApplies} applied ({(Refii.Entities.Engine_EntitySkeleton.PoseApplies > 0 ? 100.0 * Refii.Entities.Engine_EntitySkeleton.PoseChanges / Refii.Entities.Engine_EntitySkeleton.PoseApplies : 0):0}%) — 100% means the animation moves every drawn frame, ~50% at 30 fps means it moves at the 15 Hz tick" +
                $"\n    poses HELD while still local (space 1): {Refii.Entities.Engine_EntitySkeleton.LocalPosesHeld}, rigs stuck local 300+ frames: {Refii.Entities.Engine_EntitySkeleton.LocalStuckRigs}, flag-local-but-data-model applied: {Refii.Entities.Engine_EntitySkeleton.FlagLocalDataModel}" +
                $"\n    poses refused as IMPLAUSIBLE: {Refii.Entities.Engine_EntitySkeleton.ImplausiblePoses}" +
                (Refii.Entities.Engine_EntitySkeleton.ImplausiblePoses == 0
                    ? " — every accepted rig read lay within twice the rig's rest reach"
                    : " — half-converted arrays that read identically twice; held last frame's pose") +
                // WHO IS DRIVING EACH RIG. The clip animator hands a rig back to the pose feed
                // on four conditions, and only one of them is expected: flag 8, the guest's
                // physics-written pose. Folding clips over a Havok pose is what sent an
                // electrocuted body flying apart, so RAGDOLL reading zero while a corpse bursts
                // says the gate never fired — a different fault from the gate firing and the
                // feed then mishandling the array. Until now these were reachable only through
                // the debug channel, which needs a live handshake.
                $"\n    clip-driven frames: {Refii.Animations.Engine_ClipAnimator.ClipDrivenFrames}, " +
                $"handed back — ragdoll: {Refii.Animations.Engine_ClipAnimator.FallbackRagdoll}, " +
                $"procedural: {Refii.Animations.Engine_ClipAnimator.FallbackProcedural}, " +
                $"unknown clip: {Refii.Animations.Engine_ClipAnimator.FallbackUnknownClip}, " +
                $"no leaves: {Refii.Animations.Engine_ClipAnimator.FallbackNoLeaves}" +
                $"\n    rotations refused as NOT UNIT: {NonUnitRotations}" +
                (NonUnitRotations == 0
                    ? " — every entity handed us a real rotation"
                    : " — that many transforms fell back to identity because what the bridge " +
                      "returned was not a rotation") +

                // EVERY REFUSAL IN ONE LINE, and reported from here because this is the only
                // periodic report that runs the whole time the world is up. They were being
                // counted and never printed, which made them useless as the thing they exist
                // for: saying WHICH source produced a non-finite value. A guard nobody can read
                // the result of is just a silent drop.
                //
                // All zero is the answer we want. Any one of them moving names the culprit —
                // and `surfaces` in particular means a DECODE bug, not bad data, because the
                // .mdl bytes are read natively from the bank and should never yield a NaN.
                $"\n    refused as NOT FINITE — placements: {NonFinitePlacements}, " +
                $"camera: {Refii.Game.GameWorld.NonFiniteCameras}, " +
                $"fx: {Refii.Game.GameFx.NonFiniteFx}, " +
                $"prop instances: {Refii.Level.Engine_LevelProps.NonFiniteInstances}, " +
                $"mesh surfaces: {Refii.Models.Engine_MdlMesh.NonFiniteSurfaces}" +

                // Reported beside them but NOT as one of them: an out-of-reach effect carries a
                // real number and is a range judgement of ours, not evidence of a torn read.
                $"; fx out of reach: {Refii.Game.GameFx.OutOfReachFx}");
        }

        // THE GHOST COUNT, and the proof that the two sides are keyed on the same thing.
        //
        // `claimed` and `unmatched` together validate the join. Both halves are built from this
        // level's `.save` map, so a live entity's record should almost always name something
        // the region also drew; `claimed` near zero with `unmatched` near the entity count
        // would mean the guest's `entity+0x1C` is NOT the GDB record hash and the match is
        // void. That failure removes nothing — an unmatched record retires nothing — so the
        // symptom would be the ghosts coming back, never a hole in the street.
        if (Twins is { } twins)
        {
            // THE INVARIANT, AS THREE NUMBERS THAT MUST ADD UP. A record is hidden because we
            // draw it, hidden because the guest does not have it, or visible because the guest
            // has it and we cannot draw it. Anything else is a bug in the reconcile.
            int shown = twins.Records - twins.HiddenForLive - twins.HiddenByLayer;
            GD.Print($"[game-entities] static twins: {twins.Records} record(s) over " +
                $"{twins.Instances} drawable(s) — {twins.HiddenForLive} hidden because the " +
                $"guest draws its own, {twins.HiddenByLayer} hidden because their LAYER is " +
                $"deactivated, {twins.HiddenByPlace} hidden by model and place, {twins.Adopted} married to a live entity, {shown} left standing as the only copy" +
                (twins.LiveWasTruncated
                    ? "  (the entity walk saturated its buffer this tick — the dedup half is " +
                      "under-counting, the layer gate is unaffected)"
                    : string.Empty));

            // THE GATE'S OWN HEALTH, said every time, because the failure it guards against is
            // invisible by construction: a wrongly-read layer state hides props while every
            // other counter on this line stays perfectly plausible.
            GD.Print($"[game-entities] layer gate: " +
                (twins.LayerGateRan
                    ? $"ON over {twins.LayeredRecords} record(s) in " +
                      $"{twins.AlwaysOnLayers} AlwaysOn layer(s) + the rest InitiallyOn"
                    : "OFF — no `.save` layer map on the region, or the bridge could not be " +
                      "asked; nothing is being gated") +
                (twins.GatedButLive == 0
                    ? string.Empty
                    : $"  ** {twins.GatedButLive} RECORD(S) GATED OFF THAT THE GUEST REPORTS AS " +
                      "LIVE — the layer state is being read wrongly and props are being deleted **"));

            GD.Print($"[game-entities] {twins.Unmatched} live record(s) the region drew nothing " +
                $"for, {twins.NoRecord} live draw(s) with no record hash, {twins.Restored} " +
                "record(s) restored after the guest gave one up");

            if (twins.HiddenAsDestroyed != 0)
            {
                GD.Print($"[game-entities] {twins.HiddenAsDestroyed} record(s) held hidden " +
                    "because the guest's DeadEntities set says the placement is destroyed — " +
                    "without this the region's build-time copy stands where the smashed one was");
            }

            if (twins.StandingSample.Count != 0)
            {
                GD.Print("[game-entities] the static layer's whole return — records standing as " +
                    "the only copy: " + string.Join(" ", twins.StandingSample));
            }
        }

        // WHERE THE MISSING RIGS WENT. The guest reports 138 characters and we build 109
        // skeletons, and until this line the 29 had nowhere to be counted — they could equally
        // have been characters whose mesh never loaded (so no node, so no rig was ever
        // attempted) or characters with a node whose rest pose the bridge would not give up.
        // Those need opposite fixes, so the report separates them: an entity that is still in
        // the retry set AND has a live pose is a character we hold and cannot rig.
        int stubbornCharacters = 0;
        foreach (uint address in _awaitingRig)
        {
            if (HostBridge.GetEntityPose(address) is not null)
            {
                ++stubbornCharacters;
            }
        }

        GD.Print($"[game-entities] {_unrigged.Count} drawn entities still have no skeleton, " +
            $"{stubbornCharacters} of which the guest IS posing — those are characters with a " +
            "node whose rest pose the bridge will not return, not characters we never saw");

        // HERE, not at creation. Reporting a rig on the frame it was built compares the bind
        // pose against itself — Follow has not run yet, so the "live" bounds are just the seed
        // and always agree. On a settled tick they are the guest's.
        foreach (KeyValuePair<uint, Refii.Entities.Engine_EntitySkeleton> rigged in _skeletons)
        {
            if (_rigReports >= RigReportLimit)
            {
                break;
            }

            if (_nodes.TryGetValue(rigged.Key, out MeshInstance3D? owner) &&
                Refii.Entities.Engine_BodyVariants.SourceMesh(owner) is { } rigMesh)
            {
                ReportRig(rigMesh, rigged.Value);
            }
        }

        // PLAYER TWO'S RIG, NAMED, ONCE — outside the report cap, because a co-op body drawn
        // wrong on the host is the one rig whose mapping numbers have to be readable.
        (uint one, uint two) = Refii.Host.HostBridge.CoopPlayerEntities();
        if (two != 0u && two != _playerTwoReported &&
            _skeletons.TryGetValue(two, out var twoRig) &&
            _nodes.TryGetValue(two, out MeshInstance3D? twoNode) && Refii.Entities.Engine_BodyVariants.SourceMesh(twoNode) is ArrayMesh twoMesh)
        {
            _playerTwoReported = two;
            int surfaces = twoMesh.GetSurfaceCount();
            GD.Print($"[rig] PLAYER TWO 0x{two:X8} (player one 0x{one:X8}): '{twoMesh.ResourceName}' " +
                $"{surfaces} surface(s), skeleton bones={twoRig.BoneCount} (guest rig {twoRig.GuestBoneCount}, " +
                $"{twoRig.Mapped} mapped by name), node scale {twoNode.Scale}, " +
                $"aabb {twoMesh.GetAabb().Size}");
        }
    }

    /// <summary>
    /// Gives one entity a skeleton, if the guest has a rig for it. False means "not yet or not
    /// ever" — the two are indistinguishable from here, which is why <see cref="RetryRigs"/>
    /// exists.
    /// </summary>
    /// <summary>
    /// Whether Godot plays CLIPS for characters instead of copying the guest's bones.
    ///
    /// Opt-in, and off by default, because it replaces a working feed rather than adding to
    /// one. `REFII_HOST_ANIM=1` turns it on.
    /// </summary>
    private static readonly bool HostAnimation =
        System.Environment.GetEnvironmentVariable("REFII_HOST_ANIM") == "1";

    /// <summary>How many characters currently have a host-side locomotion animator.</summary>
    private int _locomotionDriven;

    private readonly Dictionary<uint, Refii.Animations.Engine_ClipAnimator> _clipAnimators = new();

    private Refii.Animations.Engine_ClipAnimator ClipAnimatorFor(
        uint address, Refii.Entities.Engine_EntitySkeleton rig)
    {
        // THE RIG, NOT THE ADDRESS, IS THE IDENTITY. A job re-creates the hero: the entity is
        // retired and built again at the same address with a new skeleton, and an animator
        // still holding the old one throws ObjectDisposedException from inside the entity walk
        // — 5,421 times after one blacksmith shift, each aborting the frame's loop for every
        // rig after the hero. "All the animations broke after the minigame."
        if (!_clipAnimators.TryGetValue(address, out Refii.Animations.Engine_ClipAnimator? animator) ||
            !animator.Drives(rig.Node))
        {
            animator = new Refii.Animations.Engine_ClipAnimator(rig.Node, rig.Node.Name, rig.ModelToGuest);
            _clipAnimators[address] = animator;
        }

        animator.Rig = rig;
        animator.Address = address;
        if (address == _morphPreviewHero) animator.InvalidateMorph();
        return animator;
    }

    /// <summary>
    /// Gives one character a locomotion animator over the skeleton it is ALREADY skinned to.
    ///
    /// The skeleton is passed in rather than built, because the mesh's bind matrices were
    /// derived from this exact one. A second skeleton would animate correctly and move nothing
    /// anyone can see.
    /// </summary>
    private void DriveLocomotion(
        uint address, MeshInstance3D node, Refii.Entities.Engine_EntitySkeleton rig)
    {
        var animator =
            node.GetNodeOrNull<Refii.Animations.Engine_LocomotionAnimator>(
                Refii.Animations.Engine_LocomotionAnimator.NodeName);

        if (animator is null)
        {
            animator = new Refii.Animations.Engine_LocomotionAnimator
            {
                Name = Refii.Animations.Engine_LocomotionAnimator.NodeName,
                EntityAddress = address,
            };

            node.AddChild(animator);

            uint[] hashes = Refii.Entities.Engine_BodyVariants.SourceMesh(node) is Refii.Entities.Engine_EntityResource resource
                ? System.Linq.Enumerable.ToArray(resource.ModelHashes)
                : System.Array.Empty<uint>();

            animator.Drive(rig.Node, rig.Node.Name, hashes);
            _locomotionDriven++;
        }
    }

    /// <summary>
    /// Puts every attached child on its parent's bone, after the parents have been placed and
    /// posed this frame. A child whose parent is not drawn — off in an unloaded part of the
    /// world, or without a rig — keeps the guest's own placement, which is what it had before.
    /// </summary>
    /// <summary>Attached children placed on the guest's own statement because no host bone could take them.</summary>
    public static long AttachedOnStated;

    private void PlaceAttached()
    {
        foreach ((uint child, MeshInstance3D node, Transform3D stated) in _attached)
        {
            // A child whose dummy names no bone this host has, or whose parent has no rig yet, is
            // placed on the guest's own statement; the walk defers every attached child to this
            // pass, so nothing else places it.
            //
            // The guest's contract (CECObjectAttachment): on every parent move, `sub_825EBAA0`
            // writes each live child's placement as inv(grip) x dummy x parentWorld
            // (`sub_825ED2A0`, unconditional). The dummy lookup `sub_82235CA8` tries the part
            // gmd tables (`sub_8223F668`, `sub_8223BE58`) and otherwise returns the IDENTITY
            // (`sub_8223BDB0`), so a child whose dummy no gmd names is planted at its parent's
            // root every update — never detached, never hidden. Following the statement puts
            // an unresolved child exactly there.
            uint parent = _attachments.ParentOf(child);
            Transform3D placed = stated;
            (string? childModel, bool isFurniture) = ChildFactsOf(child, node);
            bool onBone = false;
            if (isFurniture)
            {
                _attachments.NoteStated(child, Refii.Entities.Engine_EntityAttachments.ExitStatedFurniture);
            }
            else if (!_nodes.TryGetValue(parent, out MeshInstance3D? parentNode))
            {
                _attachments.NoteStated(child, Refii.Entities.Engine_EntityAttachments.ExitStatedParentNotDrawn);
            }
            else if (!_skeletons.TryGetValue(parent, out Refii.Entities.Engine_EntitySkeleton? rig))
            {
                _attachments.NoteStated(child, Refii.Entities.Engine_EntityAttachments.ExitStatedParentNoRig);
            }
            else
            {
                onBone = PlaceOnParent(child, stated, parent, parentNode, rig, isFurniture, childModel, out placed);
            }
            if (!onBone)
            {
                ++AttachedOnStated;
            }

            // THE WEAPON'S SHADOW FOLLOWS AT THE GUEST'S RATE. Marked before the placement
            // below, so a proxy built on this frame is placed by it; the holder's principal
            // status decides every-frame placement, as it does for the holder's own body.
            bool principalHolder = IsPrincipal(parent);
            bool syncProxy = Refii.Entities.Engine_ShadowProxy.MarkFollower(node, true, principalHolder);
            if (_entityLayers.TryGetValue(parent, out uint parentLayers))
            {
                bool parentIndoor = (parentLayers & Refii.Lighting.Engine_LightZones.IndoorMask) != 0u;
                bool childIndoor = (node.Layers & Refii.Lighting.Engine_LightZones.IndoorMask) != 0u;
                bool stripStaticDecal = (node.Layers & Refii.Fx.Engine_ModernDecals.StaticDecalReceiverLayerMask) != 0u;
                if ((parentIndoor != childIndoor || stripStaticDecal) && !Refii.Models.Engine_ClassicMaterial.Active && !Refii.Lighting.Engine_LightingFrame.CurrentInterior)
                {
                    uint cleanedLayers = node.Layers & ~Refii.Fx.Engine_ModernDecals.StaticDecalReceiverLayerMask;
                    uint updatedLayers = parentIndoor != childIndoor
                        ? (parentIndoor
                            ? ((cleanedLayers & ~(1u | Refii.Lighting.Engine_LevelLights.EntityCasterLayerMask)) |
                               Refii.Lighting.Engine_LightZones.IndoorMask | Refii.Lighting.Engine_LevelLights.IndoorEntityCasterMask)
                            : ((cleanedLayers & ~(Refii.Lighting.Engine_LightZones.IndoorMask | Refii.Lighting.Engine_LevelLights.IndoorEntityCasterMask)) |
                               1u | Refii.Lighting.Engine_LevelLights.EntityCasterLayerMask))
                        : cleanedLayers;
                    _entityLayers[child] = updatedLayers;
                    node.Layers = updatedLayers;
                    syncProxy = true;
                }
            }
            if (syncProxy)
            {
                Refii.Entities.Engine_ShadowProxy.Sync(node);
            }

            if (_skeletons.TryGetValue(child, out Refii.Entities.Engine_EntitySkeleton? childRig))
            {
                Refii.Entities.Engine_ShadowSkeletons.SetEveryFrame(childRig.Node, principalHolder);
            }
            Refii.Entities.Engine_ShadowSkeletons.SetFollowerEveryFrame(node, principalHolder);

            _followers[child] = node;
            _attachedStated[child] = stated;
            Place(child, node, placed);
            _bodies.Remove(child);  // it is not being blended; do not blend back to it
        }

        // DROPPED, NOT MISSED. A follower stops following when the guest's attachment record says
        // it has no parent — not when this frame's list lacks it, which a torn placement read can
        // cause for one walk. The body then casts as any rigid prop does.
        foreach ((uint child, MeshInstance3D node) in _followers)
        {
            if (!GodotObject.IsInstanceValid(node))
            {
                _unfollowed.Add(child);
                continue;
            }

            if (_attachments.ParentOf(child) == 0u)
            {
                _unfollowed.Add(child);
                if (Refii.Entities.Engine_ShadowProxy.MarkFollower(node, false, false))
                {
                    Refii.Entities.Engine_ShadowProxy.Sync(node);
                }
            }
        }

        foreach (uint child in _unfollowed)
        {
            _followers.Remove(child);
            _attachedStated.Remove(child);
            _childFacts.Remove(child);
            _written.Remove(child);
            _justDetached.Add(child);
            _skipIndex.Invalidate(child);
        }

        _unfollowed.Clear();
    }

    /// <summary>
    /// Places a non-mesh attached child entity (such as a particle emitter entity hung on a
    /// character or weapon dummy via <c>CECObjectAttachment</c>) onto its parent's live host
    /// bone/dummy pose for this display frame.
    /// </summary>
    public bool TryPlaceAttachedEntity(uint child, Transform3D childStated, out Transform3D placed)
    {
        placed = childStated;
        if (child == 0u)
        {
            return false;
        }

        uint parent = _attachments.ParentOf(child);
        if (parent == 0u ||
            !_nodes.TryGetValue(parent, out MeshInstance3D? parentNode) ||
            !GodotObject.IsInstanceValid(parentNode))
        {
            return false;
        }

        _skeletons.TryGetValue(parent, out Refii.Entities.Engine_EntitySkeleton? rig);
        return _attachments.TryPlace(
            child,
            childStated,
            StatedOf(parent, parentNode.Transform),
            parentNode.Transform,
            rig,
            PartPaths(parentNode),
            out placed,
            isFurniture: false,
            childModelPath: null);
    }

    /// <summary>
    /// Returns both the guest's stated placement and the host's currently drawn placement for
    /// <paramref name="address"/> (in <c>GameEntities</c> local / entity-mesh space), including
    /// attached follower entities such as weapons mounted on a character's <c>Skeleton3D</c>.
    /// </summary>
    public bool TryGetStatedAndDrawnTransform(uint address, out Transform3D stated, out Transform3D drawn)
    {
        if (address != 0u &&
            _nodes.TryGetValue(address, out MeshInstance3D? node) &&
            GodotObject.IsInstanceValid(node))
        {
            drawn = _written.TryGetValue(address, out (MeshInstance3D Node, Transform3D Drawn) w)
                ? w.Drawn
                : node.Transform;
            stated = StatedOf(address, drawn);
            return true;
        }

        stated = Transform3D.Identity;
        drawn = Transform3D.Identity;
        return false;
    }

    private uint _lastHeroZoneAddress;

    private Vector3 _lastHeroZonePos = new(float.NaN, float.NaN, float.NaN);

    /// <summary>
    /// Maps an attached effect's stated placement (<paramref name="effectStated"/>) from its
    /// target entity's stated transform (<paramref name="targetStated"/>) onto the target's live
    /// drawn transform (<paramref name="targetDrawn"/>), preserving local offsets (e.g. feet,
    /// water level, or weapon attachments).
    /// </summary>
    public static Transform3D MapAttachedTargetTransform(
        Transform3D targetStated, Transform3D targetDrawn, Transform3D effectStated)
    {
        float det = targetStated.Basis.Determinant();
        if (!float.IsFinite(det) || Mathf.Abs(det) <= 1e-6f)
        {
            return effectStated;
        }

        Transform3D mapped = targetDrawn * (targetStated.AffineInverse() * effectStated);
        return mapped.Origin.IsFinite() ? mapped : effectStated;
    }

    /// <summary>
    /// Attempts to place an effect that is attached either to an entity directly (as a
    /// <c>CECObjectAttachment</c> child via <paramref name="owner"/>) or to a target entity
    /// (<paramref name="attachedTarget"/>, such as a character or weapon). Maps the effect from
    /// the target's stated placement to its live drawn placement while preserving its local offset.
    /// </summary>
    public bool TryPlaceAttachedEffect(uint owner, uint attachedTarget, Transform3D effectStated, out Transform3D placed)
    {
        if (owner != 0u && TryPlaceAttachedEntity(owner, effectStated, out placed))
        {
            return true;
        }

        if (attachedTarget != 0u)
        {
            // Map the effect's offset relative to `attachedTarget`'s stated placement onto its
            // live drawn placement (e.g. weapon sparkles on a Weapon entity, or foot/water FX
            // offset relative to the Hero).
            if (TryGetStatedAndDrawnTransform(attachedTarget, out Transform3D targetStated, out Transform3D targetDrawn))
            {
                placed = MapAttachedTargetTransform(targetStated, targetDrawn, effectStated);
                return true;
            }
        }

        placed = effectStated;
        return false;
    }

    private readonly List<uint> _weaponChildrenScratch = new();

    /// <summary>
    /// Resolves a weapon's live drawn blade endpoints (<c>Weapon.Weapon.Hold</c> to
    /// <c>Weapon.Weapon.Top</c>) in Godot world space for host-driven double-trail FX.
    /// </summary>
    public bool TryGetWeaponTrailSpan(uint owner, uint attachedTarget, out Vector3 worldEdgeA, out Vector3 worldEdgeB)
    {
        if (attachedTarget != 0u && TryGetEntityWeaponSpan(attachedTarget, allowAabbFallback: true, out worldEdgeA, out worldEdgeB))
        {
            return true;
        }

        if (owner != 0u && TryGetEntityWeaponSpan(owner, allowAabbFallback: _attachments.ParentOf(owner) != 0u, out worldEdgeA, out worldEdgeB))
        {
            return true;
        }

        if (owner != 0u)
        {
            _attachments.ChildrenOfParent(owner, _weaponChildrenScratch);
            for (int i = 0; i < _weaponChildrenScratch.Count; i++)
            {
                uint child = _weaponChildrenScratch[i];
                if (_attachments.TryGetDummyName(child, out string dummy) &&
                    dummy.Contains("Sheath", System.StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                if (TryGetEntityWeaponSpan(child, allowAabbFallback: false, out worldEdgeA, out worldEdgeB))
                {
                    return true;
                }
            }
        }

        worldEdgeA = Vector3.Zero;
        worldEdgeB = Vector3.Zero;
        return false;
    }

    private bool TryResolveWeaponLocalSpan(MeshInstance3D node, out Vector3 holdLocal, out Vector3 topLocal)
    {
        foreach (string part in PartPaths(node))
        {
            if (Refii.Entities.Engine_EntityAttachments.TryResolveWeaponBladeSpan(part, out holdLocal, out topLocal))
            {
                return true;
            }
        }

        holdLocal = Vector3.Zero;
        topLocal = Vector3.Zero;
        return false;
    }

    /// <summary>
    /// Evaluates whether a guest-stated blade/spline span (<paramref name="trailEdgeA"/>,
    /// <paramref name="trailEdgeB"/>) matches a drawn melee weapon's stated Hold/Top span
    /// (<paramref name="weaponHold"/>, <paramref name="weaponTop"/>), tolerating the 15/30 Hz phase
    /// offset of fast 360-degree flourish spins while rejecting unrelated weapons and ground effects.
    /// </summary>
    public static bool IsCompatibleWeaponTrailMatch(
        Vector3 weaponHold,
        Vector3 weaponTop,
        Vector3 trailEdgeA,
        Vector3 trailEdgeB,
        out bool swapped,
        out float score)
    {
        const float MaxHiltDistSq = 1.15f * 1.15f;
        const float MaxTipDistSq = 1.85f * 1.85f;
        const float MinAlignmentDot = -0.25f;

        swapped = false;
        score = float.PositiveInfinity;

        Vector3 weaponVec = weaponTop - weaponHold;
        Vector3 trailVec = trailEdgeB - trailEdgeA;
        float weaponLen = weaponVec.Length();
        float trailLen = trailVec.Length();
        if (!float.IsFinite(weaponLen) || !float.IsFinite(trailLen) ||
            weaponLen < 0.15f || trailLen < 0.15f || trailLen > 3.5f ||
            trailLen < weaponLen * 0.65f || trailLen > weaponLen * 1.45f)
        {
            return false;
        }

        Vector3 weaponDir = weaponVec / weaponLen;
        Vector3 trailDir = trailVec / trailLen;
        float dot = weaponDir.Dot(trailDir);

        float directHiltSq = weaponHold.DistanceSquaredTo(trailEdgeA);
        float directTipSq = weaponTop.DistanceSquaredTo(trailEdgeB);
        if (directHiltSq <= MaxHiltDistSq && directTipSq <= MaxTipDistSq && dot >= MinAlignmentDot)
        {
            score = (directHiltSq * 2f) + directTipSq;
            swapped = false;
        }

        float swapHiltSq = weaponHold.DistanceSquaredTo(trailEdgeB);
        float swapTipSq = weaponTop.DistanceSquaredTo(trailEdgeA);
        if (swapHiltSq <= MaxHiltDistSq && swapTipSq <= MaxTipDistSq && -dot >= MinAlignmentDot)
        {
            float swapScore = (swapHiltSq * 2f) + swapTipSq;
            if (swapScore < score)
            {
                score = swapScore;
                swapped = true;
            }
        }

        return float.IsFinite(score);
    }

    /// <summary>
    /// Matches a guest-stated <c>Mode == 3</c> double-trail endpoint pair (<paramref name="statedWorldA"/>,
    /// <paramref name="statedWorldB"/>) against all currently drawn non-sheath melee weapons so unowned
    /// sword trails still lock to Godot's 60/120 FPS animated weapon pose. Returns false for non-weapon
    /// trails such as spell circles.
    /// </summary>
    public bool TryMatchDrawnWeaponTrailSpan(
        Vector3 statedWorldA,
        Vector3 statedWorldB,
        out uint matchedEntity,
        out bool swapped,
        out Vector3 drawnWorldA,
        out Vector3 drawnWorldB)
    {
        matchedEntity = 0u;
        swapped = false;
        drawnWorldA = Vector3.Zero;
        drawnWorldB = Vector3.Zero;
        float bestErr = float.PositiveInfinity;

        foreach (uint child in _attachments.Children)
        {
            if (_attachments.TryGetDummyName(child, out string dummy) &&
                dummy.Contains("Sheath", System.StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (!_nodes.TryGetValue(child, out MeshInstance3D? node) ||
                !GodotObject.IsInstanceValid(node) ||
                !TryGetStatedAndDrawnTransform(child, out Transform3D stated, out Transform3D drawn) ||
                !TryResolveWeaponLocalSpan(node, out Vector3 holdLocal, out Vector3 topLocal))
            {
                continue;
            }

            Vector3 statedHold = stated * holdLocal;
            Vector3 statedTop = stated * topLocal;
            if (!IsCompatibleWeaponTrailMatch(
                    statedHold, statedTop, statedWorldA, statedWorldB, out bool candidateSwapped, out float err) ||
                err >= bestErr)
            {
                continue;
            }

            Vector3 dHold = drawn * holdLocal;
            Vector3 dTop = drawn * topLocal;
            if (!dHold.IsFinite() || !dTop.IsFinite())
            {
                continue;
            }

            bestErr = err;
            matchedEntity = child;
            swapped = candidateSwapped;
            drawnWorldA = candidateSwapped ? dTop : dHold;
            drawnWorldB = candidateSwapped ? dHold : dTop;
        }

        return matchedEntity != 0u;
    }

    /// <summary>
    /// Matches an unowned 2..3 point Type 5 weapon spline override (<c>FX_64D9750B</c> when the guest
    /// reports <c>attached == 0</c>) against the best drawn non-sheath melee weapon and latches its
    /// local-space control points.
    /// </summary>
    public bool TryMatchWeaponEntityForSpline(
        System.ReadOnlySpan<Vector3> statedWorldSpline,
        out uint weaponEntity,
        out Vector3[] localPts)
    {
        weaponEntity = 0u;
        localPts = System.Array.Empty<Vector3>();
        if (statedWorldSpline.Length is < 2 or > 3)
        {
            return false;
        }

        Vector3 first = statedWorldSpline[0];
        Vector3 last = statedWorldSpline[statedWorldSpline.Length - 1];
        float bestErr = float.PositiveInfinity;

        foreach (uint child in _attachments.Children)
        {
            if (_attachments.TryGetDummyName(child, out string dummy) &&
                dummy.Contains("Sheath", System.StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (!_nodes.TryGetValue(child, out MeshInstance3D? node) ||
                !GodotObject.IsInstanceValid(node) ||
                !TryGetStatedAndDrawnTransform(child, out Transform3D stated, out _) ||
                !TryResolveWeaponLocalSpan(node, out Vector3 holdLocal, out Vector3 topLocal))
            {
                continue;
            }

            if (!IsCompatibleWeaponTrailMatch(
                    stated * holdLocal, stated * topLocal, first, last, out _, out float err) ||
                err >= bestErr ||
                !GameFx.TryLatchLocalSpline(stated, statedWorldSpline, out Vector3[] candidateLocal))
            {
                continue;
            }

            bestErr = err;
            weaponEntity = child;
            localPts = candidateLocal;
        }

        return weaponEntity != 0u;
    }

    /// <summary>
    /// Reads the live drawn blade endpoints for a previously matched non-sheath weapon entity on
    /// intermediate display frames without rescanning all attachments.
    /// </summary>
    public bool TryGetDrawnWeaponTrailSpanForEntity(
        uint weaponEntity,
        bool swapped,
        out Vector3 drawnWorldA,
        out Vector3 drawnWorldB)
    {
        drawnWorldA = Vector3.Zero;
        drawnWorldB = Vector3.Zero;
        if (weaponEntity == 0u ||
            (_attachments.TryGetDummyName(weaponEntity, out string dummy) &&
             dummy.Contains("Sheath", System.StringComparison.OrdinalIgnoreCase)) ||
            !_nodes.TryGetValue(weaponEntity, out MeshInstance3D? node) ||
            !GodotObject.IsInstanceValid(node) ||
            !TryGetStatedAndDrawnTransform(weaponEntity, out _, out Transform3D drawn) ||
            !TryResolveWeaponLocalSpan(node, out Vector3 holdLocal, out Vector3 topLocal))
        {
            return false;
        }

        Vector3 dHold = drawn * holdLocal;
        Vector3 dTop = drawn * topLocal;
        if (!dHold.IsFinite() || !dTop.IsFinite())
        {
            return false;
        }

        drawnWorldA = swapped ? dTop : dHold;
        drawnWorldB = swapped ? dHold : dTop;
        return true;
    }

    private bool TryGetEntityWeaponSpan(uint entityAddress, bool allowAabbFallback, out Vector3 worldEdgeA, out Vector3 worldEdgeB)
    {
        worldEdgeA = Vector3.Zero;
        worldEdgeB = Vector3.Zero;
        if (entityAddress == 0u ||
            !_nodes.TryGetValue(entityAddress, out MeshInstance3D? node) ||
            !GodotObject.IsInstanceValid(node) ||
            !TryGetStatedAndDrawnTransform(entityAddress, out _, out Transform3D drawn))
        {
            return false;
        }

        if (TryResolveWeaponLocalSpan(node, out Vector3 holdLocal, out Vector3 topLocal))
        {
            worldEdgeA = drawn * holdLocal;
            worldEdgeB = drawn * topLocal;
            return worldEdgeA.IsFinite() && worldEdgeB.IsFinite();
        }

        if (allowAabbFallback && node.Mesh is { } mesh)
        {
            Aabb aabb = mesh.GetAabb();
            Vector3 size = aabb.Size;
            int axis = size.X >= size.Y && size.X >= size.Z ? 0 : (size.Y >= size.Z ? 1 : 2);
            float extent = size[axis];
            if (extent is >= 0.25f and <= 3.0f)
            {
                Vector3 center = aabb.Position + (size * 0.5f);
                Vector3 localA = center;
                Vector3 localB = center;
                localA[axis] = aabb.Position[axis];
                localB[axis] = aabb.Position[axis] + extent;
                worldEdgeA = drawn * localA;
                worldEdgeB = drawn * localB;
                return worldEdgeA.IsFinite() && worldEdgeB.IsFinite();
            }
        }

        return false;
    }

    /// <summary>
    /// A child's model path and furniture verdict, as <see cref="ChildModelPathOf"/> and
    /// <see cref="IsFurnitureEntity"/> answer them, kept while the node, the mesh it shows and the
    /// model index are the ones they were read from.
    /// </summary>
    /// <remarks>
    /// Both answers are made of the node's entity metadata (written once, at creation), the model
    /// index and the mesh, and <see cref="PlaceAttached"/> asked them on every drawn frame for
    /// every attached child: four metadata crossings and two index lookups a child a frame, to
    /// read the same two values. The shown mesh is read to validate (one crossing); a different
    /// mesh, node or index version asks again.
    /// </remarks>
    private (string? Model, bool Furniture) ChildFactsOf(uint child, MeshInstance3D node)
    {
        Mesh? shown = node.Mesh;
        int version = Refii.Level.Engine_PropModelIndex.Version;
        if (_childFacts.TryGetValue(child, out ChildFacts known) &&
            ReferenceEquals(known.Node, node) && ReferenceEquals(known.Shown, shown) &&
            known.IndexVersion == version)
        {
            return (known.Model, known.Furniture);
        }

        string? model = ChildModelPathOf(node);
        bool furniture = IsFurnitureEntity(node);
        _childFacts[child] = new ChildFacts(node, shown, version, model, furniture);
        return (model, furniture);
    }

    private readonly record struct ChildFacts(MeshInstance3D Node, Mesh? Shown, int IndexVersion, string? Model, bool Furniture);

    /// <summary><see cref="ChildFactsOf"/>'s answers, by child address; dropped with the follower.</summary>
    private readonly Dictionary<uint, ChildFacts> _childFacts = new();

    /// <summary>The child on its parent's bone; the parent's transform read once (a crossing).</summary>
    private bool PlaceOnParent(uint child, Transform3D stated, uint parent, MeshInstance3D parentNode,
        Refii.Entities.Engine_EntitySkeleton rig, bool isFurniture, string? childModel, out Transform3D placed)
    {
        Transform3D parentPlacement = parentNode.Transform;
        return _attachments.TryPlace(child, stated, StatedOf(parent, parentPlacement),
            parentPlacement, rig, PartPaths(parentNode), out placed, isFurniture, childModel);
    }

    /// <summary>Attached children whose shadow casts through a following proxy, by address.</summary>
    private readonly Dictionary<uint, MeshInstance3D> _followers = new();

    /// <summary>The guest's stated placement for each attached follower before <see cref="PlaceAttached"/> moves its node onto the host bone.</summary>
    private readonly Dictionary<uint, Transform3D> _attachedStated = new();

    /// <summary>Entities that detached from their parent this tick and must not be skipped by prologue checks.</summary>
    private readonly HashSet<uint> _justDetached = new();

    private readonly List<uint> _unfollowed = new();

    /// <summary>
    /// The newest placement the guest STATED for an entity, as opposed to the blended one it is
    /// being drawn at. Falls back to the drawn transform for an entity with no history yet.
    /// </summary>
    private Transform3D StatedOf(uint address, Transform3D drawn) =>
        _attachedStated.TryGetValue(address, out Transform3D attachedStated)
            ? attachedStated
            : _bodies.TryGetValue(address, out Refii.Core.Engine_TickTimeline? body)
                ? body.Newest
                : drawn;

    /// <summary><see cref="PartPaths"/> per mesh, computed once.</summary>
    private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<Mesh, string[]> _partPathsByMesh = new();

    /// <summary>Cached single-element path arrays keyed by <see cref="MetaEntityModel"/> hash when a mesh carries neither <c>part_paths</c> nor <c>ResourcePath</c>.</summary>
    private static readonly System.Collections.Generic.Dictionary<uint, string[]> _partPathsByModelHash = new();

    /// <summary>
    /// The model paths behind a node's mesh — every part of a composite, or the one model. The
    /// dummy an attachment names is a record in one of their `.mdl.gmd` sidecars.
    /// </summary>
    private static string[] PartPaths(MeshInstance3D node)
    {
        if (Refii.Entities.Engine_BodyVariants.SourceMesh(node) is { } mesh)
        {
            if (!_partPathsByMesh.TryGetValue(mesh, out string[]? known))
            {
                known = PartPathsOf(mesh);
                _partPathsByMesh.AddOrUpdate(mesh, known);
            }

            if (known.Length != 0)
            {
                return known;
            }
        }

        if (node.HasMeta(MetaEntityModel))
        {
            uint modelHash = (uint)node.GetMeta(MetaEntityModel).AsInt64();
            if (modelHash != 0u)
            {
                lock (_partPathsByModelHash)
                {
                    if (_partPathsByModelHash.TryGetValue(modelHash, out string[]? byHash))
                    {
                        return byHash;
                    }
                }

                string? resolved = Refii.Level.Engine_PropModelIndex.Resolve(modelHash, System.Array.Empty<string>());
                string[] byModel = !string.IsNullOrEmpty(resolved) ? new[] { resolved } : System.Array.Empty<string>();
                if (byModel.Length != 0)
                {
                    lock (_partPathsByModelHash)
                    {
                        _partPathsByModelHash[modelHash] = byModel;
                    }
                }
                return byModel;
            }
        }

        return System.Array.Empty<string>();
    }

    private static string[] PartPathsOf(Mesh mesh) =>
        Refii.Models.Engine_MdlComposite.ModelPathsOf(mesh);

    private static string? ChildModelPathOf(MeshInstance3D node)
    {
        if (node.HasMeta(MetaEntityModel))
        {
            uint modelHash = (uint)node.GetMeta(MetaEntityModel).AsInt64();
            string? path = Refii.Level.Engine_PropModelIndex.Resolve(modelHash, System.Array.Empty<string>());
            if (!string.IsNullOrEmpty(path))
            {
                return path;
            }
        }

        string[] parts = PartPaths(node);
        if (parts.Length > 0 && !string.IsNullOrEmpty(parts[0]))
        {
            return parts[0];
        }

        if (node.Mesh is not null)
        {
            if (!string.IsNullOrEmpty(node.Mesh.ResourcePath))
            {
                return node.Mesh.ResourcePath;
            }
            if (!string.IsNullOrEmpty(node.Mesh.ResourceName))
            {
                return node.Mesh.ResourceName;
            }
        }

        return null;
    }

    private static bool IsFurnitureEntity(MeshInstance3D node)
    {
        if (node.HasMeta(MetaEntityRecord))
        {
            uint recordHash = (uint)node.GetMeta(MetaEntityRecord).AsInt64();
            if (recordHash == Refii.Equipment.Engine_RecordNameCatalog.FurnitureBaseHash)
            {
                return true;
            }
        }

        string? path = ChildModelPathOf(node);
        return path is not null && IsFurnitureModelPath(path);
    }

    public static bool IsFurnitureModelPath(string path) =>
        Refii.Entities.Engine_EntityAttachments.IsFurnitureModelPath(path);

    /// <summary>
    /// A composite's displacement-only parts (GDB DisplacementMeshes,
    /// <see cref="HostBridge.GetEntityDisplacementParts"/>): the guest draws them only into the
    /// displacement target (flag 0x800, `sub_8220DE88`), so their surfaces leave the body
    /// (<see cref="Refii.Entities.Engine_HiddenClusters.SetDisplacedSurfaces"/>) and draw on the same
    /// skeleton into the displacement layer with their own diffuse: under classic a child on the
    /// classic layer, under modern an instance in the modern layer's world.
    /// </summary>
    private void AttachDisplacementParts(uint address, MeshInstance3D node, Mesh mesh)
    {
        bool classic = Refii.Models.Engine_ClassicMaterial.Active;
        if (mesh is not ArrayMesh source ||
            node.Skin is null || node.GetNodeOrNull(node.Skeleton) is not Node skeleton ||
            !source.HasMeta(Refii.Models.Engine_MdlComposite.SurfacePartsMetaName) ||
            !source.HasMeta(Refii.Models.Engine_MdlComposite.PartHashesMetaName))
        {
            return;
        }
        uint[] displaced = HostBridge.GetEntityDisplacementParts(address);
        if (displaced.Length == 0)
        {
            return;
        }
        int[] surfaceParts = source.GetMeta(Refii.Models.Engine_MdlComposite.SurfacePartsMetaName).AsInt32Array();
        uint[] partHashes = System.Array.ConvertAll(
            source.GetMeta(Refii.Models.Engine_MdlComposite.PartHashesMetaName).AsInt64Array(), v => unchecked((uint)v));
        var subset = new ArrayMesh();
        System.UInt128 mask = System.UInt128.Zero;
        for (int s = 0; s < source.GetSurfaceCount() && s < 128 && s < surfaceParts.Length; s++)
        {
            int part = surfaceParts[s];
            if (part < 0 || part >= partHashes.Length || System.Array.IndexOf(displaced, partHashes[part]) < 0)
            {
                continue;
            }
            mask |= System.UInt128.One << s;
            Godot.Collections.Array arrays = Refii.Models.Engine_MdlSurfaceArrays.Of(source, s);
            if (!classic)
            {
                // The modern layer's stage scales by the vertex colour; a body's is its bake, not an offset.
                arrays[(int)Mesh.ArrayType.Color] = default;
            }
            int added = Refii.Models.Engine_MdlSurfaceArrays.Add(subset, Mesh.PrimitiveType.Triangles, arrays, null);
            subset.SurfaceSetMaterial(added, classic
                ? Refii.Models.Engine_ClassicDisplacement.MeshMaterial(source.SurfaceGetMaterial(s))
                : Refii.Fx.Engine_DisplacementMeshes.PartMaterial(source.SurfaceGetMaterial(s)));
        }
        if (mask == System.UInt128.Zero)
        {
            return;
        }
        if (!classic)
        {
            Refii.Fx.Engine_DisplacementMeshes.AdoptParts(node, subset);
            Refii.Entities.Engine_HiddenClusters.SetDisplacedSurfaces(node, mask);
            return;
        }
        var child = new MeshInstance3D
        {
            Name = "DisplacementParts",
            Mesh = subset,
            Skin = node.Skin,
            Layers = Refii.Models.Engine_ClassicDisplacement.Layer,
            CastShadow = GeometryInstance3D.ShadowCastingSetting.Off,
        };
        node.AddChild(child);
        child.Skeleton = child.GetPathTo(skeleton);
        Refii.Models.Engine_ClassicDisplacement.Track(child);
        Refii.Entities.Engine_HiddenClusters.SetDisplacedSurfaces(node, mask);
    }

    private bool TryRig(uint address, MeshInstance3D node, Mesh mesh, bool syncProxy = true)
    {
        // THE MODEL'S OWN BONE TABLE IS THE SKELETON. The mesh's bone indices address it, so
        // building in any other numbering binds every vertex to the wrong bone; the guest is
        // asked only for the names, which is the one key that relates its rig to this one.
        if (Refii.Entities.Engine_EntitySkeleton.TryBuild(
                address, Refii.Models.Engine_MdlRig.TryFrom(mesh)) is not { } skeleton)
        {
            return false;
        }

        skeleton.IsRigidPieceMesh = Refii.Models.Engine_MdlDisarticulation.GetProfile(mesh).IsRigidPieceMesh;
        node.AddChild(skeleton.Node);

        // The mesh has to be a CHILD of the skeleton and name it. The skin is the rig's own:
        // the model's inverse rests, each carrying the bone's live scale (the morph), which the
        // skeleton itself never holds.
        node.Skeleton = node.GetPathTo(skeleton.Node);
        node.Skin = skeleton.Skin;
        Refii.AndroidRuntime.AndroidCharacterLevers.Rigged(node);
        _skeletons[address] = skeleton;

        // A prop rigged late (RetryRigs) was a still verdict; it poses from the next walk.
        _skipIndex.Invalidate(address);

        // The proxy casts on THIS skeleton and skin, or its shadow stands in the bind pose.
        // A creation syncs it once, after the rest of its setup.
        if (syncProxy)
        {
            Refii.Entities.Engine_ShadowProxy.Sync(node);
        }

        if (_alignReports < RigReportLimit)
        {
            ++_alignReports;

            // THE NUMBER THAT SAYS WHETHER A CHARACTER CAN BE POSED AT ALL is how many of the
            // model's bones found a guest bone of the same name — the only key the guest's own
            // map uses (`sub_82B7F4C8`). A bone that did not map keeps its local rest under its
            // parent, as the guest draws it; zero means the guest poses none of the mesh either.
            GD.Print($"[rig] '{(mesh as ArrayMesh)?.ResourceName}' " +
                $"model={skeleton.BoneCount} guest={skeleton.GuestBoneCount} " +
                $"mapped={skeleton.Mapped} " +
                $"({skeleton.BoneCount - skeleton.Mapped} model bones unposed, " +
                $"{skeleton.GuestOnly} guest bones unused)" +
                (skeleton.Mapped == 0
                    ? " — no name matches the guest skeleton; the guest's own map leaves it at rest too"
                    : ""));
        }

        _dirtyPlaceHides = true;
        return true;
    }

    /// <summary>
    /// Gives the entities that had no rig at creation another chance.
    ///
    /// A RIG ATTEMPTED ONCE CAN NEVER RECOVER, and the original code attempted it exactly once
    /// on the frame the node was made. The bridge returns nothing for a character whose pose
    /// vector is "sized but never filled" — `pose_end &lt;= pose` — which is the ordinary state
    /// of an entity that exists before the animation system has reached it. Every character
    /// that lost that race stayed a static mesh for the rest of the run, and the symptom is a
    /// street of correctly placed people standing in bind pose, which reads as an animation
    /// problem rather than as a rig that was never built.
    ///
    /// Polled rather than event-driven because the guest offers no event: the pose array simply
    /// starts being non-empty. A thirtieth of a second's granularity is invisible next to how
    /// long a region takes to populate, and the probe costs one bit test at `entity+0x24` for
    /// the props, which are most of the set and answer no forever.
    /// </summary>
    private void RetryRigs()
    {
        if (_awaitingRig.Count == 0 || _ticks % RigRetryTicks != 0)
        {
            return;
        }

        _rigged.Clear();
        foreach (uint address in _awaitingRig)
        {
            if (!_nodes.TryGetValue(address, out MeshInstance3D? node) ||
                !GodotObject.IsInstanceValid(node) || Refii.Entities.Engine_BodyVariants.SourceMesh(node) is not { } mesh)
            {
                _rigged.Add(address);  // gone, not rigged — either way stop asking
                continue;
            }

            if (TryRig(address, node, mesh))
            {
                // A character after all: no screen-size cull and no size-keyed shadow reach, only
                // the range it states (none leaves it drawn and casting at any distance).
                node.VisibilityRangeEnd = 0f;
                Refii.Level.Engine_PropDrawRange.Apply(node);
                Refii.Lighting.Engine_ShadowReach.Release(node);

                // After the release, which hands the BODY its shadow back: the proxy casts.
                Refii.Entities.Engine_ShadowProxy.Sync(node);
                _lastModelHash.TryGetValue(address, out uint modelHash);
                _nodeUid.TryGetValue(address, out ulong uid);
                TryStartPendingCrumble(address, node, mesh, GlobalTransform, modelHash, uid);
                _rigged.Add(address);
            }
        }

        foreach (uint address in _rigged)
        {
            _awaitingRig.Remove(address);
            _unrigged.Remove(address);
        }
    }

    /// <summary>Entities with a node and no skeleton (props and characters waiting on a rig alike).</summary>
    private readonly HashSet<uint> _unrigged = new();

    /// <summary>Entities whose mesh carries a rig table, still waiting for the guest's pose vector.</summary>
    private readonly HashSet<uint> _awaitingRig = new();

    /// <summary>
    /// Each entity's stated range as applied ({begin, margin, end, margin, fade}); absent when the
    /// guest states none usable or the entity is a principal. See <see cref="StateDrawRange"/>.
    /// </summary>
    private readonly Dictionary<uint, float[]> _drawRanges = new();

    /// <summary>
    /// THE GUEST'S DRAW DISTANCE. The renderable states D — `MaxDrawDistance` times the class
    /// multiplier — and the gathers fade the entity out to D and cut its shadow hard there
    /// (`sub_82219AB8`, `sub_8221E5F0`). Stamped on the node for the same range rule the static
    /// placements use; `entity_draw_distance_scale` widens it. A principal is never stamped: the
    /// players are drawn at any distance. False when nothing is stated.
    /// </summary>
    private bool StateDrawRange(uint address, GeometryInstance3D node)
    {
        if (IsPrincipal(address) ||
            !HostBridge.GetEntityDrawRange(address, out float distance, out float fade) ||
            Refii.Level.Engine_PropDrawRange.EntityRange(distance, fade) is not { } stated)
        {
            return false;
        }

        _drawRanges[address] = stated;
        Refii.Level.Engine_PropDrawRange.StampEntity(node, stated);
        if (Refii.Models.Engine_ClassicMaterial.Active)
        {
            // The same D cuts its shadow hard (`sub_8221E5F0`), from the eye.
            Refii.Models.Engine_ClassicCasters.Mark(node, CastingOf(address, node),
                Refii.Level.Engine_PropDrawRange.Reach(stated));
            Refii.Models.Engine_ClassicCasters.Register(node, dynamic: true);
        }
        return true;
    }

    /// <summary>
    /// The cascades an entity casts into, as its definition states them (the renderable's flag
    /// word, <see cref="HostBridge.GetEntityCasterFlags"/>: 0x40 none, 0x80 not the far cascade,
    /// 0x100 both faces). Only the static-mesh descriptor carries the both-faces bit
    /// (`sub_822370A8`; the animated one, `sub_8220DE88`, never does), so a skinned body drops it.
    /// </summary>
    private static byte CastingOf(uint address, GeometryInstance3D node)
    {
        if (HostBridge.GetEntityCasterFlags(address) is not { } flags)
        {
            return Refii.Models.Engine_ClassicShadows.CastsDefault;
        }
        byte casting = Refii.Models.Engine_ClassicShadows.CastingOf(flags);
        if (node is MeshInstance3D { Skin: not null })
        {
            casting &= unchecked((byte)~Refii.Models.Engine_ClassicShadows.CastsTwoSided);
        }
        return casting;
    }

    /// <summary>
    /// Godot measures a range to the instance's box centre, the pose gate to the rig's origin; the
    /// slack keeps a body at the edge of its range posed while it can still be drawn.
    /// </summary>
    private const float ReachSlackMetres = 3f;

    /// <summary>How far from the eye this entity is drawn at all; infinity when it states no range.</summary>
    private float DrawnReach(uint address) =>
        _drawRanges.TryGetValue(address, out float[]? stated)
            ? Refii.Level.Engine_PropDrawRange.Reach(stated)
            : float.PositiveInfinity;

    /// <summary>Scratch for <see cref="RetryRigs"/> — a set cannot be edited while walked.</summary>
    private readonly HashSet<uint> _rigged = new();

    /// <summary>Half a second at 60fps between rig retries.</summary>
    private const int RigRetryTicks = 30;

    /// <summary>
    /// How long the live entity count must hold steady before anything is reported.
    ///
    /// Two seconds at 60fps. The world streams in for far longer than that overall, but the
    /// entity list is what these reports describe, and it stops changing well before the last
    /// texture lands.
    /// </summary>
    private const int SettleTicks = 120;

    /// <summary>Ticks after which the pass counts as settled whether or not the count held still.</summary>
    private const int SettleFallbackTicks = 600;

    private int _lastCount = -1;

    private int _settledFor;

    /// <summary>The entity list has filled at least once. Never cleared — see the reconcile.</summary>
    private bool _everSettled;

    /// <summary>Whether the twins have been reconciled at least once for this region.</summary>
    private bool _reconciledOnce;

    /// <summary>Whether at least one entity walk executed after the guest's world became active.</summary>
    private bool _walkedWhileWorldActive;

    private bool _dirtyPlaceHides = true;

    /// <summary>
    /// Whether every entity the guest has reported is on screen the way it will stay: every
    /// mesh built, the region's static copies reconciled against them. The world holds the
    /// guest in its loading screen until this is true, so nothing fills in after control returns.
    /// </summary>
    /// <remarks>
    /// A creation deferred by the per-frame budget (<c>_awaitingCreate</c>) is pending too: its
    /// mesh is built but it has no node yet.
    /// </remarks>
    public bool ReadyForRelease =>
        _ticks > 1 &&
        (!HostBridge.IsInGame || _walkedWhileWorldActive) &&
        _awaitingBuild == 0 &&
        _awaitingCreate == 0 &&
        _reconciledOnce;

    /// <summary>
    /// True once the entity feed has captured at least two guest frames after the guest's own
    /// loading screen came down (so all initial skeletons have been posed at least once).
    /// </summary>
    public bool HasInitialWalk => _postLoadScreenWalks >= 2 && _lastEntities.Count > 0;

    /// <summary>The hero as the feed has it, for the release line: does it have a node, and is it shown.</summary>
    public string DescribeHero()
    {
        uint hero = HostBridge.HeroEntityAddress();
        if (hero == 0u)
        {
            return "hero: the guest names none";
        }

        return _nodes.TryGetValue(hero, out MeshInstance3D? node) && GodotObject.IsInstanceValid(node)
            ? $"hero {hero:X8}: node built, {(node.IsVisibleInTree() ? "visible" : "HIDDEN")}"
            : $"hero {hero:X8}: NO NODE yet";
    }

    /// <summary>What <see cref="ReadyForRelease"/> is still waiting on, for the hold's log line.</summary>
    public string ReleaseWait =>
        _ticks <= 1 ? "the first entity pass"
        : (HostBridge.IsInGame && !_walkedWhileWorldActive) ? "guest world activation walk"
        : _awaitingBuild != 0 ? $"{_awaitingBuild} entity mesh build(s) ({_awaitingStub} stub(s) not counted)"
        : _awaitingCreate != 0 ? $"{_awaitingCreate} entity node(s) not yet created"
        : !_reconciledOnce ? "the first twin reconcile"
        : "nothing";

    /// <summary>Half a second at 60fps between reconciles.</summary>
    private const int ReconcileTicks = 30;

    /// <summary>
    /// A per-entity cadence: true on one walk in <paramref name="period"/>, at a phase fixed by the
    /// address. Every entity still reconciles once a period, but not all on the SAME walk, so the
    /// part reads, relights and full passes are spread across the period.
    /// </summary>
    private bool Due(uint address, int period) =>
        (_watched.Count != 0 && _watched.Contains(address)) ||
        (uint)(_ticks + (int)((address * 2654435761u) >> 20)) % (uint)period == 0u;

    /// <summary>Entities re-read on every walk, whatever their cadence — see <see cref="Watch"/>.</summary>
    private readonly HashSet<uint> _watched = new();

    /// <summary>
    /// Re-reads these entities — their part lists, garments and dyes — on every walk instead of
    /// on the reconcile cadence, for a consumer that shows them close up and changes them on a
    /// press: the co-op screen's two figures, redressed the moment a player picks their own hero
    /// or a henchman. An empty set returns every entity to its cadence.
    /// </summary>
    public void Watch(uint first, uint second)
    {
        _watched.Clear();
        if (first != 0u)
        {
            _watched.Add(first);
        }

        if (second != 0u)
        {
            _watched.Add(second);
        }
    }

    /// <summary>Scratch for the reconcile, reused so a twice-a-second pass allocates nothing.</summary>
    private readonly HashSet<uint> _liveRecords = new();

    /// <summary>The guest's deactivated-layer set, refreshed each reconcile. See the note there.</summary>
    private readonly HashSet<uint> _deactivatedLayers = new();

    /// <summary>The layer gate handed to <c>Reconcile</c>, built once. See the call site.</summary>
    private System.Func<uint, bool>? _layerIsDeactivated;

    /// <summary>Records we have a live node for THIS frame, filled as the entities are drawn.</summary>
    private readonly HashSet<uint> _drawnRecords = new();

    /// <summary>The model and place of every entity we drew this pass — see Engine_StaticTwins.HideAtPlaces.</summary>
    private readonly List<(string ModelKey, Vector3 Origin)> _drawnPlaces = new();

    /// <summary>
    /// THE PLACE OF EVERY DRAWN ENTITY, read from the node AFTER attachment placement: a door is a
    /// child attached to its building, and its stated position describes the parent-relative
    /// body nobody draws; the node's world origin is where the mesh is. Read when the reconcile
    /// (or a readout) asks, not on every walk: it is a node read per drawn entity.
    /// </summary>
    private void FillDrawnPlaces()
    {
        _skipIndex.AddDrawn(_drawnRecords, _drawnKeys);
        _drawnPlaces.Clear();
        foreach ((uint address, string modelKey) in _drawnKeys)
        {
            if (_nodes.TryGetValue(address, out MeshInstance3D? placed) && IsInstanceValid(placed) && placed.IsInsideTree())
            {
                _drawnPlaces.Add((modelKey, placed.GlobalTransform.Origin));
            }
        }
    }
    private readonly List<(uint Address, string ModelKey)> _drawnKeys = new();

    /// <summary>
    /// THE MARRIED ENTITIES: the region's own copy is the drawable, driven by the guest's
    /// placement (and for a rigged model, by its dominant bone's pose), and the entity's own
    /// node stays invisible so the rig still poses. See Engine_StaticTwins.Adoption.
    /// </summary>
    private readonly Dictionary<uint, (Refii.Entities.Engine_StaticTwins.Adoption Twin, ulong Uid)> _adoptions = new();

    private readonly Dictionary<Mesh, int> _dominantBones = new();

    public int AdoptedEntities => _adoptions.Count;

    /// <summary>
    /// The bone that carries most of a skinned mesh's weight — a rigid door is one bone of
    /// its rig, and the static copy follows that bone's pose relative to its rest.
    /// </summary>
    private int DominantBone(Mesh mesh)
    {
        if (_dominantBones.TryGetValue(mesh, out int cached))
        {
            return cached;
        }

        int best = 0;
        if (mesh is ArrayMesh array && array.GetSurfaceCount() > 0)
        {
            var weights = new Dictionary<int, float>();
            for (int surface = 0; surface < array.GetSurfaceCount(); surface++)
            {
                Godot.Collections.Array arrays = Refii.Models.Engine_MdlSurfaceArrays.Of(array, surface);
                Variant bonesVariant = arrays[(int)Mesh.ArrayType.Bones];
                Variant weightsVariant = arrays[(int)Mesh.ArrayType.Weights];
                if (bonesVariant.VariantType == Variant.Type.Nil ||
                    weightsVariant.VariantType == Variant.Type.Nil)
                {
                    continue;
                }

                int[] bones = bonesVariant.AsInt32Array();
                float[] w = weightsVariant.AsFloat32Array();
                for (int i = 0; i < bones.Length && i < w.Length; i++)
                {
                    weights.TryGetValue(bones[i], out float had);
                    weights[bones[i]] = had + w[i];
                }
            }

            float top = -1f;
            foreach (KeyValuePair<int, float> pair in weights)
            {
                if (pair.Value > top)
                {
                    top = pair.Value;
                    best = pair.Key;
                }
            }
        }

        _dominantBones[mesh] = best;
        return best;
    }

    /// <summary>Where the married copy stands this tick, in world space.</summary>
    private Transform3D AdoptedWorld(uint address, MeshInstance3D node)
    {
        Transform3D world = node.GlobalTransform;
        if (_skeletons.TryGetValue(address, out Refii.Entities.Engine_EntitySkeleton? rig) &&
            Refii.Entities.Engine_BodyVariants.SourceMesh(node) is { } mesh)
        {
            Skeleton3D sk = rig.Node;
            int bone = DominantBone(mesh);
            if (bone >= 0 && bone < sk.GetBoneCount())
            {
                world *= sk.GetBoneGlobalPose(bone) * sk.GetBoneGlobalRest(bone).AffineInverse();
            }
        }

        return world;
    }
    /// <summary>
    /// The live entities whose model key contains <paramref name="needle"/> and the static
    /// twins of the same models, side by side: the join HideAtPlaces makes, laid out to read.
    /// </summary>
    public Dictionary<string, object?> DescribeTwins(string needle, bool apply = false)
    {
        string want = needle.ToLowerInvariant();
        int applied = -1;
        if (apply && Twins is { } twinsNow)
        {
            int before = twinsNow.HiddenByPlace;
            FillDrawnPlaces();
            twinsNow.HideAtPlaces(_drawnPlaces);
            applied = twinsNow.HiddenByPlace - before;
        }
        var live = new List<Dictionary<string, object?>>();
        foreach ((uint address, MeshInstance3D node) in _nodes)
        {
            uint modelHash = node.HasMeta("entity_model") ? (uint)node.GetMeta("entity_model").AsInt64() : 0u;
            string? key = modelHash == 0u ? null : ModelKeyFor(modelHash);
            if (key is null || !key.Contains(want, System.StringComparison.Ordinal))
            {
                continue;
            }

            Vector3 at = node.GlobalTransform.Origin;
            live.Add(new Dictionary<string, object?>
            {
                ["address"] = $"{address:X8}",
                ["model"] = key,
                ["modelHash"] = $"{modelHash:X8}",
                ["record"] = node.HasMeta("entity_record") ? $"{(uint)node.GetMeta("entity_record").AsInt64():X8}" : null,
                ["at"] = $"{at.X:F2},{at.Y:F2},{at.Z:F2}",
                ["visible"] = node.Visible,
                ["rigged"] = _skeletons.ContainsKey(address),
                ["adopted"] = _adoptions.ContainsKey(address),
                ["seen"] = _seen.Contains(address) || _skipIndex.Owns(address),
            });
        }

        int drawnKeys = 0;
        FillDrawnPlaces();
        foreach ((string key, Vector3 _) in _drawnPlaces)
        {
            if (key.Contains(want, System.StringComparison.Ordinal))
            {
                ++drawnKeys;
            }
        }

        return new Dictionary<string, object?>
        {
            ["live"] = live,
            ["drawnPlaces"] = drawnKeys,
            ["drawnTotal"] = _drawnPlaces.Count,
            ["applied"] = applied,
            ["ticks"] = _ticks,
            ["everSettled"] = _everSettled,
            ["twins"] = Twins?.DescribeByModel(want, _drawnPlaces),
        };
    }

    /// <summary>
    /// Every drawn entity with a drawn static twin within <paramref name="radius"/>, any model:
    /// the complete list of doubles on screen, with the flags that say which path missed.
    /// </summary>
    public Dictionary<string, object?> DescribeDoubles(float radius)
    {
        var pairs = new List<Dictionary<string, object?>>();
        var unresolved = new List<Dictionary<string, object?>>();
        if (Twins is not { } twins)
        {
            return new Dictionary<string, object?> { ["error"] = "no twins" };
        }

        var grid = new Dictionary<(int, int, int), List<int>>();
        List<(string Model, Vector3 At, uint Record, bool Adopted, string Node)> drawn = twins.DrawnTwins();
        for (int i = 0; i < drawn.Count; i++)
        {
            (int, int, int) cell = Cell(drawn[i].At);
            if (!grid.TryGetValue(cell, out List<int>? list))
            {
                list = new List<int>();
                grid[cell] = list;
            }

            list.Add(i);
        }

        float limit = radius * radius;
        foreach ((uint address, MeshInstance3D node) in _nodes)
        {
            if (!node.Visible || !node.IsInsideTree())
            {
                continue;
            }

            uint modelHash = node.HasMeta("entity_model") ? (uint)node.GetMeta("entity_model").AsInt64() : 0u;
            uint record = node.HasMeta("entity_record") ? (uint)node.GetMeta("entity_record").AsInt64() : 0u;
            string? key = modelHash == 0u ? null : ModelKeyFor(modelHash);
            Vector3 at = node.GlobalTransform.Origin;
            if (key is null)
            {
                unresolved.Add(new Dictionary<string, object?>
                {
                    ["address"] = $"{address:X8}",
                    ["modelHash"] = $"{modelHash:X8}",
                    ["record"] = $"{record:X8}",
                    ["at"] = $"{at.X:F2},{at.Y:F2},{at.Z:F2}",
                });
            }

            (int cx, int cy, int cz) = Cell(at);
            for (int dx = -1; dx <= 1; dx++)
            {
                for (int dy = -1; dy <= 1; dy++)
                {
                    for (int dz = -1; dz <= 1; dz++)
                    {
                        if (!grid.TryGetValue((cx + dx, cy + dy, cz + dz), out List<int>? list))
                        {
                            continue;
                        }

                        foreach (int i in list)
                        {
                            float d2 = drawn[i].At.DistanceSquaredTo(at);
                            if (d2 > limit)
                            {
                                continue;
                            }

                            pairs.Add(new Dictionary<string, object?>
                            {
                                ["address"] = $"{address:X8}",
                                ["entityModel"] = key,
                                ["entityRecord"] = $"{record:X8}",
                                ["rigged"] = _skeletons.ContainsKey(address),
                                ["adopted"] = _adoptions.ContainsKey(address),
                                ["twinModel"] = drawn[i].Model,
                                ["twinNode"] = drawn[i].Node,
                                ["twinRecord"] = drawn[i].Record == 0u ? null : $"{drawn[i].Record:X8}",
                                ["twinAdopted"] = drawn[i].Adopted,
                                ["distance"] = $"{Mathf.Sqrt(d2):F3}",
                                ["at"] = $"{at.X:F2},{at.Y:F2},{at.Z:F2}",
                            });
                        }
                    }
                }
            }
        }

        // OWN NODES STANDING IN FOR A RECORD: the entity draws through the entity lighting path
        // (no vertex bake) while its record copy is hidden. Every one of these is a surface that
        // lost its authored bake; a rock among them is a rock gone black in a cave.
        var ownNodes = new List<Dictionary<string, object?>>();
        foreach ((uint address, MeshInstance3D node) in _nodes)
        {
            if (!node.Visible || _adoptions.ContainsKey(address))
            {
                continue;
            }

            uint record = node.HasMeta("entity_record") ? (uint)node.GetMeta("entity_record").AsInt64() : 0u;
            if (record == 0u || !twins.HasRecord(record))
            {
                continue;
            }

            uint modelHash = node.HasMeta("entity_model") ? (uint)node.GetMeta("entity_model").AsInt64() : 0u;
            Vector3 at = node.GlobalTransform.Origin;
            ownNodes.Add(new Dictionary<string, object?>
            {
                ["address"] = $"{address:X8}",
                ["record"] = $"{record:X8}",
                ["entityModel"] = modelHash == 0u ? null : ModelKeyFor(modelHash),
                ["twinModels"] = twins.ModelsOfRecord(record),
                ["rigged"] = _skeletons.ContainsKey(address),
                ["at"] = $"{at.X:F2},{at.Y:F2},{at.Z:F2}",
            });
        }

        return new Dictionary<string, object?>
        {
            ["ticks"] = _ticks,
            ["drawnEntities"] = _nodes.Count,
            ["drawnTwins"] = drawn.Count,
            ["pairs"] = pairs,
            ["unresolvedModel"] = unresolved,
            ["ownNodesWithRecordTwin"] = ownNodes,
        };
    }

    private static (int, int, int) Cell(Vector3 at) =>
        (Mathf.FloorToInt(at.X), Mathf.FloorToInt(at.Y), Mathf.FloorToInt(at.Z));

    private readonly Dictionary<uint, string?> _modelKeys = new();

    private string? ModelKeyFor(uint modelHash)
    {
        if (_modelKeys.TryGetValue(modelHash, out string? cached))
        {
            return cached;
        }

        string? path = Refii.Level.Engine_PropModelIndex.Resolve(modelHash, System.Array.Empty<string>());
        string? key = path is null ? null : Refii.Entities.Engine_StaticTwins.ModelKey(path);
        _modelKeys[modelHash] = key;
        return key;
    }

    /// <summary>
    /// Checks that the MESH's bone indices and the GUEST's skeleton are the same numbering.
    ///
    /// They may well not be, and that is the leading explanation for characters rendering as
    /// a collapsed fan of triangles. The guest does not hand vertex bone indices straight to
    /// the skeleton: `sub_8222E2B8` builds a per-LOD bone PALETTE and remaps through it
    /// (`v23 = *(v19 + *(v11 + 20))`, a per-LOD table) before concatenating matrices. If the
    /// mesh indices address that palette rather than the skeleton, then binding them to a
    /// Skeleton3D built in skeleton order attaches every vertex to the wrong bone — which
    /// looks exactly like broken weights or a broken rig, and is neither.
    ///
    /// Reported rather than worked around, because the fix depends on which it is: equal
    /// counts and an in-range maximum means the numbering agrees and the fault is elsewhere;
    /// a maximum at or above the skeleton's bone count proves a remap is missing.
    /// </summary>
    /// <summary>
    /// How much geometry is weighted to a bone the guest never drives — ACROSS EVERY SURFACE.
    ///
    /// With the bone bounds now matching the bind pose, the skeleton is right and whatever is
    /// left is about which vertices attach where. A bone the guest does not name keeps its bind
    /// transform for ever while its parent moves, so any vertex weighted to it is anchored in
    /// place while the rest of the body walks away — which on screen is a flat sheet of
    /// triangles fanning between the character and where that bone used to be. That is the
    /// shape actually on screen, and it is a different fault from a rig that explodes.
    ///
    /// EVERY SURFACE, not surface 0. A composite character is a body plus a head plus garments,
    /// concatenated as separate surfaces, and the body is usually first — so a garment left
    /// behind is invisible to a probe that only ever looks at surface 0. The pale sheets hang
    /// off forearms and chests, which is where garments are.
    ///
    /// Weight-bearing only: an index with a zero weight influences nothing and is common
    /// padding, so counting it would report tearing on meshes that are perfectly fine.
    /// </summary>
    private static void ReportUndrivenWeights(
        ArrayMesh array, Refii.Entities.Engine_EntitySkeleton skeleton)
    {
        var byBone = new Dictionary<int, int>();
        int affected = 0;
        int total = 0;

        for (int s = 0; s < array.GetSurfaceCount(); ++s)
        {
            Godot.Collections.Array arrays = Refii.Models.Engine_MdlSurfaceArrays.Of(array, s);
            if (arrays.Count <= (int)Mesh.ArrayType.Weights ||
                arrays[(int)Mesh.ArrayType.Bones].VariantType == Variant.Type.Nil ||
                arrays[(int)Mesh.ArrayType.Weights].VariantType == Variant.Type.Nil)
            {
                continue;
            }

            int[] bones = arrays[(int)Mesh.ArrayType.Bones].AsInt32Array();
            float[] weights = arrays[(int)Mesh.ArrayType.Weights].AsFloat32Array();

            for (int v = 0; v + 3 < bones.Length && v + 3 < weights.Length; v += 4)
            {
                ++total;
                bool torn = false;
                for (int k = 0; k < 4; ++k)
                {
                    if (weights[v + k] <= 0.0001f || skeleton.IsPosed(bones[v + k]))
                    {
                        continue;
                    }

                    torn = true;
                    byBone.TryGetValue(bones[v + k], out int count);
                    byBone[bones[v + k]] = count + 1;
                }

                if (torn)
                {
                    ++affected;
                }
            }
        }

        if (affected == 0)
        {
            GD.Print($"    every weighted vertex of {total} attaches to a bone the guest " +
                "drives — nothing is anchored in bind");
            return;
        }

        var worst = new List<KeyValuePair<int, int>>(byBone);
        worst.Sort((a, b) => b.Value.CompareTo(a.Value));

        var named = new List<string>();
        for (int i = 0; i < worst.Count && i < 5; ++i)
        {
            named.Add($"'{skeleton.BoneName(worst[i].Key)}'[{worst[i].Key}] x{worst[i].Value}");
        }

        GD.Print($"    ANCHORED IN BIND: {affected} of {total} vertices carry weight on a bone " +
            $"the guest never drives, across {byBone.Count} such bone(s) — " +
            string.Join(", ", named) +
            " — those vertices stay put while their parents move, which is the sheet of " +
            "triangles on screen");
    }

    private uint _playerTwoReported;

    private void ReportRig(Mesh mesh, Refii.Entities.Engine_EntitySkeleton skeleton)
    {
        if (_rigReports >= RigReportLimit || mesh is not ArrayMesh array || array.GetSurfaceCount() == 0)
        {
            return;
        }

        Godot.Collections.Array surface = Refii.Models.Engine_MdlSurfaceArrays.Of(array, 0);
        if (surface.Count <= (int)Mesh.ArrayType.Bones ||
            surface[(int)Mesh.ArrayType.Bones].VariantType == Variant.Type.Nil)
        {
            ++_rigReports;
            GD.Print($"[rig] '{array.ResourceName}' has NO bone array — it is rigged in the " +
                "guest but our mesh reader produced no weights, so it can only draw in bind pose");
            return;
        }

        // ONLY REPORT THINGS THAT ARE ACTUALLY SKINNED, and spend the report budget on them.
        //
        // The first run of this burned all three reports on a lamp post and a cemetery gate —
        // props that carry a skeleton but weight every vertex to bone 0, so they exercise none
        // of the skinning and answer none of the question. The characters, which are the whole
        // reason this exists, never got a line. A cap that fires before the interesting case is
        // not a diagnostic; it is a way of not looking.
        int[] probe = surface[(int)Mesh.ArrayType.Bones].AsInt32Array();
        int probeHighest = 0;
        foreach (int id in probe)
        {
            if (id > probeHighest)
            {
                probeHighest = id;
            }
        }

        if (probeHighest == 0)
        {
            return;  // single-bone prop, not a rig
        }

        ++_rigReports;

        int[] bones = surface[(int)Mesh.ArrayType.Bones].AsInt32Array();
        float[] weights = surface[(int)Mesh.ArrayType.Weights].AsFloat32Array();

        int highest = -1;
        foreach (int bone in bones)
        {
            if (bone > highest)
            {
                highest = bone;
            }
        }

        // Weight sums, because Godot expects them normalised and a mesh whose weights sum to
        // something else collapses toward the origin in its own right.
        int unnormalised = 0;
        for (int v = 0; v + 3 < weights.Length; v += 4)
        {
            float sum = weights[v] + weights[v + 1] + weights[v + 2] + weights[v + 3];
            if (Mathf.Abs(sum - 1f) > 0.01f)
            {
                ++unnormalised;
            }
        }

        // Now that the skeleton IS the model's table this should never be out of range, and
        // that is exactly why it is still checked: it is the assertion that the numbering the
        // skin binds through and the numbering the vertices index are the same one.
        GD.Print($"[rig] '{array.ResourceName}': mesh highest bone index={highest}, " +
            $"skeleton bones={skeleton.BoneCount} (guest rig {skeleton.GuestBoneCount}, " +
            $"{skeleton.Mapped} mapped by name), " +
            $"{unnormalised} of {weights.Length / 4} vertices have weights that do not sum to 1" +
            (highest >= skeleton.BoneCount
                ? " — OUT OF RANGE, the skeleton is not the model's table"
                : " — in range"));

        // The bind is the MODEL's, because that is the space the vertices are authored in. This
        // line is how far the GUEST's own bind sits from it — informative, deliberately unused.
        // The child hero is the reason: its guest bind is the adult rig, 1.85 against a 1.38
        // mesh, so "same rig as the pose" is the wrong rule and this number is the proof.
        GD.Print($"    bind: from the MODEL" +
            (skeleton.BindGap > 0.0005f
                ? $"; the guest's own bind differs by up to {skeleton.BindGap:0.####} at " +
                  $"'{skeleton.BindGapBone}' — measured, not used"
                : "; the guest's bind agrees with it"));

        ReportUndrivenWeights(array, skeleton);

        // The decisive comparison, and the one that needs no camera. If the bind bounds do not
        // resemble the mesh the rig is wrong before the guest's pose is even applied; if they
        // do and the live bounds do not, the pose feed is what breaks it. Screenshots could not
        // separate those — four runs in a row put no character in frame at all.
        Aabb mesh3d = array.GetAabb();
        Aabb bind = skeleton.Bounds(rest: true);
        Aabb live = skeleton.Bounds(rest: false);

        GD.Print($"    mesh aabb pos={mesh3d.Position} size={mesh3d.Size}");
        GD.Print($"    bind bones pos={bind.Position} size={bind.Size}");
        GD.Print($"    live bones pos={live.Position} size={live.Size}");

        // WHICH bone, when the live bounds do not resemble the mesh. A rig whose live extent is
        // several times its bind extent has one chain flying off, and the name plus the parent's
        // position says whether the composition is at fault or that single bone's transform is.
        if (live.Size.Length() > bind.Size.Length() * 2f)
        {
            GD.Print($"    EXPLODED — live extent is {live.Size.Length() / bind.Size.Length():0.#}x " +
                $"the bind extent{skeleton.DescribeOutlier()}");
        }
    }

    private int _rigReports;

    /// <summary>
    /// Rig reports per run.
    ///
    /// Eight rather than three because a region holds more interesting cases than that — the
    /// chickens, the child hero and the composite characters are each worth a line, and a cap
    /// that fires before the case being chased reports nothing useful.
    /// </summary>
    private const int RigReportLimit = 8;

    /// <summary>Bone-alignment reports emitted, capped so a busy region does not flood.</summary>
    private int _alignReports;

    /// <summary>Drops nodes for entities the guest no longer reports.</summary>
    private void Retire()
    {
        // The skip index's owned hits are this walk's too; they are not in `_seen`.
        if (_nodes.Count == _seen.Count + _skipIndex.OwnedHits)
        {
            return;
        }

        // A dictionary cannot be edited while it is walked, so the addresses are collected
        // first, into a reused list: this can run every frame.
        _gone.Clear();
        foreach (KeyValuePair<uint, MeshInstance3D> pair in _nodes)
        {
            if (!_seen.Contains(pair.Key) && !_skipIndex.Owns(pair.Key))
            {
                _gone.Add(pair.Key);
            }
        }

        foreach (uint address in _gone)
        {
            RetireAddress(address);
        }
    }

    /// <summary>
    /// Frees the node at <paramref name="address"/> and forgets everything kept for the entity
    /// that held it: it left the walk, or the guest made another entity there.
    /// </summary>
    private void RetireAddress(uint address)
    {
        bool adoptedCorpse = TryAdoptRetiredCorpse(address);
        if (!adoptedCorpse &&
            _nodes.TryGetValue(address, out MeshInstance3D? node) &&
            GodotObject.IsInstanceValid(node))
        {
            Refii.Entities.Engine_EntityAppearance.Forget(node);
            node.QueueFree();
        }

        _nodes.Remove(address);
        _nodeUid.Remove(address);
        _seen.Remove(address);
        if (SkipIndexEnabled)
        {
            _skipIndex.Unpark(address);
        }
        _guestPlacements.Remove(address);
        _lastLook.Remove(address);
        _batches?.Demote(address);
        _still.Remove(address);
        if (_adoptions.TryGetValue(address, out var married))
        {
            // Destroyed by the guest (a key taken, a door broken): the copy goes with it.
            // Merely unreported (streamed out, walk truncated): it stays where it stood.
            bool destroyed = false;
            foreach (ulong dead in HostBridge.GetDeadEntities())
            {
                if (dead == married.Uid)
                {
                    destroyed = true;
                    break;
                }
            }
            Twins?.Release(married.Twin, destroyed);
            _adoptions.Remove(address);
        }

        // The rig goes with the node it hung under; leaving the entry behind would keep
        // a freed skeleton reachable and pose it every frame.
        _skeletons.Remove(address);
        _lastModelHash.Remove(address);
        _disarticulated.Remove(address);
        _pendingCrumble.Remove(address);
        _deadCorpseState.Remove(address);
        _sawEtherealAlive.Remove(address);
        _rigSeenMs.Remove(address);
        _viewSpheres.Remove(address);
        _boneWriteAskedMs.Remove(address);
        _lastLit.Remove(address);
        _eyes.Remove(address);
        _bodyAndEyes.Remove(address);
        _clipAnimators.Remove(address);
        _bodies.Remove(address);
        _movers.Remove(address);
        _written.Remove(address);
        _compositeParts.Remove(address);
        _partMorphs.Remove(address);
        _textureLayers.Remove(address);
        _meshParts.Remove(address);
        _multiMeshSplit.Remove(address);
        _interiorMeshesPending.Remove(address);
        _interiorNodes.Remove(address);
        _nodeMesh.Remove(address);
        _relit.Remove(address);
        _attachments.Forget(address);
        _followers.Remove(address);
        _attachedStated.Remove(address);
        _childFacts.Remove(address);
        _justDetached.Remove(address);

        // And the retry, which would otherwise keep asking the guest about an entity it
        // no longer has — an address the guest has reused answers for something else.
        _unrigged.Remove(address);
        _awaitingRig.Remove(address);
        _extentHeldStreak.Remove(address);
        _drawRanges.Remove(address);
        _entityLightZone.Remove(address);
        _entityLayers.Remove(address);
        _offscreenDeferred.Remove(address);
    }

    private void ClearCorpseState()
    {
        _corpses.Clear();
        _deadCorpseState.Clear();
        _sawEtherealAlive.Clear();
        _pendingCrumble.Clear();
    }

    private Vector3 CurrentEyePosition() =>
        _poseViewCount > 0 ? _poseViews[0].Eye : (HeroPosition ?? Vector3.Zero);

    private void ObserveCorpseLiveness(in HostBridge.GuestEntity entity, float walkDelta)
    {
        if (!entity.Appearance.IsDeadCreature)
        {
            if (Refii.Entities.Engine_CorpsePolicy.IsEtherealAppearance(entity.Appearance))
            {
                _sawEtherealAlive.Add(entity.Address);
            }
            else if (Refii.Entities.Engine_CorpsePolicy.IsSolidLivingAppearance(entity.Appearance))
            {
                _sawEtherealAlive.Remove(entity.Address);
            }
            _deadCorpseState.Remove(entity.Address);
            return;
        }

        if (!_deadCorpseState.TryGetValue(entity.Address, out DeadCorpseTrack? track))
        {
            track = new DeadCorpseTrack
            {
                RecordHash = entity.RecordHash,
            };
            _deadCorpseState[entity.Address] = track;
        }
        else if (track.RecordHash != entity.RecordHash)
        {
            track.RecordHash = entity.RecordHash;
            track.ModelPath = null;
            track.RecordClassified = false;
            track.ClassifiedFromGdb = false;
        }

        track.DeathAge += walkDelta;
        track.GuestCorpse |= entity.Appearance.IsGuestCorpse;

        if (!track.RecordClassified || (!track.ClassifiedFromGdb && track.ModelPath is null))
        {
            if (track.ModelPath is null)
            {
                string? resolved = entity.ModelHash != 0u
                    ? Refii.Level.Engine_PropModelIndex.Resolve(entity.ModelHash, System.Array.Empty<string>())
                    : null;
                if (string.IsNullOrEmpty(resolved) && _nodeMesh.TryGetValue(entity.Address, out Mesh? existingMesh))
                {
                    resolved = FirstExcludedCompositeModelPath(existingMesh);
                }

                if (!string.IsNullOrEmpty(resolved))
                {
                    track.ModelPath = resolved;
                }
            }

            track.DeathMode = Refii.Entities.Engine_CorpsePolicy.ClassifyRecord(
                entity.RecordHash,
                track.ModelPath,
                out bool fromGdb);
            track.ClassifiedFromGdb = fromGdb;
            track.RecordClassified = true;
        }

        if (entity.Appearance.Drawn(false) && entity.Appearance.Scale > 0.05f)
        {
            track.SawVisibleWhileDead = true;
            track.LastDeadLook = entity.Appearance.VisualOnly;
        }
    }

    private static string? FirstExcludedCompositeModelPath(Mesh? mesh)
    {
        if (mesh is null)
        {
            return null;
        }

        foreach (string path in Refii.Models.Engine_MdlComposite.ModelPathsOf(mesh))
        {
            if (Refii.Entities.Engine_CorpsePolicy.HasExcludedName(path))
            {
                return path;
            }
        }

        return null;
    }

    /// <summary>The guest leaves this dead creature lying (Engine_CorpsePolicy.LeavesCorpse).</summary>
    private static bool LeavesCorpse(DeadCorpseTrack track) =>
        Refii.Entities.Engine_CorpsePolicy.LeavesCorpse(
            Refii.Core.Engine_TitleGame.Active.StatesCorpses,
            track.GuestCorpse,
            track.DeathMode,
            unstatedExcluded: track.DeathMode == Refii.Entities.Engine_CorpsePolicy.CorpseDeathMode.Excluded ||
                (!track.ClassifiedFromGdb && Refii.Entities.Engine_CorpsePolicy.HasExcludedName(track.ModelPath)));

    private HostBridge.EntityAppearance ResolveEffectiveAppearance(
        in HostBridge.GuestEntity entity,
        Refii.Core.Engine_TitleGame title,
        out DeadCorpseTrack? corpseTrack)
    {
        corpseTrack = null;
        if (!Refii.Entities.Engine_CorpsePolicy.Enabled ||
            !entity.Appearance.IsDeadCreature ||
            IsPrincipal(entity.Address) ||
            entity.Address == title.CompanionEntityAddress ||
            _sawEtherealAlive.Contains(entity.Address) ||
            (!_skeletons.ContainsKey(entity.Address) && !_awaitingRig.Contains(entity.Address)) ||
            !_deadCorpseState.TryGetValue(entity.Address, out DeadCorpseTrack? track) ||
            !track.SawVisibleWhileDead ||
            track.DeathMode == Refii.Entities.Engine_CorpsePolicy.CorpseDeathMode.Excluded ||
            (!track.ClassifiedFromGdb && Refii.Entities.Engine_CorpsePolicy.HasExcludedName(track.ModelPath)) ||
            !Refii.Entities.Engine_CorpsePolicy.HasValidDeathPose(
                track.DeathMode,
                track.SawPosedWhileDead,
                track.SawRagdollWhileDead,
                track.SawDisarticulatedWhileDead || _disarticulated.ContainsKey(entity.Address),
                track.SawCollapsedPoseWhileDead) ||
            !LeavesCorpse(track))
        {
            return entity.Appearance;
        }

        if (!track.FadePinned && entity.Appearance.Alpha >= 0.98f && entity.Appearance.Drawable)
        {
            return entity.Appearance;
        }

        track.FadePinned = true;
        corpseTrack = track;
        HostBridge.EntityAppearance clean = track.LastDeadLook.VisualOnly;
        Vector4 tint = clean.TintLanes;
        if (!tint.IsFinite() || (tint.X == 0f && tint.Y == 0f && tint.Z == 0f && tint.W == 0f))
        {
            tint = Vector4.One;
        }

        float scale = float.IsFinite(clean.Scale) && clean.Scale > 0.05f ? clean.Scale : 1f;
        return clean with
        {
            Alpha = 1f,
            CameraAlpha = 1f,
            Scale = scale,
            TintLanes = tint,
            Flags = clean.Flags | HostBridge.EntityAppearance.DrawableFlag,
        };
    }

    private static Vector3 ComputeInheritedVelocity(in HostBridge.GuestEntity entity)
    {
        if (!IsUsablePlacement(entity.PreviousPosition))
        {
            return Vector3.Zero;
        }

        Vector3 delta = ToGodot(entity.Position) - ToGodot(entity.PreviousPosition);
        if (!delta.IsFinite() || delta.LengthSquared() > 16f)
        {
            return Vector3.Zero;
        }

        return delta * 15f;
    }

    private void TryCapturePendingCrumble(
        uint address,
        ulong uniqueId,
        uint sourceModelHash,
        uint targetModelHash,
        Transform3D captureWorld,
        Refii.Entities.Engine_EntitySkeleton liveRig,
        Vector3 inheritedVelocity)
    {
        liveRig.CaptureWorldBones(captureWorld, _capturedBonesScratch);
        var pending = new PendingCrumble
        {
            UniqueId = uniqueId,
            SourceModelHash = sourceModelHash,
            TargetModelHash = targetModelHash,
            EntityWorld = captureWorld,
            InheritedVelocity = inheritedVelocity,
        };
        foreach ((string boneName, (Vector3 Pos, Quaternion Rot) xform) in _capturedBonesScratch)
        {
            pending.Bones[boneName] = xform;
        }

        _pendingCrumble[address] = pending;
    }

    private void TryStartPendingCrumble(
        uint address,
        MeshInstance3D node,
        Mesh mesh,
        Transform3D toWorld,
        uint modelHash,
        ulong uniqueId)
    {
        if (!_pendingCrumble.TryGetValue(address, out PendingCrumble? pending))
        {
            return;
        }

        _pendingCrumble.Remove(address);
        if (pending.UniqueId != 0ul && uniqueId != 0ul && pending.UniqueId != uniqueId)
        {
            return;
        }

        Refii.Models.Engine_MdlDisarticulation.DisarticulatedMeshProfile profile =
            Refii.Models.Engine_MdlDisarticulation.GetProfile(mesh);
        if (modelHash == 0u ||
            Refii.Models.Engine_MdlRig.TryFrom(mesh) is not { } crumbleRig ||
            !Refii.Core.Engine_TitleGame.Active.Rig.IsDisarticulatedCrumble(
                pending.SourceModelHash,
                modelHash,
                Refii.Level.Engine_PropModelIndex.Resolve(modelHash, System.Array.Empty<string>()),
                crumbleRig.Parents,
                profile.IsRigidPieceMesh) ||
            !_skeletons.TryGetValue(address, out Refii.Entities.Engine_EntitySkeleton? crumbledRig))
        {
            return;
        }

        crumbledRig.IsDisarticulated = true;
        Refii.Entities.Engine_ShadowSkeletons.SetEveryFrame(crumbledRig.Node, true);
        if (_restGlobalsScratch.Length < crumbledRig.BoneCount)
        {
            _restGlobalsScratch = new Transform3D[crumbledRig.BoneCount];
        }

        crumbledRig.CopyRestGlobals(_restGlobalsScratch);
        Transform3D entityWorld = node.IsInsideTree() ? node.GlobalTransform : pending.EntityWorld;
        var shatter = new Refii.Physics.Engine_DisarticulatedRig();
        shatter.Initialize(
            entityWorld,
            crumbledRig.BoneNames,
            new System.ReadOnlySpan<Transform3D>(_restGlobalsScratch, 0, crumbledRig.BoneCount),
            crumbledRig.Parents,
            pending.Bones,
            pending.InheritedVelocity,
            address ^ (uint)uniqueId,
            profile.Pieces);
        crumbledRig.ApplyModelSpacePoses(shatter.ModelPoses);
        _extentHeldStreak.Remove(address);
        Refii.Entities.Engine_CorpseManager.SyncDisarticulatedBounds(node, shatter, entityWorld, force: true);
        _disarticulated[address] = shatter;
        if (_deadCorpseState.TryGetValue(address, out DeadCorpseTrack? deadTrack))
        {
            deadTrack.SawDisarticulatedWhileDead = true;
            deadTrack.SawPosedWhileDead = true;
        }
    }

    private bool TryAutoStartRigidPieceCrumble(
        in HostBridge.GuestEntity entity,
        MeshInstance3D node,
        Mesh mesh,
        Refii.Entities.Engine_EntitySkeleton crumbledRig,
        Transform3D toWorld,
        out Refii.Physics.Engine_DisarticulatedRig shatter)
    {
        Refii.Models.Engine_MdlDisarticulation.DisarticulatedMeshProfile profile =
            Refii.Models.Engine_MdlDisarticulation.GetProfile(mesh);
        if (!profile.IsRigidPieceMesh)
        {
            shatter = null!;
            return false;
        }

        crumbledRig.IsDisarticulated = true;
        Refii.Entities.Engine_ShadowSkeletons.SetEveryFrame(crumbledRig.Node, true);
        if (_restGlobalsScratch.Length < crumbledRig.BoneCount)
        {
            _restGlobalsScratch = new Transform3D[crumbledRig.BoneCount];
        }

        crumbledRig.CopyRestGlobals(_restGlobalsScratch);
        Transform3D entityWorld = node.IsInsideTree()
            ? node.GlobalTransform
            : toWorld * ToGodot(entity.Position, entity.Rotation);
        crumbledRig.CaptureWorldBones(entityWorld, _capturedBonesScratch);

        shatter = new Refii.Physics.Engine_DisarticulatedRig();
        shatter.Initialize(
            entityWorld,
            crumbledRig.BoneNames,
            new System.ReadOnlySpan<Transform3D>(_restGlobalsScratch, 0, crumbledRig.BoneCount),
            crumbledRig.Parents,
            _capturedBonesScratch,
            ComputeInheritedVelocity(in entity),
            entity.Address ^ (uint)entity.UniqueId,
            profile.Pieces);
        _extentHeldStreak.Remove(entity.Address);
        Refii.Entities.Engine_CorpseManager.SyncDisarticulatedBounds(node, shatter, entityWorld, force: true);
        _disarticulated[entity.Address] = shatter;
        if (_deadCorpseState.TryGetValue(entity.Address, out DeadCorpseTrack? deadTrack))
        {
            deadTrack.SawDisarticulatedWhileDead = true;
            deadTrack.SawPosedWhileDead = true;
        }
        return true;
    }

    private bool TryAdoptRetiredCorpse(uint address)
    {
        if (_shuttingDown ||
            !Refii.Entities.Engine_CorpsePolicy.Enabled ||
            (HostBridge.IsInGame && !HostBridge.WorldIsActive()) ||
            !_deadCorpseState.TryGetValue(address, out DeadCorpseTrack? track) ||
            !_nodes.TryGetValue(address, out MeshInstance3D? node) ||
            !GodotObject.IsInstanceValid(node))
        {
            return false;
        }

        bool hasSkeleton = _skeletons.TryGetValue(address, out Refii.Entities.Engine_EntitySkeleton? rig) &&
            GodotObject.IsInstanceValid(rig.Node);
        _disarticulated.TryGetValue(address, out Refii.Physics.Engine_DisarticulatedRig? shatter);

        var candidate = new Refii.Entities.Engine_CorpsePolicy.CorpseCandidate(
            IsDeadCreature: true,
            GuestLeftCorpse: LeavesCorpse(track),
            HasSkeleton: hasSkeleton,
            SawVisibleWhileDead: track.SawVisibleWhileDead,
            SawEtherealWhileAlive: _sawEtherealAlive.Contains(address),
            IsPrincipal: IsPrincipal(address),
            IsCompanion: address == Refii.Core.Engine_TitleGame.Active.CompanionEntityAddress,
            RecordHash: track.RecordHash,
            ModelPath: track.ModelPath,
            DeathMode: track.DeathMode,
            ClassifiedFromGdb: track.ClassifiedFromGdb,
            SawPosedWhileDead: track.SawPosedWhileDead,
            SawRagdollWhileDead: track.SawRagdollWhileDead,
            SawDisarticulatedWhileDead: track.SawDisarticulatedWhileDead || shatter is not null,
            SawCollapsedPoseWhileDead: track.SawCollapsedPoseWhileDead);

        if (!Refii.Entities.Engine_CorpsePolicy.IsEligibleCorpse(in candidate))
        {
            return false;
        }

        Unbatch(address, node);
        _corpses.Adopt(node, rig, shatter, track.LastDeadLook, track.DeathAge);
        return true;
    }

    /// <summary>The unique id of the entity each node was built for (entity+0x14).</summary>
    private readonly Dictionary<uint, ulong> _nodeUid = new();

    /// <summary>Addresses the guest recycled under a drawn node, run total.</summary>
    public static long RecycledAddresses;

    private int _recycledReports;

    private const string InteriorChildName = "Interior";

    /// <summary>Scratch for <see cref="Retire"/> — reused, for the reason stated there.</summary>
    private readonly List<uint> _gone = new();

    private int _withModel;

    private int _noMesh;

    /// <summary>Entities waiting on a build that is running off this thread. Not a fault.</summary>
    private int _awaitingBuild;

    /// <summary>Entities whose model keeps coming back as a stub; not pending for the release.</summary>
    private int _awaitingStub;

    /// <summary>Entities with a built mesh still waiting for their node because the frame's creation budget was spent.</summary>
    private int _awaitingCreate;

    /// <summary>How many times each model's build has returned an empty stub.</summary>
    private readonly Dictionary<uint, int> _stubReturns = new();

    private const int StubGiveUp = 3;

    private const int StubRetryTicks = 300;

    /// <summary>
    /// Applies the region's current lighting to one live entity.
    ///
    /// Re-applied whenever the mesh changes, and re-applied for every entity when the region's
    /// time of day moves — the environment node's own refresh walks the region and these are
    /// not in it.
    /// </summary>
    private static void LightEntity(GeometryInstance3D node)
    {
        Refii.Lighting.Engine_LevelBakedLighting.ApplyInstanceLighting(
            node, Refii.Level.Engine_SkyEnvironmentNode.Current?.Lighting);

        // The cloth draws beside its body and is lit as its body is.
        if (Refii.Physics.Engine_ClothBody.Of(node) is { } cloth)
        {
            Refii.Lighting.Engine_LevelBakedLighting.ApplyInstanceLighting(
                cloth, Refii.Level.Engine_SkyEnvironmentNode.Current?.Lighting);
        }

        if (node.GetNodeOrNull<GeometryInstance3D>(InteriorChildName) is { } interior)
        {
            Refii.Lighting.Engine_LevelBakedLighting.ApplyInstanceLighting(
                interior, Refii.Level.Engine_SkyEnvironmentNode.Current?.Lighting);
        }
    }

    /// <summary>Probe-lit at the entity's current place: the region root is the sky node's
    /// parent, and the probe field is indexed in the region's own frame.</summary>
    private static void RelightEntity(Node3D node)
    {
        Refii.Level.Engine_SkyEnvironmentNode? sky = Refii.Level.Engine_SkyEnvironmentNode.Current;
        if (sky is null || sky.GetParent() is not Node3D root || node is not GeometryInstance3D geometry)
        {
            return;
        }

        Vector3 local = root.GlobalTransform.AffineInverse() * node.GlobalPosition;
        Refii.Lighting.Engine_LevelBakedLighting.LightEntityAt(geometry, local, sky.Lighting);
        if (Refii.Physics.Engine_ClothBody.Of(node) is { } cloth)
        {
            Refii.Lighting.Engine_LevelBakedLighting.LightEntityAt(cloth, local, sky.Lighting);
        }
        if (node.GetNodeOrNull<GeometryInstance3D>(InteriorChildName) is { } interior)
        {
            Refii.Lighting.Engine_LevelBakedLighting.LightEntityAt(interior, local, sky.Lighting);
        }
    }

    /// <summary>
    /// The mesh for a model hash, or null when it does not resolve or will not decode.
    ///
    /// Cached including the failures — a hash that resolves to nothing resolves to nothing
    /// every frame, and 1049 entities re-attempting a bank lookup per frame is not free.
    /// </summary>
    /// <summary>
    /// The assembled mesh for a composite character, or null when the entity is not one.
    ///
    /// Cached by the PART LIST, not by the entity, because a crowd is built from a small pool
    /// of heads and torsos — two villagers wearing the same three models share one mesh, and
    /// re-assembling per entity would rebuild the same surfaces dozens of times.
    ///
    /// The guest's SOURCE list is what the bridge returns. The component's other, built list is
    /// populated in its draw virtual, and this guest never draws; the count comes back only so
    /// the report can say whether that assumption held.
    /// </summary>
    /// <summary>
    /// Component 7's eye models for a SINGLE-MODEL entity, or empty. The bridge answers the
    /// eyes alone when there are no morph parts. Re-read on the reconcile cadence when the
    /// entity actually carries eyes; a static prop with no eye component keeps its empty array
    /// for the life of its unique id.
    /// </summary>
    private uint[] EyesOf(uint address, ulong uniqueId)
    {
        if (_eyes.TryGetValue(address, out (ulong UniqueId, uint[] Parts) known) &&
            known.UniqueId == uniqueId &&
            (known.Parts.Length == 0 || !Due(address, ReconcileTicks)))
        {
            return known.Parts;
        }

        uint[] eyes = HostBridge.GetEntityMorphParts(address, out _);
        _eyes[address] = (uniqueId, eyes);
        return eyes;
    }

    private readonly Dictionary<uint, (ulong UniqueId, uint[] Parts)> _eyes = new();

    /// <summary>
    /// The body hash followed by <see cref="EyesOf"/>'s, as one part list.
    /// </summary>
    /// <remarks>
    /// Cached per entity to avoid a per-frame allocation. `EyesOf` hands back the same instance
    /// between reconciles, so eye-array identity plus the model hash is the whole change test.
    /// </remarks>
    private uint[] BodyAndEyes(uint address, uint modelHash, uint[] eyes)
    {
        if (_bodyAndEyes.TryGetValue(address, out (uint Model, uint[] Eyes, uint[] Parts) known) &&
            known.Model == modelHash && ReferenceEquals(known.Eyes, eyes))
        {
            return known.Parts;
        }

        var parts = new uint[eyes.Length + 1];
        parts[0] = modelHash;
        eyes.CopyTo(parts, 1);
        _bodyAndEyes[address] = (modelHash, eyes, parts);
        return parts;
    }

    private readonly Dictionary<uint, (uint Model, uint[] Eyes, uint[] Parts)> _bodyAndEyes = new();

    private Mesh? ResolveComposite(uint address, out bool hasParts)
    {
        // The part list is re-read on the reconcile cadence, as the eyes are.
        uint[] parts;
        int built = 0;
        if (!Due(address, ReconcileTicks) && _compositeParts.TryGetValue(address, out uint[]? knownParts))
        {
            parts = knownParts;
        }
        else
        {
            parts = HostBridge.GetEntityMorphParts(address, out built);
            _compositeParts[address] = parts;
        }

        if (parts.Length == 0)
        {
            hasParts = false;
            return null;
        }

        hasParts = true;
        _builtParts += built;
        return ComposeParts(parts, TextureLayersOf(address), PartMorphsOf(address),
            address == _principals.One ? Engine_TitleGame.Active.ManualTan(1) :
            address == _principals.Two ? Engine_TitleGame.Active.ManualTan(2) : 0.5f) ??
            _nodeMesh.GetValueOrDefault(address);
    }

    /// <summary>
    /// The entity's morphed parts as the guest stated them (<c>sub_82A48B90</c>), re-read on the
    /// reconcile cadence; the same array is handed back until the statement moves.
    /// </summary>
    private HostBridge.GuestPartMorph[] PartMorphsOf(uint address)
    {
        if (address != _morphPreviewHero && !Due(address, ReconcileTicks) &&
            _partMorphs.TryGetValue(address, out (uint Generation, HostBridge.GuestPartMorph[] Parts) known))
        {
            return known.Parts;
        }

        bool cached = _partMorphs.TryGetValue(address, out known);
        HostBridge.GuestPartMorph[] parts = HostBridge.GetEntityPartMorphs(address, out uint generation,
            cached ? known.Generation : null, cached ? known.Parts : null);
        if (_partMorphs.TryGetValue(address, out known) && known.Generation == generation &&
            known.Parts.Length == parts.Length)
        {
            return known.Parts;
        }

        _partMorphs[address] = (generation, parts);
        return parts;
    }

    private readonly Dictionary<uint, (uint Generation, HostBridge.GuestPartMorph[] Parts)> _partMorphs = new();

    private uint _morphPreviewHero;
    private int _morphPreviewGeneration;
    private int _morphPreviewUntilTick;

    /// <summary>
    /// The texture composites the guest states for the entity (<c>sub_825B38B0</c>), re-read on
    /// the reconcile cadence; the same array is handed back until the statement moves.
    /// </summary>
    private HostBridge.GuestTextureComposite[] TextureLayersOf(uint address)
    {
        if (address != _morphPreviewHero && !Due(address, ReconcileTicks) &&
            _textureLayers.TryGetValue(address, out (uint Generation, HostBridge.GuestTextureComposite[] Composites) known))
        {
            return known.Composites;
        }

        HostBridge.GuestTextureComposite[] composites =
            HostBridge.GetEntityTextureLayers(address, out uint generation);
        if (_textureLayers.TryGetValue(address, out known) && known.Generation == generation &&
            known.Composites.Length == composites.Length)
        {
            return known.Composites;
        }

        _textureLayers[address] = (generation, composites);
        return composites;
    }

    private readonly Dictionary<uint, (uint Generation, HostBridge.GuestTextureComposite[] Composites)> _textureLayers = new();

    /// <summary>Composites built with at least one morphed part; run total.</summary>
    private int _compositesMorphed;

    /// <summary>
    /// The assembled mesh for a multi-mesh appearance component (id 5), or null.
    /// </summary>
    /// <remarks>
    /// Same assembly as a composite character and deliberately so — both are "N models drawn as
    /// one entity", and the cache keys on the part list, so a street of identical townhouses
    /// shares a single built mesh. What differs is only where the hashes come from.
    ///
    /// When a building carries both exterior and interior mesh records (<c>GetEntityInteriorMeshes</c>,
    /// <c>sub_828F9BA0</c> / <c>sub_828F9F90</c>), the exterior parts are composed onto the
    /// primary node (outdoor layer 1) and the interior parts onto the <see cref="InteriorChildName"/>
    /// child node (<see cref="Refii.Lighting.Engine_LightZones.IndoorMask"/>), so outdoor lights
    /// illuminate building facades and indoor lights never bleed through exterior walls.
    /// </remarks>
    private Mesh? ResolveMultiMesh(uint address, out bool hasParts)
    {
        uint[] extParts;
        uint[] intParts;
        int total = 0;
        if (!Due(address, ReconcileTicks) && _multiMeshSplit.TryGetValue(address, out (uint[] Exterior, uint[] Interior) known))
        {
            extParts = known.Exterior;
            intParts = known.Interior;
            // The records the last refresh stated, so the census counts cached frames too.
            total = extParts.Length + intParts.Length;
        }
        else
        {
            uint[] parts = HostBridge.GetEntityMeshParts(address, out total);
            uint[] interiors = parts.Length > 0 ? HostBridge.GetEntityInteriorMeshes(address) : System.Array.Empty<uint>();
            if (interiors.Length == 0)
            {
                extParts = parts;
                intParts = System.Array.Empty<uint>();
            }
            else
            {
                var extList = new List<uint>(parts.Length);
                var intList = new List<uint>(interiors.Length);
                for (int i = 0; i < parts.Length; i++)
                {
                    uint p = parts[i];
                    if (System.Array.IndexOf(interiors, p) >= 0)
                    {
                        intList.Add(p);
                    }
                    else
                    {
                        extList.Add(p);
                    }
                }
                extParts = extList.ToArray();
                intParts = intList.ToArray();
            }
            _meshParts[address] = parts;
            _multiMeshSplit[address] = (extParts, intParts);
        }

        if (extParts.Length == 0 && intParts.Length == 0)
        {
            _interiorMeshesPending.Remove(address);
            hasParts = false;
            return null;
        }

        hasParts = true;
        _multiMeshParts += total;

        if (extParts.Length == 0)
        {
            _interiorMeshesPending.Remove(address);
            return ComposeParts(intParts);
        }

        Mesh? extMesh = ComposeParts(extParts);
        if (intParts.Length == 0)
        {
            _interiorMeshesPending.Remove(address);
            return extMesh;
        }

        Mesh? intMesh = ComposeParts(intParts);
        if (extMesh is null || intMesh is null)
        {
            return null;
        }

        _interiorMeshesPending[address] = intMesh;
        return extMesh;
    }

    private readonly Dictionary<uint, (uint[] Exterior, uint[] Interior)> _multiMeshSplit = new();
    private readonly Dictionary<uint, Mesh> _interiorMeshesPending = new();
    private readonly Dictionary<uint, MeshInstance3D> _interiorNodes = new();

    private void SyncInteriorMesh(in HostBridge.GuestEntity entity, MeshInstance3D node, bool adopted)
    {
        if (!_interiorMeshesPending.TryGetValue(entity.Address, out Mesh? intMesh))
        {
            if (_interiorNodes.Remove(entity.Address, out MeshInstance3D? stale) && GodotObject.IsInstanceValid(stale))
            {
                Refii.Lighting.Engine_ShadowReach.Release(stale);
                stale.QueueFree();
            }
            return;
        }

        uint intLayers = InteriorEntityLayers(entity);
        if (!_interiorNodes.TryGetValue(entity.Address, out MeshInstance3D? intNode) || !GodotObject.IsInstanceValid(intNode))
        {
            intNode = new MeshInstance3D
            {
                Name = InteriorChildName,
                Mesh = intMesh,
                Layers = intLayers,
                GIMode = GeometryInstance3D.GIModeEnum.Dynamic,
            };
            node.AddChild(intNode);
            _interiorNodes[entity.Address] = intNode;
            LightEntity(intNode);
            Refii.Level.Engine_PropStreamer.ApplyVisibilityRange(intNode, intMesh);
            Refii.Lighting.Engine_ShadowReach.Adopt(intNode);
            if (!adopted)
            {
                Refii.Entities.Engine_HiddenClusters.Apply(intNode, entity.Address);
            }
            Refii.Entities.Engine_ShadowProxy.Sync(intNode);
            return;
        }

        if (intNode.Layers != intLayers)
        {
            intNode.Layers = intLayers;
            Refii.Entities.Engine_ShadowProxy.Sync(intNode);
        }

        if (!ReferenceEquals(intNode.Mesh, intMesh))
        {
            intNode.Mesh = intMesh;
            LightEntity(intNode);
            intNode.VisibilityRangeEnd = 0f;
            Refii.Level.Engine_PropStreamer.ApplyVisibilityRange(intNode, intMesh);
            Refii.Lighting.Engine_ShadowReach.Release(intNode);
            Refii.Lighting.Engine_ShadowReach.Adopt(intNode);
            if (!adopted)
            {
                Refii.Entities.Engine_HiddenClusters.Apply(intNode, entity.Address);
            }
            Refii.Entities.Engine_ShadowProxy.Sync(intNode);
        }
        else if (!adopted && Due(entity.Address, ReconcileTicks))
        {
            Refii.Entities.Engine_HiddenClusters.Apply(intNode, entity.Address);
            Refii.Entities.Engine_ShadowProxy.SyncHidden(intNode);
        }
    }

    /// <summary>Morph part lists per entity, re-read on the reconcile cadence.</summary>
    private readonly Dictionary<uint, uint[]> _compositeParts = new();

    /// <summary>Entities with no model, no composite parts and no mesh records, by unique id. See the walk.</summary>
    private readonly Dictionary<uint, ulong> _nothingToDraw = new();

    /// <summary>Prologues answered from <see cref="_nothingToDraw"/> this walk.</summary>
    private int _prologueSkipped;

    /// <summary>
    /// `entity_skip_index`: whether the walk answers unchanged entities from the last walk's skip
    /// verdicts (<see cref="Refii.Entities.Engine_EntitySkipIndex"/>) rather than re-running the
    /// lookups of the skip paths. Off by default; the debug channel's `render entitySkipIndex=`
    /// switches it live, and the next walk starts the index afresh.
    /// </summary>
    /// <remarks>
    /// The same entities are skipped, owned, drawn and counted either way. Every verdict is also
    /// re-asked on the entity's reconcile cadence (one walk in <see cref="ReconcileTicks"/>), so
    /// one an input outside the hooks undid would hold for half a second at worst.
    /// </remarks>
    public static bool SkipIndexEnabled = Refii.Core.Engine_RefiiConfig.Bool(
        Refii.Core.Engine_RefiiConfig.Read(out _), "entity_skip_index", false);

    /// <summary>
    /// `entity_still_reconcile_scale`: how many reconcile periods an entity answered from the skip
    /// index waits before it is re-checked against the game (1, the default, is every period;
    /// clamped to 1-8). Live through the config hook.
    /// </summary>
    /// <remarks>
    /// Only what the index answers: entities with no rig, unchanged since the last walk, not a
    /// principal. Anything the game states anew (a move, a look) still takes the full path at once;
    /// what waits longer is what only the reconcile reads, for scenery standing still: a garment or
    /// part list changed without a stated change, its light zone, its relight after the level's
    /// lighting changes. At 4 that is two seconds where one is now. Each reconcile of a still prop
    /// is the whole prologue and placement (guest reads among them), ~1 in 30 of ~4,000 props per
    /// walk in Bowerstone.
    /// </remarks>
    public static int StillReconcileScale = ReadStillReconcileScale();

    private static int ReadStillReconcileScale() =>
        System.Math.Clamp(Refii.Core.Engine_RefiiConfig.Int(
            Refii.Core.Engine_RefiiConfig.Read(out _), "entity_still_reconcile_scale", 1), 1, 8);

    /// <summary>The last walk's entities answered from the skip index, and the verdicts held after it.</summary>
    public static int SkipIndexHits;

    public static int SkipIndexHeld;

    private readonly Refii.Entities.Engine_EntitySkipIndex _skipIndex = new();

    /// <summary>The lever and the batching globals the index was filled under; a change clears it.</summary>
    private int _skipEpoch;

    /// <summary>The attachment table the index last undid children for.</summary>
    private int _skipAttachmentsVersion = -1;

    /// <summary>Entities the loop left alone because their statement had not moved; run total.</summary>
    public static long UnchangedSkipped => _unchangedSkippedTotal;

    private int _unchangedSkipped;
    private static long _unchangedSkippedTotal;

    /// <summary>The mesh this feed last assigned to each entity's node.</summary>
    private readonly Dictionary<uint, Mesh?> _nodeMesh = new();

    /// <summary>Never fewer than this many a frame, however slow they turn out to be.</summary>
    private const int MinCreationsPerFrame = 8;

    /// <summary>And never more, however cheap: a crowd arriving together is still a crowd.</summary>
    private const int MaxCreationsPerFrame = 128;

    /// <summary>How long a frame may spend building new entity nodes.</summary>
    private const double CreateMillisecondsPerFrame = 2.0;

    /// <summary>The same two limits while the world holds the guest behind its loading screen.</summary>
    private const int MaxCreationsPerLoadFrame = 2048;

    /// <remarks>Creation time only (see the budget check); the prop streamer takes 100 ms a held frame.</remarks>
    private const double CreateMillisecondsPerLoadFrame = 40.0;

    /// <summary>Set by the world while it holds the guest for the region build.</summary>
    public bool LoadHold { get; set; }

    /// <summary>The batches standing for still props; made on first use, a child of this feed.</summary>
    private Refii.Entities.Engine_EntityBatches? _batches;

    /// <summary>Consecutive walks each unrigged prop has gone unchanged.</summary>
    private readonly Dictionary<uint, int> _still = new();

    private readonly Dictionary<Mesh, bool> _meshHasCloth = new();

    private bool MeshHasCloth(Mesh mesh)
    {
        if (_meshHasCloth.TryGetValue(mesh, out bool cached))
        {
            return cached;
        }

        bool any = false;
        foreach (string path in PartPathsOf(mesh))
        {
            if (Refii.Physics.Engine_PhysicsIndex.HasCloth(path))
            {
                any = true;
                break;
            }
        }

        _meshHasCloth[mesh] = any;
        return any;
    }

    /// <summary>
    /// One more unchanged walk for a prop; at <see cref="Refii.Entities.Engine_EntityBatches.StillWalks"/>
    /// it is drawn from a batch and its node hidden. Only what is nothing but a prop: no rig, not
    /// married to a static twin (that node is hidden already), not highlighted, and only under the
    /// unified lighting model.
    /// </summary>
    /// <returns>
    /// True when it did nothing and will keep doing nothing while the prop, its node and the
    /// batching levers stay as they are: the skip index may answer the prop's next walks.
    /// </returns>
    private bool NoteStill(in HostBridge.GuestEntity entity, MeshInstance3D node, Mesh mesh, bool adopted)
    {
        // A target's silhouette is drawn from its node, and this branch is where an unchanged
        // prop that has just been targeted is seen.
        if (entity.Highlighted || !entity.Appearance.IsConstructed)
        {
            Unbatch(entity.Address, node);
            return false;
        }

        if (StaysAsIs(entity.Address, node, mesh, adopted) || mesh is not ArrayMesh shape)
        {
            return true;
        }

        int still = _still.GetValueOrDefault(entity.Address) + 1;
        _still[entity.Address] = still;
        if (still < Refii.Entities.Engine_EntityBatches.StillWalks ||
            !_written.TryGetValue(entity.Address, out (MeshInstance3D Node, Transform3D Drawn) placed) ||
            !ReferenceEquals(placed.Node, node))
        {
            return false;
        }

        if (_batches is null)
        {
            _batches = new Refii.Entities.Engine_EntityBatches { Name = Refii.Entities.Engine_EntityBatches.NodeName };
            AddChild(_batches);
        }

        uint layers = EntityLayers(entity);
        if (_batches.Promote(entity.Address, shape, layers, placed.Drawn,
            _drawRanges.GetValueOrDefault(entity.Address)))
        {
            node.Visible = false;
        }

        return false;
    }

    /// <summary>
    /// <see cref="NoteStill"/>'s answer for a constructed, unhighlighted prop that it leaves as it
    /// is (true), asked without its count: nothing to batch it into, or nothing it may be batched as.
    /// </summary>
    private bool StaysAsIs(uint address, MeshInstance3D node, Mesh mesh, bool adopted) =>
        adopted || !Refii.Entities.Engine_EntityBatches.Enabled ||
        !Refii.Lighting.Engine_LightingFrame.ModernModel || !_unrigged.Contains(address) ||
        mesh is not ArrayMesh || (_batches?.Contains(address) ?? false) ||
        _interiorNodes.ContainsKey(address) ||
        MeshHasCloth(mesh) || Refii.Physics.Engine_ClothBody.Of(node) is not null;

    /// <summary>
    /// Whether the verdict <see cref="Refii.Entities.Engine_EntitySkipIndex.TryFind"/> found answers this
    /// walk: off the cadence its kind's own path re-asks on (Engine_EntitySkipIndex.RecheckTicks), and, for the
    /// kinds Android holds (GameEntities.Android.cs), what their replay needs.
    /// </summary>
    private bool HeldThisWalk(uint address, Refii.Entities.Engine_EntitySkipIndex.Kind kind, int tag) =>
        Due(address, RecheckFor(kind, tag)) ? HeldDue(kind) : HoldsKind(address, kind, tag);

    /// <summary>
    /// At the end of the row of an unchanged entity without a rig that took the full path (its
    /// reconcile walk): parks the verdict its next walk's skip path would park, in that path's order
    /// (the empty path's, the still-and-adopted or batched one, the still node's), so the next walk
    /// is a hit rather than a repeat of that path. Nothing when the next walk would not skip.
    /// </summary>
    /// <remarks>
    /// EXACT: what the next walk's skip path reads is this row's result, and what could change it
    /// between here and there is what already undoes a verdict (the statement, the cadence, a rig
    /// built, a parent taken, the batching levers). The next walk is never due on the cadence that
    /// made this row take the full path, as no period is shorter than two walks.
    /// </remarks>
    private void ParkAsNextWalk(in HostBridge.GuestEntity entity, MeshInstance3D node, bool multiRoute)
    {
        uint address = entity.Address;
        if (entity.ModelHash == 0u && _nothingToDraw.ContainsKey(address))
        {
            return;
        }

        bool adopted = _adoptions.ContainsKey(address);
        if (adopted || (_batches?.Contains(address) ?? false))
        {
            _skipIndex.Park(address, entity.UniqueId,
                adopted ? Refii.Entities.Engine_EntitySkipIndex.Kind.Adopted : Refii.Entities.Engine_EntitySkipIndex.Kind.Batched,
                adopted ? 0u : entity.RecordHash, adopted ? null : ModelKeyFor(entity.ModelHash));
            ++_reparked;
            return;
        }

        if (!entity.Appearance.IsConstructed || entity.Highlighted ||
            _skeletons.ContainsKey(address) || _movers.Contains(address) ||
            !_nodes.TryGetValue(address, out MeshInstance3D? held) || !ReferenceEquals(held, node) ||
            !_nodeMesh.TryGetValue(address, out Mesh? mesh) || mesh is null ||
            !_lastLook.TryGetValue(address, out HostBridge.EntityAppearance look) || !look.IsConstructed ||
            !StaysAsIs(address, node, mesh, adopted: false))
        {
            return;
        }

        if (entity.ModelHash == 0u && _reports < 10)
        {
            // Its next walk takes the full prologue (the reports count it), which parks a building.
            if (multiRoute && HoldBuildings)
            {
                ParkHeldBuilding(entity);
                ++_reparked;
            }

            return;
        }

        _skipIndex.Park(address, entity.UniqueId, Refii.Entities.Engine_EntitySkipIndex.Kind.Still,
            entity.RecordHash, ModelKeyFor(entity.ModelHash));
        ++_reparked;
    }

    /// <summary>Back to a node, if it was batched; the still count starts again either way.</summary>
    private void Unbatch(uint address, MeshInstance3D node)
    {
        _still.Remove(address);
        if (_batches is not null && _batches.Demote(address) && GodotObject.IsInstanceValid(node))
        {
            node.Visible = true;
        }
    }

    /// <summary>Re-evaluates visibility ranges across all entity batches and unrigged dressing entity nodes.</summary>
    public void UpdateAllVisibilityRanges()
    {
        _batches?.UpdateVisibilityRanges();
        foreach (uint addr in _unrigged)
        {
            if (_nodes.TryGetValue(addr, out MeshInstance3D? node) && GodotObject.IsInstanceValid(node) && node.Mesh is not null)
            {
                Refii.AndroidRuntime.AndroidEntityProps.ApplyVisibilityRange(node, node.Mesh);
            }
        }
    }

    private void UnbatchAll()
    {
        foreach (KeyValuePair<uint, MeshInstance3D> pair in _nodes)
        {
            Unbatch(pair.Key, pair.Value);
        }
    }

    /// <summary>
    /// The players' heroes as the guest states them: the hero the vitals resolve, and keys 1 and 2
    /// of its player map. A PRINCIPAL is never given less for being far from the eye — full pose
    /// rate, re-posed every drawn frame, posed when off screen — and both players are principals
    /// alike: the co-op partner is as much the centre of attention as the local hero.
    /// </summary>
    private (uint Hero, uint One, uint Two, uint Dog) _principals;

    private bool IsHero(uint address) =>
        address != 0u && (address == _principals.Hero || address == _principals.One || address == _principals.Two);

    private bool IsPrincipal(uint address) =>
        address != 0u && (address == _principals.Hero || address == _principals.One || address == _principals.Two || address == _principals.Dog);

    private int _createdThisFrame;

    private long _createFrom;

    /// <summary>Ticks this frame's walk spent creating nodes, what the load budget counts.</summary>
    private long _createTicksThisFrame;
    private static long _creationsDeferred;

    /// <summary>Entities that waited a frame for their node because the frame's creations were spent; run total.</summary>
    public static long CreationsDeferred => _creationsDeferred;

    private static readonly StringName MetaEntityAddress = "entity_address";
    private static readonly StringName MetaEntityUniqueId = "entity_unique_id";
    private static readonly StringName MetaEntityRecord = "entity_record";
    private static readonly StringName MetaEntityModel = "entity_model";

    /// <summary>Entities lit from the probe field at least once; the rest is the reconcile cadence.</summary>
    private readonly HashSet<uint> _relit = new();
    private readonly Dictionary<uint, uint[]> _meshParts = new();

    /// <summary>Built meshes by the part list's FNV-1a hash, so the path resolve and join run once per list.</summary>
    private readonly Dictionary<ulong, Mesh?> _composedBySignature = new();
    private readonly Dictionary<ulong, string> _composedKeyBySignature = new();

    /// <summary>Resolves part hashes to paths and returns the cached assembly, queueing it once.</summary>
    private Mesh? ComposeParts(
        uint[] parts, HostBridge.GuestTextureComposite[]? layers = null,
        HostBridge.GuestPartMorph[]? stated = null, float tan = 0.5f)
    {
        ulong signature = 14695981039346656037UL;
        foreach (uint part in parts)
        {
            signature = (signature ^ part) * 1099511628211UL;
        }

        ulong statedHash = 14695981039346656037UL;
        if (stated is not null)
        {
            foreach (HostBridge.GuestPartMorph morph in stated)
            {
                statedHash = (statedHash ^ morph.BaseModel) * 1099511628211UL;
                foreach ((uint model, float weight) in morph.Targets)
                {
                    statedHash = (statedHash ^ model) * 1099511628211UL;
                    statedHash = (statedHash ^ System.BitConverter.SingleToUInt32Bits(weight)) * 1099511628211UL;
                }
            }

            signature = (signature ^ statedHash) * 1099511628211UL;
        }

        ulong layerHash = 14695981039346656037UL;
        if (layers is not null)
        {
            foreach (HostBridge.GuestTextureComposite composite in layers)
            {
                layerHash = (layerHash ^ composite.Original ^ (composite.Overlay ? 1u : 0u) ^ (composite.Wildcard ? 2u : 0u)) * 1099511628211UL;
                foreach (HostBridge.GuestTextureLayer layer in composite.Layers)
                {
                    layerHash = (layerHash ^ layer.Texture) * 1099511628211UL;
                    layerHash = (layerHash ^ (uint)layer.Mode) * 1099511628211UL;
                    layerHash = (layerHash ^ System.BitConverter.SingleToUInt32Bits(layer.Weight)) * 1099511628211UL;
                    layerHash = (layerHash ^ (layer.HasTint ? layer.Tint : 0u)) * 1099511628211UL;
                    layerHash = (layerHash ^ System.BitConverter.SingleToUInt32Bits(layer.Brightness)) * 1099511628211UL;
                    layerHash = (layerHash ^ layer.Bump) * 1099511628211UL;
                }
            }

            signature = (signature ^ layerHash) * 1099511628211UL;
        }

        if (tan != 0.5f)
            signature = (signature ^ System.BitConverter.SingleToUInt32Bits(tan)) * 1099511628211UL;

        if (_composedBySignature.TryGetValue(signature, out Mesh? known))
        {
            return known;
        }

        if (_composedKeyBySignature.TryGetValue(signature, out string? existingKey))
        {
            if (_composed.TryGetValue(existingKey, out Mesh? finished))
            {
                _composedBySignature[signature] = finished;
                return finished;
            }
            if (_compositeJobs.Contains(existingKey))
            {
                ++_awaitingBuild;
                return null;
            }
        }

        var paths = new List<string>(parts.Length);
        var hashes = new List<long>(parts.Length);
        foreach (uint part in parts)
        {
            if (Refii.Level.Engine_PropModelIndex.Resolve(
                    part, System.Array.Empty<string>()) is string path)
            {
                paths.Add(path);
                hashes.Add(part);
            }
            else
            {
                ++_compositePartsUnresolved;
            }
        }

        if (paths.Count == 0)
        {
            return null;
        }

        var key = new System.Text.StringBuilder(string.Join('|', paths));
        if (tan != 0.5f) key.Append("|tan").Append(System.BitConverter.SingleToUInt32Bits(tan).ToString("x8"));
        if (layers is not null && layers.Length != 0)
        {
            key.Append("|t").Append(layerHash.ToString("x16"));
        }

        Dictionary<uint, List<(string Path, float Weight)>>? targets = null;
        if (stated is not null && stated.Length != 0)
        {
            key.Append("|s").Append(statedHash.ToString("x16"));
            targets = new Dictionary<uint, List<(string Path, float Weight)>>();
            foreach (HostBridge.GuestPartMorph morph in stated)
            {
                var listed = new List<(string Path, float Weight)>(morph.Targets.Length);
                foreach ((uint model, float weight) in morph.Targets)
                {
                    if (Refii.Level.Engine_PropModelIndex.Resolve(
                            model, System.Array.Empty<string>()) is string target)
                    {
                        listed.Add((target, weight));
                    }
                    else
                    {
                        ++_compositePartsUnresolved;
                    }
                }

                targets[morph.BaseModel] = listed;
            }
        }

        string composedKey = key.ToString();
        _composedKeyBySignature[signature] = composedKey;
        if (_composed.TryGetValue(composedKey, out Mesh? cached))
        {
            _composedBySignature[signature] = cached;
            return cached;
        }

        // Not built yet. Queue it and draw nothing this frame; the completion stores it and the
        // next frame is a cache hit.
        ++_awaitingBuild;
        QueueCompositeBuild(composedKey, paths, hashes, layers, targets, tan);
        return null;
    }

    /// <summary>
    /// Resolves a model directly through <see cref="Refii.Level.Engine_PropStreamer.Resolve(string, out bool)"/>
    /// first so background entity workers share decoded meshes with the prop streamer and never hold
    /// Godot's internal <c>ResourceLoader</c> mutex while waiting on main-thread GPU uploads, falling
    /// back to <see cref="WorkerMeshLoads"/> when the bank loader must be queried directly.
    /// </summary>
    private static Mesh? LoadMesh(string path)
    {
        ArrayMesh? mesh = Refii.Level.Engine_PropStreamer.Resolve(path, out bool present);
        if (mesh is not null)
        {
            string fullPath = Refii.Models.Engine_BnkMeshLoader.PathPrefix + path;
            if (mesh.ResourcePath.Length == 0 && !ResourceLoader.HasCached(fullPath))
            {
                mesh.ResourcePath = fullPath;
            }
            return mesh;
        }

        if (present && Refii.Level.Engine_PropStreamer.IsHeaderOnly(path))
        {
            // Do NOT assign `ResourcePath` to transient empty stub meshes: multiple background entity
            // workers resolving the same header-only path (e.g. ESA_Rug_Worn_Large_V2.mdl) would
            // collide in Godot's global ResourceCache::set_path and log false cyclic-inclusion errors.
            return new ArrayMesh
            {
                ResourceName = path[(path.LastIndexOf('/') + 1)..],
            };
        }

        return null;
    }
    /// <summary>
    /// One part, blended with the targets the guest listed for it (<c>sub_82A48B90</c>).
    /// </summary>
    private static Mesh? LoadPart(string path, List<(string Path, float Weight)>? stated)
    {
        Mesh? part = LoadMesh(path);

        if (stated is null || part is not ArrayMesh own)
        {
            return part;
        }

        var listed = new List<(ArrayMesh Target, float Weight)>(stated.Count);
        foreach ((string target, float weight) in stated)
        {
            if (LoadMesh(target) is ArrayMesh mesh)
            {
                listed.Add((mesh, weight));
            }
        }

        return Refii.Hero.Engine_HeroMorph.Blend(own, listed);
    }

    /// <summary>One part loaded ahead of its assembly: the mesh, or what loading it threw.</summary>
    private readonly record struct PreloadedPart(
        Mesh? Mesh, System.Runtime.ExceptionServices.ExceptionDispatchInfo? Error);

    /// <summary>
    /// Every distinct part of one composite through <see cref="LoadPart"/>, side by side. The
    /// loads are independent (per-path locks in <see cref="LoadMesh"/>, the resource cache, a pure
    /// blend), so each result is the one the assembly's own serial ask would get; the assembly
    /// still consumes them in its own order. A throw is kept and rethrown where that ask was.
    /// </summary>
    private static Dictionary<string, PreloadedPart> PreloadParts(
        List<string> paths, System.Func<string, List<(string Path, float Weight)>?> stated)
    {
        var distinct = new List<string>(paths.Count);
        var seen = new HashSet<string>(System.StringComparer.Ordinal);
        foreach (string path in paths)
        {
            if (path.Length != 0 && seen.Add(path))
            {
                distinct.Add(path);
            }
        }

        var loaded = new PreloadedPart[distinct.Count];
        System.Threading.Tasks.Parallel.For(0, distinct.Count, i =>
        {
            try
            {
                loaded[i] = new PreloadedPart(LoadPart(distinct[i], stated(distinct[i])), null);
            }
            catch (System.Exception e)
            {
                loaded[i] = new PreloadedPart(null, System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(e));
            }
        });

        var byPath = new Dictionary<string, PreloadedPart>(distinct.Count, System.StringComparer.Ordinal);
        for (int i = 0; i < distinct.Count; i++)
        {
            byPath[distinct[i]] = loaded[i];
        }

        return byPath;
    }

    /// <summary>Builds one composite off this thread, once.</summary>
    private void QueueCompositeBuild(
        string key, List<string> paths, List<long> hashes,
        HostBridge.GuestTextureComposite[]? layers,
        Dictionary<uint, List<(string Path, float Weight)>>? targets, float tan)
    {
        // Already building, or the pipe is full and this retries next frame. NOT added to the
        // job set in the full case: leaving it out is what makes the retry possible.
        if (Refii.Core.Engine_RdHealth.IsDeviceLost || _retirementWorkers.Retired || _compositeJobs.Contains(key) || _inFlight >= InFlightLimit)
        {
            return;
        }

        _compositeJobs.Add(key);
        ++_inFlight;

        _retirementWorkers.TryQueue(() =>
        {
            ArrayMesh? assembled = null;
            int resolved = 0;
            int missing = 0;
            string? error = null;

            try
            {
                long assembleFrom = System.Diagnostics.Stopwatch.GetTimestamp();
                List<(string Path, float Weight)>? Stated(string path)
                {
                    int at = paths.IndexOf(path);
                    List<(string Path, float Weight)>? stated = null;
                    if (targets is not null && at >= 0)
                    {
                        targets.TryGetValue((uint)hashes[at], out stated);
                    }

                    return stated;
                }

                Dictionary<string, PreloadedPart> preloaded = PreloadParts(paths, Stated);
                assembled = Refii.Models.Engine_MdlComposite.Build(
                    paths,
                    path =>
                    {
                        // A path's first ask takes its preload; any later ask of the same path
                        // loads again, as every ask did before.
                        if (preloaded.Remove(path, out PreloadedPart part))
                        {
                            part.Error?.Throw();
                            return part.Mesh;
                        }

                        return LoadPart(path, Stated(path));
                    },
                    out resolved,
                    out missing);
                long textureFrom = System.Diagnostics.Stopwatch.GetTimestamp();

                if (assembled is not null && (tan != 0.5f || layers is { Length: > 0 }))
                {
                    Refii.Hero.Engine_HeroTextureMorph.ApplyTo(assembled,
                        layers ?? System.Array.Empty<HostBridge.GuestTextureComposite>(), tan);
                }

                Refii.Entities.Engine_LoadEntityBuilds.NoteComposite(
                    System.IO.Path.GetFileNameWithoutExtension(paths[0]) + "+" + (paths.Count - 1),
                    textureFrom - assembleFrom, System.Diagnostics.Stopwatch.GetTimestamp() - textureFrom);
            }
            catch (System.Exception e)
            {
                // CARRIED BACK, never left to the Task. An exception here does not stop the game:
                // it is stored in the Task and discarded unobserved, so the character would simply
                // never appear with nothing in the log to say why.
                // With where it was thrown: the message alone has named a fault and not its site.
                string[] trace = (e.StackTrace ?? string.Empty).Split('\n', System.StringSplitOptions.RemoveEmptyEntries);
                error = $"{e.GetType().Name}: {e.Message}" +
                    (trace.Length != 0 ? $" {trace[0].Trim()}" : string.Empty);
            }

            _retirementWorkers.TryPublish(() => _completions.Enqueue(() =>
            {
                _compositeJobs.Remove(key);
                --_inFlight;

                if (error is not null)
                {
                    GD.PushWarning($"[game-entities] composite build failed ({error}): {key}");

                    // A THROWN build is not an answer about the part list — the one seen was two
                    // workers racing on a shared table — so it is asked again, a bounded number of
                    // times; only then is the null cached like a list that does not assemble.
                    int failures = _compositeFailures.GetValueOrDefault(key) + 1;
                    _compositeFailures[key] = failures;
                    if (failures < CompositeBuildRetries)
                    {
                        return;
                    }
                }

                // Cached even when null, exactly as the synchronous path did: a part list whose
                // models do not assemble will not assemble on the next frame either.
                // The part hashes, aligned with the paths the assembly walked, so a surface's
                // part index resolves to the guest's built record (Engine_HiddenClusters).
                assembled?.SetMeta(Refii.Models.Engine_MdlComposite.PartHashesMeta, hashes.ToArray());
                _composed[key] = assembled;
                if (targets is not null)
                {
                    ++_compositesMorphed;
                }

                _compositePartsDrawn += resolved;
                _compositePartsMissing += missing;
            }));
        });
    }

    /// <summary>Thrown composite builds per part list, and how many are asked again before the null is cached.</summary>
    private readonly Dictionary<string, int> _compositeFailures = new();

    private const int CompositeBuildRetries = 3;

    /// <summary>Assembled composite meshes, keyed by their part list — a crowd shares parts.</summary>
    private readonly Dictionary<string, Mesh?> _composed = new();

    private int _composites;

    /// <summary>Entities drawn this tick from a multi-mesh appearance component (id 5).</summary>
    private int _multiMesh;

    /// <summary>Mesh records those components stated this tick.</summary>
    private int _multiMeshParts;

    private int _builtParts;

    private int _compositePartsDrawn;

    private int _compositePartsMissing;

    private int _compositePartsUnresolved;

    private Mesh? Resolve(uint modelHash)
    {
        if (_meshes.TryGetValue(modelHash, out Mesh? cached))
        {
            return cached;
        }

        // The index lookup stays here: it is a dictionary hit, and a hash that names no path is
        // an answer, not work. Cached as null so it is asked once.
        if (Refii.Level.Engine_PropModelIndex.Resolve(
                modelHash, System.Array.Empty<string>()) is not string path)
        {
            _meshes[modelHash] = null;
            return null;
        }

        // Fast-path: if the region's prop streamer already built and cached this model off-thread,
        // adopt the immutable ArrayMesh immediately on the main walk without spawning another job.
        if (Refii.Level.Engine_PropStreamer.TryPeek(path, out ArrayMesh? readyProp, out bool knownPropFailure) &&
            !knownPropFailure &&
            readyProp is not null &&
            readyProp.GetSurfaceCount() > 0 &&
            Refii.Models.Engine_ClassicMaterial.MeshSettled(readyProp))
        {
            string fullPath = Refii.Models.Engine_BnkMeshLoader.PathPrefix + path;
            if (readyProp.ResourcePath.Length == 0 && !ResourceLoader.HasCached(fullPath))
            {
                readyProp.ResourcePath = fullPath;
            }
            _meshes[modelHash] = readyProp;
            return readyProp;
        }

        QueueMeshBuild(modelHash, path);
        return null;
    }

    /// <summary>Loads one model off this thread, once.</summary>
    private void QueueMeshBuild(uint modelHash, string path)
    {
        if (Refii.Core.Engine_RdHealth.IsDeviceLost || _retirementWorkers.Retired || _meshJobs.Contains(modelHash) || _inFlight >= InFlightLimit)
        {
            return;
        }

        _meshJobs.Add(modelHash);
        ++_inFlight;

        _retirementWorkers.TryQueue(() =>
        {
            Mesh? mesh = null;
            string? error = null;

            try
            {
                long loadFrom = System.Diagnostics.Stopwatch.GetTimestamp();
                mesh = LoadMesh(path);
                Refii.Entities.Engine_LoadEntityBuilds.NoteMesh(
                    System.IO.Path.GetFileNameWithoutExtension(path), System.Diagnostics.Stopwatch.GetTimestamp() - loadFrom);
            }
            catch (System.Exception e)
            {
                error = $"{e.GetType().Name}: {e.Message}";
            }

            _retirementWorkers.TryPublish(() => _completions.Enqueue(() =>
            {
                _meshJobs.Remove(modelHash);
                --_inFlight;

                if (Refii.Core.Engine_RdHealth.IsDeviceLost)
                {
                    return;
                }

                if (error is not null)
                {
                    GD.PushWarning($"[game-entities] mesh load failed ({error}): {path}");
                    // A failed load is an answer: recorded so the entity is not re-queued every
                    // tick for the rest of the region, and so the release gate sees it settled.
                    _meshes[modelHash] = null;
                    return;
                }

                // A MESH WITH NO SURFACES IS NOT A MESH, and it is not null either.
                //
                // `Engine_BnkMeshLoader` hands back an empty `ArrayMesh` when a model is a stub —
                // a header whose body is in no mounted bank — because a loader that returns an
                // error makes Godot log a resource failure for something that is expected mid
                // level-change. The cost was paid here: the null check at the call site passed, a
                // node was created, and `Twins.Claim` hid the region's own copy for an entity
                // that draws nothing.
                //
                // NOT CACHED as a failure, and so not marked done either: the body pages in from
                // a streaming bank, so a later frame's ask can legitimately succeed. Only a
                // resolved, surfaced mesh is kept.
                if (mesh is not null && mesh.GetSurfaceCount() == 0)
                {
                    // Counted, so a stub that keeps coming back stops holding the level's
                    // release and re-asks on a slow cadence instead of every tick.
                    _stubReturns[modelHash] = _stubReturns.TryGetValue(modelHash, out int seen) ? seen + 1 : 1;
                    return;
                }

                _meshes[modelHash] = mesh;
            }));
        });
    }

    /// <summary>
    /// TELLS THE RENDERER HOW BIG THE CHARACTER ACTUALLY IS, so it stops culling live ones.
    ///
    /// A skinned <see cref="MeshInstance3D"/> is culled against its MESH's bounding box, and
    /// that box is the BIND pose — the extent of the character standing still in the shape the
    /// artist authored. Bones driven from outside, as these are, move the skin without the mesh
    /// resource ever hearing about it, so a character whose arm leaves the bind box has geometry
    /// outside the volume the renderer is testing. When the whole box leaves the frustum the
    /// character is dropped even though it is plainly on screen: it VANISHES.
    ///
    /// Nothing in our own bookkeeping can see that happen — the entity is still live, still
    /// drawn, still rigged, still posed, and every counter says so. That is why the vanishing
    /// showed up in the run and in none of the numbers.
    ///
    /// So state the extent rather than let it be inferred. The union of the mesh's own box and
    /// the live bone cloud is a real measurement of where this character is, not a guess, and it
    /// is only ever larger than what Godot would have assumed — a custom box that was too small
    /// would cull MORE, so the margin matters.
    /// </summary>
    private void StateVisibleExtent(uint address, MeshInstance3D node, Refii.Entities.Engine_EntitySkeleton rig)
    {
        // Disarticulated bone piles maintain exact capsule-endpoint bounds via
        // Engine_CorpseManager.SyncDisarticulatedBounds; do not overwrite them on the standing-rig cadence.
        if (_disarticulated.ContainsKey(address))
        {
            return;
        }

        // Not every frame. The box only has to bound the pose, not track it, and re-stating it
        // per character per frame is work the renderer does not need. Once a rig's stated box
        // has enclosed two consecutive checks without overflowing, its restate cadence drops tenfold.
        int streak = _extentHeldStreak.GetValueOrDefault(address);
        int cadence = streak >= 2 ? ExtentSettledRestateTicks : ExtentRestateTicks;
        if (!Due(address, cadence) || Refii.Entities.Engine_BodyVariants.SourceMesh(node) is not { } mesh)
        {
            return;
        }

        Aabb live = rig.Bounds(false);
        Aabb box = mesh.GetAabb().Merge(live).Grow(BoneSkinMargin);

        if (!box.Position.IsFinite() || !box.Size.IsFinite() || box.Size.Length() < 1e-3f)
        {
            return;  // a torn pose is one bad frame; do not bake it into the culling box
        }

        // Counted so the next run says whether this was the fault: a live cloud that fits inside
        // the mesh box means the renderer was never culling wrongly and the vanishing is
        // something else.
        if (!mesh.GetAabb().Encloses(live))
        {
            ++_extentOverflow;
        }

        // HOLD THE BOX WHILE IT STILL BOUNDS THE POSE. Godot queues a full instance update on
        // every custom-AABB write, changed or not, and on a caster that marks every paired
        // light's shadow dirty — so a restate on the proxy re-rendered every lantern map the
        // character stood in; spread over a crowd, that is a restate in every lamp's pool on
        // nearly every display frame. The recomputed box
        // differs by millimetres from the last one, so it is written only when the pose has
        // grown out of the stated box or shrunk well inside it; the stated box carries a
        // hysteresis margin so the next restates fit. A larger culling box costs nothing seen.
        Aabb held = node.CustomAabb;
        if (held.Size.Length() > 1e-3f && held.Encloses(box) && box.Volume >= held.Volume * ExtentShrinkAt)
        {
            ++_extentHeld;
            _extentHeldStreak[address] = streak + 1;
            return;
        }

        _extentHeldStreak[address] = 0;
        Aabb stated = box.Grow(ExtentHysteresis);
        ++_extentStated;
        Refii.Core.Engine_InstanceUpdates.Note(Refii.Core.Engine_InstanceUpdates.Source.EntityExtent);
        node.CustomAabb = stated;

        // The proxy too: without a stated box Godot rebuilds its bounds from the bones on every
        // guest-rate write and re-pairs it against every light it moves across.
        if (Refii.Entities.Engine_ShadowProxy.ProxyOf(node) is { } proxy)
        {
            Refii.Core.Engine_InstanceUpdates.Note(Refii.Core.Engine_InstanceUpdates.Source.EntityExtent);
            proxy.CustomAabb = stated;
        }
    }

    private readonly Dictionary<uint, int> _extentHeldStreak = new();
    private const int ExtentSettledRestateTicks = 300;

    /// <summary>Restates answered by the box already stated; see StateVisibleExtent.</summary>
    private int _extentHeld;

    /// <summary>Metres the stated box is grown past the pose, so a walk cycle stays inside it.</summary>
    private const float ExtentHysteresis = 0.15f;

    /// <summary>A pose box under this share of the stated box's volume restates it smaller.</summary>
    private const float ExtentShrinkAt = 0.5f;

    /// <summary>
    /// How far the skin can hang off the bones. A bone is a point; the flesh around it is not,
    /// and the bone cloud alone would still cut a shoulder off at the joint.
    /// </summary>
    private const float BoneSkinMargin = 0.35f;

    /// <summary>Half a second at 60fps. The box bounds the pose; it need not track it.</summary>
    private const int ExtentRestateTicks = 30;

    private int _extentStated;

    private int _extentOverflow;

    /// <summary>
    /// DOES A CHARACTER FACE THE WAY IT WALKS?
    ///
    /// The conversion chain cannot answer whether the facing is RIGHT, only whether it is
    /// applied consistently — and it is: the rendered forward works out to
    /// <c>N·R·M⁻¹ · M(0,1,0) = N(guest forward)</c> algebraically, so the maths will always
    /// agree with itself no matter how wrong the input is. Settling it needs a reference the
    /// pipeline does not produce.
    ///
    /// Walking is that reference. People walk forwards, so over any distance the direction of
    /// travel IS the facing, and it is measured from positions alone — no rotation, no basis,
    /// no convention. If the two agree the facing is right and the fault is elsewhere; if they
    /// sit at a constant offset, that offset is the bug and its VALUE names the fix: 180° is a
    /// forward/back flip, ±90° is an axis convention, and a uniform spread is the facing not
    /// tracking at all.
    ///
    /// The model's own forward is +Y, measured from the `.mdl` rather than assumed:
    /// `Carry_SheathWeaponFront_DummyObject` sits at y=+0.1116 and `...Back...` at y=-0.1023 on
    /// the same spine bone. In mesh space that is <c>M(0,1,0) = (0, 0, -1)</c>.
    /// </summary>
    private void SampleFacing(uint address, MeshInstance3D node, Quaternion rotation)
    {
        Vector3 here = node.Transform.Origin;
        if (!_lastPosition.TryGetValue(address, out Vector3 last))
        {
            _lastPosition[address] = here;
            return;
        }

        _lastPosition[address] = here;

        // DID THIS ENTITY TELEPORT? The measurement that separates a torn read from a rate.
        //
        // We sample guest memory from Godot's thread while the guest simulates on its own,
        // unlocked, so a read can splice bytes from either side of a write. A torn float is
        // usually enormous, which puts the character out of the frustum for one frame and reads
        // as it VANISHING — the same symptom the culling box also produces, from a completely
        // different cause, which is why guessing between them was never going to work.
        //
        // A walking character covers ~0.025 per frame. Anything past this is a teleport: either
        // a genuine one (a door, a cutscene — rare, and they exist) or a torn read. If the count
        // is zero the reads are coherent and the jitter is purely the two clocks beating against
        // each other, which needs interpolation and NOT locking. If it is large, the reads
        // themselves are the fault and interpolating them would only smear the garbage.
        float jump = (here - last).Length();
        if (jump > TeleportThreshold)
        {
            ++_positionJumps;
            if (jump > _worstJump)
            {
                _worstJump = jump;
            }
        }

        ++_positionSamples;

        // ONLY A CHARACTER THAT IS ACTUALLY WALKING CAN TESTIFY, and the threshold decides
        // whether that is true. The first version used 0.004 per frame; a person walking at
        // ~1.4 m/s at 60fps covers 0.023, so that admitted samples six times smaller than a
        // stride — standing jitter, turning on the spot, physics settling — and every one of
        // those has a travel direction that means nothing. Half the first run's samples landed
        // off-axis and it was impossible to say whether that was a real defect or the noise
        // floor. A walker moves at least this far.
        Vector3 travel = here - last;
        travel.Y = 0f;
        if (travel.Length() < 0.015f)
        {
            return;
        }

        // KEPT RAW as well as flattened. Zeroing Y before printing would hide the one case the
        // numbers most need to distinguish — a basis that sends the model's forward somewhere
        // VERTICAL, whose horizontal remainder is then whatever noise is left over.
        Vector3 rawForward = node.Transform.Basis * new Vector3(0f, 0f, -1f);
        Vector3 forward = rawForward;
        forward.Y = 0f;
        if (forward.Length() < 1e-4f)
        {
            return;
        }

        float degrees = Mathf.RadToDeg(
            travel.Normalized().SignedAngleTo(forward.Normalized(), Vector3.Up));

        ++_facingSamples;
        _facingSum += degrees;

        // Bucketed as well as averaged, because a mean of zero is produced BOTH by a facing
        // that is correct and by one that is uniformly wrong in both directions. The buckets
        // tell those apart.
        int bucket = (int)Mathf.Round(Mathf.PosMod(degrees, 360f) / 90f) % 4;
        ++_facingBuckets[bucket];

        // SPLIT BY KIND, because the guest has more than one orientation source and a mixture
        // averages into nonsense. `sub_82211110` tries the Havok phantom first, then the
        // component's own cache at +0xE0, then the delegate chain — and a previous defect in
        // exactly this area had chickens turning correctly while every human faced one way. If
        // one kind is clean and the other is not, that names the source; if both look the same,
        // the fault is common to all of them and the source is not the question.
        //
        // Composite characters are the ones assembled from morph parts and carry no mesh
        // resource name, which is the cheapest discriminator available here and happens to
        // separate the humans from the animals and props almost exactly.
        bool composite = Refii.Entities.Engine_BodyVariants.SourceMesh(node) is not ArrayMesh named || named.ResourceName.Length == 0;
        int[] buckets = composite ? _facingBucketsComposite : _facingBucketsNamed;
        ++buckets[bucket];

        // THE RAW PAIRS, because a histogram cannot name a RELATIONSHIP.
        //
        // 21% on target with 43% at 180° and 28% at 90° is not a fixed convention error — a
        // constant offset would pile into one bucket. A spread like that is what an
        // input-DEPENDENT error looks like, and the candidates are distinguishable only by
        // seeing the two angles side by side:
        //
        //   facing ==  travel          -> right
        //   facing == -travel          -> the rotation is INVERTED (a transposed basis)
        //   facing ==  travel + k*90   -> an axis convention, and k names it
        //   facing ==  2*travel        -> the rotation is applied twice
        //   no relation at all         -> we are reading something that is not the facing
        //
        // A dozen pairs settle which. Capped, because this is a work-list and not telemetry.
        if (_facingPairs < FacingPairLimit)
        {
            ++_facingPairs;
            float travelYaw = Mathf.RadToDeg(
                Vector3.Forward.SignedAngleTo(travel.Normalized(), Vector3.Up));
            float facingYaw = Mathf.RadToDeg(
                Vector3.Forward.SignedAngleTo(forward.Normalized(), Vector3.Up));
            // THE INPUT AS WELL AS THE OUTPUT. `facing` came back as exactly 0.0° on every
            // sample, which no real rotation can produce through this basis — even identity
            // renders 180° — so either the quaternion is degenerate or my reading of the basis
            // is wrong. Printing the quaternion, its length and the forward vector settles
            // which without another round of algebra: a zero/near-zero |q| is the degenerate
            // branch, a unit |q| with a constant forward means the basis is at fault.
            float length = Mathf.Sqrt((rotation.X * rotation.X) + (rotation.Y * rotation.Y) +
                (rotation.Z * rotation.Z) + (rotation.W * rotation.W));

            GD.Print($"    [facing] 0x{address:X8} travel={travelYaw,7:0.#}°  " +
                $"facing={facingYaw,7:0.#}°  diff={degrees,7:0.#}°  " +
                $"q=({rotation.X:0.###},{rotation.Y:0.###},{rotation.Z:0.###},{rotation.W:0.###}) " +
                $"|q|={length:0.###}  " +
                $"fwd=({rawForward.X:0.##},{rawForward.Y:0.##},{rawForward.Z:0.##})  " +
                $"{(composite ? "composite" : "single")}");
        }
    }

    /// <summary>
    /// Samples the guest's own simulation clock against the frame we are about to draw.
    ///
    /// THE JITTER QUESTION, MEASURED RATHER THAN ARGUED. The guest advances on a clock we do
    /// not drive — `sub_82276D10` schedules a fixed 30 Hz step against the wall clock, with a
    /// game tick every second one — and Godot presents on vsync. Two unlocked clocks means a
    /// state is shown for a VARYING number of frames even when every individual sample is
    /// perfectly correct, and that unevenness is what a character walking in a straight line
    /// looks like when it stutters.
    ///
    /// WHAT THIS COUNTS, PRECISELY, BECAUSE IT IS NOT THE POSE RATE. `dword_83319650` is
    /// bumped only by `sub_821D4628` and `sub_823D9898`, and `sub_82275F68` under them divides
    /// its delta by 15.0 — so this is the 15 Hz GAME tick. Bone poses are driven from the
    /// other world-advance path, `sub_822AF5D8` -> `sub_82278C20` -> `sub_8220D1B8` ->
    /// `sub_82204FF0`, which runs at 30 Hz and never touches this counter. So the histogram
    /// below describes the game tick against the display, NOT animation judder, and reading it
    /// as the latter would be the same mistake as comparing a raw count to a deduplicated one.
    ///
    /// It is still worth having: it is the only direct statement of whether the guest is
    /// keeping its intended schedule at all, and a guest that has fallen off 15 Hz is a
    /// different problem from one whose clock merely disagrees with ours.
    ///
    /// Deliberately a run total: the beat is a property of the two rates and takes seconds of
    /// frames to show up, so a per-frame reset would report noise.
    /// </summary>



    /// <summary>The transform last written to each entity's node, with the node it went to.</summary>
    private readonly Dictionary<uint, (MeshInstance3D Node, Transform3D Drawn)> _written = new();

    /// <summary>
    /// Writes a node's transform only when it differs (IsEqualApprox) from the last one written
    /// to that same node. A Node3D transform set crosses into Godot and re-marks the subtree
    /// dirty whether or not the value changed, and nearly every entity is still. True when
    /// written.
    /// </summary>
    private bool Place(uint address, MeshInstance3D node, Transform3D drawn)
    {
        if (_written.TryGetValue(address, out (MeshInstance3D Node, Transform3D Drawn) last) &&
            ReferenceEquals(last.Node, node) && last.Drawn.IsEqualApprox(drawn))
        {
            return false;
        }

        Refii.Core.Engine_InstanceUpdates.Note(Refii.Core.Engine_InstanceUpdates.Source.EntityTransform);
        node.Transform = drawn;
        _written[address] = (node, drawn);
        // The body's shadow proxy stands beside it and follows at the guest's rate.
        Refii.Entities.Engine_ShadowSkeletons.Placed(node);
        return true;
    }

    /// <summary>The hero's newest stated position in Godot space; null before the first walk.</summary>
    public static Vector3? HeroPosition { get; private set; }

    /// <summary>
    /// Player two's newest stated position in Godot space (the co-op partner, local or online);
    /// null without one. The other principal's counterpart of <see cref="HeroPosition"/>.
    /// </summary>
    public static Vector3? PlayerTwoPosition { get; private set; }

    /// <summary>
    /// The light zone player two's view stands in: player two's own on split screen, else the
    /// hero's (one view). Read once a guest frame.
    /// </summary>
    public static uint SecondViewZone { get; private set; } = Refii.Models.Engine_ClassicZones.Outdoors;

    /// <summary>The same position in the GUEST's own space, for readers that reproduce the guest's
    /// arithmetic on it — the world map's minimap among them.</summary>
    public static Vector3? HeroGuestPosition { get; private set; }

    /// <summary>The hero's stated rotation in the guest's space, beside the position.</summary>
    public static Quaternion? HeroGuestRotation { get; private set; }

    /// <summary>Active dynamic character positions from the last walk that can cast dynamic shadows.</summary>
    private static readonly List<Vector3> _dynamicCasterPositions = new();

    /// <summary>Active dynamic character guest addresses from the last walk for display-rate volumetric capsule snapshots.</summary>
    private static readonly List<uint> _dynamicCasterAddresses = new();

    /// <summary>Cached local AABBs by Mesh instance ID so display-rate capsule snapshots avoid repeated C++ GetAabb() calls.</summary>
    private readonly Dictionary<ulong, Aabb> _meshAabbCache = new();

    /// <summary>Cached local AABBs by guest entity address and node reference so display-rate capsule snapshots avoid per-frame C++ GetMesh() interop.</summary>
    private readonly Dictionary<uint, (MeshInstance3D Node, Aabb Box)> _entityAabbCache = new();

    /// <summary>
    /// Emits up to <paramref name="dst"/>.Length world-space soft volumetric shadow capsules for
    /// visible dynamic characters within <paramref name="maxDistance"/> of <paramref name="eye"/>,
    /// reading display-rate interpolated transforms from <c>_written</c> with zero per-entity C++ interop.
    /// </summary>
    public static int SnapshotVolumetricCasters(
        Vector3 eye,
        float maxDistance,
        System.Span<Refii.Lighting.Engine_VolumetricLightShafts.VolumetricCapsule> dst)
    {
        if (dst.IsEmpty || maxDistance <= 0f)
        {
            return 0;
        }

        GameEntities? self = Current;
        if (self is null || !GodotObject.IsInstanceValid(self) || !self.Visible)
        {
            if (HeroPosition.HasValue && HeroPosition.Value.DistanceSquaredTo(eye) <= maxDistance * maxDistance)
            {
                Vector3 h = HeroPosition.Value;
                dst[0] = new Refii.Lighting.Engine_VolumetricLightShafts.VolumetricCapsule(
                    SegA: h + new Vector3(0f, 0.30f, 0f),
                    Radius: 0.28f,
                    SegB: h + new Vector3(0f, 1.65f, 0f),
                    Feather: 0.14f);
                return 1;
            }

            return 0;
        }

        return self.SnapshotVolumetricCastersCore(eye, maxDistance, dst);
    }

    private int SnapshotVolumetricCastersCore(
        Vector3 eye,
        float maxDistance,
        System.Span<Refii.Lighting.Engine_VolumetricLightShafts.VolumetricCapsule> dst)
    {
        Transform3D toWorld = IsInsideTree() ? GlobalTransform : Transform;
        float maxDistSq = maxDistance * maxDistance;
        int outCount = 0;

        // Pass 0: Principals first (Hero, PlayerTwo, Dog) so they always claim capsule slots.
        // Pass 1: Remaining visible NPCs/creatures from _dynamicCasterAddresses.
        for (int pass = 0; pass < 2 && outCount < dst.Length; pass++)
        {
            for (int i = 0; i < _dynamicCasterAddresses.Count && outCount < dst.Length; i++)
            {
                uint addr = _dynamicCasterAddresses[i];
                bool isHeroOrP2 = addr != 0u && (addr == _principals.Hero || addr == _principals.One || addr == _principals.Two);
                bool isDog = addr != 0u && addr == _principals.Dog;
                bool isPrincipal = isHeroOrP2 || isDog;
                if ((pass == 0) != isPrincipal)
                {
                    continue;
                }

                if (!_written.TryGetValue(addr, out (MeshInstance3D Node, Transform3D Drawn) written))
                {
                    continue;
                }

                Transform3D world = toWorld * written.Drawn;
                Vector3 origin = world.Origin;
                if (!origin.IsFinite() || origin.DistanceSquaredTo(eye) > maxDistSq)
                {
                    continue;
                }

                Vector3 scale = written.Drawn.Basis.Scale.Abs();
                Aabb localBox;
                if (_entityAabbCache.TryGetValue(addr, out (MeshInstance3D Node, Aabb Box) cached) &&
                    ReferenceEquals(cached.Node, written.Node))
                {
                    localBox = cached.Box;
                }
                else
                {
                    localBox = new Aabb(new Vector3(-0.3f, 0f, -0.3f), new Vector3(0.6f, 1.8f, 0.6f));
                    if (GodotObject.IsInstanceValid(written.Node) && written.Node.Mesh is { } mesh)
                    {
                        ulong meshId = mesh.GetInstanceId();
                        if (!_meshAabbCache.TryGetValue(meshId, out localBox))
                        {
                            localBox = mesh.GetAabb();
                            if (!localBox.Position.IsFinite() || !localBox.Size.IsFinite() || localBox.Size.LengthSquared() < 1e-4f)
                            {
                                localBox = new Aabb(new Vector3(-0.3f, 0f, -0.3f), new Vector3(0.6f, 1.8f, 0.6f));
                            }

                            _meshAabbCache[meshId] = localBox;
                        }
                    }

                    _entityAabbCache[addr] = (written.Node, localBox);
                }

                Vector3 size = (localBox.Size * scale).Abs();
                float height = System.Math.Clamp(localBox.End.Y * scale.Y, 0.35f, 5.5f);
                Vector3 up = world.Basis.Y.Normalized();
                Vector3 right = world.Basis.X.Normalized();
                Vector3 forward = world.Basis.Z.Normalized();

                if (isHeroOrP2)
                {
                    // Articulated torso+head capsule plus lower-body stride volume.
                    float rTorso = System.Math.Clamp(System.MathF.Min(size.X, size.Z) * 0.28f, 0.22f, 0.38f);
                    dst[outCount++] = new Refii.Lighting.Engine_VolumetricLightShafts.VolumetricCapsule(
                        SegA: origin + (up * (height * 0.36f)),
                        Radius: rTorso,
                        SegB: origin + (up * System.MathF.Max(height - 0.14f, 0.75f)),
                        Feather: rTorso * 0.48f);

                    if (outCount + 1 < dst.Length)
                    {
                        float rLeg = rTorso * 0.58f;
                        Vector3 hipOffset = right * (rTorso * 0.42f);
                        dst[outCount++] = new Refii.Lighting.Engine_VolumetricLightShafts.VolumetricCapsule(
                            SegA: origin + hipOffset + (up * 0.12f),
                            Radius: rLeg,
                            SegB: origin + hipOffset + (up * (height * 0.48f)),
                            Feather: rLeg * 0.50f);
                        dst[outCount++] = new Refii.Lighting.Engine_VolumetricLightShafts.VolumetricCapsule(
                            SegA: origin - hipOffset + (up * 0.12f),
                            Radius: rLeg,
                            SegB: origin - hipOffset + (up * (height * 0.48f)),
                            Feather: rLeg * 0.50f);
                    }
                }
                else if (isDog || size.Z > size.Y * 1.18f)
                {
                    // Quadruped horizontal spine capsule.
                    float bodyRadius = System.Math.Clamp(height * 0.32f, 0.16f, 0.48f);
                    float halfLen = System.Math.Clamp(size.Z * 0.34f, 0.20f, 1.20f);
                    Vector3 center = origin + (up * (height * 0.55f));
                    dst[outCount++] = new Refii.Lighting.Engine_VolumetricLightShafts.VolumetricCapsule(
                        SegA: center - (forward * halfLen),
                        Radius: bodyRadius,
                        SegB: center + (forward * halfLen),
                        Feather: bodyRadius * 0.50f);
                }
                else
                {
                    // Biped NPC / creature vertical body capsule.
                    float rBody = System.Math.Clamp(System.MathF.Min(size.X, size.Z) * 0.30f, 0.20f, 0.65f);
                    dst[outCount++] = new Refii.Lighting.Engine_VolumetricLightShafts.VolumetricCapsule(
                        SegA: origin + (up * (rBody * 0.85f)),
                        Radius: rBody,
                        SegB: origin + (up * System.MathF.Max(height - (rBody * 0.50f), rBody + 0.20f)),
                        Feather: rBody * 0.48f);
                }
            }
        }

        if (outCount == 0 && HeroPosition.HasValue && HeroPosition.Value.DistanceSquaredTo(eye) <= maxDistSq)
        {
            Vector3 h = HeroPosition.Value;
            dst[outCount++] = new Refii.Lighting.Engine_VolumetricLightShafts.VolumetricCapsule(
                SegA: h + new Vector3(0f, 0.30f, 0f),
                Radius: 0.28f,
                SegB: h + new Vector3(0f, 1.65f, 0f),
                Feather: 0.14f);
        }

        return outCount;
    }

    /// <summary>
    /// Whether any dynamic character caster (hero, companion dog, moving/visible NPC) is within
    /// reach (<paramref name="radius"/> + 1.5m) of the given position. Used by the shadow budget to avoid
    /// allocating dynamic shadow passes for lights illuminating empty areas.
    /// </summary>
    public static bool HasDynamicCasterNear(Vector3 position, float radius)
    {
        float reach = radius + 1.5f;
        float reachSq = reach * reach;
        if (HeroPosition.HasValue && position.DistanceSquaredTo(HeroPosition.Value) <= reachSq)
        {
            return true;
        }

        // Fallback if no entities walked yet (e.g. headless tests)
        if (_dynamicCasterPositions.Count == 0 && !HeroPosition.HasValue)
        {
            return true;
        }

        for (int i = 0; i < _dynamicCasterPositions.Count; i++)
        {
            if (position.DistanceSquaredTo(_dynamicCasterPositions[i]) <= reachSq)
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Copies up to <paramref name="dst"/>.Length world-space character samples (stable guest entity ID,
    /// world position, and drawn horizontal forward vector) within <paramref name="maxDistance"/> of
    /// <paramref name="eye"/> for interactive water wakes with zero heap allocations or C++ interop.
    /// </summary>
    public static int CopyNearbyWaders(Vector3 eye, float maxDistance, System.Span<Refii.Level.WaderSample> dst)
    {
        if (dst.IsEmpty || maxDistance <= 0f)
        {
            return 0;
        }

        float maxSq = maxDistance * maxDistance;
        int count = 0;

        static void TryAdd(
            Refii.Level.WaderSample candidate,
            Vector3 eyePos,
            float maxDistSq,
            System.Span<Refii.Level.WaderSample> outSpan,
            ref int outCount,
            float minXzSepSq)
        {
            Vector3 pos = candidate.Position;
            if (outCount >= outSpan.Length || !pos.IsFinite() || pos.DistanceSquaredTo(eyePos) > maxDistSq)
            {
                return;
            }

            for (int k = 0; k < outCount; k++)
            {
                if (candidate.Id != 0u && outSpan[k].Id == candidate.Id)
                {
                    return;
                }

                float dx = outSpan[k].Position.X - pos.X;
                float dz = outSpan[k].Position.Z - pos.Z;
                if (((dx * dx) + (dz * dz)) < minXzSepSq)
                {
                    return;
                }
            }

            outSpan[outCount++] = candidate;
        }

        GameEntities? self = Current;
        if (self is not null && GodotObject.IsInstanceValid(self) && self.Visible && self._written.Count > 0)
        {
            Transform3D toWorld = self.IsInsideTree() ? self.GlobalTransform : self.Transform;

            static void TryAddWritten(
                uint addr,
                GameEntities entities,
                in Transform3D worldXform,
                Vector3 eyePos,
                float maxDistSq,
                System.Span<Refii.Level.WaderSample> outSpan,
                ref int outCount)
            {
                if (addr != 0u && entities._written.TryGetValue(addr, out (MeshInstance3D Node, Transform3D Drawn) written))
                {
                    Transform3D world = worldXform * written.Drawn;
                    Vector3 fwd3 = -world.Basis.Column2;
                    var fwdXz = new Vector2(fwd3.X, fwd3.Z);
                    if (!fwdXz.IsFinite() || fwdXz.LengthSquared() < 1e-6f)
                    {
                        fwdXz = Vector2.Zero;
                    }
                    else
                    {
                        fwdXz = fwdXz.Normalized();
                    }

                    TryAdd(new Refii.Level.WaderSample(addr, world.Origin, fwdXz), eyePos, maxDistSq, outSpan, ref outCount, 0.09f);
                }
            }

            TryAddWritten(self._principals.Hero, self, toWorld, eye, maxSq, dst, ref count);
            TryAddWritten(self._principals.One, self, toWorld, eye, maxSq, dst, ref count);
            TryAddWritten(self._principals.Two, self, toWorld, eye, maxSq, dst, ref count);
            TryAddWritten(self._principals.Dog, self, toWorld, eye, maxSq, dst, ref count);

            for (int i = 0; i < _dynamicCasterAddresses.Count && count < dst.Length; i++)
            {
                TryAddWritten(_dynamicCasterAddresses[i], self, toWorld, eye, maxSq, dst, ref count);
            }
        }

        if (HeroPosition is { } hero)
        {
            Vector2 heroFwd = Vector2.Zero;
            if (HeroGuestRotation is { } q)
            {
                Vector3 fwd3 = -ToGodot(Vector3.Zero, q).Basis.Column2;
                var rawXz = new Vector2(fwd3.X, fwd3.Z);
                if (rawXz.IsFinite() && rawXz.LengthSquared() > 1e-6f)
                {
                    heroFwd = rawXz.Normalized();
                }
            }

            uint heroId = self?._principals.Hero ?? 1u;
            TryAdd(new Refii.Level.WaderSample(heroId, hero, heroFwd), eye, maxSq, dst, ref count, 0.36f);
        }

        if (PlayerTwoPosition is { } p2)
        {
            uint p2Id = self?._principals.Two ?? 2u;
            TryAdd(new Refii.Level.WaderSample(p2Id, p2, Vector2.Zero), eye, maxSq, dst, ref count, 0.36f);
        }

        return count;
    }

    /// <summary>
    /// Copies up to <paramref name="dst"/>.Length world-space character origins within
    /// <paramref name="maxDistance"/> of <paramref name="eye"/> (hero, player two, dog, and active NPCs)
    /// for interactive water wake ripples with zero heap allocations or C++ interop.
    /// </summary>
    public static int CopyNearbyWaderPositions(Vector3 eye, float maxDistance, System.Span<Vector3> dst)
    {
        if (dst.IsEmpty || maxDistance <= 0f)
        {
            return 0;
        }

        float maxSq = maxDistance * maxDistance;
        int count = 0;

        static void TryAdd(Vector3 candidate, Vector3 eyePos, float maxDistSq, System.Span<Vector3> outSpan, ref int outCount, float minSepSq)
        {
            if (outCount >= outSpan.Length || !candidate.IsFinite() || candidate.DistanceSquaredTo(eyePos) > maxDistSq)
            {
                return;
            }

            for (int k = 0; k < outCount; k++)
            {
                if (outSpan[k].DistanceSquaredTo(candidate) < minSepSq)
                {
                    return;
                }
            }

            outSpan[outCount++] = candidate;
        }

        GameEntities? self = Current;
        if (self is not null && GodotObject.IsInstanceValid(self) && self.Visible && self._written.Count > 0)
        {
            Transform3D toWorld = self.IsInsideTree() ? self.GlobalTransform : self.Transform;

            static void TryAddWritten(
                uint addr,
                GameEntities entities,
                in Transform3D worldXform,
                Vector3 eyePos,
                float maxDistSq,
                System.Span<Vector3> outSpan,
                ref int outCount)
            {
                if (addr != 0u && entities._written.TryGetValue(addr, out (MeshInstance3D Node, Transform3D Drawn) written))
                {
                    Vector3 worldPos = (worldXform * written.Drawn).Origin;
                    TryAdd(worldPos, eyePos, maxDistSq, outSpan, ref outCount, 0.09f);
                }
            }

            TryAddWritten(self._principals.Hero, self, toWorld, eye, maxSq, dst, ref count);
            TryAddWritten(self._principals.One, self, toWorld, eye, maxSq, dst, ref count);
            TryAddWritten(self._principals.Two, self, toWorld, eye, maxSq, dst, ref count);
            TryAddWritten(self._principals.Dog, self, toWorld, eye, maxSq, dst, ref count);

            for (int i = 0; i < _dynamicCasterAddresses.Count && count < dst.Length; i++)
            {
                TryAddWritten(_dynamicCasterAddresses[i], self, toWorld, eye, maxSq, dst, ref count);
            }
        }

        if (HeroPosition is { } hero)
        {
            TryAdd(hero, eye, maxSq, dst, ref count, 0.25f);
        }

        if (PlayerTwoPosition is { } p2)
        {
            TryAdd(p2, eye, maxSq, dst, ref count, 0.25f);
        }

        for (int i = 0; i < _dynamicCasterPositions.Count && count < dst.Length; i++)
        {
            TryAdd(_dynamicCasterPositions[i], eye, maxSq, dst, ref count, 0.25f);
        }

        return count;
    }

    /// <summary>Every walked entity's stated position in the guest's space, by address, from the
    /// last walk — for the readers that plot an entity the guest names (the minimap's dog and
    /// quest markers).</summary>
    private static readonly Dictionary<uint, (Vector3 Position, Quaternion Rotation)> _guestPlacements = new();

    public static bool TryGuestPosition(uint address, out Vector3 position)
    {
        bool known = _guestPlacements.TryGetValue(address, out (Vector3 Position, Quaternion Rotation) placed);
        position = placed.Position;
        return known;
    }

    /// <summary>The stated orientation beside <see cref="TryGuestPosition"/> — for the readers that
    /// turn a marker the way an entity faces (the pause minimap's hero, either player's).</summary>
    public static bool TryGuestRotation(uint address, out Quaternion rotation)
    {
        bool known = _guestPlacements.TryGetValue(address, out (Vector3 Position, Quaternion Rotation) placed);
        rotation = placed.Rotation;
        return known;
    }

    /// <summary>The guest's sim tick as of this frame's walk, read once.</summary>
    private uint _tickNow;

    /// <summary>
    /// Entities whose placement is still being interpolated, and so must be re-drawn on a frame
    /// the feed skips. An address joins when a new stated pair arrives and leaves once the drawn
    /// transform has reached the newest.
    /// </summary>
    private readonly HashSet<uint> _movers = new();

    /// <summary>Scratch for the movers that arrived this frame; cleared on every use.</summary>
    private readonly List<uint> _arrived = new();

    /// <summary>Rigs the guest's pose feed drove this tick, and so must keep blending.</summary>
    private readonly HashSet<uint> _reblending = new();

    /// <summary>Rigs the clip animator drove this tick, and so must keep advancing.</summary>
    private readonly HashSet<uint> _animating = new();

    /// <summary>
    /// Advances animation on a frame the feed skips.
    /// </summary>
    /// <remarks>
    /// BOTH BRANCHES STEP WITHOUT THIS. The pose feed's blend and the clip animator's advance
    /// are both driven by WALL TIME, so each is built to run every frame; trapped inside the
    /// tick gate they ran once per tick and the characters moved in 30 Hz jumps under a camera
    /// gliding at display rate. The clip animator takes its own delta from the clock, so
    /// calling it more often advances the same total time — it does not run animation fast.
    /// </remarks>
    private void ResampleAnimation()
    {
        // EVERY RIG IN VIEW, EVERY DRAWN FRAME. Animation runs on Godot's clock; a rig that
        // waited for the guest's next frame stepped at 30 Hz under a display drawing faster.
        // Out of view the pose holds, as the guest's own gate holds it (`sub_822142E8`). A rig
        // the walk already posed this frame is not posed twice.
        ReadPoseViews();
        Transform3D toWorld = GlobalTransform;
        ulong nowMs = Time.GetTicksMsec();

        _arrived.Clear();
        foreach (uint address in _reblending)
        {
            if (!_skeletons.TryGetValue(address, out Refii.Entities.Engine_EntitySkeleton? rig) ||
                rig.IsSettled ||
                _disarticulated.ContainsKey(address) ||
                _pendingCrumble.ContainsKey(address))
            {
                _arrived.Add(address);
                continue;
            }

            if (InViewForRepose(address, toWorld, nowMs))
            {
                rig.Reblend();
                if (rig.IsSettled)
                {
                    _arrived.Add(address);
                }
            }
        }

        foreach (uint address in _arrived)
        {
            _reblending.Remove(address);
        }

        _arrived.Clear();
        foreach (uint address in _animating)
        {
            if (!_skeletons.TryGetValue(address, out Refii.Entities.Engine_EntitySkeleton? rig) ||
                !InViewForRepose(address, toWorld, nowMs))
            {
                continue;
            }

            Refii.Animations.Engine_ClipAnimator animator = ClipAnimatorFor(address, rig);
            if (animator.WroteThisFrame)
            {
                continue;
            }

            if (animator.Begin(address, writeBones: true, out bool fold))
            {
                if (animator.IsSettled)
                {
                    _arrived.Add(address);
                }
                else if (fold)
                {
                    _poseBatch.Add(animator);
                }
            }
        }

        foreach (uint address in _arrived)
        {
            _animating.Remove(address);
        }

        // Folded on the thread pool as they were added, written in the order above.
        _poseBatch.Finish();

        if (_disarticulated.Count > 0)
        {
            _arrived.Clear();
            ulong currentFrame = Refii.Core.Engine_ProcessFrame.Current;
            foreach ((uint address, Refii.Physics.Engine_DisarticulatedRig shatter) in _disarticulated)
            {
                if (!_nodes.TryGetValue(address, out MeshInstance3D? node) ||
                    !_skeletons.TryGetValue(address, out Refii.Entities.Engine_EntitySkeleton? rig) ||
                    !GodotObject.IsInstanceValid(node))
                {
                    _arrived.Add(address);
                    continue;
                }

                // Leave ReassemblyFinished entries for the entity walk to consume so the mesh swap
                // back to the living composite happens atomically without hitting fastRow.
                if (shatter.ReassemblyFinished)
                {
                    continue;
                }

                if (shatter.LastSteppedFrame != currentFrame)
                {
                    Transform3D entityWorld = node.IsInsideTree() ? node.GlobalTransform : toWorld * node.Transform;
                    shatter.Step(_lastFrameDelta, entityWorld, SampleActiveGround, default, 0);
                    shatter.LastSteppedFrame = currentFrame;
                    rig.ApplyModelSpacePoses(shatter.ModelPoses);
                    Refii.Entities.Engine_CorpseManager.SyncDisarticulatedBounds(node, shatter, entityWorld);
                }
            }

            foreach (uint address in _arrived)
            {
                _disarticulated.Remove(address);
                if (_skeletons.TryGetValue(address, out Refii.Entities.Engine_EntitySkeleton? finishedRig))
                {
                    finishedRig.IsDisarticulated = false;
                }
            }
        }
    }

    private static bool SampleActiveGround(Vector3 worldPos, out Vector3 contactPos, out Vector3 contactNormal)
    {
        Refii.Level.Engine_SkyEnvironmentNode? sky = Refii.Level.Engine_SkyEnvironmentNode.Current;
        if (sky?.InteractionGround is not { } ground || sky.GetParent() is not Node3D regionRoot)
        {
            contactPos = default;
            contactNormal = Vector3.Up;
            return false;
        }

        Transform3D worldFromGround = regionRoot.GlobalTransform;
        Vector3 local = worldFromGround.AffineInverse() * worldPos;
        if (!ground.TryHeight(local.X, local.Z, out float h))
        {
            contactPos = default;
            contactNormal = Vector3.Up;
            return false;
        }

        contactPos = worldFromGround * new Vector3(local.X, h, local.Z);
        const float eps = 0.25f;
        if (ground.TryHeight(local.X - eps, local.Z, out float hx0) &&
            ground.TryHeight(local.X + eps, local.Z, out float hx1) &&
            ground.TryHeight(local.X, local.Z - eps, out float hz0) &&
            ground.TryHeight(local.X, local.Z + eps, out float hz1))
        {
            var localNormal = new Vector3(hx0 - hx1, 2f * eps, hz0 - hz1);
            Vector3 worldNormal = worldFromGround.Basis * localNormal;
            contactNormal = worldNormal.IsFinite() && worldNormal.LengthSquared() > 1e-6f
                ? worldNormal.Normalized()
                : Vector3.Up;
        }
        else
        {
            contactNormal = Vector3.Up;
        }

        return true;
    }

    /// <summary>Rigs whose pose is due this frame (Engine_ClipAnimator.PoseBatch).</summary>
    private readonly Refii.Animations.Engine_ClipAnimator.PoseBatch _poseBatch = new();

    /// <summary>The walk's posed rigs, for the readers that need their finished pose.</summary>
    private readonly List<(uint Address, MeshInstance3D Node, Refii.Entities.Engine_EntitySkeleton Rig)> _afterPose = new();

    /// <summary>The hero's light zone as of the latest walk, for building-boundary occlusion.</summary>
    private uint _heroZone = Refii.Models.Engine_ClassicZones.Outdoors;

    /// <summary>
    /// The light zone of the player looking through the second view as of the latest walk: player
    /// two's on split screen, else the hero's.
    /// </summary>
    private uint _secondViewZone = Refii.Models.Engine_ClassicZones.Outdoors;

    private readonly Dictionary<uint, uint> _entityLightZone = new();
    private readonly Dictionary<uint, uint> _entityLayers = new();

    private uint EntityLightZoneOf(uint address, bool refresh = false)
    {
        uint root = address;
        for (int hops = 0; hops < 4; hops++)
        {
            uint parent = _attachments.ParentOf(root);
            if (parent == 0u || parent == root)
            {
                break;
            }

            root = parent;
        }

        if (!refresh && _entityLightZone.TryGetValue(root, out uint cached))
        {
            // Unattached (the root is the entity itself), the lookup just read this very slot.
            if (root != address)
            {
                _entityLightZone[address] = cached;
            }
            return cached;
        }

        uint zone = HostBridge.GetEntityLightZone(root);
        _entityLightZone[root] = zone;
        if (root != address)
        {
            _entityLightZone[address] = zone;
        }
        return zone;
    }

    /// <summary>
    /// <see cref="EntityLightZoneOf"/> re-read from the guest, with what the entity's slot held
    /// before (0 when it had none). An entity hanging from nothing, which is every prop, is its own
    /// root: one lookup reads its old zone and stores its new one, where it was three.
    /// </summary>
    private uint RefreshEntityLightZone(uint address, out uint previous)
    {
        uint parent = _attachments.ParentOf(address);
        if (parent != 0u && parent != address)
        {
            previous = _entityLightZone.TryGetValue(address, out uint was) ? was : 0u;
            return EntityLightZoneOf(address, refresh: true);
        }

        uint zone = HostBridge.GetEntityLightZone(address);
        ref uint slot = ref System.Runtime.InteropServices.CollectionsMarshal.GetValueRefOrAddDefault(
            _entityLightZone, address, out bool known);
        previous = known ? slot : 0u;
        slot = zone;
        return zone;
    }

    /// <summary>
    /// Building occlusion, from one view: only cull non-principal rigs if they are occluded by
    /// building boundaries between them and the player looking (<paramref name="viewerZone"/>):
    /// 1. Outside looking into a building: cull beyond 55m (3025m²).
    /// 2. Inside a building looking into a DIFFERENT building: cull unconditionally (cannot see through solid walls).
    /// 3. Inside a building looking out into the open world: allow up to 65m (4225m²),
    ///    ensuring town square NPCs visible through open doors and windows remain animated.
    /// </summary>
    private bool IsBuildingOccluded(uint address, float eyeDistanceSq, uint viewerZone)
    {
        uint entityZone = EntityLightZoneOf(address);
        bool indoors = entityZone != 0u && entityZone != Refii.Models.Engine_ClassicZones.Outdoors;
        bool heroIndoors = viewerZone != 0u && viewerZone != Refii.Models.Engine_ClassicZones.Outdoors;
        if (!heroIndoors && indoors && eyeDistanceSq > 3025f)
        {
            return true;
        }
        if (heroIndoors && indoors && entityZone != viewerZone)
        {
            return true;
        }
        if (heroIndoors && !indoors && eyeDistanceSq > 4225f)
        {
            return true;
        }
        return false;
    }

    private bool InViewForRepose(uint address, Transform3D toWorld, ulong nowMs)
    {
        if (_poseViewCount == 0 || IsPrincipal(address) ||
            !_written.TryGetValue(address, out (MeshInstance3D Node, Transform3D Drawn) last))
        {
            return true;
        }

        Transform3D rigWorld = toWorld * last.Drawn;
        Vector3 rigAt = rigWorld.Origin;
        // Rigs beyond PoseFullRateRadius of every eye pose only on the guest's 15 Hz tick step
        // (FarRigPosesThisStep), not on intermediate between-walk frames.
        if (NearestEyeDistanceSq(rigAt) > PoseFullRateRadius * PoseFullRateRadius)
        {
            return false;
        }

        return PosedThisTick(address, true, rigWorld, nowMs);
    }

    /// <summary>
    /// Whether a non-principal rig is posed now: the guest's own gate where the title states it
    /// (<see cref="HostBridge.GetEntityPoseGate"/>: Fable 3's sub_82205E00 — its view test of the
    /// entity's bounds, SetAsAlwaysInView, the heroes), else the shell's view test of where the
    /// guest's pose puts the rig (<see cref="RigInView"/>). A cutscene prop is moved to the
    /// cutscene's origin and its clip carries it metres from there, so the origin is no measure
    /// of where it is drawn. <paramref name="rigWorld"/> is the rig node's world transform.
    /// </summary>
    private bool PosedThisTick(uint address, bool measurable, Transform3D rigWorld, ulong nowMs)
    {
        if (HostBridge.GetEntityPoseGate(address) is bool stated)
        {
            return stated;
        }

        Vector3 rigAt = rigWorld.Origin;
        float reach = DrawnReach(address) + ReachSlackMetres;
        int reaching = measurable ? ViewsReaching(address, rigAt, reach) : EveryView;
        return reaching != 0 && RigInView(address, measurable, rigWorld, reaching, nowMs);
    }

    /// <summary>
    /// The views a measurable non-principal rig is visible from, before the frustum: a bit per
    /// view whose eye is within the rig's drawn <paramref name="reach"/> and not walled off from
    /// it by a building (<see cref="IsBuildingOccluded"/>, with that view's player's zone).
    /// </summary>
    private int ViewsReaching(uint address, Vector3 rigAt, float reach)
    {
        int reaching = 0;
        for (int i = 0; i < _poseViewCount; ++i)
        {
            float eyeDistanceSq = _poseViews[i].Eye.DistanceSquaredTo(rigAt);
            bool withinReach = float.IsPositiveInfinity(reach) || eyeDistanceSq <= reach * reach;
            bool occluded = IsBuildingOccluded(address, eyeDistanceSq, i == 0 ? _heroZone : _secondViewZone);
            if (withinReach && !occluded)
            {
                reaching |= 1 << i;
            }
        }

        return reaching;
    }

    /// <summary>The squared distance from <paramref name="at"/> to the nearest view's eye.</summary>
    private float NearestEyeDistanceSq(Vector3 at)
    {
        float nearest = float.MaxValue;
        for (int i = 0; i < _poseViewCount; ++i)
        {
            float d = _poseViews[i].Eye.DistanceSquaredTo(at);
            if (d < nearest)
            {
                nearest = d;
            }
        }

        return nearest;
    }

    /// <summary>
    /// Whether a rig outside <see cref="PoseFullRateRadius"/> writes its bones this step;
    /// <paramref name="sinceWriteMs"/> since it last did.
    /// </summary>
    /// <remarks>
    /// The guest's divider (0x8220D67C) skips the step whenever the animation LOD is not 3 and the
    /// sub-step does not complete a tick, so a far rig lands on 15 Hz against a near rig's 30 —
    /// and EVERY far rig on the same sub-step, the one that completes the tick.
    ///
    /// The phase is the guest's as well as the rate, never spread by address: a shadow map
    /// re-renders when any caster in its pool is written, and halves interleaved by address give
    /// every lamp's pool a write on every guest frame. On the guest's phase a pool of far rigs is
    /// written on alternate guest frames and its map re-renders at 15 Hz.
    ///
    /// Under 30 fps every walk completes a tick, so that divider divides nothing. With
    /// <see cref="FarRigPoseHz"/> set, a far rig writes at that rate in real time instead, still
    /// only on tick steps (<see cref="Refii.Entities.Engine_FarRigPoseRate"/>); spread across the
    /// far rigs by address unless <see cref="FarRigPoseSpread"/> is off, which keeps the guest's one
    /// phase for the shadow pools above.
    /// </remarks>
    private bool FarRigPosesThisStep(uint address, ulong sinceWriteMs) =>
        _farRigHz <= 0f
            ? _tickCompletedThisWalk
            : Refii.Entities.Engine_FarRigPoseRate.Due(_tickCompletedThisWalk, _farRigFromSeconds,
                _farRigToSeconds, _farRigHz, FarRigPoseSpread, address, sinceWriteMs);

    /// <summary>
    /// `entity_create_budget_ms`: in play, the milliseconds of creation a frame may spend, at
    /// least one creation a frame (<see cref="Refii.Entities.Engine_EntityCreateBudget"/>); 0 (the
    /// default) keeps the minimum of 8 and the 2 ms from the walk's start. Live through `cvar-set`.
    /// </summary>
    public static double EntityCreateBudgetMs = ReadEntityCreateBudgetMs();

    private static double ReadEntityCreateBudgetMs() =>
        Refii.Entities.Engine_EntityCreateBudget.Clamp(Refii.Core.Engine_RefiiConfig.Float(
            Refii.Core.Engine_RefiiConfig.Read(out _), "entity_create_budget_ms", 0f));

    /// <summary>
    /// `far_rig_pose_hz`: the far rigs' write rate in real time; 0 (the default) leaves them on the
    /// guest's tick step. Clamped to 4-30 Hz (<see cref="Refii.Entities.Engine_FarRigPoseRate.Clamp"/>).
    /// Live through the debug channel's `render farRigPoseHz=`.
    /// </summary>
    public static float FarRigPoseHz = Refii.Entities.Engine_FarRigPoseRate.Clamp(Refii.Core.Engine_RefiiConfig.Float(
        Refii.Core.Engine_RefiiConfig.Read(out _), "far_rig_pose_hz", 0f));

    /// <summary>
    /// `far_rig_pose_spread`: with a far rate set, each far rig on its own phase (the default), so
    /// the cost is the same on every walk; off, all on one, so a lamp's shadow pool of far rigs
    /// re-renders at the rate. `render farRigPoseSpread=`.
    /// </summary>
    public static bool FarRigPoseSpread = Refii.Core.Engine_RefiiConfig.Bool(
        Refii.Core.Engine_RefiiConfig.Read(out _), "far_rig_pose_spread", true);

    /// <summary>The last walk's far rigs written, and those in view that held their pose.</summary>
    public static int FarRigsWritten;

    public static int FarRigsHeld;

    /// <summary>This walk's far rate, and the seconds at the previous tick step's walk and this one's.</summary>
    private float _farRigHz;

    private double _farRigFromSeconds;

    private double _farRigToSeconds;

    /// <summary>Whether this walk's guest frame completed a game tick; see FarRigPosesThisStep.</summary>
    private bool _tickCompletedThisWalk = true;

    /// <summary>
    /// The shell's visibility gate for posing, for a title that states none: the sphere around
    /// the guest's published pose (<see cref="HostBridge.GetEntityViewSphere"/>, placed by
    /// <paramref name="rigWorld"/>) reaches into the frustum of one of the <paramref name="views"/>
    /// (a bit per view) now, or was seen within <see cref="RigHoldMs"/>. Without a stated sphere
    /// the rig's origin is tested. An unmeasurable rig (not in the tree yet) and a rig never
    /// written both count as in view. The margin covers the skin past the bones (past the origin
    /// without a sphere); the sphere holds the model origin, so a rig the origin test passed
    /// still passes.
    /// </summary>
    private bool RigInView(uint address, bool measurable, Transform3D rigWorld, int views, ulong nowMs)
    {
        if (!measurable || _poseViewCount == 0 || !_rigSeenMs.ContainsKey(address))
        {
            _rigSeenMs[address] = nowMs;
            return true;
        }

        // The origin first: the stated sphere holds it, so an origin in view is a sphere in view,
        // and the guest's bones need not be read for the answer.
        bool inside = SphereInViews(rigWorld.Origin, 0f, views);
        if (!inside && ViewSphereOf(address) is { } stated)
        {
            (Vector3 Centre, float Radius) sphere = Refii.Entities.Engine_ViewSphere.ToWorld(
                rigWorld, Refii.Entities.Engine_EntitySkeleton.Swizzle(stated.Centre), stated.Radius);
            inside = SphereInViews(sphere.Centre, sphere.Radius, views);
        }

        if (inside)
        {
            _rigSeenMs[address] = nowMs;
            return true;
        }

        return nowMs - _rigSeenMs[address] <= RigHoldMs;
    }

    private bool SphereInViews(Vector3 centre, float radius, int views)
    {
        for (int i = 0; i < _poseViewCount; ++i)
        {
            if ((views & (1 << i)) != 0 &&
                Refii.Entities.Engine_ViewSphere.InsideAll(_poseViews[i].Planes, centre, radius, RigViewMargin))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// The guest's stated view sphere for a rig (model space), read once per guest frame: the
    /// published pose changes only when the guest steps.
    /// </summary>
    private (Vector3 Centre, float Radius)? ViewSphereOf(uint address)
    {
        uint frame = Refii.Core.Engine_GuestFrame.Frame;
        if (_viewSpheres.TryGetValue(address, out (uint Frame, (Vector3 Centre, float Radius)? Sphere) cached) &&
            cached.Frame == frame)
        {
            return cached.Sphere;
        }

        (Vector3 Centre, float Radius)? sphere = HostBridge.GetEntityViewSphere(address);
        _viewSpheres[address] = (frame, sphere);
        return sphere;
    }

    /// <summary>Each rig's stated view sphere and the guest frame it was read on.</summary>
    private readonly Dictionary<uint, (uint Frame, (Vector3 Centre, float Radius)? Sphere)> _viewSpheres = new();

    /// <summary>
    /// An entity's render layers: the fixture rule (Engine_LevelLights.EntityLayersOf), in its
    /// light zone under the modern renderer (Engine_LightZones: an interior trades the world bit).
    /// Under the modern renderer where static world shadows are baked, only characters, attached
    /// items, and active movers carry EntityCasterLayerMask so static props do not flood dynamic shadow passes.
    /// </summary>
    private uint EntityLayers(in HostBridge.GuestEntity entity) =>
        EntityLayers(entity, IsDynamicShadowEntity(entity.Address));

    /// <summary>The same, with the entity's <see cref="IsDynamicShadowEntity"/> already asked.</summary>
    private uint EntityLayers(in HostBridge.GuestEntity entity, bool isDynamic)
    {
        uint layers = ZonedEntityLayers(entity, isDynamic);
        return _collectedKeys.TryGetValue(entity.Address, out uint taken) ? CollectedLayers(layers, taken) : layers;
    }

    /// <summary>
    /// True when <paramref name="address"/> is a character, companion, attached item, or actively
    /// moving/animating entity rather than a static prop with dummy bones (such as a streetlamp).
    /// </summary>
    private bool IsDynamicShadowEntity(uint address) =>
        IsPrincipal(address) ||
        _movers.Contains(address) ||
        _animating.Contains(address) ||
        _reblending.Contains(address) ||
        _awaitingRig.Contains(address) ||
        _attachments.ParentOf(address) != 0u ||
        _compositeParts.ContainsKey(address) ||
        (_clipAnimators.TryGetValue(address, out Refii.Animations.Engine_ClipAnimator? anim) && anim.IsCharacterRig) ||
        (_skeletons.TryGetValue(address, out Refii.Entities.Engine_EntitySkeleton? skel) && skel.Mapped > 8) ||
        (_nodeMesh.TryGetValue(address, out Mesh? m) && Refii.Models.Engine_MdlRig.TryFrom(m) is { } mdlRig && mdlRig.BoneCount > 8);

    /// <summary>The entity's layers from its record, its dynamic-caster standing and its light zone.</summary>
    private uint ZonedEntityLayers(in HostBridge.GuestEntity entity, bool isDynamic)
    {

        uint layers = Refii.Lighting.Engine_LevelLights.EntityLayersOf(
            entity.RecordHash,
            includeDynamicCaster: isDynamic || Refii.Models.Engine_ClassicMaterial.Active);
        if (!Refii.Lighting.Engine_LightZones.Active || Refii.Lighting.Engine_LightingFrame.CurrentInterior)
        {
            return layers;
        }

        uint zone = EntityLightZoneOf(entity.Address);
        if (zone == (entity.RecordHash & 0x7FFFFFFFu))
        {
            // `entity` is a CECBuilding in an outdoor level. Its interior mesh records (if any)
            // are split onto the "Interior" child node (SyncInteriorMesh -> InteriorEntityLayers),
            // so the primary node holds only exterior meshes unless the building has interior
            // records and zero exterior records.
            if (!_multiMeshSplit.TryGetValue(entity.Address, out (uint[] Exterior, uint[] Interior) split) ||
                split.Exterior.Length > 0 || split.Interior.Length == 0)
            {
                zone = Refii.Models.Engine_ClassicZones.Outdoors;
            }
        }

        return Refii.Lighting.Engine_LightZones.Zoned(layers, zone);
    }

    private uint InteriorEntityLayers(in HostBridge.GuestEntity entity)
    {
        uint layers = Refii.Lighting.Engine_LevelLights.EntityLayersOf(
            entity.RecordHash,
            includeDynamicCaster: Refii.Models.Engine_ClassicMaterial.Active);
        if (!Refii.Lighting.Engine_LightZones.Active)
        {
            return layers;
        }

        uint zone = EntityLightZoneOf(entity.Address);
        if (zone == 0u || zone == Refii.Models.Engine_ClassicZones.Outdoors)
        {
            zone = entity.RecordHash & 0x7FFFFFFFu;
        }

        return Refii.Lighting.Engine_LightZones.Zoned(layers, zone);
    }

    /// <summary>Silver keys a player has taken (HostBridge.CollectedKeys), refreshed once a second.</summary>
    private readonly Dictionary<uint, uint> _collectedKeys = new();

    private double _collectedKeysAge = double.MaxValue;

    private void RefreshCollectedKeys(double delta)
    {
        _collectedKeysAge += delta;
        if (_collectedKeysAge >= 1.0)
        {
            _collectedKeysAge = 0.0;
            HostBridge.CollectedKeys(_collectedKeys);
        }
    }

    /// <summary>
    /// A ONE-TIME COLLECTABLE IS SHOWN ONLY TO WHOEVER STILL NEEDS IT (Fable 3's ghosting): on split
    /// screen the key a player has taken is drawn in the other player's view alone; online each
    /// machine hides what its own player has; a shared screen hides it only once both have it
    /// (Fable 3's DoNotGhostInCouchUnlessBothCollected).
    /// </summary>
    private static uint CollectedLayers(uint layers, uint taken)
    {
        if (Refii.Core.Engine_Views.Split)
        {
            return taken switch
            {
                1u => Refii.Models.Engine_ClassicViews.SecondViewOnlyLayer,
                2u => Refii.Models.Engine_ClassicViews.FirstViewOnlyLayer,
                _ => 0u,
            };
        }

        bool hidden = Engine_TitleGame.Active.LocalCoop
            ? taken == 3u
            : (taken & (Engine_TitleGame.Active.OnlineClient ? 2u : 1u)) != 0u;
        return hidden ? 0u : layers;
    }

    /// <summary>
    /// Reads every view drawing the world (Engine_Views) into <see cref="_poseViews"/>: its eye
    /// now, and its frustum planes once per drawn frame. Returns the number of views.
    /// </summary>
    private int ReadPoseViews()
    {
        IReadOnlyList<Camera3D> cameras = Refii.Core.Engine_Views.Cameras(GetViewport()?.GetCamera3D());
        int count = System.Math.Min(cameras.Count, _poseViews.Length);
        ulong frame = Engine.GetProcessFrames();
        for (int i = 0; i < count; ++i)
        {
            Camera3D camera = cameras[i];
            PoseView view = _poseViews[i];
            view.Eye = camera.GlobalPosition;
            if (frame == view.PlanesFrame && ReferenceEquals(view.Camera, camera))
            {
                continue;
            }

            // Extract the six outward-facing frustum planes directly from projection + transform
            // without allocating a Godot.Collections.Array<Plane> wrapper via GetFrustum().
            Projection projection = camera.GetCameraProjection();
            Transform3D camTransform = camera.GetCameraTransform();
            Plane[] planes = view.Planes;
            planes[0] = camTransform * projection.GetProjectionPlane(Projection.Planes.Near);
            planes[1] = camTransform * projection.GetProjectionPlane(Projection.Planes.Far);
            planes[2] = camTransform * projection.GetProjectionPlane(Projection.Planes.Left);
            planes[3] = camTransform * projection.GetProjectionPlane(Projection.Planes.Top);
            planes[4] = camTransform * projection.GetProjectionPlane(Projection.Planes.Right);
            planes[5] = camTransform * projection.GetProjectionPlane(Projection.Planes.Bottom);

            view.Camera = camera;
            view.PlanesFrame = frame;
        }

        _poseViewCount = count;
        return count;
    }

    /// <summary>One view as the pose gates read it: its eye and its frustum (planes face outward).</summary>
    private sealed class PoseView
    {
        public Vector3 Eye;
        public readonly Plane[] Planes = new Plane[6];
        public Camera3D? Camera;
        public ulong PlanesFrame = ulong.MaxValue;
    }

    /// <summary>The views read by the latest <see cref="ReadPoseViews"/>; the window's first.</summary>
    private readonly PoseView[] _poseViews = { new(), new() };
    private int _poseViewCount;

    /// <summary>A view mask that passes every view: an unmeasurable rig or a principal.</summary>
    private const int EveryView = ~0;

    /// <summary>Last time each rig was inside the frustum.</summary>
    private readonly Dictionary<uint, ulong> _rigSeenMs = new();

    /// <summary>
    /// Last time the feed ASKED for each rig's bones to be written — in view and on its rate
    /// step. For the debug channel: beside the animator's `driven` and the rig's `applied` it
    /// separates "off screen", "in view but starved by the rate gate" and "asked and not
    /// written", which those two counters cannot, since both only move while a rig is in view.
    /// </summary>
    private readonly Dictionary<uint, ulong> _boneWriteAskedMs = new();

    /// <summary>Where each entity was when its probe sample was last taken, and under which theme.</summary>
    private readonly Dictionary<uint, (Vector3 At, long Gen)> _lastLit = new();

    /// <summary>The guest keeps posing for 200ms after a rig leaves view to absorb camera twitch.</summary>
    private const ulong RigHoldMs = 200;

    /// <summary>Metres past a frustum plane a rig's origin may sit and still count as in view.</summary>
    private const float RigViewMargin = 2.5f;


    /// <summary>
    /// Re-drives the static twin of every entity that is moving, on the frames between walks.
    /// </summary>
    /// <remarks>
    /// The walk drives married twins at tick rate; this keeps them with their bodies every
    /// frame. Only the movers: a twin married to something standing still needs no re-drive.
    /// </remarks>
    private void ResampleAdopted()
    {
        if (_adoptions.Count == 0 || _movers.Count == 0)
        {
            return;
        }

        foreach (uint address in _movers)
        {
            if (_adoptions.TryGetValue(address, out (Refii.Entities.Engine_StaticTwins.Adoption Twin, ulong Uid) married) &&
                _nodes.TryGetValue(address, out MeshInstance3D? node) &&
                IsInstanceValid(node))
            {
                Refii.Entities.Engine_StaticTwins.Drive(married.Twin, AdoptedWorld(address, node));
            }
        }
    }

    /// <summary>
    /// Everything that turns the guest's stated pairs into what is on screen, in the one order
    /// it must happen in, run EVERY frame.
    /// </summary>
    /// <remarks>
    /// On a walk frame (<paramref name="fromWalk"/> = true), the walk loop has already placed
    /// every mover, posed every rig, and driven every married twin for this frame; re-running
    /// <see cref="ResampleMovers"/>, <see cref="ResampleAnimation"/>, and
    /// <see cref="ResampleAdopted"/> on the same frame duplicated that work and caused far rigs
    /// whose bones were intentionally skipped on the half-tick (<c>WroteThisFrame == false</c>)
    /// to be posed anyway. Only <see cref="PlaceAttached"/> and
    /// <see cref="Refii.Entities.Engine_ShadowSkeletons.EndWalk"/> need to run after the walk.
    /// </remarks>
    private void ApplyDrawn(bool fromWalk = false)
    {
        ++Refii.Entities.Engine_DrawnFrame.Frames;
        long lap = System.Diagnostics.Stopwatch.GetTimestamp();
        if (!fromWalk)
        {
            ResampleMovers();
            Refii.Core.Engine_FramePhases.Lap("ent.drawn.movers", ref lap);
            ResampleAnimation();
            Refii.Core.Engine_FramePhases.Lap("ent.drawn.animation", ref lap);
        }
        PlaceAttached();
        Refii.Core.Engine_FramePhases.Lap("ent.drawn.attached", ref lap);
        if (!fromWalk)
        {
            ResampleAdopted();
            Refii.Core.Engine_FramePhases.Lap("ent.drawn.adopted", ref lap);
        }
        Refii.Entities.Engine_ShadowSkeletons.EndWalk();
        Refii.Core.Engine_FramePhases.Lap("ent.drawn.shadows", ref lap);

        // The trace reads what this frame put on screen, walk frame or not: sampling only after a
        // walk's poses missed every frame that only resampled, a third of them at 20 fps.
        if (_traceRemaining > 0 && _nodes.TryGetValue(_traceAddress, out MeshInstance3D? traced) &&
            IsInstanceValid(traced))
        {
            _skeletons.TryGetValue(_traceAddress, out Refii.Entities.Engine_EntitySkeleton? tracedRig);
            SampleTrace(_traceAddress, traced, StatedOf(_traceAddress, traced.Transform), tracedRig);
        }
    }

    private void ResampleMovers()
    {
        if (_movers.Count == 0)
        {
            return;
        }

        ReadPoseViews();
        Transform3D toWorld = GlobalTransform;
        ulong nowMs = Time.GetTicksMsec();

        _arrived.Clear();
        double drawTick = Refii.Entities.Engine_DrawnFrame.DrawTick;
        foreach (uint address in _movers)
        {
            if (!_bodies.TryGetValue(address, out Refii.Core.Engine_TickTimeline? body) ||
                !_nodes.TryGetValue(address, out MeshInstance3D? node) ||
                !IsInstanceValid(node))
            {
                _arrived.Add(address);
                continue;
            }

            if (body.Settled)
            {
                _arrived.Add(address);
                continue;
            }

            Transform3D advanced = body.Advance(Refii.Entities.Engine_DrawnFrame.NowUsec, drawTick);
            if (InViewForRepose(address, toWorld, nowMs))
            {
                if (_lastLook.TryGetValue(address, out HostBridge.EntityAppearance look))
                {
                    advanced = Refii.Entities.Engine_EntityAppearance.Scaled(advanced, look);
                }
                Place(address, node, advanced);
            }
            if (body.Settled)
            {
                _arrived.Add(address);
            }
        }

        foreach (uint address in _arrived)
        {
            _movers.Remove(address);
        }
    }

    /// <summary>
    /// Inside this (metres), a rig poses every step; outside it, every other step. The guest's
    /// <c>BoneLODDistance</c> (AM+220), whose constructor default is 10.0 and which
    /// <c>sub_8257CB20</c> clamps UP to a floor of 8.0 (<c>flt_8209B478</c>).
    /// </summary>
    /// <remarks>
    /// A rate, never a cutoff: the guest never stops posing a visible rig at any distance, and a
    /// rig that is never written stays in its bind pose (<c>sub_8257CB20</c> seeds the palette
    /// with the model-space rest pose). Live-settable through the debug channel's
    /// `render poseFullRate=`: a very large value puts every rig on 30 Hz, 0 puts every rig on
    /// 15 Hz.
    /// </remarks>
    public static float PoseFullRateRadius = 55f;

    /// <summary>Entities drawn blended between consecutive stated transforms, and those taken live.</summary>
    public static long BlendedEntities;

    public static long LiveOnlyEntities;

    /// <summary>
    /// The transform to draw: a blend of the previous and current stated transforms, following
    /// the guest's own interpolation contract.
    /// </summary>
    /// <remarks>
    /// The guest's tick first latches every renderable's transform (`sub_8222B2E0`: position to
    /// `obj+0xA0`, quaternion to `obj+0xB0`), the tick moves it, and `sub_822112D8` draws
    /// `prev*(1-alpha) + live*alpha` with a real slerp for the rotation.
    ///
    /// `Interpolate` is `obj+0xC9` and it is an ENABLE — non-zero blends, zero takes the live
    /// transform. It is cleared only by the Havok rigid-body component's init, so a physics-owned
    /// body is drawn where physics put it.
    /// </remarks>
    /// <summary>
    /// Resolves the game-tick stamp for an observed entity placement so a moved placement read
    /// slightly ahead of <paramref name="frameTick"/> advances the body's stamp by one tick
    /// (<c>[frameTick, frameTick + 1]</c>) instead of overwriting the current tick in place and
    /// doubling velocity, matching <see cref="Refii.Animations.Engine_ClipPlayhead.Observe"/>.
    /// </summary>
    /// <param name="frameTick">The game tick latched at frame start.</param>
    /// <param name="newestTick">The timeline's newest observed stamp.</param>
    /// <param name="bodyEmpty">True when the timeline has not yet been seeded.</param>
    /// <param name="moved">True when the placement changed relative to the timeline's newest sample.</param>
    /// <returns>The resolved monotonic tick stamp within <c>[frameTick, frameTick + 1]</c>.</returns>
    public static double ResolvePlacementStamp(uint frameTick, double newestTick, bool bodyEmpty, bool moved)
    {
        if (!bodyEmpty && moved && frameTick <= newestTick)
        {
            return System.Math.Clamp(newestTick + 1.0, frameTick, frameTick + 1.0);
        }

        return frameTick;
    }

    private Transform3D Drawn(HostBridge.GuestEntity entity, Transform3D stated)
    {
        // Rigged characters (NPCs, creatures, heroes) have their Havok rigid-body component
        // clear obj+0xC9 at spawn, but their 30 Hz stated movement must still be interpolated
        // by the timeline to match their display-rate (100–240 FPS) animation.
        bool canInterpolate = entity.Interpolate || _skeletons.ContainsKey(entity.Address);
        if (!canInterpolate)
        {
            _bodies.Remove(entity.Address);
            _movers.Remove(entity.Address);
            ++LiveOnlyEntities;
            return stated;
        }

        // Blended between consecutive STATED transforms, not across the guest's own pair: the
        // guest latches `prev <- live` late in a tick and moves `live` only partway into the
        // next, so for part of every tick both halves are equal. The guest renders only at alpha
        // 0.5 and 1.0, where that is harmless; we render through the window. The value drawn is
        // still the guest's; only its history is kept here, stamped with the tick it was read on
        // and drawn at the world's draw point with the limbs it carries.
        uint tick = Refii.Core.Engine_TickClock.GuestGameTick;
        if (!_bodies.TryGetValue(entity.Address, out Refii.Core.Engine_TickTimeline? body))
        {
            body = new Refii.Core.Engine_TickTimeline();
            body.Reset(tick, stated);
            _bodies[entity.Address] = body;
            ++LiveOnlyEntities;
            return stated;
        }

        bool moved = !stated.Origin.IsEqualApprox(body.Newest.Origin) ||
            !stated.Basis.IsEqualApprox(body.Newest.Basis);

        // A jump beyond TeleportMetres (a door, a cut, a scripted placement) is a teleport,
        // drawn at once rather than glided across.
        if (moved && body.Newest.Origin.DistanceSquaredTo(stated.Origin) >
            Refii.Core.Engine_TickTimeline.TeleportMetres * Refii.Core.Engine_TickTimeline.TeleportMetres)
        {
            body.Reset(tick, stated);
            _movers.Remove(entity.Address);
            ++LiveOnlyEntities;
            return stated;
        }

        // Only observe on a new placement or after 4 ticks have elapsed without movement:
        // ambient civilian NPCs often update placement every 2nd or 3rd game tick (7.5 Hz / 5 Hz),
        // so observing an unchanged intermediate tick (tick - NewestTick < 4.0) would zero velocity mid-stride.
        // Under `motion_arrival_fit` a moving body's timeline times this statement by the frame
        // that draws it rather than by the stamp (Engine_ArrivalFit); the observation rule is the same.
        if (moved || (_tickCompletedThisWalk && tick - body.NewestTick >= 4.0))
        {
            double stamp = ResolvePlacementStamp(tick, body.NewestTick, body.Empty, moved);
            body.Observe(stamp, stated);
        }
        if (moved)
        {
            _movers.Add(entity.Address);

            // Still told, because the world's own playhead is what MEASURES the shared period.
            Refii.Entities.Engine_DrawnFrame.NoteRestated();
        }

        Transform3D drawn = body.Advance(Refii.Entities.Engine_DrawnFrame.NowUsec,
            Refii.Entities.Engine_DrawnFrame.DrawTick);
        if (body.Settled)
        {
            _movers.Remove(entity.Address);
            ++LiveOnlyEntities;
            return stated;  // standing still: nothing to blend, and nothing to re-blend later
        }

        _movers.Add(entity.Address);
        ++BlendedEntities;
        return drawn;
    }

    /// <summary>Each entity's stated placements, stamped with the tick they were read on.</summary>
    private readonly Dictionary<uint, Refii.Core.Engine_TickTimeline> _bodies = new();

    private uint _traceAddress;
    private int _traceRemaining;
    private readonly List<(ulong Usec, uint Tick, Vector3 Drawn, Vector3 Stated, Vector3 Hip, double Newest, double DrawTick)> _trace = new();

    /// <summary>
    /// Starts recording one entity's drawn origin, stated origin and hip bone per frame — the
    /// series that says whether a body moves in even steps or in the jerks a viewer calls
    /// stutter. Read back with <see cref="TraceReport"/>.
    /// </summary>
    public void BeginTrace(uint address, int frames)
    {
        _traceAddress = address;
        _traceRemaining = frames;
        _trace.Clear();
    }

    public List<Dictionary<string, object?>> TraceReport()
    {
        var rows = new List<Dictionary<string, object?>>();
        foreach ((ulong usec, uint tick, Vector3 drawn, Vector3 stated, Vector3 hip, double newest, double drawTick) in _trace)
        {
            rows.Add(new Dictionary<string, object?>
            {
                ["usec"] = usec,
                ["tick"] = tick,
                ["drawn"] = new[] { drawn.X, drawn.Y, drawn.Z },
                ["stated"] = new[] { stated.X, stated.Y, stated.Z },
                ["hip"] = new[] { hip.X, hip.Y, hip.Z },
                // The body's newest statement's stamp beside the frame's draw point: how far behind
                // its own history the body was drawn.
                ["newest"] = double.IsFinite(newest) ? newest : null,
                ["drawTick"] = drawTick,
            });
        }

        return rows;
    }

    private void SampleTrace(uint address, MeshInstance3D node, Transform3D stated,
        Refii.Entities.Engine_EntitySkeleton? rig)
    {
        if (address != _traceAddress || _traceRemaining <= 0)
        {
            return;
        }

        --_traceRemaining;
        Vector3 hip = Vector3.Zero;
        if (rig is not null &&
            rig.TryBone(Refii.Animations.Engine_TitleRig.RigRole.Hip, out int bone))
        {
            hip = (node.Transform * rig.Node.GetBoneGlobalPose(bone)).Origin;
        }

        double newest = _bodies.TryGetValue(address, out Refii.Core.Engine_TickTimeline? body) ? body.NewestTick : double.NaN;
        _trace.Add((Godot.Time.GetTicksUsec(), Refii.Host.HostBridge.SimTick,
            node.Transform.Origin, stated.Origin, hip, newest, Refii.Entities.Engine_DrawnFrame.DrawTick));
    }

    private SubViewport? _closeup;
    private Camera3D? _closeupCamera;

    /// <summary>
    /// Frames one entity's head (or its whole body) in a private viewport that shares the
    /// world, so the debug channel can look at a face without moving the player's camera. One
    /// render is queued; the image is ready on the following frame.
    /// </summary>
    public bool ArmCloseup(uint address, string bone, float distance, int size)
    {
        if (!_nodes.TryGetValue(address, out MeshInstance3D? node))
        {
            return false;
        }

        if (_closeup is null)
        {
            _closeup = new SubViewport
            {
                Size = new Vector2I(size, size),
                RenderTargetUpdateMode = SubViewport.UpdateMode.Disabled,
                RenderTargetClearMode = SubViewport.ClearMode.Always,
                World3D = GetViewport().World3D,
                Msaa3D = Viewport.Msaa.Msaa4X,
            };
            _closeupCamera = new Camera3D { Current = true, Near = 0.02f, Far = 50f, Fov = 30f };
            _closeup.AddChild(_closeupCamera);
            AddChild(_closeup);
        }

        _closeup.Size = new Vector2I(size, size);
        Vector3 target = node.GlobalTransform.Origin + Vector3.Up;
        Vector3 forward = -node.GlobalTransform.Basis.Z;
        if (_skeletons.TryGetValue(address, out Refii.Entities.Engine_EntitySkeleton? rig) &&
            bone.Length != 0 && rig.TryBone(bone, out int index))
        {
            Transform3D boneGlobal = node.GlobalTransform * rig.Node.GetBoneGlobalPose(index);
            target = boneGlobal.Origin;
        }

        // In front of the face: the character's forward is its -Z the way the placement
        // conversion builds it, and a camera looks down its own -Z.
        Vector3 eye = target + forward.Normalized() * distance;
        _closeupCamera!.GlobalTransform = new Transform3D(Basis.Identity, eye).LookingAt(target, Vector3.Up);
        _closeup.RenderTargetUpdateMode = SubViewport.UpdateMode.Once;
        return true;
    }

    public Image? CloseupImage() => _closeup?.GetTexture().GetImage();

    /// <summary>Every clip-driven rig, for the debug channel: address, driven frames, fallback.</summary>
    public List<Dictionary<string, object?>> DescribeAnimators()
    {
        var rows = new List<Dictionary<string, object?>>();
        ulong nowMs = Time.GetTicksMsec();
        foreach ((uint address, Refii.Animations.Engine_ClipAnimator animator) in _clipAnimators)
        {
            if (!animator.Alive)
            {
                continue;
            }

            rows.Add(new Dictionary<string, object?>
            {
                ["address"] = $"{address:X8}",

                // The host's OWN view decision, in ms since it last held; -1 is never.
                ["seenMsAgo"] = _rigSeenMs.TryGetValue(address, out ulong seen) ? (long)(nowMs - seen) : -1L,
                ["writeAskedMsAgo"] = _boneWriteAskedMs.TryGetValue(address, out ulong asked)
                    ? (long)(nowMs - asked)
                    : -1L,
                ["driven"] = animator.Driven,
                ["lastFallback"] = animator.LastFallback,
                ["bones"] = animator.BoneCount,
                ["restBones"] = animator.LastRestBones,
                ["mostlyRestFrames"] = animator.MostlyRestFrames,
                ["topClip"] = $"{animator.LastTopKey:X8}",
                ["topShare"] = animator.LastTopShare,
                ["postOps"] = animator.PostOpsApplied,
                ["channels"] = animator.LastChannels,
                ["overlays"] = animator.LastOverlays,
                ["clips"] = animator.DescribeClips(),
            });
        }

        return rows;
    }

    /// <summary>The guest's stated clip leaves for one drawn entity, for the debug channel.</summary>
    public List<Dictionary<string, object?>>? DescribeLeaves(uint address) =>
        _clipAnimators.TryGetValue(address, out Refii.Animations.Engine_ClipAnimator? animator)
            ? animator.Describe(address)
            : null;

    /// <summary>
    /// The surfaces one drawn entity is made of, with the material each carries — the answer to
    /// "what is actually on this face", which a screenshot of a 40-pixel head cannot give.
    /// </summary>
    /// <summary>
    /// One entity's rig as GODOT holds it: every bone's current pose rotation and global
    /// position, and whether the node that should follow it is visible and skinned. The
    /// question this answers is "the animator drove the rig, so why did nothing move" — by
    /// showing whether the bones moved and whether a mesh is bound to them at all.
    /// </summary>
    /// <summary>For the debug channel: one rig's local rotations against the mdl rest, the guest reference and the guest live pose.</summary>
    public List<string>? DescribeLocalSources(uint address, string filter) =>
        _skeletons.TryGetValue(address, out Refii.Entities.Engine_EntitySkeleton? skeleton)
            ? skeleton.DescribeLocalSources(address, filter)
            : null;

    /// <summary>
    /// For the debug channel: one rig skinned here from what Godot holds — the mesh's own vertex,
    /// bone and weight arrays, the skin's binds and the skeleton's global poses — against the same
    /// vertices carried rigidly by <paramref name="anchor"/>. States the binds that are not their
    /// bone's inverse rest, the bones posed with a scale, the furthest vertices with their
    /// influences, and every node under the rig.
    /// </summary>
    public Dictionary<string, object?>? DescribeSkinning(uint address, string anchor)
    {
        if (!_nodes.TryGetValue(address, out MeshInstance3D? node) ||
            !_skeletons.TryGetValue(address, out Refii.Entities.Engine_EntitySkeleton? rig))
        {
            return null;
        }

        Skeleton3D sk = rig.Node;
        int bones = sk.GetBoneCount();
        var result = new Dictionary<string, object?>
        {
            ["node"] = node.GetPath().ToString(),
            ["skeletonPath"] = node.Skeleton.ToString(),
            ["bones"] = bones,
            ["skinIsRigs"] = ReferenceEquals(node.Skin, rig.Skin),
        };

        var tree = new List<string>();
        void Walk(Node at, int depth)
        {
            if (tree.Count >= 64)
            {
                return;
            }

            string shown = at is Node3D spatial ? $" visible={spatial.Visible}" : string.Empty;
            string mesh = at is MeshInstance3D instance
                ? $" mesh={instance.Mesh?.GetSurfaceCount() ?? 0} surfaces skin={(instance.Skin is null ? "none" : instance.Skin.GetBindCount().ToString())} layers={instance.Layers:X}"
                : string.Empty;
            tree.Add($"{new string(' ', depth * 2)}{at.Name} [{at.GetType().Name}]{shown}{mesh}");
            foreach (Node child in at.GetChildren())
            {
                Walk(child, depth + 1);
            }
        }

        Walk(node, 0);
        result["tree"] = tree;

        Skin? skin = node.Skin;
        if (skin is null || node.Mesh is not ArrayMesh mesh)
        {
            result["error"] = "no skin or not an ArrayMesh";
            return result;
        }

        int binds = skin.GetBindCount();
        var palette = new Transform3D[binds];
        var badBinds = new List<string>();
        for (int i = 0; i < binds; ++i)
        {
            int bone = skin.GetBindBone(i);
            string named = skin.GetBindName(i).ToString();
            if (bone < 0 && named.Length != 0)
            {
                bone = sk.FindBone(named);
            }

            if (bone < 0 || bone >= bones)
            {
                badBinds.Add($"bind {i}: no bone (bone={bone} name='{named}')");
                palette[i] = Transform3D.Identity;
                continue;
            }

            Transform3D bind = skin.GetBindPose(i);
            Transform3D atRest = sk.GetBoneGlobalRest(bone) * bind;
            float offOrigin = atRest.Origin.Length();
            Vector3 scale = atRest.Basis.Scale;
            float offBasis = (atRest.Basis.X - Vector3.Right).Length() + (atRest.Basis.Y - Vector3.Up).Length() +
                (atRest.Basis.Z - Vector3.Back).Length();
            if (bone != i || offOrigin > 1e-3f || offBasis > 1e-3f)
            {
                badBinds.Add($"bind {i} -> bone {bone} '{sk.GetBoneName(bone)}': rest*bind origin={offOrigin:0.0000} basisOff={offBasis:0.0000} scale=({scale.X:0.000},{scale.Y:0.000},{scale.Z:0.000})");
            }

            palette[i] = sk.GetBoneGlobalPose(bone) * bind;
        }

        result["binds"] = binds;
        result["bindsNotInverseRest"] = badBinds;

        var scaled = new List<string>();
        for (int b = 0; b < bones; ++b)
        {
            Vector3 poseScale = sk.GetBonePoseScale(b);
            Vector3 globalScale = sk.GetBoneGlobalPose(b).Basis.Scale;
            if ((poseScale - Vector3.One).Length() > 1e-3f || (globalScale - Vector3.One).Length() > 1e-3f)
            {
                scaled.Add($"{b} '{sk.GetBoneName(b)}' pose=({poseScale.X:0.000},{poseScale.Y:0.000},{poseScale.Z:0.000}) global=({globalScale.X:0.000},{globalScale.Y:0.000},{globalScale.Z:0.000})");
            }
        }

        result["scaledBones"] = scaled;

        int anchorBone = sk.FindBone(anchor);
        result["anchor"] = anchorBone < 0 ? "none: displacement is from the bind position" : sk.GetBoneName(anchorBone);
        Transform3D rigid = anchorBone >= 0 && anchorBone < binds ? palette[anchorBone] : Transform3D.Identity;

        var surfaces = new List<object?>();
        for (int surface = 0; surface < mesh.GetSurfaceCount(); ++surface)
        {
            Godot.Collections.Array arrays = mesh.SurfaceGetArrays(surface);
            Vector3[] positions = arrays[(int)Mesh.ArrayType.Vertex].AsVector3Array();
            int[] ids = arrays[(int)Mesh.ArrayType.Bones].AsInt32Array();
            float[] weights = arrays[(int)Mesh.ArrayType.Weights].AsFloat32Array();
            int per = positions.Length == 0 ? 0 : ids.Length / positions.Length;
            var worst = new List<(float Distance, int Vertex)>();
            int outOfRange = 0;
            int badSum = 0;
            for (int v = 0; v < positions.Length && per > 0; ++v)
            {
                Vector3 skinned = Vector3.Zero;
                float sum = 0f;
                for (int k = 0; k < per; ++k)
                {
                    float w = weights[(v * per) + k];
                    int id = ids[(v * per) + k];
                    if (w <= 0f)
                    {
                        continue;
                    }

                    if (id < 0 || id >= binds)
                    {
                        ++outOfRange;
                        continue;
                    }

                    skinned += (palette[id] * positions[v]) * w;
                    sum += w;
                }

                if (Mathf.Abs(sum - 1f) > 0.02f)
                {
                    ++badSum;
                }

                float distance = sum > 0f ? ((skinned / sum) - (rigid * positions[v])).Length() : 0f;
                worst.Add((distance, v));
            }

            worst.Sort((a, b) => b.Distance.CompareTo(a.Distance));
            var top = new List<string>();
            for (int i = 0; i < System.Math.Min(12, worst.Count); ++i)
            {
                int v = worst[i].Vertex;
                var influences = new List<string>();
                for (int k = 0; k < per; ++k)
                {
                    float w = weights[(v * per) + k];
                    int id = ids[(v * per) + k];
                    if (w > 0f)
                    {
                        influences.Add($"{(id >= 0 && id < bones ? sk.GetBoneName(id) : "?" + id)}@{w:0.00}");
                    }
                }

                top.Add($"v{v} moved {worst[i].Distance:0.000} at ({positions[v].X:0.00},{positions[v].Y:0.00},{positions[v].Z:0.00}) {string.Join(' ', influences)}");
            }

            int over = 0;
            foreach ((float distance, int _) in worst)
            {
                if (distance > 0.5f)
                {
                    ++over;
                }
            }

            surfaces.Add(new Dictionary<string, object?>
            {
                ["surface"] = surface,
                ["vertices"] = positions.Length,
                ["influencesPerVertex"] = per,
                ["idsOutOfRange"] = outOfRange,
                ["weightSumsOff"] = badSum,
                ["movedOverHalfUnit"] = over,
                ["furthest"] = top,
            });
        }

        result["surfaces"] = surfaces;
        return result;
    }

    public Dictionary<string, object?>? DescribeBones(uint address)
    {
        if (!_nodes.TryGetValue(address, out MeshInstance3D? node))
        {
            return null;
        }

        var result = new Dictionary<string, object?>
        {
            ["visible"] = node.Visible && node.IsVisibleInTree(),
            ["surfaces"] = node.Mesh?.GetSurfaceCount() ?? 0,
            ["skinned"] = node.Skin is not null && !node.Skeleton.IsEmpty,
            ["twinHidden"] = Twins?.IsHidden(node.GetMeta("entity_record", 0u).AsUInt32()),
            ["position"] = $"({node.GlobalPosition.X:0.00},{node.GlobalPosition.Y:0.00},{node.GlobalPosition.Z:0.00})",
        };

        if (_skeletons.TryGetValue(address, out Refii.Entities.Engine_EntitySkeleton? skeleton))
        {
            Skeleton3D sk = skeleton.Node;
            var bones = new List<string>();
            for (int b = 0; b < sk.GetBoneCount(); b++)
            {
                Transform3D pose = sk.GetBonePose(b);
                Vector3 e = pose.Basis.GetEuler() * (180f / Mathf.Pi);
                Vector3 g = (sk.GlobalTransform * sk.GetBoneGlobalPose(b)).Origin;
                Vector3 sc = sk.GetBonePoseScale(b);
                string scale = (sc - Vector3.One).LengthSquared() > 1e-6f ? $" scale=({sc.X:0.000},{sc.Y:0.000},{sc.Z:0.000})" : string.Empty;
                bones.Add($"{b} '{sk.GetBoneName(b)}' parent={sk.GetBoneParent(b)} rot=({e.X:0.0},{e.Y:0.0},{e.Z:0.0}) " +
                    $"local=({pose.Origin.X:0.00},{pose.Origin.Y:0.00},{pose.Origin.Z:0.00}) global=({g.X:0.00},{g.Y:0.00},{g.Z:0.00}){scale}");
            }

            result["bones"] = bones;
        }

        // THE GUEST'S OWN MORPH RECORD for this entity, as exported: what the rig applies.
        var morph = new List<string>();
        foreach (HostBridge.GuestBoneMorph m in HostBridge.GetBoneMorph(address))
        {
            morph.Add($"bone {m.Bone} off=({m.Offset.X:0.000},{m.Offset.Y:0.000},{m.Offset.Z:0.000}) sc=({m.Scale.X:0.000},{m.Scale.Y:0.000},{m.Scale.Z:0.000})");
        }

        result["morph"] = morph;

        // What the guest stated to its composite submit for this entity.
        var partMorphs = new List<string>();
        HostBridge.GuestPartMorph[] statedParts = HostBridge.GetEntityPartMorphs(address, out uint partGeneration);
        foreach (HostBridge.GuestPartMorph part in statedParts)
        {
            var targets = new List<string>();
            foreach ((uint model, float weight) in part.Targets)
            {
                targets.Add($"{model:X8}@{weight:0.000}");
            }

            partMorphs.Add($"{part.BaseModel:X8} <- {string.Join(' ', targets)}");
        }

        result["partMorphs"] = partMorphs;
        result["partMorphGeneration"] = partGeneration;
        result["partMorphStatements"] = HostBridge.PartMorphStatements;
        result["partMorphsMorphed"] = HostBridge.PartMorphsMorphed;

        var textureLayers = new List<string>();
        foreach (HostBridge.GuestTextureComposite composite in HostBridge.GetEntityTextureLayers(address, out _))
        {
            var layers = new List<string>();
            foreach (HostBridge.GuestTextureLayer layer in composite.Layers)
            {
                layers.Add($"{layer.Texture:X8} mode={layer.Mode} w={layer.Weight:0.000}" +
                    (layer.HasTint ? $" tint={layer.Tint:X8} b={layer.Brightness:0.00}" : string.Empty) +
                    $" '{Refii.Textures.Engine_TexPathIndex.Resolve(layer.Texture) ?? "?"}'");
            }

            textureLayers.Add($"{(composite.Overlay ? "OVERLAY " : string.Empty)}{composite.Original:X8} '{Refii.Textures.Engine_TexPathIndex.Resolve(composite.Original) ?? "?"}': {string.Join(" | ", layers)}");
        }

        result["textureLayers"] = textureLayers;
        List<HostBridge.GuestWornItem> worn = HostBridge.GetEntityWornItems(address);
        var wornLines = new List<string>();
        foreach (HostBridge.GuestWornItem item in worn)
        {
            string name = Refii.Gdb.Engine_GdbCatalog.All?.NameOf(item.Hash) ?? "?";
            Refii.Equipment.ItemDetail? detail = Refii.Entities.Engine_HiddenClusters.DetailFor(item.Hash);
            wornLines.Add($"{item.Hash:X8} '{name}'{(item.Female ? " female" : "")}{(item.Open ? " open" : "")} clusters=[{(detail is null ? "-" : string.Join(",", detail.ClustersCovered))}]");
        }

        result["worn"] = wornLines;
        var partLines = new List<string>();
        List<HostBridge.GuestPartHides> builtParts = HostBridge.GetEntityPartHides(address);
        Dictionary<uint, HashSet<string>> hiddenPerPart = Refii.Entities.Engine_HiddenClusters.HiddenPerPart(builtParts);
        foreach (HostBridge.GuestPartHides part in builtParts)
        {
            string hides = hiddenPerPart.TryGetValue(part.Hash, out HashSet<string>? set) ? string.Join(";", set) : "";
            partLines.Add($"{part.Hash:X8} layer {part.Layer}{(part.Body ? " body" : "")} covers=[{string.Join(";", part.Covers)}] hidden=[{hides}]");
        }

        result["parts"] = partLines;
        if (HostBridge.GetAnimPostOps(address) is { } post)
        {
            var opLines = new List<string>();
            foreach (HostBridge.GuestPostOp op in post.Ops)
            {
                opLines.Add($"{(op.Kind == 0 ? "aim" : "look")} uid {op.RecordUid:X8} mode {op.Mode} w {op.Weight:0.000} rw {op.RecordWeight:0.000} q0=({op.Targets[0].X:0.00},{op.Targets[0].Y:0.00},{op.Targets[0].Z:0.00},{op.Targets[0].W:0.00})");
            }

            result["postOps"] = new Dictionary<string, object?>
            {
                ["aimOn"] = post.AimOn,
                ["lookOn"] = post.LookOn,
                ["aimBones"] = post.AimBones,
                ["lookBones"] = post.LookBones,
                ["ops"] = opLines,
            };
        }
        if (_nodes.TryGetValue(address, out MeshInstance3D? owner) && Refii.Entities.Engine_BodyVariants.SourceMesh(owner) is ArrayMesh ownerMesh)
        {
            string[] regions = Refii.Models.Engine_MdlMesh.SurfaceRegions(ownerMesh);
            var surfaceRegions = new List<string>();
            for (int s = 0; s < ownerMesh.GetSurfaceCount(); s++)
            {
                string region = s < regions.Length ? regions[s] : string.Empty;
                bool hidden = (Refii.Entities.Engine_HiddenClusters.CollapsedMask(owner) & (System.UInt128.One << s)) != System.UInt128.Zero;
                surfaceRegions.Add($"{s} '{ownerMesh.SurfaceGetName(s)}' region='{region}'{(hidden ? " HIDDEN" : "")}");
            }

            result["surfaceRegions"] = surfaceRegions;
        }

        return result;
    }

    /// <summary>
    /// What lights one entity, as numbers: the instance terms the shader was handed (probe SH,
    /// lightmap factor and bake mode, flat ambient) and every placed light whose range reaches
    /// it. An address of 0 is the hero.
    /// </summary>
    /// <summary>
    /// Bones whose global pose has a NEGATIVE determinant. Godot takes a skinned instance's facing
    /// from the NODE transform alone, so triangles skinned to a mirrored bone rasterise as back
    /// faces, and the material negates a back face's normal: that body region lights inside out.
    /// </summary>
    private static List<string> MirroredBones(MeshInstance3D node)
    {
        var found = new List<string>();
        if (node.Skeleton.IsEmpty || node.GetNodeOrNull<Skeleton3D>(node.Skeleton) is not Skeleton3D skeleton)
        {
            found.Add("(no skeleton)");
            return found;
        }

        int bones = skeleton.GetBoneCount();
        for (int bone = 0; bone < bones; bone++)
        {
            float determinant = skeleton.GetBoneGlobalPose(bone).Basis.Determinant();
            if (determinant < 0f)
            {
                found.Add($"{skeleton.GetBoneName(bone)} det={determinant:0.###}");
            }
        }

        found.Insert(0, $"{found.Count} of {bones} bones mirrored");
        return found;
    }

    /// <summary>Each drawn entity's look as last stated, for the debug channel's `looks`.</summary>
    private readonly Dictionary<uint, HostBridge.EntityAppearance> _lastLook = new();

    /// <summary>
    /// For the debug channel: the drawn entities nearest the eye with what the guest states for
    /// their look — appearance (opacity, tint, flags), texture composites (mode per layer,
    /// wildcard, overlay) — and how the node draws it (transparency, layers).
    /// </summary>
    public List<string> DescribeLooks(Vector3 eye, int take)
    {
        var rows = new List<(float Distance, uint Address, MeshInstance3D Node)>();
        foreach ((uint address, MeshInstance3D node) in _nodes)
        {
            if (GodotObject.IsInstanceValid(node) && node.IsInsideTree() && node.IsVisibleInTree())
            {
                rows.Add((eye.DistanceTo(node.GlobalPosition), address, node));
            }
        }
        rows.Sort((a, b) => a.Distance.CompareTo(b.Distance));
        var described = new List<string>();
        foreach ((float distance, uint address, MeshInstance3D node) in rows.GetRange(0, System.Math.Min(take, rows.Count)))
        {
            string look = _lastLook.TryGetValue(address, out HostBridge.EntityAppearance a)
                ? $"alpha={a.Alpha:0.00} cam={a.CameraAlpha:0.00} scale={a.Scale:0.00} tint=({a.TintLanes.X:0.00},{a.TintLanes.Y:0.00},{a.TintLanes.Z:0.00},{a.TintLanes.W:0.00}) flags=0x{a.Flags:X}"
                : "look=?";
            var composites = new List<string>();
            foreach (HostBridge.GuestTextureComposite c in HostBridge.GetEntityTextureLayers(address, out _))
            {
                var modes = string.Join("", System.Array.ConvertAll(c.Layers, l =>
                    $"{l.Mode}{(l.HasTint ? $"t{l.Tint:X8}" : "")}{(l.Brightness != 1f ? $"b{l.Brightness:0.00}" : "")}"));
                composites.Add($"0x{c.Original:X8}{(c.Wildcard ? "W" : "")}{(c.Overlay ? "O" : "")}[{modes}]");
            }
            string model = node.HasMeta(MetaEntityModel) ? $"0x{(uint)node.GetMeta(MetaEntityModel).AsInt64():X8}" : "?";
            described.Add($"{address:X8} d={distance:0.0} model={model} mesh='{node.Mesh?.ResourceName}' {look} " +
                $"transparency={node.Transparency:0.00} layers=0x{node.Layers:X} composites={composites.Count} {string.Join(" ", composites)}");
        }
        return described;
    }

    public Dictionary<string, object?>? DescribeLighting(uint address)
    {
        if (address == 0u)
        {
            address = HostBridge.HeroEntityAddress();
        }

        if (!_nodes.TryGetValue(address, out MeshInstance3D? node) || !node.IsInsideTree())
        {
            return null;
        }

        Vector3 at = node.GlobalPosition;
        var result = new Dictionary<string, object?>
        {
            ["address"] = $"{address:X8}",
            ["at"] = $"({at.X:0.0},{at.Y:0.0},{at.Z:0.0})",
            ["layers"] = $"0x{node.Layers:X}",
            ["nodeDeterminant"] = node.GlobalTransform.Basis.Determinant(),
            ["mirroredBones"] = MirroredBones(node),
            ["castShadow"] = node.CastShadow.ToString(),
        };

        foreach (string parameter in new[]
            { "probe_r", "probe_g", "probe_b", "lightmap_colour", "ambient_colour", "darkening_colour" })
        {
            Variant value = node.GetInstanceShaderParameter(parameter);
            result[parameter] = value.VariantType == Variant.Type.Nil ? "unset" : value.ToString();
        }

        var lights = new List<Light3D>();
        Refii.Lighting.Engine_LevelLights.SnapshotLights(lights);
        var reaching = new List<(float Distance, string Row)>();
        foreach (Light3D light in lights)
        {
            if (!light.IsInsideTree() || !light.Visible)
            {
                continue;
            }

            float range = light is OmniLight3D omni ? omni.OmniRange
                : light is SpotLight3D spot ? spot.SpotRange : 0f;
            float distance = at.DistanceTo(light.GlobalPosition);
            if (distance > range)
            {
                continue;
            }

            Color c = Refii.Lighting.Engine_LightColour.Linear(light);
            reaching.Add((distance,
                $"{light.Name} {light.GetType().Name} d={distance:0.0} range={range:0.0} " +
                $"energy={light.LightEnergy:0.00} colour=({c.R:0.00},{c.G:0.00},{c.B:0.00}) " +
                $"shadow={light.ShadowEnabled} casters=0x{light.ShadowCasterMask:X}"));
        }

        reaching.Sort((a, b) => a.Distance.CompareTo(b.Distance));
        result["reaching"] = reaching.ConvertAll(row => row.Row);
        return result;
    }

    public List<Dictionary<string, object?>>? DescribeSurfaces(uint address)
    {
        if (!_nodes.TryGetValue(address, out MeshInstance3D? node) || Refii.Entities.Engine_BodyVariants.SourceMesh(node) is not { } mesh)
        {
            return null;
        }

        var rows = new List<Dictionary<string, object?>>();
        for (int surface = 0; surface < mesh.GetSurfaceCount(); ++surface)
        {
            var row = new Dictionary<string, object?>
            {
                ["surface"] = surface,
                ["name"] = mesh is ArrayMesh named ? named.SurfaceGetName(surface) : string.Empty,
            };

            if (mesh.SurfaceGetMaterial(surface) is ShaderMaterial material)
            {
                row["shader"] = Refii.Models.Engine_ShaderSpecialisation.PathOf(material.Shader);
                foreach (string parameter in new[]
                    { "albedo_tex", "normal_tex", "specular_tex", "emission_tex", "orm_tex" })
                {
                    if (material.GetShaderParameter(parameter).As<Texture2D>() is { } texture)
                    {
                        row[parameter] = texture.ResourcePath.Length != 0
                            ? texture.ResourcePath
                            : texture.ResourceName;
                    }
                }

                row["scissor"] = material.GetShaderParameter("alpha_scissor_on").AsBool();
            }
            else
            {
                row["material"] = mesh.SurfaceGetMaterial(surface)?.GetType().Name ?? "none";
            }

            rows.Add(row);
        }

        return rows;
    }

    /// <summary>The attachments hanging on one drawn entity, for the debug channel.</summary>
    public List<Dictionary<string, object?>> DescribeAttachments(uint address) =>
        _attachments.Describe(address, _skeletons.TryGetValue(address, out Refii.Entities.Engine_EntitySkeleton? rig) ? rig : null);

    /// <summary>The rig of one drawn entity, for the debug channel.</summary>
    public Dictionary<string, object?>? DescribeRig(uint address)
    {
        if (!_skeletons.TryGetValue(address, out Refii.Entities.Engine_EntitySkeleton? rig))
        {
            return null;
        }

        (int changes, int applies) = rig.ChangeCounts;
        return new Dictionary<string, object?>
        {
            ["bones"] = rig.BoneCount,
            ["guestBones"] = rig.GuestBoneCount,
            ["mapped"] = rig.Mapped,
            ["guestOnly"] = rig.GuestOnly,
            ["changed"] = changes,
            ["applied"] = applies,
            ["localReads"] = rig.LocalReads,
            ["channels"] = _clipAnimators.TryGetValue(address, out Refii.Animations.Engine_ClipAnimator? fold)
                ? fold.LastChannels : -1,
            ["runnerFlips"] = fold?.RigRunnerFlips ?? -1,
            ["topUid"] = fold?.LastTopUid ?? 0u,
            ["runnerUid"] = fold?.LastRunnerUid ?? 0u,
            ["topShare"] = fold?.LastTopShare ?? -1f,
            ["topKey"] = fold?.LastTopKey ?? 0u,
            ["shareSteps"] = fold?.RigShareSteps ?? -1,
        };
    }

    public string MostActiveRigs()
    {
        var rows = new List<(int Changes, int Applies, uint Address)>();
        foreach ((uint address, Refii.Entities.Engine_EntitySkeleton rig) in _skeletons)
        {
            (int changes, int applies) = rig.ChangeCounts;
            if (applies > 0)
            {
                rows.Add((changes, applies, address));
            }
        }

        rows.Sort((a, b) => b.Changes.CompareTo(a.Changes));
        var line = new System.Text.StringBuilder();
        for (int i = 0; i < rows.Count && i < 3; ++i)
        {
            line.Append($" 0x{rows[i].Address:X8} {rows[i].Changes}/{rows[i].Applies} ({100.0 * rows[i].Changes / rows[i].Applies:0}%)");
        }

        return line.Length == 0 ? "none" : line.ToString();
    }

    private void SampleSimClock()
    {
        uint tick = HostBridge.SimTick;
        ++_renderFrames;

        if (_simClockStartMsec == 0)
        {
            _simClockStartMsec = Godot.Time.GetTicksMsec();
            _simTickLast = tick;
            return;
        }

        // INEQUALITY, not subtraction. The counter's reset behaviour across a level load is
        // not settled, so a difference is not a trustworthy count of ticks — but "it changed"
        // is trustworthy whatever the counter did.
        if (tick == _simTickLast)
        {
            ++_framesOnThisState;
            return;
        }

        // SKIPPED STATES, counted separately and defensively. A difference is only meaningful
        // while the counter is monotonic, and whether a level load resets it is unproven — so
        // an implausible step is DISCARDED rather than reported as a thousand lost frames. What
        // survives is the case that matters: the guest ticking faster than we draw, where we
        // never see some of the states it produced.
        uint delta = tick - _simTickLast;
        if (delta > 1u && delta < 1000u)
        {
            _simStatesSkipped += (int)(delta - 1u);
        }

        _simTickLast = tick;
        ++_simAdvances;

        // Bucket how long the state we just finished was on screen for. Index 0 is one frame.
        int shown = _framesOnThisState + 1;
        _framesOnThisState = 0;
        _stateRepeats[Mathf.Min(shown - 1, _stateRepeats.Length - 1)]++;
    }

    /// <summary>
    /// Renders the two clocks and the beat between them.
    ///
    /// Reports both rates because either one alone is unfalsifiable: a sim rate with no render
    /// rate beside it cannot say whether the mismatch is the guest running slow or Godot
    /// running fast, and those call for opposite responses.
    /// </summary>
    private string SimClockReport()
    {
        if (_simClockStartMsec == 0 || _simAdvances == 0)
        {
            return "\n    game tick: the guest has not advanced a tick yet";
        }

        float seconds = (Godot.Time.GetTicksMsec() - _simClockStartMsec) / 1000f;
        if (seconds < 1f)
        {
            return "\n    game tick: too little wall time to state a rate";
        }

        int settled = _stateRepeats[0] + _stateRepeats[1] + _stateRepeats[2] + _stateRepeats[3];
        int dominant = 0;
        for (int i = 1; i < _stateRepeats.Length; ++i)
        {
            if (_stateRepeats[i] > _stateRepeats[dominant])
            {
                dominant = i;
            }
        }

        bool locked = settled != 0 && _stateRepeats[dominant] >= (int)(0.95f * settled);

        return $"\n    game tick: the guest advanced {_simAdvances / seconds:0.#} time(s)/s, " +
            $"Godot drew {_renderFrames / seconds:0.#} frame(s)/s over {seconds:0.#}s" +
            $"\n    each game tick was shown for: " +
            $"1 frame x{_stateRepeats[0]}, 2 x{_stateRepeats[1]}, 3 x{_stateRepeats[2]}, " +
            $"4+ x{_stateRepeats[3]}" +
            (locked
                ? $" — {dominant + 1} frame(s) every time, so these two clocks are IN STEP"
                : " — an UNEVEN spread, which is two clocks beating; that shows as stutter " +
                  "however correct each sample is, and the answer to it is interpolation, " +
                  "never locking") +
            (_simStatesSkipped == 0
                ? ""
                : $"\n    game ticks we never sampled: {_simStatesSkipped} — the guest is " +
                  "ticking faster than we look at it");
    }

    private uint _simTickLast;

    private ulong _simClockStartMsec;

    private int _simStatesSkipped;

    private int _renderFrames;

    private int _simAdvances;

    private int _framesOnThisState;

    /// <summary>How many frames each sim state stayed on screen: 1, 2, 3, 4 or more.</summary>
    private readonly int[] _stateRepeats = new int[4];

    /// <summary>
    /// Eighty times a walking stride in one frame. Below this is movement; above it is either a
    /// scripted teleport or a read that straddled a write.
    /// </summary>
    private const float TeleportThreshold = 2.0f;

    private int _positionJumps;

    private int _positionSamples;

    private float _worstJump;

    private int _facingPairs;

    /// <summary>Enough pairs to see the relationship, few enough to read in one screen.</summary>
    private const int FacingPairLimit = 14;

    /// <summary>Quadrant counts for composite (morph-assembled) characters.</summary>
    private readonly int[] _facingBucketsComposite = new int[4];

    /// <summary>Quadrant counts for characters drawn from a single named model.</summary>
    private readonly int[] _facingBucketsNamed = new int[4];

    /// <summary>Renders one quadrant histogram, with the share that landed on target.</summary>
    private static string Quadrants(int[] buckets)
    {
        int total = buckets[0] + buckets[1] + buckets[2] + buckets[3];
        return total == 0
            ? "no moving samples"
            : $"0°={buckets[0]} 90°={buckets[1]} 180°={buckets[2]} 270°={buckets[3]} " +
              $"({100f * buckets[0] / total:0}% on target of {total})";
    }

    /// <summary>Last frame's position per rigged entity, for the travel direction.</summary>
    private readonly Dictionary<uint, Vector3> _lastPosition = new();

    private int _facingSamples;

    private float _facingSum;

    /// <summary>Counts of samples nearest 0°, 90°, 180° and 270° off the direction of travel.</summary>
    private readonly int[] _facingBuckets = new int[4];

    private int _poseYawSamples;

    private float _poseYawSum;

    private float _poseYawWorst;

    /// <summary>
    /// A guest entity's transform, in Godot world space.
    ///
    /// Three conversions have to agree here and it is worth naming all three, because using
    /// any one alone is wrong:
    ///
    /// 1. N, the guest-to-Godot map this node lives in: <c>(x, y, z) -> (-x, z, y)</c>. That
    ///    is the level root's mirror composed with the per-placement swizzle, and it is what
    ///    <see cref="GameWorld"/>'s camera uses for the same reason — both are siblings of the
    ///    region rather than children of it.
    /// 2. R, the guest's rotation, taken from the quaternion with the guest's own formula
    ///    (sub_8220C8F0, <c>s = 2/|q|^2</c>) in ROW-vector form, so its rows are the local axes.
    /// 3. M, the map already baked into the mesh by Engine_MdlGeometry.ConvertZUpToYUp:
    ///    <c>(x, y, z) -> (x, z, -y)</c>.
    ///
    /// The basis is <c>N * R * M^-1</c>, and M^-1 sends e0, e1, e2 to localX, localZ and
    /// -localY — which is why the columns below are not localX/Y/Z in order.
    /// </summary>
    internal static Transform3D ToGodot(Vector3 position, Quaternion rotation)
    {
        float lengthSquared = (rotation.X * rotation.X) + (rotation.Y * rotation.Y) +
            (rotation.Z * rotation.Z) + (rotation.W * rotation.W);

        // A ROTATION QUATERNION IS UNIT. Anything else is not a rotation we can use, and the
        // old guard — finite and not near zero — let a great deal through: measured live,
        // character rotations arrived as `(0, 0, -8.98e34, 0)` and `(0, 0, 19169.12, -0)`.
        //
        // The second of those is the dangerous shape. The `s = 2/|q|²` form self-normalises, so
        // a wildly non-unit quaternion does not blow up — it quietly becomes a PERFECTLY VALID
        // rotation that has nothing to do with the character, and every downstream check passes.
        // Rejecting it makes the failure visible as "everyone faces one way" instead of
        // "everyone faces a plausible wrong way", and the counter below says how often.
        if (!float.IsFinite(lengthSquared) || lengthSquared < 0.98f || lengthSquared > 1.02f)
        {
            ++NonUnitRotations;
            return new Transform3D(Basis.Identity, ToGodot(position));
        }

        float s = 2f / lengthSquared;
        float x = rotation.X, y = rotation.Y, z = rotation.Z, w = rotation.W;

        var localX = new Vector3(
            1f - (s * ((y * y) + (z * z))),
            s * ((x * y) + (z * w)),
            s * ((x * z) - (y * w)));
        var localY = new Vector3(
            s * ((x * y) - (z * w)),
            1f - (s * ((x * x) + (z * z))),
            s * ((y * z) + (x * w)));
        var localZ = new Vector3(
            s * ((x * z) + (y * w)),
            s * ((y * z) - (x * w)),
            1f - (s * ((x * x) + (y * y))));

        return new Transform3D(
            new Basis(ToGodot(localX), ToGodot(localZ), -ToGodot(localY)),
            ToGodot(position));
    }

    /// <summary>Guest world space to Godot world space, outside the region root.</summary>
    private static Vector3 ToGodot(Vector3 guest) => new(-guest.X, guest.Z, guest.Y);

    /// <summary>
    /// Entity transforms whose quaternion was not unit and was refused. Run total.
    ///
    /// The number that says whether the bridge is handing us rotations at all. Non-zero means
    /// some entity's orientation source is still returning something that is not a rotation.
    /// </summary>
    public static int NonUnitRotations;

    /// <summary>
    /// Entity placements whose ORIGIN was not usable and was refused. Run total.
    ///
    /// Non-zero is not automatically a fault — a torn read of a position the guest is midway
    /// through writing is expected at some rate. A rate that CLIMBS is the fault, and one that
    /// reaches the renderer is a hang rather than a wrong pixel.
    /// </summary>
    public static int NonFinitePlacements;

    /// <summary>Squares to 1e18, three orders inside float's range, so a depth cannot overflow.</summary>
    private const float MaxPlacementCoordinate = 1e9f;

    private static bool IsUsablePlacement(Vector3 position) =>
        position.IsFinite() &&
        System.Math.Abs(position.X) < MaxPlacementCoordinate &&
        System.Math.Abs(position.Y) < MaxPlacementCoordinate &&
        System.Math.Abs(position.Z) < MaxPlacementCoordinate;
}
