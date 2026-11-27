namespace Refii.Titles.Fable3;

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using Godot;
using Refii.Host;
using Refii.Models;

/// <summary>
/// Fable 3's map tables — the world map (GUI.ToggleWorldMap) and the town map
/// (GUI.ToggleTownMapPrototype) — drawn from what the guest states
/// (Fable3/src/hooks/src/map_table_hooks.cpp, read by RefiiHostGetFable3MapTable).
/// </summary>
/// <remarks>
/// THE GUEST PRESENTS A SCENE OF ITS OWN. Each map builds its table in a render scene its holder
/// owns and, while it is up, hands the engine that scene and its camera in place of the world's
/// (sub_8292AEA8 -> sub_822E26D0); the world is not drawn under it. So the table stands in its
/// own world here, full screen over Godot's, looking through the holder's camera, and the world
/// is back the moment the guest un-presents.
///
/// EACH PIECE IS A MESH THE GUEST ADDED to that scene (sub_8292AF98: the model's resource id at
/// message +112, its orientation, position and scale), moved by the commands its map entity sends
/// when they change (sub_821F5110: 3 position and orientation, 6 scale, 15 tint, 14 alpha). The
/// model comes through the props index and the `game://` loader every world mesh does.
///
/// The input is the guest's: the map's Lua (WorldMap:ProcessGameAction) moves the camera and the
/// pieces, and this view follows. The keyboard and mouse reach it as the pad does, by the PC
/// build's map bindings (<see cref="Fable3Input.MapUp"/>).
///
/// THE CLOSE IS THE GUEST'S TOO. B (the right button, Escape) is the map's cancel: its Lua steps
/// out one view at a time, asks its own questions and at the top calls WorldMap.Exit; START is its
/// exit to the game. Both stand behind the map's own gates (the back-out ability among them). The
/// host's own close request is the guest tick action `world-map:close:&lt;p&gt;`
/// (Fable3/src/hooks/src/world_map_close.cpp), that same WorldMap.Exit on the map's mode. Never
/// GUI.ToggleWorldMap: with the map up it only pops the mode, and the scene stays presented with
/// no input left to reach it.
/// </remarks>
public partial class Fable3MapTable : SubViewportContainer
{
	private const int HeaderWords = 14;
	private const int RowWords = 18;
	private const int MaxRows = 2048;
	private static readonly StringName ClassicTint = "ps_g_StandardMaterialTintColour";
	private static readonly StringName ModernTint = "entity_tint";

	private sealed class Piece
	{
		public required MeshInstance3D Node;
		public uint Model;
		public long Seen;
	}

	private readonly uint[] _words = new uint[HeaderWords + MaxRows * RowWords];
	private readonly Dictionary<uint, Piece> _pieces = new();
	private readonly List<uint> _gone = new();
	private readonly Dictionary<uint, Mesh?> _meshes = new();
	private readonly HashSet<uint> _loading = new();
	private readonly ConcurrentQueue<(uint Hash, Mesh? Mesh)> _loaded = new();
	private readonly HashSet<uint> _reported = new();
	private SubViewport _viewport = null!;
	private Node3D _root = null!;
	private Camera3D _camera = null!;
	private uint _holder;

	/// <summary>
	/// Whether the guest presents a map table now (its scene in place of the world, sub_8292AEA8):
	/// the map has the pad — B backs out (GAME_ACTION_WORLD_MAP_CANCEL), so the keys take their menu meanings.
	/// </summary>
	public static bool Presented { get; private set; }

	/// <summary>
	/// A world map scene still presented after its game mode left (<see cref="Fable3WorldMapMode.Stranded"/>):
	/// nothing of the guest's reads the map's input any more, so the keys go back to the game under it.
	/// </summary>
	public static bool Stranded => Fable3WorldMapMode.Stranded(Presented);

	private uint _serial = uint.MaxValue;
	private long _frames;
	private bool _bridgeMissing;

	public override void _Ready()
	{
		// Anchors AND offsets, as Fable3Canvas: anchors alone leave the container 0×0 under the
		// CanvasLayer and its stretched viewport 2×2 — nothing rendered.
		SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);
		MouseFilter = MouseFilterEnum.Ignore;
		Stretch = true;
		Visible = false;

		_viewport = new SubViewport
		{
			Name = "Table",
			OwnWorld3D = true,
			TransparentBg = false,
			HandleInputLocally = false,
			RenderTargetUpdateMode = SubViewport.UpdateMode.Disabled,
		};
		AddChild(_viewport);

		_root = new Node3D { Name = "Root" };
		_viewport.AddChild(_root);
		_camera = new Camera3D { Name = "MapCamera", Current = true };
		_root.AddChild(_camera);

		// The guest lights its table through render objects of its own (the component's Lights,
		// sub_824CDF40, and the holder's sun message, sub_8292B100) that no feed states yet; until
		// one does, a plain key light and fill so the pieces read.
		_root.AddChild(new WorldEnvironment
		{
			Name = "Environment",
			Environment = new Godot.Environment
			{
				BackgroundMode = Godot.Environment.BGMode.Color,
				BackgroundColor = Colors.Black,
				AmbientLightSource = Godot.Environment.AmbientSource.Color,
				AmbientLightColor = new Color(0.55f, 0.55f, 0.55f),
			},
		});
		_root.AddChild(new DirectionalLight3D
		{
			Name = "Key",
			RotationDegrees = new Vector3(-55f, 30f, 0f),
		});
	}

	public override void _Process(double delta)
	{
		// The container covers the window: its parent is a CanvasLayer, not a Control, so nothing
		// else resizes it when the window does.
		Vector2 cover = GetViewportRect().Size;
		if (Size != cover && cover.X > 0f && cover.Y > 0f)
		{
			Position = Vector2.Zero;
			Size = cover;
		}

		while (_loaded.TryDequeue(out (uint Hash, Mesh? Mesh) done))
		{
			_loading.Remove(done.Hash);
			_meshes[done.Hash] = done.Mesh;
		}

		uint words = Read();
		uint holder = words >= HeaderWords ? _words[0] : 0u;
		if (holder == 0u)
		{
			Present(false);
			return;
		}

		if (holder != _holder)
		{
			Clear();
			_holder = holder;
			_serial = uint.MaxValue;
		}
		Present(true);

		if (_words[1] != 0u)
		{
			PlaceCamera();
		}

		++_frames;
		int rows = Math.Min(Math.Min((int)_words[13], MaxRows), (int)((words - HeaderWords) / RowWords));
		if (_words[12] != _serial)
		{
			_serial = _words[12];
			GD.Print($"[map-table] holder {holder:X8}: {rows} meshes, camera {(_words[1] != 0u ? "stated" : "not stated")}");
		}
		for (int i = 0; i < rows; i++)
		{
			Place(HeaderWords + i * RowWords);
		}

		_gone.Clear();
		foreach ((uint id, Piece piece) in _pieces)
		{
			if (piece.Seen != _frames)
			{
				piece.Node.QueueFree();
				_gone.Add(id);
			}
		}
		foreach (uint id in _gone)
		{
			_pieces.Remove(id);
		}
	}

	private uint Read()
	{
		if (_bridgeMissing || !HostBridge.IsGuestUp)
		{
			return 0u;
		}
		try
		{
			return RefiiHostGetFable3MapTable(_words, (uint)_words.Length);
		}
		catch (EntryPointNotFoundException)
		{
			_bridgeMissing = true;
			return 0u;
		}
	}

	private void Present(bool on)
	{
		if (Visible == on)
		{
			return;
		}
		Visible = on;
		Presented = on;
		Fable3WorldMapMode.ScenePresented(on);
		_viewport.RenderTargetUpdateMode = on ? SubViewport.UpdateMode.Always : SubViewport.UpdateMode.Disabled;
		GD.Print(on ? $"[map-table] holder {_holder:X8} presented" : $"[map-table] holder {_holder:X8} no longer presented");
	}

	private void Clear()
	{
		foreach (Piece piece in _pieces.Values)
		{
			piece.Node.QueueFree();
		}
		_pieces.Clear();
	}

	private float F(int at) => BitConverter.UInt32BitsToSingle(_words[at]);

	/// <summary>The D3D rows of a unit quaternion (x, y, z, w): row r is the image of basis axis r.</summary>
	private static (Vector3 R0, Vector3 R1, Vector3 R2) Rows(float x, float y, float z, float w)
	{
		float n = x * x + y * y + z * z + w * w;
		if (!float.IsFinite(n) || n < 1e-6f)
		{
			return (new Vector3(1f, 0f, 0f), new Vector3(0f, 1f, 0f), new Vector3(0f, 0f, 1f));
		}
		float s = 2f / n;
		return (
			new Vector3(1f - s * (y * y + z * z), s * (x * y + w * z), s * (x * z - w * y)),
			new Vector3(s * (x * y - w * z), 1f - s * (x * x + z * z), s * (y * z + w * x)),
			new Vector3(s * (x * z + w * y), s * (y * z - w * x), 1f - s * (x * x + y * y)));
	}

	/// <summary>The holder's camera: forward is its quaternion's row 1, up its row 2 (sub_82274210's
	/// rows are forward x up, forward, up); the guest states the vertical fov, Godot's default axis.</summary>
	private void PlaceCamera()
	{
		var position = new Vector3(F(2), F(3), F(4));
		(Vector3 _, Vector3 forward, Vector3 up) = Rows(F(5), F(6), F(7), F(8));
		Vector3 eye = ToGodot(position);
		Vector3 back = -ToGodot(forward).Normalized();
		Vector3 y = ToGodot(up);
		y = (y - back * y.Dot(back)).Normalized();
		if (!eye.IsFinite() || !back.IsFinite() || !y.IsFinite() || y.LengthSquared() < 0.5f)
		{
			return;
		}
		_camera.GlobalTransform = new Transform3D(new Basis(y.Cross(back), y, back), eye);

		float fov = Mathf.RadToDeg(F(9));
		if (fov > 1f && fov < 179f)
		{
			_camera.Fov = fov;
		}
		float near = F(10), far = F(11);
		if (near > 0f && far > near)
		{
			_camera.Near = near;
			_camera.Far = far;
		}
	}

	private void Place(int at)
	{
		uint id = _words[at];
		uint model = _words[at + 1];
		if (MeshOf(model) is not { } mesh)
		{
			if (_pieces.TryGetValue(id, out Piece? waiting))
			{
				waiting.Seen = _frames;
			}
			return;
		}

		if (!_pieces.TryGetValue(id, out Piece? piece))
		{
			var node = new MeshInstance3D { Name = $"Piece_{id:X}" };
			_root.AddChild(node);
			piece = new Piece { Node = node };
			_pieces[id] = piece;
		}
		piece.Seen = _frames;
		if (piece.Model != model || piece.Node.Mesh != mesh)
		{
			piece.Model = model;
			piece.Node.Mesh = mesh;
		}

		// World = scale * (file x R0 + file y R1 + file z R2) + position; a Godot model point q is
		// the file's (q.x, -q.z, q.y) and a guest world point goes to Godot as (-x, z, y).
		(Vector3 r0, Vector3 r1, Vector3 r2) = Rows(F(at + 5), F(at + 6), F(at + 7), F(at + 8));
		var position = new Vector3(F(at + 9), F(at + 10), F(at + 11));
		float scale = F(at + 12);
		if (!float.IsFinite(scale) || scale <= 0f)
		{
			scale = 1f;
		}
		Vector3 Guest(Vector3 file) => (r0 * file.X + r1 * file.Y + r2 * file.Z) * scale + position;
		Vector3 origin = ToGodot(position);
		var basis = new Basis(
			ToGodot(Guest(new Vector3(1f, 0f, 0f))) - origin,
			ToGodot(Guest(new Vector3(0f, 0f, 1f))) - origin,
			ToGodot(Guest(new Vector3(0f, -1f, 0f))) - origin);
		if (!origin.IsFinite())
		{
			piece.Node.Visible = false;
			return;
		}
		piece.Node.Transform = new Transform3D(basis, origin);

		float alpha = Math.Clamp(F(at + 17), 0f, 1f);
		piece.Node.Visible = alpha > 0f;
		piece.Node.Transparency = alpha < 0.99609375f ? 1f - alpha : 0f;
		piece.Node.SetInstanceShaderParameter(Engine_ClassicMaterial.Active ? ClassicTint : ModernTint,
			new Vector4(F(at + 13), F(at + 14), F(at + 15), F(at + 16)));
	}

	/// <summary>The model a resource id names, through the props index and the shared loader, built
	/// off the main thread once; null until it stands.</summary>
	private Mesh? MeshOf(uint hash)
	{
		if (_meshes.TryGetValue(hash, out Mesh? mesh))
		{
			return mesh;
		}
		if (_loading.Contains(hash))
		{
			return null;
		}
		if (Refii.Level.Engine_PropModelIndex.Resolve(hash, Array.Empty<string>()) is not string path)
		{
			_meshes[hash] = null;
			if (_reported.Add(hash))
			{
				GD.Print($"[map-table] model {hash:X8}: the props index names no model for it");
			}
			return null;
		}
		_loading.Add(hash);
		System.Threading.Tasks.Task.Run(() =>
		{
			Mesh? loaded = null;
			try
			{
				loaded = ResourceLoader.Load<Mesh>(Engine_BnkMeshLoader.PathPrefix + path, "Mesh");
			}
			catch (Exception)
			{
				loaded = null;
			}
			_loaded.Enqueue((hash, loaded is not null && loaded.GetSurfaceCount() > 0 ? loaded : null));
		});
		return null;
	}

	private static Vector3 ToGodot(Vector3 guest) => new(-guest.X, guest.Z, guest.Y);

	[DllImport(HostBridge.Library)]
	private static extern uint RefiiHostGetFable3MapTable([Out] uint[] words, uint capacity);
}
