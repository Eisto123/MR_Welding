using UnityEngine;
using UnityEngine.Events;

public class BaseEventListener<T> : MonoBehaviour
{
    public BaseEventSO<T> eventSO;
    public UnityEvent<T> response;

    private void OnEnable()
    {
        if (eventSO != null)
        {
            eventSO.onEventRaised += RaiseEvent;
        }
    }
    private void OnDisable()
    {
        if (eventSO != null)
        {
            eventSO.onEventRaised -= RaiseEvent;
        }
    }

    private void RaiseEvent(T value)
    {
        response.Invoke(value);
    }
}
