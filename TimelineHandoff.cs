using UnityEngine;
using UnityEngine.Playables;

public class TimelineHandoff : MonoBehaviour
{
    [SerializeField] PlayableDirector director;
    [SerializeField] Animator animator;
    [SerializeField] string idleStateName = "Idle"; // adjust to your state path

    void Awake()
    {
        if (!director) director = GetComponent<PlayableDirector>();
        if (!animator) animator = GetComponent<Animator>();
        director.stopped += OnTimelineStopped;
    }

    public void TimelineStopped() => OnTimelineStopped(director);
    void OnTimelineStopped(PlayableDirector pd)
    {
        // Ensure Timeline releases any influence and Animator resets cleanly
        animator.Rebind();       // resets bindings to default values
        animator.Update(0f);     // immediately apply
        // Optionally force idle if you use a triggerless entry state:
        // animator.Play(idleStateName, 0, 0f);
    }
}