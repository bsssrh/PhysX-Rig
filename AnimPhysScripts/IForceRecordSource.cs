using UnityEngine;

/// <summary>
/// Anything that applies forces/torques to a Rigidbody each FixedUpdate can expose what it applied,
/// so a recorder can capture & replay it.
/// Values are in Acceleration form (ForceMode.Acceleration).
/// </summary>
public interface IForceRecordSource
{
    Rigidbody Body { get; }
    Vector3 LastAppliedAccel { get; }      // m/s^2
    Vector3 LastAppliedAngAccel { get; }   // rad/s^2
}
