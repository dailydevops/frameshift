namespace NetEvolve.FrameShift.TestSurface;

using Microsoft.CodeAnalysis;

/// <summary>
/// Recognises the test methods of a single compilation for one version of one test framework. An instance
/// is created per compilation by an <see cref="ITestFrameworkProbe" />, so that the symbols the framework
/// is identified by are resolved exactly once.
/// </summary>
/// <remarks>
/// <para>
/// <em>Recognising a method fails closed</em>, which is the opposite direction from the fail-open
/// detection of <see cref="ITestFrameworkProbe.TryCreateRecognizer(Compilation)" />. Nothing but positive
/// evidence makes a method a test: an attribute that is, or derives from, the well-known test attribute
/// type the probe resolved out of the framework's own assembly. A matching simple type name is not
/// evidence, because any assembly may declare a type of that name, and an attribute belonging to another
/// version of the same framework is not evidence either. A recogniser created by a probe that only saw a
/// reference to the framework, without resolving its well-known type, consequently recognises nothing at
/// all — the same, harmless outcome as an empty test project. Every judgement FrameShift makes about
/// mutation coverage rests on this decision, so a false positive here is far more expensive than a false
/// negative.
/// </para>
/// <para>
/// A recogniser answers only for its own framework version and never asks whether another one would answer
/// the same. Two recognisers may well accept the same method: the two versions of one framework describe
/// the same tests by design, and a single method can carry the test attributes of two frameworks at once.
/// Keeping the developer from hearing about such a method twice is the business of the shared analysis,
/// not of a recogniser.
/// </para>
/// <para>
/// Implementations must be immutable and thread-safe, because analyzer callbacks run concurrently.
/// </para>
/// </remarks>
internal interface ITestMethodRecognizer
{
    /// <summary>
    /// Gets the display name of the recognised test framework, used in diagnostic messages. It names the
    /// framework version, matching the <see cref="ITestFrameworkProbe.FrameworkName" /> of the probe that
    /// created the recogniser.
    /// </summary>
    string FrameworkName { get; }

    /// <summary>
    /// Determines whether <paramref name="method" /> is a test method of the recognised framework version.
    /// </summary>
    /// <param name="method">The method to inspect.</param>
    /// <returns>
    /// <see langword="true" /> if the method is a test method; otherwise <see langword="false" />.
    /// </returns>
    /// <remarks>
    /// Answering <see langword="true" /> claims nothing exclusively: another framework's recogniser may
    /// accept the same method.
    /// </remarks>
    bool IsTestMethod(IMethodSymbol method);

    /// <summary>
    /// Counts the test cases <paramref name="method" /> contributes, i.e. how many times the framework
    /// runs it with a different set of input values.
    /// </summary>
    /// <param name="method">The test method to count the cases of.</param>
    /// <returns>
    /// The exact number of test cases when it can be read off the declaration, and a lower bound
    /// otherwise.
    /// </returns>
    /// <remarks>
    /// <para>
    /// The answer is only meaningful for a method <see cref="IsTestMethod(IMethodSymbol)" /> accepts, so
    /// a caller asks the two questions in that order. For any other method the returned count is
    /// unspecified — it may describe the data attributes the method happens to carry, or nothing at all —
    /// and a caller must not read anything into it. It is still a well-formed count: an implementation
    /// answers every method it is handed and throws for none.
    /// </para>
    /// <para>
    /// <em>A parameterless test method is exactly one case.</em> That is the counter-intuitive part of
    /// this contract and the reason the heuristic built on it works at all: such a method has no data
    /// attribute to read, but its input values are hardcoded in its body, which makes it exactly as
    /// narrow as a single row of inline data. It is therefore never exempt and never a lower bound.
    /// </para>
    /// <para>
    /// The counting rules every implementation follows, so that five frameworks cannot drift apart:
    /// </para>
    /// <list type="bullet">
    /// <item>
    /// <description>a parameterless test method is <see cref="TestCaseCount.Exact(int)" /> one;</description>
    /// </item>
    /// <item>
    /// <description>
    /// <c>n</c> inline data attributes are exactly <c>n</c> cases, because each one is a row of its own;
    /// </description>
    /// </item>
    /// <item>
    /// <description>
    /// a combinatorial attribute over literal value sets is exactly the size of their cross product;
    /// </description>
    /// </item>
    /// <item>
    /// <description>
    /// a data source whose referenced member is a literal sequence is exactly the length of that
    /// sequence;
    /// </description>
    /// </item>
    /// <item>
    /// <description>every other data source is <see cref="TestCaseCount.AtLeast(int)" /> one;</description>
    /// </item>
    /// <item>
    /// <description>
    /// inline data and a data source on one method add up, via <see cref="TestCaseCount.Add(TestCaseCount)" />, and the
    /// sum stays exact only while every contributing part is exact.
    /// </description>
    /// </item>
    /// </list>
    /// <para>
    /// <em>A data source that cannot be enumerated statically is a lower bound, never "unknown".</em> A
    /// framework resolves such a source by executing the member it names while it discovers tests, and an
    /// analyzer must not execute the code it analyses. What is left is not ignorance: the source is
    /// declared, so it contributes cases, and the honest static answer is "at least this many". A lower
    /// bound suppresses the heuristic downstream, which is the safe direction; "unknown" as a separate
    /// state would only invite a caller to treat it as zero.
    /// </para>
    /// <para>
    /// An implementation never throws for an attribute shape it does not know, however malformed: a data
    /// attribute of an unexpected arity, a value it cannot fold to a constant, a data source naming a
    /// member that does not exist. Analyzers run on code that is being typed, and the safe answer to
    /// anything unrecognised is <see cref="TestCaseCount.AtLeast(int)" /> one.
    /// </para>
    /// </remarks>
    TestCaseCount GetTestCaseCount(IMethodSymbol method);
}
