using System;
using System.Globalization;
using System.IO;
using System.Collections.Generic;
using System.Linq;
using FluentAssertions;
using Newtonsoft.Json.Linq;
using Newtonsoft.Json;
using Newtonsoft.Json.Schema;
using NUnit.Framework;
using StardewModdingAPI.Framework.Health;

namespace SMAPI.Tests.Framework.Health;

[TestFixture]
internal sealed class ModHealthReportContractTests
{
    [Test]
    public void CanonicalReport_MatchesExamplesAndSchema()
    {
        ModHealthReport report = ModHealthReportFixtureFactory.CreateCanonical();
        string json = new ModHealthReportJsonSerializer().Serialize(report);
        string text = new ModHealthReportTextFormatter().Format(report);
        string assetDirectory = Path.Combine(TestContext.CurrentContext.TestDirectory, "TestAssets", "ModHealthReport");

        string exampleJson = File.ReadAllText(Path.Combine(assetDirectory, "mod-health-report-v1.json"));
        JToken.DeepEquals(JToken.Parse(json), JToken.Parse(exampleJson)).Should().BeTrue();
        text.Should().Be(File.ReadAllText(Path.Combine(assetDirectory, "mod-health-report-v1.txt")));

        JSchema schema = JSchema.Parse(File.ReadAllText(Path.Combine(assetDirectory, "mod-health-report-schema-v1.json")));
        bool isValid = JToken.Parse(json).IsValid(schema, out IList<string> errors);
        isValid.Should().BeTrue(string.Join("\n", errors));
        bool isExampleValid = JToken.Parse(exampleJson).IsValid(schema, out IList<string> exampleErrors);
        isExampleValid.Should().BeTrue(string.Join("\n", exampleErrors));
    }

    [Test]
    public void Serializer_IsInvariantAndUsesLfOnly()
    {
        CultureInfo previousCulture = CultureInfo.CurrentCulture;
        CultureInfo previousUiCulture = CultureInfo.CurrentUICulture;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("fr-FR");
            CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo("ja-JP");

            string json = new ModHealthReportJsonSerializer().Serialize(ModHealthReportFixtureFactory.CreateCanonical());
            string secondJson = new ModHealthReportJsonSerializer().Serialize(ModHealthReportFixtureFactory.CreateCanonical());
            string text = new ModHealthReportTextFormatter().Format(ModHealthReportFixtureFactory.CreateCanonical());

            json.Should().NotContain("\r").And.Contain("33.333");
            json.Should().Be(secondJson);
            text.Should().NotContain("\r").And.Contain("33.333");
        }
        finally
        {
            CultureInfo.CurrentCulture = previousCulture;
            CultureInfo.CurrentUICulture = previousUiCulture;
        }
    }

    [Test]
    public void OptInContract_DoesNotSerializePrivacyCanaries()
    {
        const string username = "private-user-canary";
        const string saveName = "private-save-canary";
        const string rawLog = "private-raw-log-canary";
        ModHealthReport report = ModHealthReportFixtureFactory.CreateCanonical();
        var prohibitedSources = new { Username = username, SaveName = saveName, RawLog = rawLog };

        string json = new ModHealthReportJsonSerializer().Serialize(report);
        string text = new ModHealthReportTextFormatter().Format(report);

        json.Should().NotContain(username).And.NotContain(saveName).And.NotContain(rawLog);
        text.Should().NotContain(username).And.NotContain(saveName).And.NotContain(rawLog);
        GC.KeepAlive(prohibitedSources);
    }

    [Test]
    public void Serializer_RejectsNonFiniteNumbers()
    {
        ModHealthReport report = ModHealthReportFixtureFactory.CreateCanonical();
        report = report with { Capture = report.Capture with { DurationMilliseconds = double.NaN } };

        FluentActions.Invoking(() => new ModHealthReportJsonSerializer().Serialize(report))
            .Should().Throw<JsonSerializationException>()
            .WithMessage("*non-finite*");
    }
}
