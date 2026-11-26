using System;
using System.Collections.Generic;

namespace Refii.Text;

/// <summary>
/// OUR OWN WORDS, in every language the games ship. A row, notice or page of Refii's that the game's text
/// banks have no line for names a <c>REFII_*</c> key; the key is looked up here AFTER the running book, so a
/// line the game has always wins and none of the game's keys is ever restated (no row here starts with the
/// game's <c>GUI_</c>). A culture this table does not state reads en-uk, the game's own fallback
/// (<c>cultures.txt</c>: every row falls back to en-uk); a key it does not know is drawn as written.
/// </summary>
/// <remarks>
/// <para>Two tables, because the titles word things apart: <see cref="Shared"/> holds the shell's words
/// (the owner tag, the page counter, the host book's rows, the PC settings pages' prompts), each title its
/// own (<see cref="Fable2"/>, <see cref="Fable3"/>), stated on its <see cref="Refii.Core.Engine_Title"/>.
/// The title's table is asked first.</para>
/// <para>The cultures are the games' language folders across the pressings: en-uk, fr-fr, de-de, it-it,
/// es-es, ru-ru, pl-pl, cs-cz, sk-sk, hu-hu, ja-jp, ko-kr, zh-cht. Both titles' sk-sk books are the cs-cz
/// text word for word (Fable 3's file is byte-identical; Fable 2's strings are), so sk-sk says the Czech.
/// The wording follows each language's own book: "Hrdina" / "Bohater" / "Hős" / "영웅" / "英雄" for the
/// Hero, the books' Save and Quit forms (cs "Uložit a ukončit", pl "Zapisz i zakończ", hu "Mentés és
/// kilépés", ko "저장하고 끝내기", zh "儲存並結束"), and their address (cs and hu say "ty", ko is polite,
/// zh says 您).</para>
/// <para>Pure: the culture in force is <see cref="Engine_GameCulture.Current"/> unless one is given, so the
/// rules are held by tests.</para>
/// </remarks>
public static class Engine_OwnWords
{
	/// <summary>The cultures every row states, in this order.</summary>
	public static readonly IReadOnlyList<string> Cultures = new[]
	{
		"en-uk", "fr-fr", "de-de", "it-it", "es-es", "ru-ru", "pl-pl", "cs-cz", "sk-sk", "hu-hu", "ja-jp", "ko-kr",
		"zh-cht",
	};

	/// <summary>One key's line per culture (<see cref="Cultures"/>).</summary>
	public sealed class Table
	{
		private readonly Dictionary<string, Dictionary<string, string>> _rows = new(StringComparer.Ordinal);

		/// <summary>The keys, in the order they were stated.</summary>
		public List<string> Keys { get; } = new();

		/// <summary>
		/// A key and its line in en-uk, fr-fr, de-de, it-it, es-es, ru-ru, pl-pl, cs-cz, hu-hu, ja-jp, ko-kr,
		/// zh-cht; sk-sk is the cs-cz line (the books' own practice).
		/// </summary>
		public Table Add(string key, string en, string fr, string de, string it, string es, string ru, string pl,
			string cs, string hu, string ja, string ko, string zh)
		{
			_rows[key] = new Dictionary<string, string>(StringComparer.Ordinal)
			{
				["en-uk"] = en, ["fr-fr"] = fr, ["de-de"] = de, ["it-it"] = it, ["es-es"] = es, ["ru-ru"] = ru,
				["pl-pl"] = pl, ["cs-cz"] = cs, ["sk-sk"] = cs, ["hu-hu"] = hu, ["ja-jp"] = ja, ["ko-kr"] = ko,
				["zh-cht"] = zh,
			};
			Keys.Add(key);
			return this;
		}

		/// <summary>The key's line in exactly <paramref name="culture"/>, or null.</summary>
		public string? Get(string key, string culture) =>
			_rows.TryGetValue(key, out Dictionary<string, string>? lines) && lines.TryGetValue(culture, out string? line)
				? line
				: null;

		public bool Has(string key) => _rows.ContainsKey(key);
	}

	/// <summary>
	/// <paramref name="key"/> in <paramref name="culture"/> (the one in force when null), from the active title's
	/// table then <see cref="Shared"/>; the culture's own line, else en-uk's; null for a key neither knows.
	/// </summary>
	public static string? Find(string? key, string? culture = null)
	{
		if (string.IsNullOrEmpty(key) || !key.StartsWith(KeyPrefix, StringComparison.Ordinal))
		{
			return null;
		}

		string code = CultureOf(culture ?? Engine_GameCulture.Current);
		foreach (Table table in new[] { Refii.Core.Engine_Title.Active.OwnWords, Shared })
		{
			if (table.Has(key))
			{
				return table.Get(key, code) ?? table.Get(key, Engine_GameCulture.Fallback);
			}
		}

		return null;
	}

	/// <summary><paramref name="key"/>'s line, or the key as written when no table knows it.</summary>
	public static string Say(string key) => Find(key) ?? key;

	/// <summary>The line with <c>{0}</c>, <c>{1}</c> … filled (invariant culture).</summary>
	public static string Say(string key, params object[] args) =>
		string.Format(System.Globalization.CultureInfo.InvariantCulture, Say(key), args);

	/// <summary>Every key of ours starts so; none of the game's does.</summary>
	public const string KeyPrefix = "REFII_";

	/// <summary>
	/// The table's culture for a culture code in force: a folder code it states (<c>cs-cz</c>), else the one
	/// that shares its language (<c>fr</c>, <c>de-at</c> → <c>fr-fr</c>, <c>de-de</c>; <c>cz</c>, the
	/// cultures list's own spelling, → <c>cs-cz</c>; <c>ja</c> → <c>ja-jp</c>), else en-uk.
	/// </summary>
	public static string CultureOf(string? culture)
	{
		string? code = Engine_GameCulture.Normalize(culture);
		if (code is null)
		{
			return Engine_GameCulture.Fallback;
		}

		foreach (string stated in Cultures)
		{
			if (stated == code)
			{
				return stated;
			}
		}

		if (code.StartsWith("zh", StringComparison.Ordinal))
		{
			// Only the traditional script is stated; zh-chs falls back as cultures.txt says (en-uk).
			return code is "zh-cht" or "zh-tw" or "zh-hk" ? "zh-cht" : Engine_GameCulture.Fallback;
		}

		string language = code.Length >= 2 ? code[..2] : code;
		if (language == "cz")
		{
			language = "cs";
		}

		foreach (string stated in Cultures)
		{
			if (stated.StartsWith(language + "-", StringComparison.Ordinal))
			{
				return stated;
			}
		}

		return Engine_GameCulture.Fallback;
	}

	/// <summary>The shell's words, both titles.</summary>
	public static readonly Table Shared = new Table()
		.Add("REFII_PLAYER_1", "Player 1", "Joueur 1", "Spieler 1", "Giocatore 1", "Jugador 1", "Игрок 1", "Gracz 1",
			"Hráč 1", "1-es játékos", "プレイヤー 1", "플레이어 1", "玩家 1")
		.Add("REFII_PLAYER_2", "Player 2", "Joueur 2", "Spieler 2", "Giocatore 2", "Jugador 2", "Игрок 2", "Gracz 2",
			"Hráč 2", "2-es játékos", "プレイヤー 2", "플레이어 2", "玩家 2")
		.Add("REFII_PAGE_OF", "Page {0} / {1}", "Page {0} / {1}", "Seite {0} / {1}", "Pagina {0} / {1}",
			"Página {0} / {1}", "Страница {0} / {1}", "Strona {0} / {1}", "Strana {0} / {1}", "{0}. / {1} oldal",
			"ページ {0} / {1}", "페이지 {0} / {1}", "第 {0} / {1} 頁")
		.Add("REFII_QUIT_TO_DESKTOP", "Quit to Desktop", "Quitter vers le bureau", "Zum Desktop beenden",
			"Esci al desktop", "Salir al escritorio", "Выйти на рабочий стол", "Wyjdź do pulpitu", "Ukončit na plochu",
			"Kilépés az asztalra", "デスクトップに戻る", "바탕 화면으로 끝내기", "結束並返回桌面")
		.Add("REFII_ACHIEVEMENTS", "Achievements", "Succès", "Erfolge", "Obiettivi", "Logros", "Достижения",
			"Osiągnięcia", "Úspěchy", "Teljesítmények", "実績", "도전 과제", "成就")
		.Add("REFII_MULTIPLAYER", "Multiplayer", "Multijoueur", "Mehrspieler", "Multigiocatore", "Multijugador",
			"Сетевая игра", "Gra wieloosobowa", "Hra více hráčů", "Többjátékos mód", "マルチプレイ", "멀티플레이어",
			"多人遊戲")
		.Add("REFII_MODS", "Mods", "Mods", "Mods", "Mod", "Mods", "Моды", "Mody", "Módy", "Modok", "MOD", "모드", "模組")
		.Add("REFII_ACTIVE_MODS", "Active Mods", "Mods actifs", "Aktive Mods", "Mod attive", "Mods activos",
			"Активные моды", "Aktywne mody", "Aktivní módy", "Aktív modok", "有効な MOD", "활성 모드", "啟用的模組")
		.Add("REFII_CONTINUE", "Continue", "Continuer", "Weiter", "Continua", "Continuar", "Продолжить", "Kontynuuj",
			"Pokračovat", "Folytatás", "続ける", "계속하기", "繼續")
		.Add("REFII_BACK", "Back", "Retour", "Zurück", "Indietro", "Atrás", "Назад", "Wstecz", "Zpět", "Vissza", "戻る",
			"뒤로", "返回")
		.Add("REFII_OPEN", "Open", "Ouvrir", "Öffnen", "Apri", "Abrir", "Открыть", "Otwórz", "Otevřít", "Megnyitás",
			"開く", "열기", "開啟")
		.Add("REFII_SAVE", "Save", "Sauvegarder", "Speichern", "Salva", "Guardar", "Сохранить", "Zapisz", "Uložit",
			"Mentés", "セーブ", "저장", "儲存")
		.Add("REFII_PRESS_AN_INPUT", "Press an input…", "Appuyez sur une touche…", "Taste drücken…",
			"Premi un tasto…", "Pulsa una tecla…", "Нажмите клавишу или кнопку…", "Naciśnij klawisz lub przycisk…",
			"Stiskni klávesu nebo tlačítko…", "Nyomj meg egy gombot…", "キーかボタンを押してください…",
			"키나 버튼을 누르십시오…", "請按下按鍵或按鈕…")
		.Add("REFII_MODS_IN_SAVE", "Mods in this save", "Mods de cette sauvegarde", "Mods in diesem Spielstand",
			"Mod in questo salvataggio", "Mods de esta partida guardada", "Моды в этом сохранении", "Mody w tym zapisie",
			"Módy v této uložené pozici", "Modok ebben a mentésben", "このセーブデータの MOD", "이 저장 파일의 모드",
			"此存檔的模組")
		.Add("REFII_MODS_SAVE_MADE_WITH", "This save was made with these mods:",
			"Cette sauvegarde a été créée avec ces mods :", "Dieser Spielstand wurde mit diesen Mods erstellt:",
			"Questo salvataggio è stato creato con queste mod:", "Esta partida se guardó con estos mods:",
			"Это сохранение сделано с этими модами:", "Ten zapis utworzono z tymi modami:",
			"Tato pozice byla uložena s těmito módy:", "Ez a mentés ezekkel a modokkal készült:",
			"このセーブデータは次の MOD で作成されました:", "이 저장 파일은 다음 모드로 만들어졌습니다:",
			"此存檔是以下列模組建立的：")
		.Add("REFII_MODS_NOT_ENABLED", " — not enabled, its items are left out",
			" — non activé, ses objets sont laissés de côté", " — nicht aktiviert, seine Gegenstände werden ausgelassen",
			" — non attiva, i suoi oggetti vengono esclusi", " — no activado, sus objetos se omiten",
			" — не включён, его предметы не загружаются", " — niewłączony, jego przedmioty są pomijane",
			" — není zapnutý, jeho předměty se vynechají", " — nincs bekapcsolva, a tárgyai kimaradnak",
			" — 無効: この MOD のアイテムは除外されます", " — 사용 안 함: 이 모드의 아이템은 제외됩니다",
			" — 未啟用，其物品將被略過")
		.Add("REFII_MODS_KEPT_FOR_LATER",
			"What a mod not enabled left in the save is kept with it and comes back when that mod is enabled again.",
			"Ce qu'un mod non activé a laissé dans la sauvegarde y est conservé et revient quand ce mod est de nouveau activé.",
			"Was ein nicht aktivierter Mod im Spielstand hinterlassen hat, bleibt darin erhalten und kehrt zurück, sobald der Mod wieder aktiviert wird.",
			"Ciò che una mod non attiva ha lasciato nel salvataggio viene conservato e ritorna quando la mod viene riattivata.",
			"Lo que un mod no activado dejó en la partida se conserva en ella y vuelve cuando ese mod se activa de nuevo.",
			"То, что невключённый мод оставил в сохранении, хранится в нём и вернётся, когда мод снова будет включён.",
			"To, co niewłączony mod zostawił w zapisie, jest w nim przechowywane i wróci po ponownym włączeniu tego moda.",
			"Co nezapnutý mód zanechal v uložené pozici, v ní zůstává a vrátí se, až ten mód znovu zapneš.",
			"Amit egy be nem kapcsolt mod a mentésben hagyott, az megmarad, és visszatér, amikor újra bekapcsolod azt a modot.",
			"無効な MOD がセーブデータに残したものは保持され、その MOD を再び有効にすると戻ります。",
			"사용하지 않는 모드가 저장 파일에 남긴 것은 보존되며, 그 모드를 다시 사용하면 되돌아옵니다.",
			"未啟用的模組在存檔中留下的內容會被保留，再次啟用該模組時即會恢復。")
		.Add("REFII_MP_FRIENDS", "Friends", "Amis", "Freunde", "Amici", "Amigos", "Друзья", "Znajomi", "Přátelé",
			"Barátok", "フレンド", "친구", "好友")
		.Add("REFII_MP_FRIEND_REQUESTS", "Friend Requests", "Demandes d'ami", "Freundschaftsanfragen",
			"Richieste di amicizia", "Solicitudes de amistad", "Запросы в друзья", "Prośby o dodanie do znajomych",
			"Žádosti o přátelství", "Barátkérelmek", "フレンドリクエスト", "친구 요청", "好友請求")
		.Add("REFII_MP_INVITATIONS", "Invitations", "Invitations", "Einladungen", "Inviti", "Invitaciones",
			"Приглашения", "Zaproszenia", "Pozvánky", "Meghívások", "招待", "초대", "邀請")
		.Add("REFII_MP_GAMES", "Games", "Parties", "Spiele", "Partite", "Partidas", "Игры", "Gry", "Hry", "Játékok",
			"ゲーム", "게임", "遊戲")
		.Add("REFII_MP_NO_FRIENDS", "No Friends", "Aucun ami", "Keine Freunde", "Nessun amico", "Sin amigos",
			"Нет друзей", "Brak znajomych", "Žádní přátelé", "Nincsenek barátok", "フレンドはいません", "친구 없음",
			"沒有好友")
		.Add("REFII_MP_NO_REQUESTS", "No Requests", "Aucune demande", "Keine Anfragen", "Nessuna richiesta",
			"Sin solicitudes", "Нет запросов", "Brak próśb", "Žádné žádosti", "Nincsenek kérelmek",
			"リクエストはありません", "요청 없음", "沒有請求")
		.Add("REFII_MP_NO_INVITATIONS", "No Invitations", "Aucune invitation", "Keine Einladungen", "Nessun invito",
			"Sin invitaciones", "Нет приглашений", "Brak zaproszeń", "Žádné pozvánky", "Nincsenek meghívások",
			"招待はありません", "초대 없음", "沒有邀請")
		.Add("REFII_MP_NO_GAMES", "No Games", "Aucune partie", "Keine Spiele", "Nessuna partita", "Sin partidas",
			"Нет игр", "Brak gier", "Žádné hry", "Nincsenek játékok", "ゲームはありません", "게임 없음", "沒有遊戲");

	/// <summary>Fable 2's own words: its PC rows, its co-op notices and mode row, its save limit.</summary>
	public static readonly Table Fable2 = new Table()
		.Add("REFII_F2_SAVE_QUIT_DESKTOP", "Save and Quit to Desktop", "Sauvegarder et quitter vers le bureau",
			"Speichern und zum Desktop beenden", "Salva ed esci al desktop", "Guardar y salir al escritorio",
			"Сохранить и выйти на рабочий стол", "Zapisz i wyjdź do pulpitu", "Uložit a ukončit na plochu",
			"Mentés és kilépés az asztalra", "セーブしてデスクトップに戻る", "저장하고 바탕 화면으로 끝내기",
			"儲存並結束返回桌面")
		.Add("REFII_F2_QUIT_DESKTOP_NO_SAVE", "Quit to Desktop and Don't Save",
			"Quitter vers le bureau sans sauvegarder", "Zum Desktop beenden, ohne zu speichern",
			"Esci al desktop senza salvare", "Salir al escritorio sin guardar", "Выйти на рабочий стол без сохранения",
			"Wyjdź do pulpitu i nie zapisuj", "Ukončit na plochu bez uložení", "Kilépés az asztalra mentés nélkül",
			"セーブせずにデスクトップに戻る", "저장하지 않고 바탕 화면으로 끝내기", "結束返回桌面且不儲存")
		.Add("REFII_F2_QUIT_GAME", "Quit Game", "Quitter le jeu", "Spiel beenden", "Esci dal gioco", "Salir del juego",
			"Выйти из игры", "Wyjdź z gry", "Ukončit hru", "Kilépés a játékból", "ゲームを終了", "게임 끝내기", "結束遊戲")
		.Add("REFII_F2_SAVE_QUIT_GAME", "Save and Quit Game", "Sauvegarder et quitter le jeu",
			"Speichern und Spiel beenden", "Salva ed esci dal gioco", "Guardar y salir del juego",
			"Сохранить и выйти из игры", "Zapisz i wyjdź z gry", "Uložit a ukončit hru", "Mentés és kilépés a játékból",
			"セーブしてゲームを終了", "저장하고 게임 끝내기", "儲存並結束遊戲")
		.Add("REFII_F2_QUIT_GAME_NO_SAVE", "Quit Game and Don't Save", "Quitter le jeu sans sauvegarder",
			"Spiel beenden, ohne zu speichern", "Esci dal gioco senza salvare", "Salir del juego sin guardar",
			"Выйти из игры без сохранения", "Wyjdź z gry i nie zapisuj", "Ukončit hru bez uložení",
			"Kilépés a játékból mentés nélkül", "セーブせずにゲームを終了", "저장하지 않고 게임 끝내기", "結束遊戲且不儲存")
		.Add("REFII_F2_KICK_CONFIRM", "Kick the other Hero out of your game (their own Hero is saved first)",
			"Expulser l'autre Héros de votre partie (son Héros est d'abord sauvegardé)",
			"Den anderen Helden aus deinem Spiel werfen (sein Held wird vorher gespeichert)",
			"Espelli l'altro Eroe dalla tua partita (prima viene salvato il suo Eroe)",
			"Expulsar al otro Héroe de tu partida (antes se guarda su Héroe)",
			"Выгнать другого Героя из вашей игры (сначала его Герой будет сохранён)",
			"Wyrzuć drugiego Bohatera z gry (najpierw zostanie zapisany jego Bohater)",
			"Vyhodit druhého Hrdinu ze hry (jeho Hrdina se nejdřív uloží)",
			"A másik Hős kidobása a játékodból (előbb az ő Hőse mentésre kerül)",
			"もう一人のヒーローをゲームから追い出す (先にそのヒーローがセーブされます)",
			"다른 영웅을 게임에서 내보냅니다 (그 영웅은 먼저 저장됩니다)",
			"將另一位英雄踢出您的遊戲 (會先儲存該英雄)")
		.Add("REFII_F2_MP_MY_GAME", "My game", "Ma partie", "Mein Spiel", "La mia partita", "Mi partida", "Моя игра",
			"Moja gra", "Moje hra", "Saját játék", "自分のゲーム", "내 게임", "我的遊戲")
		.Add("REFII_F2_MP_ADD_FRIEND", "Add a friend (paste code)", "Ajouter un ami (coller le code)",
			"Freund hinzufügen (Code einfügen)", "Aggiungi un amico (incolla il codice)",
			"Añadir un amigo (pegar código)", "Добавить друга (вставить код)", "Dodaj znajomego (wklej kod)",
			"Přidat přítele (vložit kód)", "Barát hozzáadása (kód beillesztése)", "フレンドを追加 (コードを貼り付け)",
			"친구 추가 (코드 붙여넣기)", "新增好友 (貼上代碼)")
		.Add("REFII_F2_MP_MY_CODE", "My friend code", "Mon code ami", "Mein Freundescode", "Il mio codice amico",
			"Mi código de amigo", "Мой код друга", "Mój kod znajomego", "Můj kód přítele", "Saját barátkód",
			"自分のフレンドコード", "내 친구 코드", "我的好友代碼")
		.Add("REFII_F2_DYNAMIC_SKY", "Dynamic Sky and Weather", "Ciel et météo dynamiques",
			"Dynamischer Himmel und Wetter", "Cielo e meteo dinamici", "Cielo y clima dinámicos",
			"Динамическое небо и погода", "Dynamiczne niebo i pogoda", "Dynamická obloha a počasí",
			"Dinamikus égbolt és időjárás", "ダイナミックな空と天候", "동적 하늘과 날씨", "動態天空與天氣")
		.Add("REFII_F2_COOP_LOCAL_JOINED", "Player Two has joined the game.", "Le joueur 2 a rejoint la partie.",
			"Spieler 2 ist dem Spiel beigetreten.", "Il giocatore 2 si è unito alla partita.",
			"El jugador 2 se ha unido a la partida.", "Игрок 2 присоединился к игре.", "Gracz 2 dołączył do gry.",
			"Hráč 2 se připojil ke hře.", "A 2-es játékos csatlakozott a játékhoz.", "プレイヤー 2 がゲームに参加しました。",
			"플레이어 2가 게임에 참가했습니다.", "玩家 2 已加入遊戲。")
		.Add("REFII_F2_COOP_LOCAL_LEFT", "Player Two has left the game.", "Le joueur 2 a quitté la partie.",
			"Spieler 2 hat das Spiel verlassen.", "Il giocatore 2 ha lasciato la partita.",
			"El jugador 2 ha abandonado la partida.", "Игрок 2 покинул игру.", "Gracz 2 opuścił grę.",
			"Hráč 2 opustil hru.", "A 2-es játékos kilépett a játékból.", "プレイヤー 2 がゲームから抜けました。",
			"플레이어 2가 게임에서 나갔습니다.", "玩家 2 已離開遊戲。")
		.Add("REFII_F2_COOP_PROGRESSION", "Each Hero keeps their own gold and experience.",
			"Chaque Héros garde son or et son expérience.", "Jeder Held behält sein eigenes Gold und seine Erfahrung.",
			"Ogni Eroe tiene il proprio oro e la propria esperienza.", "Cada Héroe conserva su oro y su experiencia.",
			"Каждый Герой сохраняет своё золото и опыт.", "Każdy Bohater zachowuje własne złoto i doświadczenie.",
			"Každý Hrdina si ponechá své zlato a zkušenosti.", "Minden Hős megtartja a saját aranyát és tapasztalatát.",
			"ヒーローはそれぞれ自分のゴールドと経験値を持ち続けます。", "각 영웅은 자신의 골드와 경험치를 그대로 유지합니다.",
			"每位英雄都保有自己的金錢與經驗值。")
		.Add("REFII_F2_COOP_PRESS_START", "Press START on a second controller to join.",
			"Appuyez sur START sur une deuxième manette pour rejoindre la partie.",
			"Drücke START auf einem zweiten Controller, um beizutreten.",
			"Premi START su un secondo controller per unirti.", "Pulsa START en un segundo mando para unirte.",
			"Нажмите START на втором контроллере, чтобы присоединиться.", "Naciśnij START na drugim kontrolerze, aby dołączyć.",
			"Stiskni START na druhém ovladači a připoj se.", "Nyomd meg a START gombot egy második kontrolleren a csatlakozáshoz.",
			"2 台目のコントローラーで START を押すと参加できます。", "두 번째 컨트롤러의 START 버튼을 누르면 참가합니다.",
			"在第二個控制器上按 START 即可加入。")
		.Add("REFII_F2_COOP_HOST_JOINED", "{0} has joined your game.", "{0} a rejoint votre partie.",
			"{0} ist deinem Spiel beigetreten.", "{0} si è unito alla tua partita.", "{0} se ha unido a tu partida.",
			"{0} присоединяется к вашей игре.", "{0} dołącza do twojej gry.", "{0} se připojil k tvé hře.",
			"{0} csatlakozott a játékodhoz.", "{0} があなたのゲームに参加しました。", "{0} 님이 게임에 참가했습니다.",
			"{0} 已加入您的遊戲。")
		.Add("REFII_F2_COOP_PARTNER_JOINED", "Your co-op partner has joined your game.",
			"Votre partenaire de coopération a rejoint votre partie.", "Dein Koop-Partner ist deinem Spiel beigetreten.",
			"Il tuo compagno di cooperativa si è unito alla tua partita.", "Tu compañero cooperativo se ha unido a tu partida.",
			"Ваш напарник по совместной игре присоединился к игре.", "Partner w trybie współpracy dołączył do twojej gry.",
			"Tvůj spolupracující partner se připojil k tvé hře.", "A kooperatív játékostársad csatlakozott a játékodhoz.",
			"協力プレイのパートナーがあなたのゲームに参加しました。", "협동 게임 파트너가 게임에 참가했습니다.",
			"您的合作伙伴已加入您的遊戲。")
		.Add("REFII_F2_COOP_JOINED_HOST", "Successfully connected to {0}'s game!", "Connecté à la partie de {0} !",
			"Erfolgreich mit dem Spiel von {0} verbunden!", "Connesso alla partita di {0}!",
			"¡Conectado a la partida de {0}!", "Подключение к игре {0} выполнено!", "Połączono z grą gracza {0}!",
			"Připojení ke hře hráče {0} proběhlo úspěšně!", "Sikeresen csatlakoztál {0} játékához!",
			"{0} のゲームに接続しました！", "{0} 님의 게임에 연결되었습니다!", "已成功連線至 {0} 的遊戲！")
		.Add("REFII_F2_COOP_JOINED_PARTNER", "Successfully connected to your partner's game!",
			"Connecté à la partie de votre partenaire !", "Erfolgreich mit dem Spiel deines Partners verbunden!",
			"Connesso alla partita del tuo compagno!", "¡Conectado a la partida de tu compañero!",
			"Подключение к игре напарника выполнено!", "Połączono z grą partnera!",
			"Připojení ke hře partnera proběhlo úspěšně!", "Sikeresen csatlakoztál a társad játékához!",
			"パートナーのゲームに接続しました！", "파트너의 게임에 연결되었습니다!", "已成功連線至合作伙伴的遊戲！")
		.Add("REFII_F2_COOP_MODE_SHARED", "Shared Screen", "Écran partagé", "Geteilter Bildschirm", "Schermo condiviso",
			"Pantalla compartida", "Общий экран", "Wspólny ekran", "Sdílená obrazovka", "Közös képernyő", "共有画面",
			"공유 화면", "共用畫面")
		.Add("REFII_F2_COOP_MODE_SIDE", "Split Screen: Side by Side", "Écran scindé : côte à côte",
			"Geteilter Bildschirm: nebeneinander", "Schermo diviso: affiancato", "Pantalla dividida: lado a lado",
			"Разделённый экран: рядом", "Podzielony ekran: obok siebie", "Rozdělená obrazovka: vedle sebe",
			"Osztott képernyő: egymás mellett", "画面分割: 左右", "화면 분할: 좌우", "分割畫面：左右")
		.Add("REFII_F2_COOP_MODE_STACKED", "Split Screen: Top and Bottom", "Écran scindé : haut et bas",
			"Geteilter Bildschirm: oben und unten", "Schermo diviso: sopra e sotto", "Pantalla dividida: arriba y abajo",
			"Разделённый экран: сверху и снизу", "Podzielony ekran: góra i dół", "Rozdělená obrazovka: nahoře a dole",
			"Osztott képernyő: alul és felül", "画面分割: 上下", "화면 분할: 위아래", "分割畫面：上下")
		.Add("REFII_F2_COOP_MODE_PENDING", "Takes effect when player two next joins",
			"Prend effet la prochaine fois que le joueur 2 rejoint la partie",
			"Gilt, sobald Spieler 2 das nächste Mal beitritt", "Ha effetto al prossimo ingresso del giocatore 2",
			"Se aplica la próxima vez que se una el jugador 2", "Вступит в силу при следующем входе игрока 2",
			"Zadziała, gdy gracz 2 dołączy następnym razem", "Projeví se, až se hráč 2 příště připojí",
			"Akkor lép életbe, amikor a 2-es játékos legközelebb csatlakozik",
			"プレイヤー 2 が次に参加したときに有効になります", "플레이어 2가 다음에 참가할 때 적용됩니다",
			"玩家 2 下次加入時生效")
		.Add("REFII_F2_SAVES_REPLACE_A_SAVE", "Replace a save", "Remplacer une sauvegarde", "Einen Spielstand ersetzen",
			"Sostituisci un salvataggio", "Sustituir una partida guardada", "Заменить сохранение", "Zastąp zapis",
			"Nahradit uloženou hru", "Mentés lecserélése", "セーブデータを置き換える", "저장 게임 교체", "取代存檔")
		.Add("REFII_F2_SAVES_REPLACE", "Replace", "Remplacer", "Ersetzen", "Sostituisci", "Sustituir", "Заменить",
			"Zastąp", "Nahradit", "Lecserélés", "置き換える", "교체", "取代")
		.Add("REFII_F2_SAVES_CHOOSE_ONE", "Choose a save to replace", "Choisissez une sauvegarde à remplacer",
			"Wähle einen Spielstand zum Ersetzen", "Scegli un salvataggio da sostituire",
			"Elige una partida guardada para sustituirla", "Выберите сохранение для замены", "Wybierz zapis do zastąpienia",
			"Vyber uloženou hru, kterou chceš nahradit", "Válaszd ki a lecserélendő mentést",
			"置き換えるセーブデータを選んでください", "교체할 저장 게임을 선택하십시오", "選擇要取代的存檔")
		.Add("REFII_F2_SAVES_CHOOSE_MANY", "Choose {0} saves to replace", "Choisissez {0} sauvegardes à remplacer",
			"Wähle {0} Spielstände zum Ersetzen", "Scegli {0} salvataggi da sostituire",
			"Elige {0} partidas guardadas para sustituirlas", "Выберите сохранения для замены: {0}",
			"Wybierz zapisy do zastąpienia: {0}", "Vyber uložené hry k nahrazení: {0}",
			"Válaszd ki a lecserélendő mentéseket: {0}", "置き換えるセーブデータを {0} 個選んでください",
			"교체할 저장 게임을 {0}개 선택하십시오", "選擇 {0} 個要取代的存檔")
		.Add("REFII_F2_SAVES_MORE_TO_CHOOSE", "{0} more to choose", "Encore {0} à choisir", "Noch {0} auszuwählen",
			"Ancora {0} da scegliere", "Faltan {0} por elegir", "Осталось выбрать: {0}", "Pozostało do wybrania: {0}",
			"Zbývá vybrat: {0}", "Még {0} választandó", "あと {0} 個選んでください", "{0}개 더 선택하십시오",
			"還要選擇 {0} 個")
		.Add("REFII_F2_SAVES_CHOSEN", "Chosen to replace", "Choisie pour être remplacée", "Zum Ersetzen gewählt",
			"Scelto per la sostituzione", "Elegida para sustituir", "Выбрано для замены", "Wybrany do zastąpienia",
			"Vybráno k nahrazení", "Lecserélésre kiválasztva", "置き換え対象", "교체 대상으로 선택됨", "已選擇取代")
		.Add("REFII_F2_SAVES_CONFIRM_ONE", "Replace {0}? It will be kept in Older saves.",
			"Remplacer {0} ? Elle sera conservée dans Anciennes sauvegardes.",
			"{0} ersetzen? Er wird unter Ältere Spielstände aufbewahrt.",
			"Sostituire {0}? Sarà conservato in Salvataggi precedenti.",
			"¿Sustituir {0}? Se conservará en Partidas anteriores.",
			"Заменить {0}? Оно будет храниться в папке «Старые сохранения».",
			"Zastąpić {0}? Zapis zostanie zachowany w folderze Starsze zapisy.",
			"Nahradit {0}? Uloží se do složky Starší uložené hry.",
			"Lecseréled: {0}? A Régebbi mentések között megmarad.",
			"{0} を置き換えますか？「古いセーブデータ」に保管されます。",
			"{0}을(를) 교체하시겠습니까? 이전 저장 게임에 보관됩니다.",
			"要取代 {0} 嗎？它會保留在「較舊的存檔」中。")
		.Add("REFII_F2_SAVES_CONFIRM_MANY", "Replace these saves? They will be kept in Older saves.\n{0}",
			"Remplacer ces sauvegardes ? Elles seront conservées dans Anciennes sauvegardes.\n{0}",
			"Diese Spielstände ersetzen? Sie werden unter Ältere Spielstände aufbewahrt.\n{0}",
			"Sostituire questi salvataggi? Saranno conservati in Salvataggi precedenti.\n{0}",
			"¿Sustituir estas partidas guardadas? Se conservarán en Partidas anteriores.\n{0}",
			"Заменить эти сохранения? Они будут храниться в папке «Старые сохранения».\n{0}",
			"Zastąpić te zapisy? Zostaną zachowane w folderze Starsze zapisy.\n{0}",
			"Nahradit tyto uložené hry? Uloží se do složky Starší uložené hry.\n{0}",
			"Lecseréled ezeket a mentéseket? A Régebbi mentések között megmaradnak.\n{0}",
			"これらのセーブデータを置き換えますか？「古いセーブデータ」に保管されます。\n{0}",
			"이 저장 게임들을 교체하시겠습니까? 이전 저장 게임에 보관됩니다.\n{0}",
			"要取代這些存檔嗎？它們會保留在「較舊的存檔」中。\n{0}")
		.Add("REFII_F2_SAVES_SET_ASIDE_FAILED", "The save could not be set aside, so no new game was started:\n{0}",
			"La sauvegarde n'a pas pu être mise de côté ; aucune nouvelle partie n'a été lancée :\n{0}",
			"Der Spielstand konnte nicht beiseitegelegt werden, daher wurde kein neues Spiel gestartet:\n{0}",
			"Impossibile mettere da parte il salvataggio, quindi non è stata avviata una nuova partita:\n{0}",
			"No se pudo apartar la partida guardada, así que no se ha empezado una partida nueva:\n{0}",
			"Не удалось отложить сохранение, поэтому новая игра не начата:\n{0}",
			"Nie udało się odłożyć zapisu, więc nowa gra nie została rozpoczęta:\n{0}",
			"Uloženou hru nebylo možné odložit, nová hra proto nezačala:\n{0}",
			"A mentést nem sikerült félretenni, ezért nem indult új játék:\n{0}",
			"セーブデータを移動できなかったため、新しいゲームは開始されませんでした:\n{0}",
			"저장 게임을 옮길 수 없어서 새 게임을 시작하지 않았습니다:\n{0}",
			"無法移開存檔，因此未開始新遊戲：\n{0}");

	/// <summary>Fable 3's own words. Its PC settings pages say the shell's (<see cref="Shared"/>).</summary>
	public static readonly Table Fable3 = new();
}
