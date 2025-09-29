using System;
using UnityEngine;
using UnityEngine.UI;

public class SimpleSpinner : MonoBehaviour
{
    public RectTransform spinner;
    public float speed = 180f; // deg/sec
    public bool spin;
    public bool spinOnStart = false;
    
    void Update()
    {
        if (spinner && spin) spinner.Rotate(0f, 0f, -speed * Time.deltaTime);
    }

    private void OnEnable()
    {
        spin = true;
    }

    private void OnDisable()
    {
        spin = false;
    }
}