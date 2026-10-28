using UnityEngine;

public class NEPlayer : MonoBehaviour
{
    public class NEPlayerElement : NetworkEntity
    {
        public float X { get; set; }
        public float Y { get; set; }
        public float Z { get; set; }

        
        public NEPlayerElement(float x, float y, float z) : base(nameof(NEPlayerElement))
        {
            X = x;
            Y = y;
            Z = z;
        }

        // Pour System.Text.Json : pas d'allocation de NetId à la réception
        public NEPlayerElement() { }

        public static NEPlayerElement Deserialize(string json) => DeserializeChecked<NEPlayerElement>(json);

        public override void DeSerialize(string jsonString)
        {
            var v = DeserializeChecked<NEPlayerElement>(jsonString);
            if (v != null) { X = v.X; Y = v.Y; Z = v.Z; }
        }
    }
    NEPlayerElement neplayer;
    public bool isOriginal = true;

    public bool sendPosition = true;
    public bool sendRotation = true;
    const float SendInterval = 0.1f;
    float  nextSend;
    Vector3 dest;
    Vector3 lerpOrigin;
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
        neplayer = new NEPlayerElement(transform.position.x, transform.position.y, transform.position.z);
        neplayer.Name = gameObject.name;
        dest = lerpOrigin = transform.position;
    }

    public void SetDest(Vector3 d)
    {
        lerpOrigin  = transform.position;
        dest        = d;
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
            neplayer.X = transform.position.x;
            neplayer.Y = transform.position.y;
            neplayer.Z = transform.position.z;    
                
            ProgramClient.BroadcastEntity(neplayer);
            neplayer.EventType = NetworkEntity.EnumEventType.Update;
        }
        else
        {
            lerpElapsed += Time.deltaTime;
            transform.position = Vector3.Lerp(lerpOrigin, dest, Mathf.Clamp01(lerpElapsed / SendInterval));
        }
    }
    void OnDestroy()
    {
        Debug.Log("OnDestroy appelé pour " + gameObject.name);
        if (neplayer==null) return;
        neplayer.X = transform.position.x;
        neplayer.Y = transform.position.y;
        neplayer.Z = transform.position.z;
        neplayer.EventType = NetworkEntity.EnumEventType.Destroy;
        ProgramClient.BroadcastEntity(neplayer);
    }
}
