using System.Security.Cryptography;
using System.Text.Json;
using PalworldAtlas.Extractor;
using Xunit;

namespace PalworldAtlas.Extractor.Tests;

public sealed class BuildMetadataGeneratorTests
{
    private static readonly string ValidateDataFixtures =
        Path.Combine(AppContext.BaseDirectory, "Fixtures", "validate-data");

    private static readonly string BuildMetadataFixtures =
        Path.Combine(AppContext.BaseDirectory, "Fixtures", "build-metadata");

    private static readonly string SchemaDir = Path.Combine(ValidateDataFixtures, "schemas");
    private static readonly string ValidDataDir = Path.Combine(ValidateDataFixtures, "valid");

    private static readonly string[] DatasetFileNames =
        ["pals.json", "passive-skills.json", "partner-skills.json", "game-settings.json"];

    /// <summary>Same composition helper as DataValidatorTests: valid baseline + at most one mutated file.</summary>
    private sealed class TempDataDir : IDisposable
    {
        public string Path { get; }

        public TempDataDir(string? targetFileName = null, string? mutationFileName = null)
        {
            Path = Directory.CreateTempSubdirectory("palworld-build-metadata-").FullName;

            foreach (var fileName in DatasetFileNames)
            {
                var source = fileName == targetFileName
                    ? System.IO.Path.Combine(ValidateDataFixtures, "mutations", mutationFileName!)
                    : System.IO.Path.Combine(ValidDataDir, fileName);

                File.Copy(source, System.IO.Path.Combine(Path, fileName));
            }
        }

        public void Dispose() => Directory.Delete(Path, recursive: true);
    }

    [Fact]
    public void ValidDatasetProducesSuccessfulManifest()
    {
        using var dataDir = new TempDataDir();

        var result = BuildMetadataGenerator.Generate(dataDir.Path, SchemaDir, "12345", "1.0.0", previousBuildPath: null);

        Assert.True(result.Success);
        Assert.NotNull(result.ManifestJson);

        using var manifest = JsonDocument.Parse(result.ManifestJson!);
        var root = manifest.RootElement;

        Assert.Equal("12345", root.GetProperty("steamBuildId").GetString());
        Assert.Equal("1.0.0", root.GetProperty("gameVersion").GetString());
        Assert.Equal(2, root.GetProperty("datasets").GetProperty("pals").GetProperty("rows").GetInt32());
        Assert.False(root.GetProperty("previousBuildComparison").GetProperty("available").GetBoolean());
        Assert.Equal(0, root.GetProperty("validation").GetProperty("errors").GetInt32());
    }

    [Fact]
    public void Sha256IsStableAndMatchesFileContent()
    {
        using var dataDir = new TempDataDir();

        var result = BuildMetadataGenerator.Generate(dataDir.Path, SchemaDir, "12345", null, previousBuildPath: null);
        var secondResult = BuildMetadataGenerator.Generate(dataDir.Path, SchemaDir, "12345", null, previousBuildPath: null);

        using var manifest = JsonDocument.Parse(result.ManifestJson!);
        using var secondManifest = JsonDocument.Parse(secondResult.ManifestJson!);

        var palsSha = manifest.RootElement.GetProperty("datasets").GetProperty("pals").GetProperty("sha256").GetString();
        var secondPalsSha = secondManifest.RootElement.GetProperty("datasets").GetProperty("pals").GetProperty("sha256").GetString();

        var expectedSha = Convert.ToHexStringLower(
            SHA256.HashData(File.ReadAllBytes(Path.Combine(dataDir.Path, "pals.json"))));

        Assert.Equal(expectedSha, palsSha);
        Assert.Equal(palsSha, secondPalsSha);
    }

    [Fact]
    public void ValidatorErrorPreventsGeneration()
    {
        using var dataDir = new TempDataDir("pals.json", "pals.duplicate-id.json");

        var result = BuildMetadataGenerator.Generate(dataDir.Path, SchemaDir, "12345", null, previousBuildPath: null);

        Assert.False(result.Success);
        Assert.Null(result.ManifestJson);
        Assert.False(result.ValidationReport.IsValid);
    }

    [Fact]
    public void WarningDoesNotPreventGeneration()
    {
        // The shared valid fixture's game-settings.json omits runtime evidence, which
        // is a real validate-data warning - it must not block manifest generation.
        using var dataDir = new TempDataDir();

        var result = BuildMetadataGenerator.Generate(dataDir.Path, SchemaDir, "12345", null, previousBuildPath: null);

        Assert.True(result.Success);
        Assert.NotEmpty(result.ValidationReport.Warnings);
    }

    [Fact]
    public void NoPreviousBuildMeansComparisonUnavailable()
    {
        using var dataDir = new TempDataDir();

        var result = BuildMetadataGenerator.Generate(dataDir.Path, SchemaDir, "12345", null, previousBuildPath: null);

        using var manifest = JsonDocument.Parse(result.ManifestJson!);
        var comparison = manifest.RootElement.GetProperty("previousBuildComparison");

        Assert.False(comparison.GetProperty("available").GetBoolean());
        Assert.Single(comparison.EnumerateObject()); // only "available" - nothing decorative
    }

    [Fact]
    public void PreviousBuildProducesCorrectDeltas()
    {
        using var dataDir = new TempDataDir();
        var previousBuildPath = Path.Combine(BuildMetadataFixtures, "previous-build.json");

        var result = BuildMetadataGenerator.Generate(dataDir.Path, SchemaDir, "12345", null, previousBuildPath);

        Assert.True(result.Success);

        using var manifest = JsonDocument.Parse(result.ManifestJson!);
        var datasets = manifest.RootElement.GetProperty("previousBuildComparison").GetProperty("datasets");

        var palsComparison = datasets.GetProperty("pals");
        Assert.Equal(1, palsComparison.GetProperty("previousRows").GetInt32());
        Assert.Equal(2, palsComparison.GetProperty("currentRows").GetInt32());
        Assert.Equal(1, palsComparison.GetProperty("delta").GetInt32());
        Assert.Equal(100.0, palsComparison.GetProperty("deltaPercent").GetDouble());
    }

    [Fact]
    public void AbnormalRowCountDropProducesRiskWarning()
    {
        using var dataDir = new TempDataDir();
        var previousBuildPath = Path.Combine(BuildMetadataFixtures, "previous-build.json");

        var result = BuildMetadataGenerator.Generate(dataDir.Path, SchemaDir, "12345", null, previousBuildPath);

        // previous-build.json records partnerSkills=20 rows; the valid fixture has 1 -
        // a ~95% drop, far past the 5% risk threshold.
        Assert.Contains(result.ComparisonWarnings, w => w.Contains("partnerSkills") && w.Contains("dropped"));

        using var manifest = JsonDocument.Parse(result.ManifestJson!);
        var riskWarnings = manifest.RootElement
            .GetProperty("previousBuildComparison")
            .GetProperty("riskWarnings")
            .EnumerateArray()
            .Select(e => e.GetString())
            .ToArray();

        Assert.Contains(riskWarnings, w => w!.Contains("partnerSkills"));
    }

    [Fact]
    public void RowCountIncreaseIsNotFlaggedAsRisk()
    {
        using var dataDir = new TempDataDir();
        var previousBuildPath = Path.Combine(BuildMetadataFixtures, "previous-build.json");

        var result = BuildMetadataGenerator.Generate(dataDir.Path, SchemaDir, "12345", null, previousBuildPath);

        // previous-build.json records pals=1 row; the valid fixture has 2 - a 100%
        // increase, which must never itself be a risk warning.
        Assert.DoesNotContain(result.ComparisonWarnings, w => w.Contains("pals") && w.Contains("dropped"));
        Assert.True(result.Success);
    }

    [Fact]
    public void InvalidBuildSchemaFailsGeneration()
    {
        using var dataDir = new TempDataDir();
        var brokenSchemaDir = Path.Combine(BuildMetadataFixtures, "broken-build-schema");

        var result = BuildMetadataGenerator.Generate(dataDir.Path, brokenSchemaDir, "12345", null, previousBuildPath: null);

        Assert.False(result.Success);
        Assert.Null(result.ManifestJson);
        Assert.NotEmpty(result.SchemaErrors);
    }

    [Fact]
    public void OutputIsWrittenOnlyAfterSuccess()
    {
        using var dataDir = new TempDataDir();
        using var failingDataDir = new TempDataDir("pals.json", "pals.duplicate-id.json");

        var outputDir = Directory.CreateTempSubdirectory("palworld-build-metadata-output-").FullName;
        try
        {
            var outputPath = Path.Combine(outputDir, "build.json");

            var failingResult = BuildMetadataGenerator.Generate(failingDataDir.Path, SchemaDir, "12345", null, null);
            var wroteOnFailure = BuildMetadataGenerator.WriteIfValid(failingResult, outputPath);

            Assert.False(wroteOnFailure);
            Assert.False(File.Exists(outputPath));

            var successResult = BuildMetadataGenerator.Generate(dataDir.Path, SchemaDir, "12345", null, null);
            var wroteOnSuccess = BuildMetadataGenerator.WriteIfValid(successResult, outputPath);

            Assert.True(wroteOnSuccess);
            Assert.True(File.Exists(outputPath));
            Assert.Equal(successResult.ManifestJson, File.ReadAllText(outputPath));
        }
        finally
        {
            Directory.Delete(outputDir, recursive: true);
        }
    }
}
