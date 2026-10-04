using UnityEngine;
using JumpNowBro.Util;

namespace JumpNowBro.Gameplay
{
    [RequireComponent(typeof(Collider2D))]
    public class Hazard : MonoBehaviour
    {
        void OnTriggerEnter2D(Collider2D other) => TryKill(other);

        // Invulnerability may expire without another entry into this trigger.
        void OnTriggerStay2D(Collider2D other) => TryKill(other);

        void TryKill(Collider2D other)
        {
            if (!Authority.IsHost) return;                         // host owns Die(); client sees death via STATE.deathCount delta
            if (!other.TryGetComponent<PlayerController>(out var player)) return;
            if (player.IsInvulnerable) return;
            player.Die();
        }
    }
}
