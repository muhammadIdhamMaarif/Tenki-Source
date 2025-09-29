using System;
using System.Collections;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.Animations.Rigging;

[RequireComponent(typeof(RigBuilder))]
public class MouseAimTarget : MonoBehaviour
{
    [Header("Required")]
    [Tooltip("The main camera used to interpret mouse motion in world space.")]
    public Camera cam;

    [Tooltip("The MultiAimConstraint on the head (or neck) rig.")]
    public MultiAimConstraint headAimConstraint;

    [Tooltip("A Transform the head aims at. Usually an empty GameObject. The constraint should use this as its source object.")]
    public Transform aimTarget;

    [Header("Optional (auto-filled if on same object)")]
    public RigBuilder rigBuilder;

    [Header("Behavior")] 
    [Tooltip("Sesuai nama bjir")]
    public bool followMouse;
    
    [Tooltip("If true, the head will look at the playerTransform. If false, it will follow mouse movement.")]
    public bool followPlayer;

    [Tooltip("Set this to your player character (when followPlayer = true).")]
    public Transform playerTransform;

    [Header("Mouse Look Settings (when followPlayer = false)")]
    [Tooltip("Distance in front of the camera to place the aim target.")]
    public float aimDistance = 8f;

    [Tooltip("How strongly mouse delta moves the target (world units per pixel).")]
    public float sensitivity = 0.01f;

    [Tooltip("Clamp for vertical motion in world units relative to camera up/down.")]
    public Vector2 verticalClamp = new Vector2(-2.5f, 2.5f);

    [Tooltip("Clamp for horizontal motion in world units relative to camera right/left.")]
    public Vector2 horizontalClamp = new Vector2(-3f, 3f);

    [Tooltip("How quickly the target lerps to its desired position.")]
    public float followLerp = 20f;

    // Internal state for mouse-driven offset relative to the camera forward anchor.
    private Vector2 _offset; // x=right(+)/left(-), y=up(+)/down(-)

    void Reset()
    {
        cam = Camera.main;
        rigBuilder = GetComponent<RigBuilder>();
    }

    void Awake()
    {
        if (rigBuilder == null) rigBuilder = GetComponent<RigBuilder>();
        if (cam == null) cam = Camera.main;

        // Ensure the constraint has our aimTarget as a source.
        if (headAimConstraint != null && aimTarget != null)
        {
            var sources = headAimConstraint.data.sourceObjects;
            bool hasTarget = false;
            for (int i = 0; i < sources.Count; i++)
            {
                if (sources[i].transform == aimTarget) { hasTarget = true; break; }
            }
            if (!hasTarget)
            {
                var wt = new WeightedTransform(aimTarget, 1f);
                sources.Add(wt);
                headAimConstraint.data.sourceObjects = sources;
            }
        }
    }

    void Update()
    {
        if (headAimConstraint == null || aimTarget == null || rigBuilder == null || cam == null)
            return;

        if (followPlayer && playerTransform != null)
        {
            // Look directly at the player (you can tweak an offset if needed).
            Vector3 desired = playerTransform.position;
            MoveAimTarget(desired);
        }
        else if (followMouse && Mouse.current != null)
        {
            Vector2 mp = Mouse.current.position.ReadValue();
            Ray ray = cam.ScreenPointToRay(mp);

            // Desired point directly under cursor
            Vector3 desired = ray.GetPoint(aimDistance);

            // Optional: clamp around the forward anchor
            Vector3 anchor = cam.transform.position + cam.transform.forward * aimDistance;
            Vector3 local  = desired - anchor;

            float x = Mathf.Clamp(Vector3.Dot(local, cam.transform.right), horizontalClamp.x, horizontalClamp.y);
            float y = Mathf.Clamp(Vector3.Dot(local, cam.transform.up),    verticalClamp.x,   verticalClamp.y);

            desired = anchor + cam.transform.right * x + cam.transform.up * y;

            MoveAimTarget(desired);
        }

        // Per your requirement: constantly rebuild/update the rig each frame.
        // (Note: This is expensive, but you asked to do it always.)
        // rigBuilder.Build();
    }

    private void MoveAimTarget(Vector3 desiredWorldPos)
    {
        if (followLerp <= 0f)
        {
            aimTarget.position = desiredWorldPos;
        }
        else
        {
            aimTarget.position = Vector3.Lerp(aimTarget.position, desiredWorldPos, Time.deltaTime * followLerp);
        }
    }

    // Optional helper: allow runtime toggling via keyboard (new input system).
    // You can bind this from an InputAction if you prefer—not required.
    public void SetFollowPlayer(bool value)
    {
        followPlayer = value;
    }

    public void SetFollowMouse(bool value)
    {
        followMouse = value;
    }
    
    // Smoothly fade the MultiAimConstraint weight between 0 and 1
    // --- Fade settings (shows in Inspector) ---
    [SerializeField, Min(0f)] private float defaultFadeDuration = 0.5f;

    // Keep a handle so we can stop a previous fade
    private Coroutine _fadeRoutine;

    // Smoothly fade the MultiAimConstraint weight between 0 and 1
    private IEnumerator FadeHeadIK(float targetWeight, float duration)
    {
        if (headAimConstraint == null) yield break;

        float start = headAimConstraint.weight;
        float time = 0f;

        // Clamp inputs
        targetWeight = Mathf.Clamp01(targetWeight);
        duration = Mathf.Max(0f, duration);

        if (duration <= 0f)
        {
            headAimConstraint.weight = targetWeight;
            yield break;
        }

        while (time < duration)
        {
            time += Time.deltaTime;
            float t = Mathf.Clamp01(time / duration);
            headAimConstraint.weight = Mathf.Lerp(start, targetWeight, t);
            yield return null;
        }

        headAimConstraint.weight = targetWeight; // snap to exact final value
    }

    // ---- PUBLIC, VOID methods (UnityEvent/Animation Event friendly) ----

    // Parameterized (shows in UnityEvent): pass 0 = fade out, 1 = fade in
    public void StartFadeHeadIK(float targetWeight)
    {
        // Uses default duration from Inspector
        if (_fadeRoutine != null) StopCoroutine(_fadeRoutine);
        _fadeRoutine = StartCoroutine(FadeHeadIK(targetWeight, defaultFadeDuration));
    }

    // Parameterized with custom duration (also shows in UnityEvent via string overload pattern)
    // Call from code or Animation Event with a float duration.
    public void StartFadeHeadIKWithDuration(float targetWeightAndDuration)
    {
        // If you only need one float in Animation Event, use default above.
        // This overload exists mainly for code use; keep if needed or remove.
        float targetWeight = Mathf.Clamp01(targetWeightAndDuration);
        if (_fadeRoutine != null) StopCoroutine(_fadeRoutine);
        _fadeRoutine = StartCoroutine(FadeHeadIK(targetWeight, defaultFadeDuration));
    }

    // Convenience no-arg buttons (also show in UnityEvent dropdown)
    public void StartFadeInHeadIK()
    {
        if (_fadeRoutine != null) StopCoroutine(_fadeRoutine);
        _fadeRoutine = StartCoroutine(FadeHeadIK(1f, defaultFadeDuration));
    }

    public void StartFadeOutHeadIK()
    {
        if (_fadeRoutine != null) StopCoroutine(_fadeRoutine);
        _fadeRoutine = StartCoroutine(FadeHeadIK(0f, defaultFadeDuration));
    }
}
