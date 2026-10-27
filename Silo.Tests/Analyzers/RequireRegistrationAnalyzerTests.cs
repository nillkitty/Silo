namespace Silo.Tests.Analyzers;

/// <summary>SILO001: implementations of an interface marked <c>[RequireRegistration]</c> must declare their registration.</summary>
public class RequireRegistrationAnalyzerTests
{
    private static IEnumerable<RequireRegistrationCase> Cases => RequireRegistrationHarness.Cases;

    [TestCaseSource(nameof(Cases))]
    public async Task Reports_exactly_the_unregistered_implementations(RequireRegistrationCase c)
    {
        var found = await RequireRegistrationHarness.RunAsync(c);

        Assert.That(found.Select(RequireRegistrationHarness.NameAt), Is.EqualTo(c.Reported), c.Name);
        Assert.That(found, Is.All.Matches<Microsoft.CodeAnalysis.Diagnostic>(d => d?.Severity == Microsoft.CodeAnalysis.DiagnosticSeverity.Warning));

        if (c.MessageHas is not null)
            Assert.That(found[0].GetMessage(), Does.Contain(c.MessageHas));
    }

    [Test]
    public void The_marker_attribute_in_Silo_is_the_one_the_analyzer_looks_for()
    {
        Assert.That(typeof(Silo.Contracts.RequireRegistrationAttribute).FullName,
                    Is.EqualTo(Silo.Analyzers.RequireRegistrationAnalyzer.RequireRegistrationAttributeName));
    }

    [Test]
    public void The_registration_attributes_it_accepts_are_the_ones_the_app_scans_for()
    {
        Assert.That(Silo.Analyzers.RequireRegistrationAnalyzer.RegistrationAttributeNames,
                    Is.EquivalentTo(new[] { typeof(Telefrag.DI.SingletonAttribute).FullName, typeof(Telefrag.DI.TransientAttribute).FullName, typeof(Silo.Ui.RegisterAttribute).FullName }));
    }
}
