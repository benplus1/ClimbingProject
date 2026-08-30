using System;
using UnityEngine;
using UnityEngine.Rendering;
using static GripContactConstants;

/// <summary>Per-hold GPU residency for the grip pipeline: the mesh buffers the contact pass reads,
/// the readback slots it writes, and which hands have latched this hold. It owns one visual, spec
/// 04's contact-patch field: a copy of the hold's own mesh shaded additively from the per-vertex
/// contact buffer, which lights only where a fingertip is within reach and adds exactly nothing
/// everywhere else. The hold's scanned appearance is what the participant is there to read, so no
/// cue may tint it; hand-level grip quality stays on the silhouette, drawn by
/// GripAffordanceOutlinePresenter from the scores this pipeline publishes.</summary>
internal sealed class GripHoldContactState : IDisposable
{
    public const string PatchFieldName = "GripContactPatchField";

    /// <summary>The patch field carries no collider, so it cannot compete for hold selection; it is
    /// still kept off the study interaction layers, and off the ghost layer that
    /// SceneConfiguror.RegisterGhostHold stamps recursively over a spawned ghost's children.</summary>
    private const int PatchFieldLayer = 0;

    private static readonly int VertexContactId = UnityEngine.Shader.PropertyToID("_VertexContact");

    private readonly GripContactOutputSet[] outputs;
    private readonly Material patchFieldMaterial;
    private MeshRenderer patchRenderer;
    private MaterialPropertyBlock patchProperties;
    private bool patchRequested;
    private bool contactBufferReady;
    private long boundEpoch;
    private int latchedHandMask;
    public readonly GameObject hold;
    public readonly Mesh mesh;
    public readonly int vertexCount;
    public readonly ComputeBuffer vertices;
    public readonly ComputeBuffer normals;
    public readonly ComputeBuffer vertexAreas;
    public readonly ComputeBuffer leftHandBones;
    public readonly ComputeBuffer rightHandBones;

    public GripHoldContactState(
        GripContactReadbackProcessor owner,
        GripScoreConfig config,
        GameObject hold,
        MeshFilter meshFilter,
        Material patchFieldMaterial)
    {
        this.hold = hold;
        this.patchFieldMaterial = patchFieldMaterial;
        mesh = meshFilter.sharedMesh;
        vertexCount = mesh.vertexCount;
        Vector3[] meshVertices = mesh.vertices;
        Vector3[] meshNormals = mesh.normals;
        if (meshNormals.Length != vertexCount)
        {
            mesh.RecalculateNormals();
            meshNormals = mesh.normals;
        }
        float[] areas = ComputeVertexAreas(mesh, hold.transform);

        vertices = new ComputeBuffer(vertexCount, sizeof(float) * 3);
        normals = new ComputeBuffer(vertexCount, sizeof(float) * 3);
        vertexAreas = new ComputeBuffer(vertexCount, sizeof(float));
        leftHandBones = new ComputeBuffer(BoneCount, sizeof(float) * 3);
        rightHandBones = new ComputeBuffer(BoneCount, sizeof(float) * 3);
        vertices.SetData(meshVertices);
        normals.SetData(meshNormals);
        vertexAreas.SetData(areas);
        outputs = new[]
        {
            new GripContactOutputSet(owner, this),
            new GripContactOutputSet(owner, this),
        };
    }

    public int LatchedHandMask => latchedHandMask;

    public GripContactOutputSet GetAvailableOutput()
    {
        foreach (GripContactOutputSet output in outputs)
        {
            if (!output.IsPending)
            {
                return output;
            }
        }
        return null;
    }

    /// <summary>The store hides every hold's field each frame and the dispatcher re-shows the one it
    /// is about to dispatch, so a hand leaving a hold takes the patches with it in the same frame.
    /// The field is created the first time a hold is actually targeted, never for the whole route.</summary>
    public void SetOverlayVisible(bool visible)
    {
        patchRequested = visible;
        if (!visible)
        {
            if (patchRenderer != null)
            {
                patchRenderer.enabled = false;
            }
            return;
        }
        if (patchFieldMaterial == null)
        {
            return;
        }

        EnsurePatchField().enabled = contactBufferReady;
    }

    /// <summary>Binds the buffer the pass just wrote. The two output sets alternate, so the binding
    /// is refreshed per dispatch rather than cached.</summary>
    public void SetContactBuffer(ComputeBuffer contactBuffer, long epoch)
    {
        if (contactBuffer == null)
        {
            throw new ArgumentNullException(nameof(contactBuffer));
        }

        boundEpoch = epoch;
        contactBufferReady = true;
        if (patchFieldMaterial == null)
        {
            return;
        }

        MeshRenderer renderer = EnsurePatchField();
        patchProperties ??= new MaterialPropertyBlock();
        renderer.GetPropertyBlock(patchProperties);
        patchProperties.SetBuffer(VertexContactId, contactBuffer);
        renderer.SetPropertyBlock(patchProperties);
        renderer.enabled = patchRequested;
    }

    /// <summary>Drops the patches when the data behind them cannot be trusted - a failed readback, a
    /// hand that changed target, a pipeline that stopped - so a stalled pass can never leave a frozen
    /// glow standing on a hold.</summary>
    public void InvalidateContactData(long epoch = -1)
    {
        if (!GripContactPatchPolicy.ShouldInvalidate(epoch, boundEpoch))
        {
            return;
        }

        contactBufferReady = false;
        patchRequested = false;
        if (patchRenderer != null)
        {
            patchRenderer.enabled = false;
        }
    }

    public void SetLatchedHand(int handMask, bool latched)
    {
        if (handMask == 0)
        {
            throw new ArgumentOutOfRangeException(nameof(handMask));
        }

        latchedHandMask = latched ? latchedHandMask | handMask : latchedHandMask & ~handMask;
    }

    public void ClearLatchFeedback()
    {
        latchedHandMask = 0;
    }

    /// <summary>Shares the hold's own mesh, so a patch sits exactly on the surface it reports and no
    /// extra geometry is uploaded. Collider-free and flagged never to serialise, mirroring the rim.</summary>
    private MeshRenderer EnsurePatchField()
    {
        if (patchRenderer != null)
        {
            // RegisterGhostHold re-stamps a spawned ghost's whole subtree onto the ghost layer, so
            // the field reasserts its own layer rather than trusting the layer it was created on.
            patchRenderer.gameObject.layer = PatchFieldLayer;
            return patchRenderer;
        }

        Transform existing = hold.transform.Find(PatchFieldName);
        GameObject fieldObject = existing != null ? existing.gameObject : new GameObject(PatchFieldName);
        fieldObject.hideFlags = HideFlags.DontSaveInEditor | HideFlags.DontSaveInBuild;
        fieldObject.layer = PatchFieldLayer;
        fieldObject.transform.SetParent(hold.transform, false);
        fieldObject.transform.localPosition = Vector3.zero;
        fieldObject.transform.localRotation = Quaternion.identity;
        fieldObject.transform.localScale = Vector3.one;

        if (!fieldObject.TryGetComponent(out MeshFilter fieldMesh))
        {
            fieldMesh = fieldObject.AddComponent<MeshFilter>();
        }
        fieldMesh.sharedMesh = mesh;

        if (!fieldObject.TryGetComponent(out MeshRenderer renderer))
        {
            renderer = fieldObject.AddComponent<MeshRenderer>();
        }
        renderer.sharedMaterial = patchFieldMaterial;
        renderer.shadowCastingMode = ShadowCastingMode.Off;
        renderer.receiveShadows = false;
        renderer.lightProbeUsage = LightProbeUsage.Off;
        renderer.reflectionProbeUsage = ReflectionProbeUsage.Off;
        renderer.enabled = false;
        patchRenderer = renderer;
        return renderer;
    }

    public void Dispose()
    {
        ClearLatchFeedback();
        if (patchRenderer != null)
        {
            DestroyObject(patchRenderer.gameObject);
        }
        patchRenderer = null;
        foreach (GripContactOutputSet output in outputs)
        {
            output.Dispose();
        }
        vertices.Release();
        normals.Release();
        vertexAreas.Release();
        leftHandBones.Release();
        rightHandBones.Release();
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

    private static float[] ComputeVertexAreas(Mesh sourceMesh, Transform transform)
    {
        Vector3[] meshVertices = sourceMesh.vertices;
        int[] triangles = sourceMesh.triangles;
        float[] areas = new float[meshVertices.Length];
        for (int i = 0; i + 2 < triangles.Length; i += 3)
        {
            int a = triangles[i];
            int b = triangles[i + 1];
            int c = triangles[i + 2];
            Vector3 edgeA = transform.TransformVector(meshVertices[b] - meshVertices[a]);
            Vector3 edgeB = transform.TransformVector(meshVertices[c] - meshVertices[a]);
            float thirdArea = Vector3.Cross(edgeA, edgeB).magnitude / 6f;
            areas[a] += thirdArea;
            areas[b] += thirdArea;
            areas[c] += thirdArea;
        }
        return areas;
    }
}
