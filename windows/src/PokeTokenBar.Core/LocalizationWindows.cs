namespace PokeTokenBar.Core;

/// <summary>
/// Windows wording for the few <c>Localization.swift</c> strings that describe macOS mechanisms
/// (Keychain, "this Mac", unencrypted key file). <c>Localization.cs</c> is generated from the Swift
/// table and pinned to it by the parity tests, so the Windows UI uses these <c>Win*</c> members instead.
/// </summary>
public sealed partial class L
{
    /// <summary>The session key is DPAPI-encrypted for the Windows user (macOS: owner-only, not encrypted).</summary>
    public string WinSessionKeyStorageNote => T(
        "키는 이 PC의 앱 폴더에 Windows 사용자 계정으로 암호화(DPAPI)되어 저장됩니다. 브라우저에서 로그아웃하면 즉시 무효화됩니다.",
        "The key is stored in this PC's app folder, encrypted for your Windows account (DPAPI). Logging out in your browser invalidates it immediately.",
        "キーはこの PC のアプリフォルダに、Windows アカウント用に暗号化(DPAPI)して保存されます。ブラウザでログアウトすると即時無効になります。",
        "La clave se guarda en la carpeta de la app de este PC, cifrada para tu cuenta de Windows (DPAPI). Al cerrar sesión en el navegador se invalida de inmediato.",
        "La clé est enregistrée dans le dossier de l'app sur ce PC, chiffrée pour ton compte Windows (DPAPI). Te déconnecter dans le navigateur l'invalide immédiatement.",
        "A chave fica na pasta do app neste PC, criptografada para sua conta do Windows (DPAPI). Sair da conta no navegador a invalida na hora.",
        "Der Schlüssel liegt im App-Ordner dieses PCs, verschlüsselt für dein Windows-Konto (DPAPI). Meldest du dich im Browser ab, wird er sofort ungültig.",
        "Ключ хранится в папке приложения на этом ПК, зашифрованный для вашей учётной записи Windows (DPAPI). Выход из аккаунта в браузере сразу делает его недействительным.");

    public string WinSessionKeyHint => T(
        "Claude Code 로그인 정보 없이 공식 한도를 조회합니다. 브라우저 개발자도구 → Application → Cookies → claude.ai → sessionKey 값을 붙여넣으세요.",
        "Fetches official limits without the Claude Code login. Paste the value from DevTools → Application → Cookies → claude.ai → sessionKey.",
        "Claude Code のログイン情報なしで公式上限を取得します。開発者ツール → Application → Cookies → claude.ai → sessionKey の値を貼り付けてください。",
        "Obtiene los límites oficiales sin el inicio de sesión de Claude Code. Pega el valor de DevTools → Application → Cookies → claude.ai → sessionKey.",
        "Récupère les limites officielles sans la connexion Claude Code. Colle la valeur depuis DevTools → Application → Cookies → claude.ai → sessionKey.",
        "Busca os limites oficiais sem o login do Claude Code. Cole o valor de DevTools → Application → Cookies → claude.ai → sessionKey.",
        "Ruft offizielle Limits ohne die Claude-Code-Anmeldung ab. Füge den Wert aus DevTools → Application → Cookies → claude.ai → sessionKey ein.",
        "Загружает официальные лимиты без входа в Claude Code. Вставьте значение из DevTools → Application → Cookies → claude.ai → sessionKey.");

    /// <summary>The tray holds an icon only, so the "menu bar" items show in the tray tooltip (macOS: "Show in menu bar").</summary>
    public string WinMenuBarSectionTitle => T(
        "트레이 툴팁에 표시",
        "Show in tray tooltip",
        "トレイのツールチップに表示",
        "Mostrar en la información de la bandeja",
        "Afficher dans l'info-bulle de la zone de notification",
        "Mostrar na dica da bandeja",
        "Im Tray-Tooltip anzeigen",
        "Показывать в подсказке значка в трее");

    /// <summary>The tray always shows the icon alone; with every item off the tooltip shows only the app name
    /// (macOS: "All off shows only the character").</summary>
    public string WinAllOffHint => T(
        "전부 끄면 툴팁에 앱 이름만 표시됩니다",
        "All off shows only the app name in the tooltip",
        "すべてオフにするとツールチップにはアプリ名だけが表示されます",
        "Con todo desactivado, la información solo muestra el nombre de la app",
        "Tout désactivé, l'info-bulle n'affiche que le nom de l'app",
        "Com tudo desligado, a dica mostra só o nome do app",
        "Alles aus zeigt im Tooltip nur den App-Namen",
        "Если всё выключить, в подсказке останется только название приложения");

    /// <summary>Windows reads <c>~/.claude/.credentials.json</c>; there is no prompt to avoid.</summary>
    public string WinOnlyOnPress => T(
        "Claude Code 로그인 파일(~/.claude/.credentials.json)을 지금 다시 읽어 한도를 갱신해요. 토큰이 만료됐다면 Claude Code 에 다시 로그인한 뒤 누르세요",
        "Re-reads the Claude Code login file (~/.claude/.credentials.json) now and refreshes limits. If the token expired, sign in to Claude Code again first",
        "Claude Code のログインファイル(~/.claude/.credentials.json)を今すぐ読み直して上限を更新します。トークン期限切れの場合は先に Claude Code へ再ログインしてください",
        "Vuelve a leer ahora el archivo de inicio de sesión de Claude Code (~/.claude/.credentials.json) y actualiza los límites. Si el token expiró, inicia sesión de nuevo en Claude Code primero",
        "Relit maintenant le fichier de connexion de Claude Code (~/.claude/.credentials.json) et actualise les limites. Si le token a expiré, reconnecte-toi d'abord à Claude Code",
        "Lê de novo agora o arquivo de login do Claude Code (~/.claude/.credentials.json) e atualiza os limites. Se o token expirou, entre de novo no Claude Code antes",
        "Liest die Claude-Code-Anmeldedatei (~/.claude/.credentials.json) jetzt neu ein und aktualisiert die Limits. Ist das Token abgelaufen, melde dich zuerst erneut bei Claude Code an",
        "Заново читает файл входа Claude Code (~/.claude/.credentials.json) и обновляет лимиты. Если токен истёк, сначала снова войдите в Claude Code");

    public string WinAdditionalClaudeAccountsHint => T(
        "다른 Claude Code 로그인(CLAUDE_CONFIG_DIR)은 ~/.claude-* 폴더와 CLAUDE_CONFIG_DIR 환경 변수에서 자동으로 찾습니다. 다른 위치의 설정 폴더만 여기 추가하세요(콤마·줄바꿈 구분). 공식 한도에 계정마다 탭이 생깁니다.",
        "Other Claude Code logins (CLAUDE_CONFIG_DIR) are detected in ~/.claude-* folders and from the CLAUDE_CONFIG_DIR environment variable. Add config folders stored elsewhere here, comma/newline separated. Each account gets its own tab in the official limits.",
        "他のClaude Codeログイン(CLAUDE_CONFIG_DIR)は ~/.claude-* フォルダと環境変数 CLAUDE_CONFIG_DIR から自動検出されます。別の場所にある設定フォルダだけをここに追加してください(カンマ・改行区切り)。公式上限にアカウントごとのタブが追加されます。",
        "Los otros inicios de sesión de Claude Code (CLAUDE_CONFIG_DIR) se detectan en las carpetas ~/.claude-* y en la variable de entorno CLAUDE_CONFIG_DIR. Añade aquí las carpetas de configuración guardadas en otro lugar, separadas por coma o salto de línea. Cada cuenta tiene su pestaña en los límites oficiales.",
        "Les autres connexions Claude Code (CLAUDE_CONFIG_DIR) sont détectées dans les dossiers ~/.claude-* et via la variable d'environnement CLAUDE_CONFIG_DIR. Ajoute ici les dossiers de config rangés ailleurs, séparés par des virgules ou des retours à la ligne. Chaque compte a son onglet dans les limites officielles.",
        "Os outros logins do Claude Code (CLAUDE_CONFIG_DIR) são detectados nas pastas ~/.claude-* e na variável de ambiente CLAUDE_CONFIG_DIR. Adicione aqui pastas de configuração guardadas em outro lugar, separadas por vírgula ou quebra de linha. Cada conta ganha sua aba nos limites oficiais.",
        "Weitere Claude-Code-Anmeldungen (CLAUDE_CONFIG_DIR) werden in ~/.claude-*-Ordnern und über die Umgebungsvariable CLAUDE_CONFIG_DIR erkannt. Füge hier Konfigurationsordner an anderen Orten hinzu, durch Kommas oder Zeilenumbrüche getrennt. Jedes Konto bekommt bei den offiziellen Limits einen eigenen Tab.",
        "Другие входы в Claude Code (CLAUDE_CONFIG_DIR) находятся автоматически в папках ~/.claude-* и по переменной окружения CLAUDE_CONFIG_DIR. Папки конфигурации в других местах добавьте здесь — через запятую или с новой строки. У каждого аккаунта своя вкладка в официальных лимитах.");

    /// <summary>A save moves between any two machines — Mac or PC.</summary>
    public string WinImportSaveHint => T(
        "다른 PC나 Mac에서 내보낸 파일을 골라 이 PC에서 이어서 키워요",
        "Pick a file exported from another PC or Mac and continue here",
        "他のPCやMacから書き出したファイルを選んでこのPCで続けます",
        "Elige un archivo exportado desde otro PC o Mac y continúa aquí",
        "Choisis un fichier exporté depuis un autre PC ou Mac et continue ici",
        "Escolha um arquivo exportado de outro PC ou Mac e continue por aqui",
        "Wähle eine auf einem anderen PC oder Mac exportierte Datei aus und mach hier weiter",
        "Выберите файл, экспортированный на другом ПК или Mac, и продолжайте здесь");

    /// <summary>Windows-only switch for the run to the cursor on an agent done (macOS always runs).</summary>
    public string WinFloatingPetAgentRunLabel => T(
        "작업이 끝나면 커서로 달려가기",
        "Run to the cursor when an agent finishes",
        "作業が終わるとカーソルへ駆け寄る",
        "Correr hacia el cursor al terminar",
        "Courir vers le curseur à la fin",
        "Correr até o cursor ao terminar",
        "Nach Abschluss zum Mauszeiger laufen",
        "Подбегать к курсору по завершении");
}
