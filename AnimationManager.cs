using UnityEngine;
using System.Collections;

public class AnimationManager : MonoBehaviour
{
    public Animator animator;

    public void PlayAnimation(string animationName)
    {
        if (animator == null)
        {
            Debug.LogWarning("No Animator component found on this GameObject.");
            return;
        }

        animator.Play(animationName);
    }
    
    public void SetSelesaiState(bool value)
    {
        animator.SetBool("selesai", value);
    }
}