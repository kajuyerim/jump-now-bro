using System;
using UnityEngine;
using JumpNowBro.Util;

namespace JumpNowBro.Gameplay
{
    public class ControlMapStore : MonoBehaviour
    {
        public static ControlMapStore Instance { get; private set; }

        public ControlMap Current { get; private set; } = ControlMap.Default;
        public event Action<ControlMap> OnChanged;

        void Awake()
        {
            if (Instance != null && Instance != this)
            {
                Destroy(gameObject);
                return;
            }
            Instance = this;
            DontDestroyOnLoad(gameObject);
        }

        // Publishes map changes from scheduled swaps, session sync, respawn and level resets.
        public void Apply(ControlMap newMap)
        {
            Current = newMap;
            OnChanged?.Invoke(newMap);
        }
    }
}
