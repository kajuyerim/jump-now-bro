using UnityEngine;

namespace JumpNowBro.Gameplay
{
    public enum SimulationGate { Gameplay, Timeline, SceneLoading }

    /// Fixed-step consumers choose a gate and implement only the work performed when it is open.
    /// The architecture test rejects separate FixedUpdate callbacks that bypass this entry point.
    public abstract class GatedSimulationBehaviour : MonoBehaviour
    {
        protected abstract SimulationGate Gate { get; }

        protected void FixedUpdate()
        {
            var level = LevelManager.Instance;
            if (level != null && level.IsGated(Gate)) return;
            SimulationTick();
        }

        protected abstract void SimulationTick();
    }
}
