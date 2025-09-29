using System.ComponentModel;
using TMPro;
using Unity.Collections;
using Unity.VisualScripting;
using UnityEngine;

public class FirstPromptManager : MonoBehaviour
{
    public GameObject firstPrompt;
    public GameObject mainPrompt;
    public GameObject hasilWindow;
    public GameObject normalResult;
    public GameObject weatherResult;
    public TMP_InputField inputField;
    public TenkiChatController tenkiChatController;
    [SerializeField, Unity.Collections.ReadOnly] private bool isFirstPrompt = true;
    
    private FirstPromptManager instance;
    
    private void Awake()
    {
        if (instance == null)
        {
            instance = this;
        }
        else
        {
            Destroy(gameObject);
        }

        isFirstPrompt = true;
    }

    public void GenerateFirstPrompt()
    {
        if (firstPrompt)
        {
            isFirstPrompt = false;
            var text = inputField != null ? inputField.text.Trim() : "";
            TenkiChatController.instance.StartChatFromExternal(text);
        }
    }

    public void ChooseToActivate(bool value)
    {
        if (isFirstPrompt)
        {
            firstPrompt.SetActive(value);
        }
        else
        {
            mainPrompt.SetActive(value);
            ChooseResultWindow(value);
        }
    }
    
    public void ChooseResultWindow(bool value)
    {
        bool isWeather = tenkiChatController.isAskingWeather;
        if (!value)
        {
            weatherResult.SetActive(false);
            normalResult.SetActive(false);
        }
        else if (isWeather)
        {
            weatherResult.SetActive(true);
            normalResult.SetActive(false);
        }
        else
        {
            weatherResult.SetActive(false);
            normalResult.SetActive(true);
        }
    }
    
}
