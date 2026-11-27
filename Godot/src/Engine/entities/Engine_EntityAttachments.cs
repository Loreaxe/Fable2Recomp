using System.Collections.Generic;
using Godot;
using Refii.Host;

namespace Refii.Entities;

/// <summary>
/// The entities that hang off another entity's bone — weapons, carried props, a sheathed
/// blade — placed from the HOST's pose.
///
/// THE GUEST STATES THE ATTACHMENT, NOT THE PLACEMENT. `CECObjectAttachment` recomputes each
/// attached child's world transform every time the parent moves and writes it into the child's
/// own placement component, composing the parent's dummy with the GUEST's bone pose. Godot now
/// plays the animation itself, so that placement describes a body nobody is drawing: the sword
/// swims a frame behind the back it is strapped to and bounces whenever the two poses disagree.
/// Following the statement instead — this child, that parent, that dummy — puts the child on
/// the bone the host is actually drawing.
///
/// THE OFFSET IS RECOVERED, NOT RE-DERIVED. The guest composes three things: the dummy's local
/// transform out of the parent's `.mdl.gmd`, the live bone pose, and the child's own offset
/// matrix (`record+80`, the rigid inverse of the dummy the child presents). Two of those are
/// constants of the attachment, so their product is one constant transform from the bone to the
/// child — and the guest states that product every frame in the child's placement. Solving for
/// it once, from the guest's own pose and its own stated result, needs no guess about matrix
/// order, quaternion sign or handedness: it is whatever makes the guest's numbers agree.
///
/// The `.gmd` is still read, for one thing only: WHICH BONE. A dummy is a record in the parent
/// model's attachment table naming the node it hangs from, and that node is either a bone or a
/// dummy whose parent chain reaches one — the same walk `sub_8223D160` does when the gmd bone
/// has no live counterpart.
/// </summary>
public sealed class Engine_EntityAttachments
{
	/// <summary>One live attachment, and what the host needs to place it.</summary>
	public sealed class Record
	{
		public uint Child;
		public uint Parent;
		public uint Ticket;
		public string Dummy = string.Empty;
		public int Index;

		/// <summary>The parent's model bone the dummy hangs from, or -1 while unresolved.</summary>
		public int Bone = -1;

		/// <summary>The child's transform relative to that bone. Constant once solved.</summary>
		public Transform3D Offset = Transform3D.Identity;

		public bool Solved;

		/// <summary>Attempts to solve, so a hopeless one stops costing a pose read a frame.</summary>
		public int Attempts;

		/// <summary>Solves taken so far; the offset keeps being re-solved while young.</summary>
		public int Solves;

		/// <summary>How far the offset put the child from the guest's statement, last frame.</summary>
		public float LastResidual = float.NaN;

		/// <summary>Consecutive frames a fresh solve landed far from the held offset.</summary>
		public int Disagreements;

		/// <summary>
		/// The mount bone as the guest last stated it, and whether we have one — the re-solve
		/// gate. See the re-solve in <c>Place</c>.
		/// </summary>
		public Transform3D LastGuestBone = Transform3D.Identity;

		public bool HaveLastGuestBone;

		/// <summary>
		/// The child's offset as the guest states it — record+0x50, the rigid inverse of the
		/// child's own grip dummy — in Godot's axes. Present once RefiiHostGetAttachmentGrips
		/// has answered for this ticket.
		/// </summary>
		public Transform3D Grip = Transform3D.Identity;

		public bool HaveGrip;

		/// <summary>
		/// The parent dummy relative to <see cref="Bone"/> (or to the model root when there is no
		/// bone): its gmd entry's local under the gmd node locals up to the first live bone —
		/// `sub_8223D148`'s accumulation. Composed once; the bone's pose is Godot's, every frame.
		/// </summary>
		public Transform3D Mount = Transform3D.Identity;

		public bool DummyResolved;

		/// <summary>
		/// True when <see cref="ResolveDummy"/> actually resolved <see cref="Dummy"/> on a parent
		/// <c>.gmd</c> sidecar, static <c>.mdl</c> bone table, or character focal bone. When false
		/// for a non-empty <see cref="Dummy"/>, <see cref="TryPlace"/> declines so the guest's own
		/// stated child transform stands rather than snapping to the parent's origin.
		/// </summary>
		public bool DummyFound;

		/// <summary>
		/// True only when <see cref="ResolveDummy"/> fell back to <see cref="TryResolveFocalBone"/>
		/// because no part GMD named the dummy, so <see cref="Mount"/> needs a one-shot rest offset
		/// solved from the guest bone pose.
		/// </summary>
		public bool FocalFallback;

		/// <summary>Whether the stated composition was compared with a solve, for the log.</summary>
		public bool Compared;

		/// <summary>Frames placed by the stated composition; the comparison waits past the switch.</summary>
		public int Placements;

		/// <summary>
		/// The attachment this one replaced on the same parent — the sheath a drawn weapon left —
		/// and the game tick the switch was read on. Until the world's draw point reaches that
		/// tick the previous mount stands: the guest re-attaches on its current tick, and the
		/// hand reaching for the weapon is drawn about a tick behind it.
		/// </summary>
		public Record? Previous;

		public double SwitchTick;

		/// <summary>Where <see cref="ResolveDummy"/> found <see cref="Mount"/>: one of the Mount* names.</summary>
		public string MountSource = string.Empty;

		/// <summary>The exit the last placement took: one of the Exit* names.</summary>
		public string Exit = string.Empty;

		/// <summary>Exit changes logged for this record; see <see cref="Took"/>.</summary>
		public int ExitReports;
	}

	// WHERE ResolveDummy FOUND THE MOUNT. The guest has one source, the part gmd chain to a live
	// bone or the identity (`sub_82237490` TU1 / `sub_822374D0` disc); the others are ours.
	public const string MountGmdBone = "gmd-bone";
	public const string MountGmdRoot = "gmd-root";
	public const string MountGmdFocal = "gmd-focal";
	public const string MountLevelFx = "levelfx-model-space";
	public const string MountFocalName = "focal-name";
	public const string MountIdentity = "identity";

	// THE EXIT A PLACEMENT TOOK. "composed-*" is the guest's composition W . bone . mount . grip
	// (TU1 `sub_825ED2A0`, disc `sub_8250F5F0`) with the mount from that source; "stated-*" left
	// the child on the guest's placement, which is on the guest's pose of the parent, not ours.
	public const string ExitComposedGmdBone = "composed-gmd-bone";
	public const string ExitComposedGmdRoot = "composed-gmd-root";
	public const string ExitComposedGmdFocal = "composed-gmd-focal";
	public const string ExitComposedLevelFx = "composed-levelfx-model-space";
	public const string ExitComposedFocalName = "composed-focal-name";
	public const string ExitComposedIdentity = "composed-identity";
	public const string ExitStatedDummyUnresolved = "stated-dummy-unresolved";
	public const string ExitStatedHandWithoutGrip = "stated-hand-dummy-without-grip";
	public const string ExitStatedCharacterNoBone = "stated-character-dummy-no-bone";
	public const string ExitStatedNoGripNoRig = "stated-no-grip-no-rig";
	public const string ExitStatedSolvePending = "stated-solve-pending";
	public const string ExitStatedSolveExhausted = "stated-solve-exhausted";
	public const string ExitStatedNoBone = "stated-no-bone";
	public const string ExitSolvedOffset = "solved-offset";
	public const string ExitStatedFurniture = "stated-furniture";
	public const string ExitStatedParentNotDrawn = "stated-parent-not-drawn";
	public const string ExitStatedParentNoRig = "stated-parent-no-rig";

	/// <summary>The composed exit for a mount source.</summary>
	public static string ComposedExit(string mountSource) => mountSource switch
	{
		MountGmdBone => ExitComposedGmdBone,
		MountGmdRoot => ExitComposedGmdRoot,
		MountGmdFocal => ExitComposedGmdFocal,
		MountLevelFx => ExitComposedLevelFx,
		MountFocalName => ExitComposedFocalName,
		_ => ExitComposedIdentity,
	};

	/// <summary>How many exit changes one record logs; the first is the one a run asks about.</summary>
	private const int ExitReportsLogged = 4;

	/// <summary>
	/// Records the exit and logs it when it changed, up to <see cref="ExitReportsLogged"/> times
	/// per record. Not a frequency.
	/// </summary>
	private static void Took(Record record, string exit)
	{
		if (ReferenceEquals(record.Exit, exit))
		{
			return;
		}

		record.Exit = exit;
		if (record.ExitReports >= ExitReportsLogged)
		{
			return;
		}

		++record.ExitReports;
		GD.Print($"[attach] {record.Child:X8} on {record.Parent:X8} '{record.Dummy}'[{record.Index}] " +
			$"exit {exit}: mount {(record.MountSource.Length != 0 ? record.MountSource : "unresolved")}, " +
			$"bone {record.Bone}, grip {record.HaveGrip}, dummyFound {record.DummyFound}, " +
			$"focal {record.FocalFallback}, mount at {record.Mount.Origin}");
	}

	/// <summary>The caller left a child on its stated placement without asking TryPlace.</summary>
	public void NoteStated(uint child, string exit)
	{
		if (_byChild.TryGetValue(child, out Record? record))
		{
			Took(record, exit);
		}
	}


	/// <summary>How many solve attempts a record gets before it is left on its stated placement.</summary>
	private const int MaxAttempts = 240;

	/// <summary>Attachments placed from the host's bones this frame.</summary>
	public static int Placed;

	/// <summary>Attachments the host could not resolve to a bone.</summary>
	public static int Unresolved;

	private readonly Dictionary<uint, Record> _byChild = new();

	/// <summary>
	/// Moves whenever a child gains, changes or loses its record: what reads <see cref="ParentOf"/>
	/// once and keeps the answer (the feed's skip index) asks again when this has moved.
	/// </summary>
	public int Version { get; private set; }

	/// <summary>Every child with a record now.</summary>
	public Dictionary<uint, Record>.KeyCollection Children => _byChild.Keys;

	private readonly List<HostBridge.GuestAttachment> _stated = new();

	private readonly Dictionary<uint, (uint Ticket, float[] Matrix)> _grips = new();

	/// <summary>Children placed by the stated composition, and by the old solve; run totals.</summary>
	public static int PlacedStated;

	private static readonly Dictionary<string, Refii.Models.Engine_GmdSkeleton?> GmdCache = new();
	private static readonly Dictionary<string, (bool Found, Vector3 Hold, Vector3 Top)> WeaponBladeSpanCache = new();

	/// <summary>Drops the dummy-name skeleton lookups. Keyed by model path, so region-scoped.</summary>
	public static void ClearCaches()
	{
		lock (GmdCache)
		{
			GmdCache.Clear();
			WeaponBladeSpanCache.Clear();
		}
	}

	/// <summary>
	/// Resolves a melee weapon's authored hilt (<c>Weapon.Weapon.Hold</c>) and tip
	/// (<c>Weapon.Weapon.Top</c>) endpoints in mesh-local Godot space from its cached <c>.mdl.gmd</c>.
	/// </summary>
	public static bool TryResolveWeaponBladeSpan(string modelPath, out Vector3 holdLocal, out Vector3 topLocal)
	{
		holdLocal = Vector3.Zero;
		topLocal = Vector3.Zero;
		if (string.IsNullOrEmpty(modelPath))
		{
			return false;
		}

		lock (GmdCache)
		{
			if (WeaponBladeSpanCache.TryGetValue(modelPath, out (bool Found, Vector3 Hold, Vector3 Top) cached))
			{
				holdLocal = cached.Hold;
				topLocal = cached.Top;
				return cached.Found;
			}

			Refii.Models.Engine_GmdSkeleton? gmd = Gmd(modelPath);
			if (gmd is not null &&
				gmd.TryResolveEntry("Weapon.Weapon.Hold", 0, out int holdNode, out Transform3D holdT) &&
				gmd.TryResolveEntry("Weapon.Weapon.Top", 0, out int topNode, out Transform3D topT))
			{
				Vector3 hold = (holdNode >= 0 ? gmd.ComposeNode(holdNode) * holdT : holdT).Origin;
				Vector3 top = (topNode >= 0 ? gmd.ComposeNode(topNode) * topT : topT).Origin;
				if (hold.IsFinite() && top.IsFinite() && (top - hold).LengthSquared() > 1e-4f)
				{
					WeaponBladeSpanCache[modelPath] = (true, hold, top);
					holdLocal = hold;
					topLocal = top;
					return true;
				}
			}

			WeaponBladeSpanCache[modelPath] = (false, Vector3.Zero, Vector3.Zero);
			return false;
		}
	}

	/// <summary>The guest's current statement, folded into the table. Cheap; call on a cadence.</summary>
	public void Refresh()
	{
		if (HostBridge.GetAttachments(_stated) == 0 && _byChild.Count == 0)
		{
			return;
		}

		foreach (HostBridge.GuestAttachment stated in _stated)
		{
			if (_byChild.TryGetValue(stated.Child, out Record? record))
			{
				// A RE-ATTACH IS A NEW TICKET. Same child, same parent, different dummy — a
				// weapon drawn from the back into the hand — and the solved offset from the
				// old dummy would hold the sword out sideways for the rest of the run.
				if (record.Ticket == stated.Ticket && record.Parent == stated.Parent)
				{
					continue;
				}

				_byChild.Remove(stated.Child);
			}

			++Version;
			_byChild[stated.Child] = new Record
			{
				Child = stated.Child,
				Parent = stated.Parent,
				Ticket = stated.Ticket,
				Dummy = stated.Dummy,
				Index = stated.Index,
				Previous = record is { HaveGrip: true } replaced && replaced.Parent == stated.Parent
					? replaced
					: null,
				SwitchTick = Refii.Core.Engine_TickClock.GuestGameTick,
			};
		}

		// The stated offsets, for every record that has not taken one for its current ticket —
		// read only when such a record exists: a grip is constant for its attachment's life.
		bool needGrip = false;
		foreach (Record held in _byChild.Values)
		{
			if (!held.HaveGrip)
			{
				needGrip = true;
				break;
			}
		}

		if (needGrip && HostBridge.GetAttachmentGrips(_grips))
		{
			foreach ((uint child, (uint ticket, float[] matrix)) in _grips)
			{
				if (_byChild.TryGetValue(child, out Record? held) && held.Ticket == ticket && !held.HaveGrip)
				{
					held.Grip = GripToGodot(matrix);
					held.HaveGrip = held.Grip.Origin.IsFinite();
				}
			}
		}

		if (_byChild.Count == _stated.Count)
		{
			return;
		}

		// Detached children go back to their own placement: the guest stops writing them from
		// the parent and starts moving them itself (the dropped weapon falls).
		var live = new HashSet<uint>();
		foreach (HostBridge.GuestAttachment stated in _stated)
		{
			live.Add(stated.Child);
		}

		var gone = new List<uint>();
		foreach (uint child in _byChild.Keys)
		{
			if (!live.Contains(child))
			{
				gone.Add(child);
			}
		}

		foreach (uint child in gone)
		{
			_byChild.Remove(child);
		}

		if (gone.Count != 0)
		{
			++Version;
		}
	}

	/// <summary>Drops every record naming a retired entity, as child or as parent.</summary>
	public void Forget(uint entity)
	{
		++Version;
		_byChild.Remove(entity);
		var orphans = new List<uint>();
		foreach ((uint child, Record record) in _byChild)
		{
			if (record.Parent == entity)
			{
				orphans.Add(child);
			}
		}

		foreach (uint child in orphans)
		{
			_byChild.Remove(child);
		}
	}

	/// <summary>Re-solves taken because an offset missed the guest's statement.</summary>
	public static int Resolves;

	/// <summary>Every record hanging on one parent, for the debug channel.</summary>
	public List<Dictionary<string, object?>> Describe(uint parent, Engine_EntitySkeleton? rig)
	{
		var rows = new List<Dictionary<string, object?>>();
		foreach (Record record in _byChild.Values)
		{
			if (record.Parent != parent)
			{
				continue;
			}

			rows.Add(new Dictionary<string, object?>
			{
				["child"] = $"{record.Child:X8}",
				["dummy"] = record.Dummy,
				["ticket"] = record.Ticket,
				["bone"] = record.Bone,
				["boneName"] = rig is not null && record.Bone >= 0 ? rig.BoneName(record.Bone) : null,
				["solved"] = record.Solved,
				["offset"] = new[] { record.Offset.Origin.X, record.Offset.Origin.Y, record.Offset.Origin.Z },
				// Finite or null: a NaN residual (never solved) or an infinite offset is not a
				// JSON number, and one record took the whole entity reply down with it.
				["offsetMetres"] = float.IsFinite(record.Offset.Origin.Length()) ? record.Offset.Origin.Length() : null,
				["residual"] = float.IsFinite(record.LastResidual) ? record.LastResidual : null,
				["scored"] = record.Solves,
				["exit"] = record.Exit,
				["mountSource"] = record.MountSource,
				["dummyFound"] = record.DummyFound,
				["focalFallback"] = record.FocalFallback,
				["haveGrip"] = record.HaveGrip,
				["mount"] = new[] { record.Mount.Origin.X, record.Mount.Origin.Y, record.Mount.Origin.Z },
				["grip"] = new[] { record.Grip.Origin.X, record.Grip.Origin.Y, record.Grip.Origin.Z },
			});
		}

		return rows;
	}

	/// <summary>The parent this child hangs on, or 0.</summary>
	public uint ParentOf(uint child) =>
		_byChild.TryGetValue(child, out Record? record) ? record.Parent : 0u;

	/// <summary>The dummy name <paramref name="child"/> hangs from on its parent, if attached.</summary>
	public bool TryGetDummyName(uint child, out string dummy)
	{
		if (_byChild.TryGetValue(child, out Record? record) && !string.IsNullOrEmpty(record.Dummy))
		{
			dummy = record.Dummy;
			return true;
		}

		dummy = string.Empty;
		return false;
	}

	/// <summary>Enumerates child entities attached to the given parent entity.</summary>
	public List<uint> ChildrenOfParent(uint parent)
	{
		var result = new List<uint>();
		ChildrenOfParent(parent, result);
		return result;
	}

	/// <summary>Fills <paramref name="into"/> with child entities attached to <paramref name="parent"/> without allocating.</summary>
	public void ChildrenOfParent(uint parent, List<uint> into)
	{
		into.Clear();
		foreach (Record record in _byChild.Values)
		{
			if (record.Parent == parent)
			{
				into.Add(record.Child);
			}
		}
	}

	public int Count => _byChild.Count;

	/// <summary>
	/// Where the child belongs given the parent as the host is drawing it. False leaves the
	/// caller on the guest's stated placement — the honest fallback while a record is
	/// unsolved, and the permanent answer for a dummy no `.gmd` names.
	/// </summary>
	/// <param name="childStated">The guest's own placement for the child, for the solve.</param>
	/// <param name="parentStated">
	/// The parent's own stated placement — NOT the blended one it is drawn at. The guest
	/// composed the child's placement against this transform, and solving against a parent that
	/// is a fraction of a tick behind bakes that fraction into the offset for good.
	/// </param>
	public bool TryPlace(uint child, Transform3D childStated, Transform3D parentStated,
		Transform3D parentPlacement, Engine_EntitySkeleton? parentRig, string[] parentParts,
		out Transform3D placed, bool isFurniture = false, string? childModelPath = null)
	{
		placed = childStated;
		if (!_byChild.TryGetValue(child, out Record? record))
		{
			return false;
		}

		// THE GUEST STATES THE OFFSET; COMPOSE IT. `sub_8250F5F0`: child = record+0x50 . D . W
		// (row vectors), D the parent dummy on the LIVE pose. In Godot's order that is
		// W x bone x dummy x grip, with Godot's own bone — so the child is rigid on the body
		// that is drawn, every frame, with nothing solved from reads taken at different instants.
		// A dummy no gmd names is the identity (`sub_822374D0`'s last fallback): the child sits at
		// the parent's root, still composed, never the raw stated placement.
		if (record.HaveGrip)
		{
			Record use = record;
			if (record.Previous is { } previous)
			{
				double drawTick = Refii.Entities.Engine_DrawnFrame.DrawTick;
				if (!double.IsNaN(drawTick) && drawTick < record.SwitchTick)
				{
					use = previous;
				}
				else
				{
					record.Previous = null;
				}
			}

			if (!use.DummyResolved)
			{
				ResolveDummy(use, parentRig, parentParts);
			}

			// If a named dummy could not be resolved on any parent GMD, static .mdl bone table, or
			// character focal bone, decline host re-placement so the guest's own stated child world
			// transform stands instead of collapsing onto `parentPlacement` with an identity mount.
			if (!use.DummyFound && use.Dummy.Length != 0)
			{
				++Unresolved;
				Took(record, ExitStatedDummyUnresolved);
				return false;
			}

			// Objects classified as furniture (chairs, benches, stools) being dragged on the floor
			// must remain grounded (stated). Handheld props (mugs, weapons, bowls, tools, food)
			// mount cleanly to the character's hand bone dummy (Carry_Hand_DummyObject1).
			if (IsHandDummyWithoutGrip(use.Dummy, use.Grip, childModelPath, isFurniture))
			{
				Took(record, ExitStatedHandWithoutGrip);
				return false;
			}

			if (use.Bone < 0 && parentRig is not null &&
			    use.Dummy.StartsWith("Character.", System.StringComparison.OrdinalIgnoreCase))
			{
				Took(record, ExitStatedCharacterNoBone);
				return false;
			}

			if (use.FocalFallback && parentRig is not null && use.Bone >= 0 && !record.Solved &&
			    parentRig.TryGuestBoneGlobal(record.Parent, use.Bone, out Transform3D guestBone))
			{
				Transform3D from = parentStated * guestBone;
				if (from.Origin.IsFinite() && Mathf.Abs(from.Basis.Determinant()) > 1e-6f)
				{
					Transform3D candidate = from.AffineInverse() * childStated;
					if (candidate.Origin.IsFinite() && candidate.Origin.Length() < 4f)
					{
						if (use.Mount.IsEqualApprox(Transform3D.Identity))
						{
							use.Mount = candidate * use.Grip.AffineInverse();
						}
						record.Solved = true;
					}
				}
			}

			Transform3D mount = parentRig is not null && use.Bone >= 0 && use.Bone < parentRig.BoneCount
				? parentRig.Node.GetBoneGlobalPose(use.Bone) * use.Mount
				: use.Mount;
			placed = parentPlacement * mount * use.Grip;
			if (parentRig is not null && use == record && !record.Compared && ++record.Placements >= 10)
			{
				Compare(record, childStated, parentStated, parentRig, parentParts, mount);
			}

			Took(record, ComposedExit(use.MountSource));
			++Placed;
			++PlacedStated;
			return true;
		}

		if (parentRig is null)
		{
			Took(record, ExitStatedNoGripNoRig);
			return false;
		}

		if (!record.Solved)
		{
			if (record.Attempts >= MaxAttempts)
			{
				Took(record, ExitStatedSolveExhausted);
				return false;
			}

			++record.Attempts;
			if (!Solve(record, childStated, parentStated, parentRig, parentParts))
			{
				Took(record, record.Attempts == MaxAttempts ? ExitStatedSolveExhausted : ExitStatedSolvePending);
				if (record.Attempts == MaxAttempts)
				{
					++Unresolved;
					GD.Print($"[attach] '{record.Dummy}'[{record.Index}] on " +
						$"{record.Parent:X8} named no bone this host has — child " +
						$"{record.Child:X8} stays on its stated placement");
				}

				return false;
			}
		}
		else if (record.Bone >= 0 &&
			parentRig.TryGuestBoneGlobal(record.Parent, record.Bone, out Transform3D guestBone))
		{
			// RE-SOLVE ON A CANDIDATE, NOT ON A RESIDUAL. The child's placement is written on the
			// guest's tick and the pose is read now, so while the body moves the two disagree by
			// a tick's travel — a steady 3 cm on a walking hero, which a residual threshold
			// re-solved every frame (630 re-solves in seconds, the sword flipping between two
			// offsets). A torn read, the thing this guards against, gives a candidate offset
			// metres away; phase gives centimetres. So the held offset is replaced only when a
			// fresh solve lands more than ten centimetres from it three frames running.
			Transform3D from = parentStated * guestBone;
			record.LastResidual = (from * record.Offset).Origin.DistanceTo(childStated.Origin);
			++record.Solves;

			// RE-SOLVE ONLY WHILE THE MOUNT IS NEARLY STILL. The two halves of a solve sit on
			// different clocks — the child's placement is stated on the guest's tick, the bone
			// is read from the 30 Hz pose array now — so the error between them is proportional
			// to how fast that bone is TRAVELLING. Walking gives the few centimetres the ten
			// centimetre threshold below was chosen for. Swinging a blacksmith's hammer gives
			// far more, and three frames of that in a row is all it takes to bake the phase
			// error in as the mount itself. Measured after one job: sheathed weapons solved to
			// 0.31 m and 0.41 m from their own dummy bones, against 0.13 m to 0.17 m for the
			// ones solved at rest — a sword hanging a third of a metre off the body.
			//
			// A mount is rigid, so a sample taken while the bone is still is the honest one and
			// costs nothing to wait for. The FIRST solve is left alone: this only stops a good
			// offset being corrupted, which is the failure — the weapons are right until a job
			// is played.
			float travelled = record.HaveLastGuestBone
				? guestBone.Origin.DistanceTo(record.LastGuestBone.Origin)
				: 0f;
            bool settled = record.HaveLastGuestBone && travelled < 0.02f;
			record.LastGuestBone = guestBone;
			record.HaveLastGuestBone = true;

			if (settled && Mathf.Abs(from.Basis.Determinant()) > 1e-6f)
			{
				Transform3D candidate = from.AffineInverse() * childStated;
				if (candidate.Origin.IsFinite() &&
					candidate.Origin.DistanceTo(record.Offset.Origin) > 0.10f)
				{
					if (++record.Disagreements >= 3)
					{
						record.Offset = candidate;
						record.Disagreements = 0;
						++Resolves;
					}
				}
				else
				{
					record.Disagreements = 0;
				}
			}
		}

		if (record.Bone < 0 || record.Bone >= parentRig.BoneCount)
		{
			Took(record, ExitStatedNoBone);
			return false;
		}

		placed = parentPlacement * parentRig.Node.GetBoneGlobalPose(record.Bone) * record.Offset;
		Took(record, ExitSolvedOffset);
		++Placed;
		return true;
	}

	private static bool Solve(Record record, Transform3D childStated,
		Transform3D parentStated, Engine_EntitySkeleton parentRig, string[] parentParts)
	{
		if (record.Bone < 0 && !TryBone(record, parentRig, parentParts))
		{
			return false;
		}

		// THE GUEST'S OWN POSE, not the one the host is playing. The stated child placement was
		// composed against the guest's bones, so the offset only comes out constant when both
		// sides of the solve are the guest's.
		if (!parentRig.TryGuestBoneGlobal(record.Parent, record.Bone, out Transform3D bone))
		{
			return false;
		}

		Transform3D from = parentStated * bone;
		if (!from.Origin.IsFinite() || Mathf.Abs(from.Basis.Determinant()) < 1e-6f)
		{
			return false;
		}

		Transform3D offset = from.AffineInverse() * childStated;
		if (!offset.Origin.IsFinite() || offset.Origin.Length() > 8f)
		{
			// A metre or two is a sheath; eight is a torn read of one of the three transforms.
			return false;
		}

		record.Offset = offset;
		record.Solved = true;

		if (Solves < SolvesLogged)
		{
			++Solves;
			GD.Print($"[attach] {record.Child:X8} on {record.Parent:X8} " +
				$"'{record.Dummy}'[{record.Index}] -> bone {record.Bone} " +
				$"'{parentRig.BoneName(record.Bone)}', offset {offset.Origin} " +
				$"({offset.Origin.Length():0.###} m from the bone)");
		}

		return true;
	}

	/// <summary>Solves reported, so the first few can be read back and checked against the model.</summary>
	private static int Solves;

	private const int SolvesLogged = 8;

	/// <summary>
	/// The parent's model bone a stated dummy hangs from. The dummy is a record in one of the
	/// parent model's `.mdl.gmd` attachment tables — a morph character carries one per part and
	/// the guest takes the first that has the name — and the node it names is either a bone or a
	/// dummy node whose parent chain reaches one.
	/// </summary>
	private static bool TryBone(Record record, Engine_EntitySkeleton rig, string[] parentParts)
	{
		foreach (string part in parentParts)
		{
			Refii.Models.Engine_GmdSkeleton? gmd = Gmd(part);
			if (gmd is null || !gmd.TryResolveNode(record.Dummy, record.Index, out int node))
			{
				continue;
			}

			for (int guard = 0; node >= 0 && node < gmd.Names.Length && guard < 64; ++guard)
			{
				if (rig.TryBone(gmd.Names[node], out int bone))
				{
					record.Bone = bone;
					return true;
				}

				node = gmd.Parents[node];
			}
		}

		if (TryResolveFocalBone(record.Dummy, rig, out int focalBone))
		{
			record.Bone = focalBone;
			return true;
		}

		return false;
	}

	/// <summary>
	/// Converts a model-space focal chain transform into <paramref name="boneGlobalRest"/>-local space
	/// when the dummy lies within plausible proximity of that rest bone, or returns Identity when the
	/// transform is degenerate or outside local socket range.
	/// </summary>
	public static Transform3D ComputeFocalMount(Transform3D modelSpaceChain, Transform3D boneGlobalRest)
	{
		if (!modelSpaceChain.Origin.IsFinite() ||
			!boneGlobalRest.Origin.IsFinite() ||
			Mathf.Abs(boneGlobalRest.Basis.Determinant()) <= 1e-6f ||
			modelSpaceChain.Origin.DistanceTo(boneGlobalRest.Origin) >= 0.75f)
		{
			return Transform3D.Identity;
		}

		Transform3D local = boneGlobalRest.AffineInverse() * modelSpaceChain;
		return local.Origin.IsFinite() ? local : Transform3D.Identity;
	}

	/// <summary>
	/// A dummy no part's <c>.mdl.gmd</c> names, on the rig bone the title mounts it on
	/// (<see cref="Refii.Animations.Engine_TitleRig.FocalBone"/>); false when it mounts none.
	/// </summary>
	private static bool TryResolveFocalBone(string dummy, Engine_EntitySkeleton? rig, out int bone)
	{
		bone = -1;
		if (rig is null || string.IsNullOrEmpty(dummy))
		{
			return false;
		}

		bone = Refii.Core.Engine_TitleGame.Active.Rig.FocalBone(rig.BoneNames, dummy, rig.IsPosedPredicate);
		return bone >= 0;
	}

	/// <summary>
	/// Tests whether a dummy is a hand attachment whose child is world furniture or a static prop
	/// being manipulated on the floor rather than a handheld item.
	/// Handheld items (mugs, weapons, tools, food) mount directly to the character's hand bone dummy
	/// (Carry_Hand_DummyObject1), while world furniture remains grounded on the floor (stated).
	/// </summary>
	public static bool IsHandDummyWithoutGrip(string dummy, Transform3D grip, string? childModelPath = null, bool? isFurniture = null)
	{
		if (!dummy.StartsWith("Character.Carry.Hand", System.StringComparison.OrdinalIgnoreCase))
		{
			return false;
		}

		if (isFurniture.HasValue)
		{
			return isFurniture.Value;
		}

		if (!string.IsNullOrEmpty(childModelPath))
		{
			return IsFurnitureModelPath(childModelPath);
		}

		return grip.IsEqualApprox(Transform3D.Identity);
	}

	/// <summary>
	/// Tests whether a model path represents furniture, seating, or static fixtures that must
	/// remain grounded on the floor (stated) rather than following animated character hand bones.
	/// In Fable 2, all furniture models reside in the Furniture folder or have the '_F_' prefix.
	/// </summary>
	public static bool IsFurnitureModelPath(string path)
	{
		if (string.IsNullOrEmpty(path))
		{
			return false;
		}

		// Furniture folder or prefix
		if (path.Contains("Furniture", System.StringComparison.OrdinalIgnoreCase) ||
			path.Contains("_F_", System.StringComparison.OrdinalIgnoreCase))
		{
			return true;
		}

		// Specific furniture types
		if (path.Contains("Chair", System.StringComparison.OrdinalIgnoreCase) ||
			path.Contains("Stool", System.StringComparison.OrdinalIgnoreCase) ||
			path.Contains("Bench", System.StringComparison.OrdinalIgnoreCase) ||
			path.Contains("Armchair", System.StringComparison.OrdinalIgnoreCase) ||
			path.Contains("Throne", System.StringComparison.OrdinalIgnoreCase) ||
			path.Contains("Sofa", System.StringComparison.OrdinalIgnoreCase) ||
			path.Contains("Couch", System.StringComparison.OrdinalIgnoreCase) ||
			path.Contains("Seat", System.StringComparison.OrdinalIgnoreCase) ||
			path.Contains("Desk", System.StringComparison.OrdinalIgnoreCase) ||
			path.Contains("Counter", System.StringComparison.OrdinalIgnoreCase) ||
			path.Contains("Bed", System.StringComparison.OrdinalIgnoreCase) ||
			path.Contains("BabyCot", System.StringComparison.OrdinalIgnoreCase) ||
			path.Contains("Cot_", System.StringComparison.OrdinalIgnoreCase) ||
			path.Contains("_Cot", System.StringComparison.OrdinalIgnoreCase) ||
			path.Contains("Cupboard", System.StringComparison.OrdinalIgnoreCase) ||
			path.Contains("Wardrobe", System.StringComparison.OrdinalIgnoreCase) ||
			path.Contains("Dresser", System.StringComparison.OrdinalIgnoreCase) ||
			path.Contains("Drawers", System.StringComparison.OrdinalIgnoreCase) ||
			path.Contains("Cabinet", System.StringComparison.OrdinalIgnoreCase) ||
			path.Contains("Bookcase", System.StringComparison.OrdinalIgnoreCase) ||
			path.Contains("Shelf", System.StringComparison.OrdinalIgnoreCase) ||
			path.Contains("Shelves", System.StringComparison.OrdinalIgnoreCase) ||
			path.Contains("Stove", System.StringComparison.OrdinalIgnoreCase) ||
			path.Contains("Fireplace", System.StringComparison.OrdinalIgnoreCase))
		{
			return true;
		}

		// Table (excluding Vegetable and Stable)
		if (path.Contains("Table", System.StringComparison.OrdinalIgnoreCase) &&
			!path.Contains("Vegetable", System.StringComparison.OrdinalIgnoreCase) &&
			!path.Contains("Stable", System.StringComparison.OrdinalIgnoreCase))
		{
			return true;
		}

		return false;
	}

	/// <summary>
	/// The guest's row-major, row-vector 4x4 in Godot's axes: rows 0-2 are the images of the
	/// basis axes (Godot's columns), row 3 the translation, conjugated by the same Z-up to Y-up
	/// map the gmd transforms and the bones take.
	/// </summary>
	private static Transform3D GripToGodot(float[] m)
	{
		var basis = new Basis(
			new Vector3(m[0], m[1], m[2]),
			new Vector3(m[4], m[5], m[6]),
			new Vector3(m[8], m[9], m[10]));
		Basis map = Refii.Models.Engine_GmdSkeleton.ZUpToYUp;
		return new Transform3D(map * basis * map.Inverse(), map * new Vector3(m[12], m[13], m[14]));
	}

	/// <summary>
	/// The parent dummy, as `sub_8223B890` composes it: the gmd entry's local, under the locals of
	/// every gmd node between it and the first node the live skeleton has (`sub_8223D148` climbs
	/// child first). The first part gmd that names the dummy wins, as the guest's comp17 walk does.
	/// </summary>
	private static void ResolveDummy(Record record, Engine_EntitySkeleton? rig, string[] parentParts)
	{
		record.DummyResolved = true;
		record.DummyFound = false;
		record.FocalFallback = false;
		record.Bone = -1;
		record.Mount = Transform3D.Identity;
		record.MountSource = MountIdentity;
		foreach (string part in parentParts)
		{
			Refii.Models.Engine_GmdSkeleton? gmd = Gmd(part);
			if (gmd is null || !gmd.TryResolveEntry(record.Dummy, record.Index, out int node, out Transform3D local))
			{
				continue;
			}

			Transform3D chain = local;
			for (int guard = 0; node >= 0 && node < gmd.Names.Length && guard < 64; ++guard)
			{
				if (rig is not null && rig.TryBone(gmd.Names[node], out int bone))
				{
					record.Bone = bone;
					break;
				}

				chain = gmd.Locals[node] * chain;
				node = gmd.Parents[node];
			}

			if (record.Bone < 0 && rig is not null && TryResolveFocalBone(record.Dummy, rig, out int focalBone))
			{
				record.Bone = focalBone;
				record.FocalFallback = true;
				record.Mount = ComputeFocalMount(chain, rig.Node.GetBoneGlobalRest(focalBone));
				record.DummyFound = true;
				record.MountSource = MountGmdFocal;
				return;
			}

			record.Mount = chain;
			record.DummyFound = true;
			record.MountSource = record.Bone >= 0 ? MountGmdBone : MountGmdRoot;
			return;
		}

		if (rig is null)
		{
			foreach (string part in parentParts)
			{
				if (Refii.Level.Engine_LevelFx.TryDummyTransform(part, record.Dummy, out Transform3D fallbackMount) ||
					(Refii.Level.Engine_LevelProps.CompanionPath(part) is { Length: > 0 } companion &&
					 Refii.Level.Engine_LevelFx.TryDummyTransform(companion, record.Dummy, out fallbackMount)))
				{
					record.Mount = fallbackMount;
					record.DummyFound = true;
					record.MountSource = MountLevelFx;
					return;
				}
			}
		}

		if (record.Bone < 0 && TryResolveFocalBone(record.Dummy, rig, out int focalFallback))
		{
			record.Bone = focalFallback;
			record.Mount = Transform3D.Identity;
			record.DummyFound = true;
			record.FocalFallback = true;
			record.MountSource = MountFocalName;
		}
	}

	/// <summary>
	/// A MODEL HELPER BY NAME, for anything that stands at one (a mod's play_fx_on `at`): the
	/// first part gmd of <paramref name="parts"/> naming it wins, as the guest's comp17 walk does
	/// (<see cref="ResolveDummy"/>). <paramref name="bone"/> is the model bone of
	/// <paramref name="rig"/> the helper hangs from (-1 with no rig, or when its node chain reaches
	/// no live bone) and <paramref name="mount"/> the helper's transform RELATIVE TO THAT BONE —
	/// or, with no bone, in the model's own space (Godot Y-up) — so the node-local frame is
	/// <c>bone global pose * mount</c>, or <c>mount</c>. <paramref name="rest"/> is the helper's
	/// rest transform in model space either way (the classic look's point). False for a name no
	/// part gmd has; the entity's own frame is then the caller's answer.
	/// </summary>
	public static bool TryResolveHelper(string helper, Engine_EntitySkeleton? rig, IReadOnlyList<string> parts,
		out int bone, out Transform3D mount, out Transform3D rest)
	{
		bone = -1;
		mount = Transform3D.Identity;
		rest = Transform3D.Identity;
		if (helper.Length == 0)
		{
			return false;
		}

		foreach (string part in parts)
		{
			Refii.Models.Engine_GmdSkeleton? gmd = Gmd(part);
			if (gmd is null || !gmd.TryResolveEntry(helper, 0, out int node, out Transform3D local))
			{
				continue;
			}

			rest = node >= 0 && node < gmd.Names.Length ? gmd.ComposeNode(node) * local : local;
			Transform3D chain = local;
			for (int guard = 0; node >= 0 && node < gmd.Names.Length && guard < 64; ++guard)
			{
				if (rig is not null && rig.TryBone(gmd.Names[node], out int live))
				{
					bone = live;
					break;
				}

				chain = gmd.Locals[node] * chain;
				node = gmd.Parents[node];
			}

			if (bone < 0 && rig is not null && TryResolveFocalBone(helper, rig, out int focalBone))
			{
				Transform3D boneRest = rig.Node.GetBoneGlobalRest(focalBone);
				bone = focalBone;
				mount = ComputeFocalMount(chain, boneRest);
				rest = boneRest * mount;
				return true;
			}

			mount = chain;
			return true;
		}

		if (rig is not null && TryResolveFocalBone(helper, rig, out int fallbackBone))
		{
			bone = fallbackBone;
			mount = Transform3D.Identity;
			rest = rig.Node.GetBoneGlobalRest(fallbackBone);
			return true;
		}

		return false;
	}

	/// <summary>Dummies whose stated composition has been compared and logged: once per name.</summary>
	private static readonly HashSet<string> ComparedNames = new(System.StringComparer.OrdinalIgnoreCase);

	private const int ComparedLogged = 32;

	/// <summary>
	/// Once per dummy name, ten placements in: the stated composition beside the old solve
	/// from the guest's own numbers, so a convention error shows up as a fixed, readable offset in
	/// the first run rather than as a weapon in the wrong place.
	/// </summary>
	private static void Compare(Record record, Transform3D childStated, Transform3D parentStated,
		Engine_EntitySkeleton rig, string[] parentParts, Transform3D mount)
	{
		record.Compared = true;
		if (ComparedNames.Count >= ComparedLogged || ComparedNames.Contains(record.Dummy) || record.Bone < 0 ||
			!rig.TryGuestBoneGlobal(record.Parent, record.Bone, out Transform3D guestBone))
		{
			return;
		}

		ComparedNames.Add(record.Dummy);
		Transform3D guestMount = parentStated * guestBone * record.Mount;
		Transform3D composed = guestMount * record.Grip;
		float angle = Mathf.RadToDeg(composed.Basis.GetRotationQuaternion().AngleTo(
			childStated.Basis.GetRotationQuaternion()));
		GD.Print($"[attach] stated {record.Child:X8} '{record.Dummy}'[{record.Index}] -> bone " +
			$"{record.Bone} '{rig.BoneName(record.Bone)}': composed on the guest's pose lands " +
			$"{composed.Origin.DistanceTo(childStated.Origin):0.000} m and {angle:0.0} deg from the " +
			$"guest's own placement (grip {record.Grip.Origin.Length():0.000} m, dummy " +
			$"{record.Mount.Origin.Length():0.000} m)");
	}

	private static Refii.Models.Engine_GmdSkeleton? Gmd(string modelPath)
	{
		if (GmdCache.TryGetValue(modelPath, out Refii.Models.Engine_GmdSkeleton? cached))
		{
			return cached;
		}

		Refii.Models.Engine_GmdSkeleton? gmd = Refii.Models.Engine_GmdSkeleton.TryParse(
			Refii.Models.Engine_BnkMeshLoader.ReadBytes(modelPath + ".gmd"));
		GmdCache[modelPath] = gmd;
		return gmd;
	}
}
