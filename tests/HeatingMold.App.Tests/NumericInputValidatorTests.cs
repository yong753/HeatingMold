using System.Globalization;
using HeatingMold.App;

namespace HeatingMold.App.Tests;

/// <summary>
/// UI 입력 검증은 현재 문화권을 따르되, 유한하지 않거나 범위를 벗어난 값을
/// 조용히 기본값으로 바꾸지 않는다. 테스트는 창/네이티브 뷰어를 만들지 않는다.
/// </summary>
public class NumericInputValidatorTests
{
    private readonly CultureInfo _originalCulture = CultureInfo.CurrentCulture;

    private void WithCulture(string name, Action action)
    {
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo(name);
            action();
        }
        finally
        {
            CultureInfo.CurrentCulture = _originalCulture;
        }
    }

    [Theory]
    [InlineData("5", 5.0)]
    [InlineData("0", 0.0)]
    [InlineData(" 12.5 ", 12.5)]
    public void MarginAcceptsFiniteNonNegativeValues(string text, double expected)
    {
        WithCulture("en-US", () =>
        {
            Assert.True(NumericInputValidator.TryMargin(text, out double value, out string error));
            Assert.Equal(expected, value, 9);
            Assert.Equal("", error);
        });
    }

    [Theory]
    [InlineData("-1")]
    [InlineData("NaN")]
    [InlineData("Infinity")]
    [InlineData("abc")]
    [InlineData("")]
    public void MarginRejectsInvalidOrNegativeValues(string text)
    {
        WithCulture("en-US", () =>
        {
            Assert.False(NumericInputValidator.TryMargin(text, out _, out string error));
            Assert.NotEqual("", error);
        });
    }

    [Theory]
    [InlineData("50", 50.0)]
    [InlineData("0.5", 0.5)]
    public void SpacingAcceptsPositiveValues(string text, double expected)
    {
        WithCulture("en-US", () =>
        {
            Assert.True(NumericInputValidator.TrySpacing(text, out double value, out _));
            Assert.Equal(expected, value, 9);
        });
    }

    [Theory]
    [InlineData("0")]
    [InlineData("-1")]
    [InlineData("NaN")]
    public void SpacingRejectsZeroNegativeOrNonFinite(string text)
    {
        WithCulture("en-US", () =>
        {
            Assert.False(NumericInputValidator.TrySpacing(text, out _, out string error));
            Assert.Contains("0보다 큰", error);
        });
    }

    [Theory]
    [InlineData("0", 0.0)]
    [InlineData("1.5", 1.5)]
    public void InsertionDepthAcceptsNonNegative(string text, double expected)
    {
        WithCulture("en-US", () =>
        {
            Assert.True(NumericInputValidator.TryInsertionDepth(text, out double value, out _));
            Assert.Equal(expected, value, 9);
        });
    }

    [Theory]
    [InlineData("-0.5")]
    [InlineData("NaN")]
    [InlineData("Infinity")]
    public void InsertionDepthRejectsNegativeOrNonFinite(string text)
    {
        WithCulture("en-US", () =>
        {
            Assert.False(NumericInputValidator.TryInsertionDepth(text, out _, out string error));
            Assert.Contains("0 이상", error);
        });
    }

    [Fact]
    public void ParsingUsesCurrentCulture()
    {
        WithCulture("de-DE", () =>
        {
            Assert.True(NumericInputValidator.TryMargin("5,5", out double value, out _));
            Assert.Equal(5.5, value, 9);
        });
    }

    [Fact]
    public void PositiveAndNonNegativeHelpersEnforceBounds()
    {
        WithCulture("en-US", () =>
        {
            Assert.False(NumericInputValidator.TryPositive("0", "직경", out _, out string positiveError));
            Assert.Contains("0보다 큰", positiveError);
            Assert.True(NumericInputValidator.TryNonNegative("0", "전압", out _, out _));
            Assert.False(NumericInputValidator.TryNonNegative("-1", "전압", out _, out string nonNegativeError));
            Assert.Contains("0 이상", nonNegativeError);
        });
    }
}
