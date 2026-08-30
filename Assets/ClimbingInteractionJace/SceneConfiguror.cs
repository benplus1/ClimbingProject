using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text.RegularExpressions;
using Unity.VisualScripting;
using Unity.XR.CoreUtils;
using UnityEngine;
using UnityEngine.Networking;
using UnityEngine.XR.Interaction.Toolkit;
using UnityEngine.XR.Interaction.Toolkit.Interactables;
using UnityEngine.XR.Interaction.Toolkit.Interactors;
using UnityEngine.XR.Interaction.Toolkit.Interactors.Casters;
using Debug = UnityEngine.Debug;

public enum GameMode
{
    Basic = 0, // Only shader
    Grip = 1, // JACE: In development, fixed-grip movement mode
    Ghost = 2,
}

public enum RoutesLoadState
{
    Loading,
    Ready,
    Failed,
}

public enum HoldAffordancesLoadState
{
    Loading,
    Ready,
    Failed,
}

public class SceneConfiguror : MonoBehaviour
{
    private const int TrackedBoneCount = 26;
    private const string StudyHoldsLayerName = "StudyHolds";
    private const string StudyGhostHoldsLayerName = "StudyGhostHolds";
    private static readonly int[] FingertipBoneIndices = { 5, 10, 15, 20, 25 };
    [Header("Action Recorder")]
    public ActionRecorder actionRecorder;
    [Header("Route Cues")]
    [SerializeField] private RouteCuePresentation baselineRouteCuePresentation =
        RouteCuePresentation.PhysicalBoardLeds;

    [Header("Minimap Settings")]
    public Camera mainCamera;     // assign your main/world camera here
    //public Camera minimapCamera;  // assign your minimap camera here [Update 7/7/2026: we don't need a minimap anymore. CAROLINE]
    [Header("Scene References")]
    public GameObject environment;
    public GameObject gripLocomotionSceneryRoot;
    public GameObject holdsParentGameObject;
    public Dictionary<string, GameObject> holdsDictionary;
    public List<string> activeRouteHoldsNamesList;
    public List<GameObject> activeHoldsList;
    private int studyHoldsLayer = -1;
    private int studyGhostHoldsLayer = -1;
    private bool panelInputSuppressed;

    [Header("Hands References")]
    public GameObject centerEyeAnchor;
    public OVRSkeleton leftHandOVRSkeleton;
    public OVRSkeleton rightHandOVRSkeleton;

    [Header("Hands State")]
    public int numBonesPerHand;
    public Vector3 centerEyePosition;
    public List<Vector3> leftHandBonePositions = new List<Vector3>();
    public List<Vector3> rightHandBonePositions = new List<Vector3>();
    public List<Quaternion> leftHandBoneQuaternions = new List<Quaternion>();
    public List<Quaternion> rightHandBoneQuaternions = new List<Quaternion>();

    [Header("Climb Settings")]
    public GameMode gameMode;
    public string currentRouteName = string.Empty;
    public GhostHoldController ghostHoldController;

    [Header("Ghost Viewing Standoff")]
    // Detached inspection never touches the wall, so the reach standoff that Condition B needs
    // leaves the top of a 40-degree board hanging behind the participant's head where it can be
    // neither seen nor pointed at. Condition C therefore takes the board further away for as long
    // as it is the active technique; see GhostViewingStandoffPolicy for the derivation.
    [SerializeField] private float ghostViewingExtraStandoffMeters =
        GhostViewingStandoffPolicy.DefaultExtraStandoffMeters;

    private const float GhostStandoffDriftToleranceSqrMeters = 0.0001f;
    private readonly List<GameObject> ghostGripTargets = new();
    private Vector3 ghostStandoffBaselinePosition;
    private Vector3 appliedGhostStandoffDelta;
    private bool ghostStandoffApplied;

    public RouteDefinition ActiveRouteDefinition { get; internal set; }
    public RoutesLoadState RoutesJsonLoadState => routes.RoutesJsonLoadState;
    public string RoutesLoadFailureReason => routes.RoutesLoadFailureReason;
    public string RoutesJsonSha256 => routes.RoutesJsonSha256;
    public HoldAffordancesLoadState HoldAffordancesState => HoldAffordances.State;
    public string HoldAffordancesFailureReason => HoldAffordances.FailureReason;

    [Header("Interaction Settings")]
    public float interactionColorMaxDistanceOverride;
    public bool disableInactiveHolds;
    public float inactiveHoldAlpha;
    public float activeHoldAlpha;

    [Header("Interaction State")]
    public GameObject leftHandInteractingClimbingHold;
    public GameObject rightHandInteractingClimbingHold;
    public int HoverContactEpoch { get; private set; }
    public bool IsPanelInputSuppressed => panelInputSuppressed;

    [Header("Interaction Compute Shader Settings")]
    public ComputeShader distanceToClosestBoneComputeShader;
    public int kernelHandle;

    [Header("Grip Settings")]
    public GameObject moonBoardEnv;
    public float gripFingertipRange;
    public GameObject leftHandGripStatusDisplayHelper;
    public GameObject rightHandGripStatusDisplayHelper;
    public GripScoreConfig gripScoreConfig;
    [Range(1, 4)] public int defaultMinFingers = 3;
    [Range(0f, 1f)] public float gripFlexionEngageThreshold = 0.55f;
    [Range(0f, 1f)] public float gripFlexionReleaseThreshold = 0.35f;
    [Min(0f)] public float gripReleaseGraceSeconds = 0.15f;
    [Min(0f)] public float gripTrackingFreezeSeconds = 0.25f;
    [Min(0.01f)] public float gripFrozenTimeoutSeconds = 2f;
    [Min(0.01f)] public float gripOneEuroMinCutoff = 1f;
    [Min(0f)] public float gripOneEuroBeta = 0.007f;
    [Min(0.01f)] public float gripMaximumAcceleration = 12f;

    [Header("Grip State")]
    public float[] leftHandBoneToHoldMinDistances;
    public float[] rightHandBoneToHoldMinDistances;
    public bool isGripLocomotionActive;
    public bool leftHandIsGripping;
    public bool rightHandIsGripping;
    public int perFingerContactMask = -1;
    public float currentGripScore = -1f;
    public int leftFingerContactMask;
    public int rightFingerContactMask;
    public float leftHandGripScore;
    public float rightHandGripScore;
    public IReadOnlyList<float> LeftFingerCurls => Grip.LeftFingerCurls;
    public IReadOnlyList<float> RightFingerCurls => Grip.RightFingerCurls;
    public event Action<string, Hand, GameObject, string> GripEngagementRecorded;
    public Vector3 leftHandGripStartPosition;
    public Vector3 leftHandGripLastPosition;
    public Vector3 rightHandGripStartPosition;
    public Vector3 rightHandGripLastPosition;
    public List<Vector3> leftHandGripStartPose;
    public List<Vector3> leftHandGripCurrentPose;
    public List<Vector3> rightHandGripStartPose;
    public List<Vector3> rightHandGripCurrentPose;
    private GripContactPipeline gripContactPipeline;
    private bool gripRecoveryAttempted;
    private bool debugForceGripReadbackFailures;
    public bool IsGripFeedbackDegraded { get; private set; }
    public string GripFeedbackDegradedUtc { get; private set; }
    public bool IsDegradedGripAcquisitionActive => Grip.IsDegradedAcquisitionActive;
    public string DegradedGripAcquisitionFailureReason => Grip.DegradedAcquisitionFailureReason;
    private string holdsDictionaryError = string.Empty;

    // Collaborators are created on first use rather than in Awake so the route, affordance and
    // feedback properties below stay answerable no matter which component queries them first.
    private GripInteractionCoordinator grip;
    private GripInteractionCoordinator Grip => grip ??= new GripInteractionCoordinator(this);
    private readonly RouteCatalogService routes = new();
    private HoldAffordanceLoader holdAffordances;
    private StudyEnvironmentPresenter studyEnvironment;
    private HoldVisualsController holdVisuals;
    private HoverContactTracker hoverContacts;
    private HoldAffordanceLoader HoldAffordances =>
        holdAffordances ??= new HoldAffordanceLoader(routes);
    private StudyEnvironmentPresenter StudyEnvironment =>
        studyEnvironment ??= new StudyEnvironmentPresenter(this);
    private HoldVisualsController HoldVisuals =>
        holdVisuals ??= new HoldVisualsController(this);
    private HoverContactTracker HoverContacts =>
        hoverContacts ??= new HoverContactTracker(this, HoldVisuals);

    private void Awake()
    {
        Grip.Initialize();
        EnsureRuntimeControllers();
    }

    void Start()
    {
        UnityEngine.Debug.Log("SceneConfiguror initializing.");
        StudyEnvironment.CacheMoonBoardTransform();

        // Add all the children of the holds parent to the holds dictionary, to be accessed using the string [A-K][1-18]
        // Jace: Note that the holds are currently named [A-K][1-18].[001/002/003]
        EnsureHoldsDictionary();
        EnsureGripPipeline();
        StartCoroutine(routes.LoadRoutesJson());
        StartCoroutine(HoldAffordances.LoadHoldAffordances());

        EnsureRuntimeControllers();
        ghostHoldController.Initialize(this);
        SetGameMode(gameMode);
    }

    void Update()
    {
        EnsureHoldsDictionary();
        EnsureGripPipeline();
        gripContactPipeline?.Update(Time.unscaledTime);
        HandleGripPipelineHealth();
        centerEyePosition = centerEyeAnchor.transform.position;

        // Override interaction color max distance, update interaction status
        if (leftHandInteractingClimbingHold != null)
        {
            HoldVisuals.SetInteractionVisual(
                leftHandInteractingClimbingHold,
                IsLegacyInteractionShaderActive,
                interactionColorMaxDistanceOverride);
        }
        if (rightHandInteractingClimbingHold != null)
        {
            HoldVisuals.SetInteractionVisual(
                rightHandInteractingClimbingHold,
                IsLegacyInteractionShaderActive,
                interactionColorMaxDistanceOverride);
        }

        Grip.UpdateTracking();
        EnsureGripDistanceArrays(TrackedBoneCount);
        gripContactPipeline?.Process(
            leftHandInteractingClimbingHold,
            rightHandInteractingClimbingHold,
            leftHandBonePositions,
            rightHandBonePositions,
            Grip.LeftFingerCurls,
            Grip.RightFingerCurls,
            Grip.LeftTrackingValid,
            Grip.RightTrackingValid);

        // Grip state is shared by in-context and detached examination. Only Grip mode may move the board.
        if (!panelInputSuppressed)
        {
            if (gameMode == GameMode.Grip)
            {
                UpdateGripMode();
            }
            else if (gameMode == GameMode.Ghost && ghostHoldController != null &&
                     ghostHoldController.HasGhosts)
            {
                UpdateGripMode(false);
            }
        }

        HoldVisuals.UpdateGripAffordances();

        if (!Grip.LeftTrackingValid && !Grip.RightTrackingValid)
        {
            return;
        }

    }

    /// <summary>True while the legacy per-hold interaction shader owns the hold tint, i.e. the GPU
    /// grip pipeline is absent and feedback has not degraded.</summary>
    internal bool IsLegacyInteractionShaderActive =>
        gripContactPipeline == null && !IsGripFeedbackDegraded;

    internal void NotifyGripTargetDiscontinuity(Hand hand)
    {
        gripContactPipeline?.NotifyTargetDiscontinuity(hand);
    }

    private void EnsureGripDistanceArrays(int count)
    {
        if (leftHandBoneToHoldMinDistances == null || leftHandBoneToHoldMinDistances.Length != count)
        {
            leftHandBoneToHoldMinDistances = new float[count];
            Array.Fill(leftHandBoneToHoldMinDistances, float.PositiveInfinity);
        }
        if (rightHandBoneToHoldMinDistances == null || rightHandBoneToHoldMinDistances.Length != count)
        {
            rightHandBoneToHoldMinDistances = new float[count];
            Array.Fill(rightHandBoneToHoldMinDistances, float.PositiveInfinity);
        }
    }

    internal void ResetHandDistances(int hand)
    {
        float[] distances = hand == 0
            ? leftHandBoneToHoldMinDistances
            : rightHandBoneToHoldMinDistances;
        if (distances != null)
        {
            Array.Fill(distances, float.PositiveInfinity);
        }
    }

    public void UpdateGripMode(bool allowLocomotion = true)
    {
        Grip.UpdateGripMode(allowLocomotion);
    }

    internal void RaiseGripEngagement(string action, Hand hand, GameObject hold, string details)
    {
        GripEngagementRecorded?.Invoke(action, hand, hold, details);
        actionRecorder?.Record(action, hand == Hand.Left ? "Left" : "Right", hold, details);
    }

    internal void PublishGripAcquisitionSample(
        Hand hand,
        int holdId,
        IReadOnlyList<float> curls,
        IReadOnlyList<float> distances,
        float sampledAt)
    {
        Grip.PublishAcquisitionSample(hand, holdId, curls, distances, sampledAt);
    }

    internal void InvalidateGripAcquisitionSample(Hand hand)
    {
        Grip.InvalidateAcquisitionSample(hand);
    }

    public bool CheckIfHandIsGrippingHold(int handIndex, GameObject climbingHold)
    {
        // If we don't have access to the expected hand bone positions, return false.
        // NOTE: The expected hand bone positions refer to the standard number and ordering of hand bone positions as returned from OVRSkeleton.Bones

        // Clarify left or right hand
        float[] handBoneToHoldMinDistances = null;
        if (handIndex == 0)
        {
            handBoneToHoldMinDistances = leftHandBoneToHoldMinDistances;
        }
        else if (handIndex == 1)
        {
            handBoneToHoldMinDistances = rightHandBoneToHoldMinDistances;
        }

        if (handBoneToHoldMinDistances == null || handBoneToHoldMinDistances.Length <= 25)
        {
            return false;
        }

        // Keep the legacy all-five-fingertips threshold as telemetry only. Locomotion consumes
        // GripLatchStateMachine instead.
        // Currently, the following will check if each fingertip is close to the hold. (When using Meta SDK v71's new OpenXR Hand Skeleton, bone indices 5, 10, 15, 20, 25 for thumb, pointer, middle, ring, and little fingertips respectively)
        bool isGripping = true;
        foreach (int boneIndex in FingertipBoneIndices)
        {
            if (handBoneToHoldMinDistances[boneIndex] > gripFingertipRange)
            {
                isGripping = false;
                break;
            }
        }

        return isGripping;
    }
    public bool AreHandPosesApproximatelyEqual(List<Vector3> pose1, List<Vector3> pose2, float threshold)
    {
        if (pose1.Count != pose2.Count)
        {
            return false;
        }
        for (int i = 2; i < pose1.Count; i++) // Skip bones 0 (palm) and 1 (wrist)
        {
            if (Vector3.Distance(pose1[i], pose2[i]) > threshold)
            {
                return false;
            }
        }
        return true;
    }

    public OVRSkeleton GetOVRSkeletonFromHandIndex(int handIndex)
    {
        if (handIndex == 0)
        {
            return leftHandOVRSkeleton;
        }
        else if (handIndex == 1)
        {
            return rightHandOVRSkeleton;
        }
        else
        {
            UnityEngine.Debug.LogError("Hand index " + handIndex + " not found!");
            return null;
        }
    }
    public void HandHoverEnter(int hand, GameObject hoveredGameObject)
    {
        HoverContacts.HandHoverEnter(hand, hoveredGameObject);
    }

    public void HandHoverExit(int hand, GameObject hoveredGameObject)
    {
        HoverContacts.HandHoverExit(hand, hoveredGameObject);
    }

    public void SetUpRouteByName(string routeName)
    {
        EnsureHoldsDictionary();
        UnityEngine.Debug.Log("Requested route by name: " + routeName);
        if (!routes.TryGetRouteDefinition(routeName, out RouteDefinition route))
        {
            UnityEngine.Debug.LogError("Route name " + routeName + " not found!");
            return;
        }

        ResetInteractionState();
        Grip.ClearDegradedGeometryCache();
        ActiveRouteDefinition = route;
        activeRouteHoldsNamesList = new List<string>(route.holds);
        currentRouteName = routeName;
        if (routeName != "[PREVIEW ALL (SHADER OFF)]")
        {
            UnityEngine.Debug.Log("Setting up route " + routeName + " with holds " + string.Join(", ", activeRouteHoldsNamesList));
            HoldVisuals.SetUpRouteByHoldList(route);
        }
        else
        {
            UnityEngine.Debug.Log("Setting up route " + routeName + " with all holds");
            HoldVisuals.PreviewAllHolds();
        }
        ApplyModeToRouteHolds();
        if (gameMode == GameMode.Grip)
        {
            gripContactPipeline?.Prepare(activeHoldsList);
        }
    }

    public bool SetRouteCatalog(MoonBoardStudyCatalog catalog, out string error)
    {
        if (catalog == null)
        {
            error = "MoonBoard route catalog is unavailable.";
            return false;
        }
        if (!catalog.TryValidate(out error))
        {
            return false;
        }
        if (HoldAffordances.Catalog != null &&
            !HoldAffordanceLoader.TryValidateHoldAffordances(catalog, HoldAffordances.Catalog, out error))
        {
            return false;
        }

        routes.SetCatalog(catalog);
        holdsDictionary = null;
        EnsureHoldsDictionary();
        if (!string.IsNullOrEmpty(holdsDictionaryError))
        {
            error = holdsDictionaryError;
            return false;
        }
        if (holdsDictionary.Count != catalog.holds.Length)
        {
            error = $"Scene contains {holdsDictionary.Count} holds; catalog requires {catalog.holds.Length}.";
            return false;
        }
        foreach (MoonBoardHoldDefinition hold in catalog.holds)
        {
            if (!holdsDictionary.TryGetValue(hold.coordinate, out GameObject sceneHold) ||
                sceneHold.GetComponent<MeshFilter>() == null ||
                sceneHold.GetComponent<MeshRenderer>() == null)
            {
                error = "Scene is missing usable MoonBoard 2016 hold " + hold.coordinate + ".";
                return false;
            }
        }
        foreach (string coordinate in holdsDictionary.Keys)
        {
            if (!catalog.TryGetHold(coordinate, out _))
            {
                error = "Scene contains hold outside the MoonBoard 2016 catalog: " + coordinate + ".";
                return false;
            }
        }

        currentRouteName = catalog.routes[0].id;
        SetUpRouteByName(currentRouteName);
        error = string.Empty;
        return true;
    }

    public bool TryGetRouteDefinition(string routeId, out MoonBoardRouteDefinition route)
    {
        return routes.TryGetRouteDefinition(routeId, out route);
    }

    public bool TryValidateRoute(string routeName, out string error)
    {
        EnsureHoldsDictionary();
        if (!string.IsNullOrEmpty(holdsDictionaryError))
        {
            error = holdsDictionaryError;
            return false;
        }
        if (!routes.TryEnsureRouteSourceReady(routeName, out error))
        {
            return false;
        }
        if (!routes.TryGetRouteDefinition(routeName, out RouteDefinition route))
        {
            error = "Unknown route: " + routeName + ".";
            return false;
        }

        string[] missing = route.holds.Where(hold => !holdsDictionary.ContainsKey(hold)).ToArray();
        if (missing.Length > 0)
        {
            error = routeName + " is missing hold" + (missing.Length == 1 ? " " : "s ") +
                    string.Join(", ", missing) + ".";
            return false;
        }

        error = string.Empty;
        return true;
    }

    public bool TrySelectBaselineRoute(string routeName, out string error)
    {
        if (!TryValidateRoute(routeName, out error))
        {
            return false;
        }
        if (!routes.TryGetRouteDefinition(routeName, out RouteDefinition route))
        {
            error = "Unknown route: " + routeName + ".";
            return false;
        }

        ActiveRouteDefinition = route;
        activeRouteHoldsNamesList = new List<string>(route.holds);
        currentRouteName = routeName;
        error = string.Empty;
        return true;
    }

    public bool IsBuiltInRoute(string routeName)
    {
        return routes.IsBuiltInRoute(routeName);
    }

    public string GetRoutesLoadStatusLine()
    {
        return routes.GetRoutesLoadStatusLine();
    }

    /// <summary>Catalog routes first, then built-in study routes, then routes.json entries.</summary>
    public List<string> GetAvailableRouteNames()
    {
        return routes.GetAvailableRouteNames();
    }

    public List<string> GetStudyRouteNames()
    {
        return routes.GetStudyRouteNames();
    }

    public void SetGameMode(GameMode newMode)
    {
        bool leavingGhostMode = gameMode == GameMode.Ghost && newMode != GameMode.Ghost;
        if (leavingGhostMode && ghostHoldController != null)
        {
            ghostHoldController.SetModeActive(false);
        }
        if (newMode == GameMode.Ghost)
        {
            ApplyGhostViewingStandoff();
        }
        else
        {
            RemoveGhostViewingStandoff();
        }
        ResetInteractionState();
        gameMode = newMode;
        SetRouteCuePresentation(newMode == GameMode.Basic
            ? baselineRouteCuePresentation
            : RouteCuePresentation.VirtualHalos);
        ApplyModeToRouteHolds();
        if (newMode == GameMode.Grip || newMode == GameMode.Ghost)
        {
            Grip.PrewarmDegradedGeometry(activeHoldsList);
        }
        if (!leavingGhostMode && ghostHoldController != null)
        {
            ghostHoldController.SetModeActive(newMode == GameMode.Ghost);
        }
        if (newMode == GameMode.Grip)
        {
            gripContactPipeline?.Prepare(activeHoldsList);
        }
        else
        {
            gripContactPipeline?.ClearFeedback();
            gripContactPipeline?.Prepare((IReadOnlyList<GameObject>)null);
        }
        actionRecorder?.Record("ModeChanged", "", null, "mode=" + newMode);
    }

    /// <summary>True from the moment the palm-up summon dwell completes until the pinch lands or
    /// the palm drops. Input is suppressed while armed, but the console itself is still closed —
    /// the diagnostics HUD dims for this state instead of vanishing.</summary>
    public bool IsPanelSummonArmed { get; private set; }

    public void SetPanelSummonArmed(bool armed)
    {
        IsPanelSummonArmed = armed;
    }

    public void SetPanelInputSuppressed(bool suppressed)
    {
        bool suppressionStarted = suppressed && !panelInputSuppressed;
        panelInputSuppressed = suppressed;
        Grip.SetInputSuppressed(suppressed);
        if (suppressionStarted)
        {
            ResetInteractionState();
        }
        ApplyModeToRouteHolds();
        ghostHoldController?.SetPanelInputSuppressed(suppressed);
    }

    /// <summary>The engagement that must stand the palm-up console summon down, or None while
    /// the participant's hands are free. Evaluated by the summon detector every frame, so a
    /// dwell can never complete - and an armed summon cancels, lifting its input suppression -
    /// while a hand is latched, locomoting, hovering a hold, or holding a ghost proxy.</summary>
    public SummonBlockReason GetSummonBlockReason()
    {
        return SummonGatePolicy.GetBlockReason(
            Grip.GetLatchedHold(Hand.Left) != null,
            Grip.GetLatchedHold(Hand.Right) != null,
            isGripLocomotionActive,
            leftHandInteractingClimbingHold != null,
            rightHandInteractingClimbingHold != null,
            ghostHoldController != null && ghostHoldController.IsAnyProxyHeld);
    }

    public RouteCuePresentation BaselineRouteCuePresentation => baselineRouteCuePresentation;
    public RouteCuePresentation CurrentRouteCuePresentation { get; private set; } =
        RouteCuePresentation.Hidden;
    public bool AreVirtualRouteCuesVisible =>
        CurrentRouteCuePresentation == RouteCuePresentation.VirtualHalos;

    public RouteCuePresentation GetRouteCuePresentationForCondition(string condition)
    {
        return RouteCuePolicy.ForCondition(condition, baselineRouteCuePresentation);
    }

    public RouteCueRole GetRouteCueRole(string holdCoordinate)
    {
        if (ActiveRouteDefinition?.start != null &&
            Array.Exists(ActiveRouteDefinition.start,
                coordinate => string.Equals(coordinate, holdCoordinate, StringComparison.OrdinalIgnoreCase)))
        {
            return RouteCueRole.Start;
        }
        if (ActiveRouteDefinition?.finish != null &&
            Array.Exists(ActiveRouteDefinition.finish,
                coordinate => string.Equals(coordinate, holdCoordinate, StringComparison.OrdinalIgnoreCase)))
        {
            return RouteCueRole.Finish;
        }
        return RouteCueRole.Intermediate;
    }

    public RouteCueStyle GetRouteCueStyle(string holdCoordinate)
    {
        return RouteCuePolicy.GetStyle(GetRouteCueRole(holdCoordinate));
    }

    public void SetBaselineRouteCuePresentation(RouteCuePresentation presentation)
    {
        baselineRouteCuePresentation = presentation;
        if (gameMode == GameMode.Basic)
        {
            SetRouteCuePresentation(presentation);
        }
    }

    public void SetRouteCuePresentation(RouteCuePresentation presentation)
    {
        CurrentRouteCuePresentation = presentation;
        bool showRoleRings = presentation == RouteCuePresentation.VirtualHalos;
        HoldVisuals.SetRoleRingsVisible(showRoleRings);
        if (!showRoleRings)
        {
            HoldVisuals.ClearRoleRings();
        }
    }

    public void PrepareGripHold(GameObject hold)
    {
        gripContactPipeline?.Prepare(hold);
    }

    public void ResetMoonBoardTransform()
    {
        StudyEnvironment.ResetMoonBoardTransform();
    }

    /// <summary>Parent of the board that fiducial calibration and the runtime seating both write.</summary>
    internal Transform BoardAlignmentRoot =>
        moonBoardEnv != null ? moonBoardEnv.transform.parent : null;

    // The standoff is layered onto the alignment root rather than onto the board itself. Those are
    // two independent layers: ResetMoonBoardTransform restores the board's local pose and so leaves
    // this offset intact across a block reset, while fiducial calibration writes the alignment
    // root absolutely - and always after BeginCalibration has dropped the mode back to Basic, which
    // removes the offset first. Re-entering the technique then re-derives it from the calibrated
    // pose, so an aligned board always wins.
    private void ApplyGhostViewingStandoff()
    {
        Transform alignmentRoot = BoardAlignmentRoot;
        if (ghostStandoffApplied || alignmentRoot == null)
        {
            return;
        }

        float extraStandoff =
            GhostViewingStandoffPolicy.ClampExtraStandoffMeters(ghostViewingExtraStandoffMeters);
        if (extraStandoff <= 0f)
        {
            return;
        }

        Vector3 delta =
            GhostViewingStandoffPolicy.GetRetreatDirection(alignmentRoot.rotation) * extraStandoff;
        ghostStandoffBaselinePosition = alignmentRoot.position;
        appliedGhostStandoffDelta = delta;
        alignmentRoot.position = ghostStandoffBaselinePosition + delta;
        ghostStandoffApplied = true;
    }

    private void RemoveGhostViewingStandoff()
    {
        if (!ghostStandoffApplied)
        {
            return;
        }

        Transform alignmentRoot = BoardAlignmentRoot;
        ghostStandoffApplied = false;
        if (alignmentRoot == null)
        {
            return;
        }

        // Anything that repositioned the alignment root while the offset was live - clearing an
        // alignment, localizing a spatial anchor - has already resolved the board where it belongs.
        // Subtracting a stale offset from that pose would push the board a further 1.8 m off, so
        // say so loudly and leave the resolved pose alone.
        Vector3 expectedPosition = ghostStandoffBaselinePosition + appliedGhostStandoffDelta;
        if ((alignmentRoot.position - expectedPosition).sqrMagnitude >
            GhostStandoffDriftToleranceSqrMeters)
        {
            Debug.LogWarning(
                "[SceneConfiguror] Board alignment moved while the ghost viewing standoff was applied " +
                "(expected " + expectedPosition.ToString("F4") + ", found " +
                alignmentRoot.position.ToString("F4") +
                "); keeping the resolved pose instead of subtracting a stale offset.");
            return;
        }
        alignmentRoot.position = ghostStandoffBaselinePosition;
    }

    /// <summary>Mid-run "back to start" for grip rehearsal: releases both grips and restores the
    /// board and scenery to their pristine pose, and nothing else — the active run, its recording,
    /// the mode, and the experimenter panel all continue untouched.</summary>
    public void ResetClimbToBase(string reason)
    {
        if (string.IsNullOrWhiteSpace(reason))
        {
            throw new ArgumentException("Climb reset reason is required.", nameof(reason));
        }

        actionRecorder?.Record("EnvironmentReset", "", null, "reason=" + reason);
        ResetInteractionState();
        ResetMoonBoardTransform();
    }

    public void ResetManualStudyState(bool restoreBasicMode = false)
    {
        ghostHoldController?.DismissGhost();
        HoldVisuals.ClearRoleRings();
        HoldVisuals.ClearGripAffordances();
        SetGameMode(restoreBasicMode ? GameMode.Basic : gameMode);
        ResetMoonBoardTransform();
        SetStudyEnvironmentVisible(true);
        SetStudyFeedbackVisible(true);
    }

    public void MoveStudyEnvironment(Vector3 worldDelta)
    {
        StudyEnvironment.MoveStudyEnvironment(worldDelta);
    }

    public void SetStudyEnvironmentVisible(bool visible)
    {
        StudyEnvironment.SetStudyEnvironmentVisible(visible);
    }

    public bool IsGripFeedbackReady => !IsGripFeedbackDegraded &&
                                        HoldAffordancesState == HoldAffordancesLoadState.Ready &&
                                        gripContactPipeline != null && gripContactPipeline.IsSupported;

    public void SetStudyFeedbackVisible(bool visible)
    {
        bool effectiveVisibility = visible && !IsGripFeedbackDegraded;
        StudyEnvironment.SetFeedbackVisible(effectiveVisibility);
        gripContactPipeline?.SetFeedbackVisible(effectiveVisibility);
        HoldVisuals.SetGripAffordancesVisible(effectiveVisibility);
    }

    public void SetGripLatchFeedback(Hand hand, GameObject hold, bool latched)
    {
        gripContactPipeline?.SetLatchFeedback(hand, hold, latched);
        HoldVisuals.SetGripLatchedHold(hand, hold, latched);
    }

    public void DebugInjectGripReadbackFailures(int epochCount = 1)
    {
        if (!Debug.isDebugBuild)
        {
            return;
        }
        EnsureGripPipeline();
        gripContactPipeline?.DebugInjectReadbackFailures(epochCount);
    }

    public void DebugSetGripReadbackFailures(bool enabled)
    {
        if (!Debug.isDebugBuild)
        {
            return;
        }
        debugForceGripReadbackFailures = enabled;
        EnsureGripPipeline();
        gripContactPipeline?.DebugSetReadbackFailures(enabled);
    }

    public Transform GetGripNormalReference(GameObject hold)
    {
        GameObject wallReferent = ghostHoldController != null
            ? ghostHoldController.GetWallReferent(hold)
            : null;
        return wallReferent != null ? wallReferent.transform : hold.transform;
    }

    public bool IsActiveRouteHold(GameObject candidate)
    {
        return GetActiveRouteHold(candidate) != null;
    }

    public GameObject GetActiveRouteHold(GameObject candidate)
    {
        if (candidate == null || activeHoldsList == null)
        {
            return null;
        }

        Transform candidateTransform = candidate.transform;
        foreach (GameObject activeHold in activeHoldsList)
        {
            if (activeHold != null &&
                (candidateTransform == activeHold.transform || candidateTransform.IsChildOf(activeHold.transform)))
            {
                return activeHold;
            }
        }
        return null;
    }

    public bool IsGhostHold(GameObject candidate)
    {
        return ghostHoldController != null && ghostHoldController.IsGhostHold(candidate);
    }

    public void RegisterGhostHold(GameObject ghost)
    {
        if (ghost == null)
        {
            return;
        }

        ResolveStudyLayers();
        if (studyGhostHoldsLayer >= 0)
        {
            SetLayerRecursively(ghost, studyGhostHoldsLayer);
        }

        XRGrabInteractable grab = ghost.GetComponent<XRGrabInteractable>();
        if (grab != null)
        {
            grab.enabled = gameMode == GameMode.Ghost && !panelInputSuppressed;
        }
    }

    public void UnregisterGhostHold(GameObject ghost)
    {
        if (ghost == null)
        {
            return;
        }

        HoverContacts.Remove(ghost);
        HoverContacts.RefreshHandHoverTarget(0);
        HoverContacts.RefreshHandHoverTarget(1);
        Grip.ReleaseHold(ghost.GetInstanceID());
    }

    /// <summary>Releases <paramref name="hold"/> from whichever grip latch holds it, leaving the
    /// hover and ghost registries untouched. Aligning rotates a proxy out of the gripping hand,
    /// so it has to drop the latch the way dismissing does, without unregistering the proxy.</summary>
    public void ReleaseGripHold(GameObject hold)
    {
        if (hold != null)
        {
            Grip.ReleaseHold(hold.GetInstanceID());
        }
    }

    /// <summary>True while either hand's grip latch is engaged on <paramref name="hold"/>.</summary>
    public bool IsGripHoldLatched(GameObject hold)
    {
        return hold != null &&
               (Grip.GetLatchedHold(Hand.Left) == hold || Grip.GetLatchedHold(Hand.Right) == hold);
    }

    /// <summary>The acquisition-gate configuration stamp for the run manifest (gate v1 = curl
    /// only, v2 = curl + coverage + grace), so every recording names the gate it ran under.</summary>
    public string DescribeGripGateVersion()
    {
        return Grip.DescribeGateVersion();
    }

    /// <summary>Which grip cue the participant was shown, so a recording says whether the per-finger
    /// contact patches were on top of the silhouette rim or the rim stood alone.</summary>
    public string DescribeGripCueVersion()
    {
        return gripScoreConfig != null && gripScoreConfig.contactPatchCueEnabled
            ? "rim+patches-v1"
            : "rim-v1";
    }

    private void ApplyModeToRouteHolds()
    {
        if (activeHoldsList == null)
        {
            return;
        }

        bool enableWallColliders = gameMode != GameMode.Basic;
        bool enableWallGrab = gameMode == GameMode.Grip && !panelInputSuppressed;
        foreach (GameObject hold in activeHoldsList)
        {
            if (hold == null)
            {
                continue;
            }

            XRGrabInteractable grab = hold.GetComponent<XRGrabInteractable>();
            if (grab != null)
            {
                grab.enabled = enableWallGrab;
            }
            foreach (Collider collider in hold.GetComponentsInChildren<Collider>(true))
            {
                collider.enabled = enableWallColliders;
            }
        }
    }

    private void ResetInteractionState()
    {
        Grip.ResetState();
        HoldVisuals.SetInteractionVisual(leftHandInteractingClimbingHold, false);
        if (rightHandInteractingClimbingHold != leftHandInteractingClimbingHold)
        {
            HoldVisuals.SetInteractionVisual(rightHandInteractingClimbingHold, false);
        }
        leftHandInteractingClimbingHold = null;
        rightHandInteractingClimbingHold = null;
        HoverContacts.Clear();
        HoverContactEpoch++;
        gripContactPipeline?.ClearFeedback();
        ResetHandDistances(0);
        ResetHandDistances(1);
        leftFingerContactMask = 0;
        rightFingerContactMask = 0;
        perFingerContactMask = 0;
        leftHandGripScore = 0f;
        rightHandGripScore = 0f;
        currentGripScore = 0f;
    }

    /// <summary>
    /// Recursively sets go and all its children to the given layer.
    /// </summary>
    internal static void SetLayerRecursively(GameObject go, int layer)
    {
        go.layer = layer;
        foreach (Transform child in go.transform)
            SetLayerRecursively(child.gameObject, layer);
    }

    private void EnsureHoldsDictionary()
    {
        if (holdsDictionary != null && holdsDictionary.Count > 0)
        {
            return;
        }

        holdsDictionary = new Dictionary<string, GameObject>(StringComparer.Ordinal);
        holdsDictionaryError = string.Empty;
        if (holdsParentGameObject == null)
        {
            return;
        }
        ResolveStudyLayers();
        foreach (Transform child in holdsParentGameObject.transform)
        {
            string holdName = child.name.Split('.')[0].ToUpperInvariant();
            if (!MoonBoardStudyCatalog.TryParseCoordinate(holdName, out _, out _))
            {
                holdsDictionaryError = "Invalid direct child in hold hierarchy: " + child.name + ".";
                continue;
            }
            if (!holdsDictionary.TryAdd(holdName, child.gameObject))
            {
                holdsDictionaryError = "Duplicate hold coordinate in scene: " + holdName + ".";
            }
            if (studyHoldsLayer >= 0)
            {
                SetLayerRecursively(child.gameObject, studyHoldsLayer);
            }
            FitHoldHoverCollider(child.gameObject);
        }
    }

    private void ResolveStudyLayers()
    {
        if (studyHoldsLayer < 0)
        {
            studyHoldsLayer = LayerMask.NameToLayer(StudyHoldsLayerName);
        }
        if (studyGhostHoldsLayer < 0)
        {
            studyGhostHoldsLayer = LayerMask.NameToLayer(StudyGhostHoldsLayerName);
        }
        if (studyHoldsLayer < 0 || studyGhostHoldsLayer < 0)
        {
            throw new InvalidOperationException(
                "Study hold layers are missing; hold and ghost interaction layers cannot be assigned.");
        }
    }

    // The scene's baked hold SphereColliders are authored with a hardcoded local radius that
    // the FBX import scale inflates to ~2 m world (measured live 2026-07-16): every hover
    // volume overlapped most of the board, hover targeting was last-enter-wins from meters
    // away, and the solid spheres intercepted the experimenter panel's pinch raycast. Fit the
    // sphere to the actual mesh bounds instead (the same math GhostHoldController applies to
    // ghost clones) and make it a trigger so HandHoverCollider still fires while
    // Physics.Raycast(..., QueryTriggerInteraction.Ignore) passes straight through.
    private static void FitHoldHoverCollider(GameObject hold)
    {
        SphereCollider sphere = hold.GetComponent<SphereCollider>();
        MeshFilter meshFilter = hold.GetComponent<MeshFilter>();
        if (sphere == null || meshFilter == null || meshFilter.sharedMesh == null)
        {
            return;
        }

        Bounds meshBounds = meshFilter.sharedMesh.bounds;
        sphere.center = meshBounds.center;
        sphere.radius = meshBounds.extents.magnitude;
        sphere.isTrigger = true;
    }

    public int GetMinFingersForHold(GameObject hold)
    {
        return ResolveMinFingers(hold);
    }

    private int ResolveMinFingers(GameObject hold)
    {
        if (HoldAffordances.Catalog == null || hold == null)
        {
            return defaultMinFingers;
        }

        GameObject wallReferent = ghostHoldController != null
            ? ghostHoldController.GetWallReferent(hold)
            : null;
        GameObject sourceHold = wallReferent != null ? wallReferent : hold;
        string coordinate = sourceHold.name.Split('.')[0];
        int ghostMarker = coordinate.IndexOf('#');
        if (ghostMarker >= 0)
        {
            coordinate = coordinate.Substring(0, ghostMarker);
        }
        coordinate = coordinate.ToUpperInvariant();
        return routes.Catalog != null &&
               routes.Catalog.TryGetHold(coordinate, out MoonBoardHoldDefinition definition)
            ? HoldAffordances.Catalog.ResolveMinFingers(definition.scanId, defaultMinFingers)
            : defaultMinFingers;
    }

    private void EnsureGripPipeline()
    {
        if (gripContactPipeline != null || IsGripFeedbackDegraded ||
            distanceToClosestBoneComputeShader == null)
        {
            return;
        }

        gripScoreConfig ??= Resources.Load<GripScoreConfig>("GripScoreConfig");
        if (gripScoreConfig == null)
        {
            gripScoreConfig = ScriptableObject.CreateInstance<GripScoreConfig>();
            Debug.LogWarning("GripScoreConfig asset was not found; using runtime defaults.");
        }
        gripContactPipeline = new GripContactPipeline(
            this,
            distanceToClosestBoneComputeShader,
            gripScoreConfig,
            gripRecoveryAttempted);
        gripContactPipeline.SetFeedbackVisible(
            StudyEnvironment.IsStudyFeedbackVisible && !IsGripFeedbackDegraded);
        gripContactPipeline.DebugSetReadbackFailures(debugForceGripReadbackFailures);
    }

    private void HandleGripPipelineHealth()
    {
        if (gripContactPipeline == null)
        {
            return;
        }

        if (gripContactPipeline.IsRecoveryReady)
        {
            Debug.LogWarning("[SceneConfiguror] Grip feedback recovery attempt after sustained readback failure.");
            actionRecorder?.Record(
                "GripFeedbackRecoveryAttempt",
                "",
                null,
                "recreating GPU readback pipeline");
            gripContactPipeline.ClearFeedback();
            gripContactPipeline.Dispose();
            gripContactPipeline = null;
            gripRecoveryAttempted = true;
            EnsureGripPipeline();
            PrepareCurrentGripTargets();
            Grip.RestoreLatchFeedback();
        }
        else if (gripContactPipeline.IsDegradationReady)
        {
            IsGripFeedbackDegraded = true;
            GripFeedbackDegradedUtc = DateTime.UtcNow.ToString("o");
            Debug.LogError("[SceneConfiguror] Grip feedback entered DEGRADED; block continues.");
            SetStudyFeedbackVisible(false);
            gripContactPipeline.Dispose();
            gripContactPipeline = null;
            Grip.ActivateDegradedAcquisition();
        }
    }

    private void PrepareCurrentGripTargets()
    {
        if (gripContactPipeline == null)
        {
            return;
        }

        if (gameMode == GameMode.Grip)
        {
            gripContactPipeline.Prepare(activeHoldsList);
        }
        else if (gameMode == GameMode.Ghost && ghostHoldController != null &&
                 ghostHoldController.HasGhosts)
        {
            ghostHoldController.CollectGhostRoots(ghostGripTargets);
            gripContactPipeline.Prepare(ghostGripTargets);
        }
    }

    private void EnsureRuntimeControllers()
    {
        ghostHoldController = ghostHoldController != null
            ? ghostHoldController
            : FindAnyObjectByType<GhostHoldController>();
        if (ghostHoldController == null)
        {
            GameObject controllerObject = new("GhostHoldController");
            ghostHoldController = controllerObject.AddComponent<GhostHoldController>();
        }
        if (ghostHoldController.GetComponent<StudyManager>() == null)
        {
            ghostHoldController.gameObject.AddComponent<StudyManager>();
        }
    }

    private void OnDestroy()
    {
        gripContactPipeline?.Dispose();
    }

}
