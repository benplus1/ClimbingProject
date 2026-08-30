using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using UnityEngine;

/// <summary>
/// Owns the block lifecycle: validating a schedule row, creating the block directory and
/// manifest, tracking rehearsal elapsed time, and finalizing the manifest on an explicit end.
/// </summary>
public enum ManualRunRecoveryOutcome
{
    None,
    Expired,
    Resumed,
}

public sealed class BlockRunController
{
    private const string NullRoutesHashSentinel = "__NULL_ROUTES_JSON_SHA256__";

    private readonly StudySessionState state;
    private readonly SceneConfiguror sceneConfiguror;
    private readonly ActionRecorder actionRecorder;
    private readonly BoardAlignmentController boardAlignment;
    private readonly StudyControlPanel panel;
    private readonly HeadsetPresenceTracker headsetPresence;
    private readonly EstimationController estimation;
    private readonly Action ensureScheduleLoadedForRuntime;

    private StudySessionManifest activeManifest;
    private string manifestPath;
    private MoonBoardRouteDefinition activeRouteDefinition;
    private double blockStartRealtime;
    private double elapsedBeforeCurrentProcess;
    private int segmentDroppedCaptureFramesBaseline;
    private HoldAggregateData[] segmentHoldAggregateBaseline = Array.Empty<HoldAggregateData>();
    private bool manifestCreated;
    private bool firstInteractionRecorded;
    private bool completionRequested;
    private string retryConfirmationKey;
    private float retryConfirmationDeadline;

    public float ElapsedSeconds => state.blockTimerStarted
        ? StudyRehearsalTiming.ResolveElapsedSeconds(
            elapsedBeforeCurrentProcess,
            blockStartRealtime,
            Time.realtimeSinceStartupAsDouble)
        : 0f;

    public BlockRunController(
        StudySessionState state,
        SceneConfiguror sceneConfiguror,
        ActionRecorder actionRecorder,
        BoardAlignmentController boardAlignment,
        StudyControlPanel panel,
        HeadsetPresenceTracker headsetPresence,
        EstimationController estimation,
        Action ensureScheduleLoadedForRuntime)
    {
        this.state = state;
        this.sceneConfiguror = sceneConfiguror;
        this.actionRecorder = actionRecorder;
        this.boardAlignment = boardAlignment;
        this.panel = panel;
        this.headsetPresence = headsetPresence;
        this.estimation = estimation;
        this.ensureScheduleLoadedForRuntime = ensureScheduleLoadedForRuntime;
    }

    public bool StartSelectedBlock()
    {
        ensureScheduleLoadedForRuntime();
        if (state.participants.Count == 0)
        {
            state.statusMessage = "No valid schedule loaded.";
            panel.RefreshPanelText();
            return false;
        }
        return StartBlock(state.participants[state.participantIndex], state.selectedBlock);
    }

    public bool StartBlock(string participant, int block)
    {
        ensureScheduleLoadedForRuntime();
        if (state.IsAuxiliaryActive)
        {
            state.statusMessage = "End the practice or estimation sequence first.";
            panel.RefreshPanelText();
            return false;
        }
        if (state.blockRunning)
        {
            state.statusMessage = "End the current block first.";
            panel.RefreshPanelText();
            return false;
        }
        StudyScheduleRow row = state.schedule.FirstOrDefault(candidate =>
            candidate.participant == participant && candidate.block == block);
        if (row == null)
        {
            state.statusMessage = $"No schedule row for {participant} block {block}.";
            panel.RefreshPanelText();
            return false;
        }
        if (!TryValidateRowRuntime(row))
        {
            return false;
        }
        if (boardAlignment != null && boardAlignment.IsBusy)
        {
            state.statusMessage = "Wait for board calibration or spatial-anchor loading to finish.";
            panel.RefreshPanelText();
            return false;
        }
        if (!sceneConfiguror.TryGetRouteDefinition(row.route, out MoonBoardRouteDefinition routeDefinition))
        {
            state.statusMessage = StudyRouteIdentity.FormatRouteFailureStatus(row.route);
            panel.RefreshPanelText();
            Debug.LogError("[StudyManager] Authoritative route record is unavailable: " + row.route + ".");
            return false;
        }
        activeRouteDefinition = routeDefinition;

        string routeToken = SanitizePathToken(row.route);
        string baseName = $"block{row.block}_{row.condition}_{routeToken}";
        string participantRoot = Path.Combine(Application.persistentDataPath, "study", row.participant);
        string requestedDirectory = Path.Combine(participantRoot, baseName);
        int retry = 0;
        if (Directory.Exists(requestedDirectory))
        {
            string confirmationKey = row.participant + ":" + row.block;
            if (retryConfirmationKey != confirmationKey || Time.unscaledTime > retryConfirmationDeadline)
            {
                retryConfirmationKey = confirmationKey;
                retryConfirmationDeadline = Time.unscaledTime + 10f;
                state.statusMessage = "Block data exists. Press Start again to create a retry.";
                panel.RefreshPanelText();
                return false;
            }

            retry = 1;
            while (Directory.Exists(requestedDirectory + "_retry" + retry))
            {
                retry++;
            }
            requestedDirectory += "_retry" + retry;
        }

        retryConfirmationKey = null;
        BeginValidatedRow(
            row,
            requestedDirectory,
            retry,
            false,
            Time.realtimeSinceStartupAsDouble,
            DateTimeOffset.UtcNow);
        return true;
    }

    /// <summary>
    /// Starts an unscheduled VR run. The panel exposes canonical B/C as Mode A/B and cycles
    /// only the approved catalog routes. Timestamps identify runs for post-hoc annotation.
    /// </summary>
    public bool StartManualRun()
    {
        if (state.manualRunRecoveryBlocked)
        {
            state.statusMessage = "Resolve the previous manual-run recovery error before starting another run.";
            panel.RefreshPanelText();
            return false;
        }
        if (state.IsAuxiliaryActive)
        {
            state.statusMessage = "End the practice or estimation sequence first.";
            panel.RefreshPanelText();
            return false;
        }
        if (state.blockRunning)
        {
            state.statusMessage = "End the current block first.";
            panel.RefreshPanelText();
            return false;
        }
        if (boardAlignment != null && boardAlignment.IsBusy)
        {
            state.statusMessage = "Wait for board calibration or spatial-anchor loading to finish.";
            panel.RefreshPanelText();
            return false;
        }
        double requestedStartRealtime = Time.realtimeSinceStartupAsDouble;
        DateTimeOffset requestedStartUtc = DateTimeOffset.UtcNow;
        List<string> routes = sceneConfiguror != null
            ? sceneConfiguror.GetStudyRouteNames()
            : new List<string>();
        if (routes.Count == 0)
        {
            state.statusMessage = "No routes are available.";
            panel.RefreshPanelText();
            return false;
        }

        state.adhocRouteIndex = Mathf.Clamp(state.adhocRouteIndex, 0, routes.Count - 1);
        StudyScheduleRow row = new()
        {
            participant = "UNASSIGNED",
            block = 0,
            condition = StudySessionState.RuntimeConditions[state.adhocConditionIndex],
            route = routes[state.adhocRouteIndex],
        };
        if (!TryValidateRowRuntime(row))
        {
            return false;
        }
        if (!sceneConfiguror.TryGetRouteDefinition(row.route, out activeRouteDefinition))
        {
            throw new InvalidOperationException("Approved route definition is unavailable: " + row.route + ".");
        }

        string directory = Path.Combine(
            Application.persistentDataPath,
            "study",
            "MANUAL",
            $"{requestedStartUtc.UtcDateTime:yyyyMMdd_HHmmss_fff}_{row.condition}_{SanitizePathToken(row.route)}");
        directory = GetUnusedDirectory(directory);
        BeginValidatedRow(
            row,
            directory,
            0,
            true,
            requestedStartRealtime,
            requestedStartUtc);
        return true;
    }

    private bool TryValidateRowRuntime(StudyScheduleRow row)
    {
        if (sceneConfiguror == null || actionRecorder == null)
        {
            state.statusMessage = "Study runtime references are unavailable.";
            panel.RefreshPanelText();
            return false;
        }
        if (row.condition != "A" && !sceneConfiguror.IsGripFeedbackReady)
        {
            state.statusMessage = "Grip feedback is unavailable on this device.";
            panel.RefreshPanelText();
            return false;
        }
        bool routeReady = row.condition == "A"
            ? sceneConfiguror.TrySelectBaselineRoute(row.route, out string routeError)
            : sceneConfiguror.TryValidateRoute(row.route, out routeError);
        if (!routeReady)
        {
            state.statusMessage = StudyRouteIdentity.FormatRouteFailureStatus(row.route);
            panel.RefreshPanelText();
            Debug.LogError("[StudyManager] " + routeError);
            return false;
        }
        return true;
    }

    private void BeginValidatedRow(
        StudyScheduleRow row,
        string directory,
        int retry,
        bool adhoc,
        double requestedStartRealtime,
        DateTimeOffset requestedStartUtc)
    {
        if (Directory.Exists(directory) && Directory.EnumerateFileSystemEntries(directory).Any())
        {
            throw new IOException("Refusing to overwrite non-empty study run directory: " + directory);
        }
        Directory.CreateDirectory(directory);
        if (!adhoc)
        {
            state.participantsWithBlockRuns.Add(row.participant);
        }
        state.activeRow = row;
        state.activeDirectory = directory;
        manifestPath = Path.Combine(state.activeDirectory, "session.json");
        manifestCreated = false;
        activeManifest = new StudySessionManifest
        {
            participant = row.participant,
            block = row.block,
            condition = row.condition,
            route = row.route,
            routeName = activeRouteDefinition != null ? activeRouteDefinition.name : row.route,
            routeSourceProblemId = activeRouteDefinition != null ? activeRouteDefinition.sourceProblemId : string.Empty,
            routeCatalogSha256 = state.routeCatalogSha256,
            boardSetup = state.routeCatalog != null ? state.routeCatalog.setupName : string.Empty,
            boardOverhangAngleDegrees = state.routeCatalog != null ? state.routeCatalog.overhangAngleDegrees : 0,
            routeCuePresentation = string.Empty,
            routeDefinition = activeRouteDefinition,
            boardAlignment = boardAlignment != null ? boardAlignment.GetSnapshot() : null,
            boardAlignmentEnd = null,
            retry = retry,
            adhoc = adhoc,
            appVersion = Application.version,
            gitRevision = StudyBuildRevision.Current,
            startUtc = string.Empty,
            rehearsalStartUtc = string.Empty,
            rehearsalDeadlineUtc = string.Empty,
            resumeCount = 0,
            pendingStart = true,
            pendingResumeIndex = 0,
            firstInteractionRecorded = row.condition == "A",
            recordingSummaryComplete = true,
            endUtc = string.Empty,
            endedEarly = false,
            endReason = "running",
            routesJsonSha256 = sceneConfiguror.IsBuiltInRoute(row.route) ||
                               sceneConfiguror.TryGetRouteDefinition(
                                   row.route,
                                   out MoonBoardRouteDefinition _)
                ? null
                : sceneConfiguror.RoutesJsonSha256,
            gripFeedback = sceneConfiguror.IsGripFeedbackDegraded
                ? "degraded_at_" + sceneConfiguror.GripFeedbackDegradedUtc
                : "ok",
            gripGateVersion = sceneConfiguror.DescribeGripGateVersion(),
            gripCueVersion = sceneConfiguror.DescribeGripCueVersion(),
        };

        segmentDroppedCaptureFramesBaseline = 0;
        segmentHoldAggregateBaseline = Array.Empty<HoldAggregateData>();
        bool recordingStarted = false;
        try
        {
            state.blockTimerStarted = false;
            completionRequested = false;
            firstInteractionRecorded = activeManifest.firstInteractionRecorded;
            state.panelPinned = true;
            state.blockRunning = true;
            PrepareRehearsalClock(requestedStartRealtime, requestedStartUtc);
            WriteManifest();

            sceneConfiguror.ResetMoonBoardTransform();
            sceneConfiguror.SetStudyEnvironmentVisible(true);
            sceneConfiguror.SetUpRouteByName(row.route);
            sceneConfiguror.SetGameMode(row.condition switch
            {
                "B" => GameMode.Grip,
                "C" => GameMode.Ghost,
                _ => GameMode.Basic,
            });
            sceneConfiguror.SetStudyEnvironmentVisible(row.condition != "A");
            sceneConfiguror.SetStudyFeedbackVisible(row.condition != "A");
            activeManifest.routeCuePresentation = row.condition == "A"
                ? RouteCuePresentation.PhysicalBoardLeds.ToString()
                : sceneConfiguror.CurrentRouteCuePresentation.ToString();
            WriteManifest();

            actionRecorder.BeginBlock(
                state.activeDirectory,
                activeManifest,
                string.Empty,
                ElapsedSeconds);
            recordingStarted = true;

            activeManifest.pendingStart = false;
            RecordRehearsalClockStarted("ManualStart");

            headsetPresence.InitializeBlockHeadsetWear();
            state.statusMessage = adhoc
                ? $"Running mode {(row.condition == "B" ? "A" : "B")}."
                : $"Running {row.participant} block {row.block}.";
            if (row.condition == "A")
            {
                panel.ShowPanel();
            }
            else
            {
                panel.SetPanelVisible(false);
            }
            panel.RefreshPanelText();
        }
        catch (Exception startException)
        {
            List<Exception> rollbackErrors = new();
            if (recordingStarted || actionRecorder.HasActiveSession)
            {
                try
                {
                    actionRecorder.EndBlock();
                }
                catch (Exception exception)
                {
                    rollbackErrors.Add(exception);
                }
            }
            try
            {
                sceneConfiguror.ResetManualStudyState(true);
            }
            catch (Exception exception)
            {
                rollbackErrors.Add(exception);
            }
            try
            {
                if (manifestCreated)
                {
                    activeManifest.pendingStart = false;
                    activeManifest.recordingSummaryComplete = false;
                    activeManifest.endUtc = DateTime.UtcNow.ToString("o");
                    activeManifest.endedEarly = true;
                    activeManifest.endReason = "startup_failed";
                    activeManifest.boardAlignmentEnd = null;
                    WriteManifest();
                }
            }
            catch (Exception exception)
            {
                rollbackErrors.Add(exception);
            }
            if (!manifestCreated && Directory.Exists(directory))
            {
                try
                {
                    if (!Directory.EnumerateFileSystemEntries(directory).Any())
                    {
                        Directory.Delete(directory);
                    }
                }
                catch (Exception exception)
                {
                    rollbackErrors.Add(exception);
                }
            }
            bool unresolvedStorage = !manifestCreated && Directory.Exists(directory);
            if (!adhoc)
            {
                state.participantsWithBlockRuns.Remove(row.participant);
            }
            ClearRecoveredRunReferences();
            state.manualRunRecoveryBlocked = adhoc && (unresolvedStorage || rollbackErrors.Count > 0);
            if (rollbackErrors.Count > 0)
            {
                rollbackErrors.Insert(0, startException);
                throw new AggregateException(
                    "Study run startup failed and rollback was incomplete.",
                    rollbackErrors);
            }
            throw;
        }
    }

    /// <summary>
    /// Experimenter-facing summary of recordings set aside during the last recovery scan
    /// (stale-catalog data that can never be resumed); null when the scan set nothing aside.
    /// </summary>
    public string RecoveryNotice { get; private set; }

    public ManualRunRecoveryOutcome TryRecoverManualRun()
    {
        RecoveryNotice = null;
        string manualRoot = Path.Combine(Application.persistentDataPath, "study", "MANUAL");
        List<string> approvedRoutes = sceneConfiguror != null
            ? sceneConfiguror.GetStudyRouteNames()
            : new List<string>();
        if (approvedRoutes.Count == 0 || string.IsNullOrWhiteSpace(state.routeCatalogSha256))
        {
            return ManualRunRecoveryOutcome.None;
        }

        DateTimeOffset utcNow = DateTimeOffset.UtcNow;
        bool found = StudyRehearsalTiming.TryRecoverActiveManualRun(
            manualRoot,
            state.routeCatalogSha256,
            approvedRoutes,
            utcNow,
            out StudyRehearsalTiming.ActiveManualRunRecovery recovery,
            out string diagnostic,
            out string staleNotice);
        if (!string.IsNullOrWhiteSpace(staleNotice))
        {
            int staleCount = staleNotice.Split('\n').Length;
            Debug.LogWarning(
                "[BlockRunController] Set aside " + staleCount + " manual recording(s) pinned " +
                "to a different approved catalog; they cannot be resumed and their files were " +
                "left untouched.\n" + staleNotice);
            RecoveryNotice = "Set aside " + staleCount +
                             " stale manual recording(s) from older catalogs; see the Unity log.";
        }
        if (!found)
        {
            if (!string.IsNullOrWhiteSpace(diagnostic))
            {
                throw new InvalidDataException(
                    "Manual-run recovery found unresolved data.\n" + diagnostic);
            }
            return ManualRunRecoveryOutcome.None;
        }
        if (!string.IsNullOrWhiteSpace(diagnostic))
        {
            throw new InvalidDataException(
                "Manual-run recovery found unresolved data.\n" + diagnostic);
        }
        if (!sceneConfiguror.TryGetRouteDefinition(
                recovery.Manifest.route,
                out MoonBoardRouteDefinition routeDefinition))
        {
            throw new InvalidOperationException(
                "Recovered approved route definition is unavailable: " + recovery.Manifest.route + ".");
        }

        activeManifest = recovery.Manifest;
        activeRouteDefinition = routeDefinition;
        manifestPath = Path.Combine(recovery.DirectoryPath, "session.json");
        manifestCreated = File.Exists(manifestPath);
        ReconcilePendingResumeSegment(recovery.DirectoryPath);
        utcNow = DateTimeOffset.UtcNow;
        if (activeManifest.pendingStart)
        {
            return FinalizeInterruptedStartup(recovery, utcNow);
        }
        StudyScheduleRow row = new()
        {
            participant = activeManifest.participant,
            block = activeManifest.block,
            condition = activeManifest.condition,
            route = activeManifest.route,
        };
        int routeIndex = approvedRoutes.IndexOf(row.route);
        if (routeIndex < 0)
        {
            throw new InvalidOperationException("Recovered route disappeared from the approved catalog.");
        }
        if (boardAlignment != null && boardAlignment.IsBusy)
        {
            throw new InvalidOperationException(
                "Manual-run recovery cannot start while board calibration or anchor loading is active.");
        }
        if (!TryValidateRowRuntime(row))
        {
            throw new InvalidOperationException(
                "Manual-run recovery failed runtime validation: " + state.statusMessage);
        }
        if (!string.Equals(activeManifest.appVersion, Application.version, StringComparison.Ordinal) ||
            !string.Equals(activeManifest.gitRevision, StudyBuildRevision.Current, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                "Manual-run recovery requires the same app version and git revision that started the run.");
        }
        ValidateExistingRecordingSegments(recovery.DirectoryPath, activeManifest.resumeCount);

        sceneConfiguror.ResetMoonBoardTransform();
        sceneConfiguror.SetStudyEnvironmentVisible(true);
        sceneConfiguror.SetUpRouteByName(row.route);
        sceneConfiguror.SetGameMode(row.condition == "B" ? GameMode.Grip : GameMode.Ghost);
        sceneConfiguror.SetStudyFeedbackVisible(true);

        int resumeIndex = checked(activeManifest.resumeCount + 1);
        segmentDroppedCaptureFramesBaseline = activeManifest.droppedCaptureFrames;
        segmentHoldAggregateBaseline = StudyRehearsalTiming.MergeHoldAggregates(
            Array.Empty<HoldAggregateData>(),
            activeManifest.holdAggregates);
        bool recordingStarted = false;
        try
        {
            activeManifest.pendingResumeIndex = resumeIndex;
            WriteManifest();
            elapsedBeforeCurrentProcess = recovery.GetElapsedSeconds(DateTimeOffset.UtcNow);
            blockStartRealtime = Time.realtimeSinceStartupAsDouble;
            actionRecorder.BeginBlock(
                recovery.DirectoryPath,
                activeManifest,
                "_resume" + resumeIndex.ToString(CultureInfo.InvariantCulture),
                elapsedBeforeCurrentProcess);
            recordingStarted = true;

            activeManifest.resumeCount = resumeIndex;
            activeManifest.pendingResumeIndex = 0;
            activeManifest.recordingSummaryComplete = false;
            WriteManifest();

            state.adhocConditionIndex = row.condition == "B" ? 0 : 1;
            state.adhocRouteIndex = routeIndex;
            state.activeRow = row;
            state.activeDirectory = recovery.DirectoryPath;
            state.blockTimerStarted = true;
            completionRequested = false;
            firstInteractionRecorded = activeManifest.firstInteractionRecorded;
            state.panelPinned = true;
            state.blockRunning = true;
            state.manualRunRecoveryBlocked = false;

            headsetPresence.InitializeBlockHeadsetWear();
            actionRecorder.Record(
                "ManualRunRecovered",
                "",
                null,
                "condition=" + row.condition +
                ";resumeIndex=" + resumeIndex.ToString(CultureInfo.InvariantCulture) +
                ";elapsedBeforeResumeSeconds=" +
                elapsedBeforeCurrentProcess.ToString("F3", CultureInfo.InvariantCulture) +
                ";elapsedSeconds=" +
                ElapsedSeconds.ToString("F3", CultureInfo.InvariantCulture));
            panel.SetPanelVisible(false);
            state.statusMessage = "Recovered mode " + (row.condition == "B" ? "A" : "B") +
                                  " after " + StudyRehearsalTiming.FormatElapsedSeconds(ElapsedSeconds) +
                                  " elapsed.";
            panel.RefreshPanelText();
            return ManualRunRecoveryOutcome.Resumed;
        }
        catch (Exception recoveryException)
        {
            state.blockRunning = false;
            state.blockTimerStarted = false;
            state.manualRunRecoveryBlocked = true;
            if (!recordingStarted && !actionRecorder.HasActiveSession)
            {
                throw;
            }

            try
            {
                actionRecorder.EndBlock();
            }
            catch (Exception endException)
            {
                throw new AggregateException(
                    "Manual-run recovery and recording rollback both failed.",
                    recoveryException,
                    endException);
            }
            throw;
        }
    }

    public void EndBlockEarly()
    {
        EndBlock(true, "completed_early");
    }

    public void CompleteBlock()
    {
        if (state.blockRunning)
        {
            completionRequested = true;
        }
    }

    public void EndBlock(bool endedEarly, string reason)
    {
        if (!state.blockRunning)
        {
            return;
        }
        completionRequested = false;

        if (sceneConfiguror != null)
        {
            sceneConfiguror.SetGameMode(GameMode.Basic);
            sceneConfiguror.ResetMoonBoardTransform();
            sceneConfiguror.SetStudyEnvironmentVisible(true);
            sceneConfiguror.SetStudyFeedbackVisible(true);
        }
        activeManifest.endUtc = DateTime.UtcNow.ToString("o");
        activeManifest.pendingStart = false;
        activeManifest.endedEarly = endedEarly;
        activeManifest.endReason = reason;
        headsetPresence.FinalizeBlockHeadsetWear();
        actionRecorder.EndBlock();
        UpdateManifestRecordingSummary();
        activeManifest.boardAlignmentEnd = boardAlignment != null ? boardAlignment.GetSnapshot() : null;
        WriteManifest();

        state.blockRunning = false;
        state.blockTimerStarted = false;
        if (activeManifest != null && !activeManifest.adhoc && state.activeRow != null &&
            state.activeRow.block >= 1 && state.activeRow.block <= 3)
        {
            int endedParticipantIndex = state.participants.IndexOf(state.activeRow.participant);
            if (endedParticipantIndex < 0)
            {
                throw new InvalidOperationException(
                    "Ended participant is missing from the loaded schedule: " + state.activeRow.participant + ".");
            }
            state.lastEndedRow = state.activeRow;
            state.lastEndedDirectory = state.activeDirectory;
            state.lastEndedParticipantIndex = endedParticipantIndex;
        }
        else
        {
            estimation.ClearPendingEstimation();
        }
        state.statusMessage = activeManifest.adhoc
            ? "Run ended: " + reason + "."
            : $"Ended {state.activeRow.participant} block {state.activeRow.block}: {reason}.";
        panel.ShowPanel();
        panel.RefreshPanelText();
    }

    /// <summary>
    /// Advances the running block one frame. Elapsed time is informational only; this method
    /// never changes or ends the active condition.
    /// </summary>
    public void UpdateRunningBlock()
    {
        if (completionRequested)
        {
            EndBlock(false, "completed_manual");
            return;
        }
        HandleGripFeedbackDegradation();
        if (!firstInteractionRecorded && state.activeRow.condition != "A")
        {
            bool ghostDetached = sceneConfiguror != null &&
                                 sceneConfiguror.ghostHoldController != null &&
                                 sceneConfiguror.ghostHoldController.CurrentGhost != null;
            if (StudyRehearsalTiming.TryGetFirstInteraction(
                    state.activeRow.condition,
                    sceneConfiguror != null && sceneConfiguror.isGripLocomotionActive,
                    ghostDetached,
                    out string interaction))
            {
                if (!headsetPresence.BlockHeadsetDonnedRecorded)
                {
                    headsetPresence.InferHeadsetDonnedFromInteraction(interaction);
                }
                firstInteractionRecorded = true;
                activeManifest.firstInteractionRecorded = true;
                float donningLatency = Mathf.Max(
                    0f,
                    (float)(Time.realtimeSinceStartupAsDouble - headsetPresence.DonningStartRealtime));
                actionRecorder.Record(
                    "FirstInteraction",
                    "",
                    null,
                    "condition=" + state.activeRow.condition +
                    ";interaction=" + interaction +
                    ";donningLatencySeconds=" +
                    donningLatency.ToString("F3", CultureInfo.InvariantCulture) +
                    ";rehearsalElapsedSeconds=" +
                    ElapsedSeconds.ToString("F3", CultureInfo.InvariantCulture));
                CheckpointRecordingProgress();
            }
        }
    }

    private void PrepareRehearsalClock(
        double requestedStartRealtime,
        DateTimeOffset requestedStartUtc)
    {
        if (!state.blockRunning || state.blockTimerStarted || state.activeRow == null)
        {
            throw new InvalidOperationException("Rehearsal clock cannot start in the current run state.");
        }

        double now = Time.realtimeSinceStartupAsDouble;
        if (double.IsNaN(requestedStartRealtime) || double.IsInfinity(requestedStartRealtime) ||
            requestedStartRealtime < 0d || requestedStartRealtime > now)
        {
            throw new ArgumentOutOfRangeException(nameof(requestedStartRealtime));
        }
        blockStartRealtime = requestedStartRealtime;
        elapsedBeforeCurrentProcess = 0d;
        state.blockTimerStarted = true;
        activeManifest.startUtc = requestedStartUtc.ToString("o");
        activeManifest.rehearsalStartUtc = requestedStartUtc.ToString("o");
    }

    private void RecordRehearsalClockStarted(string trigger)
    {
        if (!state.blockRunning || !state.blockTimerStarted || state.activeRow == null ||
            actionRecorder == null || !actionRecorder.IsRecording)
        {
            throw new InvalidOperationException("Rehearsal clock start cannot be recorded in the current run state.");
        }

        actionRecorder.Record(
            "RehearsalClockStarted",
            "",
            null,
            "condition=" + state.activeRow.condition +
            ";block=" + state.activeRow.block.ToString(CultureInfo.InvariantCulture) +
            ";trigger=" + trigger);
        state.statusMessage = $"Running {state.activeRow.participant} block {state.activeRow.block}.";
        WriteManifest();
        panel.RefreshPanelText();
    }

    private void HandleGripFeedbackDegradation()
    {
        if (activeManifest == null || sceneConfiguror == null ||
            !sceneConfiguror.IsGripFeedbackDegraded ||
            !string.Equals(activeManifest.gripFeedback, "ok", StringComparison.Ordinal))
        {
            return;
        }

        string degradedUtc = string.IsNullOrEmpty(sceneConfiguror.GripFeedbackDegradedUtc)
            ? DateTime.UtcNow.ToString("o")
            : sceneConfiguror.GripFeedbackDegradedUtc;
        activeManifest.gripFeedback = "degraded_at_" + degradedUtc;
        actionRecorder?.Record(
            "GripFeedbackDegraded",
            "",
            null,
            "GRIP CUE OFF at " + degradedUtc + "; block continues");
        state.statusMessage = "Run continuing.";
        CheckpointRecordingProgress();
    }

    public void CheckpointRecordingProgress()
    {
        if (!state.blockRunning || activeManifest == null || actionRecorder == null ||
            !actionRecorder.IsRecording)
        {
            return;
        }

        UpdateManifestRecordingSummary();
        WriteManifest();
    }

    private void UpdateManifestRecordingSummary()
    {
        activeManifest.droppedCaptureFrames = checked(
            segmentDroppedCaptureFramesBaseline + actionRecorder.DroppedCaptureFrames);
        activeManifest.holdAggregates = StudyRehearsalTiming.MergeHoldAggregates(
            segmentHoldAggregateBaseline,
            actionRecorder.GetHoldAggregates());
    }

    private void WriteManifest()
    {
        if (activeManifest == null || string.IsNullOrEmpty(manifestPath))
        {
            return;
        }

        string routesHash = activeManifest.routesJsonSha256;
        string manifestJson = string.Empty;
        try
        {
            if (routesHash == null)
            {
                activeManifest.routesJsonSha256 = NullRoutesHashSentinel;
            }
            manifestJson = JsonUtility.ToJson(activeManifest, true);
        }
        finally
        {
            activeManifest.routesJsonSha256 = routesHash;
        }
        if (routesHash == null)
        {
            manifestJson = manifestJson.Replace('"' + NullRoutesHashSentinel + '"', "null");
        }
        StudyManifestStorage.WriteAtomically(manifestPath, manifestJson, manifestCreated);
        manifestCreated = true;
        StudyManifestStorage.DeleteRecoveryFiles(manifestPath);
    }

    private ManualRunRecoveryOutcome FinalizeInterruptedStartup(
        StudyRehearsalTiming.ActiveManualRunRecovery recovery,
        DateTimeOffset utcNow)
    {
        activeManifest.pendingStart = false;
        activeManifest.pendingResumeIndex = 0;
        activeManifest.recordingSummaryComplete = false;
        activeManifest.endUtc = utcNow.ToString("o");
        activeManifest.endedEarly = true;
        activeManifest.endReason = "app_interrupted_startup";
        activeManifest.boardAlignmentEnd = null;
        WriteManifest();
        state.statusMessage = "Previous run was interrupted during startup.";
        ClearRecoveredRunReferences();
        return ManualRunRecoveryOutcome.Expired;
    }

    private void ReconcilePendingResumeSegment(string directory)
    {
        int pendingIndex = activeManifest.pendingResumeIndex;
        if (pendingIndex == 0)
        {
            return;
        }
        if (pendingIndex != activeManifest.resumeCount + 1)
        {
            throw new InvalidDataException(
                "Pending resume transaction is inconsistent with the manifest resume count.");
        }

        string suffix = "_resume" + pendingIndex.ToString(CultureInfo.InvariantCulture);
        DeleteRecordingSegmentFiles(directory, pendingIndex);
        Debug.LogWarning(
            "[StudyManager] Rolled back uncommitted recording segment " + suffix.Substring(1) + ".");
        activeManifest.pendingResumeIndex = 0;
        WriteManifest();
    }

    private static void DeleteRecordingSegmentFiles(string directory, int resumeIndex)
    {
        string suffix = "_resume" + resumeIndex.ToString(CultureInfo.InvariantCulture);
        string eventsPath = Path.Combine(directory, "events" + suffix + ".csv");
        string capturePath = Path.Combine(directory, "capture" + suffix + ".csv.gz");
        if (File.Exists(eventsPath))
        {
            File.Delete(eventsPath);
        }
        if (File.Exists(capturePath))
        {
            File.Delete(capturePath);
        }
    }

    private void ValidateExistingRecordingSegments(string directory, int resumeCount)
    {
        if (!actionRecorder.recordToCsv)
        {
            return;
        }

        for (int index = 0; index <= resumeCount; index++)
        {
            string suffix = index == 0
                ? string.Empty
                : "_resume" + index.ToString(CultureInfo.InvariantCulture);
            string eventsPath = Path.Combine(directory, "events" + suffix + ".csv");
            string capturePath = Path.Combine(directory, "capture" + suffix + ".csv.gz");
            if (!File.Exists(eventsPath) || !File.Exists(capturePath))
            {
                throw new IOException(
                    "Recovered recording segment is incomplete: " +
                    (index == 0 ? "initial" : suffix.Substring(1)) + ".");
            }
        }

        HashSet<int> eventIndexes = GetResumeFileIndexes(directory, "events_resume", ".csv");
        HashSet<int> captureIndexes = GetResumeFileIndexes(directory, "capture_resume", ".csv.gz");
        if (!eventIndexes.SetEquals(captureIndexes) ||
            eventIndexes.Any(index => index < 1 || index > resumeCount) ||
            eventIndexes.Count != resumeCount)
        {
            throw new IOException("Recording segment files do not match the manifest resume count.");
        }
    }

    private static HashSet<int> GetResumeFileIndexes(
        string directory,
        string prefix,
        string extension)
    {
        HashSet<int> indexes = new();
        foreach (string path in Directory.GetFiles(directory, prefix + "*" + extension))
        {
            string fileName = Path.GetFileName(path);
            string indexText = fileName.Substring(
                prefix.Length,
                fileName.Length - prefix.Length - extension.Length);
            if (!int.TryParse(
                    indexText,
                    NumberStyles.None,
                    CultureInfo.InvariantCulture,
                    out int index) ||
                index <= 0 ||
                !string.Equals(
                    fileName,
                    prefix + index.ToString(CultureInfo.InvariantCulture) + extension,
                    StringComparison.Ordinal))
            {
                throw new IOException("Recording segment file has a non-canonical name: " + fileName);
            }
            indexes.Add(index);
        }
        return indexes;
    }

    private void ClearRecoveredRunReferences()
    {
        activeManifest = null;
        activeRouteDefinition = null;
        manifestPath = null;
        manifestCreated = false;
        state.activeRow = null;
        state.activeDirectory = null;
        state.blockRunning = false;
        state.blockTimerStarted = false;
        completionRequested = false;
        state.manualRunRecoveryBlocked = false;
        elapsedBeforeCurrentProcess = 0d;
        segmentDroppedCaptureFramesBaseline = 0;
        segmentHoldAggregateBaseline = Array.Empty<HoldAggregateData>();
    }

    private static string SanitizePathToken(string value)
    {
        StringBuilder output = new(value.Length);
        foreach (char character in value.ToUpperInvariant())
        {
            output.Append(char.IsLetterOrDigit(character) ? character : '_');
        }
        return output.ToString().Trim('_');
    }

    public static string GetUnusedDirectory(string requestedDirectory)
    {
        if (!Directory.Exists(requestedDirectory))
        {
            return requestedDirectory;
        }
        int retry = 1;
        while (Directory.Exists(requestedDirectory + "_retry" + retry.ToString(CultureInfo.InvariantCulture)))
        {
            retry++;
        }
        return requestedDirectory + "_retry" + retry.ToString(CultureInfo.InvariantCulture);
    }
}
