using System.Text.Json;
using UnityEngine;

public class NESynchro : MonoBehaviour
{
    public string PrefabType="";
    private NetworkEntity _ne;
    public bool isOriginal = true;

    public bool sendPosition = true;
    public bool sendRotation = true;
    const float SendInterval = 0.1f;
    float  nextSend;
    Vector3 dest;
    Vector3 lerpOrigin;
    Quaternion destRot;
    Quaternion lerpOriginRot;
    float   lerpElapsed;

    float      _pushTimer;
    public float pushRecoveryTime = 0.8f;
    Rigidbody  _rb;
    RandomMove _rm;

    bool    _pendingPush;
    Vector3 _pendingVelocity;

    // Push() appelé depuis Update — on diffère l'application de la vélocité au prochain FixedUpdate
    // pour éviter que MovePosition du même step l'écrase
    public void Push(Vector3 velocity)
    {
        if (!isOriginal || _rb == null) return;
        if (_rm != null) _rm.enabled = false;
        _pendingVelocity = velocity;
        _pendingPush     = true;
        _pushTimer       = pushRecoveryTime;
        Debug.Log("Pushing");
        Debug.Log(velocity);
    }

    void FixedUpdate()
    {
        if (!_pendingPush) return;
        _rb.linearVelocity = _pendingVelocity; // applique directement, indépendant de la masse
        _pendingPush = false;
    }

    void Start()
    {
        _rb = GetComponent<Rigidbody>();
        _rm = GetComponent<RandomMove>();

        if (isOriginal && _rb != null)
        {
            _rb.isKinematic    = false;
            _rb.freezeRotation = true;
            if (TryGetComponent<Collider>(out var col))
                col.material = new PhysicsMaterial {
                    bounciness      = 0f,
                    bounceCombine   = PhysicsMaterialCombine.Minimum,
                    dynamicFriction = 0f,
                    staticFriction  = 0f,
                    frictionCombine = PhysicsMaterialCombine.Minimum,
                };
        }
        else if (!isOriginal && _rb != null)
            _rb.isKinematic = true;

        // Seul l'original a une identité réseau locale ; une copie distante ne fait que recevoir
        if (isOriginal)
        {
            _ne = new NetworkEntity(PrefabType);
            _ne.Name = gameObject.name;
        }

        // Une copie distante peut avoir reçu son premier état avant Start() : ne pas l'écraser
        if (!_hasRemoteState)
        {
            dest = lerpOrigin = transform.position;
            destRot = lerpOriginRot = transform.rotation;
        }
    }

    bool _hasRemoteState;

    // Envoi (original)
    const float KeepAliveInterval = 1f;
    const float MinMoveSqr        = 1e-6f;   // 1 mm
    const float MinAngleDeg       = 0.1f;
    ushort     _seq;
    float      _lastSentAt = float.NegativeInfinity;
    Vector3    _lastSentPos;
    Quaternion _lastSentRot = Quaternion.identity;

    // Réception (copie distante)
    bool   _hasSeq;
    ushort _lastSeq;

    public void SetDest(Vector3 d)
    {
        lerpOrigin  = transform.position;
        dest        = d;
        lerpElapsed = 0f;
    }
    public void SetDestRot(Quaternion d)
    {
        lerpOriginRot  = transform.rotation;
        destRot        = d;
        lerpElapsed = 0f;
    }
    void Update()
    {
        // Décompte de la poussée — ré-active RandomMove à l'expiration
        if (_pushTimer > 0f)
        {
            _pushTimer -= Time.deltaTime;
            if (_pushTimer <= 0f && _rm != null)
                _rm.Restart();
        }

        if (isOriginal)
        {
            if (Time.time < nextSend) return;
            nextSend = Time.time + SendInterval;

            // Immobile : un état de maintien par seconde suffit (et fait apparaître l'entité chez un nouvel arrivant)
            bool moved = (transform.position - _lastSentPos).sqrMagnitude > MinMoveSqr
                      || Quaternion.Angle(transform.rotation, _lastSentRot) > MinAngleDeg;
            if (!moved && Time.time - _lastSentAt < KeepAliveInterval) return;

            _lastSentPos = transform.position;
            _lastSentRot = transform.rotation;
            _lastSentAt  = Time.time;

            // Envoi groupé en binaire sur le canal "state" (TransformBroadcaster.Flush en LateUpdate)
            TransformBroadcaster.Queue(new TransformState
            {
                NetId       = _ne.NetId,
                Seq         = ++_seq,
                PrefabType  = PrefabType,
                HasPosition = sendPosition,
                HasRotation = sendRotation,
                Position    = transform.position,
                Rotation    = transform.rotation,
            });
        }
        else
        {
            lerpElapsed += Time.deltaTime;
            transform.position = Vector3.Lerp(lerpOrigin, dest, Mathf.Clamp01(lerpElapsed / SendInterval));
            transform.rotation = Quaternion.Lerp(lerpOriginRot, destRot, Mathf.Clamp01(lerpElapsed / SendInterval));
        }
    }
    void OnDestroy()
    {
        // Prévenir les pairs que l'original disparaît (les copies distantes ne diffusent rien)
        if (!isOriginal || _ne == null) return;
        _ne.EventType = NetworkEntity.EnumEventType.Destroy;
        try { ProgramClient.BroadcastEntity(_ne); } catch { }
    }

    // Transform reçu sur le canal "state" (binaire)
    public void OnReceiveTransform(TransformState s)
    {
        if (_hasSeq && !TransformState.IsNewer(s.Seq, _lastSeq)) return;   // état périmé, un plus récent est déjà appliqué
        _hasSeq  = true;
        _lastSeq = s.Seq;
        ApplyRemote(s.HasPosition, s.Position, s.HasRotation, s.Rotation);
    }

    // Entité reçue en JSON sur le canal "lobby" (attributs Position/Rotation)
    public void OnReceiveNetMsg(string from, NetworkEntity ne)
    {
        bool hasPos = ne.Attribs.TryGetValue("Position", out var posJson);
        bool hasRot = ne.Attribs.TryGetValue("Rotation", out var rotJson);
        ApplyRemote(hasPos, hasPos ? JsonSerializer.Deserialize<Vector3DTO>(posJson).ToVector3() : default,
                    hasRot, hasRot ? JsonSerializer.Deserialize<QuaternionDTO>(rotJson).ToQuaternion() : default);
    }

    void ApplyRemote(bool hasPos, Vector3 pos, bool hasRot, Quaternion rot)
    {
        bool first = !_hasRemoteState;
        _hasRemoteState = true;

        if (hasPos)
        {
            if (first) transform.position = pos;   // 1er état : placer directement, pas de lerp depuis l'origine
            SetDest(pos);
        }
        if (hasRot)
        {
            if (first) transform.rotation = rot;
            SetDestRot(rot);
        }
    }
}

