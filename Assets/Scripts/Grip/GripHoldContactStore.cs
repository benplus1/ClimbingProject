using System;
using System.Collections.Generic;
using UnityEngine;

internal sealed class GripHoldContactStore
{
    private static readonly int ContactThresholdId = UnityEngine.Shader.PropertyToID("_ContactThreshold");
    private static readonly int ProximityThresholdId = UnityEngine.Shader.PropertyToID("_ProximityThreshold");
    private static readonly int PatchExposureId = UnityEngine.Shader.PropertyToID("_PatchExposure");

    /// <summary>Indexed the way GripContactPatchPolicy.HueIndex reports a finger.</summary>
    private static readonly int[] PatchColorIds =
    {
        UnityEngine.Shader.PropertyToID("_ThumbColor"),
        UnityEngine.Shader.PropertyToID("_IndexColor"),
        UnityEngine.Shader.PropertyToID("_MiddleColor"),
        UnityEngine.Shader.PropertyToID("_RingColor"),
        UnityEngine.Shader.PropertyToID("_LittleColor"),
    };

    private readonly GripContactReadbackProcessor readback;
    private readonly GripScoreConfig config;
    private readonly Dictionary<int, GripHoldContactState> holdStates = new();
    private readonly List<int> staleStateIds = new();
    private Material patchFieldMaterial;
    private bool patchFieldMaterialResolved;

    public GripHoldContactStore(GripContactReadbackProcessor readback, GripScoreConfig config)
    {
        this.readback = readback;
        this.config = config;
    }

    public void Retain(IReadOnlyList<GameObject> holds)
    {
        HashSet<int> retainedIds = new();
        if (holds != null)
        {
            foreach (GameObject hold in holds)
            {
                if (hold != null)
                {
                    retainedIds.Add(hold.GetInstanceID());
                }
            }
        }

        staleStateIds.Clear();
        foreach (KeyValuePair<int, GripHoldContactState> pair in holdStates)
        {
            if (!retainedIds.Contains(pair.Key))
            {
                staleStateIds.Add(pair.Key);
            }
        }
        foreach (int id in staleStateIds)
        {
            holdStates[id].Dispose();
            holdStates.Remove(id);
        }
    }

    public void Prepare(GameObject hold)
    {
        if (hold == null || holdStates.ContainsKey(hold.GetInstanceID()) ||
            !hold.TryGetComponent(out MeshFilter meshFilter) || meshFilter.sharedMesh == null)
        {
            return;
        }

        holdStates.Add(
            hold.GetInstanceID(),
            new GripHoldContactState(readback, config, hold, meshFilter, EnsurePatchFieldMaterial()));
    }

    /// <summary>One material for every hold's patch field, cloned from the asset the config points at
    /// so the shared asset is never mutated and the shader reaches the build through Resources. The
    /// palette and the two distance thresholds are the same on every hold, so they ride the material
    /// and only the per-hold contact buffer rides a property block; no material is created once the
    /// study is running. A null return is the cue switched off, not a failure - an enabled cue with
    /// no material is a misconfiguration and throws.</summary>
    private Material EnsurePatchFieldMaterial()
    {
        if (patchFieldMaterialResolved)
        {
            return patchFieldMaterial;
        }

        patchFieldMaterialResolved = true;
        if (!config.contactPatchCueEnabled)
        {
            return null;
        }
        if (config.contactPatchFieldMaterial == null)
        {
            throw new InvalidOperationException(
                "The contact patch cue is enabled but GripScoreConfig.contactPatchFieldMaterial is unset.");
        }

        patchFieldMaterial = new Material(config.contactPatchFieldMaterial)
        {
            name = GripHoldContactState.PatchFieldName,
        };
        patchFieldMaterial.SetFloat(ContactThresholdId, config.contactThreshold);
        patchFieldMaterial.SetFloat(ProximityThresholdId, config.proximityThreshold);
        patchFieldMaterial.SetFloat(PatchExposureId, config.contactPatchExposure);

        // The palette is authored in sRGB and the shader declares each entry as a Color property, so
        // the conversion into the project's linear space happens on upload. Converting here as well
        // renders the hues a second gamma too dark.
        for (int finger = 0; finger < PatchColorIds.Length; finger++)
        {
            patchFieldMaterial.SetColor(PatchColorIds[finger], config.GetPatchColor(finger));
        }
        return patchFieldMaterial;
    }

    public GripHoldContactState ResolveState(GameObject hold)
    {
        if (!hold.TryGetComponent(out MeshFilter meshFilter) || meshFilter.sharedMesh == null)
        {
            return null;
        }

        int id = hold.GetInstanceID();
        if (!holdStates.TryGetValue(id, out GripHoldContactState state))
        {
            Prepare(hold);
            state = holdStates[id];
        }
        return state;
    }

    public void HideAllOverlays()
    {
        foreach (GripHoldContactState state in holdStates.Values)
        {
            state.SetOverlayVisible(false);
        }
    }

    public void InvalidateAllContactData()
    {
        foreach (GripHoldContactState state in holdStates.Values)
        {
            state.InvalidateContactData();
        }
    }

    public void InvalidateHoldContact(GameObject hold)
    {
        if (hold != null && holdStates.TryGetValue(hold.GetInstanceID(), out GripHoldContactState state))
        {
            state.InvalidateContactData();
        }
    }

    public void SetLatchFeedback(GameObject hold, int handMask, bool latched)
    {
        if (hold == null)
        {
            return;
        }

        GripHoldContactState state;
        if (latched)
        {
            state = ResolveState(hold);
        }
        else
        {
            holdStates.TryGetValue(hold.GetInstanceID(), out state);
        }
        state?.SetLatchedHand(handMask, latched);
    }

    public void ClearAllLatchFeedback()
    {
        foreach (GripHoldContactState state in holdStates.Values)
        {
            state.ClearLatchFeedback();
        }
    }

    public void RemoveDestroyedStates()
    {
        staleStateIds.Clear();
        foreach (KeyValuePair<int, GripHoldContactState> pair in holdStates)
        {
            if (pair.Value.hold == null)
            {
                staleStateIds.Add(pair.Key);
            }
        }
        foreach (int id in staleStateIds)
        {
            holdStates[id].Dispose();
            holdStates.Remove(id);
        }
    }

    public void DisposeAll()
    {
        foreach (GripHoldContactState state in holdStates.Values)
        {
            state.Dispose();
        }
        holdStates.Clear();
        if (patchFieldMaterial != null)
        {
            DestroyObject(patchFieldMaterial);
            patchFieldMaterial = null;
        }
        patchFieldMaterialResolved = false;
    }

    private static void DestroyObject(UnityEngine.Object target)
    {
        if (Application.isPlaying)
        {
            UnityEngine.Object.Destroy(target);
        }
        else
        {
            UnityEngine.Object.DestroyImmediate(target);
        }
    }
}
