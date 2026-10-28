using System.Net;
using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Text;

// Connexion d'un joueur (une par WebSocket). Les champs mutables sont protégés par Lobby.Lock.
sealed class Conn(WebSocket ws, IPAddress ip)
{
    public WebSocket Ws { get; } = ws;
    public IPAddress Ip { get; } = ip;

    public string?  Name;           // nom effectif dans la room (peut différer du nom demandé : Bob → Bob_2)
    public Room?    Room;           // room dont il est membre
    public Room?    PendingRoom;    // room où il attend la validation de l'owner
    public string?  PendingName;
    public DateTime PendingSince;
    public int      BadCodes;       // codes d'invitation faux envoyés
    public readonly Dictionary<string, DateTime> RejectedUntil = [];   // room → fin du délai après un refus
}

sealed class Room(string name, RoomVisibility visibility, int maxPlayers)
{
    public string         Name       { get; } = name;
    public RoomVisibility Visibility = visibility;
    public int            MaxPlayers = maxPlayers;
    public string?        Code;                       // code d'invitation (room privée)
    public Conn?          Owner;
    public readonly List<Conn> Members = [];
    public readonly List<Conn> Pending = [];          // demandes en attente de la réponse de l'owner
    public readonly Dictionary<string, DateTime> Tokens = [];   // jetons de reconnexion → date d'émission
}

// Message à envoyer une fois le verrou relâché
readonly record struct Outgoing(Conn To, OutMsg Msg);

// ------------------------------------------------------------------ //
// ROOMS : création, accès (public/privé, validation par l'owner), départs
// Chaque opération modifie l'état sous Lock et retourne les messages à envoyer.
// ------------------------------------------------------------------ //
static class Lobby
{
    const int MaxRooms          = 200;
    const int MaxPendingPerRoom = 16;
    const int MaxTokensPerRoom  = 256;
    public const int MaxBadCodes = 5;    // au-delà : déconnexion (essais de codes à la chaîne)
    static readonly TimeSpan RejectCooldown = TimeSpan.FromSeconds(30);
    static readonly TimeSpan TokenLifetime  = TimeSpan.FromHours(12);
    public static readonly TimeSpan PendingTimeout = TimeSpan.FromSeconds(
        int.TryParse(Environment.GetEnvironmentVariable("LOBBY_PENDING_TIMEOUT_SECONDS"), out var s) && s > 0 ? s : 60);

    static readonly object Lock = new();
    static readonly Dictionary<string, Room> rooms = [];

    static OutMsg Error(string text) => new() { Type = MsgType.Error, Text = text };

    // ------------------------------------------------------------------ //
    // JOIN
    // ------------------------------------------------------------------ //

    // Crée la room si elle n'existe pas (le créateur en devient owner), sinon transmet la demande à l'owner.
    // Le client de l'owner accepte automatiquement ou laisse le jeu décider (RoomOptions.AutoAccept côté client).
    // badCodeLimitReached : trop de codes faux, l'appelant doit fermer la connexion.
    public static List<Outgoing> Join(Conn c, InMsg m, out bool badCodeLimitReached)
    {
        badCodeLimitReached = false;
        var outs = new List<Outgoing>();
        var roomName = m.Room!;
        var now = DateTime.UtcNow;

        lock (Lock)
        {
            LeaveLocked(c, outs);

            if (c.RejectedUntil.TryGetValue(roomName, out var until) && until > now)
            {
                outs.Add(new(c, Error("demande refusée récemment : réessayer plus tard")));
                return outs;
            }

            if (!rooms.TryGetValue(roomName, out var r))
            {
                if (rooms.Count >= MaxRooms)
                {
                    outs.Add(new(c, Error("trop de rooms ouvertes")));
                    return outs;
                }
                r = new Room(roomName, m.Visibility ?? RoomVisibility.Public, m.MaxPlayers ?? Protocol.MaxPlayers);
                if (r.Visibility == RoomVisibility.Private) r.Code = NewCode();
                rooms[roomName] = r;
                AddMemberLocked(r, c, m.Name!, outs);
                return outs;
            }

            // Un jeton de reconnexion valide dispense du code et de la validation
            bool hasToken = m.Token != null && r.Tokens.TryGetValue(m.Token, out var issued) && now - issued < TokenLifetime;

            if (!hasToken && r.Visibility == RoomVisibility.Private && !CodeMatches(m.Code, r.Code))
            {
                badCodeLimitReached = ++c.BadCodes >= MaxBadCodes;
                outs.Add(new(c, Error("room privée : code d'invitation manquant ou invalide")));
                return outs;
            }

            if (r.Members.Count >= r.MaxPlayers)
            {
                outs.Add(new(c, Error("room pleine")));
                return outs;
            }

            if (!hasToken && r.Owner != null)
            {
                if (r.Pending.Count >= MaxPendingPerRoom)
                {
                    outs.Add(new(c, Error("trop de demandes en attente pour cette room")));
                    return outs;
                }
                c.PendingRoom  = r;
                c.PendingName  = UniqueName(r, m.Name!);
                c.PendingSince = now;
                r.Pending.Add(c);
                outs.Add(new(c,       new OutMsg { Type = MsgType.JoinPending, Room = r.Name, Name = c.PendingName,
                                                   Text = "en attente de validation par le propriétaire" }));
                outs.Add(new(r.Owner, new OutMsg { Type = MsgType.JoinRequest, Room = r.Name, Name = c.PendingName }));
                return outs;
            }

            AddMemberLocked(r, c, m.Name!, outs);
        }
        return outs;
    }

    static void AddMemberLocked(Room r, Conn c, string requestedName, List<Outgoing> outs)
    {
        c.Name = UniqueName(r, requestedName);
        c.Room = r;
        r.Members.Add(c);
        r.Owner ??= c;   // premier arrivant = owner

        var token = NewToken();
        PruneTokens(r);
        r.Tokens[token] = DateTime.UtcNow;

        outs.Add(new(c, new OutMsg
        {
            Type       = MsgType.RoomInfo,
            Room       = r.Name,
            Owner      = r.Owner.Name,
            Name       = c.Name,
            IceServers = TurnCredentials.For(c.Name),
            Visibility = r.Visibility,
            MaxPlayers = r.MaxPlayers,
            Players    = r.Members.Count,
            Code       = r.Code,
            Token      = token,
        }));
        BroadcastLocked(r, new OutMsg { Type = MsgType.PeerJoined, Name = c.Name, Room = r.Name, Text = $"{c.Name} a rejoint le lobby" }, outs);

        // Si l'owner était absent (cas limite), les demandes en attente lui sont présentées
        if (r.Owner == c)
            foreach (var p in r.Pending)
                outs.Add(new(c, new OutMsg { Type = MsgType.JoinRequest, Room = r.Name, Name = p.PendingName }));
    }

    // ------------------------------------------------------------------ //
    // DÉPART (leave, déconnexion, ou nouveau join)
    // ------------------------------------------------------------------ //

    public static List<Outgoing> Leave(Conn c)
    {
        var outs = new List<Outgoing>();
        lock (Lock) LeaveLocked(c, outs);
        return outs;
    }

    static void LeaveLocked(Conn c, List<Outgoing> outs)
    {
        // Demande en attente : l'annuler
        if (c.PendingRoom is { } pr)
        {
            pr.Pending.Remove(c);
            if (pr.Owner != null)
                outs.Add(new(pr.Owner, new OutMsg { Type = MsgType.JoinRequestCancelled, Room = pr.Name, Name = c.PendingName }));
            c.PendingRoom = null;
            c.PendingName = null;
        }

        if (c.Room is not { } r) return;
        r.Members.Remove(c);
        c.Room = null;

        // Room vide : la fermer et refuser les demandes en attente
        if (r.Members.Count == 0)
        {
            rooms.Remove(r.Name);
            foreach (var p in r.Pending)
            {
                p.PendingRoom = null;
                outs.Add(new(p, new OutMsg { Type = MsgType.JoinRejected, Room = r.Name, Text = "la room a été fermée" }));
            }
            r.Pending.Clear();
            return;
        }

        BroadcastLocked(r, new OutMsg { Type = MsgType.PeerLeft, Name = c.Name, Room = r.Name, Text = $"{c.Name} a quitté le lobby" }, outs);

        // L'owner part : le suivant reprend la room et les demandes en attente
        if (r.Owner == c)
        {
            r.Owner = r.Members[0];
            BroadcastLocked(r, new OutMsg { Type = MsgType.OwnerChanged, Room = r.Name, Owner = r.Owner.Name,
                                            Text = $"{r.Owner.Name} est le nouveau propriétaire du lobby" }, outs);
            foreach (var p in r.Pending)
                outs.Add(new(r.Owner, new OutMsg { Type = MsgType.JoinRequest, Room = r.Name, Name = p.PendingName }));
        }
    }

    // ------------------------------------------------------------------ //
    // OWNER : validation des demandes, réglages
    // ------------------------------------------------------------------ //

    public static List<Outgoing> Decide(Conn owner, string name, bool accept)
    {
        var outs = new List<Outgoing>();
        lock (Lock)
        {
            if (owner.Room is not { } r || r.Owner != owner)
            {
                outs.Add(new(owner, Error("réservé au propriétaire de la room")));
                return outs;
            }
            var p = r.Pending.Find(x => x.PendingName == name);
            if (p == null)
            {
                outs.Add(new(owner, Error("aucune demande en attente pour ce joueur")));
                return outs;
            }
            r.Pending.Remove(p);
            p.PendingRoom = null;

            if (!accept)
            {
                p.RejectedUntil[r.Name] = DateTime.UtcNow + RejectCooldown;
                outs.Add(new(p, new OutMsg { Type = MsgType.JoinRejected, Room = r.Name, Text = "refusé par le propriétaire" }));
                return outs;
            }
            if (r.Members.Count >= r.MaxPlayers)
            {
                outs.Add(new(p, new OutMsg { Type = MsgType.JoinRejected, Room = r.Name, Text = "room pleine" }));
                return outs;
            }
            AddMemberLocked(r, p, name, outs);
        }
        return outs;
    }

    public static List<Outgoing> UpdateSettings(Conn owner, InMsg m)
    {
        var outs = new List<Outgoing>();
        lock (Lock)
        {
            if (owner.Room is not { } r || r.Owner != owner)
            {
                outs.Add(new(owner, Error("réservé au propriétaire de la room")));
                return outs;
            }

            if (m.Visibility is { } v)
            {
                r.Visibility = v;
                if (v == RoomVisibility.Private) r.Code ??= NewCode();
                else                             r.Code = null;
            }
            if (m.MaxPlayers is { } max) r.MaxPlayers = max;   // ne renvoie personne si on réduit : bloque juste les arrivées

            BroadcastLocked(r, new OutMsg
            {
                Type = MsgType.RoomUpdated, Room = r.Name, Visibility = r.Visibility,
                MaxPlayers = r.MaxPlayers, Players = r.Members.Count, Code = r.Code,
            }, outs);
        }
        return outs;
    }

    // Demandes sans réponse de l'owner après PendingTimeout : refusées (appelé périodiquement)
    public static List<Outgoing> ExpirePending()
    {
        var outs = new List<Outgoing>();
        var limit = DateTime.UtcNow - PendingTimeout;
        lock (Lock)
        {
            foreach (var r in rooms.Values)
                foreach (var p in r.Pending.Where(p => p.PendingSince < limit).ToList())
                {
                    r.Pending.Remove(p);
                    p.PendingRoom = null;
                    outs.Add(new(p, new OutMsg { Type = MsgType.JoinRejected, Room = r.Name, Text = "pas de réponse du propriétaire" }));
                    if (r.Owner != null)
                        outs.Add(new(r.Owner, new OutMsg { Type = MsgType.JoinRequestCancelled, Room = r.Name, Name = p.PendingName }));
                }
        }
        return outs;
    }

    // ------------------------------------------------------------------ //
    // LECTURES
    // ------------------------------------------------------------------ //

    // Rooms publiques uniquement : une room privée n'apparaît jamais dans la liste
    public static List<RoomSummary> ListPublic()
    {
        lock (Lock)
            return rooms.Values
                .Where(r => r.Visibility == RoomVisibility.Public)
                .Select(r => new RoomSummary(r.Name, r.Members.Count, r.MaxPlayers, r.Owner?.Name))
                .ToList();
    }

    // Nom de l'émetteur et membres de sa room (pour le chat)
    public static (string? Name, string? Room, List<Conn> Members) MembersOf(Conn c)
    {
        lock (Lock)
            return c.Room is { } r ? (c.Name, r.Name, r.Members.ToList()) : (null, null, []);
    }

    // Nom de l'émetteur et membre destinataire dans la même room (pour le signaling)
    public static (string? From, Conn? Target) FindMember(Conn c, string name)
    {
        lock (Lock)
            return c.Room is { } r ? (c.Name, r.Members.Find(x => x.Name == name && x != c)) : (null, null);
    }

    // ------------------------------------------------------------------ //
    // OUTILS
    // ------------------------------------------------------------------ //

    static void BroadcastLocked(Room r, OutMsg msg, List<Outgoing> outs)
    {
        foreach (var m in r.Members) outs.Add(new(m, msg));
    }

    // "Bob" déjà pris (membre ou demande en attente) → "Bob_2", "Bob_3"... sans dépasser la longueur max
    static string UniqueName(Room r, string name)
    {
        bool Taken(string n) => r.Members.Any(c => c.Name == n) || r.Pending.Any(c => c.PendingName == n);
        var candidate = name;
        for (int i = 2; Taken(candidate); i++)
        {
            var suffix = $"_{i}";
            candidate = name[..Math.Min(name.Length, Protocol.MaxNameLength - suffix.Length)] + suffix;
        }
        return candidate;
    }

    static void PruneTokens(Room r)
    {
        var now = DateTime.UtcNow;
        foreach (var t in r.Tokens.Where(kv => now - kv.Value > TokenLifetime).Select(kv => kv.Key).ToList())
            r.Tokens.Remove(t);
        while (r.Tokens.Count >= MaxTokensPerRoom)
            r.Tokens.Remove(r.Tokens.MinBy(kv => kv.Value).Key);
    }

    const string Base32 = "ABCDEFGHIJKLMNOPQRSTUVWXYZ234567";

    // 8 caractères base32 = 40 bits aléatoires (avec MaxBadCodes par connexion, impossible à deviner à la main)
    static string NewCode()
    {
        Span<char> chars = stackalloc char[8];
        for (int i = 0; i < chars.Length; i++) chars[i] = Base32[RandomNumberGenerator.GetInt32(Base32.Length)];
        return new string(chars);
    }

    static string NewToken() =>
        Convert.ToBase64String(RandomNumberGenerator.GetBytes(16)).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    // Comparaison en temps constant : ne révèle pas combien de caractères sont justes
    static bool CodeMatches(string? given, string? expected) =>
        given != null && expected != null &&
        CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(given), Encoding.ASCII.GetBytes(expected));
}
