using System.Collections.Generic;
using System.Linq;
using UnityEngine;

namespace UI.Statistics
{
    public class SimpleLineGraphData : ILineGraphData
    {
        private readonly IList<IList<Vector2>> _data;

        public SimpleLineGraphData(params IList<Vector2>[] rawData)
        {
            _data = new List<IList<Vector2>>(rawData);
        }

        (Vector2 min, Vector2 max) ILineGraphData.GetMinMaxValues()
        {
            float maxX = _data[0][0].x;
            float minX = maxX;
            float maxY = _data[0][0].y;
            float minY = maxY;
            foreach (var point in  _data.SelectMany(x => x))
            {
                if (maxX <= point.x) 
                    maxX = point.x;
                if (maxY <= point.y) 
                    maxY = point.y;
                if (minX >= point.x) 
                    minX = point.x;
                if (minY >= point.y) 
                    minY = point.y;
            }

            var max = new Vector2(maxX, maxY);
            var min = new Vector2(minX, minY);
            return (min, max);
        }

        IEnumerable<Vector2> ILineGraphData.this[int lineId] => _data[lineId];

        int ILineGraphData.LinesCount => _data.Count;
    }
}