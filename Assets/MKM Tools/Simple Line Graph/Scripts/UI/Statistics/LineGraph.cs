using System.Collections.Generic;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace UI.Statistics
{
    public class LineGraph : MonoBehaviour
    {
        public delegate void LineCreated(int lineId, Image line);

        public delegate void DotCreated(int lineId, Image dot);

        public delegate void CriticalValuesFound(Vector2 min, Vector2 max);

        public delegate void NormalizedLineDotsCalculated(int lineId, List<Vector2> positions);

        /// <summary>
        /// Pool for the objects to decrease the number of allocations and destructions 
        /// </summary>
        /// <typeparam name="T"></typeparam>
        private class Pool<T> where T : Component
        {
            private readonly Queue<T> _queue;
            private readonly List<T> _activeObjects;
            private readonly T _prefab;
            private readonly Transform _container;

            public Pool(T prefab, Transform container)
            {
                _prefab = prefab;
                _container = container;
                _queue = new Queue<T>();
                _activeObjects = new List<T>();
            }

            public T PoolObject()
            {
                T image = _queue.Count > 0 ? _queue.Dequeue() : Instantiate(_prefab, _container);
                image.gameObject.SetActive(true);
                _activeObjects.Add(image);
                return image;
            }

            public void Deactivate()
            {
                foreach (var activeObject in _activeObjects)
                {
                    activeObject.gameObject.SetActive(false);
                    _queue.Enqueue(activeObject);
                }

                _activeObjects.Clear();
            }
        }

        [Header("Deps")] [SerializeField] private RectTransform graphContainer;
        [SerializeField] private RectTransform gridContainer;
        [SerializeField] private RectTransform horizontalContainer;
        [SerializeField] private RectTransform verticalContainer;
        [Header("Prefabs")] [SerializeField] private Image dotPrefab;
        [SerializeField] private Image linePrefab;
        [SerializeField] private Image gridLinePrefab;
        [SerializeField] private TMP_Text verticalTextPrefab;
        [SerializeField] private TMP_Text horizontalTextPrefab;

        [Header("Params")] [SerializeField, Min(0)]
        private float dotSize = 5f;

        [SerializeField, Min(0)] private float lineWidth = 5f;
        [SerializeField, Min(1)] private float horizontalDiscretization;
        [SerializeField, Min(1)] private float verticalDiscretization;
        [SerializeField, Min(0)] private float gridLineWidth = 1f;
        [SerializeField] private string prefixForHorizontalText;
        [SerializeField] private string prefixForVerticalText;
        [Header("Flags")] [SerializeField] private bool toDrawHorizontalGridLines = true;
        [SerializeField] private bool toDrawVerticalGridLines = true;
        [SerializeField] private bool toDrawHorizontalNumbers = true;
        [SerializeField] private bool toDrawVerticalNumbers = true;

        private Pool<Image> _dotsPool;
        private Pool<Image> _linesPool;
        private Pool<Image> _gridlLinesPool;
        private Pool<TMP_Text> _horizontalTextPool;
        private Pool<TMP_Text> _verticalTextPool;

        private Vector2 _max;
        private Vector2 _min;
        private LineCreated _lineCreated;
        private DotCreated _dotCreated;
        private CriticalValuesFound _criticalValuesFound;
        private NormalizedLineDotsCalculated _normalizedLineDotsCalculated;
        private List<Image> _outlines;


        private void Awake()
        {
            _dotsPool = new Pool<Image>(dotPrefab, graphContainer);
            _linesPool = new Pool<Image>(linePrefab, graphContainer);
            _gridlLinesPool = new Pool<Image>(gridLinePrefab, gridContainer);
            _horizontalTextPool = new Pool<TMP_Text>(horizontalTextPrefab, horizontalContainer);
            _verticalTextPool = new Pool<TMP_Text>(verticalTextPrefab, verticalContainer);
        }

        /// <summary>
        /// Plots the Graph, cleaning it beforehand
        /// </summary>
        /// <param name="dataPoints">The given dataset</param>
        /// <param name="dotCreated">Called when the dot is put on graph</param>
        /// <param name="lineCreated">Called when the line is put on graph</param>
        /// <param name="criticalValuesFound">Called when the minimum and maximum values are estimated</param>
        public void PlotGraph(ILineGraphData dataPoints, DotCreated dotCreated = null,
            LineCreated lineCreated = null, CriticalValuesFound criticalValuesFound = null,
            NormalizedLineDotsCalculated normalizedLineDotsCalculated = null)
        {
            Clear();

            _dotCreated = dotCreated;
            _lineCreated = lineCreated;
            _criticalValuesFound = criticalValuesFound;
            _normalizedLineDotsCalculated = normalizedLineDotsCalculated;

            if (dataPoints == null || dataPoints.LinesCount == 0) return;

            (_min, _max) = dataPoints.GetMinMaxValues();

            _criticalValuesFound?.Invoke(_min, _max);

            PlotGrid();

            for (int i = 0; i < dataPoints.LinesCount; i++)
            {
                PlotLine(dataPoints[i], i);
            }
        }

        private void PlotGrid()
        {
            var gridSize = gridContainer.rect.size;
            var segmentsCount = (_max - _min) / Discretization;

            PlotInitialGridUI(gridSize);

            if (toDrawVerticalNumbers || toDrawHorizontalGridLines)
            {
                for (float i = 0, number = 0; i < gridSize.y; i += gridSize.y / segmentsCount.y, ++number)
                {
                    if (toDrawVerticalNumbers)
                        DrawVerticalNumber($"{_min.y + number * Discretization.y:F0}", i);

                    if (toDrawHorizontalGridLines)
                        DrawHorizontalLine(i);
                }
            }

            if (toDrawHorizontalNumbers || toDrawVerticalGridLines)
            {
                for (float i = 0, number = 0; i < gridSize.x; i += gridSize.x / segmentsCount.x, ++number)
                {
                    if (toDrawHorizontalNumbers)
                        DrawHorizontalNumber($"{_min.x + number * Discretization.x:F0}", i);

                    if (toDrawVerticalGridLines)
                        DrawVerticalLine(i);
                }
            }
        }

        private void PlotInitialGridUI(Vector2 gridSize)
        {
            DrawHorizontalLine(0);
            DrawVerticalLine(0);

            if (toDrawVerticalNumbers)
                DrawVerticalNumber($"{_max.y:F0}", gridSize.y);

            if (toDrawHorizontalNumbers)
                DrawHorizontalNumber($"{_max.x:F0}", gridSize.x);
        }

        private void DrawHorizontalLine(float i)
        {
            var gridSize = gridContainer.rect.size;
            var lineObject = _gridlLinesPool.PoolObject();

            RectTransform rectTransform = (RectTransform)lineObject.transform;
            rectTransform.sizeDelta = new Vector2(gridSize.x, gridLineWidth);
            rectTransform.anchorMin = new Vector2(0, 0);
            rectTransform.anchorMax = new Vector2(0, 0);
            rectTransform.pivot = new Vector2(0f, 0.5f);
            rectTransform.anchoredPosition = new Vector2(0, i);
        }

        private void DrawVerticalLine(float f)
        {
            var gridSize = gridContainer.rect.size;
            var lineObject = _gridlLinesPool.PoolObject();

            RectTransform rectTransform = (RectTransform)lineObject.transform;
            rectTransform.sizeDelta = new Vector2(gridLineWidth, gridSize.y);
            rectTransform.anchorMin = new Vector2(0, 0);
            rectTransform.anchorMax = new Vector2(0, 0);
            rectTransform.pivot = new Vector2(0.5f, 0f);
            rectTransform.anchoredPosition = new Vector2(f, 0);
        }

        private void DrawVerticalNumber(string number, float yPosition)
        {
            if (!string.IsNullOrEmpty(prefixForVerticalText)) number = prefixForVerticalText + number;

            var text = _verticalTextPool.PoolObject();

            text.text = number;
            text.fontSize = gridLineWidth * 5f;

            RectTransform rectTransform = (RectTransform)text.transform;
            rectTransform.anchorMin = new Vector2(1, 0);
            rectTransform.anchorMax = new Vector2(1, 0);
            rectTransform.pivot = new Vector2(1f, 0.5f);
            rectTransform.anchoredPosition = new Vector2(0, yPosition);
        }

        private void DrawHorizontalNumber(string number, float xPosition)
        {
            if (!string.IsNullOrEmpty(prefixForHorizontalText)) number = prefixForHorizontalText + number;

            var text = _horizontalTextPool.PoolObject();

            text.text = number;
            text.fontSize = gridLineWidth * 5f;

            RectTransform rectTransform = (RectTransform)text.transform;
            rectTransform.anchorMin = new Vector2(0, 1);
            rectTransform.anchorMax = new Vector2(0, 1);
            rectTransform.pivot = new Vector2(0.5f, 1f);
            rectTransform.anchoredPosition = new Vector2(xPosition, 0);
        }

        private void PlotLine(IEnumerable<Vector2> dataPoints, int id)
        {
            if (dataPoints == null) return;

            var graphSize = graphContainer.rect.size;

            Vector2 ConvertToUIPos(Vector2 data)
            {
                return Normalize(data) * graphSize;
            }


            using (var enumerator = dataPoints.GetEnumerator())
            {
                Vector2 firstPoint = Vector2.zero;

                if (enumerator.MoveNext())
                {
                    firstPoint = enumerator.Current;
                }
                else
                    return;

                var allDots = new List<Vector2>();
                while (enumerator.MoveNext())
                {
                    var secondPoint = enumerator.Current;

                    var firstUIPoint = ConvertToUIPos(firstPoint);
                    var secondUIPoint = ConvertToUIPos(secondPoint);
                    var dot = CreatePoint(firstUIPoint);
                    var linePart = CreateLine(firstUIPoint, secondUIPoint);

                    _dotCreated?.Invoke(id, dot);
                    _lineCreated?.Invoke(id, linePart);

                    allDots.Add(Normalize(firstPoint));

                    firstPoint = secondPoint; // Shift to the next point
                }

                var lastUIPoint = ConvertToUIPos(firstPoint);
                var lastDot = CreatePoint(lastUIPoint);
                _dotCreated?.Invoke(id, lastDot);

                allDots.Add(Normalize(firstPoint));
                _normalizedLineDotsCalculated?.Invoke(id, allDots);
            }
        }

        private Vector2 Normalize(Vector2 data)
        {
            //value = (value - inMin) * (outMax - outMin) / (inMax - inMin) + outMin;
            Vector2 normalizedDataPos = (data - _min) / (_max - _min);
            return normalizedDataPos;
        }

        private Image CreatePoint(Vector2 anchoredPosition)
        {
            var pointObject = _dotsPool.PoolObject();

            RectTransform rectTransform = (RectTransform)pointObject.transform;
            rectTransform.SetAsLastSibling();
            rectTransform.sizeDelta = new Vector2(dotSize, dotSize);
            rectTransform.anchorMin = new Vector2(0, 0);
            rectTransform.anchorMax = new Vector2(0, 0);
            rectTransform.anchoredPosition = anchoredPosition;

            return pointObject;
        }

        private Image CreateLine(Vector2 start, Vector2 end)
        {
            var lineObject = _linesPool.PoolObject();

            RectTransform rectTransform = (RectTransform)lineObject.transform;
            rectTransform.SetAsFirstSibling();
            Vector2 direction = (end - start).normalized;
            float distance = Vector2.Distance(start, end);
            rectTransform.sizeDelta = new Vector2(distance, lineWidth);
            rectTransform.anchorMin = new Vector2(0, 0);
            rectTransform.anchorMax = new Vector2(0, 0);
            rectTransform.anchoredPosition = start + direction * distance * 0.5f;
            rectTransform.localEulerAngles = new Vector3(0, 0, Mathf.Atan2(direction.y, direction.x) * Mathf.Rad2Deg);

            return lineObject;
        }

        /// <summary>
        /// Clear all Dots, Lines, Grid lines, and text
        /// </summary>
        public void Clear()
        {
            _dotsPool.Deactivate();
            _linesPool.Deactivate();
            _gridlLinesPool.Deactivate();
            _horizontalTextPool.Deactivate();
            _verticalTextPool.Deactivate();
            if (_outlines != null)
            {
                _outlines.ForEach(x => Destroy(x.gameObject));
                _outlines = null;
            }
        }

        /// <summary>
        /// Sets the min and max values showed on graph
        /// </summary>
        /// <param name="min">Minimum value</param>
        /// <param name="max">Maximum value</param>
        public void SetMinMaxOnGraph(Vector2 min, Vector2 max)
        {
            _min = min;
            _max = max;
        }

        /// <summary>
        /// Draws the highlight of the line based on the normilized dot positions.
        /// </summary>
        /// <param name="positions">Normalized positions of dots on graph</param>
        /// <param name="color">Color of highlight</param>
        /// <param name="lineHighlightPrefab">Prefab of the image on which highlight is applied</param>
        public void DrawHighlight(List<Vector2> positions, Color color, Image lineHighlightPrefab)
        {
            var outlines = Utils.Utils.DrawHighlight(positions, color, lineHighlightPrefab, graphContainer, OnHighlightPartCreated);
            if (_outlines == null)
            {
                _outlines = outlines;
            }
            else
            {
                _outlines.AddRange(outlines);
            }
        }

        private void OnHighlightPartCreated(RectTransform arg0)
        {
            arg0.SetAsFirstSibling();
        }

        /// <summary>
        /// Discretization of the graph
        /// </summary>
        public Vector2 Discretization
        {
            get => new Vector2(horizontalDiscretization, verticalDiscretization);
            set
            {
                horizontalDiscretization = value.x;
                verticalDiscretization = value.y;
            }
        }

        public bool ToDrawHorizontalGridLines
        {
            get => toDrawHorizontalGridLines;
            set => toDrawHorizontalGridLines = value;
        }

        public bool ToDrawVerticalGridLines
        {
            get => toDrawVerticalGridLines;
            set => toDrawVerticalGridLines = value;
        }

        public bool ToDrawHorizontalNumbers
        {
            get => toDrawHorizontalNumbers;
            set => toDrawHorizontalNumbers = value;
        }

        public bool ToDrawVerticalNumbers
        {
            get => toDrawVerticalNumbers;
            set => toDrawVerticalNumbers = value;
        }

        public string PrefixForHorizontalText
        {
            get => prefixForHorizontalText;
            set => prefixForHorizontalText = value;
        }

        public string PrefixForVerticalText
        {
            get => prefixForVerticalText;
            set => prefixForVerticalText = value;
        }
    }
}