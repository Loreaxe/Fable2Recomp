using System;
using System.Collections.Generic;
using Godot;

namespace Refii.Entities;

/// <summary>
/// Keeps the region's build-time copy of an entity and the guest's live one from both being
/// drawn, in either direction, for as long as the game runs.
///
/// THE GHOST. A region is built from the level's `.save` map and so is the guest: it
/// instantiates those same records as live entities and moves them. Both halves then draw, so
/// every door, cupboard, clock and music box in the street stands twice in the same spot, and
/// the copy you cannot interact with is ours.
///
/// ONE INVARIANT DECIDES EVERYTHING:
///
///     a static copy is visible  IFF  we are not drawing a live node for that record
///                                    AND its LAYER is not deactivated
///
/// THE SECOND HALF USED TO READ "the guest HAS that record", AND THAT WAS THE BUG. It inferred
/// the gate from whether the entity turned up in a walk of guest memory, and absence from a walk
/// is not a statement. Two independent things made it wrong. The walk TRUNCATES — it stops at its
/// buffer's end and returns a plain count — so absence often meant "past the cap"; that alone
/// hid 1497 of Ravenscar's drawables while HobbeCave, whose population fit, lost none. And the
/// guest instantiates from a deferred store on demand, so a record it has not built YET is not a
/// record it has gated off.
///
/// The layer is the thing both sides actually state. The level states membership positionally in
/// its `.save`, and the guest keeps the runtime layer lists itself — so the gate is read rather
/// than guessed. See <c>GatedOff</c> for which list, which is not the obvious one.
///
/// The first half stands unchanged and for its original reason: the guest states 1437 live
/// entities but only 1030 name a model, so a record we cannot draw a live node for must keep its
/// static copy — that is the only copy, and removing it empties the street.
///
/// NOTHING IS FREED, AND THAT IS THE POINT. `Layers.ActivateLayer` spawns a layer's entities
/// mid-game and `DeactivateLayer` destroys them again, so the correct answer for a given record
/// changes while the game runs. An earlier version called `QueueFree` and could only ever answer
/// once: a layer activated on entering a building found its props already destroyed, with no way
/// back. Hiding is reversible, so the invariant can simply be re-applied.
/// </summary>
public sealed class Engine_StaticTwins
{
    /// <summary>Node names by instance, read once: the name is a native string each time it is asked for.</summary>
    private readonly Dictionary<ulong, string> _twinNames = new();

    private string NameOf(Node node)
    {
        ulong id = node.GetInstanceId();
        if (!_twinNames.TryGetValue(id, out string? name))
        {
            name = node.Name;
            _twinNames[id] = name;
        }

        return name;
    }

    /// <summary>One drawable the guest may take over: a whole node, or one MultiMesh slot.</summary>
    private readonly struct Twin
    {
        public Twin(Node3D node, int instance, Transform3D standing)
        {
            Node = node;
            Instance = instance;
            Standing = standing;
        }

        public Node3D Node { get; }

        /// <summary>-1 for a node that is the instance; otherwise its MultiMesh slot.</summary>
        public int Instance { get; }

        /// <summary>
        /// Where a batched instance stands, captured at index time.
        ///
        /// A MultiMesh slot is hidden by collapsing its basis to zero, which destroys the
        /// transform — so restoring it needs the original, and the original has to be taken
        /// before anything is ever hidden.
        /// </summary>
        public Transform3D Standing { get; }
    }

    private readonly Dictionary<uint, List<Twin>> _byRecord = new();

    /// <summary>
    /// EVERY drawable by model name, record or not. Record-less static twins — such as
    /// companion sub-parts or pre-baked <c>engine_level</c> placements — have no <c>.save</c>
    /// record for the record join to match on. This index lets a drawn entity hide a
    /// record-less static copy that shares its model and place.
    /// </summary>
    private readonly Dictionary<string, List<Twin>> _byModel = new();

    /// <summary>Twins hidden because a drawn entity of the same model stands where they do.</summary>
    private readonly HashSet<(Node3D Node, int Instance)> _hiddenByPlace = new();

    public int HiddenByPlace => _hiddenByPlace.Count;

    /// <summary>
    /// THE MARRIAGE. A twin a live entity has adopted is neither hidden nor drawn twice: the
    /// region's copy — with its baked lighting, in the wall it was painted into — stays on
    /// screen and the guest's placement drives it, so no second mesh is built for the entity
    /// at all. Hide and Show leave adopted twins alone; the entity releases the twin when the
    /// guest stops reporting it, hiding it if the guest destroyed the entity (a key taken, a
    /// door broken) and leaving it where it stood otherwise.
    /// </summary>
    public readonly struct Adoption
    {
        public Adoption(Node3D node, int instance, Transform3D standing)
        {
            Node = node;
            Instance = instance;
            Standing = standing;
        }

        public Node3D Node { get; }
        public int Instance { get; }
        public Transform3D Standing { get; }
    }

    private readonly HashSet<(Node3D Node, int Instance)> _adopted = new();

    public int Adopted => _adopted.Count;

    private bool IsAdopted(Twin twin) => _adopted.Contains((twin.Node, twin.Instance));

    /// <summary>The nearest free twin of this model within <paramref name="tolerance"/> of the place, adopted.</summary>
    /// <summary>
    /// BY RECORD FIRST. An entity that states a .save record is that placement, and its copy is
    /// the twin indexed under that record — taken whenever the twin is the same model, even if
    /// a neighbour's drawn place had hidden it, because identity outranks proximity. On a table
    /// of five goblets the nearest free copy was routinely a neighbour's, and the mis-pairing
    /// walked down the table one goblet at a time. Model and place is the fallback for an
    /// entity with no record, or whose record copy is a different variant (then the entity
    /// draws its own node and the record join hides the copy).
    /// </summary>
    public Adoption? TryAdopt(uint record, string modelKey, Vector3 origin, float tolerance = 0.75f)
    {
        if (record != 0u && _byRecord.TryGetValue(record, out List<Twin>? own))
        {
            foreach (Twin twin in own)
            {
                if (IsAdopted(twin) || !GodotObject.IsInstanceValid(twin.Node) ||
                    !twin.Node.IsInsideTree() ||
                    !_modelOf.TryGetValue((twin.Node, twin.Instance), out string? key) ||
                    key != modelKey)
                {
                    continue;
                }

                _hiddenByPlace.Remove((twin.Node, twin.Instance));
                _adopted.Add((twin.Node, twin.Instance));
                ShowTwin(twin);
                return new Adoption(twin.Node, twin.Instance, twin.Standing);
            }

            return null;
        }

        if (!_byModel.TryGetValue(modelKey, out List<Twin>? twins))
        {
            return null;
        }

        float best = tolerance * tolerance;
        Twin? found = null;
        foreach (Twin twin in twins)
        {
            if (IsAdopted(twin) || _hiddenByPlace.Contains((twin.Node, twin.Instance)) ||
                _withRecord.Contains((twin.Node, twin.Instance)) ||
                !GodotObject.IsInstanceValid(twin.Node) || !twin.Node.IsInsideTree())
            {
                continue;
            }

            Vector3 at = twin.Instance < 0
                ? twin.Node.GlobalTransform.Origin
                : (twin.Node.GlobalTransform * twin.Standing).Origin;
            float d = at.DistanceSquaredTo(origin);
            if (d <= best)
            {
                best = d;
                found = twin;
            }
        }

        if (found is not { } chosen)
        {
            return null;
        }

        _adopted.Add((chosen.Node, chosen.Instance));
        ShowTwin(chosen);
        return new Adoption(chosen.Node, chosen.Instance, chosen.Standing);
    }

    /// <summary>Puts the adopted twin where the entity says it stands, in world space.</summary>
    public static void Drive(Adoption adoption, Transform3D world)
    {
        if (!GodotObject.IsInstanceValid(adoption.Node) || !adoption.Node.IsInsideTree())
        {
            return;
        }

        if (adoption.Node is Refii.Level.SilverKeyNode key)
        {
            // The guest's CECHighlightedItem bobs and turns the key's entity itself (SilverKeyNode): the
            // key stands where the guest states it, with no motion of the node's own on top.
            Transform3D parent = key.GetParent() is Node3D above
                ? above.GlobalTransform
                : Transform3D.Identity;
            key.BaseTransform = parent.AffineInverse() * world;
            key.Stated = true;
            return;
        }

        if (adoption.Instance < 0)
        {
            // An unchanged transform is not written: Node3D has no equality check, so the write
            // would update the instance and redraw every shadow map it is paired with.
            if (adoption.Node.GlobalTransform.IsEqualApprox(world))
            {
                return;
            }

            Refii.Core.Engine_InstanceUpdates.Note(Refii.Core.Engine_InstanceUpdates.Source.TwinDriven);
            adoption.Node.GlobalTransform = world;
            Refii.Models.Engine_ClassicCasters.Moved(adoption.Node);
            return;
        }

        if (adoption.Node is MultiMeshInstance3D batched && batched.Multimesh is { } mesh &&
            adoption.Instance < mesh.InstanceCount)
        {
            Transform3D local = batched.GlobalTransform.AffineInverse() * world;
            if (Refii.Level.Engine_PropShadow.StatedTransforms(mesh) is { } stated &&
                (uint)adoption.Instance < (uint)stated.Length &&
                stated[adoption.Instance].IsEqualApprox(local))
            {
                return;
            }

            Refii.Core.Engine_InstanceUpdates.Note(Refii.Core.Engine_InstanceUpdates.Source.TwinDriven);
            Refii.Level.Engine_PropShadow.SetInstanceTransform(mesh, adoption.Instance, local);
            Refii.Level.Engine_LevelProps.CoverInstance(mesh, local);
        }
    }

    /// <summary>The entity is gone: hide the twin if the guest destroyed it, else leave it standing.</summary>
    public void Release(Adoption adoption, bool hide)
    {
        var twin = new Twin(adoption.Node, adoption.Instance, adoption.Standing);
        _adopted.Remove((adoption.Node, adoption.Instance));
        if (hide)
        {
            _hiddenByPlace.Add((adoption.Node, adoption.Instance));
            HideTwin(twin);
        }
    }

    /// <summary>The model name a prop node or an entity resolves to: file name, no extension, lower case.</summary>
    public static string ModelKey(string path)
    {
        string name = path.Replace('\\', '/');
        int slash = name.LastIndexOf('/');
        if (slash >= 0)
        {
            name = name[(slash + 1)..];
        }

        int dot = name.LastIndexOf('.');
        if (dot > 0)
        {
            name = name[..dot];
        }

        return name.ToLowerInvariant();
    }

    /// <summary>The model key a prop node states, or empty when it states none.</summary>
    private static string StatedModelKey(Node3D node) =>
        node.HasMeta(Refii.Level.Engine_PropStreamer.ModelPathMeta)
            ? ModelKey(node.GetMeta(Refii.Level.Engine_PropStreamer.ModelPathMeta).AsString())
            : string.Empty;

    private void AddByModel(string key, Twin twin)
    {
        if (key.Length == 0)
        {
            return;
        }

        _modelOf[(twin.Node, twin.Instance)] = key;
        if (!_byModel.TryGetValue(key, out List<Twin>? list))
        {
            _byModel[key] = list = new List<Twin>();
        }

        list.Add(twin);
    }

    /// <summary>
    /// Hides every twin of a model that stands within <paramref name="tolerance"/> of a drawn
    /// entity of that model. Called after <see cref="Reconcile"/> with the entities we put on
    /// screen this pass; a twin hidden here stays hidden for the session.
    /// </summary>
    public void HideAtPlaces(IReadOnlyList<(string ModelKey, Vector3 Origin)> drawn, float tolerance = 0.75f)
    {
        float limit = tolerance * tolerance;
        foreach ((string key, Vector3 origin) in drawn)
        {
            if (!_byModel.TryGetValue(key, out List<Twin>? twins))
            {
                continue;
            }

            foreach (Twin twin in twins)
            {
                // RECORD-LESS ONLY. A twin with a record answers to the record join: hidden when
                // its entity is drawn, standing when the entity is one we cannot draw. Hiding it
                // by a neighbour's place would open a hole where the guest still has a thing.
                if (IsAdopted(twin) || _hiddenByPlace.Contains((twin.Node, twin.Instance)) ||
                    _withRecord.Contains((twin.Node, twin.Instance)) ||
                    !GodotObject.IsInstanceValid(twin.Node) || !twin.Node.IsInsideTree())
                {
                    continue;
                }

                Vector3 at = twin.Instance < 0
                    ? twin.Node.GlobalTransform.Origin
                    : (twin.Node.GlobalTransform * twin.Standing).Origin;
                if (at.DistanceSquaredTo(origin) > limit)
                {
                    continue;
                }

                _hiddenByPlace.Add((twin.Node, twin.Instance));
                HideTwin(twin);
            }
        }
    }

    /// <summary>Records currently hidden. Membership IS the state; nothing is destroyed.</summary>
    private readonly HashSet<uint> _hidden = new();

    /// <summary>
    /// Records whose twin would be shown again, waiting on a second pass to agree. See the
    /// restore in <see cref="Reconcile"/> for why one pass is not evidence.
    /// </summary>
    private readonly HashSet<uint> _restorable = new();

    /// <summary>Records held hidden because the guest says the placement is destroyed.</summary>
    public int HiddenAsDestroyed { get; private set; }

    /// <summary>
    /// Node names of the records left standing as the only copy, most-repeated first.
    /// </summary>
    /// <remarks>
    /// THE ONLY MEASURE OF WHAT THE STATIC LAYER IS FOR. Every other record here is built and
    /// then hidden, so these are the whole return on maintaining a second copy of the region
    /// and the whole reason a ghost can be left behind. Names rather than record hashes because
    /// the question is what CLASS of thing the guest never gives us geometry for.
    /// </remarks>
    public IReadOnlyList<string> StandingSample { get; private set; } = System.Array.Empty<string>();

    /// <summary>Distinct `.save` records the region drew something for.</summary>
    public int Records => _byRecord.Count;

    /// <summary>Drawables behind those records — more than one where a record drew several.</summary>
    public int Instances { get; private set; }

    /// <summary>Records hidden because the guest is drawing its own copy.</summary>
    public int HiddenForLive { get; private set; }

    /// <summary>
    /// Records hidden because their LAYER is off.
    ///
    /// This used to mean "the guest reported no entity for it", which was an inference and a
    /// wrong one. It now means the level put the record in a layer and the guest says that layer
    /// is not active — a statement from both sides.
    /// </summary>
    public int HiddenByLayer { get; private set; }

    /// <summary>
    /// Records gated off whose entity the guest is nonetheless reporting as LIVE.
    ///
    /// MUST BE ZERO. Anything else means the layer state is being read wrongly, and it is the
    /// only counter that can tell a correct gate from one that is quietly deleting the level.
    /// </summary>
    public int GatedButLive { get; private set; }

    /// <summary>The layer gate actually ran — the map was present and the guest could be asked.</summary>
    public bool LayerGateRan { get; private set; }

    /// <summary>Records shown again after the guest gave one up. Non-zero means a layer activated.</summary>
    public int Restored { get; private set; }

    /// <summary>Restores held back this pass, waiting for a second pass to agree.</summary>
    public int RestorePending { get; private set; }

    /// <summary>
    /// The last reconcile ran against a TRUNCATED entity list.
    ///
    /// This no longer changes what is hidden — visibility is decided by the layer and by whether
    /// we drew a live node, neither of which reads an entity's absence. It still matters for the
    /// DEDUP half: a record whose live node exists past the cap will not be in `drawn`, so its
    /// static copy stays up and the prop is briefly doubled. A duplicate, not a hole, which is
    /// the direction this is all arranged to fail in.
    /// </summary>
    public bool LiveWasTruncated { get; private set; }

    /// <summary>
    /// Records the guest named that the region drew nothing for.
    ///
    /// THE NUMBER THAT VALIDATES THE JOIN KEY. Both sides are built from this level's `.save`,
    /// so a live entity's record should almost always name something the region also drew. This
    /// being large while <see cref="HiddenForLive"/> is near zero would mean the guest's
    /// `entity+0x1C` is not the GDB record hash and the whole match is void — which shows up as
    /// a report rather than as props vanishing, because an unmatched record hides nothing.
    /// </summary>
    public int Unmatched { get; private set; }

    /// <summary>Live draws that carried no record hash at all.</summary>
    public int NoRecord { get; private set; }

    /// <summary>
    /// Indexes a mounted region's static entity copies.
    ///
    /// Both containers, because the region builds entities in two places for two reasons: the
    /// props path draws whatever a placement names a model for, and
    /// <see cref="Engine_LevelEntities"/> draws the creatures on top of it. They duplicate the
    /// guest identically and are reconciled identically.
    /// </summary>
    public static Engine_StaticTwins Build(Node3D region)
    {
        var twins = new Engine_StaticTwins();

        if (region.GetNodeOrNull<Node3D>(Refii.Level.Engine_LevelProps.NodeName) is { } props)
        {
            twins.IndexProps(props);
            twins.IndexLayers(props);
        }

        if (region.GetNodeOrNull<Node3D>(Engine_LevelEntities.NodeName) is { } entities)
        {
            twins.IndexEntities(entities);
        }

        return twins;
    }

    /// <summary>The layer each record sits in, from the `.save`. Zero when the file named none.</summary>
    private readonly Dictionary<uint, uint> _layerOf = new();

    /// <summary>Layers whose `load` is `AlwaysOn` — unconditional, never gateable.</summary>
    private readonly HashSet<uint> _alwaysOnLayers = new();

    /// <summary>Records whose layer the `.save` did state. Reported so a zero map is visible.</summary>
    public int LayeredRecords => _layerOf.Count;

    public int AlwaysOnLayers => _alwaysOnLayers.Count;

    /// <summary>
    /// Reads the record → layer map the props build left on the container.
    ///
    /// This is the gate's INPUT, and it is read from the level's own `.save` rather than
    /// inferred: layer membership is positional in that file — an entity belongs to the element
    /// that contains it — so it is a statement the level makes and not a heuristic.
    /// </summary>
    private void IndexLayers(Node3D props)
    {
        if (!props.HasMeta(Refii.Level.Engine_LevelProps.LayerRecordsMeta) ||
            !props.HasMeta(Refii.Level.Engine_LevelProps.LayerKeysMeta))
        {
            return;
        }

        long[] records =
            props.GetMeta(Refii.Level.Engine_LevelProps.LayerRecordsMeta).AsInt64Array();
        long[] keys = props.GetMeta(Refii.Level.Engine_LevelProps.LayerKeysMeta).AsInt64Array();

        for (int i = 0; i < records.Length && i < keys.Length; i++)
        {
            if (records[i] != 0L)
            {
                _layerOf[(uint)records[i]] = (uint)keys[i];
            }
        }

        if (props.HasMeta(Refii.Level.Engine_LevelProps.AlwaysOnLayersMeta))
        {
            foreach (long key in
                props.GetMeta(Refii.Level.Engine_LevelProps.AlwaysOnLayersMeta).AsInt64Array())
            {
                _alwaysOnLayers.Add((uint)key);
            }
        }
    }

    /// <summary>
    /// Should the region's copy of this record be gated off, per the guest's LAYER state?
    ///
    /// THE PREDICATE IS "IS THIS LAYER DEACTIVATED", AND IT IS NOT THE OBVIOUS ONE.
    ///
    /// The world manager keeps two `std::list&lt;uint32_t&gt;` — ACTIVATED at +60 and DEACTIVATED at
    /// +72 — and `sub_823662C8` reads a different one per `load` value:
    ///
    ///   823664d0  bne cr6, loc_82366548   AlwaysOn    -> spawn, consulting NEITHER list
    ///   823664e8  bl  sub_82320A68        InitiallyOn -> spawn iff ABSENT from +72
    ///   82366520  bl  sub_8244C428        else        -> spawn iff PRESENT in +60
    ///
    /// `sub_82320A68` walks `W+72` and returns PRESENT (`xori r3, r8, 1` at 82320AE4); the call
    /// site then spawns on zero (`beq cr6, loc_82366548` at 823664F4). And the two lists are
    /// pinned by their writers touching both off one base: `Layers.ActivateLayer` inserts into
    /// +60 and erases from +72, `DeactivateLayer` does the reverse.
    ///
    /// Only the first two rows can reach this code — the props build already dropped everything
    /// else — so the whole gate is: an `InitiallyOn` layer named in the DEACTIVATED list.
    ///
    /// Asking the ACTIVATED list instead would have been catastrophic and plausible. Both lists
    /// start EMPTY (`sub_8231BF20` default-constructs each, and only a save-game load assigns
    /// them), so on a new game "is it in the activated list" is false for every layer in the
    /// level and would have hidden every `InitiallyOn` prop in it.
    ///
    /// Null means we cannot ask, and then nothing is gated — the safe direction: an ungated prop
    /// is at worst a duplicate the entity pass retires, a wrongly gated one is a hole.
    /// </summary>
    private bool GatedOff(uint record, Func<uint, bool>? layerIsDeactivated)
    {
        if (layerIsDeactivated is null ||
            !_layerOf.TryGetValue(record, out uint layer) || layer == 0u)
        {
            return false;
        }

        return !_alwaysOnLayers.Contains(layer) && layerIsDeactivated(layer);
    }

    private void IndexProps(Node3D props)
    {
        foreach (Node child in props.GetChildren())
        {
            if (child is MultiMeshInstance3D batched)
            {
                if (batched.Multimesh is not { } mesh)
                {
                    continue;
                }

                long[] records = batched.HasMeta(Refii.Level.Engine_PropStreamer.RecordsMeta)
                    ? batched.GetMeta(Refii.Level.Engine_PropStreamer.RecordsMeta).AsInt64Array()
                    : System.Array.Empty<long>();

                // FROM THE BUILD'S RECORD, not the server. `GetInstanceTransform` is a value-
                // returning server call: with the separate render thread each one waits for that
                // thread, and this loop runs once per batched instance at the mount (the market's
                // foliage alone is ten thousand). The record holds exactly the floats the build
                // wrote, and every later write goes through Engine_PropShadow.SetInstanceTransform,
                // which keeps it in step; a batch built elsewhere still asks the server.
                int count = mesh.InstanceCount;
                Transform3D[]? stated = Refii.Level.Engine_PropShadow.StatedTransforms(mesh);

                // ONCE PER BATCH: a batch's instances are one model, and a level has tens of thousands
                // of them (Bower Lake 56,200), each a metadata read and four strings on the mount's frame.
                string batchKey = StatedModelKey(batched);
                for (int i = 0; i < count; i++)
                {
                    Transform3D placed;
                    if (stated is not null)
                    {
                        placed = stated[i];
                    }
                    else
                    {
                        long from = Refii.Core.Engine_ServerSyncs.Begin();
                        placed = mesh.GetInstanceTransform(i);
                        Refii.Core.Engine_ServerSyncs.End(Refii.Core.Engine_ServerSyncs.Site.MultimeshTransform, from);
                    }

                    var twin = new Twin(batched, i, placed);
                    AddByModel(batchKey, twin);
                    if (i < records.Length && records[i] != 0L)
                    {
                        Add((uint)records[i], twin);
                    }
                }

                continue;
            }

            if (child is MeshInstance3D single)
            {
                var twin = new Twin(single, -1, single.Transform);
                AddByModel(StatedModelKey(single), twin);
                if (single.HasMeta(Refii.Level.Engine_PropStreamer.RecordMeta))
                {
                    long record =
                        single.GetMeta(Refii.Level.Engine_PropStreamer.RecordMeta).AsInt64();
                    if (record != 0L)
                    {
                        Add((uint)record, twin);
                    }
                }
            }
        }
    }

    /// <summary>
    /// Indexes the creature container, whose record rides as the text the inspector shows.
    ///
    /// Parsed rather than duplicated into a second numeric meta: the string is already the
    /// entity's identity everywhere else in the editor, and two spellings of one fact is how
    /// they drift apart.
    /// </summary>
    private void IndexEntities(Node3D entities)
    {
        foreach (Node child in entities.GetChildren())
        {
            if (child is not Node3D node || !node.HasMeta(Engine_EntityInstance.HashMeta))
            {
                continue;
            }

            string stated = node.GetMeta(Engine_EntityInstance.HashMeta).AsString();
            if (stated.StartsWith("0x", System.StringComparison.OrdinalIgnoreCase) &&
                uint.TryParse(stated[2..], System.Globalization.NumberStyles.HexNumber,
                    System.Globalization.CultureInfo.InvariantCulture, out uint record) &&
                record != 0u)
            {
                Add(record, new Twin(node, -1, node.Transform));
            }
        }
    }

    private void Add(uint record, Twin twin)
    {
        if (!_byRecord.TryGetValue(record, out List<Twin>? list))
        {
            list = new List<Twin>(1);
            _byRecord[record] = list;
        }

        list.Add(twin);
        _withRecord.Add((twin.Node, twin.Instance));
        ++Instances;
    }

    /// <summary>The twins that carry a .save record: the record join governs those, not the place.</summary>
    private readonly HashSet<(Node3D Node, int Instance)> _withRecord = new();

    /// <summary>Each indexed twin's model key, for the record path of adoption.</summary>
    private readonly Dictionary<(Node3D Node, int Instance), string> _modelOf = new();

    /// <summary>
    /// Hides the region's copy of one record the moment a live node for it is DRAWN.
    ///
    /// The fast half of the invariant, and the reason it exists separately from
    /// <see cref="Reconcile"/>: a region populates over many seconds, and waiting for the
    /// periodic pass would leave every door visibly doubled for the whole of that. Called on
    /// the frame the live node is created, never when the entity is merely reported — an entity
    /// whose model is a header-only stub resolves a path and then loads no mesh, and hiding on
    /// the report would take the only visible copy away.
    /// </summary>
    /// <summary>Whether the region's copy of a record is currently hidden (claimed or off).</summary>
    public bool IsHidden(uint record) => _hidden.Contains(record);

    public void Claim(uint record)
    {
        if (record == 0u)
        {
            ++NoRecord;
            return;
        }

        if (!_byRecord.ContainsKey(record))
        {
            ++Unmatched;
            return;
        }

        Hide(record);
    }

    /// <summary>
    /// Re-applies the invariant to every indexed record.
    ///
    /// This is what makes the answer follow the guest for the whole run rather than being fixed
    /// at load. `Layers.ActivateLayer` spawns a gated layer's entities mid-game and
    /// `DeactivateLayer` destroys them, so records move between all three states while playing:
    /// hidden because we draw it, hidden because the guest does not have it, and visible because
    /// the guest has it and we cannot draw it.
    /// </summary>
    /// <param name="live">Every record the guest currently reports an entity for.</param>
    /// <param name="drawn">The subset of those we have a live node for.</param>
    /// <param name="liveIsComplete">
    /// <paramref name="live"/> is EVERY entity the guest has, not a prefix of them. False
    /// disables the hide-on-unstated half — see the note on that branch.
    /// </param>
    /// <param name="layerIsDeactivated">
    /// The guest's own answer to "is this layer hash in the DEACTIVATED list", or null when it
    /// cannot be asked. THIS IS THE GATE — see <see cref="GatedOff"/> for why it is this list
    /// and not the activated one.
    /// </param>
    private readonly HashSet<ulong> _destroyed = new();
    private readonly Dictionary<string, int> _standing = new();
    private readonly List<KeyValuePair<string, int>> _ranked = new();
    private (int Live, int Layer, int Destroyed, int Restored) _lastStandingCounts = (-1, -1, -1, -1);

    public void Reconcile(
        HashSet<uint> live,
        HashSet<uint> drawn,
        bool liveIsComplete = true,
        Func<uint, bool>? layerIsDeactivated = null)
    {
        HiddenForLive = 0;
        HiddenByLayer = 0;
        GatedButLive = 0;
        LiveWasTruncated = !liveIsComplete;
        LayerGateRan = layerIsDeactivated is not null && _layerOf.Count != 0;
        HiddenAsDestroyed = 0;
        RestorePending = 0;

        // READ FRESH EVERY PASS, never cached. Deactivating a layer purges that layer's entries
        // from the guest's set, which is how broken props legitimately come back when a layer
        // cycles; a cached copy would keep them hidden for the rest of the session.
        _destroyed.Clear();
        foreach (ulong deadId in Refii.Host.HostBridge.GetDeadEntities())
        {
            _destroyed.Add(deadId);
        }

        foreach (KeyValuePair<uint, List<Twin>> pair in _byRecord)
        {
            bool ours = drawn.Contains(pair.Key);

            // THE GATE IS THE LAYER, NOT THE ENTITY'S ABSENCE FROM A WALK.
            //
            // This used to read `live.Contains(record)` and hide anything the guest had not
            // reported. Two separate things made that wrong. The entity walk TRUNCATES — it stops
            // at its buffer's end and returns a plain count — so absence often just meant "past
            // the cap"; that alone cost Ravenscar 1497 drawables against HobbeCave's 0, the only
            // difference between them being whether the population fit. And even with a complete
            // list it inferred a gate the guest states outright: the guest instantiates from a
            // deferred store on demand, so a record it has not built yet is not a record it has
            // gated off.
            //
            // So ask the question the level and the guest both answer. Layer membership is
            // positional in the `.save` and the guest keeps the runtime layer state itself; a
            // record outside an inactive layer is not gated, whatever the entity walk did or did
            // not return. `live` is no longer consulted for visibility at all — only `drawn` is,
            // and that is a positive statement that we put geometry on screen for this record.
            bool gated = GatedOff(pair.Key, layerIsDeactivated);

            // THE THIRD TERM. "The guest has no live entity for this record" is not "show the
            // region's copy" — destroying a breakable produces exactly that state, and the copy
            // reappearing where the smashed one stood is the ghost. The guest states destruction
            // outright and persists it to its own save, so this asks rather than infers.
            //
            // A PLACED ENTITY'S UID IS ITS RECORD, zero-extended: the guest keys its entity map
            // and DeadEntities by `0:record` for every `.save` placement (entity+0x14 == 0,
            // +0x18 == +0x1C). The set persists in the save, so a placement can be dead before
            // any live entity for it was ever reported — the join cannot wait to be learned.
            bool gone = _destroyed.Contains(pair.Key);

            bool shouldShow = !ours && !gated && !gone;

            if (!shouldShow)
            {
                if (ours)
                {
                    ++HiddenForLive;
                }
                else if (gone)
                {
                    ++HiddenAsDestroyed;
                }
                else
                {
                    ++HiddenByLayer;

                    // THE CONTRADICTION, AND THE ONLY THING THAT CAN CATCH A WRONG GATE.
                    //
                    // We just hid a record because its layer reads as off, and the guest is
                    // reporting a live entity for that same record. Both cannot be true. A
                    // non-zero count here means the layer state is being read wrongly — the wrong
                    // world-manager list, or the right one with inverted polarity — and it is the
                    // failure that would otherwise look exactly like the bug this replaced:
                    // props quietly disappearing with every counter claiming to be correct.
                    if (live.Contains(pair.Key))
                    {
                        ++GatedButLive;
                    }
                }

                Hide(pair.Key);
                continue;
            }

            if (_hidden.Contains(pair.Key))
            {
                // A RESTORE NEEDS TWO PASSES TO AGREE. Picking an item up puts the record in
                // exactly this state for one pass: the live entity is gone, so `ours` is false,
                // and the guest has not published the destruction yet, so `gone` is false too.
                // Restoring on that pass brought the item back — through `ShowTwin`, whose
                // `EnsureStreamed` decodes the prop ON THE MAIN THREAD — and the next pass hid
                // it again. That is the hitch and the second of standing there after a pickup.
                //
                // The two states a restore is FOR both persist: a layer that was activated stays
                // activated, and an entity destroyed under us stays destroyed. So a single pass
                // of disagreement is not evidence, and waiting one more costs a restore nothing.
                if (!_restorable.Add(pair.Key))
                {
                    _restorable.Remove(pair.Key);
                    _hidden.Remove(pair.Key);

                    // THE GUEST GAVE ONE BACK. Counted loudly: a non-zero Restored is a layer
                    // having been activated or an entity having been destroyed under us, which is
                    // exactly the behaviour the one-shot version could not produce.
                    ++Restored;
                    Show(pair.Value);
                }
                else
                {
                    ++RestorePending;
                }
            }
        }

        var currentCounts = (HiddenForLive, HiddenByLayer, HiddenAsDestroyed, Restored);
        if (currentCounts != _lastStandingCounts)
        {
            _lastStandingCounts = currentCounts;
            _standing.Clear();
            foreach (KeyValuePair<uint, List<Twin>> pair in _byRecord)
            {
                if (_hidden.Contains(pair.Key))
                {
                    continue;
                }
                foreach (Twin twin in pair.Value)
                {
                    if (!GodotObject.IsInstanceValid(twin.Node))
                    {
                        continue;
                    }
                    string name = NameOf(twin.Node);
                    _standing.TryGetValue(name, out int seen);
                    _standing[name] = seen + 1;
                }
            }

            _ranked.Clear();
            _ranked.AddRange(_standing);
            _ranked.Sort((a, b) => b.Value.CompareTo(a.Value));
            var sample = new List<string>(Math.Min(_ranked.Count, 12));
            for (int i = 0; i < _ranked.Count && i < 12; i++)
            {
                sample.Add($"{_ranked[i].Key}x{_ranked[i].Value}");
            }

            StandingSample = sample;
        }
    }

    private void Hide(uint record)
    {
        // A record being hidden is the disagreement resolving the other way: forget any restore
        // that was waiting on a second pass, so a later one starts its two passes over.
        _restorable.Remove(record);

        if (!_hidden.Add(record) || !_byRecord.TryGetValue(record, out List<Twin>? twins))
        {
            return;  // already hidden
        }

        foreach (Twin twin in twins)
        {
            if (!IsAdopted(twin))
            {
                HideTwin(twin);
            }
        }
    }

    /// <summary>
    /// Every indexed twin whose model key contains <paramref name="needle"/>: where it stands
    /// in world space, whether it is currently drawn, and which path hid it. A diagnostic for
    /// a doorway that still shows two doors.
    /// </summary>
    public List<Dictionary<string, object?>> DescribeByModel(
        string needle, IReadOnlyList<(string ModelKey, Vector3 Origin)>? drawnPlaces = null)
    {
        var rows = new List<Dictionary<string, object?>>();
        string want = needle.ToLowerInvariant();
        foreach (KeyValuePair<string, List<Twin>> entry in _byModel)
        {
            if (!entry.Key.Contains(want, StringComparison.Ordinal))
            {
                continue;
            }

            foreach (Twin twin in entry.Value)
            {
                if (!GodotObject.IsInstanceValid(twin.Node))
                {
                    continue;
                }

                bool drawn = twin.Instance < 0
                    ? twin.Node.Visible
                    : twin.Node is MultiMeshInstance3D batched && batched.Multimesh is { } mesh &&
                      twin.Instance < mesh.InstanceCount &&
                      mesh.GetInstanceTransform(twin.Instance).Basis.X.LengthSquared() > 1e-12f;
                Vector3 at = twin.Instance < 0
                    ? twin.Node.GlobalTransform.Origin
                    : (twin.Node.GlobalTransform * twin.Standing).Origin;
                uint record = 0u;
                foreach (KeyValuePair<uint, List<Twin>> byRecord in _byRecord)
                {
                    foreach (Twin candidate in byRecord.Value)
                    {
                        if (candidate.Node == twin.Node && candidate.Instance == twin.Instance)
                        {
                            record = byRecord.Key;
                        }
                    }
                }

                float nearest = float.PositiveInfinity;
                if (drawnPlaces is not null)
                {
                    foreach ((string key, Vector3 origin) in drawnPlaces)
                    {
                        if (key == entry.Key)
                        {
                            nearest = Math.Min(nearest, at.DistanceTo(origin));
                        }
                    }
                }

                rows.Add(new Dictionary<string, object?>
                {
                    ["model"] = entry.Key,
                    ["node"] = twin.Node.Name.ToString(),
                    ["insideTree"] = twin.Node.IsInsideTree(),
                    ["nearestDrawn"] = float.IsInfinity(nearest) ? null : $"{nearest:F3}",
                    ["instance"] = twin.Instance,
                    ["at"] = $"{at.X:F2},{at.Y:F2},{at.Z:F2}",
                    ["drawn"] = drawn,
                    ["record"] = record == 0u ? null : $"{record:X8}",
                    ["hiddenByRecord"] = record != 0u && _hidden.Contains(record),
                    ["hiddenByPlace"] = _hiddenByPlace.Contains((twin.Node, twin.Instance)),
                    ["adopted"] = IsAdopted(twin),
                });
            }
        }

        return rows;
    }

    public bool HasRecord(uint record) => _byRecord.ContainsKey(record);

    /// <summary>The model keys of the twins indexed under a record.</summary>
    public List<string> ModelsOfRecord(uint record)
    {
        var keys = new List<string>();
        if (_byRecord.TryGetValue(record, out List<Twin>? twins))
        {
            foreach (Twin twin in twins)
            {
                keys.Add(_modelOf.TryGetValue((twin.Node, twin.Instance), out string? key) ? key : "?");
            }
        }

        return keys;
    }

    /// <summary>Every twin currently drawn: model key, world origin, record and adoption state.</summary>
    public List<(string Model, Vector3 At, uint Record, bool Adopted, string Node)> DrawnTwins()
    {
        var recordOf = new Dictionary<(Node3D, int), uint>();
        foreach (KeyValuePair<uint, List<Twin>> byRecord in _byRecord)
        {
            foreach (Twin twin in byRecord.Value)
            {
                recordOf[(twin.Node, twin.Instance)] = byRecord.Key;
            }
        }

        var rows = new List<(string, Vector3, uint, bool, string)>();
        foreach (KeyValuePair<string, List<Twin>> entry in _byModel)
        {
            foreach (Twin twin in entry.Value)
            {
                if (!GodotObject.IsInstanceValid(twin.Node) || !twin.Node.IsInsideTree())
                {
                    continue;
                }

                bool drawn = twin.Instance < 0
                    ? twin.Node.Visible
                    : twin.Node is MultiMeshInstance3D batched && batched.Multimesh is { } mesh &&
                      twin.Instance < mesh.InstanceCount &&
                      mesh.GetInstanceTransform(twin.Instance).Basis.X.LengthSquared() > 1e-12f;
                if (!drawn)
                {
                    continue;
                }

                Vector3 at = twin.Instance < 0
                    ? twin.Node.GlobalTransform.Origin
                    : (twin.Node.GlobalTransform * twin.Standing).Origin;
                recordOf.TryGetValue((twin.Node, twin.Instance), out uint record);
                rows.Add((entry.Key, at, record, IsAdopted(twin), twin.Node.Name.ToString()));
            }
        }

        return rows;
    }

    private static void HideTwin(Twin twin)
    {
        if (!GodotObject.IsInstanceValid(twin.Node))
        {
            return;
        }

        if (twin.Instance < 0)
        {
            twin.Node.Visible = false;
            return;
        }

        // A MULTIMESH INSTANCE CANNOT BE REMOVED, so it is collapsed instead. Godot's
        // instance buffer is a fixed-length array and `VisibleInstanceCount` only trims a
        // prefix, which is no use for an arbitrary slot. A basis of exactly zero sends every
        // vertex of the instance to one point, so it covers no pixels — and the slot is
        // still there, which is what lets `Show` put it back.
        if (twin.Node is MultiMeshInstance3D batched && batched.Multimesh is { } mesh &&
            twin.Instance < mesh.InstanceCount)
        {
            Refii.Core.Engine_InstanceUpdates.Note(Refii.Core.Engine_InstanceUpdates.Source.TwinDriven);
            Refii.Level.Engine_PropShadow.SetInstanceTransform(mesh, twin.Instance, new Transform3D(
                new Basis(Vector3.Zero, Vector3.Zero, Vector3.Zero), twin.Standing.Origin));
        }
    }

    private void Show(List<Twin> twins)
    {
        foreach (Twin twin in twins)
        {
            if (!IsAdopted(twin))
            {
                ShowTwin(twin);
            }
        }
    }

    private static void ShowTwin(Twin twin)
    {
        {
            if (!GodotObject.IsInstanceValid(twin.Node))
            {
                return;
            }

            if (twin.Instance < 0)
            {
                twin.Node.Visible = true;

                // Its mesh may never have been streamed — the streamer skips hidden props, so a
                // prop that was hidden before its turn came up has no geometry yet.
                if (twin.Node is MeshInstance3D single &&
                    twin.Node.GetParent() is Refii.Level.Engine_PropStreamer streamer)
                {
                    streamer.EnsureStreamed(single);
                }

                return;
            }

            if (twin.Node is MultiMeshInstance3D batched && batched.Multimesh is { } mesh &&
                twin.Instance < mesh.InstanceCount)
            {
                Refii.Core.Engine_InstanceUpdates.Note(Refii.Core.Engine_InstanceUpdates.Source.TwinDriven);
                Refii.Level.Engine_PropShadow.SetInstanceTransform(mesh, twin.Instance, twin.Standing);
                Refii.Level.Engine_LevelProps.CoverInstance(mesh, twin.Standing);
            }
        }
    }
}
