namespace Refii.Tests;

using System;
using System.Collections.Generic;
using System.Linq;
using GdUnit4;
using static GdUnit4.Assertions;
using Refii.Titles.Fable3;

/// <summary>
/// FABLE 3'S CORE CHUNK CALLS THE GAME'S OWN CODE AS A TAIL CALL, held at the bytecode the game runs
/// (compiled with the game's compiler, as <see cref="CoopLuaSaveSafeTests"/> does Fable 2's). A wrapper's
/// frame left under a shipped function that yields — a camera script, a quest or action-use thread —
/// is a frame of ours inside a suspended coroutine the save writes. So every call to a local or upvalue
/// named fn or shipped* is a TAILCALL, except the listed sites that never yield.
/// </summary>
[TestSuite]
public class Fable3CoreLuaSaveSafeTests
{
	/// <summary>
	/// Calls that are not tail calls and are allowed, each by a snippet of its source line and why. Every
	/// entry must still match a call (a stale entry fails the test).
	/// </summary>
	private static readonly (string Anchor, string Why)[] NeverYields =
	{
		("local save = shipped(...)", "CameraManager.GetSaveTable: builds a table (cameramanager.lua 208-213), no call"),
		("local r1, r2, r3 = shipped(self, ...)", "RangedCombatCamera:UpdateCurrentDirection: the camera update; rangedcombatcamera.lua has no yield"),
		("local r1, r2, r3 = shipped(self, player, ...)", "CameraManager:OnLevelLoad (409-417): cameramanager.lua's one yield is IsEntityVisibleOnAnyScreen (458), not reached"),
		("local x, y, z = fn(N(e))", "Refii._queries' XYZ: fn is Refii.position / facing / velocity, natives"),
		("return Leave(key, before, fn(...))", "Refii.run_as_player inside a coroutine keeps its frame under fn on purpose (the scope is left when fn returns). Fable 3's save DOES write such a thread (a mod's quest or general-script coroutine, questmanager.lua 552-565 / generalscriptmanager.lua 151-157): the frame goes by value with upvalues that hold no upvalue (TheActingFrameIsSaveSafe) and the scope goes with the save as RefiiActing"),
	};

	/// <summary>
	/// Refii.run_as_player's frame, parked under a yield in a thread the save holds, is written by value: its
	/// upvalues are WrapHeroes, ActingKey and Leave, whose own upvalues are only Acting and ActingKey, so no
	/// table of ours (Refii, with its natives) is dragged into the save through them. The scope itself goes with the
	/// save: GetSaveTable / LoadScriptSave carry RefiiActing for the threads the save table reaches.
	/// </summary>
	[TestCase]
	public void TheActingFrameIsSaveSafe()
	{
		const string name = "@refii/core_init.lua";
		string text = Fable3CoreLua.Source;
		AssertThat(text.Contains("t.RefiiActing = { Version = 1, Threads = threads }", StringComparison.Ordinal)).IsTrue();
		AssertThat(text.Contains("if type(co) == \"thread\" and (p == 1 or p == 2) then Refii._acting[co] = p end", StringComparison.Ordinal)).IsTrue();
		byte[]? bytes = CoopLuaSaveSafeTests.Compile(text, name, out string why);
		if (bytes == null && why.Length == 0)
		{
			return;  // no game compiler beside the test host (a packaged run)
		}

		AssertThat(bytes != null).OverrideFailureMessage($"{name} does not compile: {why}").IsTrue();
		LuaProto main = LuaChunk.Parse(bytes!);
		string[] lines = text.Split('\n');
		int LineOf(string needle) => Array.FindIndex(lines, l => l.Contains(needle, StringComparison.Ordinal)) + 1;

		LuaProto? run = LuaChunk.ProtoAtLine(main, LineOf("function Refii.run_as_player(p, fn, ...)"));
		AssertThat(run != null).OverrideFailureMessage("Refii.run_as_player not found").IsTrue();
		AssertThat(run!.Nups).IsEqual(3);
		if (run.Upvalues.Length == 3)
		{
			AssertThat(run.Upvalues.OrderBy(u => u, StringComparer.Ordinal).ToArray())
				.IsEqual(new[] { "ActingKey", "Leave", "WrapHeroes" });
		}

		// The upvalue chain under the frame: WrapHeroes holds Acting (its two hero wrappers ask it), Acting
		// holds ActingKey, and the rest hold nothing — every link a function of this list.
		foreach ((string local, int nups, string[] names) in new[]
		{
			("local function WrapHeroes()", 1, new[] { "Acting" }),
			("local function Acting()", 1, new[] { "ActingKey" }),
			("local function ActingKey()", 0, Array.Empty<string>()),
			("local function Leave(key, before, ...)", 0, Array.Empty<string>()),
		})
		{
			LuaProto? proto = LuaChunk.ProtoAtLine(main, LineOf(local));
			AssertThat(proto != null).OverrideFailureMessage(local + " not found").IsTrue();
			AssertThat(proto!.Nups).OverrideFailureMessage(local).IsEqual(nups);
			if (proto.Upvalues.Length == nups)
			{
				AssertThat(proto.Upvalues).OverrideFailureMessage(local).IsEqual(names);
			}
		}
	}

	[TestCase]
	public void TheCoreChunkCallsTheGamesOwnCodeAsATailCall()
	{
		const string name = "@refii/core_init.lua";
		string text = Fable3CoreLua.Source;
		byte[]? bytes = CoopLuaSaveSafeTests.Compile(text, name, out string why);
		if (bytes == null && why.Length == 0)
		{
			return;  // no game compiler beside the test host (a packaged run)
		}

		AssertThat(bytes != null).OverrideFailureMessage($"{name} does not compile: {why}").IsTrue();
		string[] lines = text.Split('\n');
		var violations = new List<string>();
		var used = new HashSet<string>(StringComparer.Ordinal);
		foreach (LuaProto proto in LuaChunk.All(LuaChunk.Parse(bytes!)))
		{
			foreach (LuaCall call in LuaChunk.Calls(proto))
			{
				if (call.Callee == "coroutine.yield")
				{
					violations.Add($"yields: {name}:{call.Line}");
					continue;
				}

				if (call.Tail || !(call.Callee == "fn" || call.Callee.StartsWith("shipped", StringComparison.Ordinal)))
				{
					continue;
				}

				string line = call.Line > 0 && call.Line <= lines.Length ? lines[call.Line - 1].Trim() : "?";
				(string Anchor, string Why) allowed = NeverYields.FirstOrDefault(a => line.Contains(a.Anchor, StringComparison.Ordinal));
				if (allowed.Anchor != null)
				{
					used.Add(allowed.Anchor);
					continue;
				}

				violations.Add($"not a tail call: {name}:{call.Line} {call.Callee}: {line}");
			}
		}

		AssertThat(violations.Count == 0).OverrideFailureMessage("not save-safe:\n" + string.Join("\n", violations)).IsTrue();
		string[] stale = NeverYields.Select(a => a.Anchor).Where(a => !used.Contains(a)).ToArray();
		AssertThat(stale.Length == 0).OverrideFailureMessage("stale allowlist entries:\n" + string.Join("\n", stale)).IsTrue();
	}
}
