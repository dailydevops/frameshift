namespace NetEvolve.FrameShift.Tests.Unit;

using NetEvolve.FrameShift.TestSurface;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;

/// <summary>
/// Exercises the curated name lists of <see cref="AssertionRecognition" /> directly. The collector only
/// ever exercises a handful of these names incidentally through its own fixtures; this file is the one
/// place that proves every entry of both lists actually classifies the way its list says it does, and
/// that the two lists never overlap.
/// </summary>
public class AssertionRecognitionTests
{
    [Test]
    [Arguments("IsNotNull")]
    [Arguments("NotNull")]
    [Arguments("IsNull")]
    [Arguments("Null")]
    [Arguments("NotDefault")]
    [Arguments("IsDefault")]
    public async Task IsTrivialCheck_TrivialName_ReturnsTrue(string methodName) =>
        await Assert.That(AssertionRecognition.IsTrivialCheck(methodName)).IsTrue();

    [Test]
    [Arguments("AreEqual")]
    [Arguments("IsEqualTo")]
    [Arguments("That")]
    [Arguments("Unknown")]
    public async Task IsTrivialCheck_NotATrivialName_ReturnsFalse(string methodName) =>
        await Assert.That(AssertionRecognition.IsTrivialCheck(methodName)).IsFalse();

    [Test]
    [Arguments("AreEqual")]
    [Arguments("AreNotEqual")]
    [Arguments("AreSame")]
    [Arguments("AreNotSame")]
    [Arguments("Equal")]
    [Arguments("NotEqual")]
    [Arguments("EqualTo")]
    [Arguments("NotEqualTo")]
    [Arguments("Same")]
    [Arguments("NotSame")]
    [Arguments("IsEqualTo")]
    [Arguments("IsNotEqualTo")]
    [Arguments("Throws")]
    [Arguments("ThrowsAsync")]
    [Arguments("ThrowsExactly")]
    [Arguments("ThrowsExactlyAsync")]
    [Arguments("DoesNotThrow")]
    [Arguments("DoesNotThrowAsync")]
    [Arguments("Contains")]
    [Arguments("DoesNotContain")]
    [Arguments("Matches")]
    [Arguments("DoesNotMatch")]
    [Arguments("StartsWith")]
    [Arguments("EndsWith")]
    [Arguments("IsTrue")]
    [Arguments("IsFalse")]
    [Arguments("IsInstanceOf")]
    [Arguments("IsInstanceOfType")]
    [Arguments("IsNotInstanceOfType")]
    [Arguments("IsAssignableFrom")]
    [Arguments("IsNotAssignableFrom")]
    [Arguments("SequenceEqual")]
    [Arguments("IsEmpty")]
    [Arguments("IsNotEmpty")]
    [Arguments("Verify")]
    [Arguments("VerifyAsync")]
    [Arguments("Fail")]
    [Arguments("GreaterThan")]
    [Arguments("GreaterThanOrEqualTo")]
    [Arguments("LessThan")]
    [Arguments("LessThanOrEqualTo")]
    [Arguments("InRange")]
    [Arguments("NotInRange")]
    [Arguments("ShouldBe")]
    [Arguments("ShouldNotBe")]
    [Arguments("ShouldEqual")]
    [Arguments("ShouldContain")]
    [Arguments("ShouldThrow")]
    public async Task IsNonTrivialAssertion_RecognisedName_ReturnsTrue(string methodName) =>
        await Assert.That(AssertionRecognition.IsNonTrivialAssertion(methodName)).IsTrue();

    [Test]
    [Arguments("IsNotNull")]
    [Arguments("NotNull")]
    [Arguments("IsNull")]
    [Arguments("Null")]
    [Arguments("NotDefault")]
    [Arguments("IsDefault")]
    // The two lists are disjoint by design: a bare null check must never count as evidence of a real
    // assertion, which is the whole reason FSH0007 exists. Every trivial name is asserted here to never
    // leak into the non-trivial list, instead of trusting that the two lists were kept apart by hand.
    public async Task IsNonTrivialAssertion_TrivialCheckName_ReturnsFalse(string methodName) =>
        await Assert.That(AssertionRecognition.IsNonTrivialAssertion(methodName)).IsFalse();

    [Test]
    [Arguments("Unknown")]
    [Arguments("")]
    [Arguments("That")]
    [Arguments("Initialize")]
    // "That" in particular is deliberately unrecognised: it is the fluent entry point every mainstream
    // assertion library chains off (Assert.That(x).IsEqualTo(y)), not itself an assertion, and
    // recognising it would defeat the trivial/non-trivial distinction for every one of them.
    public async Task IsNonTrivialAssertion_UnrecognisedName_ReturnsFalse(string methodName) =>
        await Assert.That(AssertionRecognition.IsNonTrivialAssertion(methodName)).IsFalse();

    [Test]
    [Arguments("Unknown")]
    [Arguments("")]
    [Arguments("Initialize")]
    public async Task IsTrivialCheck_UnrecognisedName_ReturnsFalse(string methodName) =>
        await Assert.That(AssertionRecognition.IsTrivialCheck(methodName)).IsFalse();
}
