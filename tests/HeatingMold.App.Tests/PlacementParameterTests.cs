#nullable enable annotations

using System.Collections.Generic;
using System.Globalization;
using HeatingMold.App.ViewModels;

namespace HeatingMold.App.Tests;

/// <summary>경로 배치 ViewModel의 수치 입력과 파생 중심 피치를 검사합니다.</summary>
public sealed class PlacementParameterTests
{
    /// <summary>g와 공유 선경 d를 더해 검증된 생성 입력을 만듭니다.</summary>
    [Fact]
    public void TryCreateParameters_UsesMinimumGapAndSharedWireDiameter()
    {
        var wireSpecification = new WireSpecificationInputsViewModel { DiameterText = "0.5" };
        var viewModel = new HeatingPathPlacementInputViewModel(wireSpecification)
        {
            BoundaryMarginText = "5",
            MinimumClearGapText = "7.25",
            InsertionDepthText = "0.25"
        };

        bool succeeded = viewModel.TryCreateParameters(out HeatingPathPlacementParameters? parameters);

        Assert.True(succeeded);
        Assert.NotNull(parameters);
        Assert.Equal(5, parameters.BoundaryMarginMm);
        Assert.Equal(7.25, parameters.MinimumClearGapMm);
        Assert.Equal(0.25, parameters.InsertionDepthMm);
        Assert.Equal(0.5, parameters.WireDiameterMm);
        Assert.Equal(7.75, parameters.CenterPitchMm);
        Assert.Equal(7.75.ToString("F2", CultureInfo.CurrentCulture), viewModel.CenterPitchText);
    }

    /// <summary>최소 간격이나 공유 선경이 바뀌면 계산 피치 바인딩도 갱신합니다.</summary>
    [Fact]
    public void CenterPitchText_NotifiesWhenGapOrSharedWireDiameterChanges()
    {
        var wireSpecification = new WireSpecificationInputsViewModel();
        var viewModel = new HeatingPathPlacementInputViewModel(wireSpecification);
        var changedProperties = new List<string?>();
        viewModel.PropertyChanged += (_, eventArgs) => changedProperties.Add(eventArgs.PropertyName);

        viewModel.MinimumClearGapText = "7";
        wireSpecification.DiameterText = "0.5";

        Assert.Equal(new[] { "MinimumClearGapText", "CenterPitchText", "CenterPitchText" }, changedProperties);
        Assert.Equal(7.5.ToString("F2", CultureInfo.CurrentCulture), viewModel.CenterPitchText);
    }

    /// <summary>경계 여유·삽입 깊이 0은 허용하고 필수 값 누락이나 음수는 차단합니다.</summary>
    [Theory]
    [InlineData("", "9", "1", "0")]
    [InlineData("-0.1", "9", "1", "0")]
    [InlineData("0", "0", "1", "0")]
    [InlineData("0", "9", "0", "0")]
    [InlineData("NaN", "9", "1", "0")]
    [InlineData("0", "9", "1", "")]
    [InlineData("0", "9", "1", "-0.1")]
    public void TryCreateParameters_RejectsInvalidInputs(
        string boundaryMargin,
        string minimumClearGap,
        string wireDiameter,
        string insertionDepth)
    {
        var wireSpecification = new WireSpecificationInputsViewModel { DiameterText = wireDiameter };
        var viewModel = new HeatingPathPlacementInputViewModel(wireSpecification)
        {
            BoundaryMarginText = boundaryMargin,
            MinimumClearGapText = minimumClearGap,
            InsertionDepthText = insertionDepth
        };

        bool succeeded = viewModel.TryCreateParameters(out HeatingPathPlacementParameters? parameters);

        Assert.False(succeeded);
        Assert.Null(parameters);
    }

    /// <summary>현재 문화권의 소수 구분자를 사용해 입력을 읽습니다.</summary>
    [Fact]
    public void TryCreateParameters_ParsesCurrentCulture()
    {
        CultureInfo originalCulture = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("fr-FR");
            var wireSpecification = new WireSpecificationInputsViewModel { DiameterText = "0,5" };
            var viewModel = new HeatingPathPlacementInputViewModel(wireSpecification)
            {
                BoundaryMarginText = "1,5",
                MinimumClearGapText = "7,25",
                InsertionDepthText = "0,25"
            };

            bool succeeded = viewModel.TryCreateParameters(out HeatingPathPlacementParameters? parameters);

            Assert.True(succeeded);
            Assert.NotNull(parameters);
            Assert.Equal(1.5, parameters.BoundaryMarginMm);
            Assert.Equal(7.25, parameters.MinimumClearGapMm);
            Assert.Equal(0.25, parameters.InsertionDepthMm);
            Assert.Equal(7.75, parameters.CenterPitchMm);
            Assert.Equal("7,75", viewModel.CenterPitchText);
        }
        finally
        {
            CultureInfo.CurrentCulture = originalCulture;
        }
    }

    /// <summary>피치 합계가 유한 double 범위를 넘으면 생성 입력으로 받지 않습니다.</summary>
    [Fact]
    public void TryCreateParameters_RejectsCenterPitchOverflow()
    {
        string maximum = double.MaxValue.ToString("R", CultureInfo.CurrentCulture);
        var wireSpecification = new WireSpecificationInputsViewModel { DiameterText = maximum };
        var viewModel = new HeatingPathPlacementInputViewModel(wireSpecification)
        {
            BoundaryMarginText = "0",
            MinimumClearGapText = maximum,
            InsertionDepthText = "0"
        };

        bool succeeded = viewModel.TryCreateParameters(out HeatingPathPlacementParameters? parameters);

        Assert.False(succeeded);
        Assert.Null(parameters);
        Assert.Equal("—", viewModel.CenterPitchText);
    }
}
