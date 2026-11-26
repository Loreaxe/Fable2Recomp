namespace Refii.Tests;

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using GdUnit4;
using static GdUnit4.Assertions;
using Refii.Host;
using Refii.Titles.Fable2;

/// <summary>
/// THE CO-OP LUA IS SAVE-SAFE, held at the bytecode the game runs. A save persists every running
/// quest and action-use thread with Pluto, and a suspended frame keeps its pc while its function is
/// written BY NAME (a permanent) and resolved by name on load: a frame of ours under a yield would
/// resume into whatever body then carries that name. So (1) the only frames of ours that may sit
/// under a yield are the two frozen trampolines C.Call1 / C.Wait1, pinned here instruction for
/// instruction; (2) nothing else calls coroutine.yield; (3) every call that can reach yielding code
/// — a local or upvalue named fn or shipped*, and the scope runners (C.Call1, C.Wait1, C.RunAs,
/// C.RunScoped, C.RunResolved, C.RunNear, C.RunJob, C.WithFlag) — is a TAILCALL, so the caller's
/// frame is gone before anything yields, except the listed sites that never yield.
/// The chunks are compiled with the game's own compiler (refii_luadec), debug info kept.
/// </summary>
[TestSuite]
public class CoopLuaSaveSafeTests
{
	// ------------------------------------------------------------------ the frozen trampolines
	// Their bytecode, as the game compiles them. NEVER CHANGE THESE: a saved thread resumes a
	// Call1/Wait1 frame at its saved pc. A new trampoline gets a new name.
	private const string Call1Pinned =
		"p3 v2 s7 u0 code:000000C0.00800100.01000140.000001A5.0000015C.000000DD.000000DE.0080001E k:";
	private const string Wait1Pinned =
		"p3 v0 s6 u0 code:000000C0.00800100.0100C0DC.000000DA.80000016.0100011E.01000140.0080415C.7FFD8016.0080001E k:";

	/// <summary>
	/// Calls that are not tail calls and are allowed, each by a snippet of its source line and why
	/// it never yields. Every entry must still match a call (a stale entry fails the test).
	/// </summary>
	private static readonly (string Anchor, string Why)[] NeverYields =
	{
		("if shipped(entity, ...) then", "Targeted.IsHeroWithinInteractionDistance / IsEntityVisibleOnScreen: natives"),
		("local target = shipped(...)", "ScriptFunction.GetPlayersTarget: a native"),
		("local sleep_type = shipped(bed, ...)", "Bed.GetSleepType: a native"),
		("local results = { shipped(hero, ...) }", "Player.RemoveHUDSuggestionOfType: a native"),
		("shipped(other, ...)", "Player.RemoveHUDSuggestionOfType: a native"),
		("local result = shipped(...)", "BehaviourDogVariables.Reload: dog init, never yields"),
		("local module = shipped(name, ...)", "QuestManager.LoadQuestModule: require, never yields"),
		("shipped(entity, C.CombatAcc[1][field], ...)", "Player.Set*Combat*: natives"),
		("return shipped(one, b, ...) and shipped(two, b, ...)", "distance natives"),
		("return shipped(a, one, ...) and shipped(a, two, ...)", "distance natives"),
		("if not shipped(combat_info) then", "a combat sequence's IsValid predicate"),
		("local result = shipped(self, hero, ...)", "CameraManager:FrameUpdate: the native camera update, main thread"),
		("C.RunAs(two, OpinionReaction.IsEngagedToHero, villager)", "a native"),
		("C.RunAs(one, OpinionReaction.IsEngagedToHero, villager)", "a native"),
		("C.RunAs(one, in_range, gm)", "a cutscene's IsPlayerInRange, inside pcall (nothing yields across it)"),
		("C.RunAs(two, in_range, gm)", "a cutscene's IsPlayerInRange, inside pcall (nothing yields across it)"),
		("C.RunAs(player == 2 and partner or C.ShippedGetPlayerHero(), shipped_score", "CombatRegister scoring: main thread"),
		("C.RunAs(two, fn, ...)", "C.RunAsBoth: callers state fn never yields (FastExit, crime AI pause)"),
		("local results = { C.RunAs(C.ShippedGetPlayerHero(), fn, ...) }", "C.RunAsBoth, as above"),
		("local results = { C.RunAs(hero, shipped, self, ...) }", "C.PrepareAsEither: a PrepareToRun, chosen on the main thread"),
		("local results = { C.RunAs(C.ShippedGetPlayerHero(), shipped, entity, ...) }", "a behaviour group's PrepareToRun predicate"),
		("if not results[1] or not C.RunAs(two, shipped, entity, ...) then", "a behaviour group's PrepareToRun predicate"),
		("local results = { C.RunAs(C.ShippedGetPlayerHero(), shipped, self, partner, ...) }", "BehaviourVillagerChat predicate"),
	};

	/// <summary>
	/// The save-compat stub (Engine_SaveCompatLua) is what an unknown permanent resolves to, and a
	/// saved frame resumes INTO it at its saved pc: every instruction but the last must be a MOVE
	/// between its two locals, the last a RETURN, and it takes no upvalues.
	/// </summary>
	[TestCase]
	public void TheSaveCompatStubIsMovesThenReturn()
	{
		string text = Engine_SaveCompatLua.Source;
		byte[]? bytes = Compile(text, "@refii/save-compat", out string why);
		if (bytes == null && why.Length == 0)
		{
			return;  // no game compiler beside the test host (a packaged run)
		}

		AssertThat(bytes != null).OverrideFailureMessage($"save-compat does not compile: {why}").IsTrue();
		LuaProto main = LuaChunk.Parse(bytes!);
		LuaProto? stub = LuaChunk.All(main).FirstOrDefault(p => p.Code.Length >= Engine_SaveCompatLua.StubMoves);
		AssertThat(stub != null).OverrideFailureMessage("the stub function was not found").IsTrue();
		// `local a, b` at pc 0 emits no LOADNIL (registers are nil at entry), so the body is the
		// MOVEs and the RETURN alone.
		uint[] code = stub!.Code;
		const uint OpMove = 0, OpReturn = 30;
		AssertThat(code[^1] & 0x3F).IsEqual(OpReturn);
		int moves = code.Take(code.Length - 1).Count(i => (i & 0x3F) == OpMove);
		AssertThat(moves).IsEqual(code.Length - 1);
		AssertThat(moves).IsEqual(Engine_SaveCompatLua.StubMoves);
		AssertThat(stub.Nups).IsEqual(0);
	}

	private static string? RepoRoot()
	{
		string? at = AppContext.BaseDirectory;
		for (int i = 0; i < 12 && at != null; i++)
		{
			if (File.Exists(Path.Combine(at, "Godot", "src", "Titles", "Fable2", "host", "CoopHeroLua.cs")))
			{
				return at;
			}

			at = Path.GetDirectoryName(at);
		}

		return null;
	}

	private static string ConstText(string name) =>
		(string)typeof(CoopHeroLua).GetField(name, BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Static)!
			.GetRawConstantValue()!;

	[TestCase]
	public void TheCoopLuaOnlyYieldsThroughTheFrozenTrampolines()
	{
		string core = ConstText("Source");
		string camera = ConstText("CameraSource");
		string cock = Engine_GoldenCockRuntime.ScriptFor("bowerstone_north", "CVector3(1,2,3)");
		string mirror = Engine_MorphMirrorRuntime.ScriptFor("caves/bowerlake/thagscave", "CVector3(1,2,3)", "CVector3(4,5,6)", "CVector3(0,1,0)");
		var chunks = new[] { ("@refii/core_init.lua", core), ("@refii/core_camera.lua", camera), ("@refii/golden-cock", cock), ("@refii/morph-mirror", mirror) };

		var compiled = new List<(string Name, string Text, LuaProto Main)>();
		foreach ((string name, string text) in chunks)
		{
			byte[]? bytes = Compile(text, name, out string why);
			if (bytes == null && why.Length == 0)
			{
				return;  // no game compiler beside the test host (a packaged run)
			}

			AssertThat(bytes != null).OverrideFailureMessage($"{name} does not compile: {why}").IsTrue();
			compiled.Add((name, text, LuaChunk.Parse(bytes!)));
		}

		// (1) The trampolines, pinned.
		(string _, string coreText, LuaProto coreMain) = compiled[0];
		LuaProto? call1 = LuaChunk.ProtoAtLine(coreMain, LineOf(coreText, "C.Call1 = C.Permanent(function(after, state, fn, ...)"));
		LuaProto? wait1 = LuaChunk.ProtoAtLine(coreMain, LineOf(coreText, "C.Wait1 = C.Permanent(function(poll, state, yield)"));
		AssertThat(call1 != null && wait1 != null).OverrideFailureMessage("C.Call1 / C.Wait1 not found").IsTrue();
		var violations = new List<string>();
		if (LuaChunk.Signature(call1!) != Call1Pinned)
		{
			violations.Add("C.Call1's bytecode changed: " + LuaChunk.Signature(call1!));
		}

		if (LuaChunk.Signature(wait1!) != Wait1Pinned)
		{
			violations.Add("C.Wait1's bytecode changed: " + LuaChunk.Signature(wait1!));
		}

		var used = new HashSet<string>(StringComparer.Ordinal);
		foreach ((string name, string text, LuaProto main) in compiled)
		{
			string[] lines = text.Split('\n');
			foreach (LuaProto proto in LuaChunk.All(main))
			{
				bool trampoline = ReferenceEquals(proto, call1) || ReferenceEquals(proto, wait1);
				foreach (LuaCall call in LuaChunk.Calls(proto))
				{
					string line = call.Line > 0 && call.Line <= lines.Length ? lines[call.Line - 1].Trim() : "?";
					string where = $"{name}:{call.Line} {call.Callee}: {line}";

					// (2) Nothing yields but Wait1 (through its `yield` argument).
					if (call.Callee == "coroutine.yield" || (call.Callee == "yield" && !trampoline))
					{
						violations.Add("yields: " + where);
						continue;
					}

					// (3) A call that can reach yielding code is a tail call.
					if (trampoline || call.Tail || !LuaCall.Watched(call.Callee))
					{
						continue;
					}

					(string Anchor, string Why) allowed = NeverYields.FirstOrDefault(a => line.Contains(a.Anchor, StringComparison.Ordinal));
					if (allowed.Anchor != null)
					{
						used.Add(allowed.Anchor);
						continue;
					}

					violations.Add("not a tail call: " + where);
				}
			}
		}

		AssertThat(violations.Count == 0).OverrideFailureMessage("not save-safe:\n" + string.Join("\n", violations)).IsTrue();
		string[] stale = NeverYields.Select(a => a.Anchor).Where(a => !used.Contains(a)).ToArray();
		AssertThat(stale.Length == 0).OverrideFailureMessage("stale allowlist entries:\n" + string.Join("\n", stale)).IsTrue();
	}

	[TestCase]
	public void TheCoroutineScopeIsOneTableAndItIsSaved()
	{
		string core = ConstText("Source");

		// One per-coroutine record, no weak per-coroutine tables beside it.
		AssertThat(core.Contains("C.CoState = C.CoState or setmetatable({}, { __mode = \"k\" })", StringComparison.Ordinal)).IsTrue();
		AssertThat(core.Split("__mode = \"k\"").Length - 1).IsEqual(1);

		// Saved with the game: the threads the shipped save reaches, restored on load.
		AssertThat(core.Contains("t.RefiiCoop = { Version = 1, Threads = threads }", StringComparison.Ordinal)).IsTrue();
		AssertThat(core.Contains("C.CoState[co] = record", StringComparison.Ordinal)).IsTrue();

		// The trampolines are frozen text too (a test elsewhere already reads them).
		AssertThat(core.Contains("C.Call1 = C.Permanent(function(after, state, fn, ...) return after(state, fn(...)) end, \"Call1\")",
			StringComparison.Ordinal)).IsTrue();
	}

	private static int LineOf(string text, string needle)
	{
		int at = text.IndexOf(needle, StringComparison.Ordinal);
		return at < 0 ? -1 : text.Take(at).Count(c => c == '\n') + 1;
	}

	/// <summary>
	/// The chunk compiled with the game's compiler and its debug info. Null with an empty reason
	/// only outside a Linux source tree (a packaged run); null with the reason when the text does
	/// not compile or a source tree holds no compiler.
	/// </summary>
	private static byte[]? Compile(string lua, string name, out string why)
	{
		why = string.Empty;
		if (Refii.Lua.Engine_LuaDecompiler.TryCompile(lua, name, false, out byte[] chunk, out string error))
		{
			return chunk;
		}

		if (!error.Contains("not available", StringComparison.Ordinal) && !error.Contains("out of date", StringComparison.Ordinal))
		{
			why = error;
			return null;
		}

		// A library from before RefiiLuadecCompile: the same compiler, as the refii-luac32 tool.
		string? root = RepoRoot();
		if (root == null || !OperatingSystem.IsLinux())
		{
			return null;
		}

		string builds = Path.Combine(root, "out", "build");
		string? tool = Directory.Exists(builds)
			? Directory.GetDirectories(builds).Select(d => Path.Combine(d, "tools", "refii-luac32")).Where(File.Exists)
				.OrderByDescending(File.GetLastWriteTimeUtc).FirstOrDefault()
			: null;
		if (tool == null)
		{
			// A source tree on Linux always has the compiler (the native build makes it): its absence
			// is a broken check, never a pass.
			why = $"no game compiler in this source tree ({Path.Combine(builds, "*", "tools", "refii-luac32")})";
			return null;
		}

		var start = new ProcessStartInfo(tool)
		{
			RedirectStandardInput = true,
			RedirectStandardOutput = true,
			RedirectStandardError = true,
			UseShellExecute = false,
		};
		foreach (string arg in new[] { "/dev/stdin", "-o", "/dev/stdout", "-n", name })
		{
			start.ArgumentList.Add(arg);
		}

		using Process process = Process.Start(start)!;
		var stderr = process.StandardError.ReadToEndAsync();
		var output = new MemoryStream();
		var reading = process.StandardOutput.BaseStream.CopyToAsync(output);
		byte[] utf8 = Encoding.UTF8.GetBytes(lua);
		process.StandardInput.BaseStream.Write(utf8, 0, utf8.Length);
		process.StandardInput.Close();
		reading.Wait();
		process.WaitForExit();
		if (process.ExitCode != 0)
		{
			why = stderr.Result.Trim();
			return null;
		}

		return output.ToArray();
	}
}

/// <summary>One function of a compiled chunk (Lua 5.1, the guest's header: little-endian, 4-byte
/// int, size_t, instruction and float number).</summary>
internal sealed class LuaProto
{
	public int LineDefined;
	public int Nups;
	public int NumParams;
	public int IsVararg;
	public int MaxStack;
	public uint[] Code = Array.Empty<uint>();
	public object?[] Constants = Array.Empty<object?>();
	public List<LuaProto> Protos = new();
	public int[] LineInfo = Array.Empty<int>();
	public (string Name, int Start, int End)[] Locals = Array.Empty<(string, int, int)>();
	public string[] Upvalues = Array.Empty<string>();
}

internal readonly record struct LuaCall(string Callee, bool Tail, int Line)
{
	private static readonly HashSet<string> Runners = new(StringComparer.Ordinal)
	{
		"C.Call1", "C.Wait1", "C.RunAs", "C.RunScoped", "C.RunResolved", "C.RunNear", "C.RunJob", "C.WithFlag",
	};

	/// <summary>A callee that can reach yielding code.</summary>
	public static bool Watched(string callee) =>
		callee == "fn" || callee.StartsWith("shipped", StringComparison.Ordinal) || Runners.Contains(callee);
}

internal static class LuaChunk
{
	private const int OpMove = 0, OpLoadK = 1, OpLoadBool = 2, OpLoadNil = 3, OpGetUpval = 4, OpGetGlobal = 5,
		OpGetTable = 6, OpSelf = 11, OpCall = 28, OpTailCall = 29, OpForLoop = 31, OpForPrep = 32,
		OpTForLoop = 33, OpClosure = 36, OpVararg = 37;

	public static LuaProto Parse(byte[] bytes)
	{
		if (bytes.Length < 12 || bytes[0] != 0x1b || bytes[1] != (byte)'L' || bytes[4] != 0x51 || bytes[6] != 1
			|| bytes[7] != 4 || bytes[8] != 4 || bytes[9] != 4 || bytes[10] != 4)
		{
			throw new InvalidDataException("not a guest Lua 5.1 chunk");
		}

		int at = 12;
		return ReadFunction(bytes, ref at);
	}

	private static int Int(byte[] b, ref int at)
	{
		int v = BitConverter.ToInt32(b, at);
		at += 4;
		return v;
	}

	private static string? String(byte[] b, ref int at)
	{
		int size = Int(b, ref at);
		if (size == 0)
		{
			return null;
		}

		string s = Encoding.UTF8.GetString(b, at, size - 1);
		at += size;
		return s;
	}

	private static LuaProto ReadFunction(byte[] b, ref int at)
	{
		var p = new LuaProto();
		String(b, ref at);
		p.LineDefined = Int(b, ref at);
		Int(b, ref at);
		p.Nups = b[at++];
		p.NumParams = b[at++];
		p.IsVararg = b[at++];
		p.MaxStack = b[at++];
		p.Code = new uint[Int(b, ref at)];
		for (int i = 0; i < p.Code.Length; i++)
		{
			p.Code[i] = (uint)Int(b, ref at);
		}

		p.Constants = new object?[Int(b, ref at)];
		for (int i = 0; i < p.Constants.Length; i++)
		{
			byte type = b[at++];
			switch (type)
			{
				case 0: p.Constants[i] = null; break;
				case 1: p.Constants[i] = b[at++] != 0; break;
				case 3: p.Constants[i] = BitConverter.ToSingle(b, at); at += 4; break;
				case 4: p.Constants[i] = String(b, ref at); break;
				default: throw new InvalidDataException($"constant type {type}");
			}
		}

		int protos = Int(b, ref at);
		for (int i = 0; i < protos; i++)
		{
			p.Protos.Add(ReadFunction(b, ref at));
		}

		p.LineInfo = new int[Int(b, ref at)];
		for (int i = 0; i < p.LineInfo.Length; i++)
		{
			p.LineInfo[i] = Int(b, ref at);
		}

		p.Locals = new (string, int, int)[Int(b, ref at)];
		for (int i = 0; i < p.Locals.Length; i++)
		{
			string name = String(b, ref at) ?? "?";
			p.Locals[i] = (name, Int(b, ref at), Int(b, ref at));
		}

		p.Upvalues = new string[Int(b, ref at)];
		for (int i = 0; i < p.Upvalues.Length; i++)
		{
			p.Upvalues[i] = String(b, ref at) ?? "?";
		}

		return p;
	}

	public static IEnumerable<LuaProto> All(LuaProto p)
	{
		yield return p;
		foreach (LuaProto child in p.Protos)
		{
			foreach (LuaProto q in All(child))
			{
				yield return q;
			}
		}
	}

	public static LuaProto? ProtoAtLine(LuaProto main, int line) =>
		All(main).FirstOrDefault(p => !ReferenceEquals(p, main) && p.LineDefined == line);

	/// <summary>What the runtime keeps of a function: its shape, code and constants (not lines).</summary>
	public static string Signature(LuaProto p)
	{
		string code = string.Join(".", p.Code.Select(i => i.ToString("X8")));
		string k = string.Join(",", p.Constants.Select(c => c?.ToString() ?? "nil"));
		return $"p{p.NumParams} v{p.IsVararg} s{p.MaxStack} u{p.Nups} code:{code} k:{k}";
	}

	/// <summary>Every CALL and TAILCALL of a function, with the callee as written where it can be read.</summary>
	public static IEnumerable<LuaCall> Calls(LuaProto p)
	{
		bool[] pseudo = new bool[p.Code.Length];
		for (int i = 0; i < p.Code.Length; i++)
		{
			if (Op(p.Code[i]) == OpClosure)
			{
				int nups = p.Protos[(int)(p.Code[i] >> 14)].Nups;
				for (int j = 1; j <= nups && i + j < p.Code.Length; j++)
				{
					pseudo[i + j] = true;
				}
			}
		}

		for (int pc = 0; pc < p.Code.Length; pc++)
		{
			int op = Op(p.Code[pc]);
			if (pseudo[pc] || (op != OpCall && op != OpTailCall))
			{
				continue;
			}

			string callee = Value(p, pseudo, A(p.Code[pc]), pc) ?? "?";
			yield return new LuaCall(callee, op == OpTailCall, pc < p.LineInfo.Length ? p.LineInfo[pc] : 0);
		}
	}

	private static int Op(uint i) => (int)(i & 0x3F);
	private static int A(uint i) => (int)((i >> 6) & 0xFF);
	private static int B(uint i) => (int)((i >> 23) & 0x1FF);
	private static int C(uint i) => (int)((i >> 14) & 0x1FF);

	/// <summary>The name of a local in register `reg` at `pc` (luaF_getlocalname).</summary>
	private static string? LocalName(LuaProto p, int reg, int pc)
	{
		int n = reg + 1;
		foreach ((string name, int start, int end) in p.Locals)
		{
			if (start > pc)
			{
				break;
			}

			if (pc < end && --n == 0)
			{
				return name;
			}
		}

		return null;
	}

	/// <summary>What register `reg` holds just before `pc`: a local's name, an upvalue's, a global's or a field path.</summary>
	private static string? Value(LuaProto p, bool[] pseudo, int reg, int pc)
	{
		string? local = LocalName(p, reg, pc);
		if (local != null)
		{
			return local;
		}

		for (int i = pc - 1; i >= 0; i--)
		{
			if (pseudo[i])
			{
				continue;
			}

			uint ins = p.Code[i];
			int op = Op(ins), a = A(ins), b = B(ins), c = C(ins);
			switch (op)
			{
				case OpMove when a == reg:
					return Value(p, pseudo, b, i);
				case OpGetUpval when a == reg:
					return b < p.Upvalues.Length ? p.Upvalues[b] : null;
				case OpGetGlobal when a == reg:
					return p.Constants[(int)(ins >> 14)] as string;
				case OpGetTable when a == reg:
				{
					string? key = (c & 0x100) != 0 ? p.Constants[c & 0xFF] as string : null;
					string? table = Value(p, pseudo, b, i);
					return key != null && table != null ? table + "." + key : null;
				}
				case OpSelf when a == reg:
				{
					string? key = (c & 0x100) != 0 ? p.Constants[c & 0xFF] as string : null;
					string? table = Value(p, pseudo, b, i);
					return key != null && table != null ? table + ":" + key : null;
				}
				case OpSelf when a + 1 == reg:
				case OpLoadNil when a <= reg && reg <= b:
				case OpForLoop when reg == a || reg == a + 3:
				case OpTForLoop when reg >= a + 3:
				case OpCall when reg >= a:
				case OpTailCall when reg >= a:
				case OpVararg when reg >= a:
					return null;
				case OpMove or OpLoadK or OpLoadBool or OpGetUpval or OpGetGlobal or OpGetTable or OpSelf or OpLoadNil
					or OpForLoop or OpTForLoop or OpCall or OpTailCall or OpVararg:
					continue;
				case 9 or 7 or 8 or 22 or 23 or 24 or 25 or 26 or 30 or 34 or 35:
					continue;  // SETTABLE, SETGLOBAL, SETUPVAL, JMP, EQ, LT, LE, TEST, RETURN, SETLIST, CLOSE
				default:
					if (a == reg)
					{
						return null;  // arithmetic, NEWTABLE, NOT, LEN, CONCAT, CLOSURE, TESTSET, FORPREP
					}

					continue;
			}
		}

		return null;
	}
}
