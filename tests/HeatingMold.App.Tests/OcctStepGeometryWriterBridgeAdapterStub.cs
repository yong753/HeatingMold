using System;
using System.Collections.Generic;
using System.Threading;
using HeatingMold.Application.Ports.Geometry;
using HeatingMold.Infrastructure.Export;

namespace HeatingMold.App.Geometry.Occt;

/// <summary>CompositionRoot 테스트에서 Native STEP 출력을 실행하지 않는 대역입니다.</summary>
internal sealed class OcctStepGeometryWriterBridgeAdapter : IStepGeometryWriterBridge
{
    /// <summary>Native 출력 없이 기능 실패 결과를 반환합니다.</summary>
    public StepGeometryWriteResult Write(
        string outputPath,
        ModelSource sourceModel,
        IReadOnlyList<StepDesignGroup> groups,
        bool includeSourceModel,
        CancellationToken cancellationToken = default) =>
        new(StepGeometryWriteStatus.GeometryFailure, "테스트에서 Native STEP writer를 호출할 수 없습니다.");
}
