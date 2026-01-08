using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;

namespace UI.Statistics
{
    public class FunctionLineGraphData : ILineGraphData
    {
        public readonly struct Function
        {
            public readonly Func<float, float> Func;
            public readonly IList<float> XValues;

            public Function(Func<float, float> func, IList<float> xValues)
            {
                Func = func;
                XValues = xValues;
            }
        }

        private readonly IList<Function> _lines;
        
        public FunctionLineGraphData(params Function[] lines)
        {
            _lines = lines;
        }

        IEnumerable<Vector2> ILineGraphData.this[int lineId]
        {
            get
            {
                var line = _lines[lineId];
                foreach (var x in line.XValues)
                {
                    yield return new Vector2(x, line.Func(x));
                }
            }
        }

        int ILineGraphData.LinesCount => _lines.Count;

        (Vector2 min, Vector2 max) ILineGraphData.GetMinMaxValues()
        {
            var allValues = _lines.SelectMany((x, i) => ((ILineGraphData)this)[i]);
            var first = allValues.First();
            float maxX = first.x;
            float minX = maxX;
            float maxY = first.y;
            float minY = maxY;
            foreach (var point in allValues)
            {
                if (maxX <= point.x) maxX = point.x;
                if (maxY <= point.y) maxY = point.y;
                if (minX >= point.x) minX = point.x;
                if (minY >= point.y) minY = point.y;
            }

            var max = new Vector2(maxX, maxY);
            var min = new Vector2(minX, minY);
            return (min, max);
        }
    }
}