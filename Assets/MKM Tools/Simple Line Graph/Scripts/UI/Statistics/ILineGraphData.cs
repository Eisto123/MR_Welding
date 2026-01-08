using System.Collections.Generic;
using UnityEngine;

namespace UI.Statistics
{
    public interface ILineGraphData
    {
        IEnumerable<Vector2> this[int lineId] { get; }
        int LinesCount { get; }
        (Vector2 min, Vector2 max) GetMinMaxValues();
    }
}