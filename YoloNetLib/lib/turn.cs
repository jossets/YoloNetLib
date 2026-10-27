using System;
using System.Collections.Generic;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Tasks;
using SIPSorcery.Net;
using UnityEngine;

record TurnInfo(string Name, List<string> Candidates, string Sdp);

class TurnIf
{
    const string TurnSecret  = "COTURN_SECRET";
    const string TurnHost    = "host";
    const int    TurnPort    = 3478;
    const int    TurnTtlH   = 24;

    // Coturn mode use-auth-secret :
    //   username  = timestamp Unix d'expiration (doit être dans le futur)
    //   password  = Base64(HMAC-SHA1(secret, username))
    static string GenerateUser(string clientName) =>
        $"{DateTimeOffset.UtcNow.AddHours(TurnTtlH).ToUnixTimeSeconds()}:{clientName}";

    // Réutilisable par le signaling client pour créer des RTCPeerConnection
    public static RTCConfiguration GetRTCConfiguration(string clientName = "")
    {
        var user = GenerateUser(clientName);
        var cred = GenerateCredential(TurnSecret, user);
        Debug.Log($"[TURN] credentials — user: {user} (expire dans {TurnTtlH}h)");

        // ICE prioritise les candidats dans l'ordre : host > srflx > relay
        // Pour relay : UDP est tenté en premier, TCP en fallback (pare-feux bloquant UDP)
        return new RTCConfiguration
        {
            iceServers = new List<RTCIceServer>
            {
                // STUN pur — candidats server-reflexive (IP publique, sans relay)
                new RTCIceServer { urls = $"stun:{TurnHost}:{TurnPort}" },

                // TURN/UDP — relay UDP (le plus courant, faible latence)
                new RTCIceServer { urls = $"turn:{TurnHost}:{TurnPort}?transport=udp", username = user, credential = cred },

                // TURN/TCP port 3478 — fallback si UDP bloqué
                new RTCIceServer { urls = $"turn:{TurnHost}:{TurnPort}?transport=tcp", username = user, credential = cred },

                // TURNS/TLS port 443 — dernier recours (pare-feux les plus stricts)
                // Nécessite coturn avec tls-listening-port=443 + certificat
                // new RTCIceServer { urls = $"turns:{TurnHost}:443?transport=tcp", username = user, credential = cred },
            }
        };
    }

    // Usage diagnostique : collecte complète puis retourne les candidats
    public static async Task<TurnInfo> GetTurnConfig(string clientName)
    {
        var pc         = new RTCPeerConnection(GetRTCConfiguration());
        var candidates = new List<string>();
        var done       = new TaskCompletionSource<bool>();

        pc.onicecandidate += cand =>
        {
            if (cand != null) candidates.Add(cand.candidate);
            else              done.TrySetResult(true);
        };

        pc.onicegatheringstatechange += state =>
        {
            if (state == RTCIceGatheringState.complete)
                done.TrySetResult(true);
        };

        var offer = pc.createOffer();
        await pc.setLocalDescription(offer);

        await Task.WhenAny(done.Task, Task.Delay(TimeSpan.FromSeconds(10)));

        pc.close();
        return new TurnInfo(clientName, candidates, offer.sdp);
    }

    static string GenerateCredential(string secret, string username)
    {
        using var hmac = new HMACSHA1(Encoding.ASCII.GetBytes(secret));
        return Convert.ToBase64String(hmac.ComputeHash(Encoding.ASCII.GetBytes(username)));
    }
}
