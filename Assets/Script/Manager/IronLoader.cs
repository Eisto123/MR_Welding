using UnityEngine;
using System.Collections.Generic;

public class IronLoader : MonoBehaviour
{
    public List<GameObject> ironPrefabs;

    private Dictionary<int, GameObject> ironDictionary;
    private GameObject currentIron;

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



}
