using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using TMPro;
using UnityEngine;

/// <summary>
/// Runtime-built experimenter console for selecting a VR mode and route, controlling a
/// manually completed run, recentering the participant while resetting the room, and hiding
/// or moving the panel with hand-ray interaction.
/// </summary>
public sealed class StudyControlPanel
{
    private const float PanelDistanceMeters = 0.72f;
    private const float PointerLengthMeters = 1.5f;
    // Platform-pointer feel: the aim direction is damped lightly while aiming and hard while the
    // pinch closes, the line starts a hand-width past its synthetic origin, and a press within
    // the hover grace window still lands on the button the pinch kicked the ray off of.
    private const float PointerRelaxedHalfLifeSeconds = 0.04f;
    private const float PointerPinchedHalfLifeSeconds = 0.25f;
    private const float PointerHoverGraceSeconds = 0.3f;
    private const float PointerProximalGapMeters = 0.12f;
    private const float ConfirmationWindowSeconds = 4f;
    private const float TextTransformScale = 0.01f;
    private const float PostDragSettleSeconds = 0.25f;
    private const float PanelWidthMeters = 0.82f;
    private const float PanelHeightMeters = 1.02f;
    private const float PanelViewportMargin = 0.04f;
    private const float MinimumPanelDepthMeters = 0.55f;
    private const float MaximumPanelDepthMeters = 1.5f;
    private const float PanelBottomMeters = -0.51f;
    private const float PanelTopMeters = 0.51f;
    private const int PanelClampSearchIterations = 10;
    // The console's unlit shader is ZTest Always with the ShaderLab default ZWrite On, which is what
    // keeps it readable over the board. Meta's hand material sits at the Transparent queue (3000) and
    // opens with a ZWrite-only depth pass, so the console has to be queued ahead of it: the console
    // stamps its own depth first, then the hand depth-tests against it and occludes the console
    // per pixel wherever a finger is nearer. Anything queued after 3000 draws over the hand instead.
    private const int PanelBackgroundQueue = 2900;
    private const int PanelButtonQueue = 2950;
    private const int PanelTextQueue = 2975;
    // The pointer ray stays above the hand: it leaves the hand it is cast from, and a targeting ray
    // that disappears into the palm cannot be aimed.
    private const int PanelPointerQueue = 4100;
    private const string ConfirmStartBlock = "start-block";
    private const string ConfirmCompleteBlock = "complete-block";
    private const string ConfirmStartPractice = "start-practice";
    private const string ConfirmEndPractice = "end-practice";
    private const string ConfirmStartAdhoc = "start-adhoc";
    private const string ConfirmClearAlignment = "clear-alignment";

    private static readonly Color PanelColor = new(0.035f, 0.06f, 0.095f, 0.99f);
    private static readonly Color PanelHoverColor = new(0.06f, 0.105f, 0.16f, 0.99f);
    private static readonly Color PanelGrabbedColor = new(0.09f, 0.155f, 0.225f, 0.99f);
    private static readonly Color AccentColor = new(0.19f, 0.78f, 0.92f, 1f);
    private static readonly Color MutedTextColor = new(0.90f, 0.93f, 0.97f, 1f);
    private static readonly Color PointerColor = new(0.10f, 0.72f, 0.92f, 0.92f);
    private static readonly Color PointerHoverColor = new(1f, 0.68f, 0.18f, 1f);
    private static readonly Color PointerMissColor = new(0.30f, 0.42f, 0.52f, 0.65f);
    private static readonly Color SessionButtonColor = new(0.055f, 0.32f, 0.50f, 1f);
    private static readonly Color SessionHoverColor = new(0.08f, 0.62f, 0.80f, 1f);
    private static readonly Color PracticeButtonColor = new(0.27f, 0.20f, 0.55f, 1f);
    private static readonly Color PracticeHoverColor = new(0.46f, 0.34f, 0.82f, 1f);
    private static readonly Color AdhocButtonColor = new(0.055f, 0.36f, 0.36f, 1f);
    private static readonly Color AdhocHoverColor = new(0.08f, 0.62f, 0.60f, 1f);
    private static readonly Color UtilityButtonColor = new(0.17f, 0.24f, 0.33f, 1f);
    private static readonly Color UtilityHoverColor = new(0.28f, 0.42f, 0.56f, 1f);
    private static readonly Color SelectedColor = new(0.93f, 0.58f, 0.12f, 1f);

    private readonly Transform panelParent;
    private readonly Camera userCamera;
    private readonly SceneConfiguror sceneConfiguror;
    private readonly BoardAlignmentController boardAlignment;
    private readonly StudySessionState state;
    private readonly Func<float> panelSettleSeconds;
    private readonly MaterialPropertyBlock pointerProperties = new();

    private SummonGestureDetector summonGesture;
    private BlockRunController blockRun;
    private PracticeController practice;
    private EstimationController estimation;

    private GameObject panelRoot;
    private TextMeshPro panelText;
    private Material panelMaterial;
    private Material buttonMaterial;
    private Material pointerMaterial;
    private Material textMaterial;
    private TMP_FontAsset fontAsset;
    private int uiLayer;
    private int uiLayerMask;

    private TextMeshPro adhocConditionLabel;
    private TextMeshPro adhocRouteLabel;
    private TextMeshPro blockActionLabel;
    private TextMeshPro practiceBLabel;
    private TextMeshPro practiceCLabel;
    private TextMeshPro practiceEndLabel;
    private TextMeshPro adhocStartLabel;
    private TextMeshPro clearAlignmentLabel;
    private TextMeshPro manualStartLabel;
    private TextMeshPro manualCompleteLabel;
    private TextMeshPro manualPreviousRouteLabel;
    private TextMeshPro manualNextRouteLabel;
    private TextMeshPro routeReadoutText;

    private StudyPanelButton previousParticipantButton;
    private StudyPanelButton nextParticipantButton;
    private StudyPanelButton previousBlockButton;
    private StudyPanelButton nextBlockButton;
    private StudyPanelButton blockActionButton;
    private StudyPanelButton practiceBButton;
    private StudyPanelButton practiceCButton;
    private StudyPanelButton practiceEndButton;
    private StudyPanelButton adhocConditionButton;
    private StudyPanelButton adhocRouteButton;
    private StudyPanelButton adhocStartButton;
    private StudyPanelButton alignBoardButton;
    private StudyPanelButton clearAlignmentButton;
    private StudyPanelButton manualModeAButton;
    private StudyPanelButton manualModeBButton;
    private StudyPanelButton manualPreviousRouteButton;
    private StudyPanelButton manualNextRouteButton;
    private StudyPanelButton manualStartButton;
    private StudyPanelButton manualCompleteButton;
    private StudyPanelButton manualRecenterButton;
    private StudyPanelButton manualCloseButton;
    private Renderer panelBackgroundRenderer;
    private Collider panelGrabSurfaceCollider;
    private Color panelSurfaceTint = PanelColor;

    private GameObject leftPointerRoot;
    private GameObject rightPointerRoot;
    private LineRenderer leftPointerLine;
    private LineRenderer rightPointerLine;
    private Renderer leftPointerReticle;
    private Renderer rightPointerReticle;
    private StudyPanelButton leftHoveredButton;
    private StudyPanelButton rightHoveredButton;
    private Vector3 leftPointerSmoothedForward;
    private Vector3 rightPointerSmoothedForward;
    private StudyPanelButton leftRecentHoverButton;
    private StudyPanelButton rightRecentHoverButton;
    private float leftRecentHoverAt = float.NegativeInfinity;
    private float rightRecentHoverAt = float.NegativeInfinity;

    private string lastRoutesStatusLine;
    private string lastBoardAlignmentStatus;
    private string pendingConfirmationKey = string.Empty;
    private float confirmationDeadline = -1f;
    private int confirmationArmedFrame = -1;
    private int lastPanelPressFrame = -1;
    private float panelPressableAt;
    private bool? lastRecordedPanelVisible;
    private bool leftWasPinching;
    private bool rightWasPinching;
    private bool leftPinchArmed;
    private bool rightPinchArmed;
    private int lastPracticeElapsedSecond = -1;
    private PanelGrabHand activePanelGrabHand;
    private float panelGrabRayDistance;
    private Vector3 panelGrabWorldOffset;

    private enum PanelGrabHand
    {
        None,
        Left,
        Right,
    }

    private struct PanelPointerTarget
    {
        public StudyPanelButton button;
        public bool isPanelSurface;
        public Vector3 hitPoint;
        public float hitDistance;
    }

    public StudyControlPanel(
        Transform panelParent,
        Camera userCamera,
        SceneConfiguror sceneConfiguror,
        BoardAlignmentController boardAlignment,
        StudySessionState state,
        Func<float> panelSettleSeconds)
    {
        this.panelParent = panelParent;
        this.userCamera = userCamera;
        this.sceneConfiguror = sceneConfiguror;
        this.boardAlignment = boardAlignment;
        this.state = state;
        this.panelSettleSeconds = panelSettleSeconds;
        lastBoardAlignmentStatus = boardAlignment != null ? boardAlignment.StatusMessage : string.Empty;
    }

    public bool IsPanelHidden => panelRoot != null && !panelRoot.activeSelf;
    public bool IsPanelBuilt => panelRoot != null;

    public void AttachSummonDetector(SummonGestureDetector summonGesture)
    {
        this.summonGesture = summonGesture;
    }

    public void AttachControllers(
        BlockRunController blockRun,
        PracticeController practice,
        EstimationController estimation)
    {
        this.blockRun = blockRun;
        this.practice = practice;
        this.estimation = estimation;
    }

    public void BuildPanel()
    {
        if (panelRoot != null)
        {
            return;
        }

        uiLayer = LayerMask.NameToLayer("UI");
        if (uiLayer < 0)
        {
            throw new InvalidOperationException("The study panel requires the project UI layer.");
        }
        uiLayerMask = 1 << uiLayer;

        panelMaterial = CreateOverlayMaterial(PanelColor);
        buttonMaterial = CreateButtonMaterial();
        pointerMaterial = CreateOverlayMaterial(PointerColor);
        fontAsset = Resources.Load<TMP_FontAsset>("Fonts & Materials/LiberationSans SDF");
        UnityEngine.Shader textShader = UnityEngine.Shader.Find("TextMeshPro/Mobile/Distance Field Overlay");
        if (fontAsset == null || textShader == null)
        {
            throw new InvalidOperationException(
                "The study panel requires LiberationSans SDF and the TMP mobile overlay shader.");
        }
        textMaterial = new Material(fontAsset.material)
        {
            shader = textShader,
            renderQueue = PanelTextQueue,
        };
        panelMaterial.renderQueue = PanelBackgroundQueue;
        buttonMaterial.renderQueue = PanelButtonQueue;
        pointerMaterial.renderQueue = PanelPointerQueue;

        panelRoot = new GameObject("Study Experimenter Console");
        panelRoot.layer = uiLayer;
        panelRoot.transform.SetParent(panelParent, false);

        GameObject background = GameObject.CreatePrimitive(PrimitiveType.Cube);
        background.name = "Console Background";
        background.layer = uiLayer;
        background.transform.SetParent(panelRoot.transform, false);
        background.transform.localScale = new Vector3(PanelWidthMeters, PanelHeightMeters, 0.012f);
        panelBackgroundRenderer = background.GetComponent<MeshRenderer>();
        panelBackgroundRenderer.sharedMaterial = panelMaterial;
        panelGrabSurfaceCollider = background.GetComponent<Collider>();

        TextMeshPro title = CreateText(
            panelRoot.transform,
            "Console Title",
            new Vector3(0f, 0.435f, -0.014f),
            new Vector2(0.76f, 0.05f),
            0.027f,
            Color.white,
            TextAlignmentOptions.Center,
            FontStyles.Bold);
        title.text = "STUDY CONSOLE";
        TextMeshPro subtitle = CreateText(
            panelRoot.transform,
            "Console Subtitle",
            new Vector3(0f, 0.392f, -0.014f),
            new Vector2(0.72f, 0.032f),
            0.012f,
            AccentColor,
            TextAlignmentOptions.Center,
            FontStyles.Bold);
        subtitle.text = "MANUAL START  |  MANUAL COMPLETE";

        panelText = CreateText(
            panelRoot.transform,
            "Session Summary",
            new Vector3(0f, 0.24f, -0.014f),
            new Vector2(0.72f, 0.23f),
            0.014f,
            MutedTextColor,
            TextAlignmentOptions.Top,
            FontStyles.Normal);
        panelText.lineSpacing = 0f;

        CreateSectionLabel("RUN", 0.105f);
        manualModeAButton = CreateButton(
            "Mode A",
            new Vector3(-0.19f, 0.055f, -0.02f),
            new Vector2(0.30f, 0.055f),
            "MODE A",
            () => SelectManualMode(0),
            out _);
        manualModeBButton = CreateButton(
            "Mode B",
            new Vector3(0.19f, 0.055f, -0.02f),
            new Vector2(0.30f, 0.055f),
            "MODE B",
            () => SelectManualMode(1),
            out _);
        routeReadoutText = CreateText(
            panelRoot.transform,
            "Route Readout",
            new Vector3(0f, -0.004f, -0.014f),
            new Vector2(0.72f, 0.026f),
            0.016f,
            AccentColor,
            TextAlignmentOptions.Center,
            FontStyles.Bold);
        manualPreviousRouteButton = CreateButton(
            "Previous Route",
            new Vector3(-0.19f, -0.055f, -0.02f),
            new Vector2(0.30f, 0.06f),
            "NO ROUTES",
            () => ChangeManualRoute(-1),
            out manualPreviousRouteLabel);
        manualNextRouteButton = CreateButton(
            "Next Route",
            new Vector3(0.19f, -0.055f, -0.02f),
            new Vector2(0.30f, 0.06f),
            "NO ROUTES",
            () => ChangeManualRoute(1),
            out manualNextRouteLabel);
        manualStartButton = CreateButton(
            "Start Run",
            new Vector3(-0.19f, -0.18f, -0.02f),
            new Vector2(0.30f, 0.06f),
            "START",
            StartManualRun,
            out manualStartLabel);
        manualCompleteButton = CreateButton(
            "Complete Run",
            new Vector3(0.19f, -0.18f, -0.02f),
            new Vector2(0.30f, 0.06f),
            "COMPLETE",
            CompleteManualRun,
            out manualCompleteLabel);
        manualRecenterButton = CreateButton(
            "Recenter",
            new Vector3(0f, -0.29f, -0.02f),
            new Vector2(0.60f, 0.06f),
            "RECENTER",
            RecenterStudyState,
            out _);
        manualCloseButton = CreateButton(
            "Close Panel",
            new Vector3(0f, -0.40f, -0.02f),
            new Vector2(0.60f, 0.06f),
            "CLOSE",
            ClosePanel,
            out _);
        TextMeshPro grabHint = CreateText(
            panelRoot.transform,
            "Panel Grab Hint",
            new Vector3(0f, -0.487f, -0.014f),
            new Vector2(0.72f, 0.022f),
            0.0095f,
            MutedTextColor,
            TextAlignmentOptions.Center,
            FontStyles.Normal);
        grabHint.text = "PINCH THE PANEL BACKGROUND TO MOVE IT";
        SetPalette(
            SessionButtonColor,
            SessionHoverColor,
            manualModeAButton,
            manualModeBButton,
            manualStartButton,
            manualCompleteButton);
        manualPreviousRouteButton.SetPalette(AdhocButtonColor, AdhocHoverColor, SelectedColor);
        manualNextRouteButton.SetPalette(AdhocButtonColor, AdhocHoverColor, SelectedColor);
        manualRecenterButton.SetPalette(UtilityButtonColor, UtilityHoverColor, SelectedColor);
        manualCloseButton.SetPalette(UtilityButtonColor, UtilityHoverColor, SelectedColor);
        manualCompleteButton.SetDanger(true);

        BuildPointer("Left Panel Pointer", out leftPointerRoot, out leftPointerLine, out leftPointerReticle);
        BuildPointer("Right Panel Pointer", out rightPointerRoot, out rightPointerLine, out rightPointerReticle);
        PositionPanelInFrontOfUser();
        RefreshPanelText();
    }

    private void ApplyPanelSurfaceTint(Color color)
    {
        if (panelBackgroundRenderer == null || panelSurfaceTint == color)
        {
            return;
        }

        panelSurfaceTint = color;
        SetRendererColor(panelBackgroundRenderer, color);
    }

    private void BuildPointer(
        string name,
        out GameObject root,
        out LineRenderer line,
        out Renderer reticleRenderer)
    {
        root = new GameObject(name);
        root.layer = uiLayer;
        root.transform.SetParent(panelParent, false);

        line = root.AddComponent<LineRenderer>();
        line.sharedMaterial = pointerMaterial;
        line.useWorldSpace = true;
        line.positionCount = 2;
        line.startWidth = 0.004f;
        line.endWidth = 0.002f;
        line.numCapVertices = 4;
        line.alignment = LineAlignment.View;

        GameObject reticle = GameObject.CreatePrimitive(PrimitiveType.Sphere);
        reticle.name = "Pointer Reticle";
        reticle.layer = uiLayer;
        reticle.transform.SetParent(root.transform, false);
        reticle.transform.localScale = Vector3.one * 0.014f;
        DestroyUnityObject(reticle.GetComponent<Collider>());
        reticleRenderer = reticle.GetComponent<Renderer>();
        reticleRenderer.sharedMaterial = pointerMaterial;
        reticle.SetActive(false);
        root.SetActive(false);
    }

    private void CreateSectionLabel(string textValue, float y)
    {
        TextMeshPro label = CreateText(
            panelRoot.transform,
            textValue + " Section",
            new Vector3(-0.255f, y, -0.014f),
            new Vector2(0.22f, 0.026f),
            0.011f,
            AccentColor,
            TextAlignmentOptions.Left,
            FontStyles.Bold);
        label.text = textValue;
    }

    private StudyPanelButton CreateButton(
        string objectName,
        Vector3 localPosition,
        Vector2 size,
        string label,
        Action pressed,
        out TextMeshPro labelText)
    {
        GameObject buttonObject = GameObject.CreatePrimitive(PrimitiveType.Cube);
        buttonObject.name = objectName;
        buttonObject.layer = uiLayer;
        buttonObject.transform.SetParent(panelRoot.transform, false);
        buttonObject.transform.localPosition = localPosition;
        buttonObject.transform.localScale = new Vector3(size.x, size.y, 0.018f);
        buttonObject.GetComponent<MeshRenderer>().sharedMaterial = buttonMaterial;
        StudyPanelButton button = buttonObject.AddComponent<StudyPanelButton>();
        button.ConfigureSurface(size);
        button.Pressed = pressed;

        labelText = CreateText(
            panelRoot.transform,
            objectName + " Label",
            localPosition + new Vector3(0f, 0f, -0.0105f),
            new Vector2(size.x * 0.9f, size.y * 0.82f),
            0.0135f,
            Color.white,
            TextAlignmentOptions.Center,
            FontStyles.Bold);
        labelText.text = label;
        return button;
    }

    private TextMeshPro CreateText(
        Transform parent,
        string objectName,
        Vector3 localPosition,
        Vector2 worldSize,
        float worldFontSize,
        Color color,
        TextAlignmentOptions alignment,
        FontStyles style)
    {
        GameObject textObject = new(objectName);
        textObject.layer = uiLayer;
        textObject.transform.SetParent(parent, false);
        textObject.transform.localPosition = localPosition;
        textObject.transform.localRotation = Quaternion.identity;
        textObject.transform.localScale = Vector3.one * TextTransformScale;
        TextMeshPro text = textObject.AddComponent<TextMeshPro>();
        text.font = fontAsset;
        text.fontSharedMaterial = textMaterial;
        text.rectTransform.sizeDelta = worldSize / TextTransformScale;
        text.alignment = alignment;
        text.fontSize = worldFontSize / TextTransformScale * 10f;
        text.fontStyle = style;
        text.color = color;
        text.textWrappingMode = TextWrappingModes.NoWrap;
        text.overflowMode = TextOverflowModes.Overflow;
        text.raycastTarget = false;
        text.sortingOrder = 100;
        return text;
    }

    private static void SetPalette(
        Color enabled,
        Color hovered,
        params StudyPanelButton[] buttons)
    {
        foreach (StudyPanelButton button in buttons)
        {
            button?.SetPalette(enabled, hovered, SelectedColor);
        }
    }

    private static Material CreateOverlayMaterial(Color color)
    {
        UnityEngine.Shader shader = UnityEngine.Shader.Find("Oculus/Unlit Transparent Color") ??
                                     UnityEngine.Shader.Find("Interaction/UnlitTransparentColor");
        if (shader == null)
        {
            throw new InvalidOperationException("No always-visible unlit shader is available for the study panel.");
        }

        Material material = new(shader);
        material.SetColor("_Color", color);
        return material;
    }

    private static Material CreateButtonMaterial()
    {
        UnityEngine.Shader shader = UnityEngine.Shader.Find("Interaction/RoundedBoxUnlit") ??
                                     UnityEngine.Shader.Find("Oculus/Unlit Transparent Color");
        if (shader == null)
        {
            throw new InvalidOperationException("No compatible unlit shader is available for study buttons.");
        }

        Material material = new(shader);
        if (material.HasProperty("_ZTest"))
        {
            material.SetFloat("_ZTest", (float)UnityEngine.Rendering.CompareFunction.Always);
        }
        // Buttons ignore world depth but must still stamp their own, or the hand queued behind them
        // has nothing to depth-test against and paints straight through the console.
        if (material.HasProperty("_ZWrite"))
        {
            material.SetFloat("_ZWrite", 1f);
        }
        if (material.HasProperty("_Color"))
        {
            material.SetColor("_Color", new Color(0.08f, 0.35f, 0.52f, 1f));
        }
        return material;
    }

    // Mode selection is inert while the estimation cycle is showing - it only arms which mode the
    // next START enters - so it stays available there and is refused only during a run or practice.
    private void SelectManualMode(int modeIndex)
    {
        CancelConfirmation();
        if (state.blockRunning || state.practiceActive)
        {
            return;
        }
        if (modeIndex < 0 || modeIndex >= StudySessionState.RuntimeConditions.Length)
        {
            throw new ArgumentOutOfRangeException(nameof(modeIndex));
        }

        state.adhocConditionIndex = modeIndex;
        state.statusMessage = "Mode " + (modeIndex == 0 ? "A" : "B") + " selected.";
        RefreshPanelText();
    }

    private void ChangeManualRoute(int offset)
    {
        CancelConfirmation();
        if (state.blockRunning || state.IsAuxiliaryActive)
        {
            return;
        }

        List<string> routes = GetStudyRoutes();
        if (routes.Count == 0)
        {
            state.statusMessage = "No routes are available.";
            RefreshPanelText();
            return;
        }

        state.adhocRouteIndex = (state.adhocRouteIndex + offset + routes.Count) % routes.Count;
        state.statusMessage = "Selected " + StudyRouteIdentity.FormatBlindLabel(
            routes[state.adhocRouteIndex],
            state.adhocRouteIndex,
            routes.Count) + ".";
        RefreshPanelText();
    }

    private List<string> GetStudyRoutes()
    {
        return sceneConfiguror != null ? sceneConfiguror.GetStudyRouteNames() : new List<string>();
    }

    private void StartManualRun()
    {
        CancelConfirmation();
        // A rehearsal run takes over the board, so starting one closes the estimation cycle instead
        // of being refused by it: the experimenter would otherwise have to end the cycle blind.
        EndEstimationIfShowing();
        blockRun.StartManualRun();
    }

    private void EndEstimationIfShowing()
    {
        if (state.estimationActive)
        {
            estimation.EndEstimation();
        }
    }

    private void CompleteManualRun()
    {
        RequireConfirmation(
            ConfirmCompleteBlock,
            "Press COMPLETE again to confirm manual completion.",
            blockRun.CompleteBlock);
    }

    private void RecenterStudyState()
    {
        CancelConfirmation();
        // Recenter is the console's way out of any state, so it closes an open estimation
        // recording rather than leaving the cycle active with no board content behind it.
        EndEstimationIfShowing();
        if (sceneConfiguror == null)
        {
            throw new InvalidOperationException("The study environment is unavailable.");
        }

        // The environment re-seats to the participant's current standing pose before the study
        // state resets on top of it, so ghosts, mode re-entry, and the panel all land in the
        // recentered frame.
        string recenterError = "Recentring is unavailable.";
        bool recentered = boardAlignment != null &&
                          boardAlignment.TryRecenterToParticipant(
                              userCamera != null ? userCamera.transform : null,
                              out recenterError);
        if (recentered)
        {
            BoardAlignmentSnapshot pose = boardAlignment.GetSnapshot();
            sceneConfiguror.actionRecorder?.Record(
                "ViewRecenter",
                "",
                null,
                "source=console" +
                ";boardX=" + pose.position.x.ToString("F3", CultureInfo.InvariantCulture) +
                ";boardY=" + pose.position.y.ToString("F3", CultureInfo.InvariantCulture) +
                ";boardZ=" + pose.position.z.ToString("F3", CultureInfo.InvariantCulture) +
                ";boardYawDeg=" + pose.rotation.eulerAngles.y.ToString("F1", CultureInfo.InvariantCulture));
        }
        sceneConfiguror.actionRecorder?.Record(
            "EnvironmentReset",
            "",
            null,
            "reason=manual_reset");
        sceneConfiguror.ResetManualStudyState();
        CancelPanelGrab();
        ResetPointerAndHover();
        PositionPanelInFrontOfUser();
        state.panelPinned = true;
        panelPressableAt = Time.unscaledTime + Mathf.Max(0f, panelSettleSeconds());
        leftWasPinching = false;
        rightWasPinching = false;
        leftPinchArmed = false;
        rightPinchArmed = false;
        if (!state.manualRunRecoveryBlocked)
        {
            state.statusMessage = recentered
                ? "View recentered; board, room, grip, ghost, and panel reset."
                : recenterError + " Board, room, grip, ghost, and panel reset.";
        }
        RefreshPanelText();
    }

    /// <summary>
    /// Hides the panel without touching the run, the recording, or the study state. The palm-up
    /// close gesture is refused while the left ray targets the panel, so a participant looking at
    /// an accidentally summoned console has no gesture way out; this control therefore stays
    /// pressable in every console state, including mid-run and while recovery is blocked.
    /// </summary>
    private void ClosePanel()
    {
        SetPanelVisible(false);
    }

    private void PreviousParticipant()
    {
        CancelConfirmation();
        if (state.participants.Count == 0 || state.blockRunning || state.IsAuxiliaryActive)
        {
            return;
        }
        state.participantIndex = (state.participantIndex - 1 + state.participants.Count) % state.participants.Count;
        estimation.TryRecoverSelectedCompletedBlock();
        RefreshPanelText();
    }

    private void NextParticipant()
    {
        CancelConfirmation();
        if (state.participants.Count == 0 || state.blockRunning || state.IsAuxiliaryActive)
        {
            return;
        }
        state.participantIndex = (state.participantIndex + 1) % state.participants.Count;
        estimation.TryRecoverSelectedCompletedBlock();
        RefreshPanelText();
    }

    private void PreviousBlock()
    {
        CancelConfirmation();
        if (!state.blockRunning && !state.IsAuxiliaryActive)
        {
            state.selectedBlock = state.selectedBlock == 1 ? 3 : state.selectedBlock - 1;
            estimation.TryRecoverSelectedCompletedBlock();
            RefreshPanelText();
        }
    }

    private void NextBlock()
    {
        CancelConfirmation();
        if (!state.blockRunning && !state.IsAuxiliaryActive)
        {
            state.selectedBlock = state.selectedBlock == 3 ? 1 : state.selectedBlock + 1;
            estimation.TryRecoverSelectedCompletedBlock();
            RefreshPanelText();
        }
    }

    private void HandleBlockAction()
    {
        if (state.blockRunning)
        {
            RequireConfirmation(
                ConfirmCompleteBlock,
                "Press COMPLETE BLOCK again to confirm manual completion.",
                blockRun.CompleteBlock);
            return;
        }

        RequireConfirmation(
            ConfirmStartBlock,
            "Press START BLOCK again to confirm the selected participant and block.",
            () => blockRun.StartSelectedBlock());
    }

    private void HandlePracticeB()
    {
        if (state.practiceActive)
        {
            CancelConfirmation();
            practice.SetPracticePhase("B");
            return;
        }

        RequireConfirmation(
            ConfirmStartPractice,
            "Press PRACTICE B again to begin unlimited practice.",
            () => practice.StartPractice());
    }

    private void HandlePracticeC()
    {
        CancelConfirmation();
        practice.SetPracticePhase("C");
    }

    private void HandleEndPractice()
    {
        RequireConfirmation(
            ConfirmEndPractice,
            "Press END PRACTICE again to confirm.",
            practice.EndPractice);
    }

    private void CycleAdhocCondition()
    {
        CancelConfirmation();
        if (state.blockRunning || state.IsAuxiliaryActive)
        {
            return;
        }
        state.adhocConditionIndex = (state.adhocConditionIndex + 1) % StudySessionState.RuntimeConditions.Length;
        RefreshPanelText();
    }

    private void CycleAdhocRoute()
    {
        CancelConfirmation();
        if (state.blockRunning || state.IsAuxiliaryActive)
        {
            return;
        }
        int routeCount = sceneConfiguror != null ? sceneConfiguror.GetStudyRouteNames().Count : 0;
        if (routeCount == 0)
        {
            return;
        }
        state.adhocRouteIndex = (state.adhocRouteIndex + 1) % routeCount;
        RefreshPanelText();
    }

    private void HandleAdhocStart()
    {
        RequireConfirmation(
            ConfirmStartAdhoc,
            "Press START AD HOC again to confirm.",
            () => blockRun.StartManualRun());
    }

    private void RecenterPanel()
    {
        CancelPanelGrab();
        CancelConfirmation();
        PositionPanelInFrontOfUser();
        state.panelPinned = true;
        state.statusMessage = "Panel recentered.";
        RefreshPanelText();
    }

    public void RefreshPanelText()
    {
        RefreshButtonStates();
        if (panelText == null)
        {
            return;
        }

        StringBuilder text = new();
        if (state.blockRunning && state.activeRow != null)
        {
            text.Append("RUNNING MODE ")
                .Append(state.activeRow.condition == "B" ? "A" : "B")
                .AppendLine();
        }
        else if (state.estimationActive)
        {
            text.Append("ESTIMATING").AppendLine();
        }
        else
        {
            text.Append("READY  |  MODE ")
                .Append(state.adhocConditionIndex == 0 ? "A" : "B")
                .AppendLine();
        }

        lastRoutesStatusLine = GetStudyRouteStatusLine();
        if (!lastRoutesStatusLine.StartsWith("READY", StringComparison.Ordinal))
        {
            text.Append("ROUTES: ").Append(Truncate(lastRoutesStatusLine, 56)).AppendLine();
        }
        text.Append("STATUS: ").Append(Truncate(state.statusMessage, 58));
        panelText.text = text.ToString();
    }

    private void RefreshButtonStates()
    {
        bool idle = !state.blockRunning && !state.IsAuxiliaryActive &&
                    !state.manualRunRecoveryBlocked;
        List<string> routes = GetStudyRoutes();
        bool hasRoute = routes.Count > 0;
        state.adhocRouteIndex = hasRoute
            ? Mathf.Clamp(state.adhocRouteIndex, 0, routes.Count - 1)
            : 0;

        bool modeSelectable = !state.blockRunning && !state.practiceActive &&
                              !state.manualRunRecoveryBlocked;

        manualModeAButton?.SetInteractable(modeSelectable);
        manualModeBButton?.SetInteractable(modeSelectable);
        manualModeAButton?.SetSelected(state.adhocConditionIndex == 0);
        manualModeBButton?.SetSelected(state.adhocConditionIndex == 1);
        manualPreviousRouteButton?.SetInteractable(idle && hasRoute);
        manualNextRouteButton?.SetInteractable(idle && hasRoute);
        manualStartButton?.SetInteractable(modeSelectable && hasRoute);
        manualCompleteButton?.SetInteractable(state.blockRunning);
        manualRecenterButton?.SetInteractable(sceneConfiguror != null);
        manualCloseButton?.SetInteractable(true);
        if (manualStartLabel != null)
        {
            manualStartLabel.text = "START";
        }
        if (manualCompleteLabel != null)
        {
            manualCompleteLabel.text = "COMPLETE";
        }
        if (manualPreviousRouteLabel != null)
        {
            manualPreviousRouteLabel.text = StudyRouteIdentity.FormatStepLabel(
                routes,
                state.adhocRouteIndex,
                -1);
        }
        if (manualNextRouteLabel != null)
        {
            manualNextRouteLabel.text = StudyRouteIdentity.FormatStepLabel(
                routes,
                state.adhocRouteIndex,
                1);
        }
        RefreshRouteReadout(routes);
    }

    /// <summary>
    /// Route feedback for the experimenter. The readout carries the slot and the derived code
    /// only: the console has no way to display the MoonBoard record, so nothing it renders can
    /// identify the climb to a participant looking at the panel. While the estimation cycle is
    /// showing, the readout follows the board and names the problem by its code alone.
    /// </summary>
    private void RefreshRouteReadout(List<string> routes)
    {
        if (routeReadoutText == null)
        {
            return;
        }
        if (state.estimationActive && estimation != null)
        {
            routeReadoutText.text = estimation.GetProgressReadout();
            routeReadoutText.color = SelectedColor;
            return;
        }

        bool running = state.blockRunning && state.activeRow != null;
        int activeIndex = running ? routes.IndexOf(state.activeRow.route) : -1;
        int routeIndex = activeIndex >= 0 ? activeIndex : state.adhocRouteIndex;
        string routeId = routes.Count > 0
            ? routes[Mathf.Clamp(routeIndex, 0, routes.Count - 1)]
            : string.Empty;
        string blindLabel = StudyRouteIdentity.FormatBlindLabel(routeId, routeIndex, routes.Count);
        routeReadoutText.text = running ? "RUNNING     " + blindLabel : blindLabel;
        routeReadoutText.color = running ? SelectedColor : AccentColor;
    }

    public void RefreshStatusLinesIfChanged()
    {
        string routesStatusLine = GetStudyRouteStatusLine();
        if (routesStatusLine != lastRoutesStatusLine && panelRoot != null && panelRoot.activeSelf)
        {
            RefreshPanelText();
        }
        if (boardAlignment != null && boardAlignment.StatusMessage != lastBoardAlignmentStatus)
        {
            lastBoardAlignmentStatus = boardAlignment.StatusMessage;
            RefreshPanelText();
        }
    }

    public void UpdatePracticeElapsedText(float elapsedSeconds)
    {
        int elapsedSecond = Mathf.FloorToInt(elapsedSeconds);
        if (elapsedSecond == lastPracticeElapsedSecond)
        {
            return;
        }

        lastPracticeElapsedSecond = elapsedSecond;
        if (panelRoot != null && panelRoot.activeSelf)
        {
            RefreshPanelText();
        }
    }

    public void ShowPanel()
    {
        CancelPanelGrab();
        CancelConfirmation();
        PositionPanelInFrontOfUser();
        state.panelPinned = true;
        SetPanelVisible(true);
        panelPressableAt = Time.unscaledTime + Mathf.Max(0f, panelSettleSeconds());
        leftWasPinching = false;
        rightWasPinching = false;
        leftPinchArmed = false;
        rightPinchArmed = false;
        RefreshPanelText();
    }

    public void TogglePanel()
    {
        if (!IsPanelBuilt)
        {
            return;
        }

        if (IsPanelHidden)
        {
            ShowPanel();
        }
        else
        {
            SetPanelVisible(false);
        }
    }

    public void UpdateIdlePanelPosition()
    {
        if (!state.panelPinned && !state.blockRunning && panelRoot != null && panelRoot.activeSelf)
        {
            PositionPanelInFrontOfUser();
        }
    }

    private void PositionPanelInFrontOfUser()
    {
        if (panelRoot == null || userCamera == null)
        {
            return;
        }

        Transform cameraTransform = userCamera.transform;
        panelRoot.transform.position = cameraTransform.position + cameraTransform.forward * PanelDistanceMeters;
        panelRoot.transform.rotation = Quaternion.LookRotation(
            panelRoot.transform.position - cameraTransform.position,
            cameraTransform.up);
    }

    public void SetPanelVisible(bool visible)
    {
        panelRoot?.SetActive(visible);
        if (panelRoot != null && lastRecordedPanelVisible != visible)
        {
            lastRecordedPanelVisible = visible;
            sceneConfiguror?.actionRecorder?.Record(
                "PanelVisibility",
                "",
                null,
                "visible=" + (visible ? "true" : "false"));
        }
        SetGameplayInputSuppressed(visible);
        if (!visible)
        {
            CancelPanelGrab();
            CancelConfirmation();
            ResetPointerAndHover();
            summonGesture?.ResetSummonDwell();
        }
    }

    public void SetGameplayInputSuppressed(bool suppressed)
    {
        sceneConfiguror?.SetPanelInputSuppressed(suppressed);
    }

    public void SetSummonArmed(bool armed)
    {
        sceneConfiguror?.SetPanelSummonArmed(armed);
    }

    /// <summary>One PanelSummon row per gesture milestone (armed / opened / blocked), so
    /// accidental console opens are excisable by timestamp instead of inferred from grip
    /// flags, and the gate's residual false-positive rate is countable per session.</summary>
    public void RecordSummonEvent(string details)
    {
        sceneConfiguror?.actionRecorder?.Record("PanelSummon", "Left", null, details);
    }

    public void HandlePanelInput(
        OVRHand leftHand,
        OVRSkeleton leftSkeleton,
        OVRHand rightHand,
        OVRSkeleton rightSkeleton)
    {
        ExpireConfirmationIfNeeded();
        PanelPointerTarget leftTarget = UpdatePointer(
            leftHand,
            true,
            leftPointerRoot,
            leftPointerLine,
            leftPointerReticle);
        PanelPointerTarget rightTarget = UpdatePointer(
            rightHand,
            false,
            rightPointerRoot,
            rightPointerLine,
            rightPointerReticle);
        UpdateHoveredButtons(leftTarget.button, rightTarget.button);
        ApplyPanelSurfaceTint(activePanelGrabHand != PanelGrabHand.None
            ? PanelGrabbedColor
            : leftTarget.isPanelSurface || rightTarget.isPanelSurface
                ? PanelHoverColor
                : PanelColor);

        HandleHandInput(
            leftHand,
            leftSkeleton,
            ref leftWasPinching,
            ref leftPinchArmed,
            true,
            leftTarget);
        HandleHandInput(
            rightHand,
            rightSkeleton,
            ref rightWasPinching,
            ref rightPinchArmed,
            false,
            rightTarget);
    }

    private void HandleHandInput(
        OVRHand hand,
        OVRSkeleton skeleton,
        ref bool wasPinching,
        ref bool pinchArmed,
        bool isLeft,
        PanelPointerTarget target)
    {
        bool trackingConfident = hand != null && hand.IsTracked && hand.IsDataHighConfidence;
        bool pinching = trackingConfident && hand.GetFingerIsPinching(OVRHand.HandFinger.Index);
        bool pinchStarted = StudyRehearsalTiming.TryConsumeArmedPinch(
            trackingConfident,
            pinching,
            ref wasPinching,
            ref pinchArmed);
        PanelGrabHand handSide = isLeft ? PanelGrabHand.Left : PanelGrabHand.Right;
        if (activePanelGrabHand != PanelGrabHand.None)
        {
            if (activePanelGrabHand == handSide)
            {
                bool pointerValid = trackingConfident && hand.IsPointerPoseValid && hand.PointerPose != null;
                if (!pinching || !pointerValid)
                {
                    EndPanelGrab(ref pinchArmed);
                }
                else
                {
                    UpdatePanelGrab(hand);
                }
            }
            return;
        }

        bool panelTargeted = target.isPanelSurface || target.button != null;
        bool summonConsumed = summonGesture != null && summonGesture.UpdateSummonGesture(
            hand,
            skeleton,
            pinching,
            pinchStarted,
            isLeft,
            panelTargeted);
        if (!pinchStarted || summonConsumed ||
            Time.unscaledTime < panelPressableAt ||
            lastPanelPressFrame == Time.frameCount)
        {
            return;
        }

        StudyPanelButton recentButton = isLeft ? leftRecentHoverButton : rightRecentHoverButton;
        bool hasRecentHover = recentButton != null && recentButton.gameObject.activeInHierarchy;
        float secondsSinceHover = hasRecentHover
            ? Mathf.Max(0f, Time.unscaledTime - (isLeft ? leftRecentHoverAt : rightRecentHoverAt))
            : float.MaxValue;
        StudyRehearsalTiming.PanelPressResolution resolution = StudyRehearsalTiming.ResolvePanelPress(
            target.button != null,
            target.isPanelSurface,
            hasRecentHover,
            secondsSinceHover,
            PointerHoverGraceSeconds);
        if (resolution == StudyRehearsalTiming.PanelPressResolution.GrabPanel)
        {
            BeginPanelGrab(hand, handSide, target);
            return;
        }

        StudyPanelButton button =
            resolution == StudyRehearsalTiming.PanelPressResolution.PressTargetButton
                ? target.button
                : resolution == StudyRehearsalTiming.PanelPressResolution.PressRecentButton
                    ? recentButton
                    : null;
        if (button == null || !button.gameObject.activeInHierarchy)
        {
            return;
        }

        if (button.Press())
        {
            lastPanelPressFrame = Time.frameCount;
            state.panelPinned = true;
            ClearRecentHover(isLeft);
        }
    }

    private void ResetPointerAiming(bool isLeft)
    {
        if (isLeft)
        {
            leftPointerSmoothedForward = Vector3.zero;
        }
        else
        {
            rightPointerSmoothedForward = Vector3.zero;
        }
        ClearRecentHover(isLeft);
    }

    private void ClearRecentHover(bool isLeft)
    {
        if (isLeft)
        {
            leftRecentHoverButton = null;
            leftRecentHoverAt = float.NegativeInfinity;
        }
        else
        {
            rightRecentHoverButton = null;
            rightRecentHoverAt = float.NegativeInfinity;
        }
    }

    private PanelPointerTarget UpdatePointer(
        OVRHand hand,
        bool isLeft,
        GameObject pointerRoot,
        LineRenderer line,
        Renderer reticle)
    {
        bool uiVisible = panelRoot != null && panelRoot.activeSelf;
        bool pointerValid = uiVisible && hand != null && hand.IsTracked && hand.IsDataHighConfidence &&
                            hand.IsPointerPoseValid && hand.PointerPose != null;
        if (!pointerValid)
        {
            pointerRoot?.SetActive(false);
            ResetPointerAiming(isLeft);
            return default;
        }

        Vector3 origin = hand.PointerPose.position;
        Vector3 rawDirection = hand.PointerPose.forward;
        // Dragging wants the direction to follow the hand, so an active grab bypasses the pinch
        // damping that would otherwise make the panel lag behind the drag.
        bool grabbing = activePanelGrabHand == (isLeft ? PanelGrabHand.Left : PanelGrabHand.Right);
        Vector3 direction = grabbing
            ? rawDirection.normalized
            : StudyRehearsalTiming.SmoothPointerDirection(
                isLeft ? leftPointerSmoothedForward : rightPointerSmoothedForward,
                rawDirection,
                Time.unscaledDeltaTime,
                hand.GetFingerPinchStrength(OVRHand.HandFinger.Index),
                PointerRelaxedHalfLifeSeconds,
                PointerPinchedHalfLifeSeconds);
        if (isLeft)
        {
            leftPointerSmoothedForward = direction;
        }
        else
        {
            rightPointerSmoothedForward = direction;
        }

        bool hitUi = Physics.Raycast(
            origin,
            direction,
            out RaycastHit hit,
            5f,
            uiLayerMask,
            QueryTriggerInteraction.Ignore);
        PanelPointerTarget target = default;
        if (hitUi)
        {
            target.isPanelSurface = hit.collider == panelGrabSurfaceCollider;
            target.button = target.isPanelSurface
                ? null
                : hit.collider.GetComponentInParent<StudyPanelButton>();
            target.hitPoint = hit.point;
            target.hitDistance = hit.distance;
        }
        if (target.button != null && target.button.Interactable)
        {
            if (isLeft)
            {
                leftRecentHoverButton = target.button;
                leftRecentHoverAt = Time.unscaledTime;
            }
            else
            {
                rightRecentHoverButton = target.button;
                rightRecentHoverAt = Time.unscaledTime;
            }
        }
        Vector3 end = hitUi ? hit.point : origin + direction * PointerLengthMeters;
        Vector3 lineStart = origin + direction * Mathf.Min(
            PointerProximalGapMeters,
            Vector3.Distance(origin, end) * 0.5f);

        pointerRoot.SetActive(true);
        line.SetPosition(0, lineStart);
        line.SetPosition(1, end);
        reticle.gameObject.SetActive(hitUi);
        if (hitUi)
        {
            reticle.transform.position = hit.point - direction * 0.003f;
        }

        Color color = target.isPanelSurface || target.button != null && target.button.Interactable
            ? PointerHoverColor
            : hitUi ? PointerColor : PointerMissColor;
        SetRendererColor(line, color);
        SetRendererColor(reticle, color);
        return target;
    }

    private void BeginPanelGrab(OVRHand hand, PanelGrabHand handSide, PanelPointerTarget target)
    {
        if (hand == null || hand.PointerPose == null || panelRoot == null)
        {
            return;
        }

        Vector3 origin = hand.PointerPose.position;
        Vector3 direction = hand.PointerPose.forward;
        panelGrabRayDistance = Mathf.Clamp(target.hitDistance, 0.25f, 2f);
        Vector3 rayPoint = origin + direction.normalized * panelGrabRayDistance;
        panelGrabWorldOffset = panelRoot.transform.position - rayPoint;
        activePanelGrabHand = handSide;
        state.panelPinned = true;
        CancelConfirmation();
        ApplyPanelSurfaceTint(PanelGrabbedColor);
    }

    private void UpdatePanelGrab(OVRHand hand)
    {
        if (hand == null || hand.PointerPose == null || panelRoot == null)
        {
            return;
        }

        Vector3 candidatePosition = StudyRehearsalTiming.ResolvePanelDragPosition(
            hand.PointerPose.position,
            hand.PointerPose.forward,
            panelGrabRayDistance,
            panelGrabWorldOffset);
        panelRoot.transform.position = ClampPanelPositionToViewport(candidatePosition);
        if (userCamera != null)
        {
            panelRoot.transform.rotation = GetPanelFacingRotation(panelRoot.transform.position);
        }
    }

    private Vector3 ClampPanelPositionToViewport(Vector3 position)
    {
        if (userCamera == null)
        {
            return position;
        }

        Vector3 clampedViewportPosition = StudyRehearsalTiming.ClampPanelViewportPosition(
            userCamera.WorldToViewportPoint(position),
            Vector2.zero,
            PanelViewportMargin,
            MinimumPanelDepthMeters,
            MaximumPanelDepthMeters);
        float safeDepth = FindSafeCenteredPanelDepth(clampedViewportPosition.z);
        Vector3 centeredViewportPosition = new(0.5f, 0.5f, safeDepth);
        Vector3 centeredPosition = userCamera.ViewportToWorldPoint(centeredViewportPosition);
        clampedViewportPosition.z = safeDepth;
        Vector3 candidatePosition = userCamera.ViewportToWorldPoint(clampedViewportPosition);
        if (IsPanelPoseInsideViewport(candidatePosition))
        {
            return candidatePosition;
        }

        Vector3 bestPosition = centeredPosition;
        float safeFraction = 0f;
        float unsafeFraction = 1f;
        for (int i = 0; i < PanelClampSearchIterations; i++)
        {
            float fraction = (safeFraction + unsafeFraction) * 0.5f;
            Vector3 searchedViewportPosition = Vector3.Lerp(
                centeredViewportPosition,
                clampedViewportPosition,
                fraction);
            Vector3 searchedPosition = userCamera.ViewportToWorldPoint(searchedViewportPosition);
            if (IsPanelPoseInsideViewport(searchedPosition))
            {
                safeFraction = fraction;
                bestPosition = searchedPosition;
            }
            else
            {
                unsafeFraction = fraction;
            }
        }
        return bestPosition;
    }

    private float FindSafeCenteredPanelDepth(float requestedDepth)
    {
        Vector3 centeredViewportPosition = new(0.5f, 0.5f, requestedDepth);
        if (IsPanelPoseInsideViewport(userCamera.ViewportToWorldPoint(centeredViewportPosition)))
        {
            return requestedDepth;
        }

        centeredViewportPosition.z = MaximumPanelDepthMeters;
        if (!IsPanelPoseInsideViewport(userCamera.ViewportToWorldPoint(centeredViewportPosition)))
        {
            return MaximumPanelDepthMeters;
        }

        float unsafeDepth = requestedDepth;
        float safeDepth = MaximumPanelDepthMeters;
        for (int i = 0; i < PanelClampSearchIterations; i++)
        {
            float depth = (unsafeDepth + safeDepth) * 0.5f;
            centeredViewportPosition.z = depth;
            if (IsPanelPoseInsideViewport(userCamera.ViewportToWorldPoint(centeredViewportPosition)))
            {
                safeDepth = depth;
            }
            else
            {
                unsafeDepth = depth;
            }
        }
        return safeDepth;
    }

    private bool IsPanelPoseInsideViewport(Vector3 position)
    {
        GetPanelViewportBounds(
            position,
            GetPanelFacingRotation(position),
            out float minimumX,
            out float maximumX,
            out float minimumY,
            out float maximumY);
        return minimumX >= PanelViewportMargin && maximumX <= 1f - PanelViewportMargin &&
               minimumY >= PanelViewportMargin && maximumY <= 1f - PanelViewportMargin;
    }

    private Quaternion GetPanelFacingRotation(Vector3 position)
    {
        Vector3 awayFromUser = position - userCamera.transform.position;
        return Quaternion.LookRotation(awayFromUser, userCamera.transform.up);
    }

    private void GetPanelViewportBounds(
        Vector3 position,
        Quaternion rotation,
        out float minimumX,
        out float maximumX,
        out float minimumY,
        out float maximumY)
    {
        minimumX = float.PositiveInfinity;
        maximumX = float.NegativeInfinity;
        minimumY = float.PositiveInfinity;
        maximumY = float.NegativeInfinity;
        if (userCamera.stereoEnabled)
        {
            AccumulatePanelViewportBounds(
                position,
                rotation,
                Camera.MonoOrStereoscopicEye.Left,
                ref minimumX,
                ref maximumX,
                ref minimumY,
                ref maximumY);
            AccumulatePanelViewportBounds(
                position,
                rotation,
                Camera.MonoOrStereoscopicEye.Right,
                ref minimumX,
                ref maximumX,
                ref minimumY,
                ref maximumY);
            return;
        }

        AccumulatePanelViewportBounds(
            position,
            rotation,
            Camera.MonoOrStereoscopicEye.Mono,
            ref minimumX,
            ref maximumX,
            ref minimumY,
            ref maximumY);
    }

    private void AccumulatePanelViewportBounds(
        Vector3 position,
        Quaternion rotation,
        Camera.MonoOrStereoscopicEye eye,
        ref float minimumX,
        ref float maximumX,
        ref float minimumY,
        ref float maximumY)
    {
        AccumulatePanelViewportCorner(
            position,
            rotation,
            -PanelWidthMeters * 0.5f,
            PanelBottomMeters,
            eye,
            ref minimumX,
            ref maximumX,
            ref minimumY,
            ref maximumY);
        AccumulatePanelViewportCorner(
            position,
            rotation,
            PanelWidthMeters * 0.5f,
            PanelBottomMeters,
            eye,
            ref minimumX,
            ref maximumX,
            ref minimumY,
            ref maximumY);
        AccumulatePanelViewportCorner(
            position,
            rotation,
            -PanelWidthMeters * 0.5f,
            PanelTopMeters,
            eye,
            ref minimumX,
            ref maximumX,
            ref minimumY,
            ref maximumY);
        AccumulatePanelViewportCorner(
            position,
            rotation,
            PanelWidthMeters * 0.5f,
            PanelTopMeters,
            eye,
            ref minimumX,
            ref maximumX,
            ref minimumY,
            ref maximumY);
    }

    private void AccumulatePanelViewportCorner(
        Vector3 position,
        Quaternion rotation,
        float localX,
        float localY,
        Camera.MonoOrStereoscopicEye eye,
        ref float minimumX,
        ref float maximumX,
        ref float minimumY,
        ref float maximumY)
    {
        Vector3 worldCorner = position + rotation * new Vector3(localX, localY, 0f);
        Vector3 viewportCorner = userCamera.WorldToViewportPoint(worldCorner, eye);
        minimumX = Mathf.Min(minimumX, viewportCorner.x);
        maximumX = Mathf.Max(maximumX, viewportCorner.x);
        minimumY = Mathf.Min(minimumY, viewportCorner.y);
        maximumY = Mathf.Max(maximumY, viewportCorner.y);
    }

    private void EndPanelGrab(ref bool pinchArmed)
    {
        activePanelGrabHand = PanelGrabHand.None;
        pinchArmed = false;
        panelPressableAt = Time.unscaledTime + PostDragSettleSeconds;
        ApplyPanelSurfaceTint(PanelColor);
        state.statusMessage = "Panel moved.";
        RefreshPanelText();
    }

    private void CancelPanelGrab()
    {
        activePanelGrabHand = PanelGrabHand.None;
        leftPinchArmed = false;
        rightPinchArmed = false;
        ApplyPanelSurfaceTint(PanelColor);
    }

    private void UpdateHoveredButtons(StudyPanelButton leftTarget, StudyPanelButton rightTarget)
    {
        if (leftHoveredButton != null && leftHoveredButton != leftTarget && leftHoveredButton != rightTarget)
        {
            leftHoveredButton.SetHovered(false);
        }
        if (rightHoveredButton != null && rightHoveredButton != leftTarget && rightHoveredButton != rightTarget)
        {
            rightHoveredButton.SetHovered(false);
        }

        leftHoveredButton = leftTarget;
        rightHoveredButton = rightTarget;
        leftHoveredButton?.SetHovered(true);
        rightHoveredButton?.SetHovered(true);
    }

    private void SetRendererColor(Renderer renderer, Color color)
    {
        if (renderer == null)
        {
            return;
        }

        renderer.GetPropertyBlock(pointerProperties);
        pointerProperties.SetColor("_Color", color);
        pointerProperties.SetColor("_BaseColor", color);
        renderer.SetPropertyBlock(pointerProperties);
    }

    private void ResetPointerAndHover()
    {
        leftHoveredButton?.SetHovered(false);
        if (rightHoveredButton != leftHoveredButton)
        {
            rightHoveredButton?.SetHovered(false);
        }
        leftHoveredButton = null;
        rightHoveredButton = null;
        ResetPointerAiming(true);
        ResetPointerAiming(false);
        ApplyPanelSurfaceTint(PanelColor);
        leftPointerRoot?.SetActive(false);
        rightPointerRoot?.SetActive(false);
    }

    private void RequireConfirmation(string key, string prompt, Action confirmedAction)
    {
        bool confirmed = StudyRehearsalTiming.TryConfirmPanelAction(
            key,
            Time.unscaledTime,
            Time.frameCount,
            ConfirmationWindowSeconds,
            ref pendingConfirmationKey,
            ref confirmationDeadline,
            ref confirmationArmedFrame);
        if (confirmed)
        {
            confirmedAction();
            return;
        }

        state.statusMessage = prompt + " Confirmation expires in 4 seconds.";
        RefreshPanelText();
    }

    private void CancelConfirmation()
    {
        pendingConfirmationKey = string.Empty;
        confirmationDeadline = -1f;
        confirmationArmedFrame = -1;
    }

    private void ExpireConfirmationIfNeeded()
    {
        if (string.IsNullOrEmpty(pendingConfirmationKey) || Time.unscaledTime <= confirmationDeadline)
        {
            return;
        }

        CancelConfirmation();
        state.statusMessage = "Confirmation expired; no action was taken.";
        RefreshPanelText();
    }

    private void BeginBoardAlignment()
    {
        CancelConfirmation();
        if (state.blockRunning || state.IsAuxiliaryActive)
        {
            state.statusMessage = "End the current block or auxiliary sequence before aligning the board.";
        }
        else if (boardAlignment == null)
        {
            state.statusMessage = "Board alignment is unavailable.";
        }
        else if (!boardAlignment.BeginCalibration(out string error))
        {
            state.statusMessage = error;
        }
        else
        {
            state.statusMessage = "Board alignment started.";
        }
        RefreshPanelText();
    }

    private void HandleClearAlignment()
    {
        RequireConfirmation(
            ConfirmClearAlignment,
            "Press CLEAR again to remove the saved board alignment.",
            ClearBoardAlignment);
    }

    private void ClearBoardAlignment()
    {
        if (!state.blockRunning && !state.IsAuxiliaryActive && boardAlignment != null)
        {
            if (!boardAlignment.ClearAlignment())
            {
                state.statusMessage = boardAlignment.StatusMessage;
                RefreshPanelText();
                return;
            }
            state.statusMessage = boardAlignment.StatusMessage;
            RefreshPanelText();
        }
    }

    private static string Truncate(string value, int maximumLength)
    {
        if (string.IsNullOrEmpty(value) || value.Length <= maximumLength)
        {
            return value ?? string.Empty;
        }
        return value.Substring(0, maximumLength - 2) + "..";
    }

    private string GetStudyRouteStatusLine()
    {
        int routeCount = sceneConfiguror != null ? sceneConfiguror.GetStudyRouteNames().Count : 0;
        return routeCount > 0
            ? "READY (" + routeCount + " approved)"
            : "UNAVAILABLE";
    }

    private static void DestroyUnityObject(UnityEngine.Object target)
    {
        if (target == null)
        {
            return;
        }
        if (Application.isPlaying)
        {
            UnityEngine.Object.Destroy(target);
        }
        else
        {
            UnityEngine.Object.DestroyImmediate(target);
        }
    }

    public void DestroyMaterials()
    {
        if (panelMaterial != null)
        {
            DestroyUnityObject(panelMaterial);
        }
        if (buttonMaterial != null)
        {
            DestroyUnityObject(buttonMaterial);
        }
        if (pointerMaterial != null)
        {
            DestroyUnityObject(pointerMaterial);
        }
        if (textMaterial != null)
        {
            DestroyUnityObject(textMaterial);
        }
    }
}
