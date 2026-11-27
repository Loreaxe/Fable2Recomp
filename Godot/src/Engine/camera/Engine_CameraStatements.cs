using System.Collections.Generic;
using System.Runtime.CompilerServices;
using Godot;

namespace Refii.Camera;

/// <summary>
/// THE CAMERA A TITLE STATES, as records: what its camera component's setters, its manager's
/// activation, its easing step and its push were called with, in the guest's order
/// (src/runtime/include/core/camera_statements.h, which this mirrors; a test compares the two).
/// </summary>
/// <remarks>
/// A record is <see cref="RecordWords"/> words: sequence; call | depth &lt;&lt; 8 | player &lt;&lt; 16 |
/// flags &lt;&lt; 24; manager; component; entity; the integer argument; sixteen floats; the guest frame.
/// Depth is how deep inside another camera hook the call was made: SetTarget states its point and
/// the direction body it calls states the direction one deeper, before it.
/// </remarks>
public static class Engine_CameraStatements
{
	public const int RecordWords = 24;

	/// <summary>The call ids, the header's <c>refii::camera::Call</c>.</summary>
	public enum Call : byte
	{
		None = 0,
		MoveTo = 1,
		MoveToCut = 2,
		SetDirection = 3,
		SetDirectionCut = 4,
		SetTarget = 5,
		SetTargetCut = 6,
		SetAngles = 7,
		SetAnglesCut = 8,
		SetFov = 9,
		SetFovCut = 10,
		SetRoll = 11,
		SetRollCut = 12,
		SetZoomLevel = 13,
		SetDof = 14,
		SetDofCut = 15,
		AddShake = 16,
		SetShake = 17,
		StopShake = 18,
		InterpolateFor = 19,
		SetCutThisFrame = 20,
		SetBlur = 21,
		SetBlurCut = 22,
		SetRepulsion = 23,
		Activate = 24,
		EaseBegin = 25,
		EaseEnd = 26,
		ShakePosition = 27,
		ShakeRotation = 28,
		Repulsion = 29,
		GuestBlock = 30,
		Push = 31,
		SnapBegin = 32,
		SnapEnd = 33,
		Destroy = 34,
	}

	/// <summary>The Push flag: this machine draws the pushed manager's camera.</summary>
	public const byte FlagDrawn = 1;

	[InlineArray(16)]
	public struct Floats
	{
		private float _first;
	}

	/// <summary>One record.</summary>
	public struct Statement
	{
		public uint Sequence;
		public Call Call;
		public byte Depth;
		public byte Player;
		public byte Flags;
		public uint Manager;
		public uint Component;
		public uint Entity;
		public uint Word;
		public uint GuestFrame;
		public Floats Values;

		/// <summary>Values <paramref name="at"/>..+2 as a guest-axis vector.</summary>
		public readonly Vector3 Vector(int at) => new(Values[at], Values[at + 1], Values[at + 2]);

		/// <summary>Value <paramref name="at"/> as the raw word it was stated as.</summary>
		public readonly uint Bits(int at) => System.BitConverter.SingleToUInt32Bits(Values[at]);

		public static Statement From(uint[] words, int offset)
		{
			uint packed = words[offset + 1];
			var statement = new Statement
			{
				Sequence = words[offset],
				Call = (Call)(packed & 0xFFu),
				Depth = (byte)((packed >> 8) & 0xFFu),
				Player = (byte)((packed >> 16) & 0xFFu),
				Flags = (byte)(packed >> 24),
				Manager = words[offset + 2],
				Component = words[offset + 3],
				Entity = words[offset + 4],
				Word = words[offset + 5],
				GuestFrame = words[offset + 22],
			};
			for (int i = 0; i < 16; i++)
			{
				statement.Values[i] = System.BitConverter.UInt32BitsToSingle(words[offset + 6 + i]);
			}

			return statement;
		}

		/// <summary>The record's words, as the bridge hands them over (tests build records this way).</summary>
		public readonly uint[] ToWords()
		{
			var words = new uint[RecordWords];
			words[0] = Sequence;
			words[1] = (uint)Call | ((uint)Depth << 8) | ((uint)Player << 16) | ((uint)Flags << 24);
			words[2] = Manager;
			words[3] = Component;
			words[4] = Entity;
			words[5] = Word;
			for (int i = 0; i < 16; i++)
			{
				words[6 + i] = System.BitConverter.SingleToUInt32Bits(Values[i]);
			}

			words[22] = GuestFrame;
			return words;
		}
	}

	/// <summary>The <paramref name="count"/> records in <paramref name="words"/>, appended in order.</summary>
	public static void Parse(uint[] words, uint count, List<Statement> into)
	{
		for (int i = 0; i < count; i++)
		{
			into.Add(Statement.From(words, i * RecordWords));
		}
	}
}
