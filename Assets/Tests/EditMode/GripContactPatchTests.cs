using System;
using System.Linq;
using System.Reflection;
using NUnit.Framework;
using UnityEngine;

public sealed class GripContactPatchTests
{
    private const float ContactThreshold = 0.008f;
    private const float ProximityThreshold = 0.025f;

    [TestCase(0f, 0)]
    [TestCase(1f, 1)]
    [TestCase(2f, 2)]
    [TestCase(3f, 3)]
    [TestCase(4f, 4)]
    [TestCase(5f, 0)]
    [TestCase(6f, 1)]
    [TestCase(7f, 2)]
    [TestCase(8f, 3)]
    [TestCase(9f, 4)]
    public void HueIndexGivesBothHandsTheSameHuePerFinger(float ordinal, int expected)
    {
        Assert.That(GripContactPatchPolicy.HueIndex(ordinal), Is.EqualTo(expected));
    }

    [TestCase(10f)]
    [TestCase(11f)]
    [TestCase(-1f)]
    [TestCase(float.NaN)]
    public void HueIndexRejectsOrdinalsNoFingertipClaimed(float ordinal)
    {
        Assert.That(
            GripContactPatchPolicy.HueIndex(ordinal),
            Is.EqualTo(-1),
            "A vertex no fingertip reached must carry no hue.");
    }

    [Test]
    public void IntensityIsSolidInsideContactAndGoneBeyondProximity()
    {
        Assert.That(
            GripContactPatchPolicy.Intensity(0f, ContactThreshold, ProximityThreshold),
            Is.EqualTo(1f));
        Assert.That(
            GripContactPatchPolicy.Intensity(ContactThreshold, ContactThreshold, ProximityThreshold),
            Is.EqualTo(1f),
            "The contact threshold itself is still solid.");
        Assert.That(
            GripContactPatchPolicy.Intensity(ProximityThreshold, ContactThreshold, ProximityThreshold),
            Is.EqualTo(0f));
        Assert.That(
            GripContactPatchPolicy.Intensity(0.05f, ContactThreshold, ProximityThreshold),
            Is.EqualTo(0f),
            "Past the falloff the hold keeps its own scanned appearance untouched.");
        Assert.That(
            GripContactPatchPolicy.Intensity(float.MaxValue, ContactThreshold, ProximityThreshold),
            Is.EqualTo(0f),
            "The compute pass leaves an unclaimed vertex at float-max.");
    }

    [Test]
    public void IntensityFallsOffSmoothlyAndMonotonicallyAcrossTheBand()
    {
        float midpoint = (ContactThreshold + ProximityThreshold) / 2f;
        Assert.That(
            GripContactPatchPolicy.Intensity(midpoint, ContactThreshold, ProximityThreshold),
            Is.EqualTo(0.5f).Within(0.0001f));

        float previous = 1f;
        for (int step = 0; step <= 20; step++)
        {
            float distance = Mathf.Lerp(ContactThreshold, ProximityThreshold, step / 20f);
            float intensity = GripContactPatchPolicy.Intensity(
                distance, ContactThreshold, ProximityThreshold);
            Assert.That(intensity, Is.LessThanOrEqualTo(previous + 0.0001f));
            Assert.That(intensity, Is.InRange(0f, 1f));
            previous = intensity;
        }
        Assert.That(previous, Is.EqualTo(0f));
    }

    [Test]
    public void IntensityCollapsesToAStepWhenTheThresholdsCoincide()
    {
        Assert.That(
            GripContactPatchPolicy.Intensity(ContactThreshold, ContactThreshold, ContactThreshold),
            Is.EqualTo(1f));
        Assert.That(
            GripContactPatchPolicy.Intensity(ContactThreshold + 0.0001f, ContactThreshold, ContactThreshold),
            Is.EqualTo(0f));
    }

    [TestCase(-1L, 7L, true)]
    [TestCase(7L, 7L, true)]
    [TestCase(8L, 7L, true)]
    [TestCase(6L, 7L, false)]
    public void StaleEpochCannotBlankNewerContactData(long requested, long bound, bool expected)
    {
        Assert.That(GripContactPatchPolicy.ShouldInvalidate(requested, bound), Is.EqualTo(expected));
    }

    [Test]
    public void PatchPaletteIsColourBlindSafeAndOrderedByFinger()
    {
        GripScoreConfig config = ScriptableObject.CreateInstance<GripScoreConfig>();
        try
        {
            Color[] expected =
            {
                new(0.9020f, 0.6235f, 0f, 1f),
                new(0.3373f, 0.7059f, 0.9137f, 1f),
                new(0f, 0.6196f, 0.4510f, 1f),
                new(0.8000f, 0.4745f, 0.6549f, 1f),
                new(0.8353f, 0.3686f, 0f, 1f),
            };
            for (int finger = 0; finger < expected.Length; finger++)
            {
                Assert.That(config.GetPatchColor(finger), Is.EqualTo(expected[finger]));
            }
            Assert.Throws<ArgumentOutOfRangeException>(() => config.GetPatchColor(5));
        }
        finally
        {
            UnityEngine.Object.DestroyImmediate(config);
        }
    }

    [Test]
    public void PatchFieldAppearsOnlyOnceAHoldCarriesFreshContactData()
    {
        if (!SystemInfo.supportsComputeShaders)
        {
            Assert.Ignore("The contact-state fixture requires compute-buffer support.");
        }

        GameObject hold = GameObject.CreatePrimitive(PrimitiveType.Cube);
        GripScoreConfig config = ScriptableObject.CreateInstance<GripScoreConfig>();
        Material patchMaterial = Resources.Load<Material>("ContactPatchField");
        object state = null;
        ComputeBuffer contactBuffer = null;
        try
        {
            Assert.That(
                patchMaterial,
                Is.Not.Null,
                "The contact patch cue requires Resources/ContactPatchField.mat.");

            Type stateType = AppDomain.CurrentDomain.GetAssemblies()
                .Select(assembly => assembly.GetType("GripHoldContactState"))
                .Single(type => type != null);
            ConstructorInfo constructor = stateType.GetConstructors(
                    BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
                .Single();
            state = constructor.Invoke(new object[]
            {
                null, config, hold, hold.GetComponent<MeshFilter>(), patchMaterial,
            });
            MethodInfo setOverlayVisible = stateType.GetMethod("SetOverlayVisible");
            MethodInfo setContactBuffer = stateType.GetMethod("SetContactBuffer");
            MethodInfo invalidateContactData = stateType.GetMethod("InvalidateContactData");

            Assert.That(
                hold.GetComponentsInChildren<Renderer>(true).Length,
                Is.EqualTo(1),
                "Preparing a hold must not build a patch field for a hold no hand has reached.");

            setOverlayVisible.Invoke(state, new object[] { true });
            MeshRenderer field = hold.GetComponentsInChildren<MeshRenderer>(true)
                .Single(renderer => renderer.gameObject != hold);
            Assert.That(field.gameObject.name, Is.EqualTo("GripContactPatchField"));
            Assert.That(field.GetComponent<Collider>(), Is.Null, "The field must never be selectable.");
            Assert.That(
                field.GetComponent<MeshFilter>().sharedMesh,
                Is.SameAs(hold.GetComponent<MeshFilter>().sharedMesh),
                "The field shares the hold's own mesh, so a patch sits on the surface it reports.");
            Assert.That(
                field.enabled,
                Is.False,
                "A field with no contact data behind it must not render.");

            contactBuffer = new ComputeBuffer(
                hold.GetComponent<MeshFilter>().sharedMesh.vertexCount, sizeof(float) * 4);
            setContactBuffer.Invoke(state, new object[] { contactBuffer, 7L });
            Assert.That(field.enabled, Is.True);

            field.gameObject.layer = 21;
            setOverlayVisible.Invoke(state, new object[] { true });
            Assert.That(
                field.gameObject.layer,
                Is.Zero,
                "A ghost's recursive layer stamp must not drag the field onto the ghost layer.");

            invalidateContactData.Invoke(state, new object[] { 6L });
            Assert.That(
                field.enabled,
                Is.True,
                "An epoch older than the bound one must not blank data that is still good.");

            invalidateContactData.Invoke(state, new object[] { 7L });
            Assert.That(
                field.enabled,
                Is.False,
                "A stalled pass must never leave a frozen glow standing on a hold.");

            setOverlayVisible.Invoke(state, new object[] { true });
            Assert.That(
                field.enabled,
                Is.False,
                "Re-showing a hold must wait for data, not resurrect the invalidated frame.");

            setContactBuffer.Invoke(state, new object[] { contactBuffer, 8L });
            Assert.That(
                field.enabled,
                Is.True,
                "The next good dispatch restores the patches.");

            setOverlayVisible.Invoke(state, new object[] { false });
            Assert.That(field.enabled, Is.False, "A hand leaving the hold takes the patches with it.");

            Assert.Throws<TargetInvocationException>(
                () => setContactBuffer.Invoke(state, new object[] { null, 9L }),
                "A missing buffer is a pipeline defect, never a silently empty overlay.");
        }
        finally
        {
            (state as IDisposable)?.Dispose();
            contactBuffer?.Release();
            UnityEngine.Object.DestroyImmediate(config);
            UnityEngine.Object.DestroyImmediate(hold);
        }
    }
}
