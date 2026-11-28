using System;
using Godot;
using Godot.Collections;
using Refii.Core;

namespace Refii.Lighting;

/// <summary>
/// Forward+ PreOpaque dual-scale horizon GTAO compositor pass (<c>fable_ssao_tex</c>).
/// </summary>
/// <remarks>
/// Replaces Godot's disconnected <c>Environment.SsaoEnabled</c> on Forward+ so screen-space contact
/// and architectural 90-degree crease AO reach every Refii shading path uniformly:
/// <list type="bullet">
/// <item><description>Custom <c>void light()</c> directional sun, local placed torches/lamps, and specular AO.</description></item>
/// <item><description><c>EMISSION</c>-carried <c>fable_ambient_colour + FABLE_FILL_FLOOR</c> and Modern <c>amb3</c> interior lightmaps.</description></item>
/// <item><description>Alpha-scissored <c>depth_prepass_alpha</c> surfaces (iron fences, foliage, grass, two-sided trims) that write hardware depth in the prepass.</description></item>
/// <item><description>Combined AO Debug View (<c>lighting_debug_view = 11</c>).</description></item>
/// </list>
/// </remarks>
[Tool]
[GlobalClass]
public partial class Engine_ScreenSpaceAoEffect : CompositorEffect
{
	public const string TraceShaderPath = "res://src/Engine/Lighting/fable_ssao_trace.glsl";
	public const string BlurShaderPath = "res://src/Engine/Lighting/fable_ssao_blur.glsl";
	public const string GlobalTextureName = "fable_ssao_tex";
	public const string GlobalTextureSecondName = "fable_ssao_tex_second";
	public const string GlobalValidName = "fable_ssao_valid";

	private const int TraceParamsBytes = 2 * 16 * 4 + 4 * 16; // 2 mat4 + 4 vec4 = 192 bytes
	private const int BlurParamsBytes = 1 * 16 * 4 + 2 * 16;  // 1 mat4 + 2 vec4 = 96 bytes

	private static bool _globalsRegistered;
	private static readonly Texture2Drd?[] _textures = new Texture2Drd?[2];
	private static ImageTexture? _fallbackWhiteTexture;

	// D3D12 / Intel Arc DXIL safety: always bind a valid 1x1 white ImageTexture fallback to
	// fable_ssao_tex / fable_ssao_tex_second before the first compute pass publishes an RD RID.
	private static ImageTexture GetOrCreateFallbackWhiteTexture()
	{
		if (_fallbackWhiteTexture is null || !GodotObject.IsInstanceValid(_fallbackWhiteTexture))
		{
			Image image = Image.CreateEmpty(1, 1, false, Image.Format.Rgba8);
			image.Fill(Colors.White);
			_fallbackWhiteTexture = ImageTexture.CreateFromImage(image);
		}

		return _fallbackWhiteTexture;
	}

	/// <summary>View index: 0 for primary / single-screen camera, 1 for player two on split screen.</summary>
	public int View { get; set; }

	private static string TextureGlobal(int view) => view == 0 ? GlobalTextureName : GlobalTextureSecondName;

	/// <summary>
	/// The pass whose AO each view's global holds now. Two cameras run a pass for view 0 — the window's
	/// and player one's split view (Refii.Game.GameSplitView) — and only the one drawing runs, so the
	/// one that runs claims the global and only the holder clears it.
	/// </summary>
	private static readonly Engine_ScreenSpaceAoEffect?[] _publishers = new Engine_ScreenSpaceAoEffect?[2];

	/// <summary>Built for a split camera (GameSplitView); the window camera's effect claims no slot while split.</summary>
	public bool SplitCamera { get; set; }

	/// <summary>Whether this effect holds its view's slot (<c>fable_ssao_tex</c> / <c>_second</c>) now: the debug channel's per-view owner read.</summary>
	public bool HoldsViewSlot => View is >= 0 and < 2 && ReferenceEquals(System.Threading.Volatile.Read(ref _publishers[View]), this);

	private RenderingDevice? _rd;
	private Rid _traceShader;
	private Rid _tracePipeline;
	private Rid _blurShader;
	private Rid _blurPipeline;
	private Rid _nearestSampler;
	private Rid _traceParamsBuffer;
	private Rid _blurParamsBuffer;
	private Rid _rawTraceTex;
	private Rid _outAoTex;
	private Vector2I _traceSize;
	private Vector2I _outSize;
	private bool _ready;
	private bool _publishedValid;
	private bool _released;

	private readonly Engine_UniformSetKey _traceSet = new(4);
	private readonly Engine_UniformSetKey _blurSet = new(5);
	private readonly float[] _traceFloats = new float[TraceParamsBytes / 4];
	private readonly byte[] _traceBytes = new byte[TraceParamsBytes];
	private readonly float[] _blurFloats = new float[BlurParamsBytes / 4];
	private readonly byte[] _blurBytes = new byte[BlurParamsBytes];

	/// <summary>
	/// Resolves trace and output resolution and ray counts from <paramref name="terrainAoResolution"/>.
	/// <paramref name="fullSize"/> is already Godot's internal 3D render size (including FSR / 3D scale),
	/// so all presets run 1:1 at <paramref name="fullSize"/> and scale quality via horizon sample density:
	/// <list type="bullet">
	/// <item><description><c>&lt;= 2048</c> (Low / Performance): 1:1 internal 3D resolution, 2 directions x 3 steps (6 horizon pairs).</description></item>
	/// <item><description><c>4096</c> (Balanced): 1:1 internal 3D resolution, 3 directions x 4 steps (12 horizon pairs).</description></item>
	/// <item><description><c>&gt;= 8192</c> (High / Quality): 1:1 internal 3D resolution, 4 directions x 5 steps (20 horizon pairs).</description></item>
	/// </list>
	/// </summary>
	public static (Vector2I TraceSize, Vector2I OutSize, int Directions, int Steps) ResolveQualityTier(
		Vector2I fullSize,
		int terrainAoResolution)
	{
		Vector2I full = new(Math.Max(1, fullSize.X), Math.Max(1, fullSize.Y));

		if (terrainAoResolution <= 2048)
		{
			return (full, full, 2, 3);
		}

		if (terrainAoResolution <= 4096)
		{
			return (full, full, 3, 4);
		}

		return (full, full, 4, 5);
	}

	public static void RegisterGlobals()
	{
		if (Engine_AppRoot.IsTestHost || _globalsRegistered)
		{
			return;
		}

		_globalsRegistered = true;
		ImageTexture fallback = GetOrCreateFallbackWhiteTexture();
		for (int view = 0; view < _textures.Length; ++view)
		{
			RenderingServer.GlobalShaderParameterSet(TextureGlobal(view), fallback);
		}

		RenderingServer.GlobalShaderParameterSet(GlobalValidName, 0.0f);
	}

	public Engine_ScreenSpaceAoEffect()
	{
		EffectCallbackType = EffectCallbackTypeEnum.PreOpaque;
		RegisterGlobals();
		RenderingServer.CallOnRenderThread(Callable.From(Initialise));
	}

	private void Initialise()
	{
		if (_released || Engine_RdHealth.IsDeviceLost)
		{
			return;
		}

		_rd = RenderingServer.GetRenderingDevice();
		if (_rd is null)
		{
			return;
		}

		if (!TryBuildShader(TraceShaderPath, out _traceShader, out _tracePipeline) ||
		    !TryBuildShader(BlurShaderPath, out _blurShader, out _blurPipeline))
		{
			return;
		}

		_nearestSampler = _rd.Sampler(new RDSamplerState
		{
			MinFilter = RenderingDevice.SamplerFilter.Nearest,
			MagFilter = RenderingDevice.SamplerFilter.Nearest,
			MipFilter = RenderingDevice.SamplerFilter.Nearest,
			RepeatU = RenderingDevice.SamplerRepeatMode.ClampToEdge,
			RepeatV = RenderingDevice.SamplerRepeatMode.ClampToEdge,
		});
		_traceParamsBuffer = _rd.UniformBufferCreate(TraceParamsBytes, new byte[TraceParamsBytes]);
		_blurParamsBuffer = _rd.UniformBufferCreate(BlurParamsBytes, new byte[BlurParamsBytes]);

		_ready = _tracePipeline.IsValid && _blurPipeline.IsValid &&
			_nearestSampler.IsValid && _traceParamsBuffer.IsValid && _blurParamsBuffer.IsValid;
	}

	private bool TryBuildShader(string path, out Rid shader, out Rid pipeline)
	{
		shader = default;
		pipeline = default;
		if (_rd is null)
		{
			return false;
		}

		RDShaderSpirV? spirv = null;
		if (ResourceLoader.Exists(path) && GD.Load<RDShaderFile>(path) is { } imported)
		{
			spirv = imported.GetSpirV();
		}

		if (spirv is null || spirv.CompileErrorCompute.Length != 0)
		{
			string raw = FileAccess.GetFileAsString(path);
			if (string.IsNullOrEmpty(raw))
			{
				GD.PrintErr($"[ssao] could not read compute shader '{path}'");
				return false;
			}

			// Strip Godot's #[compute] import header when compiling raw GLSL 450 directly via RenderingDevice.
			int newline = raw.IndexOf('\n');
			if (raw.StartsWith("#[compute]", StringComparison.Ordinal) && newline >= 0)
			{
				raw = raw[(newline + 1)..];
			}

			spirv = _rd.ShaderCompileSpirVFromSource(new RDShaderSource
			{
				Language = RenderingDevice.ShaderLanguage.Glsl,
				SourceCompute = raw,
			});
		}

		if (spirv.CompileErrorCompute.Length != 0)
		{
			GD.PrintErr($"[ssao] {path} failed to compile: {spirv.CompileErrorCompute}");
			return false;
		}

		shader = _rd.Shader(spirv);
		if (!shader.IsValid)
		{
			GD.PrintErr($"[ssao] {path}: shader creation failed");
			return false;
		}

		pipeline = _rd.ComputePipeline(shader);
		if (!pipeline.IsValid)
		{
			GD.PrintErr($"[ssao] {path}: compute pipeline creation failed");
			return false;
		}

		return true;
	}

	private void EnsureTargets(Vector2I traceSize, Vector2I outSize)
	{
		if (Engine_RdHealth.IsDeviceLost || _rd is null)
		{
			return;
		}

		if (!_rawTraceTex.IsValid || traceSize != _traceSize)
		{
			Engine_RdRetire.Retire(_rawTraceTex);
			_rawTraceTex = _rd.Texture(new RDTextureFormat
			{
				Width = (uint)traceSize.X,
				Height = (uint)traceSize.Y,
				Format = RenderingDevice.DataFormat.R8Unorm,
				TextureType = RenderingDevice.TextureType.Type2D,
				UsageBits = RenderingDevice.TextureUsageBits.SamplingBit |
					RenderingDevice.TextureUsageBits.StorageBit |
					RenderingDevice.TextureUsageBits.CanUpdateBit,
			}, new RDTextureView());
			if (!Engine_RdHealth.CheckRdValid(_rawTraceTex, "Engine_ScreenSpaceAoEffect.RawTrace"))
			{
				_rawTraceTex = default;
				_traceSize = default;
				return;
			}
			_traceSize = traceSize;
		}

		if (!_outAoTex.IsValid || outSize != _outSize)
		{
			Engine_RdRetire.Retire(_outAoTex);
			_outAoTex = _rd.Texture(new RDTextureFormat
			{
				Width = (uint)outSize.X,
				Height = (uint)outSize.Y,
				Format = RenderingDevice.DataFormat.R8Unorm,
				TextureType = RenderingDevice.TextureType.Type2D,
				UsageBits = RenderingDevice.TextureUsageBits.SamplingBit |
					RenderingDevice.TextureUsageBits.StorageBit |
					RenderingDevice.TextureUsageBits.CanUpdateBit,
			}, new RDTextureView());
			if (!Engine_RdHealth.CheckRdValid(_outAoTex, "Engine_ScreenSpaceAoEffect.OutAo"))
			{
				_outAoTex = default;
				_outSize = default;
				return;
			}
			_outSize = outSize;
			PublishOutTexture(_outAoTex);
		}
		else if (!_publishedValid ||
			(View is >= 0 and < 2 && !ReferenceEquals(System.Threading.Volatile.Read(ref _publishers[View]), this)))
		{
			PublishOutTexture(_outAoTex);
		}
	}

	private void PublishOutTexture(Rid rid)
	{
		if (!rid.IsValid)
		{
			return;
		}

		_publishedValid = true;
		if (System.Threading.Interlocked.Exchange(ref _publishQueued, 1) == 1)
		{
			return;
		}

		int view = View;
		Callable.From(() =>
		{
			System.Threading.Volatile.Write(ref _publishQueued, 0);
			Rid current = _outAoTex;
			if (_released || !_publishedValid || !current.IsValid || view is < 0 or >= 2 ||
				!Refii.Core.Engine_Views.MayClaim(SplitCamera))
			{
				return;
			}

			Texture2Drd texture = _textures[view] ??= new Texture2Drd();
			texture.TextureRdRid = current;
			RenderingServer.GlobalShaderParameterSet(TextureGlobal(view), texture);
			if (view == 0)
			{
				RenderingServer.GlobalShaderParameterSet(GlobalValidName, 1.0f);
			}

			System.Threading.Volatile.Write(ref _publishers[view], this);
		}).CallDeferred();
	}

	/// <summary>1 while a publish is queued for the main thread.</summary>
	private int _publishQueued;

	private void SetInactive()
	{
		if (!_publishedValid)
		{
			return;
		}

		_publishedValid = false;
		if (View == 0)
		{
			Callable.From(() =>
			{
				if (!_publishedValid && ReferenceEquals(_publishers[0], this))
				{
					RenderingServer.GlobalShaderParameterSet(GlobalValidName, 0.0f);
				}
			}).CallDeferred();
		}
	}

	public override void _RenderCallback(int effectCallbackType, RenderData renderData)
	{
		if (_rd is not null)
		{
			Engine_RdRetire.Pump(_rd);
		}

		if (Engine_RdHealth.IsDeviceLost || !_ready || _rd is null)
		{
			return;
		}

		if (!Engine_LevelLighting.ShouldRunCustomScreenSpaceAo())
		{
			SetInactive();
			return;
		}

		if (renderData.GetRenderSceneBuffers() is not RenderSceneBuffersRD buffers ||
		    renderData.GetRenderSceneData() is not RenderSceneDataRD scene)
		{
			return;
		}

		Vector2I fullSize = buffers.GetInternalSize();
		if (fullSize.X <= 0 || fullSize.Y <= 0)
		{
			return;
		}

		Rid depth = buffers.GetDepthLayer(0);
		if (!depth.IsValid)
		{
			return;
		}

		(Vector2I traceSize, Vector2I outSize, int numDirs, int numSteps) =
			ResolveQualityTier(fullSize, Engine_LevelAmbientOcclusion.TerrainAoResolution);

		EnsureTargets(traceSize, outSize);
		if (Engine_RdHealth.IsDeviceLost || !_rawTraceTex.IsValid || !_outAoTex.IsValid)
		{
			return;
		}

		Projection proj = scene.GetCamProjection();
		Projection invProj = proj.Inverse();

		int tAt = 0;
		WriteProjection(_traceFloats, ref tAt, in invProj);
		WriteProjection(_traceFloats, ref tAt, in proj);
		WriteVec4(_traceFloats, ref tAt, traceSize.X, traceSize.Y, fullSize.X, fullSize.Y);
		WriteVec4(_traceFloats, ref tAt, 1.0f / traceSize.X, 1.0f / traceSize.Y, 1.0f / fullSize.X, 1.0f / fullSize.Y);
		WriteVec4(_traceFloats, ref tAt, 0.24f, 0.95f, 55.0f, 85.0f);
		WriteVec4(_traceFloats, ref tAt, numDirs, numSteps, 0.035f, 1.15f);
		Buffer.BlockCopy(_traceFloats, 0, _traceBytes, 0, TraceParamsBytes);
		_rd.BufferUpdate(_traceParamsBuffer, 0, TraceParamsBytes, _traceBytes);

		int bAt = 0;
		WriteProjection(_blurFloats, ref bAt, in invProj);
		WriteVec4(_blurFloats, ref bAt, traceSize.X, traceSize.Y, outSize.X, outSize.Y);
		WriteVec4(_blurFloats, ref bAt, 1.0f / traceSize.X, 1.0f / traceSize.Y, 1.0f / outSize.X, 1.0f / outSize.Y);
		Buffer.BlockCopy(_blurFloats, 0, _blurBytes, 0, BlurParamsBytes);
		_rd.BufferUpdate(_blurParamsBuffer, 0, BlurParamsBytes, _blurBytes);

		Rid traceSet = _traceSet.For(_rd, stackalloc Rid[] { _nearestSampler, depth, _rawTraceTex, _traceParamsBuffer });
		if (!traceSet.IsValid)
		{
			var uniforms = new Array<RDUniform>();
			var uDepth = new RDUniform { UniformType = RenderingDevice.UniformType.SamplerWithTexture, Binding = 0 };
			uDepth.AddId(_nearestSampler);
			uDepth.AddId(depth);
			uniforms.Add(uDepth);

			var uOut = new RDUniform { UniformType = RenderingDevice.UniformType.Image, Binding = 1 };
			uOut.AddId(_rawTraceTex);
			uniforms.Add(uOut);

			var uParams = new RDUniform { UniformType = RenderingDevice.UniformType.UniformBuffer, Binding = 2 };
			uParams.AddId(_traceParamsBuffer);
			uniforms.Add(uParams);

			traceSet = _traceSet.Hold(UniformSetCacheRD.GetCache(_traceShader, 0, uniforms));
			GC.KeepAlive(uniforms);
		}

		Rid blurSet = _blurSet.For(_rd, stackalloc Rid[] { _nearestSampler, _rawTraceTex, depth, _outAoTex, _blurParamsBuffer });
		if (!blurSet.IsValid)
		{
			var uniforms = new Array<RDUniform>();
			var uRaw = new RDUniform { UniformType = RenderingDevice.UniformType.SamplerWithTexture, Binding = 0 };
			uRaw.AddId(_nearestSampler);
			uRaw.AddId(_rawTraceTex);
			uniforms.Add(uRaw);

			var uDepth = new RDUniform { UniformType = RenderingDevice.UniformType.SamplerWithTexture, Binding = 1 };
			uDepth.AddId(_nearestSampler);
			uDepth.AddId(depth);
			uniforms.Add(uDepth);

			var uOut = new RDUniform { UniformType = RenderingDevice.UniformType.Image, Binding = 2 };
			uOut.AddId(_outAoTex);
			uniforms.Add(uOut);

			var uParams = new RDUniform { UniformType = RenderingDevice.UniformType.UniformBuffer, Binding = 3 };
			uParams.AddId(_blurParamsBuffer);
			uniforms.Add(uParams);

			blurSet = _blurSet.Hold(UniformSetCacheRD.GetCache(_blurShader, 0, uniforms));
			GC.KeepAlive(uniforms);
		}

		if (!Engine_RdHealth.CheckRdValid(traceSet, "Engine_ScreenSpaceAoEffect.TraceSet") ||
		    !Engine_RdHealth.CheckRdValid(blurSet, "Engine_ScreenSpaceAoEffect.BlurSet"))
		{
			return;
		}

		long traceList = _rd.ComputeListBegin();
		_rd.ComputeListBindComputePipeline(traceList, _tracePipeline);
		_rd.ComputeListBindUniformSet(traceList, traceSet, 0);
		_rd.ComputeListDispatch(traceList, (uint)((traceSize.X + 7) / 8), (uint)((traceSize.Y + 7) / 8), 1);
		_rd.ComputeListEnd();

		long blurList = _rd.ComputeListBegin();
		_rd.ComputeListBindComputePipeline(blurList, _blurPipeline);
		_rd.ComputeListBindUniformSet(blurList, blurSet, 0);
		_rd.ComputeListDispatch(blurList, (uint)((outSize.X + 7) / 8), (uint)((outSize.Y + 7) / 8), 1);
		_rd.ComputeListEnd();
	}

	private static void WriteProjection(float[] dst, ref int at, in Projection p)
	{
		WriteVec4(dst, ref at, p.X.X, p.X.Y, p.X.Z, p.X.W);
		WriteVec4(dst, ref at, p.Y.X, p.Y.Y, p.Y.Z, p.Y.W);
		WriteVec4(dst, ref at, p.Z.X, p.Z.Y, p.Z.Z, p.Z.W);
		WriteVec4(dst, ref at, p.W.X, p.W.Y, p.W.Z, p.W.W);
	}

	private static void WriteVec4(float[] dst, ref int at, float x, float y, float z, float w)
	{
		dst[at++] = x;
		dst[at++] = y;
		dst[at++] = z;
		dst[at++] = w;
	}

	public override void _Notification(int what)
	{
		if (what != NotificationPredelete)
		{
			return;
		}

		ReleaseNow(releaseStaticFallbacks: false);
	}

	public void ReleaseNow(bool releaseStaticFallbacks = false)
	{
		if (_released && !releaseStaticFallbacks)
		{
			return;
		}

		_released = true;
		_publishedValid = false;

		// Only the pass holding the global gives it up (see _publishers): player one's split view,
		// freed when local co-op ends, is not what the window draws through after it.
		bool holds = View is >= 0 and < 2 && ReferenceEquals(_publishers[View], this);
		if (holds)
		{
			_publishers[View] = null;
		}

		if (_globalsRegistered && (holds || releaseStaticFallbacks))
		{
			if (View == 0)
			{
				RenderingServer.GlobalShaderParameterSet(GlobalValidName, 0.0f);
			}

			if (View is >= 0 and < 2)
			{
				RenderingServer.GlobalShaderParameterSet(
					TextureGlobal(View),
					!releaseStaticFallbacks && _fallbackWhiteTexture is not null && GodotObject.IsInstanceValid(_fallbackWhiteTexture)
						? _fallbackWhiteTexture
						: default(Variant));
			}
		}

		if (releaseStaticFallbacks)
		{
			if (View == 0)
			{
				RenderingServer.GlobalShaderParameterSet(GlobalValidName, 0.0f);
				RenderingServer.GlobalShaderParameterSet(GlobalTextureName, default(Variant));
				RenderingServer.GlobalShaderParameterSet(GlobalTextureSecondName, default(Variant));
			}

			if (_fallbackWhiteTexture is not null && GodotObject.IsInstanceValid(_fallbackWhiteTexture))
			{
				_fallbackWhiteTexture.Dispose();
			}

			_fallbackWhiteTexture = null;
			_globalsRegistered = false;
		}

		if ((holds || releaseStaticFallbacks) && View is >= 0 and < 2 && _textures[View] is { } texture)
		{
			texture.TextureRdRid = default;
			_textures[View] = null;
		}

		Engine_ScreenSpaceAoEffect self = this;
		Engine_RdRetire.OnRenderThread(() => self.ReleaseDeviceObjects());
	}

	private void ReleaseDeviceObjects()
	{
		_ready = false;
		RenderingDevice? rd = RenderingServer.GetRenderingDevice();
		if (_rd is null || rd is null)
		{
			_rd = null;
			return;
		}

		if (_outAoTex.IsValid) rd.FreeRid(_outAoTex);
		if (_rawTraceTex.IsValid) rd.FreeRid(_rawTraceTex);
		if (_blurParamsBuffer.IsValid) rd.FreeRid(_blurParamsBuffer);
		if (_traceParamsBuffer.IsValid) rd.FreeRid(_traceParamsBuffer);
		if (_nearestSampler.IsValid) rd.FreeRid(_nearestSampler);
		if (_blurPipeline.IsValid) rd.FreeRid(_blurPipeline);
		if (_blurShader.IsValid) rd.FreeRid(_blurShader);
		if (_tracePipeline.IsValid) rd.FreeRid(_tracePipeline);
		if (_traceShader.IsValid) rd.FreeRid(_traceShader);

		_outAoTex = _rawTraceTex = default;
		_blurParamsBuffer = _traceParamsBuffer = _nearestSampler = default;
		_blurPipeline = _blurShader = _tracePipeline = _traceShader = default;
		_traceSize = _outSize = default;
		_rd = null;
	}
}
