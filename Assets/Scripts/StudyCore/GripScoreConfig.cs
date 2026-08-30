using System;
using UnityEngine;

[CreateAssetMenu(fileName = "GripScoreConfig", menuName = "VHard/Grip Score Config")]
public sealed class GripScoreConfig : ScriptableObject
{
    [Header("Contact")]
    [Min(0.001f)] public float proximityThreshold = 0.025f;
    [Min(0.001f)] public float contactThreshold = 0.008f;
    [Min(0.00001f)] public float referenceContactArea = 0.0015f;
    [Min(1000f)] public float fixedPointScale = 100000000f;

    [Header("Score Weights")]
    [Range(0f, 1f)] public float contactWeight = 0.30f;
    [Range(0f, 1f)] public float areaWeight = 0.20f;
    [Range(0f, 1f)] public float oppositionWeight = 0.25f;
    [Range(0f, 1f)] public float loadAlignmentWeight = 0.25f;

    [Header("Display")]
    [Min(0.01f)] public float smoothingSeconds = 0.15f;
    [Range(0f, 0.25f)] public float hysteresis = 0.05f;
    public bool rimGlow;
    [Range(0f, 1f)] public float rimGlowThreshold = 0.5f;
    [Range(0f, 1f)] public float rimGlowAlpha = 0.35f;
    [Range(0.5f, 8f)] public float rimGlowPower = 3f;
    public Color lowScoreColor = new(0.9f, 0.08f, 0.06f, 1f);
    public Color mediumScoreColor = new(1f, 0.55f, 0.05f, 1f);
    public Color highScoreColor = new(0.1f, 0.85f, 0.2f, 1f);
    public Material contactPatchMaterial;

    [Header("Contact Patches")]
    public bool contactPatchCueEnabled = true;
    public Material contactPatchFieldMaterial;

    /// <summary>Okabe-Ito, the colour-blind-safe set this project's analysis figures already use, in
    /// finger order: thumb #E69F00, index #56B4E9, middle #009E73, ring #CC79A7, little #D55E00. Both
    /// hands share one hue per finger.</summary>
    public Color thumbPatchColor = new(0.9020f, 0.6235f, 0f, 1f);
    public Color indexPatchColor = new(0.3373f, 0.7059f, 0.9137f, 1f);
    public Color middlePatchColor = new(0f, 0.6196f, 0.4510f, 1f);
    public Color ringPatchColor = new(0.8000f, 0.4745f, 0.6549f, 1f);
    public Color littlePatchColor = new(0.8353f, 0.3686f, 0f, 1f);
    [Range(0f, 4f)] public float contactPatchExposure = 1f;

    public float WeightSum => contactWeight + areaWeight + oppositionWeight + loadAlignmentWeight;

    /// <summary>The hue for one finger, indexed the way GripContactPatchPolicy.HueIndex reports it.</summary>
    public Color GetPatchColor(int finger)
    {
        switch (finger)
        {
            case 0: return thumbPatchColor;
            case 1: return indexPatchColor;
            case 2: return middlePatchColor;
            case 3: return ringPatchColor;
            case 4: return littlePatchColor;
            default:
                throw new ArgumentOutOfRangeException(
                    nameof(finger), finger, "A hand has five fingers.");
        }
    }

    private void OnValidate()
    {
        proximityThreshold = Mathf.Max(proximityThreshold, contactThreshold);
        referenceContactArea = Mathf.Max(referenceContactArea, 0.00001f);
        fixedPointScale = Mathf.Max(fixedPointScale, 1000f);
        hysteresis = Mathf.Min(hysteresis, 0.5f);
    }
}
