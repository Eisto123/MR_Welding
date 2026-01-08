using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using TMPro;
using UnityEngine;
using UnityEngine.Serialization;
using UnityEngine.UI;

namespace UI.Statistics
{
    public class LineGraphTest : MonoBehaviour
    {
        private interface IColorPick
        {
            Color Color { get; }
        }

        private enum Type
        {
            Mock,
            Function,
            Error
        }

        [Serializable]
        private class MockLine : IList<Vector2>, IColorPick
        {
            [FormerlySerializedAs("Data")] [SerializeField]
            private List<Vector2> data;

            [FormerlySerializedAs("Color")] [SerializeField]
            private Color color;

            IEnumerator IEnumerable.GetEnumerator()
            {
                return GetEnumerator();
            }

            public IEnumerator<Vector2> GetEnumerator() => data.GetEnumerator();

            public void Add(Vector2 item) => data.Add(item);

            public void Clear() => data.Clear();

            public bool Contains(Vector2 item) => data.Contains(item);

            public void CopyTo(Vector2[] array, int arrayIndex) => data.CopyTo(array, arrayIndex);

            public bool Remove(Vector2 item) => data.Remove(item);

            public int Count => data.Count;
            public bool IsReadOnly => ((IList<Vector2>)data).IsReadOnly;
            public int IndexOf(Vector2 item) => data.IndexOf(item);

            public void Insert(int index, Vector2 item) => data.Insert(index, item);

            public void RemoveAt(int index) => data.RemoveAt(index);

            public Vector2 this[int index]
            {
                get => data[index];
                set => data[index] = value;
            }

            public Color Color => color;
        }

        [Serializable]
        private class FunctionLine : IColorPick
        {
            public List<float> xValues;
            [SerializeField] private Color color;

            public Color Color => color;
        }

        private class ColorPick : IColorPick
        {
            public ColorPick(Color color)
            {
                Color = color;
            }

            public Color Color { get; }
        }

        [SerializeField] private LineGraph lineGraph;
        [SerializeField] private MockLine[] data;
        [SerializeField] private FunctionLine[] funcData;
        [SerializeField] private Type type;
        [SerializeField] private TMP_Dropdown dropdown;
        [SerializeField] private Image lineHighlightPrefab;
        [SerializeField] private bool changeDiscritization;
        private IList<IColorPick> _colorPick;

        private void Start()
        {
            dropdown.ClearOptions();
            var typeNames = Enum.GetNames(typeof(Type)).ToList();
            dropdown.AddOptions(typeNames);
            dropdown.value = (int)type;
            dropdown.RefreshShownValue();
            dropdown.onValueChanged.AddListener(SetType);
            Plot();
        }

        private void SetType(int arg0)
        {
            var setType = (Type)arg0;
            type = setType;
            lineGraph.Clear();
            Plot();
        }

        public void Plot()
        {
            ILineGraphData selectedData = null;
            switch (type)
            {
                case Type.Mock:
                    selectedData = new SimpleLineGraphData(data);
                    break;
                case Type.Function:
                    selectedData = new FunctionLineGraphData(funcData
                        .Select((functionLine, i) =>
                        {
                            Func<float, float> function = null;
                            switch (i % 3)
                            {
                                case 0:
                                    function = (x) => 100 + 2 * x;
                                    break;
                                case 1:
                                    function = (x) => 150 - 1.5f * x;
                                    break;
                                case 2:
                                    function = (x) => 0.05f * x * x + 100;
                                    break;
                                default: throw new NotImplementedException("Function is not created");
                            }

                            return new FunctionLineGraphData.Function(function, functionLine.xValues);
                        })
                        .ToArray()
                    );
                    break;
                case Type.Error:
                    selectedData = new SimpleLineGraphData(
                        new[] { Vector2.one * 0.5f, },
                        new List<Vector2>(), new[] { Vector2.zero, Vector2.up, Vector2.one, Vector2.right, }
                    );
                    break;
                default:
                    throw new ArgumentException("Undefined Type");
            }

            switch (type)
            {
                case Type.Mock:
                    _colorPick = data;
                    break;
                case Type.Function:
                    _colorPick = funcData;
                    break;
                case Type.Error:
                    _colorPick = new List<IColorPick>()
                        { new ColorPick(Color.blue), new ColorPick(Color.black), new ColorPick(Color.red) };
                    break;
                default: throw new ArgumentException("Undefined Type");
            }

            lineGraph.PlotGraph(selectedData, DotCreated, LineCreated, CriticalValuesFound,
                NormalizedLineDotsCalculated);
        }

        private void NormalizedLineDotsCalculated(int lineId, List<Vector2> positions)
        {
            if (lineHighlightPrefab != null)
                lineGraph.DrawHighlight(positions, _colorPick[lineId].Color, lineHighlightPrefab);
        }

        private void CriticalValuesFound(Vector2 min, Vector2 max)
        {
            if (type == Type.Error)
            {
                lineGraph.SetMinMaxOnGraph(Vector2.zero, Vector2.one * 3f);
                lineGraph.Discretization = Vector2.one;
            }
            else if (changeDiscritization)
            {
                lineGraph.Discretization = new Vector2(10, 50);
            }
        }

        private void LineCreated(int lineId, Image line)
        {
            line.color = _colorPick[lineId].Color;
        }

        private void DotCreated(int lineId, Image dot)
        {
            var main = dot.transform.GetChild(0);
            var mainImage = main.GetComponent<Image>();
            mainImage.color = _colorPick[lineId].Color;
        }
    }
}