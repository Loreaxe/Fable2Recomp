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
		// The achievements page per hero, with the profile total and the partner's (Engine_UiAchievementPage).
		.Add("REFII_ACH_THIS_HERO", "This hero: {0} of {1} unlocked", "Ce héros : {0} sur {1} débloqués",
			"Dieser Held: {0} von {1} freigeschaltet", "Questo eroe: {0} su {1} sbloccati", "Este héroe: {0} de {1} desbloqueados",
			"Этот герой: открыто {0} из {1}", "Ten bohater: odblokowano {0} z {1}", "Tento hrdina: odemčeno {0} z {1}",
			"Ez a hős: {0} / {1} feloldva", "このヒーロー: {1}個中{0}個解除", "이 영웅: {1}개 중 {0}개 달성", "此英雄：已解鎖 {0}/{1}")
		.Add("REFII_ACH_PARTNER_HERO", "Partner's hero: {0} of {1} unlocked", "Héros du partenaire : {0} sur {1} débloqués",
			"Held des Partners: {0} von {1} freigeschaltet", "Eroe del compagno: {0} su {1} sbloccati",
			"Héroe del compañero: {0} de {1} desbloqueados", "Герой напарника: открыто {0} из {1}",
			"Bohater partnera: odblokowano {0} z {1}", "Hrdina spoluhráče: odemčeno {0} z {1}", "A társ hőse: {0} / {1} feloldva",
			"相棒のヒーロー: {1}個中{0}個解除", "동료의 영웅: {1}개 중 {0}개 달성", "夥伴的英雄：已解鎖 {0}/{1}")
		.Add("REFII_ACH_GAMERSCORE", "{0} of {1} gamerscore", "{0} sur {1} points de succès", "{0} von {1} Gamerscore",
			"{0} su {1} punti obiettivo", "{0} de {1} de puntuación de jugador", "{0} из {1} очков игрока",
			"{0} z {1} punktów gracza", "{0} z {1} bodů hráče", "{0} / {1} játékospont", "ゲーマースコア {1}中{0}",
			"게이머 점수 {1} 중 {0}", "玩家點數 {0}/{1}")
		.Add("REFII_ACH_PROFILE_TOTAL", "On this profile: {0} of {1}", "Sur ce profil : {0} sur {1}", "In diesem Profil: {0} von {1}",
			"In questo profilo: {0} su {1}", "En este perfil: {0} de {1}", "В этом профиле: {0} из {1}", "W tym profilu: {0} z {1}",
			"V tomto profilu: {0} z {1}", "Ebben a profilban: {0} / {1}", "このプロフィール: {1}中{0}", "이 프로필: {1} 중 {0}",
			"此設定檔：{0}/{1}")
		.Add("REFII_ACH_PARTNER_PROFILE", "On their profile: {0} of {1}", "Sur son profil : {0} sur {1}",
			"In seinem Profil: {0} von {1}", "Nel suo profilo: {0} su {1}", "En su perfil: {0} de {1}", "В его профиле: {0} из {1}",
			"W jego profilu: {0} z {1}", "V jeho profilu: {0} z {1}", "Az ő profiljában: {0} / {1}", "相手のプロフィール: {1}中{0}",
			"상대 프로필: {1} 중 {0}", "對方的設定檔：{0}/{1}")
		.Add("REFII_ACH_SHOW_PARTNER", "Partner's Hero", "Héros du partenaire", "Held des Partners", "Eroe del compagno",
			"Héroe del compañero", "Герой напарника", "Bohater partnera", "Hrdina spoluhráče", "A társ hőse", "相棒のヒーロー",
			"동료의 영웅", "夥伴的英雄")
		.Add("REFII_ACH_SHOW_OWN", "Your Hero", "Votre héros", "Dein Held", "Il tuo eroe", "Tu héroe", "Твой герой",
			"Twój bohater", "Tvůj hrdina", "A hősöd", "あなたのヒーロー", "당신의 영웅", "您的英雄")
		// Fable 3's achievements page and another player's card (Fable3AchievementsPage, Fable3GamerCard); the
		// tally lines above are the one wording both titles' pages use (Engine_UiAchievementPage.TallyLines).
		.Add("REFII_ACH_THEIR_HERO", "Their hero: {0} of {1} unlocked", "Son héros : {0} sur {1} débloqués",
			"Sein Held: {0} von {1} freigeschaltet", "Il suo eroe: {0} su {1} sbloccati", "Su héroe: {0} de {1} desbloqueados",
			"Его герой: открыто {0} из {1}", "Jego bohater: odblokowano {0} z {1}", "Jeho hrdina: odemčeno {0} z {1}",
			"Hőse: {0} / {1} feloldva", "相手のヒーロー: {1}個中{0}個解除", "상대 영웅: {1}개 중 {0}개 달성", "對方的英雄：已解鎖 {0}/{1}")
		.Add("REFII_ACH_UNLOCKED", "Unlocked", "Débloqué", "Freigeschaltet", "Sbloccato", "Desbloqueado", "Открыто",
			"Odblokowano", "Odemčeno", "Feloldva", "解除済み", "달성함", "已解鎖")
		.Add("REFII_ACH_UNLOCKED_ON", "Unlocked {0}", "Débloqué le {0}", "Freigeschaltet am {0}", "Sbloccato il {0}",
			"Desbloqueado el {0}", "Открыто {0}", "Odblokowano {0}", "Odemčeno {0}", "Feloldva: {0}", "{0}に解除",
			"{0}에 달성함", "於 {0} 解鎖")
		.Add("REFII_ACH_LOCKED", "Locked", "Verrouillé", "Gesperrt", "Bloccato", "Bloqueado", "Закрыто", "Zablokowane",
			"Zamčeno", "Zárolva", "未解除", "잠김", "未解鎖")
		.Add("REFII_ACH_NOT_READY", "The achievement list has not been read yet.", "La liste des succès n'a pas encore été lue.",
			"Die Erfolgsliste wurde noch nicht gelesen.", "L'elenco degli obiettivi non è ancora stato letto.",
			"La lista de logros aún no se ha leído.", "Список достижений ещё не прочитан.",
			"Lista osiągnięć nie została jeszcze wczytana.", "Seznam úspěchů ještě nebyl načten.",
			"A teljesítménylista még nincs beolvasva.", "実績リストはまだ読み込まれていません。",
			"도전 과제 목록을 아직 읽지 못했습니다.", "尚未讀取成就清單。")
		.Add("REFII_ACH_NUMBERED", "Achievement {0}", "Succès {0}", "Erfolg {0}", "Obiettivo {0}", "Logro {0}",
			"Достижение {0}", "Osiągnięcie {0}", "Úspěch {0}", "{0}. teljesítmény", "実績 {0}", "도전 과제 {0}", "成就 {0}")
		.Add("REFII_ACH_VIEW", "View Achievements", "Voir les succès", "Erfolge ansehen", "Visualizza obiettivi", "Ver logros",
			"Просмотреть достижения", "Pokaż osiągnięcia", "Zobrazit úspěchy", "Teljesítmények megtekintése", "実績を見る",
			"도전 과제 보기", "檢視成就")
		.Add("REFII_ACH_NONE_SHARED", "Their achievements have not arrived.", "Ses succès ne sont pas encore arrivés.",
			"Seine Erfolge sind noch nicht eingetroffen.", "I suoi obiettivi non sono ancora arrivati.",
			"Sus logros aún no han llegado.", "Его достижения ещё не получены.", "Jego osiągnięcia jeszcze nie dotarły.",
			"Jeho úspěchy zatím nedorazily.", "A teljesítményei még nem érkeztek meg.", "相手の実績はまだ届いていません。",
			"상대의 도전 과제가 아직 도착하지 않았습니다.", "尚未收到對方的成就。")
		// The joining player's choice of the save their hero comes from (Engine_HeroSaveChoice).
		.Add("REFII_SELECT", "Select", "Sélectionner", "Auswählen", "Seleziona", "Seleccionar", "Выбрать", "Wybierz",
			"Vybrat", "Kiválasztás", "決定", "선택", "選擇")
		.Add("REFII_CHOOSE_HERO", "Choose Your Hero", "Choisissez votre héros", "Wähle deinen Helden", "Scegli il tuo eroe",
			"Elige a tu héroe", "Выберите героя", "Wybierz bohatera", "Vyber si hrdinu", "Válaszd ki a hősödet",
			"ヒーローを選択", "영웅을 선택하십시오", "選擇您的英雄")
		.Add("REFII_CANCEL", "Cancel", "Annuler", "Abbrechen", "Annulla", "Cancelar", "Отмена", "Anuluj", "Zrušit",
			"Mégse", "キャンセル", "취소", "取消")
		.Add("REFII_NEW_HERO", "New Hero", "Nouveau héros", "Neuer Held", "Nuovo eroe", "Nuevo héroe", "Новый герой",
			"Nowy bohater", "Nový hrdina", "Új hős", "新しいヒーロー", "새 영웅", "新英雄")
		.Add("REFII_NEW_HERO_SAVED", "A new hero, with a save of their own.", "Un nouveau héros, avec sa propre sauvegarde.",
			"Ein neuer Held mit eigenem Spielstand.", "Un nuovo eroe, con un salvataggio tutto suo.",
			"Un héroe nuevo, con su propia partida guardada.", "Новый герой со своим сохранением.",
			"Nowy bohater z własnym zapisem gry.", "Nový hrdina s vlastní uloženou hrou.", "Új hős, saját mentéssel.",
			"専用のセーブデータを持つ新しいヒーロー。", "자신만의 저장 파일을 가진 새 영웅입니다.", "擁有專屬存檔的新英雄。")
		.Add("REFII_NEW_HERO_UNSAVED", "A new hero. This profile has no free save slot, so this hero is not saved.",
			"Un nouveau héros. Ce profil n'a plus d'emplacement de sauvegarde libre : ce héros ne sera pas sauvegardé.",
			"Ein neuer Held. Dieses Profil hat keinen freien Speicherplatz, daher wird dieser Held nicht gespeichert.",
			"Un nuovo eroe. Questo profilo non ha slot di salvataggio liberi: questo eroe non verrà salvato.",
			"Un héroe nuevo. Este perfil no tiene ranuras de guardado libres, así que este héroe no se guardará.",
			"Новый герой. В этом профиле нет свободных ячеек сохранения, поэтому герой не будет сохранён.",
			"Nowy bohater. Ten profil nie ma wolnego miejsca na zapis, więc ten bohater nie zostanie zapisany.",
			"Nový hrdina. Tento profil nemá volnou pozici pro uložení, takže se tento hrdina neuloží.",
			"Új hős. Ebben a profilban nincs szabad mentési hely, ezért ez a hős nem lesz elmentve.",
			"新しいヒーロー。このプロフィールには空きセーブ枠がないため、このヒーローは保存されません。",
			"새 영웅입니다. 이 프로필에는 빈 저장 공간이 없으므로 이 영웅은 저장되지 않습니다.",
			"新英雄。此設定檔沒有空的存檔欄位，因此不會儲存此英雄。")
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
		.Add("REFII_CHANGE", "Change", "Modifier", "Ändern", "Modifica", "Cambiar", "Изменить", "Zmień", "Změnit",
			"Módosítás", "変更", "변경", "變更")
		.Add("REFII_PRESS_AN_INPUT", "Press an input...", "Appuyez sur une touche...", "Taste drücken...",
			"Premi un tasto...", "Pulsa una tecla...", "Нажмите клавишу или кнопку...", "Naciśnij klawisz lub przycisk...",
			"Stiskni klávesu nebo tlačítko...", "Nyomj meg egy gombot...", "キーかボタンを押してください...",
			"키나 버튼을 누르십시오...", "請按下按鍵或按鈕...")
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
			"Нет игр", "Brak gier", "Žádné hry", "Nincsenek játékok", "ゲームはありません", "게임 없음", "沒有遊戲")
		.Add("REFII_MP_MODS_NO_FOLDER", "No mods folder found", "Dossier de mods introuvable",
			"Kein Mod-Ordner gefunden", "Cartella delle mod non trovata", "No se ha encontrado la carpeta de mods",
			"Папка модов не найдена", "Nie znaleziono folderu modów", "Složka módů nenalezena",
			"Nem található modmappa", "MOD フォルダーが見つかりません", "모드 폴더를 찾을 수 없습니다",
			"找不到模組資料夾")
		.Add("REFII_MP_MODS_UNREADABLE", "The mod load order could not be read",
			"L'ordre de chargement des mods n'a pas pu être lu", "Die Mod-Ladereihenfolge konnte nicht gelesen werden",
			"Impossibile leggere l'ordine di caricamento delle mod",
			"No se ha podido leer el orden de carga de los mods", "Не удалось прочитать порядок загрузки модов",
			"Nie udało się odczytać kolejności wczytywania modów", "Pořadí načítání módů se nepodařilo přečíst",
			"A modok betöltési sorrendjét nem sikerült beolvasni", "MOD の読み込み順を読み取れませんでした",
			"모드 불러오기 순서를 읽을 수 없습니다", "無法讀取模組載入順序")
		.Add("REFII_MP_NO_MODS", "No Mods", "Aucun mod", "Keine Mods", "Nessuna mod", "Sin mods", "Нет модов",
			"Brak modów", "Žádné módy", "Nincsenek modok", "MOD はありません", "모드 없음", "沒有模組")
		.Add("REFII_MP_STATE_IN_A_GAME", "in a game", "en jeu", "im Spiel", "in partita", "en una partida", "в игре",
			"w grze", "ve hře", "játékban", "ゲーム中", "게임 중", "遊戲中")
		.Add("REFII_MP_STATE_IN_TITLE", "in {0}", "joue à {0}", "spielt {0}", "gioca a {0}", "juega a {0}",
			"в игре {0}", "w grze {0}", "ve hře {0}", "játékban: {0}", "{0} をプレイ中", "{0} 플레이 중", "正在玩 {0}")
		.Add("REFII_MP_STATE_ONLINE", "online", "en ligne", "online", "online", "en línea", "в сети", "online",
			"online", "online", "オンライン", "온라인", "線上")
		.Add("REFII_MP_STATE_OFFLINE", "offline", "hors ligne", "offline", "offline", "sin conexión", "не в сети",
			"offline", "offline", "offline", "オフライン", "오프라인", "離線")
		.Add("REFII_MP_REQUEST_REACHED",
			"Your friend request reached {0}.\n\nThey appear under Friends once they accept.",
			"Votre demande d'ami est parvenue à {0}.\n\nCe joueur apparaîtra dans Amis dès qu'il aura accepté.",
			"Deine Freundschaftsanfrage hat {0} erreicht.\n\nNach der Annahme erscheint der Spieler unter Freunde.",
			"La tua richiesta di amicizia è arrivata a {0}.\n\nIl giocatore comparirà in Amici quando avrà accettato.",
			"Tu solicitud de amistad ha llegado a {0}.\n\nAparecerá en Amigos cuando la acepte.",
			"Ваш запрос в друзья доставлен игроку {0}.\n\nИгрок появится в разделе «Друзья», когда примет его.",
			"Twoja prośba o dodanie do znajomych dotarła do gracza {0}.\n\nGracz pojawi się w Znajomych, gdy ją przyjmie.",
			"Tvoje žádost o přátelství dorazila k hráči {0}.\n\nMezi Přáteli se objeví, až ji přijme.",
			"A barátkérelmed megérkezett: {0}.\n\nA Barátok között jelenik meg, amint elfogadja.",
			"フレンドリクエストが {0} に届きました。\n\n相手が承認するとフレンドに表示されます。",
			"친구 요청이 {0} 님에게 전달되었습니다.\n\n상대가 수락하면 친구 목록에 표시됩니다.",
			"您的好友請求已送達 {0}。\n\n對方接受後就會出現在好友中。")
		.Add("REFII_MP_INVITE_UNDELIVERED",
			"Your invitation to {0} could not be delivered.\n\nThey may be offline; invite them again.",
			"Votre invitation à {0} n'a pas pu être remise.\n\nCe joueur est peut-être hors ligne ; invitez-le de nouveau.",
			"Deine Einladung an {0} konnte nicht zugestellt werden.\n\nDer Spieler ist vielleicht offline; lade ihn erneut ein.",
			"Impossibile recapitare il tuo invito a {0}.\n\nIl giocatore potrebbe essere offline; invitalo di nuovo.",
			"No se ha podido entregar tu invitación a {0}.\n\nPuede que esté sin conexión; invítale de nuevo.",
			"Ваше приглашение игроку {0} не удалось доставить.\n\nВозможно, игрок не в сети; пригласите его ещё раз.",
			"Nie udało się dostarczyć twojego zaproszenia do gracza {0}.\n\nGracz może być offline; zaproś go ponownie.",
			"Tvoji pozvánku hráči {0} se nepodařilo doručit.\n\nMožná je offline; pozvi ho znovu.",
			"A meghívódat nem sikerült kézbesíteni: {0}.\n\nLehet, hogy offline; hívd meg újra.",
			"{0} への招待を届けられませんでした。\n\n相手がオフラインの可能性があります。もう一度招待してください。",
			"{0} 님에게 초대를 전달할 수 없습니다.\n\n상대가 오프라인일 수 있습니다. 다시 초대하십시오.",
			"無法將邀請送達 {0}。\n\n對方可能已離線；請再邀請一次。")
		.Add("REFII_MP_JOIN_UNANSWERED", "{0} did not answer the join.\n\nTry again once they are in a game.",
			"{0} n'a pas répondu à la demande de connexion.\n\nRéessayez quand ce joueur sera en jeu.",
			"{0} hat auf den Beitritt nicht geantwortet.\n\nVersuche es erneut, sobald der Spieler im Spiel ist.",
			"{0} non ha risposto alla richiesta di accesso.\n\nRiprova quando il giocatore sarà in partita.",
			"{0} no ha respondido a la solicitud de unión.\n\nInténtalo de nuevo cuando esté en una partida.",
			"{0} не отвечает на запрос о присоединении.\n\nПовторите попытку, когда игрок будет в игре.",
			"{0} nie odpowiada na prośbę o dołączenie.\n\nSpróbuj ponownie, gdy gracz będzie w grze.",
			"{0} neodpovídá na žádost o připojení.\n\nZkus to znovu, až bude ve hře.",
			"{0} nem válaszolt a csatlakozásra.\n\nPróbáld újra, amikor játékban van.",
			"{0} から参加への応答がありませんでした。\n\n相手がゲーム中のときにもう一度お試しください。",
			"{0} 님이 참가 요청에 응답하지 않았습니다.\n\n상대가 게임 중일 때 다시 시도하십시오.",
			"{0} 沒有回應加入請求。\n\n請在對方進入遊戲後再試一次。")
		.Add("REFII_MP_NET_ERROR", "RexNet reported an error.", "RexNet a signalé une erreur.",
			"RexNet hat einen Fehler gemeldet.", "RexNet ha segnalato un errore.", "RexNet ha notificado un error.",
			"RexNet сообщил об ошибке.", "RexNet zgłosił błąd.", "RexNet ohlásil chybu.", "A RexNet hibát jelzett.",
			"RexNet がエラーを報告しました。", "RexNet에서 오류를 보고했습니다.", "RexNet 回報了錯誤。")
		.Add("REFII_MP_REQUEST_DECLINED", "{0} declined your friend request.\n\nYou can ask again.",
			"{0} a refusé votre demande d'ami.\n\nVous pouvez la renvoyer.",
			"{0} hat deine Freundschaftsanfrage abgelehnt.\n\nDu kannst erneut anfragen.",
			"{0} ha rifiutato la tua richiesta di amicizia.\n\nPuoi inviarla di nuovo.",
			"{0} ha rechazado tu solicitud de amistad.\n\nPuedes volver a enviarla.",
			"{0} отклоняет ваш запрос в друзья.\n\nВы можете отправить его снова.",
			"{0} odrzuca twoją prośbę o dodanie do znajomych.\n\nMożesz wysłać ją ponownie.",
			"{0} odmítá tvoji žádost o přátelství.\n\nMůžeš ji poslat znovu.",
			"{0} elutasította a barátkérelmedet.\n\nÚjra megkérdezheted.",
			"{0} がフレンドリクエストを拒否しました。\n\nもう一度送信できます。",
			"{0} 님이 친구 요청을 거절했습니다.\n\n다시 요청할 수 있습니다.",
			"{0} 拒絕了您的好友請求。\n\n您可以再次提出請求。")
		.Add("REFII_MP_ASKS_TO_JOIN", "{0} asks to join your game", "{0} demande à rejoindre votre partie",
			"{0} möchte deinem Spiel beitreten", "{0} chiede di unirsi alla tua partita",
			"{0} pide unirse a tu partida", "{0} просит принять в вашу игру", "{0} prosi o dołączenie do twojej gry",
			"{0} žádá o připojení k tvé hře", "{0} csatlakozna a játékodhoz", "{0} がゲームへの参加を希望しています",
			"{0} 님이 게임 참가를 요청했습니다", "{0} 想加入您的遊戲")
		.Add("REFII_MP_DECLINE", "    Decline", "    Refuser", "    Ablehnen", "    Rifiuta", "    Rechazar",
			"    Отклонить", "    Odrzuć", "    Odmítnout", "    Elutasítás", "    拒否", "    거절", "    拒絕")
		.Add("REFII_MP_SELF_NAMED", "\"{0}\" (self-named)", "\"{0}\" (nom déclaré par le joueur)",
			"\"{0}\" (selbst gewählter Name)", "\"{0}\" (nome dichiarato dal giocatore)",
			"\"{0}\" (nombre indicado por el jugador)", "\"{0}\" (имя указано самим игроком)",
			"\"{0}\" (nazwa podana przez gracza)", "\"{0}\" (jméno uvedené hráčem)",
			"\"{0}\" (a játékos által megadott név)", "\"{0}\" (本人が名乗った名前)", "\"{0}\" (본인이 밝힌 이름)",
			"\"{0}\" (對方自稱的名稱)")
		.Add("REFII_MP_DISMISS", "    Dismiss", "    Ignorer", "    Verwerfen", "    Ignora", "    Descartar",
			"    Скрыть", "    Pomiń", "    Zahodit", "    Elvetés", "    無視", "    무시", "    忽略")
		.Add("REFII_MP_INVITED_YOU", "{0} invited you", "{0} vous invite", "{0} lädt dich ein", "{0} ti invita",
			"{0} te invita", "{0} приглашает вас", "{0} zaprasza cię", "{0} tě zve", "{0} meghívott",
			"{0} から招待されました", "{0} 님이 초대했습니다", "{0} 邀請了您")
		.Add("REFII_MP_GAME_OF", "{0}'s game", "Partie de {0}", "Spiel von {0}", "Partita di {0}", "Partida de {0}",
			"Игра {0}", "Gra gracza {0}", "Hra hráče {0}", "{0} játéka", "{0} のゲーム", "{0} 님의 게임", "{0} 的遊戲")
		.Add("REFII_MP_GAME_OPEN", "{0} — {1}/{2} open", "{0} — {1}/{2} places libres", "{0} — {1}/{2} frei",
			"{0} — {1}/{2} posti liberi", "{0} — {1}/{2} libres", "{0} — свободно {1}/{2}", "{0} — wolne {1}/{2}",
			"{0} — volno {1}/{2}", "{0} — {1}/{2} szabad", "{0} — 空き {1}/{2}", "{0} — 빈자리 {1}/{2}",
			"{0} — {1}/{2} 空位")
		.Add("REFII_MP_HOSTING_OFF", "Hosting is unavailable — RexNet is off",
			"Hébergement indisponible — RexNet est désactivé", "Spielleitung nicht verfügbar — RexNet ist aus",
			"Hosting non disponibile — RexNet è disattivato", "Alojamiento no disponible — RexNet está desactivado",
			"Вести игру нельзя — RexNet выключен", "Prowadzenie gry niedostępne — RexNet jest wyłączony",
			"Hostování není dostupné — RexNet je vypnutý", "A játékvezetés nem érhető el — a RexNet ki van kapcsolva",
			"ホストできません — RexNet がオフです", "호스트할 수 없습니다 — RexNet이 꺼져 있습니다",
			"無法主持遊戲 — RexNet 已關閉")
		.Add("REFII_MP_JOIN_ANYONE", "Who can join: Anyone", "Qui peut rejoindre : tout le monde",
			"Wer beitreten darf: Alle", "Chi può unirsi: chiunque", "Quién puede unirse: cualquiera",
			"Кто может присоединиться: все", "Kto może dołączyć: każdy", "Kdo se může připojit: kdokoli",
			"Ki csatlakozhat: bárki", "参加できる相手: 誰でも", "참가 가능: 누구나", "誰可以加入：任何人")
		.Add("REFII_MP_JOIN_INVITES", "Who can join: Invites only", "Qui peut rejoindre : sur invitation",
			"Wer beitreten darf: Nur mit Einladung", "Chi può unirsi: solo su invito",
			"Quién puede unirse: solo con invitación", "Кто может присоединиться: только по приглашению",
			"Kto może dołączyć: tylko zaproszeni", "Kdo se může připojit: jen pozvaní",
			"Ki csatlakozhat: csak meghívással", "参加できる相手: 招待のみ", "참가 가능: 초대만",
			"誰可以加入：僅限邀請")
		.Add("REFII_MP_JOIN_NO_ONE", "Who can join: No one", "Qui peut rejoindre : personne",
			"Wer beitreten darf: Niemand", "Chi può unirsi: nessuno", "Quién puede unirse: nadie",
			"Кто может присоединиться: никто", "Kto może dołączyć: nikt", "Kdo se může připojit: nikdo",
			"Ki csatlakozhat: senki", "参加できる相手: 誰も不可", "참가 가능: 없음", "誰可以加入：無人")
		.Add("REFII_MP_NOT_HOSTED", " — no game is hosted yet", " — aucune partie hébergée pour l'instant",
			" — noch wird kein Spiel geleitet", " — nessuna partita ospitata per ora",
			" — aún no se aloja ninguna partida", " — игра ещё не ведётся", " — żadna gra nie jest jeszcze prowadzona",
			" — zatím se nehostuje žádná hra", " — még nincs vezetett játék", " — まだゲームをホストしていません",
			" — 아직 호스트 중인 게임이 없습니다", " — 尚未主持遊戲")
		.Add("REFII_MP_MY_STATUS", "My status: {0}", "Mon statut : {0}", "Mein Status: {0}", "Il mio stato: {0}",
			"Mi estado: {0}", "Мой статус: {0}", "Mój status: {0}", "Můj stav: {0}", "Állapotom: {0}",
			"自分のステータス: {0}", "내 상태: {0}", "我的狀態：{0}")
		.Add("REFII_MP_SEND", "Send: {0}", "Envoyer : {0}", "Senden: {0}", "Invia: {0}", "Enviar: {0}",
			"Отправить: {0}", "Wyślij: {0}", "Poslat: {0}", "Küldés: {0}", "送信: {0}", "보내기: {0}", "傳送：{0}")
		.Add("REFII_MP_ABOUT_FRIENDS", "Everyone who has added you, and what they are doing.",
			"Tous ceux qui vous ont ajouté, et ce qu'ils font.",
			"Alle, die dich hinzugefügt haben, und was sie gerade tun.",
			"Tutti quelli che ti hanno aggiunto e cosa stanno facendo.",
			"Todos los que te han añadido y lo que están haciendo.", "Все, кто добавил вас, и чем они заняты.",
			"Wszyscy, którzy cię dodali, i to, co robią.", "Všichni, kdo si tě přidali, a co právě dělají.",
			"Mindenki, aki felvett téged, és hogy épp mit csinál.", "あなたを追加した全員と、その現在の状況です。",
			"나를 추가한 모든 사람과 그들이 하고 있는 일입니다.", "所有已新增您的人，以及他們正在做什麼。")
		.Add("REFII_MP_ABOUT_REQUESTS", "People asking to be your friend or to join your game. A accepts.",
			"Les joueurs qui demandent à devenir votre ami ou à rejoindre votre partie. A accepte.",
			"Spieler, die dein Freund werden oder deinem Spiel beitreten möchten. A nimmt an.",
			"Giocatori che chiedono la tua amicizia o di unirsi alla tua partita. A accetta.",
			"Jugadores que piden ser tus amigos o unirse a tu partida. A acepta.",
			"Игроки, которые просятся к вам в друзья или в вашу игру. A — принять.",
			"Gracze, którzy proszą o dodanie do znajomych lub o dołączenie do twojej gry. A przyjmuje.",
			"Hráči, kteří žádají o přátelství nebo o připojení k tvé hře. A přijímá.",
			"Játékosok, akik a barátaid lennének vagy csatlakoznának a játékodhoz. Az A elfogadja.",
			"フレンドになりたい、またはゲームに参加したいプレイヤーです。A で承認します。",
			"친구가 되거나 게임에 참가하려는 플레이어입니다. A로 수락합니다.",
			"想成為您的好友或加入您遊戲的玩家。按 A 接受。")
		.Add("REFII_MP_ABOUT_INVITES", "Games you have been invited to. A joins.",
			"Les parties auxquelles vous êtes invité. A rejoint la partie.",
			"Spiele, zu denen du eingeladen wurdest. A tritt bei.",
			"Partite a cui sei stato invitato. A ti fa entrare.", "Partidas a las que te han invitado. A te une.",
			"Игры, в которые вас пригласили. A — присоединиться.", "Gry, do których cię zaproszono. A dołącza.",
			"Hry, do kterých tě někdo pozval. A se připojí.", "Játékok, amelyekbe meghívtak. Az A csatlakozik.",
			"招待されたゲームです。A で参加します。", "초대받은 게임입니다. A로 참가합니다.",
			"您受邀加入的遊戲。按 A 加入。")
		.Add("REFII_MP_ABOUT_GAMES", "Friends who are hosting, and any public games a search found.",
			"Les amis qui hébergent une partie, et les parties publiques trouvées par une recherche.",
			"Freunde, die ein Spiel leiten, und öffentliche Spiele, die eine Suche gefunden hat.",
			"Gli amici che ospitano una partita e le partite pubbliche trovate da una ricerca.",
			"Los amigos que alojan una partida y las partidas públicas que ha encontrado una búsqueda.",
			"Друзья, которые ведут игру, и открытые игры, найденные поиском.",
			"Znajomi, którzy prowadzą grę, oraz gry publiczne znalezione podczas wyszukiwania.",
			"Přátelé, kteří hostují hru, a veřejné hry nalezené hledáním.",
			"Barátok, akik játékot vezetnek, és a keresés által talált nyilvános játékok.",
			"ホスト中のフレンドと、検索で見つかった公開ゲームです。",
			"호스트 중인 친구와 검색으로 찾은 공개 게임입니다.", "正在主持遊戲的好友，以及搜尋到的公開遊戲。")
		.Add("REFII_MP_FRIEND_HOSTING_GAME", "In a game, and hosting.\n\nA asks them for an invitation.",
			"En jeu, et héberge la partie.\n\nA lui demande une invitation.",
			"Im Spiel und leitet es.\n\nA bittet um eine Einladung.",
			"In partita, e la ospita.\n\nA gli chiede un invito.",
			"En una partida, y la aloja.\n\nA le pide una invitación.",
			"В игре и ведёт её.\n\nA — попросить приглашение.", "W grze i ją prowadzi.\n\nA prosi o zaproszenie.",
			"Ve hře a hostuje ji.\n\nA požádá o pozvánku.", "Játékban van, és ő vezeti.\n\nAz A meghívót kér tőle.",
			"ゲーム中で、ホストしています。\n\nA で招待を依頼します。",
			"게임 중이며 호스트하고 있습니다.\n\nA로 초대를 요청합니다.",
			"正在遊戲中並主持遊戲。\n\n按 A 向對方請求邀請。")
		.Add("REFII_MP_FRIEND_HOSTING_TITLE", "In {0}, and hosting.\n\nA asks them for an invitation.",
			"Joue à {0}, et héberge la partie.\n\nA lui demande une invitation.",
			"Spielt {0} und leitet das Spiel.\n\nA bittet um eine Einladung.",
			"Gioca a {0}, e ospita la partita.\n\nA gli chiede un invito.",
			"Juega a {0}, y aloja la partida.\n\nA le pide una invitación.",
			"В игре {0} и ведёт её.\n\nA — попросить приглашение.",
			"W grze {0} i ją prowadzi.\n\nA prosi o zaproszenie.", "Ve hře {0} a hostuje ji.\n\nA požádá o pozvánku.",
			"Játékban: {0}, és ő vezeti.\n\nAz A meghívót kér tőle.",
			"{0} をプレイ中で、ホストしています。\n\nA で招待を依頼します。",
			"{0} 플레이 중이며 호스트하고 있습니다.\n\nA로 초대를 요청합니다.",
			"正在玩 {0} 並主持遊戲。\n\n按 A 向對方請求邀請。")
		.Add("REFII_MP_FRIEND_ONLINE", "Online, not in a game.\n\nA asks them for an invitation.",
			"En ligne, pas en jeu.\n\nA lui demande une invitation.",
			"Online, nicht im Spiel.\n\nA bittet um eine Einladung.",
			"Online, non in partita.\n\nA gli chiede un invito.",
			"En línea, fuera de partida.\n\nA le pide una invitación.",
			"В сети, не в игре.\n\nA — попросить приглашение.", "Online, poza grą.\n\nA prosi o zaproszenie.",
			"Online, není ve hře.\n\nA požádá o pozvánku.", "Online, nincs játékban.\n\nAz A meghívót kér tőle.",
			"オンラインですが、ゲーム中ではありません。\n\nA で招待を依頼します。",
			"온라인이지만 게임 중이 아닙니다.\n\nA로 초대를 요청합니다.",
			"在線上，但不在遊戲中。\n\n按 A 向對方請求邀請。")
		.Add("REFII_MP_FRIEND_OFFLINE", "Offline.\n\nThey appear here again when they next play.",
			"Hors ligne.\n\nCe joueur réapparaîtra ici la prochaine fois qu'il jouera.",
			"Offline.\n\nDer Spieler erscheint hier wieder, sobald er das nächste Mal spielt.",
			"Offline.\n\nIl giocatore ricomparirà qui quando giocherà di nuovo.",
			"Sin conexión.\n\nVolverá a aparecer aquí la próxima vez que juegue.",
			"Не в сети.\n\nИгрок снова появится здесь, когда начнёт играть.",
			"Offline.\n\nGracz pojawi się tu znowu, gdy następnym razem zagra.",
			"Offline.\n\nZnovu se tu objeví, až bude příště hrát.",
			"Offline.\n\nÚjra megjelenik itt, amikor legközelebb játszik.",
			"オフラインです。\n\n次にプレイしたときに、またここに表示されます。",
			"오프라인입니다.\n\n다음에 플레이하면 여기에 다시 표시됩니다.",
			"離線。\n\n對方下次遊玩時會再次出現在這裡。")
		.Add("REFII_MP_PUBLIC_FULL", "A public game, full ({0} of {1} taken).",
			"Une partie publique, complète ({0} places sur {1} occupées).",
			"Ein öffentliches Spiel, voll ({0} von {1} belegt).",
			"Una partita pubblica, al completo ({0} posti su {1} occupati).",
			"Una partida pública, completa ({0} de {1} plazas ocupadas).",
			"Открытая игра, мест нет (занято {0} из {1}).", "Gra publiczna, pełna (zajęte {0} z {1}).",
			"Veřejná hra, plná (obsazeno {0} z {1}).", "Nyilvános játék, megtelt ({0} / {1} foglalt).",
			"公開ゲーム、満員です ({1} 枠中 {0} 枠使用中)。", "공개 게임, 만원입니다 ({1}자리 중 {0}자리 사용 중).",
			"公開遊戲，已滿 (已佔用 {0}/{1})。")
		.Add("REFII_MP_PUBLIC_OPEN", "A public game, {0} of {1} open.",
			"Une partie publique, {0} places libres sur {1}.", "Ein öffentliches Spiel, {0} von {1} frei.",
			"Una partita pubblica, {0} posti liberi su {1}.", "Una partida pública, {0} de {1} plazas libres.",
			"Открытая игра, свободно {0} из {1}.", "Gra publiczna, wolne {0} z {1}.", "Veřejná hra, volno {0} z {1}.",
			"Nyilvános játék, {0} / {1} szabad.", "公開ゲーム、{1} 枠中 {0} 枠が空いています。",
			"공개 게임, {1}자리 중 {0}자리가 비어 있습니다.", "公開遊戲，{0}/{1} 空位。")
		.Add("REFII_MP_NEEDS_INVITE", "It needs an invitation.", "Elle nécessite une invitation.",
			"Es erfordert eine Einladung.", "Richiede un invito.", "Requiere invitación.", "Нужно приглашение.",
			"Wymaga zaproszenia.", "Vyžaduje pozvánku.", "Meghívó kell hozzá.", "招待が必要です。",
			"초대가 필요합니다.", "需要邀請。")
		.Add("REFII_MP_A_JOINS", "A joins it.", "A rejoint la partie.", "A tritt bei.", "A ti fa entrare.", "A te une.",
			"A — присоединиться.", "A dołącza.", "A se připojí.", "Az A csatlakozik.", "A で参加します。",
			"A로 참가합니다.", "按 A 加入。")
		.Add("REFII_MP_NOT_LISTED", "This player is no longer listed.", "Ce joueur n'est plus dans la liste.",
			"Dieser Spieler ist nicht mehr aufgeführt.", "Questo giocatore non è più in elenco.",
			"Este jugador ya no aparece en la lista.", "Этого игрока больше нет в списке.",
			"Tego gracza nie ma już na liście.", "Tento hráč už v seznamu není.", "Ez a játékos már nincs a listán.",
			"このプレイヤーはもう一覧にありません。", "이 플레이어는 더 이상 목록에 없습니다.", "此玩家已不在清單中。")
		.Add("REFII_MP_REQUEST_WANTS", "Wants to be your friend.", "Souhaite devenir votre ami.",
			"Möchte dein Freund werden.", "Vuole diventare tuo amico.", "Quiere ser tu amigo.",
			"Просится к вам в друзья.", "Chce zostać twoim znajomym.", "Chce být tvým přítelem.",
			"A barátod szeretne lenni.", "フレンドになりたがっています。", "친구가 되고 싶어 합니다.",
			"想成為您的好友。")
		.Add("REFII_MP_REQUEST_ANSWER", "A accepts; the row below dismisses.",
			"A accepte ; la ligne du dessous ignore la demande.",
			"A nimmt an; die Zeile darunter verwirft die Anfrage.", "A accetta; la riga sotto ignora la richiesta.",
			"A acepta; la fila de abajo descarta la solicitud.", "A — принять; строка ниже скрывает запрос.",
			"A przyjmuje; wiersz poniżej pomija prośbę.", "A přijímá; řádek níže žádost zahodí.",
			"Az A elfogadja; az alatta lévő sor elveti.", "A で承認、下の行で無視します。",
			"A로 수락하고, 아래 줄로 무시합니다.", "按 A 接受；下一列可忽略此請求。")
		.Add("REFII_MP_REQUEST_WITHDRAWN", "This request has been withdrawn.", "Cette demande a été retirée.",
			"Diese Anfrage wurde zurückgezogen.", "Questa richiesta è stata ritirata.",
			"Esta solicitud se ha retirado.", "Этот запрос отозван.", "Ta prośba została wycofana.",
			"Tato žádost byla stažena.", "Ezt a kérelmet visszavonták.", "このリクエストは取り下げられました。",
			"이 요청은 취소되었습니다.", "此請求已被撤回。")
		.Add("REFII_MP_ABOUT_DISMISS",
			"Drops this request without answering it.\n\nThey are not told, and they can ask again.",
			"Écarte cette demande sans y répondre.\n\nLe joueur n'en est pas informé et peut la renvoyer.",
			"Verwirft diese Anfrage, ohne sie zu beantworten.\n\nDer Spieler erfährt nichts davon und kann erneut anfragen.",
			"Scarta questa richiesta senza rispondere.\n\nIl giocatore non viene avvisato e può inviarla di nuovo.",
			"Descarta esta solicitud sin responderla.\n\nEl jugador no recibe aviso y puede volver a enviarla.",
			"Скрывает этот запрос без ответа.\n\nИгроку ничего не сообщается, и он может отправить запрос снова.",
			"Pomija tę prośbę bez odpowiedzi.\n\nGracz nie dostaje powiadomienia i może poprosić ponownie.",
			"Zahodí tuto žádost bez odpovědi.\n\nHráč se to nedozví a může požádat znovu.",
			"Válasz nélkül elveti ezt a kérelmet.\n\nA játékos nem kap értesítést, és újra kérheti.",
			"このリクエストに応答せずに破棄します。\n\n相手には通知されず、相手はもう一度リクエストできます。",
			"이 요청에 응답하지 않고 버립니다.\n\n상대에게 알리지 않으며, 상대는 다시 요청할 수 있습니다.",
			"不回應並捨棄此請求。\n\n對方不會收到通知，並可再次提出請求。")
		.Add("REFII_MP_ABOUT_JOINASK",
			"Asked through Discord to join your game.\n\nA sends them the invite; the row below declines. Unanswered, the ask lapses on its own.",
			"A demandé via Discord à rejoindre votre partie.\n\nA lui envoie l'invitation ; la ligne du dessous refuse. Sans réponse, la demande expire d'elle-même.",
			"Hat über Discord angefragt, deinem Spiel beizutreten.\n\nA sendet die Einladung; die Zeile darunter lehnt ab. Ohne Antwort läuft die Anfrage von selbst ab.",
			"Ha chiesto tramite Discord di unirsi alla tua partita.\n\nA gli invia l'invito; la riga sotto rifiuta. Senza risposta, la richiesta scade da sola.",
			"Ha pedido por Discord unirse a tu partida.\n\nA le envía la invitación; la fila de abajo la rechaza. Sin respuesta, la solicitud caduca sola.",
			"Запрос через Discord на присоединение к вашей игре.\n\nA — отправить приглашение; строка ниже отклоняет запрос. Без ответа запрос истечёт сам.",
			"Prośba przez Discord o dołączenie do twojej gry.\n\nA wysyła zaproszenie; wiersz poniżej odrzuca. Bez odpowiedzi prośba sama wygaśnie.",
			"Žádost přes Discord o připojení k tvé hře.\n\nA pošle pozvánku; řádek níže odmítne. Bez odpovědi žádost sama vyprší.",
			"A Discordon kérte, hogy csatlakozhasson a játékodhoz.\n\nAz A elküldi neki a meghívót; az alatta lévő sor elutasítja. Válasz nélkül a kérés magától lejár.",
			"Discord 経由であなたのゲームへの参加を希望しています。\n\nA で招待を送信、下の行で拒否します。応答しない場合、リクエストは自動的に期限切れになります。",
			"Discord를 통해 게임 참가를 요청했습니다.\n\nA로 초대를 보내고, 아래 줄로 거절합니다. 응답하지 않으면 요청은 저절로 만료됩니다.",
			"透過 Discord 請求加入您的遊戲。\n\n按 A 送出邀請；下一列可拒絕。若未回應，請求會自行失效。")
		.Add("REFII_MP_ABOUT_JOINDENY", "Tells Discord no.\n\nThey can ask again.",
			"Répond non à Discord.\n\nLe joueur peut redemander.",
			"Antwortet Discord mit Nein.\n\nDer Spieler kann erneut anfragen.",
			"Risponde di no a Discord.\n\nIl giocatore può chiedere di nuovo.",
			"Responde que no a Discord.\n\nEl jugador puede volver a pedirlo.",
			"Отвечает Discord отказом.\n\nИгрок может попросить снова.",
			"Odpowiada Discordowi odmownie.\n\nGracz może poprosić ponownie.",
			"Odpoví Discordu, že ne.\n\nHráč může požádat znovu.",
			"Nemet mond a Discordnak.\n\nA játékos újra kérheti.",
			"Discord に拒否を伝えます。\n\n相手はもう一度リクエストできます。",
			"Discord에 거절을 알립니다.\n\n상대는 다시 요청할 수 있습니다.",
			"向 Discord 回覆拒絕。\n\n對方可再次提出請求。")
		.Add("REFII_MP_ABOUT_INVITE", "You have been invited to this game.\n\nA joins it.",
			"Vous êtes invité à cette partie.\n\nA la rejoint.",
			"Du wurdest zu diesem Spiel eingeladen.\n\nA tritt bei.",
			"Sei stato invitato a questa partita.\n\nA ti fa entrare.", "Te han invitado a esta partida.\n\nA te une.",
			"Вас пригласили в эту игру.\n\nA — присоединиться.", "Zaproszono cię do tej gry.\n\nA dołącza.",
			"Někdo tě pozval do této hry.\n\nA se připojí.", "Meghívtak ebbe a játékba.\n\nAz A csatlakozik.",
			"このゲームに招待されています。\n\nA で参加します。", "이 게임에 초대받았습니다.\n\nA로 참가합니다.",
			"您已受邀加入此遊戲。\n\n按 A 加入。")
		.Add("REFII_MP_MOD_MISSING", "This mod is enabled but its package is missing from the mods folder.",
			"Ce mod est activé, mais son paquet est absent du dossier des mods.",
			"Dieser Mod ist aktiviert, aber sein Paket fehlt im Mod-Ordner.",
			"Questa mod è attiva, ma il suo pacchetto manca dalla cartella delle mod.",
			"Este mod está activado, pero su paquete no está en la carpeta de mods.",
			"Этот мод включён, но его пакета нет в папке модов.",
			"Ten mod jest włączony, ale jego pakietu brakuje w folderze modów.",
			"Tento mód je zapnutý, ale jeho balíček ve složce módů chybí.",
			"Ez a mod be van kapcsolva, de a csomagja hiányzik a modmappából.",
			"この MOD は有効ですが、パッケージが MOD フォルダーにありません。",
			"이 모드는 사용 중이지만 패키지가 모드 폴더에 없습니다.", "此模組已啟用，但模組資料夾中找不到其套件。")
		.Add("REFII_MP_MOD_VERSION_BY", "Version {0} by {1}.", "Version {0} par {1}.", "Version {0} von {1}.",
			"Versione {0} di {1}.", "Versión {0} de {1}.", "Версия {0}, автор: {1}.", "Wersja {0}, autor: {1}.",
			"Verze {0}, autor: {1}.", "{0} verzió, készítette: {1}.", "バージョン {0}、作者: {1}。",
			"버전 {0}, 제작: {1}.", "版本 {0}，作者：{1}。")
		.Add("REFII_MP_MOD_VERSION", "Version {0}.", "Version {0}.", "Version {0}.", "Versione {0}.", "Versión {0}.",
			"Версия {0}.", "Wersja {0}.", "Verze {0}.", "{0} verzió.", "バージョン {0}。", "버전 {0}.", "版本 {0}。")
		.Add("REFII_MP_MOD_BY", "By {0}.", "Par {0}.", "Von {0}.", "Di {0}.", "De {0}.", "Автор: {0}.", "Autor: {0}.",
			"Autor: {0}.", "Készítette: {0}.", "作者: {0}。", "제작: {0}.", "作者：{0}。")
		.Add("REFII_MP_NONE_MODS",
			"Mods are installed and ordered in the launcher's Mods panel.\n\nAnything enabled there is listed here, in load order — the first wins a conflict.",
			"Les mods s'installent et s'ordonnent dans le panneau Mods du lanceur.\n\nTout mod activé là-bas est listé ici, dans l'ordre de chargement — le premier l'emporte en cas de conflit.",
			"Mods werden im Mods-Bereich des Launchers installiert und sortiert.\n\nAlles dort Aktivierte wird hier in Ladereihenfolge aufgeführt — bei einem Konflikt gewinnt der erste.",
			"Le mod si installano e si ordinano nel pannello Mods del launcher.\n\nTutto ciò che è attivo lì è elencato qui, in ordine di caricamento — in caso di conflitto vince la prima.",
			"Los mods se instalan y se ordenan en el panel Mods del lanzador.\n\nTodo lo activado allí aparece aquí, en orden de carga — el primero gana en caso de conflicto.",
			"Моды устанавливаются и упорядочиваются на панели Mods в лаунчере.\n\nВсё включённое там показано здесь в порядке загрузки — при конфликте побеждает первый.",
			"Mody instaluje się i porządkuje w panelu Mods w launcherze.\n\nWszystko, co tam włączono, jest tu wymienione w kolejności wczytywania — w razie konfliktu wygrywa pierwszy.",
			"Módy se instalují a řadí v panelu Mods ve spouštěči.\n\nVše, co je tam zapnuté, je uvedeno tady v pořadí načítání — při konfliktu vyhrává první.",
			"A modokat az indító Mods paneljén lehet telepíteni és sorba rendezni.\n\nAmi ott be van kapcsolva, az itt betöltési sorrendben szerepel — ütközéskor az első nyer.",
			"MOD のインストールと並べ替えは、ランチャーの Mods パネルで行います。\n\nそこで有効にしたものが、読み込み順にここへ表示されます — 競合した場合は先頭が優先されます。",
			"모드는 런처의 Mods 패널에서 설치하고 순서를 정합니다.\n\n거기서 사용하도록 설정한 모드가 불러오기 순서대로 여기에 표시됩니다 — 충돌하면 첫 번째가 우선합니다.",
			"模組在啟動器的 Mods 面板中安裝與排序。\n\n在那裡啟用的模組會依載入順序列在這裡 — 發生衝突時以最前面的為準。")
		.Add("REFII_MP_NONE_FRIENDS",
			"No friends yet.\n\nGive someone your friend code from the row below, and once they add you they appear here with what they are playing.",
			"Pas encore d'amis.\n\nDonnez à quelqu'un votre code ami depuis la ligne ci-dessous ; dès qu'il vous aura ajouté, il apparaîtra ici avec ce à quoi il joue.",
			"Noch keine Freunde.\n\nGib jemandem deinen Freundescode aus der Zeile unten; sobald er dich hinzugefügt hat, erscheint er hier mit dem, was er gerade spielt.",
			"Ancora nessun amico.\n\nDai a qualcuno il tuo codice amico dalla riga qui sotto: quando ti avrà aggiunto, comparirà qui con ciò a cui sta giocando.",
			"Aún no tienes amigos.\n\nDale a alguien tu código de amigo desde la fila de abajo; cuando te añada, aparecerá aquí con lo que está jugando.",
			"Друзей пока нет.\n\nПередайте кому-нибудь свой код друга из строки ниже: когда вас добавят, игрок появится здесь вместе с тем, во что он играет.",
			"Nie masz jeszcze znajomych.\n\nPrzekaż komuś swój kod znajomego z wiersza poniżej; gdy cię doda, pojawi się tutaj wraz z tym, w co gra.",
			"Zatím žádní přátelé.\n\nDej někomu svůj kód přítele z řádku níže; až si tě přidá, objeví se tady i s tím, co právě hraje.",
			"Még nincsenek barátaid.\n\nAdd meg valakinek a barátkódodat az alatta lévő sorból; amint felvett, megjelenik itt azzal együtt, amivel éppen játszik.",
			"フレンドはまだいません。\n\n下の行のフレンドコードを誰かに伝えてください。相手があなたを追加すると、プレイ中のゲームとともにここに表示されます。",
			"아직 친구가 없습니다.\n\n아래 줄의 친구 코드를 다른 사람에게 알려 주십시오. 상대가 나를 추가하면 플레이 중인 게임과 함께 여기에 표시됩니다.",
			"還沒有好友。\n\n請將下一列的好友代碼交給對方；對方新增您之後，就會連同正在玩的遊戲一起出現在這裡。")
		.Add("REFII_MP_NONE_FRIENDS_OFF",
			"RexNet is off, so there are no friends to list.\n\nTurn it on in the launcher's RexNet settings.",
			"RexNet est désactivé : il n'y a donc aucun ami à afficher.\n\nActivez-le dans les paramètres RexNet du lanceur.",
			"RexNet ist aus, daher gibt es keine Freunde anzuzeigen.\n\nSchalte es in den RexNet-Einstellungen des Launchers ein.",
			"RexNet è disattivato, quindi non ci sono amici da elencare.\n\nAttivalo nelle impostazioni RexNet del launcher.",
			"RexNet está desactivado, así que no hay amigos que mostrar.\n\nActívalo en los ajustes de RexNet del lanzador.",
			"RexNet выключен, поэтому список друзей пуст.\n\nВключите его в настройках RexNet в лаунчере.",
			"RexNet jest wyłączony, więc nie ma znajomych do wyświetlenia.\n\nWłącz go w ustawieniach RexNet w launcherze.",
			"RexNet je vypnutý, takže tu nejsou žádní přátelé.\n\nZapni ho v nastavení RexNet ve spouštěči.",
			"A RexNet ki van kapcsolva, ezért nincsenek megjeleníthető barátok.\n\nKapcsold be az indító RexNet-beállításaiban.",
			"RexNet がオフのため、表示できるフレンドがいません。\n\nランチャーの RexNet 設定でオンにしてください。",
			"RexNet이 꺼져 있어 표시할 친구가 없습니다.\n\n런처의 RexNet 설정에서 켜십시오.",
			"RexNet 已關閉，因此沒有可列出的好友。\n\n請在啟動器的 RexNet 設定中開啟。")
		.Add("REFII_MP_NONE_REQUESTS",
			"Nobody has asked to be your friend.\n\nWhen someone adds you by your friend code, their request appears here to accept or dismiss.",
			"Personne n'a demandé à devenir votre ami.\n\nQuand quelqu'un vous ajoute avec votre code ami, sa demande apparaît ici pour être acceptée ou ignorée.",
			"Niemand hat eine Freundschaftsanfrage gestellt.\n\nWenn dich jemand über deinen Freundescode hinzufügt, erscheint die Anfrage hier zum Annehmen oder Verwerfen.",
			"Nessuno ha chiesto la tua amicizia.\n\nQuando qualcuno ti aggiunge con il tuo codice amico, la richiesta compare qui per essere accettata o ignorata.",
			"Nadie ha pedido ser tu amigo.\n\nCuando alguien te añada con tu código de amigo, su solicitud aparecerá aquí para aceptarla o descartarla.",
			"Никто не просился к вам в друзья.\n\nКогда кто-нибудь добавит вас по коду друга, запрос появится здесь, и его можно будет принять или скрыть.",
			"Nikt nie prosił o dodanie do znajomych.\n\nGdy ktoś doda cię za pomocą twojego kodu znajomego, jego prośba pojawi się tutaj do przyjęcia lub pominięcia.",
			"Nikdo tě nepožádal o přátelství.\n\nAž si tě někdo přidá pomocí tvého kódu přítele, jeho žádost se objeví tady a půjde přijmout nebo zahodit.",
			"Senki sem kérte, hogy a barátod lehessen.\n\nHa valaki felvesz a barátkódoddal, a kérelme itt jelenik meg, ahol elfogadhatod vagy elvetheted.",
			"フレンドリクエストはまだありません。\n\n誰かがフレンドコードであなたを追加すると、そのリクエストがここに表示され、承認または無視できます。",
			"친구 요청이 없습니다.\n\n누군가 친구 코드로 나를 추가하면 그 요청이 여기에 표시되며, 수락하거나 무시할 수 있습니다.",
			"還沒有人請求成為您的好友。\n\n有人使用您的好友代碼新增您時，其請求會出現在這裡，供您接受或忽略。")
		.Add("REFII_MP_NONE_INVITES",
			"No invitations.\n\nWhen a friend invites you to their game it appears here, and accepting is how you join.",
			"Aucune invitation.\n\nQuand un ami vous invite dans sa partie, l'invitation apparaît ici ; l'accepter vous fait rejoindre la partie.",
			"Keine Einladungen.\n\nWenn dich ein Freund in sein Spiel einlädt, erscheint die Einladung hier; mit dem Annehmen trittst du bei.",
			"Nessun invito.\n\nQuando un amico ti invita nella sua partita, l'invito compare qui; accettandolo ti unisci.",
			"Sin invitaciones.\n\nCuando un amigo te invite a su partida, aparecerá aquí; al aceptarla te unes.",
			"Приглашений нет.\n\nКогда друг пригласит вас в свою игру, приглашение появится здесь; приняв его, вы присоединитесь.",
			"Brak zaproszeń.\n\nGdy znajomy zaprosi cię do swojej gry, zaproszenie pojawi się tutaj; przyjmując je, dołączasz.",
			"Žádné pozvánky.\n\nAž tě přítel pozve do své hry, pozvánka se objeví tady; přijetím se připojíš.",
			"Nincsenek meghívások.\n\nHa egy barátod meghív a játékába, a meghívó itt jelenik meg; az elfogadásával csatlakozol.",
			"招待はありません。\n\nフレンドがゲームに招待すると、ここに表示されます。承認すると参加します。",
			"초대가 없습니다.\n\n친구가 게임에 초대하면 여기에 표시되며, 수락하면 참가합니다.",
			"沒有邀請。\n\n好友邀請您加入其遊戲時會出現在這裡，接受後即可加入。")
		.Add("REFII_MP_NONE_GAMES",
			"No games found.\n\nFriends who are hosting appear here on their own; choose Search again to look for public games.",
			"Aucune partie trouvée.\n\nLes amis qui hébergent une partie apparaissent ici d'eux-mêmes ; relancez la recherche pour trouver des parties publiques.",
			"Keine Spiele gefunden.\n\nFreunde, die ein Spiel leiten, erscheinen hier von selbst; starte die Suche erneut, um öffentliche Spiele zu finden.",
			"Nessuna partita trovata.\n\nGli amici che ospitano una partita compaiono qui da soli; avvia di nuovo la ricerca per trovare partite pubbliche.",
			"No se han encontrado partidas.\n\nLos amigos que alojan una partida aparecen aquí solos; vuelve a iniciar la búsqueda para encontrar partidas públicas.",
			"Игры не найдены.\n\nДрузья, которые ведут игру, появляются здесь сами; запустите поиск ещё раз, чтобы найти открытые игры.",
			"Nie znaleziono gier.\n\nZnajomi, którzy prowadzą grę, pojawiają się tu sami; uruchom wyszukiwanie ponownie, aby znaleźć gry publiczne.",
			"Žádné hry nenalezeny.\n\nPřátelé, kteří hostují hru, se tu objeví sami; spusť hledání znovu a najdi veřejné hry.",
			"Nem található játék.\n\nA játékot vezető barátok maguktól megjelennek itt; indítsd el újra a keresést a nyilvános játékokhoz.",
			"ゲームが見つかりません。\n\nホスト中のフレンドは自動的にここへ表示されます。公開ゲームを探すには、もう一度検索してください。",
			"게임을 찾을 수 없습니다.\n\n호스트 중인 친구는 저절로 여기에 표시됩니다. 공개 게임을 찾으려면 다시 검색하십시오.",
			"找不到遊戲。\n\n正在主持遊戲的好友會自動出現在這裡；若要尋找公開遊戲，請再搜尋一次。")
		.Add("REFII_MP_NONE_GAMES_OFF",
			"RexNet is off, so there are no games to find.\n\nTurn it on in the launcher's RexNet settings.",
			"RexNet est désactivé : il n'y a donc aucune partie à trouver.\n\nActivez-le dans les paramètres RexNet du lanceur.",
			"RexNet ist aus, daher gibt es keine Spiele zu finden.\n\nSchalte es in den RexNet-Einstellungen des Launchers ein.",
			"RexNet è disattivato, quindi non ci sono partite da trovare.\n\nAttivalo nelle impostazioni RexNet del launcher.",
			"RexNet está desactivado, así que no hay partidas que buscar.\n\nActívalo en los ajustes de RexNet del lanzador.",
			"RexNet выключен, поэтому игр не найти.\n\nВключите его в настройках RexNet в лаунчере.",
			"RexNet jest wyłączony, więc nie ma gier do znalezienia.\n\nWłącz go w ustawieniach RexNet w launcherze.",
			"RexNet je vypnutý, takže nejsou žádné hry k nalezení.\n\nZapni ho v nastavení RexNet ve spouštěči.",
			"A RexNet ki van kapcsolva, ezért nincsenek megtalálható játékok.\n\nKapcsold be az indító RexNet-beállításaiban.",
			"RexNet がオフのため、ゲームを探せません。\n\nランチャーの RexNet 設定でオンにしてください。",
			"RexNet이 꺼져 있어 찾을 게임이 없습니다.\n\n런처의 RexNet 설정에서 켜십시오.",
			"RexNet 已關閉，因此沒有可尋找的遊戲。\n\n請在啟動器的 RexNet 設定中開啟。")
		.Add("REFII_MP_NONE_OTHER", "Nothing here yet.", "Rien ici pour l'instant.", "Hier ist noch nichts.",
			"Ancora niente qui.", "Aún no hay nada aquí.", "Здесь пока ничего нет.", "Na razie nic tu nie ma.",
			"Zatím tu nic není.", "Itt még nincs semmi.", "まだ何もありません。", "아직 아무것도 없습니다.",
			"這裡還沒有任何內容。")
		.Add("REFII_MP_ABOUT_STATUS",
			"What you are here for, shown to your friends beside your name.\n\nA steps to the next.",
			"Ce pour quoi vous êtes là, affiché à vos amis à côté de votre nom.\n\nA passe au suivant.",
			"Wofür du hier bist, für deine Freunde neben deinem Namen sichtbar.\n\nA wechselt zum nächsten.",
			"Il motivo per cui sei qui, mostrato ai tuoi amici accanto al tuo nome.\n\nA passa al successivo.",
			"Para qué estás aquí; tus amigos lo ven junto a tu nombre.\n\nA pasa al siguiente.",
			"Зачем вы здесь: друзья видят это рядом с вашим именем.\n\nA — следующий вариант.",
			"Po co tu jesteś — znajomi widzą to obok twojej nazwy.\n\nA przechodzi do następnego.",
			"Proč tu jsi — přátelé to vidí vedle tvého jména.\n\nA přepne na další.",
			"Hogy miért vagy itt — a barátaid a neved mellett látják.\n\nAz A a következőre lép.",
			"ここでの目的です。フレンドにはあなたの名前の横に表示されます。\n\nA で次に切り替えます。",
			"여기 있는 목적으로, 친구에게 내 이름 옆에 표시됩니다.\n\nA로 다음으로 넘어갑니다.",
			"您在此的目的，會顯示在好友看到的您名稱旁。\n\n按 A 切換到下一個。")
		.Add("REFII_MP_ABOUT_GREET", "A greeting your friends see wherever they are playing.\n\nA sends it.",
			"Un salut que vos amis voient où qu'ils jouent.\n\nA l'envoie.",
			"Ein Gruß, den deine Freunde sehen, wo immer sie gerade spielen.\n\nA sendet ihn.",
			"Un saluto che i tuoi amici vedono ovunque stiano giocando.\n\nA lo invia.",
			"Un saludo que tus amigos ven allí donde estén jugando.\n\nA lo envía.",
			"Приветствие, которое друзья увидят, где бы они ни играли.\n\nA — отправить.",
			"Pozdrowienie, które znajomi zobaczą, gdziekolwiek grają.\n\nA je wysyła.",
			"Pozdrav, který přátelé uvidí, ať hrají kdekoli.\n\nA ho pošle.",
			"Üdvözlet, amelyet a barátaid bárhol játszanak is, látnak.\n\nAz A elküldi.",
			"フレンドがどこでプレイしていても表示されるあいさつです。\n\nA で送信します。",
			"친구가 어디서 플레이하든 보게 되는 인사입니다.\n\nA로 보냅니다.",
			"無論好友在哪裡遊玩都會看到的問候。\n\n按 A 傳送。")
		.Add("REFII_MP_ABOUT_JOINABLE",
			"Who can join the game you are playing: anyone you are friends with, only someone you invite, or no one (asks to join are declined).\n\nA steps to the next.",
			"Qui peut rejoindre la partie à laquelle vous jouez : n'importe lequel de vos amis, seulement un joueur que vous invitez, ou personne (les demandes sont refusées).\n\nA passe au suivant.",
			"Wer dem Spiel beitreten darf, das du gerade spielst: jeder deiner Freunde, nur wer von dir eingeladen wird, oder niemand (Anfragen werden abgelehnt).\n\nA wechselt zum nächsten.",
			"Chi può unirsi alla partita a cui stai giocando: qualsiasi tuo amico, solo chi inviti tu, oppure nessuno (le richieste vengono rifiutate).\n\nA passa al successivo.",
			"Quién puede unirse a la partida que estás jugando: cualquiera de tus amigos, solo quien tú invites o nadie (las solicitudes se rechazan).\n\nA pasa al siguiente.",
			"Кто может присоединиться к вашей текущей игре: любой из ваших друзей, только приглашённый вами игрок или никто (запросы отклоняются).\n\nA — следующий вариант.",
			"Kto może dołączyć do gry, w którą grasz: każdy z twoich znajomych, tylko osoba, którą zaprosisz, albo nikt (prośby są odrzucane).\n\nA przechodzi do następnego.",
			"Kdo se může připojit ke hře, kterou hraješ: kdokoli z tvých přátel, jen ten, koho pozveš, nebo nikdo (žádosti se odmítají).\n\nA přepne na další.",
			"Ki csatlakozhat a játékhoz, amellyel játszol: bármelyik barátod, csak akit meghívsz, vagy senki (a kérések elutasításra kerülnek).\n\nAz A a következőre lép.",
			"プレイ中のゲームに参加できる相手です: フレンドなら誰でも、招待した相手のみ、または誰も不可 (参加リクエストは拒否されます)。\n\nA で次に切り替えます。",
			"플레이 중인 게임에 참가할 수 있는 사람입니다: 친구라면 누구나, 내가 초대한 사람만, 또는 없음 (참가 요청은 거절됩니다).\n\nA로 다음으로 넘어갑니다.",
			"誰可以加入您正在玩的遊戲：任何好友、僅限您邀請的人，或是無人 (加入請求會被拒絕)。\n\n按 A 切換到下一個。")
		.Add("REFII_MP_ADD_OFFER", "Copy someone's friend code, then choose this.\n\nA reads your clipboard.",
			"Copiez le code ami de quelqu'un, puis choisissez cette ligne.\n\nA lit votre presse-papiers.",
			"Kopiere den Freundescode von jemandem und wähle dann diese Zeile.\n\nA liest deine Zwischenablage.",
			"Copia il codice amico di qualcuno, poi scegli questa riga.\n\nA legge i tuoi appunti.",
			"Copia el código de amigo de alguien y luego elige esta fila.\n\nA lee tu portapapeles.",
			"Скопируйте чей-нибудь код друга, затем выберите эту строку.\n\nA — прочитать буфер обмена.",
			"Skopiuj czyjś kod znajomego, a potem wybierz ten wiersz.\n\nA odczytuje schowek.",
			"Zkopíruj něčí kód přítele a pak zvol tento řádek.\n\nA přečte schránku.",
			"Másold ki valaki barátkódját, majd válaszd ezt a sort.\n\nAz A beolvassa a vágólapot.",
			"相手のフレンドコードをコピーしてから、これを選んでください。\n\nA でクリップボードを読み取ります。",
			"상대의 친구 코드를 복사한 다음 이 항목을 선택하십시오.\n\nA로 클립보드를 읽습니다.",
			"請先複製對方的好友代碼，再選擇此項目。\n\n按 A 讀取剪貼簿。")
		.Add("REFII_MP_CODE_COPIED", "Copied to your clipboard.", "Copié dans votre presse-papiers.",
			"In deine Zwischenablage kopiert.", "Copiato negli appunti.", "Copiado en tu portapapeles.",
			"Скопировано в буфер обмена.", "Skopiowano do schowka.", "Zkopírováno do schránky.", "A vágólapra másolva.",
			"クリップボードにコピーしました。", "클립보드에 복사했습니다.", "已複製到剪貼簿。")
		.Add("REFII_MP_CODE_COPY", "A copies it to your clipboard.", "A le copie dans votre presse-papiers.",
			"A kopiert ihn in deine Zwischenablage.", "A lo copia negli appunti.", "A lo copia en tu portapapeles.",
			"A — скопировать в буфер обмена.", "A kopiuje go do schowka.", "A ho zkopíruje do schránky.",
			"Az A a vágólapra másolja.", "A でクリップボードにコピーします。", "A로 클립보드에 복사합니다.",
			"按 A 複製到剪貼簿。")
		.Add("REFII_MP_CODE_NONE",
			"RexNet is not up, so there is no friend code yet.\n\nTurn it on in the launcher's RexNet settings.",
			"RexNet n'est pas démarré : il n'y a donc pas encore de code ami.\n\nActivez-le dans les paramètres RexNet du lanceur.",
			"RexNet läuft nicht, daher gibt es noch keinen Freundescode.\n\nSchalte es in den RexNet-Einstellungen des Launchers ein.",
			"RexNet non è attivo, quindi non c'è ancora un codice amico.\n\nAttivalo nelle impostazioni RexNet del launcher.",
			"RexNet no está en marcha, así que aún no hay código de amigo.\n\nActívalo en los ajustes de RexNet del lanzador.",
			"RexNet не запущен, поэтому кода друга пока нет.\n\nВключите его в настройках RexNet в лаунчере.",
			"RexNet nie działa, więc nie ma jeszcze kodu znajomego.\n\nWłącz go w ustawieniach RexNet w launcherze.",
			"RexNet neběží, takže zatím není žádný kód přítele.\n\nZapni ho v nastavení RexNet ve spouštěči.",
			"A RexNet nem fut, ezért még nincs barátkód.\n\nKapcsold be az indító RexNet-beállításaiban.",
			"RexNet が起動していないため、フレンドコードはまだありません。\n\nランチャーの RexNet 設定でオンにしてください。",
			"RexNet이 실행되고 있지 않아 아직 친구 코드가 없습니다.\n\n런처의 RexNet 설정에서 켜십시오.",
			"RexNet 尚未啟動，因此還沒有好友代碼。\n\n請在啟動器的 RexNet 設定中開啟。")
		.Add("REFII_MP_STATUS_EXPLORING", "Exploring", "Exploration", "Auf Erkundung", "In esplorazione", "Explorando",
			"Исследую мир", "Zwiedzam świat", "Na průzkumu", "Felfedezek", "探索中", "탐험 중", "探索中")
		.Add("REFII_MP_STATUS_COOP", "Looking for co-op", "Cherche une partie en coopération", "Suche Koop-Partner",
			"Cerco una partita cooperativa", "Busco partida cooperativa", "Ищу совместную игру",
			"Szukam gry we współpracy", "Hledám spoluhráče", "Kooperatív játékot keresek", "協力プレイ募集中",
			"협동 게임 구함", "尋找合作伙伴")
		.Add("REFII_MP_STATUS_TRADING", "Trading", "Commerce", "Handel", "Commercio", "Comerciando", "Торгую",
			"Handluję", "Obchoduji", "Kereskedem", "取引中", "거래 중", "交易中")
		.Add("REFII_MP_STATUS_GIFTS", "Giving gifts", "Offre des cadeaux", "Verschenke Geschenke", "Faccio regali",
			"Haciendo regalos", "Дарю подарки", "Rozdaję prezenty", "Rozdávám dary", "Ajándékozok",
			"ギフトを贈っています", "선물 주는 중", "贈送禮物")
		.Add("REFII_MP_GREET_WAVE", "Wave", "Signe de la main", "Winken", "Saluto con la mano", "Saludar con la mano",
			"Помахать рукой", "Pomachanie", "Zamávání", "Integetés", "手を振る", "손 흔들기", "揮手")
		.Add("REFII_MP_GREET_JOIN", "Come and join me", "Rejoignez-moi", "Komm zu mir", "Vieni da me", "Ven conmigo",
			"Присоединяйся ко мне", "Dołącz do mnie", "Přidej se ke mně", "Gyere, csatlakozz", "一緒に遊ぼう",
			"함께해요", "來加入我吧")
		.Add("REFII_MP_GREET_THANKS", "Thank you", "Merci", "Danke", "Grazie", "Gracias", "Спасибо", "Dziękuję",
			"Děkuji", "Köszönöm", "ありがとう", "고마워요", "謝謝")
		.Add("REFII_MP_GREET_WELL_MET", "Well met", "Heureuse rencontre", "Sei gegrüßt", "Ben trovato", "Bien hallado",
			"Приветствую", "Miło cię spotkać", "Buď zdráv", "Jó, hogy látlak", "ごきげんよう", "반가워요", "幸會")
		.Add("REFII_MP_HEARD_WAVE", "{0} waves to you.", "{0} vous fait signe de la main.", "{0} winkt dir zu.",
			"{0} ti saluta con la mano.", "{0} te saluda con la mano.", "{0} машет вам рукой.", "{0} macha do ciebie.",
			"{0} ti mává.", "{0} integet neked.", "{0} が手を振っています。", "{0} 님이 손을 흔듭니다.",
			"{0} 向您揮手。")
		.Add("REFII_MP_HEARD_JOIN", "{0} would like you to join them.", "{0} aimerait que vous veniez jouer avec lui.",
			"{0} möchte, dass du dazukommst.", "{0} vorrebbe che ti unissi alla sua partita.",
			"{0} quiere que te unas a su partida.", "{0} зовёт вас к себе.", "{0} zaprasza cię do siebie.",
			"{0} tě zve k sobě.", "{0} szeretné, ha csatlakoznál hozzá.", "{0} が一緒に遊びたがっています。",
			"{0} 님이 함께하기를 원합니다.", "{0} 希望您加入。")
		.Add("REFII_MP_HEARD_THANKS", "{0} says thank you.", "{0} vous dit merci.", "{0} sagt Danke.",
			"{0} ti ringrazia.", "{0} te da las gracias.", "{0} благодарит вас.", "{0} dziękuje ci.", "{0} ti děkuje.",
			"{0} köszönetet mond.", "{0} がお礼を言っています。", "{0} 님이 고마워합니다.", "{0} 向您道謝。")
		.Add("REFII_MP_HEARD_WELL_MET", "{0} says well met, hero.", "{0} vous salue, Héros.", "{0} grüßt dich, Held.",
			"{0} ti saluta, Eroe.", "{0} te saluda, Héroe.", "{0} приветствует вас, Герой.",
			"{0} pozdrawia cię, Bohaterze.", "{0} tě zdraví, Hrdino.", "{0} üdvözöl, Hős.",
			"{0} があいさつしています、ヒーロー。", "{0} 님이 인사합니다, 영웅이여.", "{0} 向您致意，英雄。")
		.Add("REFII_MP_A_FRIEND", "A friend", "Un ami", "Ein Freund", "Un amico", "Un amigo", "Друг", "Znajomy",
			"Přítel", "Egy barát", "フレンド", "친구", "好友")
		.Add("REFII_MP_DISCORD_PLAYER", "A Discord player", "Un joueur Discord", "Ein Discord-Spieler",
			"Un giocatore Discord", "Un jugador de Discord", "Игрок Discord", "Gracz z Discorda", "Hráč z Discordu",
			"Egy Discord-játékos", "Discord のプレイヤー", "Discord 플레이어", "Discord 玩家")
		.Add("REFII_MP_STATUS_NOW", "Your status is now: {0}.", "Votre statut est désormais : {0}.",
			"Dein Status ist jetzt: {0}.", "Il tuo stato ora è: {0}.", "Tu estado ahora es: {0}.",
			"Ваш статус теперь: {0}.", "Twój status to teraz: {0}.", "Tvůj stav je teď: {0}.",
			"Az állapotod mostantól: {0}.", "ステータスを変更しました: {0}。", "내 상태가 변경되었습니다: {0}.",
			"您的狀態現在是：{0}。")
		.Add("REFII_DISPLAY", "Display", "Affichage", "Anzeige", "Schermo", "Pantalla", "Экран", "Obraz", "Zobrazení",
			"Megjelenítés", "ディスプレイ", "디스플레이", "顯示")
		.Add("REFII_DISPLAY_WINDOW_MODE", "Window Mode", "Mode d'affichage", "Fenstermodus", "Modalità finestra",
			"Modo de ventana", "Режим окна", "Tryb okna", "Režim okna", "Ablakmód", "ウィンドウモード", "창 모드", "視窗模式")
		.Add("REFII_DISPLAY_WINDOWED", "Windowed", "Fenêtré", "Fenster", "Finestra", "Ventana", "В окне", "Okno",
			"V okně", "Ablakos", "ウィンドウ", "창", "視窗")
		.Add("REFII_DISPLAY_BORDERLESS", "Borderless Window", "Fenêtre sans bordure", "Randloses Fenster",
			"Finestra senza bordi", "Ventana sin bordes", "Окно без рамки", "Okno bez ramek", "Okno bez okrajů",
			"Keret nélküli ablak", "ボーダーレスウィンドウ", "테두리 없는 창", "無邊框視窗")
		.Add("REFII_DISPLAY_FULLSCREEN", "Fullscreen", "Plein écran", "Vollbild", "Schermo intero", "Pantalla completa",
			"Полный экран", "Pełny ekran", "Celá obrazovka", "Teljes képernyő", "フルスクリーン", "전체 화면", "全螢幕")
		.Add("REFII_DISPLAY_RESOLUTION", "Resolution", "Résolution", "Auflösung", "Risoluzione", "Resolución",
			"Разрешение", "Rozdzielczość", "Rozlišení", "Felbontás", "解像度", "해상도", "解析度")
		.Add("REFII_DISPLAY_MATCH_WINDOW", "Match Window", "Taille de la fenêtre", "Fenstergröße", "Come la finestra",
			"Igual que la ventana", "По размеру окна", "Dopasuj do okna", "Podle okna", "Ablakhoz igazítva",
			"ウィンドウに合わせる", "창에 맞춤", "符合視窗")
		.Add("REFII_DISPLAY_VSYNC", "Vertical Sync", "Synchronisation verticale", "Vertikale Synchronisation",
			"Sincronizzazione verticale", "Sincronización vertical", "Вертикальная синхронизация",
			"Synchronizacja pionowa", "Vertikální synchronizace", "Függőleges szinkron", "垂直同期", "수직 동기화", "垂直同步")
		.Add("REFII_DISPLAY_VSYNC_MAILBOX", "Mailbox", "Mailbox", "Mailbox", "Mailbox", "Mailbox", "Mailbox", "Mailbox",
			"Mailbox", "Mailbox", "メールボックス", "메일박스", "信箱")
		.Add("REFII_DISPLAY_VSYNC_ADAPTIVE", "Adaptive", "Adaptative", "Adaptiv", "Adattiva", "Adaptativa", "Адаптивная",
			"Adaptacyjna", "Adaptivní", "Adaptív", "アダプティブ", "적응형", "自適應")
		.Add("REFII_DISPLAY_ON", "On", "Activé", "Ein", "Attivo", "Activado", "Вкл.", "Wł.", "Zapnuto", "Be", "オン", "켜기",
			"開啟")
		.Add("REFII_DISPLAY_OFF", "Off", "Désactivé", "Aus", "Disattivo", "Desactivado", "Выкл.", "Wył.", "Vypnuto", "Ki",
			"オフ", "끄기", "關閉")
		.Add("REFII_DISPLAY_FRAME_LIMIT", "Frame Rate Limit", "Limite d'images par seconde", "Bildratenbegrenzung",
			"Limite fotogrammi", "Límite de fotogramas", "Ограничение частоты кадров", "Limit klatek",
			"Omezení snímkové frekvence", "Képkockasebesség-korlát", "フレームレート上限", "프레임 속도 제한", "影格率上限")
		.Add("REFII_DISPLAY_UNCAPPED", "Uncapped", "Illimité", "Unbegrenzt", "Illimitato", "Sin límite", "Без ограничения",
			"Bez limitu", "Neomezeno", "Korlátlan", "上限なし", "제한 없음", "無上限");

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
		.Add("REFII_F2_COOP_ITEM_TO_PLAYER_ONE", "This quest item goes to Player One's Hero.",
			"Cet objet de quête revient au Héros du joueur 1.", "Dieser Questgegenstand geht an den Helden von Spieler 1.",
			"Questo oggetto della missione va all'Eroe del giocatore 1.", "Este objeto de misión es para el Héroe del jugador 1.",
			"Этот предмет задания достаётся Герою игрока 1.", "Ten przedmiot zadania trafia do Bohatera gracza 1.",
			"Tento úkolový předmět dostane Hrdina hráče 1.", "Ez a küldetéstárgy az 1-es játékos Hősét illeti.",
			"このクエストアイテムはプレイヤー 1 のヒーローのものになります。", "이 퀘스트 아이템은 플레이어 1의 영웅에게 갑니다.",
			"此任務物品歸玩家 1 的英雄所有。")
		.Add("REFII_F2_COOP_ITEM_OWN_COPY", "Your partner found this. You have your own.",
			"Votre partenaire l'a trouvé. Vous avez le vôtre.", "Dein Partner hat das gefunden. Du hast dein eigenes.",
			"L'ha trovato il tuo compagno. Tu hai il tuo.", "Lo ha encontrado tu compañero. Tú tienes el tuyo.",
			"Это нашёл ваш напарник. У вас есть свой экземпляр.", "Twój partner to znalazł. Ty masz własny egzemplarz.",
			"Tohle našel tvůj partner. Ty máš vlastní.", "Ezt a társad találta. Neked is megvan a sajátod.",
			"パートナーがこれを見つけました。あなたにも同じものがあります。", "파트너가 이것을 찾았습니다. 당신도 같은 것을 가집니다.",
			"這是您的夥伴找到的。您也有一份。")
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
		.Add("REFII_F2_COOP_ROLE_HOST", "host", "hôte", "Host", "host", "anfitrión", "хост", "gospodarz", "hostitel",
			"házigazda", "ホスト", "호스트", "主持人")
		.Add("REFII_F2_COOP_ROLE_JOINER", "joiner", "invité", "Gast", "ospite", "invitado", "гость", "gość",
			"připojený hráč", "vendég", "参加者", "참가자", "加入者")
		.Add("REFII_F2_COOP_REFUSED_UNSTATED",
			"Co-op refused: the {0}'s Refii does not state its mods, so it is an older build.\n\nBoth players need the same Refii build.",
			"Coopération refusée : le Refii de l'autre joueur ({0}) n'indique pas ses mods, c'est donc une version plus ancienne.\n\nLes deux joueurs doivent utiliser la même version de Refii.",
			"Koop abgelehnt: Das Refii des anderen Spielers ({0}) gibt seine Mods nicht an, es ist also eine ältere Version.\n\nBeide Spieler brauchen dieselbe Refii-Version.",
			"Cooperativa rifiutata: il Refii dell'altro giocatore ({0}) non dichiara le sue mod, quindi è una versione precedente.\n\nEntrambi i giocatori devono avere la stessa versione di Refii.",
			"Cooperativo rechazado: el Refii del otro jugador ({0}) no indica sus mods, así que es una versión anterior.\n\nLos dos jugadores necesitan la misma versión de Refii.",
			"Совместная игра отклонена: Refii другого игрока ({0}) не сообщает свои моды, значит, это более старая версия.\n\nОбоим игрокам нужна одна и та же версия Refii.",
			"Współpraca odrzucona: Refii drugiego gracza ({0}) nie podaje swoich modów, więc jest to starsza wersja.\n\nObaj gracze potrzebują tej samej wersji Refii.",
			"Spolupráce odmítnuta: Refii druhého hráče ({0}) neuvádí své módy, jde tedy o starší verzi.\n\nOba hráči potřebují stejnou verzi Refii.",
			"Kooperatív játék elutasítva: a másik játékos ({0}) Refiije nem közli a modjait, tehát régebbi verzió.\n\nMindkét játékosnak ugyanaz a Refii-verzió kell.",
			"協力プレイを拒否しました: 相手 ({0}) の Refii が MOD を通知しないため、古いバージョンです。\n\n両方のプレイヤーに同じバージョンの Refii が必要です。",
			"협동 게임이 거부되었습니다: 상대({0})의 Refii가 모드를 알리지 않으므로 이전 버전입니다.\n\n두 플레이어 모두 같은 버전의 Refii가 필요합니다.",
			"合作遊戲遭拒：對方 ({0}) 的 Refii 未聲明其模組，因此是較舊的版本。\n\n兩位玩家需要使用相同版本的 Refii。")
		.Add("REFII_F2_COOP_REFUSED_OLD_JOINER",
			"Co-op refused: your mods and the joiner's differ, and the joiner's Refii is an older build that cannot take yours on.\n\n{0}.",
			"Coopération refusée : vos mods et ceux de l'invité diffèrent, et le Refii de l'invité est une version plus ancienne qui ne peut pas reprendre les vôtres.\n\n{0}.",
			"Koop abgelehnt: Deine Mods und die des Gastes unterscheiden sich, und das Refii des Gastes ist eine ältere Version, die deine nicht übernehmen kann.\n\n{0}.",
			"Cooperativa rifiutata: le tue mod e quelle dell'ospite sono diverse, e il Refii dell'ospite è una versione precedente che non può adottare le tue.\n\n{0}.",
			"Cooperativo rechazado: tus mods y los del invitado son distintos, y el Refii del invitado es una versión anterior que no puede adoptar los tuyos.\n\n{0}.",
			"Совместная игра отклонена: ваши моды и моды гостя различаются, а Refii гостя — более старая версия, которая не может принять ваши.\n\n{0}.",
			"Współpraca odrzucona: twoje mody i mody gościa się różnią, a Refii gościa to starsza wersja, która nie może przejąć twoich.\n\n{0}.",
			"Spolupráce odmítnuta: tvoje módy a módy připojeného hráče se liší a jeho Refii je starší verze, která ty tvoje nedokáže převzít.\n\n{0}.",
			"Kooperatív játék elutasítva: a te modjaid és a vendég modjai eltérnek, a vendég Refiije pedig régebbi verzió, amely nem tudja átvenni a tieidet.\n\n{0}.",
			"協力プレイを拒否しました: あなたの MOD と参加者の MOD が異なり、参加者の Refii は古いバージョンのため、あなたの MOD を引き継げません。\n\n{0}.",
			"협동 게임이 거부되었습니다: 내 모드와 참가자의 모드가 다르며, 참가자의 Refii는 이전 버전이라 내 모드를 받아들일 수 없습니다.\n\n{0}.",
			"合作遊戲遭拒：您的模組與加入者的不同，而且加入者的 Refii 是較舊的版本，無法採用您的模組。\n\n{0}.")
		.Add("REFII_F2_COOP_REFUSED_JOINER_DECLINED",
			"Co-op refused: the joiner declined to run your mods for this session.",
			"Coopération refusée : l'invité a refusé d'utiliser vos mods pour cette session.",
			"Koop abgelehnt: Der Gast hat es abgelehnt, deine Mods für diese Sitzung zu verwenden.",
			"Cooperativa rifiutata: l'ospite ha rifiutato di usare le tue mod per questa sessione.",
			"Cooperativo rechazado: el invitado ha rechazado usar tus mods en esta sesión.",
			"Совместная игра отклонена: гость отказался использовать ваши моды в этой сессии.",
			"Współpraca odrzucona: gość odmówił używania twoich modów w tej sesji.",
			"Spolupráce odmítnuta: připojený hráč odmítl používat tvoje módy v této relaci.",
			"Kooperatív játék elutasítva: a vendég nem vállalta, hogy ebben a munkamenetben a te modjaidat futtassa.",
			"協力プレイを拒否しました: 参加者がこのセッションであなたの MOD を使うことを拒否しました。",
			"협동 게임이 거부되었습니다: 참가자가 이번 세션에서 내 모드를 사용하기를 거절했습니다.",
			"合作遊戲遭拒：加入者拒絕在本次遊戲階段使用您的模組。")
		.Add("REFII_F2_COOP_REFUSED_JOINER_CANNOT", "Co-op refused: the joiner cannot run your mods.\n\n{0}.",
			"Coopération refusée : l'invité ne peut pas utiliser vos mods.\n\n{0}.",
			"Koop abgelehnt: Der Gast kann deine Mods nicht verwenden.\n\n{0}.",
			"Cooperativa rifiutata: l'ospite non può usare le tue mod.\n\n{0}.",
			"Cooperativo rechazado: el invitado no puede usar tus mods.\n\n{0}.",
			"Совместная игра отклонена: гость не может использовать ваши моды.\n\n{0}.",
			"Współpraca odrzucona: gość nie może używać twoich modów.\n\n{0}.",
			"Spolupráce odmítnuta: připojený hráč nemůže používat tvoje módy.\n\n{0}.",
			"Kooperatív játék elutasítva: a vendég nem tudja futtatni a modjaidat.\n\n{0}.",
			"協力プレイを拒否しました: 参加者はあなたの MOD を使えません。\n\n{0}.",
			"협동 게임이 거부되었습니다: 참가자가 내 모드를 사용할 수 없습니다.\n\n{0}.",
			"合作遊戲遭拒：加入者無法使用您的模組。\n\n{0}.")
		.Add("REFII_F2_COOP_REFUSED_JOINER_SLOW", "Co-op refused: the joiner's mod sync did not finish in {0} minutes.",
			"Coopération refusée : la synchronisation des mods de l'invité ne s'est pas terminée en {0} minutes.",
			"Koop abgelehnt: Die Mod-Synchronisierung des Gastes wurde nicht innerhalb von {0} Minuten abgeschlossen.",
			"Cooperativa rifiutata: la sincronizzazione delle mod dell'ospite non è terminata entro {0} minuti.",
			"Cooperativo rechazado: la sincronización de mods del invitado no ha terminado en {0} minutos.",
			"Совместная игра отклонена: синхронизация модов гостя не завершилась за {0} мин.",
			"Współpraca odrzucona: synchronizacja modów gościa nie zakończyła się w ciągu {0} min.",
			"Spolupráce odmítnuta: synchronizace módů připojeného hráče neskončila do {0} min.",
			"Kooperatív játék elutasítva: a vendég modszinkronizálása nem fejeződött be {0} perc alatt.",
			"協力プレイを拒否しました: 参加者の MOD 同期が {0} 分以内に完了しませんでした。",
			"협동 게임이 거부되었습니다: 참가자의 모드 동기화가 {0}분 안에 끝나지 않았습니다.",
			"合作遊戲遭拒：加入者的模組同步未在 {0} 分鐘內完成。")
		.Add("REFII_F2_COOP_REFUSED_HOST_BLOCKED",
			"Co-op refused: the host's mods cannot be taken on for this session.\n\n{0}.\n\nA hard mod must be installed and enabled on both machines before the join.",
			"Coopération refusée : les mods de l'hôte ne peuvent pas être repris pour cette session.\n\n{0}.\n\nUn hard mod doit être installé et activé sur les deux machines avant de rejoindre la partie.",
			"Koop abgelehnt: Die Mods des Hosts können für diese Sitzung nicht übernommen werden.\n\n{0}.\n\nEin Hard Mod muss vor dem Beitritt auf beiden Rechnern installiert und aktiviert sein.",
			"Cooperativa rifiutata: le mod dell'host non possono essere adottate per questa sessione.\n\n{0}.\n\nUna hard mod deve essere installata e attiva su entrambi i computer prima di unirsi.",
			"Cooperativo rechazado: los mods del anfitrión no se pueden adoptar en esta sesión.\n\n{0}.\n\nUn hard mod debe estar instalado y activado en los dos equipos antes de unirse.",
			"Совместная игра отклонена: моды хоста нельзя принять для этой сессии.\n\n{0}.\n\nHard mod должен быть установлен и включён на обоих компьютерах до присоединения.",
			"Współpraca odrzucona: modów gospodarza nie można przejąć na tę sesję.\n\n{0}.\n\nHard mod musi być zainstalowany i włączony na obu komputerach przed dołączeniem.",
			"Spolupráce odmítnuta: módy hostitele nelze pro tuto relaci převzít.\n\n{0}.\n\nHard mod musí být před připojením nainstalovaný a zapnutý na obou počítačích.",
			"Kooperatív játék elutasítva: a házigazda modjai nem vehetők át erre a munkamenetre.\n\n{0}.\n\nA hard modot a csatlakozás előtt mindkét gépen telepíteni kell és be kell kapcsolni.",
			"協力プレイを拒否しました: このセッションではホストの MOD を引き継げません。\n\n{0}.\n\nhard mod は、参加する前に両方のマシンにインストールして有効にしておく必要があります。",
			"협동 게임이 거부되었습니다: 이번 세션에서는 호스트의 모드를 받아들일 수 없습니다.\n\n{0}.\n\nhard mod는 참가하기 전에 두 컴퓨터 모두에 설치하고 사용하도록 설정해야 합니다.",
			"合作遊戲遭拒：本次遊戲階段無法採用主持人的模組。\n\n{0}.\n\nhard mod 必須在加入前於兩台電腦上安裝並啟用。")
		.Add("REFII_F2_COOP_REFUSED_YOU_DECLINED",
			"You declined the host's mods, so the session was left.\n\nJoin again to be asked again.",
			"Vous avez refusé les mods de l'hôte, la session a donc été quittée.\n\nRejoignez de nouveau la partie pour que la question vous soit reposée.",
			"Du hast die Mods des Hosts abgelehnt, daher wurde die Sitzung verlassen.\n\nTritt erneut bei, um noch einmal gefragt zu werden.",
			"Hai rifiutato le mod dell'host, quindi la sessione è stata abbandonata.\n\nUnisciti di nuovo per ricevere ancora la domanda.",
			"Has rechazado los mods del anfitrión, así que se ha abandonado la sesión.\n\nÚnete de nuevo para que se te vuelva a preguntar.",
			"Вы отказались от модов хоста, поэтому сессия покинута.\n\nПрисоединитесь снова, чтобы получить вопрос ещё раз.",
			"Odrzucono mody gospodarza, więc sesja została opuszczona.\n\nDołącz ponownie, aby pytanie pojawiło się jeszcze raz.",
			"Módy hostitele byly odmítnuty, takže relace byla opuštěna.\n\nPřipoj se znovu a otázka se zobrazí ještě jednou.",
			"Elutasítottad a házigazda modjait, ezért a munkamenet véget ért számodra.\n\nCsatlakozz újra, hogy ismét megkérdezzen.",
			"ホストの MOD を拒否したため、セッションから退出しました。\n\nもう一度参加すると、再度確認されます。",
			"호스트의 모드를 거절했으므로 세션에서 나왔습니다.\n\n다시 참가하면 다시 묻습니다.",
			"您拒絕了主持人的模組，因此已離開本次遊戲階段。\n\n再次加入即可重新選擇。")
		.Add("REFII_F2_COOP_REFUSED_SYNC_FAILED", "Co-op refused: the host's mods could not be taken on.\n\n{0}.",
			"Coopération refusée : les mods de l'hôte n'ont pas pu être repris.\n\n{0}.",
			"Koop abgelehnt: Die Mods des Hosts konnten nicht übernommen werden.\n\n{0}.",
			"Cooperativa rifiutata: non è stato possibile adottare le mod dell'host.\n\n{0}.",
			"Cooperativo rechazado: no se han podido adoptar los mods del anfitrión.\n\n{0}.",
			"Совместная игра отклонена: моды хоста не удалось принять.\n\n{0}.",
			"Współpraca odrzucona: nie udało się przejąć modów gospodarza.\n\n{0}.",
			"Spolupráce odmítnuta: módy hostitele se nepodařilo převzít.\n\n{0}.",
			"Kooperatív játék elutasítva: a házigazda modjait nem sikerült átvenni.\n\n{0}.",
			"協力プレイを拒否しました: ホストの MOD を引き継げませんでした。\n\n{0}.",
			"협동 게임이 거부되었습니다: 호스트의 모드를 받아들이지 못했습니다.\n\n{0}.",
			"合作遊戲遭拒：無法採用主持人的模組。\n\n{0}.")
		.Add("REFII_F2_COOP_MODS_PARTNER_RUNNING", "Your partner is running your mods.",
			"Votre partenaire utilise vos mods.", "Dein Partner verwendet deine Mods.",
			"Il tuo compagno sta usando le tue mod.", "Tu compañero está usando tus mods.",
			"Ваш напарник использует ваши моды.", "Partner używa twoich modów.", "Tvůj partner používá tvoje módy.",
			"A társad a te modjaidat futtatja.", "パートナーがあなたの MOD を使用しています。",
			"파트너가 내 모드를 사용하고 있습니다.", "您的合作伙伴正在使用您的模組。")
		.Add("REFII_F2_COOP_MODS_PARTNER_RECEIVING", "Your partner is receiving your mods.",
			"Votre partenaire reçoit vos mods.", "Dein Partner empfängt deine Mods.",
			"Il tuo compagno sta ricevendo le tue mod.", "Tu compañero está recibiendo tus mods.",
			"Ваш напарник получает ваши моды.", "Partner odbiera twoje mody.", "Tvůj partner přijímá tvoje módy.",
			"A társad éppen megkapja a modjaidat.", "パートナーがあなたの MOD を受信しています。",
			"파트너가 내 모드를 받고 있습니다.", "您的合作伙伴正在接收您的模組。")
		.Add("REFII_F2_COOP_MODS_SUSPENDED", "Your mods are suspended for this session.",
			"Vos mods sont suspendus pour cette session.", "Deine Mods sind für diese Sitzung ausgesetzt.",
			"Le tue mod sono sospese per questa sessione.", "Tus mods están suspendidos durante esta sesión.",
			"Ваши моды приостановлены на эту сессию.", "Twoje mody są zawieszone na tę sesję.",
			"Tvoje módy jsou pro tuto relaci pozastaveny.", "A modjaid erre a munkamenetre fel vannak függesztve.",
			"このセッションの間、あなたの MOD は停止されます。", "이번 세션 동안 내 모드는 일시 중지됩니다.",
			"您的模組在本次遊戲階段暫停使用。")
		.Add("REFII_F2_COOP_MODS_RUNNING_OF", "Running {0}'s mods for this session.",
			"Les mods de {0} sont utilisés pour cette session.", "Für diese Sitzung laufen die Mods von {0}.",
			"Per questa sessione sono in uso le mod di {0}.", "En esta sesión se usan los mods de {0}.",
			"В этой сессии используются моды игрока {0}.", "W tej sesji działają mody gracza {0}.",
			"V této relaci běží módy hráče {0}.", "Ebben a munkamenetben {0} modjai futnak.",
			"このセッションでは {0} の MOD を使用します。", "이번 세션에서는 {0} 님의 모드를 사용합니다.",
			"本次遊戲階段使用 {0} 的模組。")
		.Add("REFII_F2_COOP_MODS_RUNNING_HOST", "Running the host's mods for this session.",
			"Les mods de l'hôte sont utilisés pour cette session.", "Für diese Sitzung laufen die Mods des Hosts.",
			"Per questa sessione sono in uso le mod dell'host.", "En esta sesión se usan los mods del anfitrión.",
			"В этой сессии используются моды хоста.", "W tej sesji działają mody gospodarza.",
			"V této relaci běží módy hostitele.", "Ebben a munkamenetben a házigazda modjai futnak.",
			"このセッションではホストの MOD を使用します。", "이번 세션에서는 호스트의 모드를 사용합니다.",
			"本次遊戲階段使用主持人的模組。")
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
		.Add("REFII_F2_COOP_MODE_HYBRID_SIDE", "Split When Apart: Side by Side", "Scission à distance : côte à côte",
			"Teilen bei Abstand: nebeneinander", "Dividi se lontani: affiancato", "Dividir al separarse: lado a lado",
			"Делить при удалении: рядом", "Dziel przy oddaleniu: obok siebie", "Dělit při vzdálení: vedle sebe",
			"Osztás távolodáskor: egymás mellett", "離れたら分割: 左右", "멀어지면 분할: 좌우", "分開時分割：左右")
		.Add("REFII_F2_COOP_MODE_HYBRID_STACKED", "Split When Apart: Top and Bottom", "Scission à distance : haut et bas",
			"Teilen bei Abstand: oben und unten", "Dividi se lontani: sopra e sotto", "Dividir al separarse: arriba y abajo",
			"Делить при удалении: сверху и снизу", "Dziel przy oddaleniu: góra i dół", "Dělit při vzdálení: nahoře a dole",
			"Osztás távolodáskor: alul és felül", "離れたら分割: 上下", "멀어지면 분할: 위아래", "分開時分割：上下")
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
			"無法移開存檔，因此未開始新遊戲：\n{0}")
		.Add("REFII_F2_CARD_JOIN",
			"Join {0}'s game?\n\nJoining leaves this one, and anything you have not saved is lost.",
			"Rejoindre la partie de {0} ?\n\nVous quitterez celle-ci et tout ce qui n'a pas été sauvegardé sera perdu.",
			"Dem Spiel von {0} beitreten?\n\nDu verlässt damit dieses Spiel, und alles nicht Gespeicherte geht verloren.",
			"Unirti alla partita di {0}?\n\nLascerai questa partita e tutto ciò che non hai salvato andrà perso.",
			"¿Unirte a la partida de {0}?\n\nSaldrás de esta y se perderá todo lo que no hayas guardado.",
			"Присоединиться к игре {0}?\n\nВы покинете эту игру, и всё несохранённое будет потеряно.",
			"Dołączyć do gry gracza {0}?\n\nOpuścisz tę grę, a wszystko, czego nie zapisano, przepadnie.",
			"Připojit se ke hře hráče {0}?\n\nTuto hru opustíš a vše neuložené bude ztraceno.",
			"Csatlakozol {0} játékához?\n\nEzt a játékot elhagyod, és minden, amit nem mentettél, elvész.",
			"{0} のゲームに参加しますか？\n\nこのゲームを終了し、セーブしていない内容は失われます。",
			"{0} 님의 게임에 참가하시겠습니까?\n\n이 게임에서 나가게 되며 저장하지 않은 내용은 사라집니다.",
			"要加入 {0} 的遊戲嗎？\n\n您將離開目前的遊戲，尚未儲存的進度將會遺失。")
		.Add("REFII_F2_CARD_INVITE",
			"{0} is not hosting a game. Invite them to join yours?",
			"{0} n'héberge pas de partie. L'inviter à rejoindre la vôtre ?",
			"{0} leitet kein Spiel. Zu deinem Spiel einladen?",
			"{0} non sta ospitando una partita. Vuoi invitarlo nella tua?",
			"{0} no está alojando una partida. ¿Quieres invitarle a la tuya?",
			"{0} не ведёт игру. Пригласить в вашу игру?",
			"{0} nie prowadzi gry. Zaprosić do twojej gry?",
			"{0} nehostuje žádnou hru. Pozvat do tvé hry?",
			"{0} nem vezet játékot. Meghívod a sajátodba?",
			"{0} はゲームをホストしていません。あなたのゲームに招待しますか？",
			"{0} 님은 게임을 호스트하고 있지 않습니다. 내 게임에 초대하시겠습니까?",
			"{0} 並未主持遊戲。要邀請對方加入您的遊戲嗎？")
		.Add("REFII_F2_CARD_PARTNER",
			"{0} is already playing in your game.",
			"{0} joue déjà dans votre partie.",
			"{0} spielt bereits in deinem Spiel.",
			"{0} sta già giocando nella tua partita.",
			"{0} ya está jugando en tu partida.",
			"{0} уже играет в вашей игре.",
			"{0} już gra w twojej grze.",
			"{0} už hraje ve tvé hře.",
			"{0} már a játékodban játszik.",
			"{0} はすでにあなたのゲームに参加しています。",
			"{0} 님은 이미 내 게임에서 플레이하고 있습니다.",
			"{0} 已在您的遊戲中。")
		.Add("REFII_F2_CARD_ACHIEVEMENTS",
			"Their hero: {0} of {1} achievements\nTheir profile: {2} of {1}",
			"Son héros : {0} succès sur {1}\nSon profil : {2} sur {1}",
			"Sein Held: {0} von {1} Erfolgen\nSein Profil: {2} von {1}",
			"Il suo eroe: {0} obiettivi su {1}\nIl suo profilo: {2} su {1}",
			"Su héroe: {0} de {1} logros\nSu perfil: {2} de {1}",
			"Его герой: {0} из {1} достижений\nЕго профиль: {2} из {1}",
			"Jego bohater: {0} z {1} osiągnięć\nJego profil: {2} z {1}",
			"Jeho hrdina: {0} z {1} úspěchů\nJeho profil: {2} z {1}",
			"Hőse: {0} / {1} teljesítmény\nProfilja: {2} / {1}",
			"相手のヒーロー: 実績 {1}個中{0}個\n相手のプロフィール: {1}中{2}",
			"상대 영웅: 업적 {1}개 중 {0}개\n상대 프로필: {1} 중 {2}",
			"對方的英雄：成就 {0}/{1}\n對方的設定檔：{2}/{1}")
		.Add("REFII_F2_CARD_NOTHING",
			"{0} is not hosting a game you can join right now.",
			"{0} n'héberge aucune partie que vous puissiez rejoindre pour le moment.",
			"{0} leitet gerade kein Spiel, dem du beitreten kannst.",
			"{0} non sta ospitando una partita a cui puoi unirti in questo momento.",
			"{0} no está alojando ninguna partida a la que puedas unirte ahora.",
			"{0} сейчас не ведёт игру, к которой можно присоединиться.",
			"{0} nie prowadzi teraz gry, do której możesz dołączyć.",
			"{0} teď nehostuje hru, ke které se můžeš připojit.",
			"{0} most nem vezet olyan játékot, amelyhez csatlakozhatsz.",
			"{0} は現在、参加できるゲームをホストしていません。",
			"{0} 님은 지금 참가할 수 있는 게임을 호스트하고 있지 않습니다.",
			"{0} 目前沒有主持您可加入的遊戲。")
		.Add("REFII_F2_CARD_UNREACHABLE",
			"That player cannot be reached right now.",
			"Ce joueur est injoignable pour le moment.",
			"Dieser Spieler ist gerade nicht erreichbar.",
			"Questo giocatore non è raggiungibile in questo momento.",
			"No se puede contactar con ese jugador ahora.",
			"С этим игроком сейчас нет связи.",
			"Z tym graczem nie można się teraz połączyć.",
			"Tohoto hráče teď nelze zastihnout.",
			"Ez a játékos most nem érhető el.",
			"現在、このプレイヤーには接続できません。",
			"지금은 해당 플레이어에게 연결할 수 없습니다.",
			"目前無法聯繫該玩家。")
		.Add("REFII_F2_CARD_JOIN_GAME",
			"Join Game",
			"Rejoindre la partie",
			"Spiel beitreten",
			"Unisciti alla partita",
			"Unirse a la partida",
			"Присоединиться к игре",
			"Dołącz do gry",
			"Připojit se ke hře",
			"Csatlakozás a játékhoz",
			"ゲームに参加",
			"게임 참가",
			"加入遊戲")
		.Add("REFII_F2_CARD_INVITE_TO_GAME",
			"Invite to Game",
			"Inviter dans la partie",
			"Zum Spiel einladen",
			"Invita alla partita",
			"Invitar a la partida",
			"Пригласить в игру",
			"Zaproś do gry",
			"Pozvat do hry",
			"Meghívás a játékba",
			"ゲームに招待",
			"게임에 초대",
			"邀請加入遊戲")
		.Add("REFII_F2_CARD_JOINING",
			"Joining {0}'s game. It loads when the host answers.",
			"Connexion à la partie de {0}. Elle se chargera quand l'hôte aura répondu.",
			"Beitritt zum Spiel von {0}. Es wird geladen, sobald der Host antwortet.",
			"Accesso alla partita di {0}. Verrà caricata quando l'host risponderà.",
			"Uniéndote a la partida de {0}. Se cargará cuando el anfitrión responda.",
			"Подключение к игре {0}. Она загрузится, когда хост ответит.",
			"Dołączanie do gry gracza {0}. Wczyta się, gdy gospodarz odpowie.",
			"Připojování ke hře hráče {0}. Načte se, až hostitel odpoví.",
			"Csatlakozás {0} játékához. Akkor töltődik be, amikor a házigazda válaszol.",
			"{0} のゲームに参加しています。ホストが応答するとロードされます。",
			"{0} 님의 게임에 참가하는 중입니다. 호스트가 응답하면 불러옵니다.",
			"正在加入 {0} 的遊戲。主持人回應後即會載入。")
		.Add("REFII_F2_CARD_INVITED",
			"Invitation sent to {0}.",
			"Invitation envoyée à {0}.",
			"Einladung an {0} gesendet.",
			"Invito inviato a {0}.",
			"Invitación enviada a {0}.",
			"Приглашение отправлено игроку {0}.",
			"Wysłano zaproszenie do gracza {0}.",
			"Pozvánka odeslána hráči {0}.",
			"Meghívó elküldve: {0}.",
			"{0} に招待を送信しました。",
			"{0} 님에게 초대를 보냈습니다.",
			"已向 {0} 送出邀請。")
		.Add("REFII_F2_CARD_NOT_INVITED",
			"{0} could not be invited right now.",
			"Impossible d'inviter {0} pour le moment.",
			"{0} konnte gerade nicht eingeladen werden.",
			"Impossibile invitare {0} in questo momento.",
			"No se ha podido invitar a {0} ahora.",
			"Сейчас не удалось пригласить игрока {0}.",
			"Nie udało się teraz zaprosić gracza {0}.",
			"Hráče {0} teď nebylo možné pozvat.",
			"{0} most nem hívható meg.",
			"現在、{0} を招待できませんでした。",
			"지금은 {0} 님을 초대할 수 없습니다.",
			"目前無法邀請 {0}。")
		.Add("REFII_F2_JOIN_ASK",
			"{0} would like to join your game. Do you accept?",
			"{0} souhaite rejoindre votre partie. Acceptez-vous ?",
			"{0} möchte deinem Spiel beitreten. Nimmst du an?",
			"{0} vorrebbe unirsi alla tua partita. Accetti?",
			"{0} quiere unirse a tu partida. ¿Aceptas?",
			"{0} хочет присоединиться к вашей игре. Принять?",
			"{0} chce dołączyć do twojej gry. Zgadzasz się?",
			"{0} se chce připojit k tvé hře. Přijímáš?",
			"{0} csatlakozna a játékodhoz. Elfogadod?",
			"{0} があなたのゲームへの参加を希望しています。承認しますか？",
			"{0} 님이 게임 참가를 요청했습니다. 수락하시겠습니까?",
			"{0} 想加入您的遊戲。您接受嗎？")
		.Add("REFII_F2_JOIN_ASK_LAPSED",
			"That request could not be answered; it may have lapsed.",
			"Impossible de répondre à cette demande ; elle a peut-être expiré.",
			"Diese Anfrage konnte nicht beantwortet werden; möglicherweise ist sie abgelaufen.",
			"Impossibile rispondere a questa richiesta; potrebbe essere scaduta.",
			"No se ha podido responder a esa solicitud; puede que haya caducado.",
			"Не удалось ответить на этот запрос; возможно, он истёк.",
			"Nie udało się odpowiedzieć na tę prośbę; mogła wygasnąć.",
			"Na tuto žádost nebylo možné odpovědět; možná vypršela.",
			"Erre a kérésre nem sikerült válaszolni; lehet, hogy lejárt.",
			"このリクエストに応答できませんでした。期限が切れた可能性があります。",
			"이 요청에 응답할 수 없습니다. 만료되었을 수 있습니다.",
			"無法回應該請求；它可能已經失效。")
		.Add("REFII_F2_JOIN_ASK_INVITED",
			"Invitation sent through Discord. They join when they accept it.",
			"Invitation envoyée via Discord. Le joueur rejoindra la partie dès qu'il l'aura acceptée.",
			"Einladung über Discord gesendet. Der Beitritt erfolgt, sobald sie angenommen wird.",
			"Invito inviato tramite Discord. Il giocatore si unirà quando lo accetterà.",
			"Invitación enviada por Discord. Se unirá cuando la acepte.",
			"Приглашение отправлено через Discord. Игрок присоединится, когда примет его.",
			"Zaproszenie wysłano przez Discord. Gracz dołączy, gdy je przyjmie.",
			"Pozvánka odeslána přes Discord. Hráč se připojí, až ji přijme.",
			"A meghívó elküldve a Discordon. A játékos akkor csatlakozik, amikor elfogadja.",
			"Discord で招待を送信しました。相手が承認すると参加します。",
			"Discord로 초대를 보냈습니다. 상대가 수락하면 참가합니다.",
			"已透過 Discord 送出邀請。對方接受後即會加入。");

	/// <summary>
	/// Fable 3's own words: the PC layout's Game Settings tabs and the defaults prompt (the 360 has
	/// neither); its PC settings pages otherwise say the shell's (<see cref="Shared"/>). The friends
	/// channel's and the chat channel's captions, which their .bgf draws as art and the text bank has
	/// no line for.
	/// </summary>
	public static readonly Table Fable3 = new Table()
		// Another player's card (Fable3GamerCard): its Join Game / Invite to Game rows and their outcomes, Fable 2's
		// card's wording; Join Game is the title's own TEXT_GUI_ORB_JOIN_GAME where its text has it.
		.Add("REFII_F3_CARD_JOIN_GAME",
			"Join Game",
			"Rejoindre la partie",
			"Spiel beitreten",
			"Unisciti alla partita",
			"Unirse a la partida",
			"Присоединиться к игре",
			"Dołącz do gry",
			"Připojit se ke hře",
			"Csatlakozás a játékhoz",
			"ゲームに参加",
			"게임 참가",
			"加入遊戲")
		.Add("REFII_F3_CARD_INVITE_TO_GAME",
			"Invite to Game",
			"Inviter dans la partie",
			"Zum Spiel einladen",
			"Invita alla partita",
			"Invitar a la partida",
			"Пригласить в игру",
			"Zaproś do gry",
			"Pozvat do hry",
			"Meghívás a játékba",
			"ゲームに招待",
			"게임에 초대",
			"邀請加入遊戲")
		.Add("REFII_F3_CARD_JOIN_WARNING",
			"Joining leaves this one, and anything you have not saved is lost.",
			"Vous quitterez celle-ci et tout ce qui n'a pas été sauvegardé sera perdu.",
			"Du verlässt damit dieses Spiel, und alles nicht Gespeicherte geht verloren.",
			"Lascerai questa partita e tutto ciò che non hai salvato andrà perso.",
			"Saldrás de esta y se perderá todo lo que no hayas guardado.",
			"Вы покинете эту игру, и всё несохранённое будет потеряно.",
			"Opuścisz tę grę, a wszystko, czego nie zapisano, przepadnie.",
			"Tuto hru opustíš a vše neuložené bude ztraceno.",
			"Ezt a játékot elhagyod, és minden, amit nem mentettél, elvész.",
			"このゲームを終了し、セーブしていない内容は失われます。",
			"이 게임에서 나가게 되며 저장하지 않은 내용은 사라집니다.",
			"您將離開目前的遊戲，尚未儲存的進度將會遺失。")
		.Add("REFII_F3_CARD_JOINING",
			"Joining {0}'s game. It loads when the host answers.",
			"Connexion à la partie de {0}. Elle se chargera quand l'hôte aura répondu.",
			"Beitritt zum Spiel von {0}. Es wird geladen, sobald der Host antwortet.",
			"Accesso alla partita di {0}. Verrà caricata quando l'host risponderà.",
			"Uniéndote a la partida de {0}. Se cargará cuando el anfitrión responda.",
			"Подключение к игре {0}. Она загрузится, когда хост ответит.",
			"Dołączanie do gry gracza {0}. Wczyta się, gdy gospodarz odpowie.",
			"Připojování ke hře hráče {0}. Načte se, až hostitel odpoví.",
			"Csatlakozás {0} játékához. Akkor töltődik be, amikor a házigazda válaszol.",
			"{0} のゲームに参加しています。ホストが応答するとロードされます。",
			"{0} 님의 게임에 참가하는 중입니다. 호스트가 응답하면 불러옵니다.",
			"正在加入 {0} 的遊戲。主持人回應後即會載入。")
		.Add("REFII_F3_CARD_NOT_JOINED",
			"{0}'s game could not be joined right now.",
			"Impossible de rejoindre la partie de {0} pour le moment.",
			"Dem Spiel von {0} konnte gerade nicht beigetreten werden.",
			"Impossibile unirsi alla partita di {0} in questo momento.",
			"No se ha podido unir a la partida de {0} ahora.",
			"Сейчас не удалось присоединиться к игре {0}.",
			"Nie udało się teraz dołączyć do gry gracza {0}.",
			"Ke hře hráče {0} se teď nepodařilo připojit.",
			"{0} játékához most nem sikerült csatlakozni.",
			"現在、{0} のゲームに参加できませんでした。",
			"지금은 {0} 님의 게임에 참가할 수 없습니다.",
			"目前無法加入 {0} 的遊戲。")
		.Add("REFII_F3_CARD_INVITED",
			"Invitation sent to {0}.",
			"Invitation envoyée à {0}.",
			"Einladung an {0} gesendet.",
			"Invito inviato a {0}.",
			"Invitación enviada a {0}.",
			"Приглашение отправлено игроку {0}.",
			"Wysłano zaproszenie do gracza {0}.",
			"Pozvánka odeslána hráči {0}.",
			"Meghívó elküldve: {0}.",
			"{0} に招待を送信しました。",
			"{0} 님에게 초대를 보냈습니다.",
			"已向 {0} 送出邀請。")
		.Add("REFII_F3_CARD_NOT_INVITED",
			"{0} could not be invited right now.",
			"Impossible d'inviter {0} pour le moment.",
			"{0} konnte gerade nicht eingeladen werden.",
			"Impossibile invitare {0} in questo momento.",
			"No se ha podido invitar a {0} ahora.",
			"Сейчас не удалось пригласить игрока {0}.",
			"Nie udało się teraz zaprosić gracza {0}.",
			"Hráče {0} teď nebylo možné pozvat.",
			"{0} most nem hívható meg.",
			"現在、{0} を招待できませんでした。",
			"지금은 {0} 님을 초대할 수 없습니다.",
			"目前無法邀請 {0}。")
		.Add("REFII_F3_TAB_DISPLAY", "Display", "Affichage", "Anzeige", "Schermo", "Pantalla", "Экран", "Obraz",
			"Zobrazení", "Megjelenítés", "ディスプレイ", "디스플레이", "顯示")
		.Add("REFII_F3_TAB_CONTROLLER", "Controller", "Manette", "Controller", "Controller", "Mando", "Контроллер",
			"Kontroler", "Ovladač", "Kontroller", "コントローラー", "컨트롤러", "控制器")
		.Add("REFII_F3_TAB_AUDIO", "Audio", "Audio", "Audio", "Audio", "Audio", "Звук", "Dźwięk", "Zvuk", "Hang",
			"オーディオ", "오디오", "音訊")
		.Add("REFII_F3_TAB_MISC", "Misc", "Divers", "Sonstiges", "Varie", "Varios", "Разное", "Różne", "Různé",
			"Egyéb", "その他", "기타", "其他")
		.Add("REFII_F3_DEFAULTS", "Defaults", "Par défaut", "Standard", "Predefinite", "Predeterminado",
			"По умолчанию", "Domyślne", "Výchozí", "Alapértelmezés", "初期設定", "기본값", "預設值")
		.Add("REFII_F3_FRIENDS_CHANNEL", "Friends Channel", "Canal des amis", "Freundekanal", "Canale amici",
			"Canal de amigos", "Канал друзей", "Kanał znajomych", "Kanál přátel", "Barátok csatorna", "フレンドチャンネル",
			"친구 채널", "好友頻道")
		.Add("REFII_F3_JOIN_PARTY", "Join a Party", "Rejoindre un groupe", "Party beitreten", "Unisciti a un party",
			"Unirse a un grupo", "Присоединиться к команде", "Dołącz do imprezy", "Připojit se k partě",
			"Csatlakozás partihoz", "パーティーに参加", "파티 참가", "加入派對")
		.Add("REFII_F3_INVITE_PARTY", "Invite into Party", "Inviter dans le groupe", "Zur Party einladen",
			"Invita nel party", "Invitar al grupo", "Пригласить в команду", "Zaproś na imprezę", "Pozvat do party",
			"Meghívás partiba", "パーティーに招待", "파티에 초대", "邀請加入派對")
		.Add("REFII_F3_CHAT_CHANNEL", "Chat Channel", "Canal de discussion", "Chatkanal", "Canale chat",
			"Canal de chat", "Голосовой канал", "Kanał czatu", "Kanál chatu", "Csevegőcsatorna", "チャットチャンネル",
			"채팅 채널", "聊天頻道")
		.Add("REFII_F3_CHAT_MUTE", "Mute", "Muet", "Stumm", "Muto", "Silenciar", "Без звука", "Wycisz", "Ztlumit",
			"Némítás", "ミュート", "음소거", "靜音")
		.Add("REFII_F3_CHAT_CURRENT", "Currently set to:", "Réglage actuel :", "Aktuelle Einstellung:",
			"Impostazione attuale:", "Ajuste actual:", "Сейчас выбрано:", "Obecne ustawienie:", "Aktuální nastavení:",
			"Jelenlegi beállítás:", "現在の設定:", "현재 설정:", "目前設定：")
		.Add("REFII_F3_COOP_SCREEN", "Local Co-op Screen", "Écran du co-op local", "Bildschirm im lokalen Koop",
			"Schermo co-op locale", "Pantalla del cooperativo local", "Экран локального кооператива",
			"Ekran lokalnej kooperacji", "Obrazovka místní kooperace", "Helyi kooperatív képernyő",
			"ローカル協力プレイの画面", "로컬 협동 화면", "本機合作畫面")
		.Add("REFII_F3_COOP_MODE_SHARED", "Shared Screen", "Écran partagé", "Geteilter Bildschirm", "Schermo condiviso",
			"Pantalla compartida", "Общий экран", "Wspólny ekran", "Sdílená obrazovka", "Közös képernyő", "共有画面",
			"공유 화면", "共用畫面")
		.Add("REFII_F3_COOP_MODE_SIDE", "Split Screen: Side by Side", "Écran scindé : côte à côte",
			"Geteilter Bildschirm: nebeneinander", "Schermo diviso: affiancato", "Pantalla dividida: lado a lado",
			"Разделённый экран: рядом", "Podzielony ekran: obok siebie", "Rozdělená obrazovka: vedle sebe",
			"Osztott képernyő: egymás mellett", "画面分割: 左右", "화면 분할: 좌우", "分割畫面：左右")
		.Add("REFII_F3_COOP_MODE_STACKED", "Split Screen: Top and Bottom", "Écran scindé : haut et bas",
			"Geteilter Bildschirm: oben und unten", "Schermo diviso: sopra e sotto", "Pantalla dividida: arriba y abajo",
			"Разделённый экран: сверху и снизу", "Podzielony ekran: góra i dół", "Rozdělená obrazovka: nahoře a dole",
			"Osztott képernyő: alul és felül", "画面分割: 上下", "화면 분할: 위아래", "分割畫面：上下")
		.Add("REFII_F3_COOP_MODE_HYBRID_SIDE", "Split When Apart: Side by Side", "Scission à distance : côte à côte",
			"Teilen bei Abstand: nebeneinander", "Dividi se lontani: affiancato", "Dividir al separarse: lado a lado",
			"Делить при удалении: рядом", "Dziel przy oddaleniu: obok siebie", "Dělit při vzdálení: vedle sebe",
			"Osztás távolodáskor: egymás mellett", "離れたら分割: 左右", "멀어지면 분할: 좌우", "分開時分割：左右")
		.Add("REFII_F3_COOP_MODE_HYBRID_STACKED", "Split When Apart: Top and Bottom", "Scission à distance : haut et bas",
			"Teilen bei Abstand: oben und unten", "Dividi se lontani: sopra e sotto", "Dividir al separarse: arriba y abajo",
			"Делить при удалении: сверху и снизу", "Dziel przy oddaleniu: góra i dół", "Dělit při vzdálení: nahoře a dole",
			"Osztás távolodáskor: alul és felül", "離れたら分割: 上下", "멀어지면 분할: 위아래", "分開時分割：上下");
}
