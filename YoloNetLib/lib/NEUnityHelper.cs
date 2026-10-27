using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using UnityEngine;

public struct Vector3DTO
{
    public float x { get; set; }
    public float y { get; set; }
    public float z { get; set; }

    public Vector3DTO(Vector3 v)
    {
        x = v.x;
        y = v.y;
        z = v.z;
    }

    public Vector3 ToVector3() => new Vector3(x, y, z);
}


public struct QuaternionDTO
{
    public float x { get; set; }
    public float y { get; set; }
    public float z { get; set; }
    public float w { get; set; }
    public QuaternionDTO(Quaternion v)
    {
        x = v.x;
        y = v.y;
        z = v.z;
        w = v.w;
    }

    public Quaternion ToQuaternion() => new Quaternion(x, y, z,w);
}