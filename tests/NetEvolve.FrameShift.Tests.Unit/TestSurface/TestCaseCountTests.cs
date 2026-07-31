namespace NetEvolve.FrameShift.Tests.Unit;

using NetEvolve.FrameShift.TestSurface;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;

/// <summary>
/// Pins the counting value the whole single-test-case heuristic rests on. Whether a finding is reported
/// depends on the difference between an exact count and a lower bound, so the exactness has to survive
/// every operation: addition, formatting and parsing.
/// </summary>
public class TestCaseCountTests
{
    [Test]
    [Arguments(0)]
    [Arguments(1)]
    [Arguments(3)]
    [Arguments(int.MaxValue)]
    public async Task Exact_NonNegativeValue_IsExact(int value)
    {
        var count = TestCaseCount.Exact(value);

        using (Assert.Multiple())
        {
            _ = await Assert.That(count.Value).IsEqualTo(value);
            _ = await Assert.That(count.IsExact).IsTrue();
        }
    }

    [Test]
    [Arguments(0)]
    [Arguments(1)]
    [Arguments(3)]
    [Arguments(int.MaxValue)]
    public async Task AtLeast_NonNegativeValue_IsALowerBound(int value)
    {
        var count = TestCaseCount.AtLeast(value);

        using (Assert.Multiple())
        {
            _ = await Assert.That(count.Value).IsEqualTo(value);
            _ = await Assert.That(count.IsExact).IsFalse();
        }
    }

    [Test]
    [Arguments(-1)]
    [Arguments(int.MinValue)]
    public async Task Exact_NegativeValue_ThrowsArgumentOutOfRangeException(int value)
    {
        var exception = Assert.Throws<ArgumentOutOfRangeException>(() => _ = TestCaseCount.Exact(value));

        _ = await Assert.That(exception.ParamName).IsEqualTo("value");
    }

    [Test]
    [Arguments(-1)]
    [Arguments(int.MinValue)]
    public async Task AtLeast_NegativeValue_ThrowsArgumentOutOfRangeException(int value)
    {
        var exception = Assert.Throws<ArgumentOutOfRangeException>(() => _ = TestCaseCount.AtLeast(value));

        _ = await Assert.That(exception.ParamName).IsEqualTo("value");
    }

    /// <summary>
    /// The default instance is what an uninitialized dictionary lookup yields, and it must be the most
    /// pessimistic value there is: a lower bound of zero states nothing at all.
    /// </summary>
    [Test]
    public async Task Default_TheUninitializedValue_IsTheZeroLowerBound()
    {
        var count = default(TestCaseCount);

        using (Assert.Multiple())
        {
            _ = await Assert.That(count.Value).IsEqualTo(0);
            _ = await Assert.That(count.IsExact).IsFalse();
            _ = await Assert.That(count.ToString()).IsEqualTo("0+");
        }
    }

    [Test]
    public async Task Add_BothOperandsAreExact_StaysExact()
    {
        var count = TestCaseCount.Exact(2).Add(TestCaseCount.Exact(3));

        using (Assert.Multiple())
        {
            _ = await Assert.That(count.Value).IsEqualTo(5);
            _ = await Assert.That(count.IsExact).IsTrue();
        }
    }

    [Test]
    public async Task Add_TheRightOperandIsALowerBound_BecomesALowerBound()
    {
        var count = TestCaseCount.Exact(2).Add(TestCaseCount.AtLeast(3));

        using (Assert.Multiple())
        {
            _ = await Assert.That(count.Value).IsEqualTo(5);
            _ = await Assert.That(count.IsExact).IsFalse();
        }
    }

    [Test]
    public async Task Add_TheLeftOperandIsALowerBound_BecomesALowerBound()
    {
        var count = TestCaseCount.AtLeast(2).Add(TestCaseCount.Exact(3));

        using (Assert.Multiple())
        {
            _ = await Assert.That(count.Value).IsEqualTo(5);
            _ = await Assert.That(count.IsExact).IsFalse();
        }
    }

    [Test]
    public async Task Add_BothOperandsAreLowerBounds_StaysALowerBound()
    {
        var count = TestCaseCount.AtLeast(2).Add(TestCaseCount.AtLeast(3));

        using (Assert.Multiple())
        {
            _ = await Assert.That(count.Value).IsEqualTo(5);
            _ = await Assert.That(count.IsExact).IsFalse();
        }
    }

    /// <summary>
    /// Summing counts must never wrap around into a negative value, because a negative count would be
    /// read as fewer cases than there are.
    /// </summary>
    [Test]
    public async Task Add_TheSumExceedsTheValueRange_SaturatesAtTheMaximum()
    {
        var count = TestCaseCount.Exact(int.MaxValue).Add(TestCaseCount.Exact(2));

        using (Assert.Multiple())
        {
            _ = await Assert.That(count.Value).IsEqualTo(int.MaxValue);
            _ = await Assert.That(count.IsExact).IsTrue();
        }
    }

    [Test]
    [Arguments(0, "0")]
    [Arguments(1, "1")]
    [Arguments(3, "3")]
    [Arguments(42, "42")]
    [Arguments(2147483647, "2147483647")]
    public async Task ToString_AnExactCount_IsTheBareNumber(int value, string expected) =>
        _ = await Assert.That(TestCaseCount.Exact(value).ToString()).IsEqualTo(expected);

    [Test]
    [Arguments(0, "0+")]
    [Arguments(1, "1+")]
    [Arguments(3, "3+")]
    [Arguments(2147483647, "2147483647+")]
    public async Task ToString_ALowerBound_CarriesThePlusSuffix(int value, string expected) =>
        _ = await Assert.That(TestCaseCount.AtLeast(value).ToString()).IsEqualTo(expected);

    [Test]
    [Arguments("0", 0)]
    [Arguments("1", 1)]
    [Arguments("3", 3)]
    [Arguments("2147483647", 2147483647)]
    public async Task TryParse_DigitsOnly_YieldsAnExactCount(string text, int expected)
    {
        var parsed = TestCaseCount.TryParse(text, out var count);

        using (Assert.Multiple())
        {
            _ = await Assert.That(parsed).IsTrue();
            _ = await Assert.That(count.Value).IsEqualTo(expected);
            _ = await Assert.That(count.IsExact).IsTrue();
        }
    }

    [Test]
    [Arguments("0+", 0)]
    [Arguments("1+", 1)]
    [Arguments("3+", 3)]
    [Arguments("2147483647+", 2147483647)]
    public async Task TryParse_DigitsWithThePlusSuffix_YieldsALowerBound(string text, int expected)
    {
        var parsed = TestCaseCount.TryParse(text, out var count);

        using (Assert.Multiple())
        {
            _ = await Assert.That(parsed).IsTrue();
            _ = await Assert.That(count.Value).IsEqualTo(expected);
            _ = await Assert.That(count.IsExact).IsFalse();
        }
    }

    /// <summary>
    /// Everything that is not exactly digits, optionally followed by a single plus, is nonsense and has
    /// to be reported as a malformed manifest instead of being guessed at.
    /// </summary>
    [Test]
    [Arguments("")]
    [Arguments(" ")]
    [Arguments("+")]
    [Arguments("++")]
    [Arguments("3++")]
    [Arguments("+3")]
    [Arguments("-1")]
    [Arguments("-1+")]
    [Arguments("3-")]
    [Arguments(" 3")]
    [Arguments("3 ")]
    [Arguments("3 +")]
    [Arguments("1,000")]
    [Arguments("1.0")]
    [Arguments("0x3")]
    [Arguments("three")]
    [Arguments("2147483648")]
    [Arguments("2147483648+")]
    [Arguments("99999999999999999999")]
    public async Task TryParse_MalformedText_Fails(string text)
    {
        var parsed = TestCaseCount.TryParse(text, out var count);

        using (Assert.Multiple())
        {
            _ = await Assert.That(parsed).IsFalse();
            _ = await Assert.That(count).IsEqualTo(default(TestCaseCount));
        }
    }

    [Test]
    public async Task TryParse_TextIsNull_Fails()
    {
        var parsed = TestCaseCount.TryParse(null, out var count);

        using (Assert.Multiple())
        {
            _ = await Assert.That(parsed).IsFalse();
            _ = await Assert.That(count).IsEqualTo(default(TestCaseCount));
        }
    }

    [Test]
    [Arguments("0")]
    [Arguments("0+")]
    [Arguments("1")]
    [Arguments("1+")]
    [Arguments("17")]
    [Arguments("17+")]
    public async Task TryParse_TheOutputOfToString_RoundTrips(string text)
    {
        var parsed = TestCaseCount.TryParse(text, out var count);

        using (Assert.Multiple())
        {
            _ = await Assert.That(parsed).IsTrue();
            _ = await Assert.That(count.ToString()).IsEqualTo(text);
        }
    }

    [Test]
    public async Task Equals_SameValueAndSameExactness_AreEqual()
    {
        var left = TestCaseCount.Exact(3);
        var right = TestCaseCount.Exact(3);

        using (Assert.Multiple())
        {
            _ = await Assert.That(left.Equals(right)).IsTrue();
            _ = await Assert.That(left == right).IsTrue();
            _ = await Assert.That(left != right).IsFalse();
            _ = await Assert.That(left.GetHashCode()).IsEqualTo(right.GetHashCode());
        }
    }

    /// <summary>
    /// An exact three and a lower bound of three are different statements, and the heuristic reports on
    /// exactly that difference, so they must never compare equal.
    /// </summary>
    [Test]
    public async Task Equals_SameValueButDifferentExactness_AreNotEqual()
    {
        var exact = TestCaseCount.Exact(3);
        var lowerBound = TestCaseCount.AtLeast(3);

        using (Assert.Multiple())
        {
            _ = await Assert.That(exact.Equals(lowerBound)).IsFalse();
            _ = await Assert.That(exact == lowerBound).IsFalse();
            _ = await Assert.That(exact != lowerBound).IsTrue();
            _ = await Assert.That(exact.GetHashCode()).IsNotEqualTo(lowerBound.GetHashCode());
        }
    }

    [Test]
    public async Task Equals_DifferentValue_AreNotEqual()
    {
        using (Assert.Multiple())
        {
            _ = await Assert.That(TestCaseCount.Exact(3).Equals(TestCaseCount.Exact(4))).IsFalse();
            _ = await Assert.That(TestCaseCount.Exact(3) == TestCaseCount.Exact(4)).IsFalse();
            _ = await Assert.That(TestCaseCount.Exact(3) != TestCaseCount.Exact(4)).IsTrue();
        }
    }

    [Test]
    public async Task Equals_AnotherType_ReturnsFalse()
    {
        var count = TestCaseCount.Exact(3);

        using (Assert.Multiple())
        {
            _ = await Assert.That(count.Equals("3")).IsFalse();
            _ = await Assert.That(count.Equals(null)).IsFalse();
            _ = await Assert.That(count.Equals((object)TestCaseCount.Exact(3))).IsTrue();
        }
    }
}
