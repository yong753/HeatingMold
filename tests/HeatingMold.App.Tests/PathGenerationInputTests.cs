using System;
using System.Collections.Generic;
using System.Reflection;
using HeatingMold.App;
using HeatingMold.Core.Circuits;
using HeatingMold.Core.Geometry;
using HeatingMold.Core.Primitives;
using HeatingMold.Design.ConnectionPath;

namespace HeatingMold.App.Tests;

/// <summary>경로 생성 화면 입력을 검증하고 연결 경로 구간 계약을 확인합니다.</summary>
[Collection("WPF Bridge")]
public sealed class PathGenerationInputTests
{
    /// <summary>연결 경로가 금형 위 연장 뒤에 외부 인출 전환을 둡니다.</summary>
    [Fact]
    public void CreateConnectionLegs_UsesHeatingExtensionThenExternalConnection()
    {
        SurfaceAnchor exitAnchor = new(
            FaceRefId.New(),
            new Vec2(0.25, 0.5),
            new Vec2(10, 20),
            new Vec3(10, 20, 30));
        Vec3 externalPoint = new(40, 50, 60);

        IReadOnlyList<ConnectionPathLegRequest> legs = (IReadOnlyList<ConnectionPathLegRequest>)
            InvokePrivate(
                null,
                "CreateConnectionLegs",
                new object[]
                {
                    exitAnchor,
                    externalPoint,
                    new LengthMm(1.5),
                    new LengthMm(2.5)
                });

        Assert.Equal(2, legs.Count);
        Assert.Equal(exitAnchor.ModelPoint, legs[0].EndPoint);
        Assert.Equal(GeometryDomain.OnMold, legs[0].GeometryDomain);
        Assert.Equal(ConductorRole.HeatingWireExtension, legs[0].ConductorRole);
        Assert.Equal(1.5, legs[0].AdditionalAllowanceMm.Value);
        Assert.Equal(externalPoint, legs[1].EndPoint);
        Assert.Equal(GeometryDomain.External, legs[1].GeometryDomain);
        Assert.Equal(ConductorRole.SeparateConnectionWire, legs[1].ConductorRole);
        Assert.True(legs[1].IsMoldExitTransition);
        Assert.Equal(2.5, legs[1].AdditionalAllowanceMm.Value);
    }

    private static object InvokePrivate(object target, string methodName, object[] arguments)
    {
        MethodInfo method = typeof(MainWindow).GetMethod(
            methodName,
            BindingFlags.Instance | BindingFlags.Static | BindingFlags.NonPublic)
            ?? throw new MissingMethodException(typeof(MainWindow).FullName, methodName);
        return method.Invoke(target, arguments);
    }
}
