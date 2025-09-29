using Michsky.DreamOS;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

public class EnterDetection1 : MonoBehaviour
{
    [SerializeField] private TMP_InputField inputField;
    [SerializeField] private Button buttonTarget;
    
    void Start()
    {
        // Optionally: when using single line input, this fires automatically
        inputField.onSubmit.AddListener(OnSubmitInput);
    }

    void Update()
    {
        // Works for both single-line and multi-line inputs
        if (inputField.isFocused && Input.GetKeyDown(KeyCode.Return))
        {
            TriggerButtonClick();
        }
    }

    private void OnSubmitInput(string text)
    {
        TriggerButtonClick();
    }

    private void TriggerButtonClick()
    {
        if (buttonTarget != null)
        {
            Debug.Log("Enter pressed, triggering button: " + buttonTarget.name);
            buttonTarget.onClick.Invoke(); // Simulate button click
        }
    }

}