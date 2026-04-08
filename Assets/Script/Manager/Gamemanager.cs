using UnityEngine;

public class Gamemanager : MonoBehaviour
{
    public void LoadSceneByName(string sceneName)
    {
        if (string.IsNullOrWhiteSpace(sceneName))
        {
            Debug.LogWarning("SceneLoader: sceneName is null or empty.");
            return;
        }

        SceneLoader.Instance.LoadSceneByName(sceneName);
    }
}
