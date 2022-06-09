using System.Collections;
using System.Collections.Generic;
using UnityEngine;

public class CameraPlatform : MonoBehaviour
{
    public Transform target;
    private Vector3 offset;
    public float smoothSpeed = 0.125f;
    public float rotationDamping = 2.5f;


    private void Start()
    {
        if (target == null)
        {
            throw new System.ArgumentException("TARGET cannot be null", "original");
        }

        offset = transform.position - target.transform.position;
    }

    private void LateUpdate()
    {
        try
        {
            transform.position = Vector3.Lerp(transform.position, target.transform.position, smoothSpeed);
            transform.rotation = Quaternion.Lerp(transform.rotation, target.rotation, Time.deltaTime * rotationDamping);
        }
        catch (System.Exception)
        {
            throw;
        }
    }
}
