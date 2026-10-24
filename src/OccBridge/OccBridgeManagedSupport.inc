namespace HeatingMold { namespace OccBridge {

namespace
{
    FaceGeometryFeaturesResult^ CreateFaceGeometryFeaturesResult(
        FaceFeatureQueryStatus status,
        const std::vector<FaceGeometryFeatureNative>& nativeFeatures)
    {
        auto result = gcnew FaceGeometryFeaturesResult();
        result->Status = status;
        if (status != FaceFeatureQueryStatus::Success)
        {
            result->Features = gcnew array<FaceGeometryFeatureData^>(0);
            return result;
        }

        auto features = gcnew array<FaceGeometryFeatureData^>(static_cast<int>(nativeFeatures.size()));
        for (int featureIndex = 0; featureIndex < features->Length; ++featureIndex)
        {
            const FaceGeometryFeatureNative& nativeFeature = nativeFeatures[static_cast<size_t>(featureIndex)];
            auto feature = gcnew FaceGeometryFeatureData();
            feature->FaceIndex = nativeFeature.faceIndex;
            feature->SurfaceType = gcnew String(nativeFeature.surfaceType.c_str());
            feature->SurfaceAreaMm2 = nativeFeature.surfaceAreaMm2;
            feature->Center = gcnew array<double>{
                nativeFeature.center.X(),
                nativeFeature.center.Y(),
                nativeFeature.center.Z()};

            auto adjacentFaces = gcnew array<int>(static_cast<int>(nativeFeature.adjacentFaceIndices.size()));
            for (int adjacentIndex = 0; adjacentIndex < adjacentFaces->Length; ++adjacentIndex)
                adjacentFaces[adjacentIndex] = nativeFeature.adjacentFaceIndices[static_cast<size_t>(adjacentIndex)];
            feature->AdjacentFaceIndices = adjacentFaces;
            features[featureIndex] = feature;
        }

        result->Features = features;
        return result;
    }

    array<UvWire^>^ CreateManagedUvWires(const std::vector<NativeWire>& nativeWires)
    {
        auto wires = gcnew array<UvWire^>(static_cast<int>(nativeWires.size()));
        for (int wireIndex = 0; wireIndex < wires->Length; ++wireIndex)
        {
            const NativeWire& nativeWire = nativeWires[static_cast<size_t>(wireIndex)];
            auto wire = gcnew UvWire();
            wire->IsOuter = nativeWire.isOuter;
            wire->Closed = nativeWire.closed;

            auto points = gcnew array<double>(static_cast<int>(nativeWire.pts.size()));
            for (int pointIndex = 0; pointIndex < points->Length; ++pointIndex)
                points[pointIndex] = nativeWire.pts[static_cast<size_t>(pointIndex)];
            wire->Points = points;
            wires[wireIndex] = wire;
        }

        return wires;
    }

    WorkingSelectedTopology^ CreateManagedSelectedTopology(
        const WorkingSelectedTopologyNative& nativeResult)
    {
        auto result = gcnew WorkingSelectedTopology();
        switch (nativeResult.status)
        {
        case WorkingTopologyStatusNative::Success:
            result->Status = WorkingTopologyQueryStatus::Success;
            break;
        case WorkingTopologyStatusNative::InvalidInput:
            result->Status = WorkingTopologyQueryStatus::InvalidInput;
            break;
        case WorkingTopologyStatusNative::InvalidFace:
            result->Status = WorkingTopologyQueryStatus::InvalidFace;
            break;
        case WorkingTopologyStatusNative::StaleContext:
            result->Status = WorkingTopologyQueryStatus::StaleContext;
            break;
        case WorkingTopologyStatusNative::InvalidTopology:
            result->Status = WorkingTopologyQueryStatus::InvalidTopology;
            break;
        case WorkingTopologyStatusNative::GeometryFailure:
            result->Status = WorkingTopologyQueryStatus::GeometryFailure;
            break;
        default:
            result->Status = WorkingTopologyQueryStatus::Unknown;
            break;
        }

        result->Components = gcnew array<WorkingTopologyComponent^>(
            static_cast<int>(nativeResult.components.size()));
        for (int componentIndex = 0; componentIndex < result->Components->Length; ++componentIndex)
        {
            const WorkingTopologyComponentNative& nativeComponent =
                nativeResult.components[static_cast<size_t>(componentIndex)];
            auto component = gcnew WorkingTopologyComponent();
            component->FaceIndices = gcnew array<int>(static_cast<int>(nativeComponent.faceIndices.size()));
            for (int faceIndex = 0; faceIndex < component->FaceIndices->Length; ++faceIndex)
                component->FaceIndices[faceIndex] = nativeComponent.faceIndices[static_cast<size_t>(faceIndex)];

            component->BoundaryLoops = gcnew array<WorkingTopologyBoundaryLoop^>(
                static_cast<int>(nativeComponent.boundaryLoops.size()));
            for (int loopIndex = 0; loopIndex < component->BoundaryLoops->Length; ++loopIndex)
            {
                const WorkingTopologyBoundaryLoopNative& nativeLoop =
                    nativeComponent.boundaryLoops[static_cast<size_t>(loopIndex)];
                auto loop = gcnew WorkingTopologyBoundaryLoop();
                loop->Points = gcnew array<double>(static_cast<int>(nativeLoop.points.size()));
                for (int valueIndex = 0; valueIndex < loop->Points->Length; ++valueIndex)
                    loop->Points[valueIndex] = nativeLoop.points[static_cast<size_t>(valueIndex)];
                loop->SegmentFaceIndices = gcnew array<int>(
                    static_cast<int>(nativeLoop.segmentFaceIndices.size()));
                for (int segmentIndex = 0; segmentIndex < loop->SegmentFaceIndices->Length; ++segmentIndex)
                    loop->SegmentFaceIndices[segmentIndex] =
                        nativeLoop.segmentFaceIndices[static_cast<size_t>(segmentIndex)];
                loop->IsClosed = true;
                component->BoundaryLoops[loopIndex] = loop;
            }
            result->Components[componentIndex] = component;
        }

        result->Adjacencies = gcnew array<WorkingTopologyAdjacency^>(
            static_cast<int>(nativeResult.adjacencies.size()));
        for (int adjacencyIndex = 0; adjacencyIndex < result->Adjacencies->Length; ++adjacencyIndex)
        {
            const WorkingTopologyAdjacencyNative& nativeAdjacency =
                nativeResult.adjacencies[static_cast<size_t>(adjacencyIndex)];
            auto adjacency = gcnew WorkingTopologyAdjacency();
            adjacency->FirstFaceIndex = nativeAdjacency.firstFaceIndex;
            adjacency->SecondFaceIndex = nativeAdjacency.secondFaceIndex;
            result->Adjacencies[adjacencyIndex] = adjacency;
        }
        return result;
    }

    WorkingSurfaceElementsResult^ CreateManagedSurfaceElements(
        const WorkingSurfaceElementsNative& nativeResult)
    {
        auto result = gcnew WorkingSurfaceElementsResult();
        switch (nativeResult.status)
        {
        case WorkingSurfaceElementsStatusNative::Success:
            result->Status = WorkingSurfaceElementQueryStatus::Success;
            break;
        case WorkingSurfaceElementsStatusNative::InvalidInput:
            result->Status = WorkingSurfaceElementQueryStatus::InvalidInput;
            break;
        case WorkingSurfaceElementsStatusNative::InvalidFace:
            result->Status = WorkingSurfaceElementQueryStatus::InvalidFace;
            break;
        case WorkingSurfaceElementsStatusNative::StaleContext:
            result->Status = WorkingSurfaceElementQueryStatus::StaleContext;
            break;
        case WorkingSurfaceElementsStatusNative::InvalidRegion:
            result->Status = WorkingSurfaceElementQueryStatus::InvalidRegion;
            break;
        case WorkingSurfaceElementsStatusNative::ElementBudgetExceeded:
            result->Status = WorkingSurfaceElementQueryStatus::ElementBudgetExceeded;
            break;
        case WorkingSurfaceElementsStatusNative::GeometryFailure:
            result->Status = WorkingSurfaceElementQueryStatus::GeometryFailure;
            break;
        default:
            result->Status = WorkingSurfaceElementQueryStatus::Unknown;
            break;
        }

        result->AreaErrorBoundMm2 = nativeResult.areaErrorBoundMm2;
        result->Elements = gcnew array<WorkingSurfaceElementData^>(
            static_cast<int>(nativeResult.elements.size()));
        for (int elementIndex = 0; elementIndex < result->Elements->Length; ++elementIndex)
        {
            const WorkingSurfaceElementNative& nativeElement =
                nativeResult.elements[static_cast<size_t>(elementIndex)];
            auto element = gcnew WorkingSurfaceElementData();
            element->FaceIndex = nativeElement.faceIndex;
            element->SurfaceAreaMm2 = nativeElement.surfaceAreaMm2;
            element->ModelCentroid = gcnew array<double>(
                static_cast<int>(nativeElement.modelCentroid.size()));
            for (int valueIndex = 0; valueIndex < element->ModelCentroid->Length; ++valueIndex)
                element->ModelCentroid[valueIndex] = nativeElement.modelCentroid[static_cast<size_t>(valueIndex)];
            element->FaceUvCentroid = gcnew array<double>(
                static_cast<int>(nativeElement.faceUvCentroid.size()));
            for (int valueIndex = 0; valueIndex < element->FaceUvCentroid->Length; ++valueIndex)
                element->FaceUvCentroid[valueIndex] = nativeElement.faceUvCentroid[static_cast<size_t>(valueIndex)];
            element->WorkingUvCentroid = gcnew array<double>(
                static_cast<int>(nativeElement.workingUvCentroid.size()));
            for (int valueIndex = 0; valueIndex < element->WorkingUvCentroid->Length; ++valueIndex)
                element->WorkingUvCentroid[valueIndex] = nativeElement.workingUvCentroid[static_cast<size_t>(valueIndex)];
            element->ModelVertices = gcnew array<double>(
                static_cast<int>(nativeElement.modelVertices.size()));
            for (int valueIndex = 0; valueIndex < element->ModelVertices->Length; ++valueIndex)
                element->ModelVertices[valueIndex] = nativeElement.modelVertices[static_cast<size_t>(valueIndex)];
            element->MaximumMeasuredDeviationMm = nativeElement.maximumMeasuredDeviationMm;
            result->Elements[elementIndex] = element;
        }
        return result;
    }

    WorkingSurfaceElementsResult^ QuerySurfaceElementsForDocument(
        NativeDocumentSession& document,
        long long contextId,
        long long revision,
        array<int>^ faceIds,
        array<WorkingRegionLoopData^>^ effectiveBoundary,
        double deflectionMm,
        double angularToleranceRad)
    {
        WorkingSurfaceElementsNative failure;
        failure.status = WorkingSurfaceElementsStatusNative::InvalidInput;
        if (faceIds == nullptr || effectiveBoundary == nullptr)
            return CreateManagedSurfaceElements(failure);

        std::vector<int> nativeFaceIndices;
        nativeFaceIndices.reserve(static_cast<size_t>(faceIds->Length));
        for each (int faceIndex in faceIds)
            nativeFaceIndices.push_back(faceIndex);

        std::vector<WorkingRegionLoopNative> nativeBoundary;
        nativeBoundary.reserve(static_cast<size_t>(effectiveBoundary->Length));
        for each (WorkingRegionLoopData^ loop in effectiveBoundary)
        {
            if (loop == nullptr || loop->Points == nullptr)
                return CreateManagedSurfaceElements(failure);

            WorkingRegionLoopNative nativeLoop;
            nativeLoop.isHole = loop->IsHole;
            nativeLoop.points.reserve(static_cast<size_t>(loop->Points->Length));
            for each (double coordinate in loop->Points)
                nativeLoop.points.push_back(coordinate);
            nativeBoundary.push_back(std::move(nativeLoop));
        }

        const WorkingSurfaceElementsNative nativeResult = QueryWorkingSurfaceElementsNative(
            document,
            contextId,
            revision,
            nativeFaceIndices,
            nativeBoundary,
            deflectionMm,
            angularToleranceRad);
        return CreateManagedSurfaceElements(nativeResult);
    }

    CurveDefinitionNative ReadNativeCurveDefinition(CurveDefinitionData^ definition)
    {
        if (definition == nullptr || definition->Kind == nullptr || definition->Direction == nullptr)
            throw gcnew ArgumentException("곡선 종류와 진행 방향이 필요합니다.");

        CurveDefinitionNative curve;
        curve.kind = msclr::interop::marshal_as<std::string>(definition->Kind);
        curve.direction = msclr::interop::marshal_as<std::string>(definition->Direction);
        if (definition->Start != nullptr)
            for each (double value in definition->Start) curve.start.push_back(value);
        if (definition->End != nullptr)
            for each (double value in definition->End) curve.end.push_back(value);
        if (definition->Points != nullptr)
            for each (double value in definition->Points) curve.points.push_back(value);
        if (definition->Frame != nullptr)
            for each (double value in definition->Frame) curve.frame.push_back(value);
        if (definition->Center != nullptr)
            for each (double value in definition->Center) curve.center.push_back(value);

        curve.radiusMm = definition->RadiusMm;
        curve.startAngleRad = definition->StartAngleRad;
        curve.endAngleRad = definition->EndAngleRad;
        curve.degree = definition->Degree;
        if (definition->Poles != nullptr)
            for each (double value in definition->Poles) curve.poles.push_back(value);
        if (definition->Weights != nullptr)
            for each (double value in definition->Weights) curve.weights.push_back(value);
        if (definition->Knots != nullptr)
            for each (double value in definition->Knots) curve.knots.push_back(value);
        if (definition->Multiplicities != nullptr)
            for each (int value in definition->Multiplicities) curve.multiplicities.push_back(value);
        curve.isPeriodic = definition->IsPeriodic;
        curve.trimStart = definition->TrimStart;
        curve.trimEnd = definition->TrimEnd;
        return curve;
    }

    CurveClearanceResultData^ CreateManagedCurveClearanceResult(
        WorkingCurveClearanceStatus status,
        const NativeCurveClearanceMeasurement* measurement = nullptr,
        String^ diagnostic = nullptr)
    {
        auto result = gcnew CurveClearanceResultData();
        result->Status = status;
        result->Diagnostic = diagnostic;
        if (status == WorkingCurveClearanceStatus::Success && measurement != nullptr)
        {
            result->MinimumCenterlineDistanceMm = measurement->minimumCenterlineDistanceMm;
            result->FirstLocation = gcnew array<double>{
                measurement->firstLocation.X(), measurement->firstLocation.Y(), measurement->firstLocation.Z()};
            result->SecondLocation = gcnew array<double>{
                measurement->secondLocation.X(), measurement->secondLocation.Y(), measurement->secondLocation.Z()};
            result->ErrorBoundMm = measurement->errorBoundMm;
            return result;
        }

        switch (status)
        {
        case WorkingCurveClearanceStatus::InvalidInput:
            result->Diagnostic = diagnostic == nullptr ? "곡선 정의나 간격 설정이 유효하지 않습니다." : diagnostic;
            break;
        case WorkingCurveClearanceStatus::StaleContext:
            result->Diagnostic = diagnostic == nullptr ? "작업 좌표계가 현재 모델 revision에 속하지 않습니다." : diagnostic;
            break;
        case WorkingCurveClearanceStatus::CandidateNotFound:
            result->Diagnostic = diagnostic == nullptr ? "준비 후보 문서를 찾을 수 없습니다." : diagnostic;
            break;
        case WorkingCurveClearanceStatus::PairBudgetExceeded:
            result->Diagnostic = diagnostic == nullptr ? "곡선 쌍 거리 계산 한도를 넘었습니다." : diagnostic;
            break;
        case WorkingCurveClearanceStatus::GeometryFailure:
            result->Diagnostic = diagnostic == nullptr ? "OCCT 곡선 간격 계산에 실패했습니다." : diagnostic;
            break;
        case WorkingCurveClearanceStatus::Unresolved:
            result->Diagnostic = diagnostic == nullptr ? "비국소 거리나 수치 정밀도를 확정하지 못했습니다." : diagnostic;
            break;
        default:
            result->Status = WorkingCurveClearanceStatus::Unknown;
            result->Diagnostic = diagnostic == nullptr ? "알 수 없는 Native 곡선 간격 상태입니다." : diagnostic;
            break;
        }
        return result;
    }

    WorkingCurveDepthEnvelopeStatus MapCurveDepthEnvelopeStatus(CurveDepthEnvelopeStatusNative status)
    {
        switch (status)
        {
        case CurveDepthEnvelopeStatusNative::Success: return WorkingCurveDepthEnvelopeStatus::Success;
        case CurveDepthEnvelopeStatusNative::InvalidInput: return WorkingCurveDepthEnvelopeStatus::InvalidInput;
        case CurveDepthEnvelopeStatusNative::InvalidFace: return WorkingCurveDepthEnvelopeStatus::InvalidFace;
        case CurveDepthEnvelopeStatusNative::CandidateNotFound: return WorkingCurveDepthEnvelopeStatus::CandidateNotFound;
        case CurveDepthEnvelopeStatusNative::StaleContext: return WorkingCurveDepthEnvelopeStatus::StaleContext;
        case CurveDepthEnvelopeStatusNative::Miss: return WorkingCurveDepthEnvelopeStatus::Miss;
        case CurveDepthEnvelopeStatusNative::Tangent: return WorkingCurveDepthEnvelopeStatus::Tangent;
        case CurveDepthEnvelopeStatusNative::ProbeBudgetExceeded: return WorkingCurveDepthEnvelopeStatus::ProbeBudgetExceeded;
        case CurveDepthEnvelopeStatusNative::GeometryFailure: return WorkingCurveDepthEnvelopeStatus::GeometryFailure;
        case CurveDepthEnvelopeStatusNative::Unresolved: return WorkingCurveDepthEnvelopeStatus::Unresolved;
        default: return WorkingCurveDepthEnvelopeStatus::Unknown;
        }
    }

    CurveDepthEnvelopeResultData^ CreateManagedCurveDepthEnvelopeResult(
        const NativeCurveDepthEnvelopeMeasurement& measurement)
    {
        auto result = gcnew CurveDepthEnvelopeResultData();
        result->Status = MapCurveDepthEnvelopeStatus(measurement.status);
        result->MeasuredInsertionDepthMm = measurement.measuredDepthMm;
        result->MinimumInsertionDepthMm = measurement.minimumDepthMm;
        result->MaximumInsertionDepthMm = measurement.maximumDepthMm;
        result->ErrorBoundMm = measurement.errorBoundMm;
        result->SurfacePoint = gcnew array<double>(0);
        result->MinimumSurfacePoint = gcnew array<double>(0);
        result->MaximumSurfacePoint = gcnew array<double>(0);
        result->MinimumWireOutsidePoint = gcnew array<double>(0);
        result->MaximumWireOutsidePoint = gcnew array<double>(0);
        result->ProbeCount = measurement.probeCount;
        result->Diagnostic = measurement.diagnostic.empty()
            ? nullptr
            : gcnew String(measurement.diagnostic.c_str());

        if (measurement.status == CurveDepthEnvelopeStatusNative::Success)
        {
            result->SurfacePoint = gcnew array<double>{
                measurement.surfacePoint.X(), measurement.surfacePoint.Y(), measurement.surfacePoint.Z()};
            result->MinimumSurfacePoint = gcnew array<double>{
                measurement.minimumSurfacePoint.X(),
                measurement.minimumSurfacePoint.Y(),
                measurement.minimumSurfacePoint.Z()};
            result->MaximumSurfacePoint = gcnew array<double>{
                measurement.maximumSurfacePoint.X(),
                measurement.maximumSurfacePoint.Y(),
                measurement.maximumSurfacePoint.Z()};
            result->MinimumWireOutsidePoint = gcnew array<double>{
                measurement.minimumWireOutsidePoint.X(),
                measurement.minimumWireOutsidePoint.Y(),
                measurement.minimumWireOutsidePoint.Z()};
            result->MaximumWireOutsidePoint = gcnew array<double>{
                measurement.maximumWireOutsidePoint.X(),
                measurement.maximumWireOutsidePoint.Y(),
                measurement.maximumWireOutsidePoint.Z()};
            return result;
        }

        if (result->Diagnostic != nullptr)
            return result;
        switch (result->Status)
        {
        case WorkingCurveDepthEnvelopeStatus::InvalidInput:
            result->Diagnostic = "곡선 또는 깊이 측정 설정이 유효하지 않습니다.";
            break;
        case WorkingCurveDepthEnvelopeStatus::InvalidFace:
            result->Diagnostic = "선택한 Native Face 번호가 현재 문서에 없습니다.";
            break;
        case WorkingCurveDepthEnvelopeStatus::CandidateNotFound:
            result->Diagnostic = "준비 후보 문서를 찾을 수 없습니다.";
            break;
        case WorkingCurveDepthEnvelopeStatus::StaleContext:
            result->Diagnostic = "깊이 질의의 작업 좌표계가 현재 모델 revision에 속하지 않습니다.";
            break;
        case WorkingCurveDepthEnvelopeStatus::Miss:
            result->Diagnostic = "깊이 표본에서 선택된 표면 first-hit을 찾지 못했습니다.";
            break;
        case WorkingCurveDepthEnvelopeStatus::Tangent:
            result->Diagnostic = "깊이 표본의 작업 광선이 표면에 접합니다.";
            break;
        case WorkingCurveDepthEnvelopeStatus::ProbeBudgetExceeded:
            result->Diagnostic = "깊이 표면 질의가 설정된 probe 한도를 넘었습니다.";
            break;
        case WorkingCurveDepthEnvelopeStatus::GeometryFailure:
            result->Diagnostic = "OCCT 깊이 표면 교차 계산에 실패했습니다.";
            break;
        case WorkingCurveDepthEnvelopeStatus::Unresolved:
            result->Diagnostic = "곡선 깊이 범위를 현재 표본과 오차 근거로 확정하지 못했습니다.";
            break;
        default:
            result->Status = WorkingCurveDepthEnvelopeStatus::Unknown;
            result->Diagnostic = "알 수 없는 Native 곡선 깊이 상태입니다.";
            break;
        }
        return result;
    }

    CurveDepthAdjustmentResultData^ CreateManagedCurveDepthAdjustmentResult(
        const NativeCurveDepthAdjustment& adjustment)
    {
        auto result = gcnew CurveDepthAdjustmentResultData();
        result->Status = MapCurveDepthEnvelopeStatus(adjustment.status);
        result->CenterlineSamples = gcnew array<double>(0);
        result->MaximumDepthResidualMm = adjustment.maximumDepthResidualMm;
        result->ErrorBoundMm = adjustment.errorBoundMm;
        result->ProbeCount = adjustment.probeCount;
        result->Diagnostic = adjustment.diagnostic.empty()
            ? nullptr
            : gcnew String(adjustment.diagnostic.c_str());

        if (adjustment.status == CurveDepthEnvelopeStatusNative::Success)
        {
            result->CenterlineSamples = gcnew array<double>(static_cast<int>(adjustment.centerlineSamples.size()));
            for (size_t index = 0; index < adjustment.centerlineSamples.size(); ++index)
                result->CenterlineSamples[static_cast<int>(index)] = adjustment.centerlineSamples[index];
            return result;
        }

        if (result->Diagnostic == nullptr)
        {
            result->Diagnostic = adjustment.status == CurveDepthEnvelopeStatusNative::ProbeBudgetExceeded
                ? "깊이 경로 보정이 설정된 표면 질의 한도를 넘었습니다."
                : adjustment.status == CurveDepthEnvelopeStatusNative::Unresolved
                    ? "깊이 root 보정 또는 도체 외피 검사를 확정하지 못했습니다."
                    : "Native 깊이 경로 보정이 완료되지 않았습니다.";
        }
        return result;
    }

    String^ DescribeCurveClearanceUnresolvedReason(const NativeCurveClearanceMeasurement& measurement)
    {
        switch (measurement.unresolvedReason)
        {
        case NativeCurveClearanceUnresolvedReason::BSplineSelfContactNotCertified:
            return "B-spline 곡선 내부의 비국소 자기접촉을 아직 증명하지 못했습니다.";
        case NativeCurveClearanceUnresolvedReason::CurvedEndpointContactNotCertified:
            return "원호 끝점의 국소 접촉과 추가 교차를 구분하지 못했습니다.";
        case NativeCurveClearanceUnresolvedReason::UnclassifiedCurveEndpointContact:
            return "서로 다른 곡선의 공유 끝점 연결 관계가 없어 접촉을 안전하게 분류할 수 없습니다.";
        case NativeCurveClearanceUnresolvedReason::DeclaredCurvePairHasUncertifiedContact:
            return "연결된 두 곡선에서 지정 접점 이외의 추가 접촉을 배제할 수 없습니다.";
        case NativeCurveClearanceUnresolvedReason::DeclaredCurveEndpointsDoNotMeet:
            return "설계에서 연속으로 지정한 곡선 끝점이 허용 오차 안에서 만나지 않습니다.";
        case NativeCurveClearanceUnresolvedReason::RepeatedArcSweepNotCertified:
            return "한 바퀴를 넘게 반복되는 원호 구간의 자기접촉을 확정하지 못했습니다.";
        case NativeCurveClearanceUnresolvedReason::NoNonlocalCurvePair:
            return "국소 공유 접점을 제외하면 비교할 곡선 쌍이 없습니다.";
        case NativeCurveClearanceUnresolvedReason::NumericalErrorExceedsRequestedBound:
            return "곡선 거리 오차 추정치가 요청한 한도를 넘었습니다.";
        default:
            return nullptr;
        }
    }

    WorkingCurveClearanceStatus MapCurveClearanceStatus(NativeCurveClearanceStatus status)
    {
        switch (status)
        {
        case NativeCurveClearanceStatus::Success:
            return WorkingCurveClearanceStatus::Success;
        case NativeCurveClearanceStatus::InvalidInput:
            return WorkingCurveClearanceStatus::InvalidInput;
        case NativeCurveClearanceStatus::StaleContext:
            return WorkingCurveClearanceStatus::StaleContext;
        case NativeCurveClearanceStatus::PairBudgetExceeded:
            return WorkingCurveClearanceStatus::PairBudgetExceeded;
        case NativeCurveClearanceStatus::GeometryFailure:
            return WorkingCurveClearanceStatus::GeometryFailure;
        case NativeCurveClearanceStatus::Unresolved:
            return WorkingCurveClearanceStatus::Unresolved;
        default:
            return WorkingCurveClearanceStatus::Unknown;
        }
    }

    WorkingConnectionObstacleStatus MapConnectionObstacleStatus(NativeConnectionObstacleStatus status)
    {
        switch (status)
        {
        case NativeConnectionObstacleStatus::Clear:
            return WorkingConnectionObstacleStatus::Clear;
        case NativeConnectionObstacleStatus::Obstructed:
            return WorkingConnectionObstacleStatus::Obstructed;
        case NativeConnectionObstacleStatus::InvalidInput:
            return WorkingConnectionObstacleStatus::InvalidInput;
        case NativeConnectionObstacleStatus::StaleContext:
            return WorkingConnectionObstacleStatus::StaleContext;
        case NativeConnectionObstacleStatus::GeometryFailure:
            return WorkingConnectionObstacleStatus::GeometryFailure;
        case NativeConnectionObstacleStatus::Unresolved:
            return WorkingConnectionObstacleStatus::Unresolved;
        default:
            return WorkingConnectionObstacleStatus::Unknown;
        }
    }

    ConnectionObstacleResultData^ CreateManagedConnectionObstacleResult(
        WorkingConnectionObstacleStatus status,
        const NativeConnectionObstacleMeasurement* measurement = nullptr,
        String^ diagnostic = nullptr)
    {
        auto result = gcnew ConnectionObstacleResultData();
        result->Status = status;
        result->FirstObstruction = gcnew array<double>(0);
        result->DistanceToObstructionMm = 0.0;
        result->Diagnostic = diagnostic;
        if (status == WorkingConnectionObstacleStatus::Clear)
            return result;

        if (status == WorkingConnectionObstacleStatus::Obstructed && measurement != nullptr)
        {
            result->FirstObstruction = gcnew array<double>{
                measurement->firstObstruction.X(),
                measurement->firstObstruction.Y(),
                measurement->firstObstruction.Z()};
            result->DistanceToObstructionMm = measurement->distanceToObstructionMm;
            return result;
        }

        if (diagnostic == nullptr && measurement != nullptr && !measurement->diagnostic.empty())
            result->Diagnostic = gcnew String(measurement->diagnostic.c_str());
        if (result->Diagnostic == nullptr)
        {
            switch (status)
            {
            case WorkingConnectionObstacleStatus::InvalidInput:
                result->Diagnostic = "연결 경로 곡선이나 금형 이탈 설정이 유효하지 않습니다.";
                break;
            case WorkingConnectionObstacleStatus::StaleContext:
                result->Diagnostic = "연결 경로 질의의 작업 좌표계가 만료되었습니다.";
                break;
            case WorkingConnectionObstacleStatus::CandidateNotFound:
                result->Diagnostic = "준비 후보 문서를 찾을 수 없습니다.";
                break;
            case WorkingConnectionObstacleStatus::GeometryFailure:
                result->Diagnostic = "OCCT 연결 경로 충돌 연산에 실패했습니다.";
                break;
            case WorkingConnectionObstacleStatus::Unresolved:
                result->Diagnostic = "연결 경로의 금형 이탈 또는 전체 외부 경로를 확정하지 못했습니다.";
                break;
            default:
                result->Status = WorkingConnectionObstacleStatus::Unknown;
                result->Diagnostic = "알 수 없는 Native 연결 경로 상태입니다.";
                break;
            }
        }
        return result;
    }

    StepDesignWriteResult^ MakeStepDesignWriteResult(int status, const std::string& diagnostic)
    {
        auto result = gcnew StepDesignWriteResult();
        result->Status = static_cast<StepDesignWriteStatus>(status);
        result->Diagnostic = diagnostic.empty() ? nullptr : gcnew String(diagnostic.c_str());
        return result;
    }
}

