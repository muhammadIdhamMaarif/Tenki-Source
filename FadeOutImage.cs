using System;
using System.Collections;
using UnityEngine;
using UnityEngine.Events;
using UnityEngine.Playables;
using UnityEngine.UI;

public class FadeOutImage : MonoBehaviour
{
    public Image imageToFade;
    public float fadeDuration = 1.0f;
    public TenkiChatController chatController;
    public PlayableDirector playableDirector;
    private bool isFirst = true;
    public UnityEvent onApiKeyLoaded;

    void Awake()
    {
        if (imageToFade != null)
        {
            imageToFade.gameObject.SetActive(true);
            imageToFade.color = new Color(imageToFade.color.r, imageToFade.color.g, imageToFade.color.b, 1.0f);
        }
    }

    private void Update()
    {
        if (isFirst && !string.IsNullOrEmpty(chatController.OpenAIApiKey) &&
            !string.IsNullOrEmpty(chatController.WeatherApiKey) && !string.IsNullOrEmpty(chatController.ElevenLabsApiKey))
        {
            isFirst = false;
            onApiKeyLoaded?.Invoke();
            playableDirector.Play();
            StartCoroutine(FadeOut());
        }
    }

    public void FadeOutPublic() => StartCoroutine(FadeOut());
    public void FadeInPublic() => StartCoroutine(FadeIn());
    public void OverlayDisplayPublic() => StartCoroutine(OnUIOverlayDisplay());
    public void OverlayHidePublic() => StartCoroutine(OnUIOverlayHide());

    // Default fade out (to alpha = 0)
    private IEnumerator FadeOut()
    {
        yield return FadeToAlpha(fadeDuration, 0f);
    }

    // Default fade in (to alpha = 1)
    private IEnumerator FadeIn()
    {
        yield return FadeToAlpha(fadeDuration, 1f);
    }
    
    public IEnumerator OnUIOverlayHide()
    {
        yield return FadeToAlpha(0.2f,0f);
    }

    public IEnumerator OnUIOverlayDisplay()
    {
        yield return FadeToAlpha(0.2f,250f/256f); // semi-transparent black
    }
    
    // General fade function (replaces FadeOutCustomValue)
    public IEnumerator FadeToAlpha(float duration, float targetAlpha)
    {
        float elapsedTime = 0f;
        Color originalColor = imageToFade.color;
        float startAlpha = originalColor.a;

        while (elapsedTime < duration)
        {
            elapsedTime += Time.deltaTime;
            float alpha = Mathf.Lerp(startAlpha, targetAlpha, elapsedTime / duration);
            imageToFade.color = new Color(originalColor.r, originalColor.g, originalColor.b, alpha);
            yield return null;
        }

        // Ensure final alpha
        imageToFade.color = new Color(originalColor.r, originalColor.g, originalColor.b, targetAlpha);
    }
}