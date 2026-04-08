using System.Collections;
using UnityEngine;
using UnityEngine.SceneManagement;

public class SceneLoader : MonoBehaviour
{
    public static SceneLoader Instance { get; private set; }

    [Header("Fade Settings")]
    [SerializeField] private string fadeTag = "FadeScreen";
    [SerializeField] private float fadeDuration = 0.5f;
    [SerializeField] private int maxFadeLookupFrames = 30;

    private static readonly int ColorId = Shader.PropertyToID("_Color");
    private static readonly int BaseColorId = Shader.PropertyToID("_BaseColor");

    private bool isTransitioning;

    private struct FadeVisual
    {
        public Material Material;
        public int ColorPropertyId;
    }

    private void Awake()
    {
        if (Instance != null && Instance != this)
        {
            Destroy(gameObject);
            return;
        }

        Instance = this;
        DontDestroyOnLoad(gameObject);
    }

    public void LoadSceneByName(string sceneName)
    {
        if (string.IsNullOrWhiteSpace(sceneName))
        {
            Debug.LogWarning("SceneLoader: sceneName is null or empty.");
            return;
        }

        if (isTransitioning)
        {
            Debug.LogWarning("SceneLoader: Transition already in progress.");
            return;
        }

        StartCoroutine(LoadSceneWithFade(sceneName));
    }

    private IEnumerator LoadSceneWithFade(string sceneName)
    {
        isTransitioning = true;

        // Fade out using the current scene's FadeScreen
        if (TryFindFadeVisual(out FadeVisual currentFade))
        {
            yield return FadeAlpha(currentFade, 1f, fadeDuration);
        }
        else
        {
            Debug.LogWarning($"SceneLoader: No valid FadeScreen found in active scene '{SceneManager.GetActiveScene().name}'.");
        }

        AsyncOperation loadOperation = SceneManager.LoadSceneAsync(sceneName);
        if (loadOperation == null)
        {
            Debug.LogError($"SceneLoader: Failed to load scene '{sceneName}'.");
            isTransitioning = false;
            yield break;
        }

        while (!loadOperation.isDone)
        {
            yield return null;
        }

        // Fade in using the newly loaded scene's FadeScreen
        FadeVisual nextFade = default;
        bool foundNextFade = false;

        int attempts = Mathf.Max(1, maxFadeLookupFrames);
        for (int i = 0; i < attempts; i++)
        {
            if (TryFindFadeVisual(out nextFade))
            {
                foundNextFade = true;
                break;
            }

            yield return null;
        }

        if (foundNextFade)
        {
            SetAlpha(nextFade, 1f);
            yield return FadeAlpha(nextFade, 0f, fadeDuration);
        }
        else
        {
            Debug.LogWarning($"SceneLoader: No valid FadeScreen found after loading scene '{sceneName}'.");
        }

        isTransitioning = false;
    }

    private bool TryFindFadeVisual(out FadeVisual fadeVisual)
    {
        fadeVisual = default;

        GameObject fadeObject;
        try
        {
            fadeObject = GameObject.FindGameObjectWithTag(fadeTag);
        }
        catch (UnityException ex)
        {
            Debug.LogError($"SceneLoader: Tag '{fadeTag}' is not defined. {ex.Message}");
            return false;
        }

        if (fadeObject == null)
        {
            return false;
        }

        Renderer fadeRenderer = fadeObject.GetComponent<Renderer>();
        if (fadeRenderer == null)
        {
            Debug.LogWarning($"SceneLoader: Object tagged '{fadeTag}' has no Renderer component.");
            return false;
        }

        Material material = fadeRenderer.material;
        if (material == null)
        {
            Debug.LogWarning($"SceneLoader: FadeScreen on '{fadeObject.name}' has no material.");
            return false;
        }

        int colorPropertyId;
        if (material.HasProperty(BaseColorId))
        {
            colorPropertyId = BaseColorId;
        }
        else if (material.HasProperty(ColorId))
        {
            colorPropertyId = ColorId;
        }
        else
        {
            Debug.LogWarning($"SceneLoader: Material on '{fadeObject.name}' has neither _BaseColor nor _Color.");
            return false;
        }

        fadeVisual = new FadeVisual
        {
            Material = material,
            ColorPropertyId = colorPropertyId
        };

        return true;
    }

    private IEnumerator FadeAlpha(FadeVisual fadeVisual, float targetAlpha, float duration)
    {
        float startAlpha = GetAlpha(fadeVisual);
        float elapsed = 0f;
        float safeDuration = Mathf.Max(0.0001f, duration);

        while (elapsed < safeDuration)
        {
            elapsed += Time.unscaledDeltaTime;
            float t = Mathf.Clamp01(elapsed / safeDuration);
            float alpha = Mathf.Lerp(startAlpha, targetAlpha, t);
            SetAlpha(fadeVisual, alpha);
            yield return null;
        }

        SetAlpha(fadeVisual, targetAlpha);
    }

    private float GetAlpha(FadeVisual fadeVisual)
    {
        return fadeVisual.Material.GetColor(fadeVisual.ColorPropertyId).a;
    }

    private void SetAlpha(FadeVisual fadeVisual, float alpha)
    {
        Color color = fadeVisual.Material.GetColor(fadeVisual.ColorPropertyId);
        color.a = Mathf.Clamp01(alpha);
        fadeVisual.Material.SetColor(fadeVisual.ColorPropertyId, color);
    }
}
