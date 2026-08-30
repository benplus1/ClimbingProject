using System;

/// <summary>Reads the compute pass's per-vertex (nearest fingertip, distance to it) pair the way the
/// contact-patch cue draws it: which finger a vertex belongs to, and how brightly that vertex lights.
/// ContactPatchField.shader mirrors this ramp exactly, so the expectations pinned against these
/// functions describe what the GPU puts on the hold. Both hands share one hue per finger - the patch
/// answers "which finger is on the hold", never "which hand", which the arm already answers.</summary>
public static class GripContactPatchPolicy
{
    /// <summary>What the compute pass writes when no fingertip was found for a vertex. Ordinals 0-4
    /// are the left hand thumb-to-little, 5-9 the right.</summary>
    public const int NoFingertipOrdinal = 10;

    /// <summary>Guards a degenerate ramp: the config clamps the proximity threshold to at least the
    /// contact threshold, so the two can be equal and the falloff collapses to a step.</summary>
    private const float MinimumFalloffSpanMeters = 0.000001f;

    /// <summary>The finger whose hue a vertex carries, or -1 where no fingertip claimed it. The
    /// ordinal arrives as an integer-valued float because it rides in a float4 alongside the
    /// distances.</summary>
    public static int HueIndex(float fingertipOrdinal)
    {
        if (float.IsNaN(fingertipOrdinal))
        {
            return -1;
        }

        int ordinal = (int)Math.Round(fingertipOrdinal, MidpointRounding.AwayFromZero);
        if (ordinal < 0 || ordinal >= NoFingertipOrdinal)
        {
            return -1;
        }

        return ordinal % GripAffordancePolicy.FingerCount;
    }

    /// <summary>Spec 04's ramp: solid inside the contact threshold, smoothly out to nothing at the
    /// proximity threshold, and nothing at all beyond it. The hold's own scanned surface is what the
    /// participant is there to read, so the cue has to vanish completely rather than tint.</summary>
    public static float Intensity(float distanceToFingertip, float contactThreshold, float proximityThreshold)
    {
        if (float.IsNaN(distanceToFingertip))
        {
            return 0f;
        }
        if (distanceToFingertip <= contactThreshold)
        {
            return 1f;
        }
        if (distanceToFingertip >= proximityThreshold)
        {
            return 0f;
        }

        float span = Math.Max(proximityThreshold - contactThreshold, MinimumFalloffSpanMeters);
        float t = (distanceToFingertip - contactThreshold) / span;
        t = t < 0f ? 0f : (t > 1f ? 1f : t);
        return 1f - (t * t * (3f - (2f * t)));
    }

    /// <summary>Whether a hold still showing <paramref name="boundEpoch"/> must drop its patches
    /// because <paramref name="requestedEpoch"/> is unusable. A negative request invalidates
    /// unconditionally; otherwise only data no newer than the failed epoch is dropped, so a readback
    /// that fails after a newer dispatch has already landed cannot blank a hold that is currently
    /// being drawn from good data.</summary>
    public static bool ShouldInvalidate(long requestedEpoch, long boundEpoch)
    {
        return requestedEpoch < 0 || requestedEpoch >= boundEpoch;
    }
}
