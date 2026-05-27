using UdonSharp;
using UnityEngine;

namespace Yamadev.YamaStream.UI
{
  [UdonBehaviourSyncMode(BehaviourSyncMode.None)]
  public class IndexTrigger : UdonSharpBehaviour
  {
    [SerializeField] private UdonSharpBehaviour _udon;
    [SerializeField] private string _variableName;
    [SerializeField] private string _variableValue;
    [SerializeField] private object _variableObject;
    [SerializeField] private string _eventName;

    [HideInInspector] public bool _useIntValue;
    [HideInInspector] public int _intValue;

    public void OnButtonClick()
    {
      Debug.Log("[IndexTrigger] OnButtonClick — _udon=" + (_udon != null ? _udon.name : "NULL")
        + " name=" + _variableName + " intValue=" + _intValue + " evt=" + _eventName);

      if (_useIntValue)
        _udon.SetProgramVariable(_variableName, _intValue);
      else if (!string.IsNullOrEmpty(_variableValue))
        _udon.SetProgramVariable(_variableName, _variableValue);
      else
        _udon.SetProgramVariable(_variableName, _variableObject);

      _udon.SendCustomEvent(_eventName);
    }
  }
}