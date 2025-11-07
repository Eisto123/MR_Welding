using UnityEngine;
using UnityEngine.Events;

public class BaseEventSO<T> : ScriptableObject
{
    public string description;
    public UnityAction<T> onEventRaised;
    public string lastSender;

    public void RaiseEvent(T value, object sender)
    {
        onEventRaised?.Invoke(value);
        lastSender = sender.ToString();
    }
}
