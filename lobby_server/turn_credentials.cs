using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

// Serveur ICE envoyé au client dans room_info (même forme que RTCIceServer côté SIPSorcery)
sealed record IceServer(
    [property: JsonPropertyName("urls")]       string  Urls,
    [property: JsonPropertyName("username")]   string? Username   = null,
    [property: JsonPropertyName("credential")] string? Credential = null);

// ------------------------------------------------------------------ //
// IDENTIFIANTS TURN ÉPHÉMÈRES (coturn use-auth-secret / "TURN REST API")
//   username   = "<expiration unix>:<nom du joueur>"
//   credential = Base64(HMAC-SHA1(secret, username))
// Le secret reste sur le serveur : le client ne reçoit que des identifiants qui expirent.
// ------------------------------------------------------------------ //
static partial class TurnCredentials
{
    static string?  host;
    static int      port = 3478;
    static byte[]?  secret;
    static TimeSpan ttl  = TimeSpan.FromHours(12);

    [GeneratedRegex(@"^[A-Za-z0-9.-]{1,253}$")]
    private static partial Regex HostRegex();

    // Variables d'environnement :
    //   TURN_HOST          nom public du serveur coturn (obligatoire, sinon aucun serveur ICE envoyé)
    //   TURN_PORT          3478 par défaut
    //   TURN_TTL_HOURS     durée de validité des identifiants, 12 par défaut
    //   TURN_SECRET        secret partagé, ou à défaut :
    //   TURN_CONFIG_FILE   turnserver.conf dont on lit static-auth-secret (une seule source de vérité)
    public static void Configure(ILogger log)
    {
        var h = Environment.GetEnvironmentVariable("TURN_HOST");
        if (string.IsNullOrEmpty(h))
        {
            log.LogWarning("TURN_HOST non défini : aucun serveur STUN/TURN ne sera envoyé aux clients");
            return;
        }
        if (!HostRegex().IsMatch(h))
        {
            log.LogError("TURN_HOST invalide : STUN/TURN désactivés");
            return;
        }
        host = h;

        if (int.TryParse(Environment.GetEnvironmentVariable("TURN_PORT"), out var p) && p is > 0 and < 65536)
            port = p;
        if (int.TryParse(Environment.GetEnvironmentVariable("TURN_TTL_HOURS"), out var t) && t is > 0 and <= 48)
            ttl = TimeSpan.FromHours(t);

        var s = Environment.GetEnvironmentVariable("TURN_SECRET");
        var file = Environment.GetEnvironmentVariable("TURN_CONFIG_FILE");
        if (string.IsNullOrEmpty(s) && !string.IsNullOrEmpty(file))
            s = ReadSecretFromCoturnConfig(file, log);

        if (string.IsNullOrEmpty(s) || s == "COTURN_SECRET")
        {
            log.LogWarning("Secret TURN absent ou par défaut : STUN seul, pas de relais TURN");
            return;
        }
        secret = Encoding.UTF8.GetBytes(s);
        log.LogInformation("TURN activé sur {Host}:{Port}, identifiants valables {Ttl} h", host, port, ttl.TotalHours);
    }

    static string? ReadSecretFromCoturnConfig(string path, ILogger log)
    {
        try
        {
            foreach (var raw in File.ReadLines(path))
            {
                var line = raw.Trim();
                if (line.StartsWith("static-auth-secret=", StringComparison.Ordinal))
                    return line["static-auth-secret=".Length..].Trim();
            }
            log.LogWarning("static-auth-secret introuvable dans {File}", path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            log.LogWarning("Lecture impossible de {File} : {Error}", path, ex.Message);
        }
        return null;
    }

    // Serveurs ICE pour un joueur (null si STUN/TURN non configurés)
    public static List<IceServer>? For(string clientName)
    {
        if (host == null) return null;

        var servers = new List<IceServer> { new($"stun:{host}:{port}") };
        if (secret != null)
        {
            var username   = $"{DateTimeOffset.UtcNow.Add(ttl).ToUnixTimeSeconds()}:{clientName}";
            var credential = Convert.ToBase64String(HMACSHA1.HashData(secret, Encoding.UTF8.GetBytes(username)));
            // UDP d'abord (faible latence), TCP en secours si l'UDP est filtré
            servers.Add(new($"turn:{host}:{port}?transport=udp", username, credential));
            servers.Add(new($"turn:{host}:{port}?transport=tcp", username, credential));
        }
        return servers;
    }
}
