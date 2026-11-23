using System.Globalization;
using Refii.Host;
using Refii.Ui;

namespace Refii.Titles.Fable2;

using Refii.Game;

/// <summary>
/// THE ONLINE JOINER'S TARGETED READOUT. Villagers' opinions live on the host, where their AI runs
/// and player two's opinion of each is kept (core/coop_opinion.cpp); the joiner's guest states the
/// host player's opinion in its readout. So the joiner asks the host for the villager it targets,
/// by network id (<c>opinion.ask=</c>, restated while the readout is up), the host answers with the
/// villager's five axes for player two (<c>opinion.of=</c>), and the joiner's opinion meter shows
/// that axis in the guest's own scale (host_bridge.cpp RefiiHostGetPlayerTwoTargetedQuery).
/// </summary>
public partial class GameUi
{
	private const string OpinionAskPrefix = "opinion.ask=";
	private const string OpinionOfPrefix = "opinion.of=";
	private const double OpinionAskSeconds = 0.25;

	private ulong _opinionAsked;
	private double _opinionAskIn;
	private ulong _opinionKnownFor;
	private readonly float[] _opinionKnown = new float[5];
	private bool _opinionStated;
	private readonly float[] _opinionAnswer = new float[5];

	/// <summary>The joiner's half of the exchange, once a guest frame with the readout just published.</summary>
	private void DriveJoinerOpinion(Engine_HudValues values, double delta)
	{
		bool joiner = CoopSession.PeerStanding && CoopSession.IsClient && !CoopSession.LocalCoop;
		ulong uid = joiner && Engine_UiHudTargetedQuery.Present && Engine_UiHudTargetedQuery.Entity != 0u
			? HostBridge.GetEntityUid(Engine_UiHudTargetedQuery.Entity)
			: 0ul;
		if (uid == 0ul)
		{
			_opinionAsked = 0ul;
			if (_opinionStated)
			{
				_opinionStated = false;
				values.OverrideMeter(TargetedOpinionMeter, null);
			}

			return;
		}

		_opinionAskIn -= delta;
		if (uid != _opinionAsked || _opinionAskIn <= 0.0)
		{
			_opinionAsked = uid;
			_opinionAskIn = OpinionAskSeconds;
			HostBridge.CoopSend(OpinionAskPrefix + uid.ToString(CultureInfo.InvariantCulture));
		}

		int axis = Engine_UiHudTargetedQuery.SlideSet - 1;
		if (_opinionKnownFor != uid || axis is < 0 or > 4)
		{
			return;
		}

		// The guest's own scale (0x82176f10): fear runs the other way.
		float value = _opinionKnown[axis];
		float shown = axis == 1 ? (value - 1f) * -0.5f : (value + 1f) * 0.5f;
		values.OverrideMeter(TargetedOpinionMeter, shown * 100f);
		_opinionStated = true;
	}

	/// <summary>
	/// An opinion message from the peer: the joiner's question (answered here, on the host) or the
	/// host's answer. False for anything else.
	/// </summary>
	private bool TakeOpinionMessage(string message)
	{
		if (message.StartsWith(OpinionAskPrefix, System.StringComparison.Ordinal))
		{
			if (!CoopSession.IsClient &&
				ulong.TryParse(message[OpinionAskPrefix.Length..], NumberStyles.Integer, CultureInfo.InvariantCulture, out ulong uid) &&
				Fable2Bridge.GetOpinionOfUid(uid, 2, _opinionAnswer))
			{
				HostBridge.CoopSend(string.Create(CultureInfo.InvariantCulture,
					$"{OpinionOfPrefix}{uid}:{_opinionAnswer[0]:R}:{_opinionAnswer[1]:R}:{_opinionAnswer[2]:R}:{_opinionAnswer[3]:R}:{_opinionAnswer[4]:R}"));
			}

			return true;
		}

		if (message.StartsWith(OpinionOfPrefix, System.StringComparison.Ordinal))
		{
			string[] parts = message[OpinionOfPrefix.Length..].Split(':');
			if (CoopSession.IsClient && parts.Length == 6 &&
				ulong.TryParse(parts[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out ulong uid))
			{
				System.Span<float> axes = stackalloc float[5];
				for (int i = 0; i < 5; ++i)
				{
					if (!float.TryParse(parts[i + 1], NumberStyles.Float, CultureInfo.InvariantCulture, out axes[i]))
					{
						return true;
					}
				}

				axes.CopyTo(_opinionKnown);
				_opinionKnownFor = uid;
			}

			return true;
		}

		return false;
	}
}
