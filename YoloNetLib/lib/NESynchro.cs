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
        _ne = new NetworkEntity(PrefabType);
        _ne.Name = gameObject.name;
        dest = lerpOrigin = transform.position;
        destRot = lerpOriginRot = transform.rotation;
    }

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
           
            if (sendPosition) {
                _ne.Attribs["Position"] = JsonSerializer.Serialize(new Vector3DTO(transform.position));
            }
            if (sendRotation) {
                _ne.Attribs["Rotation"] = JsonSerializer.Serialize(new QuaternionDTO(transform.rotation));
            }
   
            //Debug.Log("NESynchro broadcasting "+gameObject.name);
            ProgramClient.BroadcastEntity(_ne);
            _ne.EventType = NetworkEntity.EnumEventType.Update;
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
        Debug.Log("OnDestroy appelé pour " + gameObject.name);
        /*
        if (neplayer==null) return;
        neplayer.EventType = NetworkEntity.EnumEventType.Destroy;
        ProgramClient.BroadcastEntity(neplayer);
        */
    }

    public void OnReceiveNetMsg(string from, NetworkEntity ne)
    {
        //Debug.Log("NESynchro OnReceiveNetMsg "+gameObject.name+" from "+from);
        Vector3DTO destdto = JsonSerializer.Deserialize<Vector3DTO>(ne.Attribs["Position"]);
        dest = destdto.ToVector3();

        QuaternionDTO destrotdto = JsonSerializer.Deserialize<QuaternionDTO>(ne.Attribs["Rotation"]);
        destRot = destrotdto.ToQuaternion();
        
    }
}

