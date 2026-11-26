using Godot;

namespace Refii.Ui;

/// <summary>
/// A slide the guest states, put on a pattern-book clone. The book is built with
/// `KeepInactiveForCatalogue`, so an element inactive at the book's own slide is a parked, hidden
/// node in every clone taken from it — PER ELEMENT, children included. Standing a group by name
/// therefore draws the group and none of its slide-gated children; the stated slide's entries are
/// what switch them (`lhGuiElement_GoToSlide` writes each entry's active bit, the later call wins).
/// </summary>
/// <remarks>
/// Each entry's switch, and its position / scale / rotation.z writes through the pivot-keeping
/// writers. Opacity commands are a tween's start, not its pose, and are not applied. The clone's
/// root keeps its canvas transform (restated by the view) and is never written.
/// </remarks>
public sealed class Engine_UiCloneSlide
{
	private readonly Node3D _root;
	private System.Collections.Generic.Dictionary<int, Node3D>? _byOrdinal;

	public Engine_UiCloneSlide(Node3D root)
	{
		_root = root;
	}

	/// <summary>
	/// The owner's slide numbered as `goToSlide(element, n)` counts: 1-based over its non-master
	/// slides in file order (`querybox.bgf` root: 1 Intro, 2 Outro, 3 IntroXA, 4 OutroXA, 5
	/// IntroReload, 6 OutroReload). Null when the file has no such slide.
	/// </summary>
	public static BgfSlide? Numbered(Engine_BgfDocument? file, int owner, int number)
	{
		if (file is null || number < 1)
		{
			return null;
		}

		int at = 0;
		foreach (BgfSlide slide in file.Slides)
		{
			if (slide.OwnerElement == owner && !slide.IsMaster && ++at == number)
			{
				return slide;
			}
		}

		return null;
	}

	/// <summary>The owner's slide by its authored name; null when the file has none.</summary>
	public static BgfSlide? Named(Engine_BgfDocument? file, int owner, string name)
	{
		foreach (BgfSlide slide in file?.Slides ?? System.Array.Empty<BgfSlide>())
		{
			if (slide.OwnerElement == owner && slide.Name.Equals(name, System.StringComparison.Ordinal))
			{
				return slide;
			}
		}

		return null;
	}

	/// <summary>States one slide's entries on the clone. <paramref name="switchesOnly"/> states the
	/// active bits alone, for a slide whose writes start a motion the clone does not play (the card's
	/// flip).</summary>
	public void Apply(BgfSlide slide, bool switchesOnly = false)
	{
		if (_byOrdinal is null)
		{
			_byOrdinal = new System.Collections.Generic.Dictionary<int, Node3D>();
			Index(_root);
		}

		uint positionX = Refii.Core.Engine_GameHash.AttributeId("position.x");
		uint positionY = Refii.Core.Engine_GameHash.AttributeId("position.y");
		uint scaleX = Refii.Core.Engine_GameHash.AttributeId("scale.x");
		uint scaleY = Refii.Core.Engine_GameHash.AttributeId("scale.y");
		uint rotationZ = Refii.Core.Engine_GameHash.AttributeId("rotation.z");

		foreach (BgfSlideEntry entry in slide.Entries)
		{
			if (!_byOrdinal.TryGetValue(entry.ElementOrdinal, out Node3D? node) || node == _root)
			{
				continue;
			}

			node.Visible = entry.Active;
			if (switchesOnly || entry.Commands is null)
			{
				continue;
			}

			float? x = null, y = null, sx = null, sy = null, rz = null;
			foreach (BgfAttribute command in entry.Commands)
			{
				if (command.Type != BgfValueType.Float)
				{
					continue;
				}

				if (command.Id == positionX) x = command.AsFloat();
				else if (command.Id == positionY) y = command.AsFloat();
				else if (command.Id == scaleX) sx = command.AsFloat();
				else if (command.Id == scaleY) sy = command.AsFloat();
				else if (command.Id == rotationZ) rz = command.AsFloat();
			}

			if (rz is { } turn)
			{
				Engine_UiSceneBuilder.SetLiveRotationZ(node, turn);
			}

			if (sx is > 0f && sy is > 0f)
			{
				Engine_UiSceneBuilder.SetLiveScale(node, new Vector3(sx.Value, sy.Value, 1f));
			}

			if (x is not null || y is not null)
			{
				Vector3 offset = node.Transform.Basis * Engine_UiSceneBuilder.PivotOf(node);
				Vector3 stated = node.Position + offset;
				node.Position = new Vector3(x ?? stated.X, y ?? stated.Y, stated.Z) - offset;
			}
		}
	}

	private void Index(Node node)
	{
		if (node is Node3D element && element.HasMeta("ordinal"))
		{
			_byOrdinal![(int)element.GetMeta("ordinal")] = element;
		}

		foreach (Node child in node.GetChildren())
		{
			Index(child);
		}
	}
}
