bool DisplayWorkingPathSegmentNative(long long id,long long revision,int pieceId,const std::string& kind,
    const std::vector<double>& xyz,double* result)
{
    if (!FindWorking(id,revision) || g_ctx.IsNull() || xyz.size()<6 || xyz.size()%3 || !result) return false;
    // Negative IDs are private staging handles: collisions must never replace another candidate.
    if (pieceId<0 && (g_documentSession->presentation.pathSegments.count(pieceId) || g_documentSession->stagedWorkingPathSegmentOwners.count(pieceId))) return false;
    Handle(AIS_Shape) ais;
    try {
        BRepBuilderAPI_MakePolygon poly;
        for (size_t i=0;i<xyz.size();i+=3) {
            if (!std::isfinite(xyz[i]) || !std::isfinite(xyz[i+1]) || !std::isfinite(xyz[i+2])) return false;
            poly.Add(gp_Pnt(xyz[i],xyz[i+1],xyz[i+2]));
        }
        if (!poly.IsDone()) return false;
        GProp_GProps props; BRepGProp::LinearProperties(poly.Wire(),props);
        if (!std::isfinite(props.Mass()) || props.Mass()<=Precision::Confusion()) return false;
        result[0]=props.Mass(); result[1]=xyz[0]; result[2]=xyz[1]; result[3]=xyz[2];
        result[4]=xyz[xyz.size()-3]; result[5]=xyz[xyz.size()-2]; result[6]=xyz[xyz.size()-1];
        if (!g_ctx.IsNull()) {
            std::string k=kind; std::transform(k.begin(),k.end(),k.begin(),[](unsigned char c){return static_cast<char>(std::tolower(c));});
            const bool turnSegment=k=="turnsegment" || k=="turn";
            auto old=g_documentSession->presentation.pathSegments.find(pieceId);
            if (old!=g_documentSession->presentation.pathSegments.end()) { g_ctx->Remove(old->second.ais,Standard_False); g_documentSession->presentation.pathSegments.erase(old); }
            ais=new AIS_Shape(poly.Wire());
            Quantity_Color color=turnSegment ? Quantity_Color(0.1,0.65,1.0,Quantity_TOC_RGB) : Quantity_Color(0.95,0.15,0.10,Quantity_TOC_RGB);
            g_ctx->SetColor(ais,color,Standard_False);
            if (turnSegment) ais->Attributes()->LineAspect()->SetTypeOfLine(Aspect_TOL_DASH);
            g_ctx->Display(ais,Standard_False); g_ctx->SetZLayer(ais,Graphic3d_ZLayerId_Topmost); g_ctx->Activate(ais,Standard_False);
            if(pieceId<0) {
                auto path=g_documentSession->presentation.pathSegments.emplace(pieceId,HeatingPathSegmentOverlay{ais,turnSegment,true,color,1.0});
                if(!path.second) { g_ctx->Remove(ais,Standard_False); return false; }
                try {
                    auto owner=g_documentSession->stagedWorkingPathSegmentOwners.emplace(pieceId,std::make_pair(id,revision));
                    if(!owner.second) { g_documentSession->presentation.pathSegments.erase(path.first); g_ctx->Remove(ais,Standard_False); return false; }
                } catch(...) { g_documentSession->presentation.pathSegments.erase(pieceId); g_ctx->Remove(ais,Standard_False); throw; }
            } else g_documentSession->presentation.pathSegments[pieceId]={ais,turnSegment,true,color,1.0};
        }
        return true;
    } catch (...) { if (!ais.IsNull() && !g_ctx.IsNull()) g_ctx->Remove(ais,Standard_False); return false; }
}

bool StageWorkingPolylineNative(long long id,long long revision,int stageId,const std::vector<double>& xyz)
{
    if (!FindWorking(id,revision) || stageId>=0 || xyz.size()<6 || xyz.size()%3 || g_ctx.IsNull() ||
        g_documentSession->stagedWorkingPolylines.count(stageId) || g_documentSession->stagedWorkingPathSegmentOwners.count(stageId) || g_documentSession->presentation.pathSegments.count(stageId)) return false;
    Handle(AIS_Shape) ais;
    try {
        BRepBuilderAPI_MakePolygon poly;
        for (size_t i=0;i<xyz.size();i+=3) {
            if (!std::isfinite(xyz[i]) || !std::isfinite(xyz[i+1]) || !std::isfinite(xyz[i+2])) return false;
            poly.Add(gp_Pnt(xyz[i],xyz[i+1],xyz[i+2]));
        }
        if (!poly.IsDone()) return false;
        ais=new AIS_Shape(poly.Wire());
        g_ctx->SetColor(ais,Quantity_Color(0.95,0.15,0.10,Quantity_TOC_RGB),Standard_False);
        g_ctx->Display(ais,Standard_False); g_ctx->SetZLayer(ais,Graphic3d_ZLayerId_Topmost); g_ctx->Deactivate(ais,Standard_False);
        if(!g_documentSession->stagedWorkingPolylines.emplace(stageId,StagedWorkingPolyline{ais,id,revision}).second) { g_ctx->Remove(ais,Standard_False); return false; }
        return true;
    } catch (...) { if (!ais.IsNull() && !g_ctx.IsNull()) { try { g_ctx->Remove(ais,Standard_False); } catch (...) {} } g_documentSession->stagedWorkingPolylines.erase(stageId); return false; }
}

bool AllFinite(const std::vector<double>& values)
{
    return std::all_of(values.begin(), values.end(), [](double value) { return std::isfinite(value); });
}

bool HasOnlyCurvePayload(
    const CurveDefinitionNative& curve,
    bool hasStart,
    bool hasEnd,
    bool hasPoints,
    bool hasFrame,
    bool hasCenter,
    bool hasPoles,
    bool hasWeights,
    bool hasKnots,
    bool hasMultiplicities)
{
    return (hasStart || curve.start.empty()) && (hasEnd || curve.end.empty()) &&
        (hasPoints || curve.points.empty()) && (hasFrame || curve.frame.empty()) &&
        (hasCenter || curve.center.empty()) && (hasPoles || curve.poles.empty()) &&
        (hasWeights || curve.weights.empty()) && (hasKnots || curve.knots.empty()) &&
        (hasMultiplicities || curve.multiplicities.empty());
}

bool HasOnlyCurveParameters(const CurveDefinitionNative& curve,bool hasArcParameters,bool hasSplineParameters)
{
    const bool arcParametersPresent=curve.radiusMm!=0 || curve.startAngleRad!=0 || curve.endAngleRad!=0;
    const bool splineParametersPresent=curve.degree!=0 || curve.isPeriodic || curve.trimStart!=0 || curve.trimEnd!=0;
    return (hasArcParameters || !arcParametersPresent) &&
        (hasSplineParameters || !splineParametersPresent);
}

bool BuildCurveWireNative(const CurveDefinitionNative& definition, TopoDS_Wire& wire)
{
    if (definition.direction != "Forward" && definition.direction != "Reversed") return false;

    if (definition.kind == "Line")
    {
        if (!HasOnlyCurvePayload(definition, true, true, false, false, false, false, false, false, false) ||
            !HasOnlyCurveParameters(definition, false, false) ||
            definition.start.size() != 3 || definition.end.size() != 3 ||
            !AllFinite(definition.start) || !AllFinite(definition.end)) return false;

        BRepBuilderAPI_MakeEdge edgeBuilder(
            gp_Pnt(definition.start[0], definition.start[1], definition.start[2]),
            gp_Pnt(definition.end[0], definition.end[1], definition.end[2]));
        if (!edgeBuilder.IsDone()) return false;
        BRepBuilderAPI_MakeWire wireBuilder(edgeBuilder.Edge());
        if (!wireBuilder.IsDone()) return false;
        wire = wireBuilder.Wire();
    }
    else if (definition.kind == "Polyline")
    {
        if (!HasOnlyCurvePayload(definition, false, false, true, false, false, false, false, false, false) ||
            !HasOnlyCurveParameters(definition, false, false) ||
            definition.points.size() < 6 || definition.points.size() % 3 != 0 ||
            !AllFinite(definition.points)) return false;

        BRepBuilderAPI_MakePolygon polygon;
        for (size_t index = 0; index < definition.points.size(); index += 3)
            polygon.Add(gp_Pnt(definition.points[index], definition.points[index + 1], definition.points[index + 2]));
        if (!polygon.IsDone()) return false;
        wire = polygon.Wire();
    }
    else if (definition.kind == "Arc")
    {
        if (!HasOnlyCurvePayload(definition, false, false, false, true, true, false, false, false, false) ||
            !HasOnlyCurveParameters(definition, true, false) ||
            definition.frame.size() != 12 || definition.center.size() != 3 ||
            !AllFinite(definition.frame) || !AllFinite(definition.center) ||
            !std::isfinite(definition.radiusMm) || definition.radiusMm <= 0 ||
            !std::isfinite(definition.startAngleRad) || !std::isfinite(definition.endAngleRad) ||
            definition.startAngleRad == definition.endAngleRad) return false;

        const gp_Vec axisX(definition.frame[3], definition.frame[4], definition.frame[5]);
        const gp_Vec axisY(definition.frame[6], definition.frame[7], definition.frame[8]);
        const gp_Vec normal(definition.frame[9], definition.frame[10], definition.frame[11]);
        constexpr double frameTolerance = 1e-9;
        if (std::fabs(axisX.Magnitude() - 1) > frameTolerance ||
            std::fabs(axisY.Magnitude() - 1) > frameTolerance ||
            std::fabs(normal.Magnitude() - 1) > frameTolerance ||
            std::fabs(axisX.Dot(axisY)) > frameTolerance ||
            std::fabs(axisX.Dot(normal)) > frameTolerance ||
            std::fabs(axisY.Dot(normal)) > frameTolerance ||
            (normal.Crossed(axisX) - axisY).Magnitude() > frameTolerance) return false;

        const gp_Pnt center(definition.center[0], definition.center[1], definition.center[2]);
        const gp_Ax2 axes(center, gp_Dir(normal), gp_Dir(axisX));
        const gp_Circ circle(axes, definition.radiusMm);
        BRepBuilderAPI_MakeEdge edgeBuilder(circle, definition.startAngleRad, definition.endAngleRad);
        if (!edgeBuilder.IsDone()) return false;
        BRepBuilderAPI_MakeWire wireBuilder(edgeBuilder.Edge());
        if (!wireBuilder.IsDone()) return false;
        wire = wireBuilder.Wire();
    }
    else if (definition.kind == "BSpline")
    {
        if (!HasOnlyCurvePayload(definition, false, false, false, false, false, true, true, true, true) ||
            !HasOnlyCurveParameters(definition, false, true) ||
            definition.poles.size() < 6 || definition.poles.size() % 3 != 0 ||
            !AllFinite(definition.poles) || !AllFinite(definition.weights) || !AllFinite(definition.knots) ||
            definition.degree < 1 || definition.degree > Geom_BSplineCurve::MaxDegree() ||
            definition.degree >= static_cast<int>(definition.poles.size() / 3) ||
            definition.weights.size() != definition.poles.size() / 3 ||
            definition.knots.size() < 2 || definition.knots.size() != definition.multiplicities.size() ||
            !std::isfinite(definition.trimStart) || !std::isfinite(definition.trimEnd) ||
            definition.trimStart < definition.knots.front() || definition.trimEnd > definition.knots.back() ||
            definition.trimStart >= definition.trimEnd) return false;

        for (double weight : definition.weights)
            if (weight <= gp::Resolution()) return false;
        for (size_t index = 1; index < definition.knots.size(); ++index)
            if (definition.knots[index - 1] >= definition.knots[index]) return false;

        long long multiplicityTotal = 0;
        for (size_t index = 0; index < definition.multiplicities.size(); ++index)
        {
            const int multiplicity = definition.multiplicities[index];
            if (multiplicity < 1 || multiplicity > definition.degree + 1) return false;
            if (index > 0 && index + 1 < definition.multiplicities.size() && multiplicity > definition.degree)
                return false;
            multiplicityTotal += multiplicity;
        }

        const int poleCount = static_cast<int>(definition.poles.size() / 3);
        long long expectedMultiplicity = 0;
        if (definition.isPeriodic)
        {
            if (definition.multiplicities.front() > definition.degree ||
                definition.multiplicities.front() != definition.multiplicities.back()) return false;
            expectedMultiplicity = static_cast<long long>(poleCount) + definition.multiplicities.front();
        }
        else
        {
            expectedMultiplicity = static_cast<long long>(poleCount) + definition.degree + 1;
        }
        if (multiplicityTotal != expectedMultiplicity) return false;

        TColgp_Array1OfPnt poles(1, poleCount);
        TColStd_Array1OfReal weights(1, static_cast<Standard_Integer>(definition.weights.size()));
        for (int index = 0; index < poleCount; ++index)
        {
            const size_t offset = static_cast<size_t>(index) * 3;
            poles.SetValue(index + 1, gp_Pnt(
                definition.poles[offset], definition.poles[offset + 1], definition.poles[offset + 2]));
            weights.SetValue(index + 1, definition.weights[index]);
        }

        const Standard_Integer knotCount = static_cast<Standard_Integer>(definition.knots.size());
        TColStd_Array1OfReal knots(1, knotCount);
        TColStd_Array1OfInteger multiplicities(1, knotCount);
        for (Standard_Integer index = 0; index < knotCount; ++index)
        {
            knots.SetValue(index + 1, definition.knots[index]);
            multiplicities.SetValue(index + 1, definition.multiplicities[index]);
        }

        Handle(Geom_BSplineCurve) curve = new Geom_BSplineCurve(
            poles,
            weights,
            knots,
            multiplicities,
            definition.degree,
            definition.isPeriodic,
            Standard_True);
        BRepBuilderAPI_MakeEdge edgeBuilder(curve, definition.trimStart, definition.trimEnd);
        if (!edgeBuilder.IsDone()) return false;
        BRepBuilderAPI_MakeWire wireBuilder(edgeBuilder.Edge());
        if (!wireBuilder.IsDone()) return false;
        wire = wireBuilder.Wire();
    }
    else
    {
        return false;
    }

    if (wire.IsNull() || !BRepCheck_Analyzer(wire).IsValid()) return false;
    if (definition.direction == "Reversed") wire.Reverse();
    return true;
}

