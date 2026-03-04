using System;
using System.Collections.Generic;
using TMPro;
using UnityEngine;

[Serializable]
public class StepInstructionEntry
{
    public WeldingStepType stepType;
    public string instructionName;
    [TextArea(3, 8)] public string instructionParagraph;
}

public class InstructionPanelManager : MonoBehaviour
{
    [Header("UI References")]
    [SerializeField] private TMP_Text instructionNameText;
    [SerializeField] private TMP_Text instructionParagraphText;

    [Header("Step Instructions (Editable in Inspector)")]
    [SerializeField] private List<StepInstructionEntry> stepInstructions = new List<StepInstructionEntry>();

    public void SetInstruction(WeldingStepType stepType)
    {
        StepInstructionEntry entry = stepInstructions.Find(x => x.stepType == stepType);

        if (entry == null)
        {
            Debug.LogWarning($"No instruction entry found for step: {stepType}");
            if (instructionNameText != null) instructionNameText.text = stepType.ToString();
            if (instructionParagraphText != null) instructionParagraphText.text = string.Empty;
            return;
        }

        if (instructionNameText != null) instructionNameText.text = entry.instructionName;
        if (instructionParagraphText != null) instructionParagraphText.text = entry.instructionParagraph;
    }
}
