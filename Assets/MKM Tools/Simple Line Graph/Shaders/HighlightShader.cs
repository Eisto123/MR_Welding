using UnityEngine;
using UnityEngine.UI;

namespace Shaders
{
    public class HighlightShader : MonoBehaviour
    {
        private void Start()
        {
            Image image = GetComponent<Image>();
            if (image != null && image.material != null)
            {
                image.material.SetColor("_LineColor", image.color);
            }
        }
    }
}