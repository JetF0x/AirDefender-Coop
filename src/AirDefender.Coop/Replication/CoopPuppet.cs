using UnityEngine;

namespace AirDefenderCoop.Replication
{
    /// <summary>
    /// Client-side motion for a replicated contact. The entity's own movers are switched off;
    /// this component dead-reckons from the host's latest position/velocity and eases towards it.
    /// </summary>
    [DisallowMultipleComponent]
    public sealed class CoopPuppet : MonoBehaviour
    {
        private const float SnapDistance = 40f;    // world units; beyond this jump instead of easing
        private const float MaxExtrapolate = 3f;   // scaled seconds

        private Vector3 _basePos;
        private Vector3 _vel;
        private float _rotZ;
        private float _sampleTime;
        private bool _has;

        public float LastUpdateRealtime { get; private set; }

        public void Push(Vector3 pos, Vector3 vel, float rotZ)
        {
            if (!_has || (pos - transform.position).sqrMagnitude > SnapDistance * SnapDistance)
            {
                transform.position = pos;
                transform.rotation = Quaternion.Euler(0f, 0f, rotZ);
            }
            _basePos = pos;
            _vel = vel;
            _rotZ = rotZ;
            _sampleTime = Time.time;
            _has = true;
            LastUpdateRealtime = Time.realtimeSinceStartup;
        }

        private void LateUpdate()
        {
            if (!_has || !ClientGate.PuppetActive) return;
            float dt = Mathf.Min(Time.time - _sampleTime, MaxExtrapolate);
            Vector3 target = _basePos + _vel * dt;
            float k = 1f - Mathf.Exp(-12f * Time.unscaledDeltaTime);
            transform.position = Vector3.Lerp(transform.position, target, k);
            transform.rotation = Quaternion.Slerp(transform.rotation, Quaternion.Euler(0f, 0f, _rotZ), k);
        }
    }
}
