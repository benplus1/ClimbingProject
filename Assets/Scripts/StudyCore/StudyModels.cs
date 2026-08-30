using System;
using UnityEngine;

[Serializable]
public sealed class StudyScheduleRow
{
    public string participant;
    public int block;
    public string condition;
    public string route;
}

[Serializable]
public sealed class HoldAggregateData
{
    public string hold;
    public float secondsTouched;
    public int gripsDetected;
    public float meanScore;
    public float maxScore;
    public int scoreSamples;
}

[Serializable]
public sealed class StudySessionManifest
{
    public string participant;
    public int block;
    public string condition;
    public string route;
    public string routeName;
    public string routeSourceProblemId;
    public string routeCatalogSha256;
    public string boardSetup;
    public int boardOverhangAngleDegrees;
    public string routeCuePresentation;
    public MoonBoardRouteDefinition routeDefinition;
    public BoardAlignmentSnapshot boardAlignment;
    public BoardAlignmentSnapshot boardAlignmentEnd;
    public int retry;
    public bool adhoc;
    public string appVersion;
    public string gitRevision;
    public string startUtc;
    public string rehearsalStartUtc;
    public string rehearsalDeadlineUtc;
    public int resumeCount;
    public bool pendingStart;
    public int pendingResumeIndex;
    public bool firstInteractionRecorded;
    public bool recordingSummaryComplete;
    public string endUtc;
    public bool endedEarly;
    public string endReason;
    public string routesJsonSha256;
    public string gripFeedback;
    public string gripGateVersion;
    public string gripCueVersion;
    public int droppedCaptureFrames;
    public HoldAggregateData[] holdAggregates = Array.Empty<HoldAggregateData>();
}

[Serializable]
public sealed class BoardAlignmentSnapshot
{
    public bool isAligned;
    public bool isSpatiallyAnchored;
    public string spatialAnchorUuid;
    public int recenterEpoch;
    public Vector3 position;
    public Quaternion rotation;
}
