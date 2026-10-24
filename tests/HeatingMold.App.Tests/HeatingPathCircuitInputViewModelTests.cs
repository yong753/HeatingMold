#nullable enable annotations

using System;
using System.Globalization;
using HeatingMold.App.ViewModels;
using HeatingMold.Core.Geometry;

namespace HeatingMold.App.Tests;

/// <summary>경로 생성 입력 ViewModel의 좌표·여유·전원 변환을 검사합니다.</summary>
public sealed class HeatingPathCircuitInputViewModelTests
{
    /// <summary>필수 좌표와 여유를 변환하고 비어 있는 전원 한도는 생략 상태로 둡니다.</summary>
    [Fact]
    public void TryCreateInputs_ConvertsRequiredValuesAndPreservesOptionalLimits()
    {
        var viewModel = CreateValidViewModel();
        viewModel.MaximumVoltageText = "240";
        viewModel.MaximumPowerText = "1500";

        bool succeeded = viewModel.TryCreateInputs(out HeatingPathCircuitInputs? inputs, out string error);

        Assert.True(succeeded, error);
        Assert.NotNull(inputs);
        Assert.Equal(new Vec3(10.5, -20, 30), inputs.StartExternalPoint);
        Assert.Equal(new Vec3(-40, 50.25, 60), inputs.EndExternalPoint);
        Assert.Equal(0.5, inputs.MoldExitAllowance.Value);
        Assert.Equal(1.25, inputs.ExternalConnectionAllowance.Value);
        Assert.Null(inputs.Supply.ActualVoltageVrms);
        Assert.Equal(240, inputs.Supply.MaximumVoltageVrms?.Value);
        Assert.Null(inputs.Supply.MaximumCurrentAmps);
        Assert.Equal(1500, inputs.Supply.MaximumPowerWatts?.Value);
    }

    /// <summary>좌표가 비유한 수이거나 연결 여유가 음수이면 결과를 만들지 않습니다.</summary>
    [Fact]
    public void TryCreateInputs_RejectsInvalidCoordinatesAndNegativeAllowance()
    {
        var viewModel = CreateValidViewModel();
        viewModel.EndExternalZText = "NaN";

        bool invalidCoordinate = viewModel.TryCreateInputs(
            out HeatingPathCircuitInputs? coordinateInputs,
            out string coordinateError);

        Assert.False(invalidCoordinate);
        Assert.Null(coordinateInputs);
        Assert.Contains("끝부 외부 연결 좌표", coordinateError);

        viewModel.EndExternalZText = "60";
        viewModel.MoldExitAllowanceText = "-0.1";

        bool negativeAllowance = viewModel.TryCreateInputs(
            out HeatingPathCircuitInputs? allowanceInputs,
            out string allowanceError);

        Assert.False(negativeAllowance);
        Assert.Null(allowanceInputs);
        Assert.Contains("0 이상의 mm", allowanceError);
    }

    /// <summary>선택 전원 한도는 비워둘 수 있지만 입력했다면 양수여야 합니다.</summary>
    [Fact]
    public void TryCreateInputs_AllowsOmittedSupplyAndRejectsZeroLimit()
    {
        var viewModel = CreateValidViewModel();

        bool omittedSupply = viewModel.TryCreateInputs(
            out HeatingPathCircuitInputs? inputs,
            out string error);

        Assert.True(omittedSupply, error);
        Assert.NotNull(inputs);
        Assert.Null(inputs.Supply.ActualVoltageVrms);
        Assert.Null(inputs.Supply.MaximumVoltageVrms);
        Assert.Null(inputs.Supply.MaximumCurrentAmps);
        Assert.Null(inputs.Supply.MaximumPowerWatts);

        viewModel.MaximumCurrentText = "0";

        bool zeroLimit = viewModel.TryCreateInputs(
            out HeatingPathCircuitInputs? invalidInputs,
            out string limitError);

        Assert.False(zeroLimit);
        Assert.Null(invalidInputs);
        Assert.Contains("최대 전류", limitError);
    }

    /// <summary>현재 문화권의 소수점 표기를 좌표와 연결 여유에 동일하게 적용합니다.</summary>
    [Fact]
    public void TryCreateInputs_UsesCurrentCultureForDecimalSeparators()
    {
        CultureInfo originalCulture = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("fr-FR");
            var viewModel = CreateValidViewModel();
            viewModel.StartExternalXText = "10,5";
            viewModel.EndExternalYText = "50,25";
            viewModel.MoldExitAllowanceText = "0,75";
            viewModel.ExternalConnectionAllowanceText = "1,25";

            bool succeeded = viewModel.TryCreateInputs(out HeatingPathCircuitInputs? inputs, out string error);

            Assert.True(succeeded, error);
            Assert.NotNull(inputs);
            Assert.Equal(10.5, inputs.StartExternalPoint.X);
            Assert.Equal(0.75, inputs.MoldExitAllowance.Value);
        }
        finally
        {
            CultureInfo.CurrentCulture = originalCulture;
        }
    }

    /// <summary>재료와 단위 저항을 검증한 뒤 배치 입력의 선경과 함께 사양을 만듭니다.</summary>
    [Fact]
    public void TryCreateWireSpecification_UsesValidatedDiameterAndResistance()
    {
        var viewModel = new WireSpecificationInputsViewModel
        {
            MaterialText = "NiCr",
            ResistanceText = "18.5"
        };

        bool succeeded = viewModel.TryCreateSpecification(
            0.8,
            out var specification,
            out string error);

        Assert.True(succeeded, error);
        Assert.NotNull(specification);
        Assert.Equal("NiCr", specification.MaterialName);
        Assert.Equal(0.8, specification.DiameterMm.Value);
        Assert.Equal(18.5, specification.ResistanceOhmsPerMeter.Value);

        viewModel.ResistanceText = "Infinity";
        bool invalidResistance = viewModel.TryCreateSpecification(
            0.8,
            out var invalidSpecification,
            out string resistanceError);

        Assert.False(invalidResistance);
        Assert.Null(invalidSpecification);
        Assert.Contains("단위 길이당 저항", resistanceError);
    }

    private static HeatingPathCircuitInputViewModel CreateValidViewModel() => new()
    {
        StartExternalXText = "10.5",
        StartExternalYText = "-20",
        StartExternalZText = "30",
        EndExternalXText = "-40",
        EndExternalYText = "50.25",
        EndExternalZText = "60",
        MoldExitAllowanceText = "0.5",
        ExternalConnectionAllowanceText = "1.25"
    };
}
