using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

// ------------------------------------------------------------------ //
// PROTOCOLE LOBBY : messages typés + validation de chaque champ entrant
// ------------------------------------------------------------------ //

// Sérialisé en snake_case : IceCandidate ↔ "ice_candidate", ListRooms ↔ "list_rooms"...
enum MsgType
{
    // Client → serveur
    Join, Leave, Chat, Offer, Answer, IceCandidate, ListRooms, Ping,
    JoinDecision,          // owner : accepte / refuse une demande d'entrée
    RoomSettings,          // owner : change visibilité, nombre de places
    // Serveur → client
    RoomInfo, PeerJoined, PeerLeft, OwnerChanged, Rooms, Pong, Error,
    JoinPending,           // demandeur : en attente de la réponse de l'owner
    JoinRequest,           // owner : un joueur demande à entrer (son client accepte seul ou laisse le jeu décider)
    JoinRequestCancelled,  // owner : le demandeur est parti ou la demande a expiré
    JoinRejected,          // demandeur : refusé (ou demande expirée)
    RoomUpdated,           // room : réglages modifiés par l'owner
}

// public  : listée par list_rooms
// private : absente de la liste ; rejoindre demande le code d'invitation généré par le serveur
enum RoomVisibility { Public, Private }

// Message reçu d'un client : seuls les champs qu'un client a le droit d'envoyer.
// Les autres champs présents dans le JSON (le client Unity envoie tout son Msg, nulls compris) sont ignorés.
sealed class InMsg
{
    [JsonPropertyName("type")]        public MsgType?        Type       { get; init; }
    [JsonPropertyName("name")]        public string?         Name       { get; init; }
    [JsonPropertyName("room")]        public string?         Room       { get; init; }
    [JsonPropertyName("text")]        public string?         Text       { get; init; }
    [JsonPropertyName("to")]          public string?         To         { get; init; }
    [JsonPropertyName("sdp")]         public string?         Sdp        { get; init; }
    [JsonPropertyName("candidate")]   public string?         Candidate  { get; init; }
    [JsonPropertyName("ts")]          public long?           Timestamp  { get; init; }
    // join (création de room) et room_settings
    [JsonPropertyName("visibility")]  public RoomVisibility? Visibility { get; init; }
    [JsonPropertyName("max_players")] public int?            MaxPlayers { get; init; }
    // join : code d'invitation (room privée) ou jeton de reconnexion
    [JsonPropertyName("code")]        public string?         Code       { get; init; }
    [JsonPropertyName("token")]       public string?         Token      { get; init; }
    // join_decision
    [JsonPropertyName("accept")]      public bool?           Accept     { get; init; }
}

// Room publique telle que listée par list_rooms
sealed record RoomSummary(
    [property: JsonPropertyName("name")]        string   Name,
    [property: JsonPropertyName("players")]     int      Players,
    [property: JsonPropertyName("max_players")] int      MaxPlayers,
    [property: JsonPropertyName("owner")]       string?  Owner);

// Message envoyé aux clients (les champs null ne sont pas sérialisés)
sealed class OutMsg
{
    [JsonPropertyName("type")]        public MsgType            Type       { get; init; }
    [JsonPropertyName("room")]        public string?            Room       { get; init; }
    [JsonPropertyName("name")]        public string?            Name       { get; init; }
    [JsonPropertyName("text")]        public string?            Text       { get; init; }
    [JsonPropertyName("rooms")]       public List<RoomSummary>? Rooms      { get; init; }
    [JsonPropertyName("ts")]          public long?              Timestamp  { get; init; }
    [JsonPropertyName("sdp")]         public string?            Sdp        { get; init; }
    [JsonPropertyName("to")]          public string?            To         { get; init; }
    [JsonPropertyName("from")]        public string?            From       { get; init; }
    [JsonPropertyName("candidate")]   public string?            Candidate  { get; init; }
    [JsonPropertyName("owner")]       public string?            Owner      { get; init; }
    [JsonPropertyName("ice_servers")] public List<IceServer>?   IceServers { get; init; }
    [JsonPropertyName("visibility")]  public RoomVisibility?    Visibility { get; init; }
    [JsonPropertyName("max_players")] public int?               MaxPlayers { get; init; }
    [JsonPropertyName("players")]     public int?               Players    { get; init; }
    [JsonPropertyName("code")]        public string?            Code       { get; init; }   // envoyé aux membres d'une room privée
    [JsonPropertyName("token")]       public string?            Token      { get; init; }   // jeton de reconnexion, propre à ce joueur
}

static partial class Protocol
{
    public const int MaxNameLength      = 32;
    public const int MaxRoomLength      = 48;
    public const int MaxTextLength      = 512;
    public const int MaxSdpLength       = 8 * 1024;   // un SDP SIPSorcery fait ~700 octets
    public const int MaxSdpLines        = 100;
    public const int MaxCandidateLength = 256;
    public const int MinPlayers         = 2;
    public const int MaxPlayers         = 16;

    // Toutes les enums en snake_case ; un entier ou une valeur inconnue est rejeté
    static readonly JsonStringEnumConverter TypeConverter =
        new(JsonNamingPolicy.SnakeCaseLower, allowIntegerValues: false);

    static readonly JsonSerializerOptions JsonIn = new()
    {
        MaxDepth                 = 4,
        AllowDuplicateProperties = false,   // {"to":"A","to":"B"} rejeté
        Converters               = { TypeConverter },
    };

    static readonly JsonSerializerOptions JsonOut = new()
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters             = { TypeConverter },
    };

    // null si le JSON est invalide, mal typé ou si le type est inconnu
    public static InMsg? Parse(string json)
    {
        try   { return JsonSerializer.Deserialize<InMsg>(json, JsonIn); }
        catch (JsonException) { return null; }
    }

    public static byte[] Serialize(OutMsg msg) =>
        Encoding.UTF8.GetBytes(JsonSerializer.Serialize(msg, JsonOut));

    // ------------------------------------------------------------------ //
    // VALIDATION : retourne null si OK, sinon la raison (texte constant, jamais l'entrée du client)
    // ------------------------------------------------------------------ //

    public static string? Validate(InMsg m) => m.Type switch
    {
        MsgType.Join                    => CheckName(m.Name, "name") ?? CheckRoom(m.Room) ?? CheckMaxPlayers(m.MaxPlayers)
                                           ?? CheckCode(m.Code) ?? CheckToken(m.Token),
        MsgType.Chat                    => CheckText(m.Text),
        MsgType.Offer or MsgType.Answer => CheckName(m.To, "to") ?? CheckSdp(m.Sdp),
        MsgType.IceCandidate            => CheckName(m.To, "to") ?? CheckCandidate(m.Candidate),
        MsgType.JoinDecision            => CheckName(m.Name, "name") ?? (m.Accept == null ? "accept manquant" : null),
        MsgType.RoomSettings            => m.Visibility == null && m.MaxPlayers == null
                                               ? "aucun réglage" : CheckMaxPlayers(m.MaxPlayers),
        MsgType.Leave or MsgType.ListRooms or MsgType.Ping => null,
        null                            => "type manquant",
        _                               => "type réservé au serveur",
    };

    // Code d'invitation : 8 caractères base32 (A-Z, 2-7), généré par le serveur
    [GeneratedRegex(@"^[A-Z2-7]{8}$")]
    private static partial Regex CodeRegex();

    // Jeton de reconnexion : 16 octets aléatoires en base64url
    [GeneratedRegex(@"^[A-Za-z0-9_-]{22}$")]
    private static partial Regex TokenRegex();

    static string? CheckMaxPlayers(int? n) =>
        n is null or (>= MinPlayers and <= MaxPlayers) ? null : $"max_players invalide ({MinPlayers} à {MaxPlayers})";

    static string? CheckCode(string? s) =>
        s == null || CodeRegex().IsMatch(s) ? null : "code invalide";

    static string? CheckToken(string? s) =>
        s == null || TokenRegex().IsMatch(s) ? null : "token invalide";

    // Lettres ASCII, chiffres, '_' et '-' : pas d'espace, de ':' ni de caractère de contrôle.
    // Le client découpe "nom: texte" sur ':' et la liste des rooms sur ' ' : ces caractères sont donc interdits.
    [GeneratedRegex(@"^[A-Za-z0-9_-]+$")]
    private static partial Regex IdentifierRegex();

    // Candidat ICE tel que produit par SIPSorcery :
    //   "<foundation> <composant> <udp|tcp> <priorité> <adresse> <port> typ <host|srflx|prflx|relay> [raddr x rport y] [generation n]"
    [GeneratedRegex(@"^(candidate:)?[A-Za-z0-9+/]{1,32} [0-9]{1,3} (udp|tcp) [0-9]{1,10} [A-Za-z0-9.:-]{1,64} [0-9]{1,5} typ (host|srflx|prflx|relay)( [A-Za-z0-9.:-]{1,64})*$",
        RegexOptions.IgnoreCase)]
    private static partial Regex CandidateRegex();

    // Ligne SDP : "<lettre>=<ASCII imprimable>"
    [GeneratedRegex(@"^[a-z]=[\x20-\x7E]*$")]
    private static partial Regex SdpLineRegex();

    public static string? CheckName(string? s, string field) =>
        s == null || s.Length == 0 || s.Length > MaxNameLength || !IdentifierRegex().IsMatch(s)
            ? $"{field} invalide ([A-Za-z0-9_-], 1 à {MaxNameLength} caractères)"
            : null;

    static string? CheckRoom(string? s) =>
        s == null || s.Length == 0 || s.Length > MaxRoomLength || !IdentifierRegex().IsMatch(s)
            ? $"room invalide ([A-Za-z0-9_-], 1 à {MaxRoomLength} caractères)"
            : null;

    // Texte libre (accents, emoji OK) mais sans caractère de contrôle, de format (bidi, zero-width) ni séparateur de ligne
    static string? CheckText(string? s)
    {
        if (s == null || s.Length == 0 || s.Length > MaxTextLength)
            return $"text invalide (1 à {MaxTextLength} caractères)";
        foreach (var r in s.EnumerateRunes())
        {
            if (r == Rune.ReplacementChar) return "text invalide (UTF-16 mal formé)";
            switch (Rune.GetUnicodeCategory(r))
            {
                case System.Globalization.UnicodeCategory.Control:
                case System.Globalization.UnicodeCategory.Format:
                case System.Globalization.UnicodeCategory.LineSeparator:
                case System.Globalization.UnicodeCategory.ParagraphSeparator:
                case System.Globalization.UnicodeCategory.PrivateUse:
                    return "text invalide (caractère interdit)";
            }
        }
        return null;
    }

    static string? CheckSdp(string? s)
    {
        if (s == null || s.Length > MaxSdpLength || !s.StartsWith("v=0\r\n", StringComparison.Ordinal))
            return "sdp invalide";
        var lines = s.Split("\r\n");
        if (lines.Length > MaxSdpLines) return "sdp invalide (trop de lignes)";
        for (int i = 0; i < lines.Length; i++)
        {
            if (lines[i].Length == 0 && i == lines.Length - 1) continue;   // CRLF final
            if (!SdpLineRegex().IsMatch(lines[i])) return "sdp invalide (ligne mal formée)";
        }
        return null;
    }

    static string? CheckCandidate(string? s) =>
        s == null || s.Length > MaxCandidateLength || !CandidateRegex().IsMatch(s)
            ? "candidate invalide"
            : null;
}
