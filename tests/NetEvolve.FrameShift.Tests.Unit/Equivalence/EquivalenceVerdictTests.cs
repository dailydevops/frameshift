namespace NetEvolve.FrameShift.Tests.Unit.Equivalence;

using NetEvolve.FrameShift.Equivalence;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;

/// <summary>
/// Covers the value object every classification returns. The reason travels into the message of
/// <c>FSH0002</c>, therefore a verdict must reject a missing or empty reason instead of reporting a
/// diagnostic with an empty parenthesis, and the two shapes of a verdict must stay distinguishable.
/// </summary>
public class EquivalenceVerdictTests
{
    private const string Reason = "the mutated expression folds to the same constant";

    [Test]
    public async Task Trivial_ReasonIsNull_ThrowsArgumentNullException()
    {
        var exception = Assert.Throws<ArgumentNullException>(() => _ = EquivalenceVerdict.Trivial(null!));

        _ = await Assert.That(exception.ParamName).IsEqualTo("reason");
    }

    [Test]
    public async Task Trivial_ReasonIsEmpty_ThrowsArgumentException()
    {
        var exception = Assert.ThrowsExactly<ArgumentException>(() => _ = EquivalenceVerdict.Trivial(string.Empty));

        _ = await Assert.That(exception.ParamName).IsEqualTo("reason");
    }

    [Test]
    public async Task Trivial_ReasonIsGiven_CarriesTheReason()
    {
        var verdict = EquivalenceVerdict.Trivial(Reason);

        using (Assert.Multiple())
        {
            _ = await Assert.That(verdict.IsTrivial).IsTrue();
            _ = await Assert.That(verdict.Reason).IsEqualTo(Reason);
        }
    }

    [Test]
    public async Task Trivial_ReasonIsASingleCharacter_IsAccepted()
    {
        var verdict = EquivalenceVerdict.Trivial("x");

        using (Assert.Multiple())
        {
            _ = await Assert.That(verdict.IsTrivial).IsTrue();
            _ = await Assert.That(verdict.Reason).IsEqualTo("x");
        }
    }

    [Test]
    public async Task ToString_VerdictIsTrivial_IsTheReason()
    {
        var verdict = EquivalenceVerdict.Trivial(Reason);

        _ = await Assert.That(verdict.ToString()).IsEqualTo(Reason);
    }

    [Test]
    public async Task NotTrivial_Always_HasNoReason()
    {
        var verdict = EquivalenceVerdict.NotTrivial;

        using (Assert.Multiple())
        {
            _ = await Assert.That(verdict.IsTrivial).IsFalse();
            _ = await Assert.That(verdict.Reason).IsNull();
        }
    }

    [Test]
    public async Task ToString_VerdictIsNotTrivial_IsTheFallbackText() =>
        await Assert.That(EquivalenceVerdict.NotTrivial.ToString()).IsEqualTo("not trivial");

    [Test]
    public async Task NotTrivial_ReadTwice_IsTheSameInstance()
    {
        var first = EquivalenceVerdict.NotTrivial;
        var second = EquivalenceVerdict.NotTrivial;

        _ = await Assert.That(ReferenceEquals(first, second)).IsTrue();
    }

    [Test]
    public async Task Trivial_CalledTwice_CreatesIndependentVerdicts()
    {
        var first = EquivalenceVerdict.Trivial(Reason);
        var second = EquivalenceVerdict.Trivial(Reason);

        using (Assert.Multiple())
        {
            _ = await Assert.That(ReferenceEquals(first, second)).IsFalse();
            _ = await Assert.That(second.Reason).IsEqualTo(first.Reason);
        }
    }
}
