using System.Collections.Generic;
using UnityEngine;

public class IronManager : MonoBehaviour
{
    public List<GameObject> irons = new List<GameObject>();
    public List<GameObject> panels = new List<GameObject>();

    public void SetIronVisual(object Step)
    {
        WeldingStepType stepType = (WeldingStepType)Step;
        if(stepType == WeldingStepType.Tacking)
        {
            foreach (var iron in irons)
            {
                iron.GetComponent<Renderer>().material.color = Color.clear;
            }
            foreach (var panel in panels)
            {
                panel.SetActive(false);
            }
            return;
        }
    }
}
