CurveClearanceResultData^ Bridge::MeasureWorkingCurveClearance(
    long long contextId,
    long long revision,
    array<CurveDefinitionData^>^ curves,
    array<int>^ localCurveIndexPairs,
    double wireDiameterMm,
    double maximumErrorMm,
    int maximumPairChecks)
{
    if (curves == nullptr || localCurveIndexPairs == nullptr || localCurveIndexPairs->Length % 2 != 0)
        return CreateManagedCurveClearanceResult(
            WorkingCurveClearanceStatus::InvalidInput,
            nullptr,
            "간격 질의에 곡선 정의와 짝을 이룬 연결 인덱스 배열이 필요합니다.");

    try
    {
        std::vector<CurveDefinitionNative> nativeCurves;
        std::vector<std::pair<int, int>> nativeLocalCurvePairs;
        nativeCurves.reserve(static_cast<size_t>(curves->Length));
        nativeLocalCurvePairs.reserve(static_cast<size_t>(localCurveIndexPairs->Length / 2));
        for each (CurveDefinitionData^ curve in curves)
            nativeCurves.push_back(ReadNativeCurveDefinition(curve));
        for (int index = 0; index < localCurveIndexPairs->Length; index += 2)
            nativeLocalCurvePairs.emplace_back(localCurveIndexPairs[index], localCurveIndexPairs[index + 1]);

        const NativeCurveClearanceMeasurement measured = MeasureCurveClearanceNative(
            *g_documentSession,
            contextId,
            revision,
            nativeCurves,
            nativeLocalCurvePairs,
            wireDiameterMm,
            maximumErrorMm,
            maximumPairChecks);
        return CreateManagedCurveClearanceResult(
            MapCurveClearanceStatus(measured.status),
            &measured,
            DescribeCurveClearanceUnresolvedReason(measured));
    }
    catch (Exception^ exception)
    {
        return CreateManagedCurveClearanceResult(
            WorkingCurveClearanceStatus::InvalidInput,
            nullptr,
            exception->Message);
    }
    catch (...)
    {
        return CreateManagedCurveClearanceResult(
            WorkingCurveClearanceStatus::GeometryFailure,
            nullptr);
    }
}

CurveClearanceResultData^ Bridge::MeasurePreparedCandidateWorkingCurveClearance(
    long long candidateId,
    long long contextId,
    long long revision,
    array<CurveDefinitionData^>^ curves,
    array<int>^ localCurveIndexPairs,
    double wireDiameterMm,
    double maximumErrorMm,
    int maximumPairChecks)
{
    auto candidate = g_preparedDocumentCandidates.find(candidateId);
    if (candidateId <= 0 || candidate == g_preparedDocumentCandidates.end() || !candidate->second.document)
    {
        return CreateManagedCurveClearanceResult(
            WorkingCurveClearanceStatus::CandidateNotFound,
            nullptr);
    }
    if (candidate->second.baseModelRevision != g_documentSession->modelRevision)
    {
        return CreateManagedCurveClearanceResult(
            WorkingCurveClearanceStatus::StaleContext,
            nullptr);
    }
    if (curves == nullptr || localCurveIndexPairs == nullptr || localCurveIndexPairs->Length % 2 != 0)
    {
        return CreateManagedCurveClearanceResult(
            WorkingCurveClearanceStatus::InvalidInput,
            nullptr,
            "간격 질의에 곡선 정의와 짝을 이룬 연결 인덱스 배열이 필요합니다.");
    }

    try
    {
        std::vector<CurveDefinitionNative> nativeCurves;
        std::vector<std::pair<int, int>> nativeLocalCurvePairs;
        nativeCurves.reserve(static_cast<size_t>(curves->Length));
        nativeLocalCurvePairs.reserve(static_cast<size_t>(localCurveIndexPairs->Length / 2));
        for each (CurveDefinitionData^ curve in curves)
            nativeCurves.push_back(ReadNativeCurveDefinition(curve));
        for (int index = 0; index < localCurveIndexPairs->Length; index += 2)
            nativeLocalCurvePairs.emplace_back(localCurveIndexPairs[index], localCurveIndexPairs[index + 1]);

        const NativeCurveClearanceMeasurement measured = MeasureCurveClearanceNative(
            *candidate->second.document,
            contextId,
            revision,
            nativeCurves,
            nativeLocalCurvePairs,
            wireDiameterMm,
            maximumErrorMm,
            maximumPairChecks);
        return CreateManagedCurveClearanceResult(
            MapCurveClearanceStatus(measured.status),
            &measured,
            DescribeCurveClearanceUnresolvedReason(measured));
    }
    catch (Exception^ exception)
    {
        return CreateManagedCurveClearanceResult(
            WorkingCurveClearanceStatus::InvalidInput,
            nullptr,
            exception->Message);
    }
    catch (...)
    {
        return CreateManagedCurveClearanceResult(
            WorkingCurveClearanceStatus::GeometryFailure,
            nullptr);
    }
}

CurveDepthEnvelopeResultData^ Bridge::MeasureWorkingCurveDepthEnvelope(
    long long contextId,
    long long revision,
    array<double>^ centerlineSamples,
    array<int>^ selectedFaceIds,
    int anchorFaceId,
    double anchorU,
    double anchorV,
    array<double>^ anchorPoint,
    double wireDiameterMm,
    double requiredInsertionDepthMm,
    double maximumSampleSpacingMm,
    double requestedAngularToleranceRad,
    double maximumMeasuredDeviationMm,
    double connectionToleranceMm,
    int maximumProbeCount)
{
    if (centerlineSamples == nullptr || centerlineSamples->Length < 6 || centerlineSamples->Length % 3 != 0 ||
        selectedFaceIds == nullptr || selectedFaceIds->Length == 0 || anchorPoint == nullptr || anchorPoint->Length != 3)
    {
        NativeCurveDepthEnvelopeMeasurement invalid;
        invalid.status = CurveDepthEnvelopeStatusNative::InvalidInput;
        invalid.diagnostic = "깊이 질의에는 곡선 표본, 선택 Face, 앵커 좌표가 필요합니다.";
        return CreateManagedCurveDepthEnvelopeResult(invalid);
    }

    try
    {
        std::vector<double> nativeSamples;
        std::vector<int> nativeFaceIds;
        std::vector<double> nativeAnchorPoint;
        nativeSamples.reserve(static_cast<size_t>(centerlineSamples->Length));
        nativeFaceIds.reserve(static_cast<size_t>(selectedFaceIds->Length));
        nativeAnchorPoint.reserve(3);
        for each (double coordinate in centerlineSamples)
            nativeSamples.push_back(coordinate);
        for each (int faceId in selectedFaceIds)
            nativeFaceIds.push_back(faceId);
        for each (double coordinate in anchorPoint)
            nativeAnchorPoint.push_back(coordinate);

        const NativeCurveDepthEnvelopeMeasurement measurement = MeasureCurveDepthEnvelopeNative(
            *g_documentSession,
            contextId,
            revision,
            nativeSamples,
            nativeFaceIds,
            anchorFaceId,
            anchorU,
            anchorV,
            nativeAnchorPoint,
            wireDiameterMm,
            requiredInsertionDepthMm,
            maximumSampleSpacingMm,
            requestedAngularToleranceRad,
            maximumMeasuredDeviationMm,
            connectionToleranceMm,
            maximumProbeCount);
        return CreateManagedCurveDepthEnvelopeResult(measurement);
    }
    catch (Exception^ exception)
    {
        NativeCurveDepthEnvelopeMeasurement failure;
        failure.status = CurveDepthEnvelopeStatusNative::InvalidInput;
        failure.diagnostic = msclr::interop::marshal_as<std::string>(exception->Message);
        return CreateManagedCurveDepthEnvelopeResult(failure);
    }
    catch (...)
    {
        NativeCurveDepthEnvelopeMeasurement failure;
        failure.status = CurveDepthEnvelopeStatusNative::GeometryFailure;
        return CreateManagedCurveDepthEnvelopeResult(failure);
    }
}

CurveDepthEnvelopeResultData^ Bridge::MeasurePreparedCandidateWorkingCurveDepthEnvelope(
    long long candidateId,
    long long contextId,
    long long revision,
    array<double>^ centerlineSamples,
    array<int>^ selectedFaceIds,
    int anchorFaceId,
    double anchorU,
    double anchorV,
    array<double>^ anchorPoint,
    double wireDiameterMm,
    double requiredInsertionDepthMm,
    double maximumSampleSpacingMm,
    double requestedAngularToleranceRad,
    double maximumMeasuredDeviationMm,
    double connectionToleranceMm,
    int maximumProbeCount)
{
    auto candidate = g_preparedDocumentCandidates.find(candidateId);
    if (candidateId <= 0 || candidate == g_preparedDocumentCandidates.end() || !candidate->second.document)
    {
        NativeCurveDepthEnvelopeMeasurement failure;
        failure.status = CurveDepthEnvelopeStatusNative::CandidateNotFound;
        return CreateManagedCurveDepthEnvelopeResult(failure);
    }
    if (candidate->second.baseModelRevision != g_documentSession->modelRevision)
    {
        NativeCurveDepthEnvelopeMeasurement failure;
        failure.status = CurveDepthEnvelopeStatusNative::StaleContext;
        return CreateManagedCurveDepthEnvelopeResult(failure);
    }
    if (centerlineSamples == nullptr || centerlineSamples->Length < 6 || centerlineSamples->Length % 3 != 0 ||
        selectedFaceIds == nullptr || selectedFaceIds->Length == 0 || anchorPoint == nullptr || anchorPoint->Length != 3)
    {
        NativeCurveDepthEnvelopeMeasurement invalid;
        invalid.status = CurveDepthEnvelopeStatusNative::InvalidInput;
        invalid.diagnostic = "깊이 질의에는 곡선 표본, 선택 Face, 앵커 좌표가 필요합니다.";
        return CreateManagedCurveDepthEnvelopeResult(invalid);
    }

    try
    {
        std::vector<double> nativeSamples;
        std::vector<int> nativeFaceIds;
        std::vector<double> nativeAnchorPoint;
        nativeSamples.reserve(static_cast<size_t>(centerlineSamples->Length));
        nativeFaceIds.reserve(static_cast<size_t>(selectedFaceIds->Length));
        nativeAnchorPoint.reserve(3);
        for each (double coordinate in centerlineSamples)
            nativeSamples.push_back(coordinate);
        for each (int faceId in selectedFaceIds)
            nativeFaceIds.push_back(faceId);
        for each (double coordinate in anchorPoint)
            nativeAnchorPoint.push_back(coordinate);

        const NativeCurveDepthEnvelopeMeasurement measurement = MeasureCurveDepthEnvelopeNative(
            *candidate->second.document,
            contextId,
            revision,
            nativeSamples,
            nativeFaceIds,
            anchorFaceId,
            anchorU,
            anchorV,
            nativeAnchorPoint,
            wireDiameterMm,
            requiredInsertionDepthMm,
            maximumSampleSpacingMm,
            requestedAngularToleranceRad,
            maximumMeasuredDeviationMm,
            connectionToleranceMm,
            maximumProbeCount);
        return CreateManagedCurveDepthEnvelopeResult(measurement);
    }
    catch (Exception^ exception)
    {
        NativeCurveDepthEnvelopeMeasurement failure;
        failure.status = CurveDepthEnvelopeStatusNative::InvalidInput;
        failure.diagnostic = msclr::interop::marshal_as<std::string>(exception->Message);
        return CreateManagedCurveDepthEnvelopeResult(failure);
    }
    catch (...)
    {
        NativeCurveDepthEnvelopeMeasurement failure;
        failure.status = CurveDepthEnvelopeStatusNative::GeometryFailure;
        return CreateManagedCurveDepthEnvelopeResult(failure);
    }
}

CurveDepthAdjustmentResultData^ Bridge::AdjustWorkingCurveInsertionDepth(
    long long contextId,
    long long revision,
    array<double>^ surfacePathWorkingSamples,
    array<int>^ selectedFaceIds,
    int anchorFaceId,
    double anchorU,
    double anchorV,
    array<double>^ anchorPoint,
    double wireDiameterMm,
    double requiredInsertionDepthMm,
    double maximumSampleSpacingMm,
    double requestedAngularToleranceRad,
    double maximumSurfaceDeviationMm,
    double connectionToleranceMm,
    int maximumProbeCount)
{
    if (surfacePathWorkingSamples == nullptr || surfacePathWorkingSamples->Length < 4 ||
        surfacePathWorkingSamples->Length % 2 != 0 || selectedFaceIds == nullptr || selectedFaceIds->Length == 0 ||
        anchorPoint == nullptr || anchorPoint->Length != 3)
    {
        NativeCurveDepthAdjustment invalid;
        invalid.status = CurveDepthEnvelopeStatusNative::InvalidInput;
        invalid.diagnostic = "깊이 보정에는 작업 평면 경로, 선택 Face와 앵커가 필요합니다.";
        return CreateManagedCurveDepthAdjustmentResult(invalid);
    }

    try
    {
        std::vector<double> nativeSamples;
        std::vector<int> nativeFaceIds;
        std::vector<double> nativeAnchorPoint;
        nativeSamples.reserve(static_cast<size_t>(surfacePathWorkingSamples->Length));
        nativeFaceIds.reserve(static_cast<size_t>(selectedFaceIds->Length));
        nativeAnchorPoint.reserve(3);
        for each (double coordinate in surfacePathWorkingSamples)
            nativeSamples.push_back(coordinate);
        for each (int faceId in selectedFaceIds)
            nativeFaceIds.push_back(faceId);
        for each (double coordinate in anchorPoint)
            nativeAnchorPoint.push_back(coordinate);

        const NativeCurveDepthAdjustment adjustment = AdjustCurveInsertionDepthNative(
            *g_documentSession,
            contextId,
            revision,
            nativeSamples,
            nativeFaceIds,
            anchorFaceId,
            anchorU,
            anchorV,
            nativeAnchorPoint,
            wireDiameterMm,
            requiredInsertionDepthMm,
            maximumSampleSpacingMm,
            requestedAngularToleranceRad,
            maximumSurfaceDeviationMm,
            connectionToleranceMm,
            maximumProbeCount);
        return CreateManagedCurveDepthAdjustmentResult(adjustment);
    }
    catch (Exception^ exception)
    {
        NativeCurveDepthAdjustment failure;
        failure.status = CurveDepthEnvelopeStatusNative::InvalidInput;
        failure.diagnostic = msclr::interop::marshal_as<std::string>(exception->Message);
        return CreateManagedCurveDepthAdjustmentResult(failure);
    }
    catch (...)
    {
        NativeCurveDepthAdjustment failure;
        failure.status = CurveDepthEnvelopeStatusNative::GeometryFailure;
        return CreateManagedCurveDepthAdjustmentResult(failure);
    }
}

CurveDepthAdjustmentResultData^ Bridge::AdjustPreparedCandidateWorkingCurveInsertionDepth(
    long long candidateId,
    long long contextId,
    long long revision,
    array<double>^ surfacePathWorkingSamples,
    array<int>^ selectedFaceIds,
    int anchorFaceId,
    double anchorU,
    double anchorV,
    array<double>^ anchorPoint,
    double wireDiameterMm,
    double requiredInsertionDepthMm,
    double maximumSampleSpacingMm,
    double requestedAngularToleranceRad,
    double maximumSurfaceDeviationMm,
    double connectionToleranceMm,
    int maximumProbeCount)
{
    auto candidate = g_preparedDocumentCandidates.find(candidateId);
    if (candidateId <= 0 || candidate == g_preparedDocumentCandidates.end() || !candidate->second.document)
    {
        NativeCurveDepthAdjustment failure;
        failure.status = CurveDepthEnvelopeStatusNative::CandidateNotFound;
        return CreateManagedCurveDepthAdjustmentResult(failure);
    }
    if (candidate->second.baseModelRevision != g_documentSession->modelRevision)
    {
        NativeCurveDepthAdjustment failure;
        failure.status = CurveDepthEnvelopeStatusNative::StaleContext;
        return CreateManagedCurveDepthAdjustmentResult(failure);
    }
    if (surfacePathWorkingSamples == nullptr || surfacePathWorkingSamples->Length < 4 ||
        surfacePathWorkingSamples->Length % 2 != 0 || selectedFaceIds == nullptr || selectedFaceIds->Length == 0 ||
        anchorPoint == nullptr || anchorPoint->Length != 3)
    {
        NativeCurveDepthAdjustment invalid;
        invalid.status = CurveDepthEnvelopeStatusNative::InvalidInput;
        invalid.diagnostic = "깊이 보정에는 작업 평면 경로, 선택 Face와 앵커가 필요합니다.";
        return CreateManagedCurveDepthAdjustmentResult(invalid);
    }

    try
    {
        std::vector<double> nativeSamples;
        std::vector<int> nativeFaceIds;
        std::vector<double> nativeAnchorPoint;
        nativeSamples.reserve(static_cast<size_t>(surfacePathWorkingSamples->Length));
        nativeFaceIds.reserve(static_cast<size_t>(selectedFaceIds->Length));
        nativeAnchorPoint.reserve(3);
        for each (double coordinate in surfacePathWorkingSamples)
            nativeSamples.push_back(coordinate);
        for each (int faceId in selectedFaceIds)
            nativeFaceIds.push_back(faceId);
        for each (double coordinate in anchorPoint)
            nativeAnchorPoint.push_back(coordinate);

        const NativeCurveDepthAdjustment adjustment = AdjustCurveInsertionDepthNative(
            *candidate->second.document,
            contextId,
            revision,
            nativeSamples,
            nativeFaceIds,
            anchorFaceId,
            anchorU,
            anchorV,
            nativeAnchorPoint,
            wireDiameterMm,
            requiredInsertionDepthMm,
            maximumSampleSpacingMm,
            requestedAngularToleranceRad,
            maximumSurfaceDeviationMm,
            connectionToleranceMm,
            maximumProbeCount);
        return CreateManagedCurveDepthAdjustmentResult(adjustment);
    }
    catch (Exception^ exception)
    {
        NativeCurveDepthAdjustment failure;
        failure.status = CurveDepthEnvelopeStatusNative::InvalidInput;
        failure.diagnostic = msclr::interop::marshal_as<std::string>(exception->Message);
        return CreateManagedCurveDepthAdjustmentResult(failure);
    }
    catch (...)
    {
        NativeCurveDepthAdjustment failure;
        failure.status = CurveDepthEnvelopeStatusNative::GeometryFailure;
        return CreateManagedCurveDepthAdjustmentResult(failure);
    }
}

ConnectionObstacleResultData^ Bridge::CheckConnectionPathObstacles(
    long long contextId,
    long long revision,
    array<CurveDefinitionData^>^ curves,
    int moldExitTransitionIndex,
    double connectionToleranceMm)
{
    if (curves == nullptr || curves->Length < 2 || curves->Length > 10000 || moldExitTransitionIndex < 1 ||
        moldExitTransitionIndex >= curves->Length || !std::isfinite(connectionToleranceMm) ||
        connectionToleranceMm <= 0.0)
    {
        return CreateManagedConnectionObstacleResult(
            WorkingConnectionObstacleStatus::InvalidInput,
            nullptr,
            "연결 곡선 배열과 금형 이탈 구간 번호가 유효하지 않습니다.");
    }
    auto context = g_documentSession->workingContexts.find(contextId);
    if (g_documentSession->modelRevision != revision || context == g_documentSession->workingContexts.end() ||
        !context->second || context->second->revision != revision)
    {
        return CreateManagedConnectionObstacleResult(WorkingConnectionObstacleStatus::StaleContext);
    }

    try
    {
        std::vector<CurveDefinitionNative> nativeCurves;
        nativeCurves.reserve(static_cast<size_t>(curves->Length));
        for each (CurveDefinitionData^ curve in curves)
            nativeCurves.push_back(ReadNativeCurveDefinition(curve));

        const NativeConnectionObstacleMeasurement measured = CheckConnectionPathObstaclesNative(
            *g_documentSession,
            nativeCurves,
            moldExitTransitionIndex,
            connectionToleranceMm);
        return CreateManagedConnectionObstacleResult(
            MapConnectionObstacleStatus(measured.status),
            &measured);
    }
    catch (Exception^ exception)
    {
        return CreateManagedConnectionObstacleResult(
            WorkingConnectionObstacleStatus::InvalidInput,
            nullptr,
            exception->Message);
    }
    catch (...)
    {
        return CreateManagedConnectionObstacleResult(
            WorkingConnectionObstacleStatus::GeometryFailure,
            nullptr);
    }
}

ConnectionObstacleResultData^ Bridge::CheckPreparedCandidateConnectionPathObstacles(
    long long candidateId,
    long long contextId,
    long long revision,
    array<CurveDefinitionData^>^ curves,
    int moldExitTransitionIndex,
    double connectionToleranceMm)
{
    auto candidate = g_preparedDocumentCandidates.find(candidateId);
    if (candidateId <= 0 || candidate == g_preparedDocumentCandidates.end() || !candidate->second.document)
    {
        return CreateManagedConnectionObstacleResult(WorkingConnectionObstacleStatus::CandidateNotFound);
    }
    if (candidate->second.baseModelRevision != g_documentSession->modelRevision)
    {
        return CreateManagedConnectionObstacleResult(WorkingConnectionObstacleStatus::StaleContext);
    }
    if (curves == nullptr || curves->Length < 2 || curves->Length > 10000 || moldExitTransitionIndex < 1 ||
        moldExitTransitionIndex >= curves->Length || !std::isfinite(connectionToleranceMm) ||
        connectionToleranceMm <= 0.0)
    {
        return CreateManagedConnectionObstacleResult(
            WorkingConnectionObstacleStatus::InvalidInput,
            nullptr,
            "연결 곡선 배열과 금형 이탈 구간 번호가 유효하지 않습니다.");
    }

    NativeDocumentSession& document = *candidate->second.document;
    auto context = document.workingContexts.find(contextId);
    if (document.modelRevision != revision || context == document.workingContexts.end() ||
        !context->second || context->second->revision != revision)
    {
        return CreateManagedConnectionObstacleResult(WorkingConnectionObstacleStatus::StaleContext);
    }

    try
    {
        std::vector<CurveDefinitionNative> nativeCurves;
        nativeCurves.reserve(static_cast<size_t>(curves->Length));
        for each (CurveDefinitionData^ curve in curves)
            nativeCurves.push_back(ReadNativeCurveDefinition(curve));

        const NativeConnectionObstacleMeasurement measured = CheckConnectionPathObstaclesNative(
            document,
            nativeCurves,
            moldExitTransitionIndex,
            connectionToleranceMm);
        return CreateManagedConnectionObstacleResult(
            MapConnectionObstacleStatus(measured.status),
            &measured);
    }
    catch (Exception^ exception)
    {
        return CreateManagedConnectionObstacleResult(
            WorkingConnectionObstacleStatus::InvalidInput,
            nullptr,
            exception->Message);
    }
    catch (...)
    {
        return CreateManagedConnectionObstacleResult(
            WorkingConnectionObstacleStatus::GeometryFailure,
            nullptr);
    }
}

