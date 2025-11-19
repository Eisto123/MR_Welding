using UnityEngine;
using System.Collections.Generic;

public class IronLoader : MonoBehaviour
{
    public List<GameObject> ironPrefabs;

    private Dictionary<int, GameObject> ironDictionary;

    void Awake()
    {
        SetupIronDictionary();
    }
    private void SetupIronDictionary()
    {
        ironDictionary = new Dictionary<int, GameObject>();
        for (int i = 0; i < ironPrefabs.Count; i++)
        {
            ironDictionary.Add(i, ironPrefabs[i]);
        }
    }

    private void InstantiateIronPrefab(int id, Vector3 position, Quaternion rotation)
    {
        GameObject ironPrefab = GetIronById(id);
        if (ironPrefab != null)
        {
            Instantiate(ironPrefab, position, rotation);
        }
    }

    public void LoadIron(object StepData)
    {
        WeldingStepType stepType = (WeldingStepType)StepData;

        if (stepType == WeldingStepType.PlaceIron)
        {
            // Example: Load iron with ID 0 at origin
            InstantiateIronPrefab(0, transform.position, transform.rotation);
        }
    }

    private GameObject GetIronById(int id)
    {
        if (ironDictionary.TryGetValue(id, out GameObject ironPrefab))
        {
            return ironPrefab;
        }
        Debug.LogWarning($"Iron with ID {id} not found.");
        return null;
    }

}
