using System;
using System.Collections.Generic;
using System.Text.Json;
using Unity.Mathematics;
using UnityEngine;

public class NetworkEntityIdGenerator
{
    static long Id = 0;
    static public long AllocateId() { Id++; return Id; }
}

public class NetworkEntityFactory
{
    // WeakReference : la factory ne maintient PAS l'entité en vie.
    // Quand plus personne ne la référence, le GC peut la collecter
    // et le finalizer ~NetworkEntity() sera bien appelé.
    static readonly Dictionary<long, WeakReference<NetworkEntity>> entities =
        new Dictionary<long, WeakReference<NetworkEntity>>();

    // Retourne l'entité ou null si l'id est inconnu / déjà collectée
    public static NetworkEntity GetById(long id)
    {
        WeakReference<NetworkEntity> wr;
        NetworkEntity e;
        return entities.TryGetValue(id, out wr) && wr.TryGetTarget(out e) ? e : null;
    }

    // Retourne l'entité castée dans le type demandé, ou null
    public static T GetById<T>(long id) where T : NetworkEntity =>
        GetById(id) as T;

    public static void Register(NetworkEntity entity) =>
        entities[entity.NetId] = new WeakReference<NetworkEntity>(entity);

    public static void Deregister(NetworkEntity entity) =>
        entities.Remove(entity.NetId);
}

public class NetworkEntity
{
    public long   NetId      { get; set; }
    public string Name      { get; set; }
    public string PrefabType { get; set; }

    public enum EnumEventType {
        Create,
        Update,
        Destroy
    } ;
    public EnumEventType EventType { get; set; }

        public Dictionary<string, string> Attribs { get; set; } = new();

    // Cache statique : évite de recréer JsonSerializerOptions à chaque appel
    static readonly JsonSerializerOptions JsonOpts = new();

    public NetworkEntity(string prefabType)
    {
        NetId      = NetworkEntityIdGenerator.AllocateId();
        Name = "";
        PrefabType = prefabType;
        EventType = EnumEventType.Create;
        NetworkEntityFactory.Register(this);
    }
    public static NetworkEntity AllocateFromJSON(string prefabType, string json)
    {
        NetworkEntity ne = new NetworkEntity(prefabType);
        //Debug.Log("AllocateFromJSON json : "+json);
        var data = JsonSerializer.Deserialize<NetworkEntity>(json);
        //Debug.Log("AllocateFromJSON data : "+data.Serialize());
        
        return data;
    }
    ~NetworkEntity()
    {
        NetworkEntityFactory.Deregister(this);
    }

    public string Serialize() => JsonSerializer.Serialize(this, GetType(), JsonOpts);

    public virtual void DeSerialize(string _) { }

    // Désérialise le JSON, vérifie la cohérence du PrefabType et retourne
    // l'instance typée — null si le JSON est invalide ou de mauvais type.
    protected static T DeserializeChecked<T>(string jsonString) where T : NetworkEntity
    {
        T v;
        try   { v = JsonSerializer.Deserialize<T>(jsonString, JsonOpts); }
        catch { return null; }

        if (v == null || v.PrefabType != typeof(T).Name)
        {
            Debug.LogWarning($"Warning: cannot deserialize '{(v != null ? v.PrefabType : "null")}' into {typeof(T).Name}");
            return null;
        }
        return v;
    }
}


// Registre global : associe un nom de type à sa fonction de désérialisation.
// À appeler au démarrage pour chaque sous-type utilisé.
public static class NetworkEntityRegistry
{
    static readonly Dictionary<string, Func<string, NetworkEntity>> _factories =
        new Dictionary<string, Func<string, NetworkEntity>>();

    public static void Register<T>(Func<string, T> factory) where T : NetworkEntity =>
        _factories[typeof(T).Name] = json => factory(json);

    public static NetworkEntity Deserialize(string typeName, string json)
    {
        Func<string, NetworkEntity> factory;
        if (!_factories.TryGetValue(typeName, out factory))
        {
            Debug.LogWarning($"[NetworkEntityRegistry] Type '{typeName}' non enregistré. Appelez Register<{typeName}>() dans NetMgr.Start().");
            return null;
        }
        return factory(json);
    }
}

/*
public class NEVect2D : NetworkEntity
{
    public float X { get; set; }
    public float Y { get; set; }

    public NEVect2D(float x, float y) : base(nameof(NEVect2D)) { X = x; Y = y; }

    public static NEVect2D Deserialize(string json) => DeserializeChecked<NEVect2D>(json);

    public override void DeSerialize(string jsonString)
    {
        var v = DeserializeChecked<NEVect2D>(jsonString);
        if (v != null) { X = v.X; Y = v.Y; }
    }
}

public class NEVect3D : NetworkEntity
{
    public float X { get; set; }
    public float Y { get; set; }
    public float Z { get; set; }

    public NEVect3D(float x, float y, float z) : base(nameof(NEVect3D)) { X = x; Y = y; Z = z; }

    public static NEVect3D Deserialize(string json) => DeserializeChecked<NEVect3D>(json);

    public override void DeSerialize(string jsonString)
    {
        var v = DeserializeChecked<NEVect3D>(jsonString);
        if (v != null) { X = v.X; Y = v.Y; Z = v.Z; }

    }
}



public class NEQuaternion : NetworkEntity
{    
    
    public float X { get; set; }
    public float Y { get; set; }
    public float Z { get; set; }
    public float W { get; set; }
    public NEQuaternion(float x, float y, float z, float w) : base(nameof(NEVect3D)) { X = x; Y = y; Z = z; W=w;}

    public static NEQuaternion Deserialize(string json) => DeserializeChecked<NEQuaternion>(json);

    public override void DeSerialize(string jsonString)
    {
        var v = DeserializeChecked<NEQuaternion>(jsonString);
        if (v != null) { X = v.X; Y = v.Y; Z = v.Z; W=v.W; }

    }
}

*/

