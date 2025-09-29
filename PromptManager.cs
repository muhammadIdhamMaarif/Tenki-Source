using System.Collections;
using Michsky.DreamOS;
using TMPro;
using UnityEngine;

public class PromptManager : MonoBehaviour
{
    [Header("References")]
    public CanvasGroup canvasGroup;
    public TMP_InputField inputField;
    public ButtonManager button;

    private bool isFirst = true;
    private bool mulai = false;

    [Header("Settings")] public float fadeInDuration = 1.0f;
    
    private void Awake()
    {
        if (canvasGroup != null)
        {
            canvasGroup.alpha = 0f;
            canvasGroup.interactable = false;
            canvasGroup.blocksRaycasts = false;
        }
    }
    
    public void ShowPrompt()
    {
        if (canvasGroup != null)
        {
            StartCoroutine(FadeIn(fadeInDuration));
            canvasGroup.interactable = true;
            canvasGroup.blocksRaycasts = true;
        }
        
        if (inputField != null)
        {
            inputField.text = string.Empty;
            inputField.ActivateInputField();
        }
        
        if (button != null)
        {
            button.UpdateUI();
        }
    }

    private IEnumerator FadeIn(float duration)
    {
        float elapsedTime = 0f;
        while (elapsedTime < duration)
        {
            elapsedTime += Time.deltaTime;
            if (canvasGroup != null)
            {
                canvasGroup.alpha = Mathf.Clamp01(elapsedTime / duration);
            }
            yield return null;
        }

        if (canvasGroup != null)
        {
            canvasGroup.alpha = 1f;
        }
    }
}
