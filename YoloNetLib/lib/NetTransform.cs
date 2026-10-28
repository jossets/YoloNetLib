using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using UnityEngine;

// État de transform d'une entité, envoyé sur le canal "state"
public struct TransformState
{
    public long       NetId;
    public ushort     Seq;          // incrémenté à chaque envoi : permet d'ignorer un état plus ancien
    public string     PrefabType;   // pour instancier l'entité chez un pair qui ne l'a pas encore vue
    public bool       HasPosition;
    public bool       HasRotation;
    public Vector3    Position;
    public Quaternion Rotation;

    // true si a est plus récent que b, en tenant compte du rebouclage après 65535
    public static bool IsNewer(ushort a, ushort b) => (short)(a - b) > 0;
}

// Format binaire (little-endian) d'un paquet :
//   [version:1][nombre d'entités:1] puis, pour chaque entité :
//   [netId:varint][seq:2][flags:1][prefab: longueur varint + UTF-8][position:3×float]?[rotation:4×float]?
// ≈ 45 octets par entité, contre ~400 pour le même état en JSON.
// Les paquets viennent d'autres joueurs : le décodage est borné et rejette tout paquet mal formé.
public static class TransformCodec
{
    const byte Version      = 1;
    const byte FlagPosition = 1;
    const byte FlagRotation = 2;

    public const int MaxPacketBytes     = 1100;   // sous la MTU : pas de fragmentation
    public const int MaxPrefabLength    = 64;
    const int MaxEntitiesPerPacket      = 255;
    const float MaxCoordinate           = 1e6f;

    static readonly UTF8Encoding StrictUtf8 = new UTF8Encoding(false, true);

    public static List<byte[]> Encode(List<TransformState> states)
    {
        var packets = new List<byte[]>();
        var packet  = new MemoryStream();
        var entity  = new MemoryStream();
        var w       = new BinaryWriter(entity);
        int count   = 0;

        foreach (var s in states)
        {
            entity.SetLength(0);
            entity.Position = 0;
            WriteEntity(w, s);
            w.Flush();

            if (count > 0 && (packet.Length + entity.Length > MaxPacketBytes || count == MaxEntitiesPerPacket))
            {
                packets.Add(Finish(packet, count));
                count = 0;
            }
            if (count == 0)
            {
                packet.SetLength(0);
                packet.Position = 0;
                packet.WriteByte(Version);
                packet.WriteByte(0);   // nombre, renseigné par Finish
            }
            entity.Position = 0;
            entity.CopyTo(packet);
            count++;
        }
        if (count > 0) packets.Add(Finish(packet, count));
        return packets;
    }

    static byte[] Finish(MemoryStream packet, int count)
    {
        var bytes = packet.ToArray();
        bytes[1] = (byte)count;
        return bytes;
    }

    static void WriteEntity(BinaryWriter w, TransformState s)
    {
        var prefab = Encoding.UTF8.GetBytes(s.PrefabType ?? "");
        if (prefab.Length > MaxPrefabLength) throw new ArgumentException($"PrefabType trop long ({prefab.Length} > {MaxPrefabLength})");

        WriteVarUInt(w, (ulong)s.NetId);
        w.Write(s.Seq);
        byte flags = 0;
        if (s.HasPosition) flags |= FlagPosition;
        if (s.HasRotation) flags |= FlagRotation;
        w.Write(flags);
        WriteVarUInt(w, (ulong)prefab.Length);
        w.Write(prefab);
        if (s.HasPosition) { w.Write(s.Position.x); w.Write(s.Position.y); w.Write(s.Position.z); }
        if (s.HasRotation) { w.Write(s.Rotation.x); w.Write(s.Rotation.y); w.Write(s.Rotation.z); w.Write(s.Rotation.w); }
    }

    // false (et rien d'ajouté) si le paquet est mal formé
    public static bool TryDecode(byte[] data, List<TransformState> into)
    {
        if (data == null || data.Length < 2 || data.Length > MaxPacketBytes * 2 || data[0] != Version) return false;

        var decoded = new List<TransformState>(data[1]);
        try
        {
            using (var r = new BinaryReader(new MemoryStream(data, 2, data.Length - 2, false)))
            {
                for (int i = 0; i < data[1]; i++)
                {
                    var s = new TransformState();
                    var netId = ReadVarUInt(r);
                    if (netId == 0 || netId > long.MaxValue) return false;
                    s.NetId = (long)netId;
                    s.Seq = r.ReadUInt16();

                    byte flags = r.ReadByte();
                    if ((flags & ~(FlagPosition | FlagRotation)) != 0) return false;
                    s.HasPosition = (flags & FlagPosition) != 0;
                    s.HasRotation = (flags & FlagRotation) != 0;

                    var prefabLength = ReadVarUInt(r);
                    if (prefabLength > MaxPrefabLength) return false;
                    var prefab = r.ReadBytes((int)prefabLength);
                    if (prefab.Length != (int)prefabLength) return false;
                    s.PrefabType = StrictUtf8.GetString(prefab);

                    if (s.HasPosition)
                    {
                        s.Position = new Vector3(r.ReadSingle(), r.ReadSingle(), r.ReadSingle());
                        if (!IsValidCoordinate(s.Position.x) || !IsValidCoordinate(s.Position.y) || !IsValidCoordinate(s.Position.z)) return false;
                    }
                    if (s.HasRotation)
                    {
                        float x = r.ReadSingle(), y = r.ReadSingle(), z = r.ReadSingle(), qw = r.ReadSingle();
                        float norm = (float)Math.Sqrt(x * x + y * y + z * z + qw * qw);
                        if (float.IsNaN(norm) || float.IsInfinity(norm) || norm < 1e-3f) return false;
                        s.Rotation = new Quaternion(x / norm, y / norm, z / norm, qw / norm);   // renormalisé
                    }
                    decoded.Add(s);
                }
                if (r.BaseStream.Position != r.BaseStream.Length) return false;   // octets en trop
            }
        }
        catch (EndOfStreamException)     { return false; }
        catch (DecoderFallbackException) { return false; }

        into.AddRange(decoded);
        return true;
    }

    static bool IsValidCoordinate(float v) =>
        !float.IsNaN(v) && !float.IsInfinity(v) && Math.Abs(v) <= MaxCoordinate;

    static void WriteVarUInt(BinaryWriter w, ulong v)
    {
        while (v >= 0x80) { w.Write((byte)(v | 0x80)); v >>= 7; }
        w.Write((byte)v);
    }

    static ulong ReadVarUInt(BinaryReader r)
    {
        ulong v = 0;
        for (int shift = 0; shift < 64; shift += 7)
        {
            byte b = r.ReadByte();
            v |= (ulong)(b & 0x7F) << shift;
            if ((b & 0x80) == 0) return v;
        }
        throw new EndOfStreamException("varint trop long");
    }
}

// Regroupe les états d'une frame et les envoie en un minimum de paquets.
// Queue() depuis les NESynchro (Update), Flush() depuis le manager (LateUpdate). Thread Unity uniquement.
public static class TransformBroadcaster
{
    static readonly List<TransformState> pending = new List<TransformState>();

    public static void Queue(TransformState s) => pending.Add(s);

    public static void Flush()
    {
        if (pending.Count == 0) return;
        try
        {
            foreach (var packet in TransformCodec.Encode(pending))
                ProgramClient.BroadcastState(packet);
        }
        finally
        {
            pending.Clear();   // même en cas d'erreur (PrefabType trop long...) : ne pas bloquer les frames suivantes
        }
    }
}
