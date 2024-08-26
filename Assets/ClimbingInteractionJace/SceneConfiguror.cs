using System.Collections.Generic;
using System.Linq;
using Unity.XR.CoreUtils;
using UnityEngine;
using UnityEngine.XR.Interaction.Toolkit;
using UnityEngine.XR.Interaction.Toolkit.Interactables;
using UnityEngine.XR.Interaction.Toolkit.Interactors;
using UnityEngine.XR.Interaction.Toolkit.Interactors.Casters;

public class SceneConfiguror : MonoBehaviour
{
    [Header("Hands References")]
    public GameObject leftHand;
    public GameObject leftHandNearFarInteractor;
    public SkinnedMeshRenderer leftHandSkinnedMeshRenderer;
    public GameObject leftHandRootBone;
    public List<GameObject> leftHandBones;

    public GameObject rightHand;
    public GameObject rightHandNearFarInteractor;
    public SkinnedMeshRenderer rightHandSkinnedMeshRenderer;
    public GameObject rightHandRootBone;
    public List<GameObject> rightHandBones;

    [Header("Interaction Settings")]
    public float hoverRadiusOverride;
    public float interactionColorMaxDistanceOverride;

    [Header("Interaction State")]
    public GameObject leftHandInteractingClimbingHold;
    public GameObject rightHandInteractingClimbingHold;

    [Header("Interaction Compute Shader State")]
    public ComputeShader distanceToClosestBoneComputeShader;
    public int kernelHandle;
    public ComputeBuffer climbingHoldVerticesBuffer;
    public ComputeBuffer leftHandBonesBuffer;
    public ComputeBuffer rightHandBonesBuffer;
    public ComputeBuffer leftHandDistancesBuffer;
    public ComputeBuffer rightHandDistancesBuffer;

    void Start()
    {
        // Traverse root bone of each hand and add all bones to list
        leftHandBones = new List<GameObject>();
        rightHandBones = new List<GameObject>();
        TraverseBones(leftHandRootBone, leftHandBones);
        TraverseBones(rightHandRootBone, rightHandBones);

        // Set up compute shader
        kernelHandle = distanceToClosestBoneComputeShader.FindKernel("CSMain");
    }

    void TraverseBones(GameObject rootBone, List<GameObject> bones)
    {
        bones.Add(rootBone);
        foreach (Transform child in rootBone.transform)
        {
            TraverseBones(child.gameObject, bones);
        }
    }

    void Update()
    {
        // Override hover radius
        foreach (var nearFarInteractor in new GameObject[] { leftHandNearFarInteractor, rightHandNearFarInteractor })
        {
            IInteractionCaster nearInteractionCaster = nearFarInteractor.GetComponent<NearFarInteractor>().nearInteractionCaster;
            if (nearInteractionCaster is SphereInteractionCaster sphereInteractionCaster)
            {
                sphereInteractionCaster.castRadius = hoverRadiusOverride;
            }
        }

        if (leftHandInteractingClimbingHold == null && rightHandInteractingClimbingHold == null)
        {
            return;
        }

        // Override interaction color max distance, update interaction status
        if (leftHandInteractingClimbingHold != null)
        {
            MeshRenderer leftHandMeshRenderer = leftHandInteractingClimbingHold.GetComponent<MeshRenderer>();
            leftHandMeshRenderer.material.SetInt("_IsBeingInteracted", 1);
            leftHandMeshRenderer.material.SetFloat("_InteractionColorMaxDistance", interactionColorMaxDistanceOverride);
        }
        if (rightHandInteractingClimbingHold != null)
        {
            MeshRenderer rightHandMeshRenderer = rightHandInteractingClimbingHold.GetComponent<MeshRenderer>();
            rightHandMeshRenderer.material.SetInt("_IsBeingInteracted", 1);
            rightHandMeshRenderer.material.SetFloat("_InteractionColorMaxDistance", interactionColorMaxDistanceOverride);
        }

        List<GameObject> interactingClimbingHolds = new List<GameObject>();
        if (leftHandInteractingClimbingHold != null)
        {
            interactingClimbingHolds.Add(leftHandInteractingClimbingHold);
        }
        if (rightHandInteractingClimbingHold != null)
        {
            interactingClimbingHolds.Add(rightHandInteractingClimbingHold);
        }

        // WARNING: Here be dragons.
        // The big idea is that for each vertex of the climbing hold, we find the distance to the closest bone of each hand, and save to two arrays.
        // Then, we encode these distances in the UVs (channel 2) of the climbing hold's mesh vertices, and access them in the shader.
        foreach (GameObject climbingHold in interactingClimbingHolds)
        {
            // Get information about the climbing hold
            MeshFilter climbingHoldMeshFilter = climbingHold.GetComponent<MeshFilter>();
            Mesh climbingHoldMesh = climbingHoldMeshFilter.mesh;
            Vector3[] climbingHoldVertices = climbingHoldMesh.vertices;
            int climbingHoldVerticesCount = climbingHoldVertices.Length;

            // Initialize buffers for compute shader
            climbingHoldVerticesBuffer = new ComputeBuffer(climbingHoldVerticesCount, sizeof(float) * 3); // World position of each vertex of the climbing hold
            leftHandBonesBuffer = new ComputeBuffer(leftHandBones.Count, sizeof(float) * 3); // World position of each bone of the left hand
            rightHandBonesBuffer = new ComputeBuffer(rightHandBones.Count, sizeof(float) * 3); // World position of each bone of the right hand
            leftHandDistancesBuffer = new ComputeBuffer(climbingHoldVerticesCount, sizeof(float)); // Distance from each vertex of the climbing hold to the closest bone of the left hand
            rightHandDistancesBuffer = new ComputeBuffer(climbingHoldVerticesCount, sizeof(float)); // Distance from each vertex of the climbing hold to the closest bone of the right hand

            // Calculate input buffer data
            for (int i = 0; i < climbingHoldVertices.Length; i++)
            {
                climbingHoldVertices[i] = climbingHold.transform.TransformPoint(climbingHoldVertices[i]); // Convert to world position
            }
            climbingHoldVerticesBuffer.SetData(climbingHoldVertices);
            leftHandBonesBuffer.SetData(leftHandBones.ConvertAll(bone => bone.transform.position).ToArray());
            rightHandBonesBuffer.SetData(rightHandBones.ConvertAll(bone => bone.transform.position).ToArray());

            // Pass buffers to compute shader
            distanceToClosestBoneComputeShader.SetBuffer(kernelHandle, "climbingHoldVertices", climbingHoldVerticesBuffer);
            distanceToClosestBoneComputeShader.SetBuffer(kernelHandle, "leftHandBones", leftHandBonesBuffer);
            distanceToClosestBoneComputeShader.SetBuffer(kernelHandle, "rightHandBones", rightHandBonesBuffer);
            distanceToClosestBoneComputeShader.SetBuffer(kernelHandle, "leftHandDistances", leftHandDistancesBuffer);
            distanceToClosestBoneComputeShader.SetBuffer(kernelHandle, "rightHandDistances", rightHandDistancesBuffer);

            // Dispatch compute shader and retrieve output buffer data
            distanceToClosestBoneComputeShader.Dispatch(kernelHandle, climbingHoldVerticesCount / 128, 1, 1);
            float[] leftHandDistances = new float[climbingHoldVerticesCount];
            float[] rightHandDistances = new float[climbingHoldVerticesCount];
            leftHandDistancesBuffer.GetData(leftHandDistances);
            rightHandDistancesBuffer.GetData(rightHandDistances);

            // Release buffers
            climbingHoldVerticesBuffer.Release();
            leftHandBonesBuffer.Release();
            rightHandBonesBuffer.Release();
            leftHandDistancesBuffer.Release();
            rightHandDistancesBuffer.Release();

            // Encode the distances in the UVs and set them in the climbing hold's mesh so that in the shader
            // This works because the order of the vertices is the same in Mesh.vertices and Mesh.uv, both in Unity and in the shader
            Vector2[] newClimbingHoldMeshUVs = new Vector2[climbingHoldVerticesCount];
            for (int i = 0; i < climbingHoldVerticesCount; i++)
            {
                newClimbingHoldMeshUVs[i] = new Vector2(leftHandDistances[i], rightHandDistances[i]);
            }
            climbingHoldMesh.SetUVs(2, newClimbingHoldMeshUVs.ToList());
        }
    }


    public void LeftHandHoverEnter(HoverEnterEventArgs args)
    {
        HandHoverEnter(leftHand, args);
    }
    public void RightHandHoverEnter(HoverEnterEventArgs args)
    {
        HandHoverEnter(rightHand, args);
    }
    void HandHoverEnter(GameObject hand, HoverEnterEventArgs args)
    {
        IXRHoverInteractable hoveredObject = args.interactableObject;
        MonoBehaviour hoveredObjectMB = hoveredObject as MonoBehaviour;
        if (hoveredObjectMB == null)
        {
            Debug.Log("Hand hover enter: " + hand.name + " is now interacting with something that isn't a MonoBehaviour.");
            return;
        }
        GameObject hoveredGameObject = hoveredObjectMB.gameObject;
        if (hoveredGameObject.tag == "ClimbingHold")
        {
            Debug.Log("Hand hover enter: " + hand.name + " is now interacting with Climbing Hold " + hoveredGameObject.name);

            MeshRenderer meshRenderer = hoveredGameObject.GetComponent<MeshRenderer>();
            meshRenderer.material.SetInt("_IsBeingInteracted", 1);
            meshRenderer.material.SetFloat("_InteractionColorMaxDistance", hoverRadiusOverride);

            if (hand == leftHand)
            {
                leftHandInteractingClimbingHold = hoveredGameObject;
            }
            else if (hand == rightHand)
            {
                rightHandInteractingClimbingHold = hoveredGameObject;
            }
        }
        else
        {
            Debug.Log("Hand hover enter: " + hand.name + " is now interacting with GameObject " + hoveredGameObject.name);
        }
    }

    public void LeftHandHoverExit(HoverExitEventArgs args)
    {
        HandHoverExit(leftHand, args);
    }
    public void RightHandHoverExit(HoverExitEventArgs args)
    {
        HandHoverExit(rightHand, args);
    }
    void HandHoverExit(GameObject hand, HoverExitEventArgs args)
    {
        IXRHoverInteractable hoveredObject = args.interactableObject;
        MonoBehaviour hoveredObjectMB = hoveredObject as MonoBehaviour;
        if (hoveredObjectMB == null)
        {
            Debug.Log("Hand hover exit: " + hand.name + " is no longer interacting with something that isn't a MonoBehaviour.");
            return;
        }
        GameObject hoveredGameObject = hoveredObjectMB.gameObject;
        if (hoveredGameObject.tag == "ClimbingHold")
        {
            Debug.Log("Hand hover exit: " + hand.name + " is no longer interacting with Climbing Hold " + hoveredGameObject.name);

            MeshRenderer meshRenderer = hoveredGameObject.GetComponent<MeshRenderer>();
            meshRenderer.material.SetInt("_IsBeingInteracted", 0);

            if (hand == leftHand)
            {
                leftHandInteractingClimbingHold = null;
            }
            else if (hand == rightHand)
            {
                rightHandInteractingClimbingHold = null;
            }
        }
        else
        {
            Debug.Log("Hand hover exit: " + hand.name + " is no longer interacting with GameObject " + hoveredGameObject.name);
        }
    }
}
