using System.Collections.Generic;
using System.Globalization;
using Godot;
using Refii.Host;
using Refii.Ui;

namespace Refii.Titles.Fable2;

using Refii.Game;

/// <summary>
/// PARTNER CONSENT, Fable 3's bed rule: sleeping moves the clock for both players and sex fades
/// both screens, so the other player is asked first (Fable 3 `sub_825311B0` / `sub_825314C8`, the
/// `*_REJECTED_BY_P2` answers). A co-op script asks (core Lua, `Refii._consent_ask`) and waits; the
/// question comes here through the host bridge (core/coop_consent.cpp).
/// </summary>
/// <remarks>
/// THE PARTNER ANSWERS ON THEIR OWN SCREEN. In local co-op the question is a host question owned by
/// the partner's pad (and on split screen it stands in their half: <see cref="StatedMenuOwner"/>).
/// Online, whichever machine plays the partner shows it: a question for the other machine's player
/// goes over the co-op channel as STATE (<c>consent.ask=</c>, restated until answered) and the answer
/// comes back the same way (<c>consent.answer=</c>, restated a few times). A refusal is told to the
/// asker. A question nobody answers in <see cref="ConsentTimeoutSeconds"/> is a refusal.
/// A mod asks its own question (<c>Refii.ask_partner</c>, kind 0): its words are the box's, and
/// travel last on the ask message (they may hold colons). With no partner in the game, every
/// question is answered yes at once.
/// FABLE 3'S PARTNERSHIPS ask the same way, from the world map's rows (Engine_UiPropertySource):
/// a proposal (kind 3) or an offer of business partnership (kind 4), asked here rather than by a
/// script; the asker's machine records the answer (Fable2Bridge.SetPartnership).
/// </remarks>
public partial class GameUi
{
	private const string ConsentAskPrefix = "consent.ask=";
	private const string ConsentAnswerPrefix = "consent.answer=";
	private const double ConsentTimeoutSeconds = 30.0;
	private const double ConsentRestateSeconds = 0.5;
	private const int ConsentAnswerRestates = 4;

	private sealed class ConsentAsk
	{
		public uint Ticket;
		public int Kind;
		public int Asker;
		public int Detail;
		public string Text = string.Empty;
		public double Age;
		public double Restate;

		/// <summary>Asked of the peer's player over the channel (this machine runs the script).</summary>
		public bool Remote;

		/// <summary>Asked on this machine (a box up for this machine's player).</summary>
		public bool Shown;

		/// <summary>Sent from the peer: answered here, the answer restated back.</summary>
		public bool FromPeer;

		public int Answer = -1;
		public int AnswerRestates;

		/// <summary>Asked here, not by a script: the answer goes to this instead of the native side.</summary>
		public System.Action<bool>? OnAnswer;
	}

	/// <summary>Tickets of questions asked here (high bit set, apart from the native side's).</summary>
	private uint _partnerTicket;

	private readonly List<ConsentAsk> _consentAsks = new();

	/// <summary>The local player who owns the consent box up, 0 when none (local co-op routing).</summary>
	private int _consentOwner;

	/// <summary>Whether this machine plays <paramref name="player"/>'s hero (the player store's rule).</summary>
	private static bool PlaysHere(int player) =>
		!CoopSession.PeerStanding || CoopSession.LocalCoop || (CoopSession.IsClient ? player == 2 : player == 1);

	private static string ConsentQuestion(int kind, int detail, string text) => kind switch
	{
		0 when text.Length != 0 => text,
		1 => "Your partner wants to sleep " + (detail switch
		{
			1 => "for six hours",
			2 => "for twelve hours",
			3 => "for a day",
			4 => "for a week",
			_ => "a while",
		}) + ". Is that all right?",
		2 => "Your partner wants to take someone to bed. Is that all right?",
		Engine_UiPropertySource.ProposalKind => "Your partner asks you to marry them. Will you?",
		Engine_UiPropertySource.BusinessKind =>
			"Your partner offers you a business partnership: you would share every property either of you buys from now on, and its rent. Do you accept?",
		_ => "Your partner asks for your consent. Is that all right?",
	};

	private static string? PartnershipMade(int kind) => kind switch
	{
		Engine_UiPropertySource.ProposalKind => "You are married.",
		Engine_UiPropertySource.BusinessKind => "You are now business partners.",
		_ => null,
	};

	/// <summary>A partnership question for the other player, from the world map's rows.</summary>
	private void AskPartnerConsent(int kind)
	{
		int asker = CoopSession.LocalCoop ? Fable2Bridge.GetMenuOwner() : (CoopSession.IsClient ? 2 : 1);
		var ask = new ConsentAsk
		{
			Ticket = 0x80000000u | ++_partnerTicket,
			Kind = kind,
			Asker = asker,
			OnAnswer = yes =>
			{
				if (!yes || !Fable2Bridge.SetPartnership(kind == Engine_UiPropertySource.ProposalKind ? 2 : 1))
				{
					return;
				}

				// The proposer's ring is given (from their own inventory, on their own machine).
				if (kind == Engine_UiPropertySource.ProposalKind && Engine_UiPropertySource.RingOf(asker) is { } ring)
				{
					HostBridge.PostFrontEndAction(string.Create(CultureInfo.InvariantCulture,
						$"debug-gamelua:Inventory.RemoveItemOfType(Refii.entity(Refii.player_hero({asker})), \"{ring}\")"));
				}

				if (PartnershipMade(kind) is { } made)
				{
					_coopNotices.Tell(made);
				}
			},
		};
		ask.Remote = !PlaysHere(asker == 2 ? 1 : 2);
		_consentAsks.Add(ask);
		GD.Print($"[coop-consent] player {asker} asks their partner (kind {kind}){(ask.Remote ? " — over the channel" : string.Empty)}");
	}

	private static string ConsentRefused(int kind) => kind switch
	{
		1 => "Your partner doesn't want to sleep now.",
		2 => "Your partner would rather you didn't.",
		Engine_UiPropertySource.ProposalKind => "Your partner declined your proposal.",
		Engine_UiPropertySource.BusinessKind => "Your partner declined the partnership.",
		_ => "Your partner said no.",
	};

	/// <summary>Takes the scripts' questions and keeps every one moving. Called once a frame.</summary>
	private void DriveCoopConsent(double delta)
	{
		DriveCoopCrime(delta);
		Engine_UiPropertySource.AskPartner ??= AskPartnerConsent;
		Engine_UiPropertySource.Told ??= _coopNotices.Tell;
		while (Fable2Bridge.ConsentTake() is { } taken)
		{
			var ask = new ConsentAsk
			{
				Ticket = taken.Ticket, Kind = taken.Kind, Asker = taken.Asker, Detail = taken.Detail, Text = taken.Text,
			};
			int partner = taken.Asker == 2 ? 1 : 2;
			if (!CoopSession.LocalCoop && !CoopSession.PeerStanding)
			{
				// Nobody to ask: no partner objects.
				Fable2Bridge.ConsentGive(taken.Ticket, true);
				continue;
			}

			ask.Remote = !PlaysHere(partner);
			_consentAsks.Add(ask);
			GD.Print($"[coop-consent] player {taken.Asker} asks player {partner} (kind {taken.Kind}, " +
				$"detail {taken.Detail}){(ask.Remote ? " — over the channel" : string.Empty)}");
		}

		for (int i = _consentAsks.Count - 1; i >= 0; --i)
		{
			ConsentAsk ask = _consentAsks[i];
			ask.Age += delta;
			if (ask.FromPeer)
			{
				DrivePeerConsent(ask, delta);
				if (ask.Answer >= 0 && ask.AnswerRestates >= ConsentAnswerRestates)
				{
					_consentAsks.RemoveAt(i);
				}

				continue;
			}

			if (ask.Answer < 0 && ask.Age > ConsentTimeoutSeconds)
			{
				ask.Answer = 0;
				GD.Print($"[coop-consent] question {ask.Ticket} lapsed unanswered — a refusal");
			}

			if (ask.Answer >= 0)
			{
				Engine_UiHostBoxes.Withdraw(ConsentKey(ask.Ticket));
				if (_consentOwner != 0 && !Engine_UiHostBoxes.Asking(ConsentKey(ask.Ticket)))
				{
					_consentOwner = 0;
				}

				if (ask.OnAnswer is { } onAnswer)
				{
					onAnswer(ask.Answer == 1);
				}
				else
				{
					Fable2Bridge.ConsentGive(ask.Ticket, ask.Answer == 1);
				}

				if (ask.Answer == 0)
				{
					_coopNotices.Tell(ConsentRefused(ask.Kind));
				}

				_consentAsks.RemoveAt(i);
				continue;
			}

			if (ask.Remote)
			{
				ask.Restate -= delta;
				if (ask.Restate <= 0.0 && CoopSession.PeerStanding)
				{
					ask.Restate = ConsentRestateSeconds;
					HostBridge.CoopSend(string.Create(CultureInfo.InvariantCulture,
						$"{ConsentAskPrefix}{ask.Ticket}:{ask.Kind}:{ask.Asker}:{ask.Detail}:{ask.Text}"));
				}
			}
			else if (!ask.Shown && Engine_UiHostBoxes.Question is null)
			{
				ShowConsent(ask, ask.Asker == 2 ? 1 : 2);
			}
		}
	}

	/// <summary>A question from the peer's script, for this machine's player: shown once, answer restated.</summary>
	private void DrivePeerConsent(ConsentAsk ask, double delta)
	{
		if (ask.Answer < 0)
		{
			if (!ask.Shown && Engine_UiHostBoxes.Question is null)
			{
				ShowConsent(ask, ask.Asker == 2 ? 1 : 2);
			}

			return;
		}

		ask.Restate -= delta;
		if (ask.Restate <= 0.0)
		{
			ask.Restate = ConsentRestateSeconds;
			++ask.AnswerRestates;
			HostBridge.CoopSend(string.Create(CultureInfo.InvariantCulture,
				$"{ConsentAnswerPrefix}{ask.Ticket}:{ask.Answer}"));
		}
	}

	private void ShowConsent(ConsentAsk ask, int partner)
	{
		ask.Shown = true;
		_consentOwner = CoopSession.LocalCoop ? partner : 0;
		Engine_UiHostBoxes.Ask(
			ConsentQuestion(ask.Kind, ask.Detail, ask.Text),
			() => AnswerConsent(ask, true),
			() => AnswerConsent(ask, false),
			ConsentKey(ask.Ticket));
	}

	private void AnswerConsent(ConsentAsk ask, bool yes)
	{
		ask.Answer = yes ? 1 : 0;
		ask.Restate = 0.0;
		_consentOwner = 0;
		if (yes && ask.FromPeer && PartnershipMade(ask.Kind) is { } made)
		{
			_coopNotices.Tell(made);
		}
		GD.Print($"[coop-consent] question {ask.Ticket} answered {(yes ? "yes" : "no")}");
	}

	private static string ConsentKey(uint ticket) => "consent:" + ticket.ToString(CultureInfo.InvariantCulture);

	/// <summary>
	/// A consent message from the peer: its script's question for this machine's player, or this
	/// machine's script's answer. False for anything else.
	/// </summary>
	private bool TakeConsentMessage(string message)
	{
		if (TakeCrimeMessage(message))
		{
			return true;
		}

		if (message.StartsWith(ConsentAskPrefix, System.StringComparison.Ordinal))
		{
			string[] parts = message[ConsentAskPrefix.Length..].Split(':', 5);
			if (parts.Length >= 4 &&
				uint.TryParse(parts[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out uint ticket) &&
				int.TryParse(parts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out int kind) &&
				int.TryParse(parts[2], NumberStyles.Integer, CultureInfo.InvariantCulture, out int asker) &&
				int.TryParse(parts[3], NumberStyles.Integer, CultureInfo.InvariantCulture, out int detail) &&
				!_consentAsks.Exists(a => a.FromPeer && a.Ticket == ticket))
			{
				_consentAsks.Add(new ConsentAsk
				{
					Ticket = ticket, Kind = kind, Asker = asker, Detail = detail, FromPeer = true,
						Text = parts.Length == 5 ? parts[4] : string.Empty,
				});
			}

			return true;
		}

		if (message.StartsWith(ConsentAnswerPrefix, System.StringComparison.Ordinal))
		{
			string[] parts = message[ConsentAnswerPrefix.Length..].Split(':');
			if (parts.Length == 2 &&
				uint.TryParse(parts[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out uint ticket) &&
				int.TryParse(parts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out int answer) &&
				_consentAsks.Find(a => !a.FromPeer && a.Remote && a.Ticket == ticket) is { } ask &&
				ask.Answer < 0)
			{
				ask.Answer = answer == 1 ? 1 : 0;
				GD.Print($"[coop-consent] question {ticket} answered {(ask.Answer == 1 ? "yes" : "no")} by the peer");
			}

			return true;
		}

		return false;
	}

	// THE CRIME PAGE IS THE ARRESTED PLAYER'S. An arrest is the host's (the crime records, the guard and
	// its script that reads the screen's world message run there), and the screen it opens comes up on the
	// host's guest, its gold and its post already player two's for player two's crime
	// (gui_screen_stub_crime.cpp: RefiiCrimeArrested, the sender, Update's purse restatement). Online, the
	// page is the joiner's to see and answer:
	//   host    the opener states the arrested player and the guard (@CrimeScreen.Open, crime_screen_stated.cpp);
	//           for player two's, `crime.open=` goes to the joiner (restated until `crime.shown=`) and this
	//           machine reports the screen as not up (HostBridge.ScreenOnPeer) — nothing draws it or takes input
	//           for it here; the joiner's `crime.answer=` is posted as player two's choice (`Crime:2:<choice>`,
	//           the handler's own answer: the message as hero two, the exit). No `crime.shown=` within
	//           CrimeShownTimeoutSeconds, or the peer gone: the screen is this machine's again.
	//   joiner  opens its own crime screen with the host's arguments through the opener itself
	//           (`Refii._crime_screen`, crime_screen_stated.cpp: `sub_823C4128` with the host's composed charge, the
	//           GUI owner stated as the arrest states it, player two) — `GUI.DisplayCrimeScreen` takes crime KEYS one
	//           a line and composes them (Lua_GUI_DisplayCrimeScreen 0x82304F90), so the host's text went through it
	//           twice — its speaker the guard's copy here, so its page, its gold and its pad are this player's;
	//           `crime.shown=` once it opens; a row's choice goes back as `crime.answer=` (its own OnMenuEvent
	//           leaves the screen and posts nothing on a client).
	// Local co-op is untouched: the arrest's owner statement already gives player two's pad the screen.
	private const string CrimeOpenPrefix = "crime.open=";
	private const string CrimeShownPrefix = "crime.shown=";
	private const string CrimeAnswerPrefix = "crime.answer=";
	private const double CrimeShownTimeoutSeconds = 5.0;
	private const double CrimeRestateSeconds = 0.5;
	private const int CrimeMessageRestates = 4;

	/// <summary>The crime screen's Godot key (screen 21; 20 on the disc build natively).</summary>
	private const uint CrimeScreenKey = 21u;

	private sealed class CrimeRelay
	{
		public uint Ticket;
		public string Message = string.Empty;
		public double Age;
		public double Restate;
		public bool Shown;
		public int LastAnswer;
		public double Gone;
	}

	/// <summary>Host: the arrest of player two the joiner's screen answers.</summary>
	private CrimeRelay? _crimeRelay;

	private uint _crimeTicket;

	/// <summary>Joiner: the last host arrest this machine opened, and the one waiting for its screen.</summary>
	private uint _crimeOpenedTicket;

	private uint _crimePendingTicket;

	/// <summary>Joiner: a message for the host restated a few times (`crime.shown=` / `crime.answer=`).</summary>
	private readonly List<(string Message, int Left)> _crimeOut = new();

	private double _crimeRestate;

	private int _crimeAnswerSeq;

	private double _crimeRelayedGone;

	private bool _crimeHooked;

	private static bool OnlineHost => CoopSession.PeerStanding && !CoopSession.LocalCoop && !CoopSession.IsClient;

	private void DriveCoopCrime(double delta)
	{
		if (!_crimeHooked)
		{
			_crimeHooked = true;
			Engine_UiCrimePage.Opened += CrimeOpened;
			Engine_UiCrimePage.Answered += CrimeAnswered;
		}

		if (_crimeRelay is { } relay)
		{
			uint? up = HostBridge.StatedScreenRaw();
			relay.Gone = up == CrimeScreenKey ? 0.0 : relay.Gone + delta;
			relay.Age += delta;
			if (!CoopSession.PeerStanding || relay.Gone > CrimeRestateSeconds ||
				(!relay.Shown && relay.Age > CrimeShownTimeoutSeconds))
			{
				GD.Print($"[coop-crime] arrest {relay.Ticket}: " + (relay.Gone > CrimeRestateSeconds
					? "the screen left"
					: !CoopSession.PeerStanding
						? "the joiner left — the page is this machine's"
						: "the joiner did not open it — the page is this machine's"));
				HostBridge.ScreenOnPeer = 0u;
				_crimeRelay = null;
			}
			else if (!relay.Shown)
			{
				relay.Restate -= delta;
				if (relay.Restate <= 0.0)
				{
					relay.Restate = CrimeRestateSeconds;
					HostBridge.CoopSend(relay.Message);
				}
			}
		}

		// The joiner's relayed screen is done when its own crime screen leaves.
		if (Engine_UiCrimePage.RelayedTicket != 0u)
		{
			_crimeRelayedGone = HostBridge.StatedScreenRaw() == CrimeScreenKey ? 0.0 : _crimeRelayedGone + delta;
			if (_crimeRelayedGone > 1.0)
			{
				Engine_UiCrimePage.RelayedTicket = 0u;
				_crimeRelayedGone = 0.0;
			}
		}

		_crimeRestate -= delta;
		if (_crimeOut.Count != 0 && _crimeRestate <= 0.0 && CoopSession.PeerStanding)
		{
			_crimeRestate = CrimeRestateSeconds;
			for (int i = _crimeOut.Count - 1; i >= 0; --i)
			{
				HostBridge.CoopSend(_crimeOut[i].Message);
				_crimeOut[i] = (_crimeOut[i].Message, _crimeOut[i].Left - 1);
				if (_crimeOut[i].Left <= 0)
				{
					_crimeOut.RemoveAt(i);
				}
			}
		}
	}

	/// <summary>The crime screen's opener stated a screen (Engine_UiCrimePage.Opened).</summary>
	private void CrimeOpened()
	{
		Refii.Host.HostBridge.CrimeScreen crime = Engine_UiCrimePage.Stated;
		if (OnlineHost && Engine_UiCrimePage.Arrested == 2 && Engine_UiCrimePage.GuardUid.Length != 0)
		{
			uint ticket = ++_crimeTicket;
			_crimeRelay = new CrimeRelay
			{
				Ticket = ticket,
				Message = string.Create(CultureInfo.InvariantCulture,
						$"{CrimeOpenPrefix}{ticket}:{crime.Fine}:{(crime.CanPayFine ? 1 : 0)}:{(crime.CanServe ? 1 : 0)}:") +
					$"{Base64(crime.Charge)}:{Base64(crime.Service)}:{Engine_UiCrimePage.GuardUid}",
			};
			HostBridge.ScreenOnPeer = CrimeScreenKey;
			GD.Print($"[coop-crime] arrest {ticket}: player two's — the crime page is the joiner's (guard {Engine_UiCrimePage.GuardUid})");
			return;
		}

		if (CoopSession.IsClient && _crimePendingTicket != 0u)
		{
			Engine_UiCrimePage.RelayedTicket = _crimePendingTicket;
			_crimeRelayedGone = 0.0;
			_crimeOut.Add((string.Create(CultureInfo.InvariantCulture, $"{CrimeShownPrefix}{_crimePendingTicket}"),
				CrimeMessageRestates));
			_crimeRestate = 0.0;
			GD.Print($"[coop-crime] the host's arrest {_crimePendingTicket} is up here");
			_crimePendingTicket = 0u;
		}
	}

	/// <summary>Joiner: a row's choice on the relayed screen (Engine_UiCrimePage.Answered).</summary>
	private void CrimeAnswered(string choice)
	{
		uint ticket = Engine_UiCrimePage.RelayedTicket;
		int seq = ++_crimeAnswerSeq;
		_crimeOut.Add((string.Create(CultureInfo.InvariantCulture, $"{CrimeAnswerPrefix}{ticket}:{seq}:{choice}"),
			CrimeMessageRestates));
		_crimeRestate = 0.0;
		GD.Print($"[coop-crime] arrest {ticket}: '{choice}' goes to the host");
	}

	private bool TakeCrimeMessage(string message)
	{
		if (message.StartsWith(CrimeOpenPrefix, System.StringComparison.Ordinal))
		{
			string[] parts = message[CrimeOpenPrefix.Length..].Split(':');
			if (CoopSession.IsClient && parts.Length == 7 &&
				uint.TryParse(parts[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out uint ticket) &&
				int.TryParse(parts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out int fine) &&
				ticket != _crimeOpenedTicket && Unbase64(parts[4]) is { } charge && Unbase64(parts[5]) is { } service)
			{
				_crimeOpenedTicket = ticket;
				_crimePendingTicket = ticket;
				string guard = LuaQuote(parts[6]);
				string amount = fine.ToString(CultureInfo.InvariantCulture);
				HostBridge.PostFrontEndAction(
					$"debug-gamelua:local g = Refii._by_uid({guard}) if g ~= nil then if not Refii._crime_screen(" +
					$"{LuaQuote(charge)}, {amount}, {(parts[2] == "1" ? "true" : "false")}, {(parts[3] == "1" ? "true" : "false")}, " +
					$"{LuaQuote(service)}, g) then Refii.log(\"the host's arrest did not open here\") end " +
					$"else Refii.log(\"the host's arrest: its guard \" .. {guard} .. \" is not here; the host shows it\") end");
				GD.Print($"[coop-crime] the host's arrest {ticket} opens here (fine {fine}, guard {parts[6]})");
			}

			return true;
		}

		if (message.StartsWith(CrimeShownPrefix, System.StringComparison.Ordinal))
		{
			if (_crimeRelay is { Shown: false } relay &&
				uint.TryParse(message[CrimeShownPrefix.Length..], NumberStyles.Integer, CultureInfo.InvariantCulture,
					out uint ticket) && ticket == relay.Ticket)
			{
				relay.Shown = true;
				GD.Print($"[coop-crime] arrest {ticket}: the joiner's page is up");
			}

			return true;
		}

		if (message.StartsWith(CrimeAnswerPrefix, System.StringComparison.Ordinal))
		{
			string[] parts = message[CrimeAnswerPrefix.Length..].Split(':');
			if (_crimeRelay is { } relay && parts.Length == 3 &&
				uint.TryParse(parts[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out uint ticket) &&
				int.TryParse(parts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out int seq) &&
				ticket == relay.Ticket && seq > relay.LastAnswer && parts[2] is "pay" or "resist" or "service")
			{
				relay.LastAnswer = seq;
				HostBridge.PostFrontEndAction("Crime:2:" + parts[2]);
				GD.Print($"[coop-crime] arrest {ticket}: the joiner chose '{parts[2]}' — answered for player two");
			}

			return true;
		}

		return false;
	}

	private static string Base64(string text) =>
		System.Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(text));

	private static string? Unbase64(string text)
	{
		try
		{
			return System.Text.Encoding.UTF8.GetString(System.Convert.FromBase64String(text));
		}
		catch (System.FormatException)
		{
			return null;
		}
	}

	/// <summary>A Lua string literal of <paramref name="text"/>: every byte but letters, digits and the space as `\ddd`.</summary>
	private static string LuaQuote(string text)
	{
		var quoted = new System.Text.StringBuilder("\"");
		foreach (byte b in System.Text.Encoding.UTF8.GetBytes(text))
		{
			if (b is >= (byte)'a' and <= (byte)'z' or >= (byte)'A' and <= (byte)'Z' or >= (byte)'0' and <= (byte)'9' or (byte)' ')
			{
				quoted.Append((char)b);
			}
			else
			{
				quoted.Append('\\').Append(((int)b).ToString("D3", CultureInfo.InvariantCulture));
			}
		}

		return quoted.Append('"').ToString();
	}
}
