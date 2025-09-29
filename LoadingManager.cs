using System.Collections;
using UnityEngine;
using UnityEngine.UI;
using TMPro;
using UnityEngine.SceneManagement;
using UnityEngine.ResourceManagement.AsyncOperations;
using UnityEngine.AddressableAssets;

public class LoadingManager : MonoBehaviour
{
    [Header("UI")]
    public Slider progressSlider;
    public TMP_Text progressText;
    [Header("Settings")]
    public string addressablesLabel = "main-deps";
    public string mainSceneName = "01_Main";

    IEnumerator Start()
    {
        // Always show something instantly
        UpdateUI(0f, "Initializing...");

        // 1) Init Addressables
        var init = Addressables.InitializeAsync();
        yield return init;

        // 2) Download/Load dependencies for your main scene/assets
        UpdateUI(0f, "Downloading assets...");
        var downloadHandle = Addressables.DownloadDependenciesAsync(addressablesLabel, true);
        while (!downloadHandle.IsDone)
        {
            UpdateUI(downloadHandle.PercentComplete * 0.6f, "Downloading assets...");
            yield return null;
        }

        // 3) Optionally load some key prefabs or ScriptableObjects into memory
        // If you have AssetReferences, you could call Addressables.LoadAssetAsync<T>
        // here, or rely on scene to load them later. We’ll keep it label-based.

        // 4) Load the main scene asynchronously (additive or single)
        UpdateUI(0.7f, "Loading scene...");
        var sceneLoad = SceneManager.LoadSceneAsync(mainSceneName, LoadSceneMode.Single);
        sceneLoad.allowSceneActivation = false;

        // Simulate fold-in of remaining progress
        while (sceneLoad.progress < 0.9f)
        {
            float p = 0.7f + sceneLoad.progress * 0.3f; // 70% → ~97%
            UpdateUI(p, "Loading scene...");
            yield return null;
        }

        // 5) All ready – small finalization
        UpdateUI(0.98f, "Finalizing...");
        yield return new WaitForSeconds(0.1f);

        // 6) Switch!
        UpdateUI(1f, "Done");
        sceneLoad.allowSceneActivation = true;
    }

    void UpdateUI(float normalized, string message)
    {
        if (progressSlider) progressSlider.value = normalized;
        if (progressText) progressText.text = $"{message} {(int)(normalized * 100f)}%";
    }
}
