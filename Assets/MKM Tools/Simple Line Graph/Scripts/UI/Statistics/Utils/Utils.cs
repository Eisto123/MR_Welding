using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Events;
using UnityEngine.UI;
using Object = UnityEngine.Object;

namespace UI.Statistics.Utils
{
    public static class Utils
    {
        /// <summary>
        /// Draws the highlight of the line based on the normilized dot positions.
        /// </summary>
        /// <param name="positions">Normalized positions of dots on graph</param>
        /// <param name="color">Color of highlight</param>
        /// <param name="lineHighlightPrefab">Prefab of the image on which highlight is applied</param>
        /// <param name="container">Container where the highlight is spawned</param>
        public static List<Image> DrawHighlight(List<Vector2> positions, Color color, Image lineHighlightPrefab,
            Transform container, UnityAction<RectTransform> onHighlightPartCreated = null)
        {
            var material = Object.Instantiate(lineHighlightPrefab.material);

            var outlines = new List<Image>(positions.Count - 1);
            for (ushort i = 0; i < positions.Count - 1; ++i)
            {
                var outline = Object.Instantiate(lineHighlightPrefab, container);
                outline.color = color;
                
                RectTransform rectTransform = (RectTransform)outline.transform;
                rectTransform.anchorMin = Vector2.zero;
                rectTransform.anchorMax = Vector2.one;
                rectTransform.offsetMax = rectTransform.offsetMin = Vector2.zero;
                onHighlightPartCreated?.Invoke(rectTransform);

                var sprite = outline.sprite = Object.Instantiate(lineHighlightPrefab.sprite);
                outline.material = material;
                
                outlines.Add(outline);

                var rect = sprite.textureRect;
                var vertices = new[]
                {
                    new Vector2(positions[i].x * rect.width, 0), positions[i] * rect.size,
                    positions[i + 1] * rect.size, new Vector2(positions[i + 1].x * rect.width, 0)
                };
                sprite.OverrideGeometry(vertices, new ushort[] { 0, 1, 2, 0, 2, 3 });
            }

            return outlines;
        }
    }
}