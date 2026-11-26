using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using Godot;

namespace Refii.Textures;

/// <summary>
/// Roughly what an entry cost to produce, so the budget sweep can evict cheaply first.
/// </summary>
/// <remarks>
/// A CLASS, NOT A TIMING. Recording milliseconds would make the value machine specific and would
/// mean an entry built on a fast machine looked cheap on a slow one; what the sweep actually needs
/// is an ordering, and the ordering is the same everywhere. Measured on this project's own data:
/// a decode is tens of milliseconds, a classical resample is a few hundred, a neural upscale is
/// hundreds to thousands.
///
/// The values are written into the container, so they are part of the format and must not be
/// renumbered without bumping its version.
/// </remarks>
public enum TexCacheCost : byte
{
	/// <summary>A straight decode of a shipped <c>.tex</c>. Cheapest to rebuild.</summary>
	Decoded = 0,

	/// <summary>Something we computed from decoded inputs — the packed ORM.</summary>
	Derived = 1,

	/// <summary>An upscale. The expensive one, and the one worth keeping longest.</summary>
	Enhanced = 2,
}

/// <summary>
/// The persistent store for decoded and enhanced textures.
/// </summary>
/// <remarks>
/// UNDER <c>user://</c>, AND IT HAS TO BE. Everything in here is derived from the player's own
/// copy of Fable 2 — an untiled, re-encoded, possibly resampled version of a shipped asset —
/// so it may not be written into the repository or into a build, only generated on the machine
/// that already owns the game. <see cref="Refii.Tools.UiSceneExporter"/> enforces the same rule
/// for the same reason.
///
/// The layout is §5's, with an epoch level and a shard level added:
///
///     &lt;app&gt;/cache/textures/v&lt;converter&gt;/&lt;first two hex&gt;/&lt;hash&gt;.rftx
///
/// Neither is cosmetic. The shard is because a region's worth of textures is thousands of
/// entries and some filesystems degrade badly on a single directory that size; the epoch is what
/// makes a converter bump a directory to delete rather than a few hundred megabytes of files
/// that are unreadable, unreachable and indistinguishable from live ones.
/// </remarks>
public static class Engine_TexCache
{
	/// <summary>
	/// The version of the DECODE pipeline, not of this file.
	/// </summary>
	/// <remarks>
	/// Bump it when <see cref="Engine_TexImage"/>, <see cref="Engine_TexData"/>,
	/// <see cref="Engine_Xbox360Tiling"/> or <see cref="Engine_BcEncoder"/> change what bytes
	/// come out for the same input. It is the "conversion version" of §5's key, and it is the
	/// reason a decode fix does not need the cache directory deleted by hand.
	/// </remarks>
	/// <remarks>
	/// 2 (2026-08-19): block compression moved off Godot's editor-only compressor onto
	/// <see cref="Engine_BcEncoder"/>. Every v1 entry an exported build wrote for an ORM or an
	/// enhanced texture is RGBA8, because Image.Compress had been failing silently — 1,485 of
	/// 3,576 entries holding 2.90 GB of 3.13 GB. Serving those would mean the new encoder never
	/// ran for anything already cached, so the version moves and v1 is simply not read.
	/// 4 (2026-11-19): bare-chain raw mips (<c>compFlag == 7</c>) byte-swap Xbox 360 16-bit
	/// words, slice full BC3 blocks, and de-interleave BC4 plane 0 from BC5 pairs.
	/// </remarks>
	public const int ConverterVersion = 4;

	/// <summary>Where converted textures live for <paramref name="title"/>: <c>&lt;app&gt;/cache/&lt;Title&gt;/textures</c>.</summary>
	public static string CacheRootFor(Refii.Core.Engine_Title title) =>
		System.IO.Path.Combine(title.CacheDirectory, "textures");

	/// <summary>Where converted textures live: <c>&lt;app&gt;/cache/&lt;Title&gt;/textures</c>.</summary>
	/// <remarks>
	/// THE APP'S OWN CACHE CONTAINER, NOT <c>user://</c> and NOT under assets/save. user:// put it
	/// in Godot's per-app data directory, outside the install entirely; assets/save put
	/// regenerable data inside the one folder a player can never regenerate. Anchored on
	/// <see cref="Refii.Core.Engine_Title.CacheDirectory"/> it is <c>&lt;app&gt;/cache/&lt;Title&gt;</c>, so
	/// deleting that one folder clears everything derived and touches no save.
	///
	/// Absolute rather than a Godot virtual path: FileAccess and DirAccess take OS paths, and
	/// GlobalizePath returns an absolute path unchanged, so every existing call site still works.
	/// </remarks>
	public static string CacheRoot =>
		CacheRootFor(Refii.Core.Engine_Title.Active);

	/// <summary>
	/// The epoch directory: everything the current decode pipeline produced.
	/// </summary>
	/// <remarks>
	/// THE CONVERTER VERSION IS IN THE PATH, not just in the key, and that is what makes the
	/// invalidation §25's phase 6 asks for actually possible. Both are needed and they do
	/// different jobs: in the key it stops a stale entry being READ, in the path it makes the
	/// stale entries IDENTIFIABLE. Without the directory, bumping the version leaves every file
	/// already on disk unreadable, unreachable and indistinguishable from a live one — a few
	/// hundred megabytes per bump that nothing would ever clean up, because the only way to know
	/// an entry is stale would be to recompute the key of a source you no longer have.
	///
	/// With it, a bump is one directory to delete.
	/// </remarks>
	private static string Root => $"{CacheRoot.Replace('\\', '/')}/v{ConverterVersion}";

	private const uint Magic = 0x58544652u; // 'RFTX' little endian

	/// <summary>Magic, version, width, height, format, mipmaps, alpha, cost, faces, length, deflated length.</summary>
	private const int HeaderSize = 33;

	/// <summary>The entry's face count, a u16: magic, version, width, height, format, three u8s.</summary>
	private const int FacesOffset = 23;

	/// <summary>The most deflate can expand: 258 bytes from a 2-bit match. Flat textures come close
	/// (1027.5 measured in a real cache).</summary>
	private const long MaxInflateRatio = 1032;

	/// <summary>
	/// Version 2 added the face count, so one entry can hold a cube's six faces.
	/// </summary>
	/// <remarks>
	/// Bumping this does NOT strand anything. <see cref="TryLoad"/> rejects a version it does not
	/// recognise, which is a miss, and the miss re-decodes and rewrites under the SAME key — the
	/// key does not include the container version. So an old entry is overwritten in place rather
	/// than orphaned, and the epoch directory does not need to change with it.
	/// </remarks>
	/// <summary>
	/// 3 added a rebuild-cost class to the header; 4 deflates the payload.
	///
	/// INTERNAL RATHER THAN PRIVATE so <c>src/tools/EvictionGate.cs</c> can build headers this
	/// build will accept. It hardcoded the number instead, and the next bump silently invalidated
	/// every entry it wrote — the sweep then classified them all as Decoded, sorted by age alone,
	/// and the gate failed reporting the exact inversion it exists to catch. It found a real
	/// regression in itself, which is the good outcome, but the constant should not have been
	/// copyable in the first place.
	/// </summary>
	/// <remarks>
	/// A BUMP STRANDS EVERY EXISTING ENTRY, which is worth saying plainly rather than discovering:
	/// the version is checked on load and a mismatch reads as a miss, so the old files sit there
	/// unreadable until the budget sweep reclaims them. That is the cost of the format being
	/// deliberately dumb, and it is paid once.
	///
	/// DEFLATE HALVES THE CACHE, measured over 200 random entries: 99.3 MB became 45.6 MB at level
	/// 6, which takes the projected 11.2 GB whole-game total to about 5.1 GB. §21 asks for a
	/// "GPU-friendly ... or appropriate native GPU-compressed format" and names KTX2/Basis as
	/// something to consider; the payload here is ALREADY native block-compressed bytes, so the
	/// requirement was met and the only thing KTX2 would still have bought is size. This buys it
	/// without taking on a container to parse.
	///
	/// It compresses far better than block-compressed data should, because a good share of the
	/// cache is NOT block compressed — the uncompressed RGBA8 entries the CPU-sampled paths ask
	/// for, and every mip tail with large flat regions.
	/// </remarks>
	internal const uint ContainerVersion = 4u;

	private static long _hits;

	private static long _misses;

	private static long _stores;

	private static long _bytesRead;

	/// <summary>Entries served from disk since the process started.</summary>
	public static long Hits => Interlocked.Read(ref _hits);

	/// <summary>Lookups that had to decode.</summary>
	public static long Misses => Interlocked.Read(ref _misses);

	/// <summary>Entries written to disk since the process started.</summary>
	public static long Stores => Interlocked.Read(ref _stores);

	/// <summary>
	/// The key for a plain DECODED texture, with no enhancement applied.
	/// </summary>
	/// <remarks>
	/// SOURCE AND CONVERTER VERSION ONLY, and the omissions are the point. Untiling and
	/// re-encoding a <c>.tex</c> produces the same bytes whatever slot bound it, so folding the
	/// semantic in here stored one identical decode once per slot — a texture named by both a
	/// prop's albedo and a menu frame was two entries of the same bytes. Folding the POLICY
	/// version in was worse: a per-semantic tuning change has nothing to do with decoding, and it
	/// discarded every decoded entry on disk to prove it.
	///
	/// <see cref="Key"/> keeps both, because an enhanced result genuinely depends on both.
	/// </remarks>
	public static string BaseKey(ReadOnlySpan<byte> source)
	{
		using Refii.Core.Engine_Hash.Incremental sha = Refii.Core.Engine_Hash.CreateIncrementalSha256();

		Span<byte> tail = stackalloc byte[4];
		BitConverter.TryWriteBytes(tail, ConverterVersion);

		sha.AppendData(source);
		sha.AppendData(tail);

		return Convert.ToHexString(sha.GetHashAndReset()).ToLowerInvariant();
	}

	/// <summary>
	/// The cache key for one ENHANCED result.
	/// </summary>
	/// <remarks>
	/// §5's inputs, all of them, and the SOURCE BYTES rather than the source path. Keying on
	/// the path would be cheaper — it would let a hit skip the bank read entirely — but it
	/// would also mean a modded or patched asset silently answering with the cached form of
	/// the file it replaced, with no way to tell. Hashing what we actually decoded is the only
	/// version of this that cannot go stale.
	///
	/// The cost is bounded and small: the bank read happens either way, and SHA-256 over a few
	/// megabytes is far below the untile-and-re-encode it stands in front of.
	/// </remarks>
	public static string Key(
		ReadOnlySpan<byte> source, TexSemantic semantic, string backendId, int scale)
	{
		// Incremental rather than one concatenated buffer: the source is the large part, and
		// copying it to prepend sixteen bytes doubles the peak allocation of every decode for
		// nothing.
		using Refii.Core.Engine_Hash.Incremental sha = Refii.Core.Engine_Hash.CreateIncrementalSha256();

		Span<byte> tail = stackalloc byte[16];
		BitConverter.TryWriteBytes(tail[..4], (int)semantic);
		BitConverter.TryWriteBytes(tail.Slice(4, 4), Engine_TexSemantic.PolicyVersion);
		BitConverter.TryWriteBytes(tail.Slice(8, 4), scale);
		BitConverter.TryWriteBytes(tail.Slice(12, 4), ConverterVersion);

		sha.AppendData(source);
		sha.AppendData(tail);
		sha.AppendData(Encoding.UTF8.GetBytes(backendId));

		return Convert.ToHexString(sha.GetHashAndReset()).ToLowerInvariant();
	}

	/// <summary>
	/// The key for something COMPUTED from input bytes — a terrain slice's compressed mip chain,
	/// its cavity. The recipe names the computation and every parameter that changes its output;
	/// the sources are the bytes it read. Same rule as <see cref="Key"/>: hash what was read, never
	/// where it came from.
	/// </summary>
	public static string DerivedKey(string recipe, ReadOnlySpan<byte> source, ReadOnlySpan<byte> second = default)
	{
		using Refii.Core.Engine_Hash.Incremental sha = Refii.Core.Engine_Hash.CreateIncrementalSha256();

		Span<byte> tail = stackalloc byte[8];
		BitConverter.TryWriteBytes(tail[..4], ConverterVersion);
		BitConverter.TryWriteBytes(tail.Slice(4, 4), second.Length);

		sha.AppendData(source);
		sha.AppendData(second);
		sha.AppendData(tail);
		sha.AppendData(Encoding.UTF8.GetBytes(recipe));

		return Convert.ToHexString(sha.GetHashAndReset()).ToLowerInvariant();
	}

	/// <summary>Reads a cached image, or null when the key is not present or unreadable.</summary>
	public static Image? TryLoad(string key, out bool hasAlpha) =>
		TryLoadFaces(key, out Image[] faces, out hasAlpha) && faces.Length == 1 ? faces[0] : null;

	/// <summary>
	/// Fast zero-allocation header check for prebuild sweeps: verifies that a valid cache container
	/// exists on disk and reads its dimensions and alpha flag without inflating the multi-megabyte
	/// payload or allocating a native <see cref="Image"/>.
	/// </summary>
	public static bool TryReadHeader(string key, out int width, out int height, out bool hasAlpha)
	{
		width = 0;
		height = 0;
		hasAlpha = false;

		if (!Engine_TexSettings.CacheEnabled)
		{
			return false;
		}

		try
		{
			string path = ProjectSettings.GlobalizePath(PathFor(key));
			if (!System.IO.File.Exists(path))
			{
				return false;
			}

			Span<byte> header = stackalloc byte[33];
			using Microsoft.Win32.SafeHandles.SafeFileHandle handle = System.IO.File.OpenHandle(
				path, System.IO.FileMode.Open, System.IO.FileAccess.Read, System.IO.FileShare.ReadWrite);
			if (System.IO.RandomAccess.Read(handle, header, 0) < 33 ||
				BinaryPrimitives.ReadUInt32LittleEndian(header) != Magic ||
				BinaryPrimitives.ReadUInt32LittleEndian(header[4..]) != ContainerVersion)
			{
				return false;
			}

			width = (int)BinaryPrimitives.ReadUInt32LittleEndian(header[8..]);
			height = (int)BinaryPrimitives.ReadUInt32LittleEndian(header[12..]);
			hasAlpha = header[21] != 0;
			ushort faceCount = BinaryPrimitives.ReadUInt16LittleEndian(header[FacesOffset..]);
			uint length = BinaryPrimitives.ReadUInt32LittleEndian(header[25..]);
			uint deflatedLength = BinaryPrimitives.ReadUInt32LittleEndian(header[29..]);
			if (width <= 0 || height <= 0 || faceCount != 1 || length == 0 || deflatedLength == 0)
			{
				return false;
			}

			Interlocked.Increment(ref _hits);
			return true;
		}
		catch (Exception)
		{
			return false;
		}
	}

	/// <summary>
	/// The size of a cached 2D entry, from its header alone; false when it has none. For a sweep
	/// asking what is already built, which has no use for the pixels.
	/// </summary>
	public static bool TryPeek(string key, out int width, out int height) =>
		TryReadHeader(key, out width, out height, out _);

	/// <summary>
	/// Reads every face of a cached entry: one image for a 2D texture, six for a cube.
	/// </summary>
	public static bool TryLoadFaces(string key, out Image[] faces, out bool hasAlpha)
	{
		faces = Array.Empty<Image>();
		hasAlpha = false;
		try
		{
			return ReadFaces(key, ref faces, ref hasAlpha);
		}
		catch (Exception e) when (e is System.IO.IOException or UnauthorizedAccessException)
		{
			Interlocked.Increment(ref _misses);
			return false;
		}
	}

	private static bool ReadFaces(string key, ref Image[] faces, ref bool hasAlpha)
	{

		if (!Engine_TexSettings.CacheEnabled)
		{
			return false;
		}

		// READ DIRECTLY, INTO THE POOL. FileAccess.GetBuffer makes a native array and then a managed
		// copy of it, a fresh large-object array per texture on every load.
		string path = ProjectSettings.GlobalizePath(PathFor(key));
		if (!System.IO.File.Exists(path))
		{
			Interlocked.Increment(ref _misses);
			return false;
		}

		Span<byte> header = stackalloc byte[HeaderSize];
		Microsoft.Win32.SafeHandles.SafeFileHandle file;
		try
		{
			file = System.IO.File.OpenHandle(path);
		}
		catch (Exception e) when (e is System.IO.IOException or UnauthorizedAccessException)
		{
			Interlocked.Increment(ref _misses);
			return false;
		}

		using (file)
		{
			if (System.IO.RandomAccess.Read(file, header, 0) != HeaderSize ||
				BinaryPrimitives.ReadUInt32LittleEndian(header) != Magic ||
				BinaryPrimitives.ReadUInt32LittleEndian(header[4..]) != ContainerVersion)
			{
				// A container from an older layout. Not an error worth reporting — the key already
				// covers the CONTENT, so this only happens when the container itself changed shape,
				// and the entry is simply rewritten below.
				Interlocked.Increment(ref _misses);
				return false;
			}

			int width = (int)BinaryPrimitives.ReadUInt32LittleEndian(header[8..]);
			int height = (int)BinaryPrimitives.ReadUInt32LittleEndian(header[12..]);
			var format = (Image.Format)BinaryPrimitives.ReadUInt32LittleEndian(header[16..]);
			bool mipmaps = header[20] != 0;
			hasAlpha = header[21] != 0;

			// header[22] is the cost class: only the sweep consults it, and it reads the header itself
			// rather than going through this path.
			int faceCount = BinaryPrimitives.ReadUInt16LittleEndian(header[23..]);
			uint length = BinaryPrimitives.ReadUInt32LittleEndian(header[25..]);
			uint deflatedLength = BinaryPrimitives.ReadUInt32LittleEndian(header[29..]);

			if (width <= 0 || height <= 0 || length == 0 || length > int.MaxValue ||
				faceCount < 1 || faceCount > Engine_TexCube.FaceCount ||
				deflatedLength == 0 || deflatedLength > int.MaxValue)
			{
				Interlocked.Increment(ref _misses);
				return false;
			}

			// The file has to back the lengths before they size a buffer: a damaged header could
			// otherwise rent 2 GB, or throw for asking more than an array can hold.
			long totalLong = (long)length * faceCount;
			if (totalLong > int.MaxValue || HeaderSize + (long)deflatedLength > System.IO.RandomAccess.GetLength(file) ||
				totalLong > deflatedLength * MaxInflateRatio)
			{
				Interlocked.Increment(ref _misses);
				return false;
			}

			// Pooled, and each face is a span of it: the inflate buffer is multi-megabyte (LOH), and
			// Image.CreateFromData copies the span.
			int total = (int)totalLong;
			byte[] compressed = System.Buffers.ArrayPool<byte>.Shared.Rent((int)deflatedLength);
			byte[] all = System.Buffers.ArrayPool<byte>.Shared.Rent(total);
			try
			{
				if (System.IO.RandomAccess.Read(file, compressed.AsSpan(0, (int)deflatedLength), HeaderSize) != deflatedLength ||
					!InflateInto(compressed, (int)deflatedLength, all, total))
				{
					// A truncated or corrupt entry. Treated as a miss so it is simply rebuilt, which
					// is what every other unreadable container here does.
					Interlocked.Increment(ref _misses);
					return false;
				}

				Interlocked.Add(ref _bytesRead, deflatedLength);

				var decoded = new Image[faceCount];
				for (int face = 0; face < faceCount; ++face)
				{
					try
					{
						Image img = Image.CreateFromData(width, height, mipmaps, format,
							new ReadOnlySpan<byte>(all, face * (int)length, (int)length));
						if (img is null || img.IsEmpty())
						{
							Interlocked.Increment(ref _misses);
							return false;
						}
						decoded[face] = img;
					}
					catch (Exception)
					{
						Interlocked.Increment(ref _misses);
						return false;
					}
				}

				faces = decoded;
			}
			finally
			{
				System.Buffers.ArrayPool<byte>.Shared.Return(all);
				System.Buffers.ArrayPool<byte>.Shared.Return(compressed);
			}
		}

		Interlocked.Increment(ref _hits);
		return true;
	}

	/// <summary>Writes an image under a key. Failures are reported once and then ignored.</summary>
	public static void Store(
		string key, Image image, bool hasAlpha, TexCacheCost cost = TexCacheCost.Decoded) =>
		Store(key, new[] { image }, hasAlpha, cost);

	/// <summary>
	/// Writes one entry holding every face. One image for a 2D texture, six for a cube.
	/// </summary>
	/// <remarks>
	/// ONE ENTRY, NOT SIX. A cube's faces share a key because they are one asset — storing them
	/// separately would mean six lookups, six chances to be half-evicted by the budget sweep, and
	/// a cube that renders with four faces because the other two aged out.
	/// </remarks>
	public static void Store(
		string key,
		IReadOnlyList<Image> images,
		bool hasAlpha,
		TexCacheCost cost = TexCacheCost.Decoded)
	{
		if (!Engine_TexSettings.CacheEnabled || images.Count == 0)
		{
			return;
		}

		Image image = images[0];

		string path = PathFor(key);
		string directory = path[..path.LastIndexOf('/')];
		if (DirAccess.MakeDirRecursiveAbsolute(directory) != Error.Ok)
		{
			WarnOnce($"[tex] cache directory unavailable: {directory}");
			return;
		}

		// WRITTEN ASIDE AND RENAMED. Several workers reach this concurrently and the process can
		// be killed at any point in it; a half-written file under the final name is a cache entry
		// that reads as valid until its payload length check fails, which is a slow way to find
		// out. The thread id keeps two workers racing on the same key off each other's temp, and
		// the process id two instances sharing one cache folder (a second package linked to this one).
		string temp = $"{path}.{System.Environment.ProcessId}.{System.Environment.CurrentManagedThreadId}.tmp";

		byte[] payload = image.GetData();

		using (FileAccess? file = FileAccess.Open(temp, FileAccess.ModeFlags.Write))
		{
			if (file is null)
			{
				WarnOnce($"[tex] cache not writable: {temp}");
				return;
			}

			file.Store32(Magic);
			file.Store32(ContainerVersion);
			file.Store32((uint)image.GetWidth());
			file.Store32((uint)image.GetHeight());
			file.Store32((uint)(int)image.GetFormat());
			file.Store8(image.HasMipmaps() ? (byte)1 : (byte)0);
			file.Store8(hasAlpha ? (byte)1 : (byte)0);

			// WHAT IT COST TO MAKE, so the sweep can evict the cheap things first. Written into the
			// entry rather than inferred from its key, because the key is a hash and says nothing
			// about how it was produced.
			file.Store8((byte)cost);
			file.Store16((ushort)images.Count);

			// The UNCOMPRESSED length of ONE face. Every face is the same size, format and mip
			// count — Cubemap.CreateFromImages would refuse them otherwise — so one length covers
			// all of them, and it is what sizes the buffer on the way back in.
			file.Store32((uint)payload.Length);

			// ONE DEFLATE OVER EVERY FACE, not one per face. A cube's six faces are near enough
			// identical in structure that compressing them together is meaningfully better than
			// compressing them apart, and the reader wants one buffer anyway.
			byte[] raw = payload;
			if (images.Count > 1)
			{
				raw = new byte[payload.Length * images.Count];
				var at = 0;
				foreach (Image face in images)
				{
					byte[] data = face.GetData();
					System.Array.Copy(data, 0, raw, at, System.Math.Min(data.Length, payload.Length));
					at += payload.Length;
				}
			}

            byte[] deflated = Deflate(raw);
			file.Store32((uint)deflated.Length);
			file.StoreBuffer(deflated);
		}

		if (DirAccess.RenameAbsolute(temp, path) != Error.Ok)
		{
			DirAccess.RemoveAbsolute(temp);
			return;
		}

		Interlocked.Increment(ref _stores);
	}

	/// <summary>
	/// Deletes stale epochs and trims the current one to its budget. Runs off the main thread.
	/// </summary>
	/// <remarks>
	/// Call ONCE at startup. The path is globalized on the calling thread and the walk happens on
	/// a worker, because a warm cache is thousands of files and stat-ing all of them is not
	/// something to do inside a frame.
	///
	/// It races the enhancement workers by design — they may be writing entries while this is
	/// deleting them. That is safe in both directions: a deleted entry is simply re-created on
	/// the next request, and <c>.tmp</c> files are skipped so a store in flight is never the
	/// thing that gets removed.
	/// </remarks>
	public static void Maintain()
	{
		string root;
		try
		{
			root = ProjectSettings.GlobalizePath(CacheRoot);
		}
		catch (Exception)
		{
			return;
		}

		long budget = BudgetMegabytes * 1048576L;
		var thread = new Thread(() => Sweep(root, budget))
		{
			IsBackground = true,
			Name = "refii-tex-sweep",
			Priority = ThreadPriority.Lowest,
		};
		thread.Start();
	}

	/// <summary>
	/// The sweep, synchronously, against a named root and budget.
	/// </summary>
	/// <remarks>
	/// Exists so the sweep can be tested against a scratch directory instead of against the
	/// player's live cache. A budget test has to actually delete things to prove anything, and
	/// pointing that at the real root would evict a warm region's worth of entries to prove a
	/// property that has nothing to do with them.
	/// </remarks>
	internal static void SweepNow(string globalRoot, long budgetBytes) =>
		Sweep(globalRoot, budgetBytes);

	/// <summary>
	/// How much disk the cache may hold, in megabytes.
	/// </summary>
	/// <remarks>
	/// A budget is not optional here. One region measured 359 MB of DECODED entries alone, and
	/// Fable 2 has enough regions that a full playthrough would run to several gigabytes before
	/// enhancement is turned on at all. Unbounded growth in a directory nobody looks at is how a
	/// partition fills.
	/// </remarks>
	public static long BudgetMegabytes => Engine_TexSettings.BudgetMegabytes;

	private static void Sweep(string root, long budget)
	{
		if (!System.IO.Directory.Exists(root))
		{
			return;
		}

		long freed = 0;
		int removed = 0;
		int epochs = 0;

		string current = $"v{ConverterVersion}";
		foreach (string directory in Safely(() => System.IO.Directory.EnumerateDirectories(root)))
		{
			string name = System.IO.Path.GetFileName(directory);
			if (name == current)
			{
				continue;
			}

			// ANYTHING THAT IS NOT THE CURRENT EPOCH, not just directories named v-something.
			// This originally matched on a "v" prefix and would have leaked the entire layout
			// that came before epochs existed: those entries sit in two-hex-character shard
			// directories, no hex digit is a "v", so every one of them would have been skipped
			// forever by the check meant to collect them. Nothing else is allowed to live under
			// the cache root, so "not the current epoch" is the correct rule and it also covers
			// whatever the layout after this one turns out to be.

			long size = 0;
			foreach (string file in Safely(
						 () => System.IO.Directory.EnumerateFiles(
							 directory, "*", System.IO.SearchOption.AllDirectories)))
			{
				size += Length(file);
			}

			try
			{
				System.IO.Directory.Delete(directory, true);
				freed += size;
				++epochs;
			}
			catch (Exception)
			{
				// A directory another process is reading. It will be caught next startup.
			}
		}

		string epoch = System.IO.Path.Combine(root, current);
		if (!System.IO.Directory.Exists(epoch))
		{
			ReportSweep(epochs, removed, freed, 0, budget);
			return;
		}

		// Clean up any incomplete temporary files left behind by killed processes.
		foreach (string tmp in Safely(
					 () => System.IO.Directory.EnumerateFiles(
						 epoch, "*.tmp", System.IO.SearchOption.AllDirectories)))
		{
			try
			{
				System.IO.File.Delete(tmp);
			}
			catch (Exception)
			{
			}
		}

		var entries = new List<(string Path, long Size, DateTime Written, byte Cost)>();
		long total = 0;
		foreach (string file in Safely(
					 () => System.IO.Directory.EnumerateFiles(
						 epoch, "*.rftx", System.IO.SearchOption.AllDirectories)))
		{
			try
			{
				var info = new System.IO.FileInfo(file);
				entries.Add((file, info.Length, info.LastWriteTimeUtc, CostOf(file)));
				total += info.Length;
			}
			catch (Exception)
			{
				// Deleted underneath us by another sweep or an external tidy-up.
			}
		}

		if (total <= budget)
		{
			ReportSweep(epochs, removed, freed, total, budget);
			return;
		}

		// CHEAPEST FIRST, THEN OLDEST. Age alone treated a neural upscale of hundreds of
		// milliseconds exactly like a decode of tens, so a trim could throw away the most
		// expensive thing in the cache to make room for the cheapest — and then rebuild it.
		// Sorting on the cost class first means a trim spends the decodes, which are the entries
		// that cost least to get back.
		//
		// WITHIN A CLASS IT IS STILL WRITE TIME, which is insertion order and NOT true LRU — an
		// entry is written once and never rewritten, so nothing here records that a texture was
		// read a hundred times. Proper LRU would mean touching a file on every cache hit, which
		// turns the read path into a write path for the sake of an eviction that runs once per
		// startup.
		//
		// The practical consequence is that revisiting a region evicted earlier in the same
		// session re-decodes it. That is the right trade against a write per hit, and it is worth
		// knowing before anyone reads a low hit rate as a defect.
		entries.Sort((a, b) => a.Cost != b.Cost
			? a.Cost.CompareTo(b.Cost)
			: a.Written.CompareTo(b.Written));

		foreach ((string path, long size, DateTime _, byte _) in entries)
		{
			if (total <= budget)
			{
				break;
			}

			try
			{
				System.IO.File.Delete(path);
				total -= size;
				freed += size;
				++removed;
			}
			catch (Exception)
			{
			}
		}

		ReportSweep(epochs, removed, freed, total, budget);
	}

	/// <summary>
	/// Reads an entry's cost class out of its header without decoding the payload.
	/// </summary>
	/// <remarks>
	/// AT AN ABSOLUTE OFFSET, not a relative seek from wherever the reader stopped. The layout is
	/// magic(4) version(4) width(4) height(4) format(4) mipmaps(1) alpha(1) = 22, and computing
	/// that as "current position plus n" is how it was got wrong the first time — it landed on the
	/// alpha flag and would have sorted the whole cache by transparency.
	///
	/// An unreadable or older entry reports Decoded, which puts it at the front of the eviction
	/// order — exactly where a container the current build cannot read belongs.
	/// </remarks>
	private const ulong CostOffset = 22u;
	/// <summary>
	/// Deflates a payload. Level 6, which the measurement chose.
	/// </summary>
	/// <remarks>
	/// SmallestSize was not worth it: level 6 already reached 45.9% over a 200-entry sample and the
	/// higher setting costs several times the CPU for a couple of points. This runs on a background
	/// worker, but it runs once per entry for the whole game and the prebuild runs it 6,563 times
	/// in a row.
	/// </remarks>
	private static byte[] Deflate(byte[] raw)
	{
		using var output = new System.IO.MemoryStream(raw.Length / 2);
		using (var deflate = new System.IO.Compression.DeflateStream(
			output, System.IO.Compression.CompressionLevel.Optimal, leaveOpen: true))
		{
			deflate.Write(raw, 0, raw.Length);
		}

		return output.ToArray();
	}

	/// <summary>
	/// Inflates a payload into <paramref name="result"/> at exactly the expected size, or false.
	/// </summary>
	/// <remarks>
	/// THE EXPECTED SIZE IS AN INPUT, not something the stream is trusted to produce. A corrupt
	/// entry can inflate to anything at all, and <c>Image.CreateFromData</c> asserts on a buffer
	/// that is not the exact length the format demands — so a short or long result is rejected here
	/// rather than becoming a crash two frames later.
	/// </remarks>
	private static bool InflateInto(byte[] compressed, int compressedLength, byte[] result, int expected)
	{
		if (expected <= 0)
		{
			return false;
		}

		try
		{
			using var input = new System.IO.MemoryStream(compressed, 0, compressedLength);
			using var inflate = new System.IO.Compression.DeflateStream(
				input, System.IO.Compression.CompressionMode.Decompress);

			var at = 0;
			while (at < expected)
			{
				int read = inflate.Read(result, at, expected - at);
				if (read <= 0)
				{
					return false;
				}

				at += read;
			}

			// Anything left over means the entry does not describe what it claims to.
			return inflate.ReadByte() == -1;
		}
		catch (Exception)
		{
			return false;
		}
	}

	private static byte CostOf(string path)
	{
		try
		{
			using FileAccess? file = FileAccess.Open(path, FileAccess.ModeFlags.Read);
			if (file is null || file.Get32() != Magic || file.Get32() != ContainerVersion)
			{
				return (byte)TexCacheCost.Decoded;
			}

			file.Seek(CostOffset);
			return file.Get8();
		}
		catch (Exception)
		{
			return (byte)TexCacheCost.Decoded;
		}
	}

	private static void ReportSweep(
		int epochs, int removed, long freed, long remaining, long budget)
	{
		// PRINTS EVEN WHEN IT DID NOTHING. A sweep that only speaks up when it deletes something
		// leaves "is the cache being trimmed at all?" unanswerable from a log.
		GD.Print(
			$"[tex] cache sweep: {epochs} stale epoch(s), {removed:N0} entry(s) evicted, " +
			$"{freed / 1048576.0:F1} MB freed, {remaining / 1048576.0:F1} MB held " +
			$"(budget {budget / 1048576.0:N1} MB)");
	}

	private static long Length(string path)
	{
		try
		{
			return new System.IO.FileInfo(path).Length;
		}
		catch (Exception)
		{
			return 0;
		}
	}

	/// <summary>
	/// Enumerates, or yields nothing if the directory disappears mid-walk.
	/// </summary>
	/// <remarks>
	/// The lazy enumerators throw from <c>MoveNext</c>, not from the call that creates them, so a
	/// try/catch around the <c>foreach</c> header does not cover the iteration. Materializing the
	/// list is what makes the failure catchable in one place.
	/// </remarks>
	private static IReadOnlyList<string> Safely(Func<IEnumerable<string>> enumerate)
	{
		try
		{
			return enumerate().ToList();
		}
		catch (Exception)
		{
			return Array.Empty<string>();
		}
	}

	/// <summary>
	/// One line of what the cache actually did.
	/// </summary>
	/// <remarks>
	/// It prints on a good run as well as a bad one. A cache that reports only its failures
	/// leaves a working session with no evidence that it worked, and "the hit rate was fine" is
	/// then a thing nobody can check.
	/// </remarks>
	public static void Report()
	{
		long hits = Hits;
		long total = hits + Misses;
		double rate = total == 0 ? 0.0 : 100.0 * hits / total;
		GD.Print(
			$"[tex] cache {hits:N0}/{total:N0} hits ({rate:F1}%), {Stores:N0} stored, " +
			$"{Interlocked.Read(ref _bytesRead) / 1048576.0:F1} MB read");
	}

	private static string PathFor(string key) => $"{Root}/{key[..2]}/{key}.rftx";

	private static int _warned;

	private static void WarnOnce(string message)
	{
		if (Interlocked.Exchange(ref _warned, 1) == 0)
		{
			GD.PushWarning(message);
		}
	}
}
