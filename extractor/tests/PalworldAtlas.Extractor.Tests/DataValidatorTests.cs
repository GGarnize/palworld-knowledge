using PalworldAtlas.Extractor;
using Xunit;

namespace PalworldAtlas.Extractor.Tests;

public sealed class DataValidatorTests
{
    private static readonly string FixturesRoot =
        Path.Combine(AppContext.BaseDirectory, "Fixtures", "validate-data");

    private static readonly string SchemaDir = Path.Combine(FixturesRoot, "schemas");
    private static readonly string ValidDir = Path.Combine(FixturesRoot, "valid");
    private static readonly string MutationsDir = Path.Combine(FixturesRoot, "mutations");

    private static readonly string[] DatasetFileNames =
        ["pals.json", "passive-skills.json", "partner-skills.json", "game-settings.json"];

    /// <summary>
    /// Builds a temp data-dir starting from the shared valid fixture, replacing at
    /// most one file with a mutation variant, so every scenario changes exactly one
    /// thing relative to the known-good baseline.
    /// </summary>
    private sealed class TempDataDir : IDisposable
    {
        public string Path { get; }

        /// <param name="targetFileName">Which of the 4 dataset files to replace, e.g. "pals.json".</param>
        /// <param name="mutationFileName">The mutation fixture under Fixtures/validate-data/mutations to use instead of the valid baseline for <paramref name="targetFileName"/>.</param>
        public TempDataDir(string? targetFileName = null, string? mutationFileName = null)
        {
            Path = Directory.CreateTempSubdirectory("palworld-validate-data-").FullName;

            foreach (var fileName in DatasetFileNames)
            {
                var source = fileName == targetFileName
                    ? System.IO.Path.Combine(MutationsDir, mutationFileName!)
                    : System.IO.Path.Combine(ValidDir, fileName);

                File.Copy(source, System.IO.Path.Combine(Path, fileName));
            }
        }

        public void Dispose() => Directory.Delete(Path, recursive: true);
    }

    [Fact]
    public void ValidDatasetPasses()
    {
        using var dataDir = new TempDataDir();

        var report = DataValidator.Validate(dataDir.Path, SchemaDir);

        Assert.True(report.IsValid);
        Assert.Empty(report.Errors);
    }

    [Fact]
    public void WarningsDoNotFailValidation()
    {
        using var dataDir = new TempDataDir();

        var report = DataValidator.Validate(dataDir.Path, SchemaDir);

        // The valid fixture's game-settings.json intentionally omits runtime
        // evidence, which is a real, expected warning - not an error.
        Assert.True(report.IsValid);
        Assert.Contains(report.Warnings, w => w.Message.Contains("runtime evidence"));
    }

    [Fact]
    public void DuplicatePalIdFails()
    {
        using var dataDir = new TempDataDir("pals.json", "pals.duplicate-id.json");

        var report = DataValidator.Validate(dataDir.Path, SchemaDir);

        Assert.False(report.IsValid);
        Assert.Contains(report.Errors, e => e.Message.Contains("duplicate id"));
    }

    [Fact]
    public void MissingPartnerSkillReferenceFails()
    {
        using var dataDir = new TempDataDir("pals.json", "pals.missing-partner-skill-ref.json");

        var report = DataValidator.Validate(dataDir.Path, SchemaDir);

        Assert.False(report.IsValid);
        Assert.Contains(report.Errors, e =>
            e.Message.Contains("partnerSkillId=DoesNotExist") &&
            e.Message.Contains("reference not found in partner-skills.json"));
    }

    [Fact]
    public void MissingNaturalPassiveReferenceFails()
    {
        using var dataDir = new TempDataDir("pals.json", "pals.missing-natural-passive-ref.json");

        var report = DataValidator.Validate(dataDir.Path, SchemaDir);

        Assert.False(report.IsValid);
        Assert.Contains(report.Errors, e =>
            e.Message.Contains("naturalPassive=DoesNotExist") &&
            e.Message.Contains("reference not found in passive-skills.json"));
    }

    [Fact]
    public void MissingPassiveReferenceInPartnerSkillFails()
    {
        using var dataDir = new TempDataDir("partner-skills.json", "partner-skills.missing-passive-ref.json");

        var report = DataValidator.Validate(dataDir.Path, SchemaDir);

        Assert.False(report.IsValid);
        Assert.Contains(report.Errors, e =>
            e.Message.Contains("passiveSkillId=DoesNotExist") &&
            e.Message.Contains("reference not found in passive-skills.json"));
    }

    [Fact]
    public void InconsistentBaseCampRelevantFails()
    {
        using var dataDir = new TempDataDir("partner-skills.json", "partner-skills.inconsistent-base-camp.json");

        var report = DataValidator.Validate(dataDir.Path, SchemaDir);

        Assert.False(report.IsValid);
        Assert.Contains(report.Errors, e => e.Message.Contains("baseCampRelevant=false"));
    }

    [Fact]
    public void EligibleBossFails()
    {
        using var dataDir = new TempDataDir("pals.json", "pals.boss-eligible.json");

        var report = DataValidator.Validate(dataDir.Path, SchemaDir);

        Assert.False(report.IsValid);
        Assert.Contains(report.Errors, e =>
            e.Message.Contains("BOSS_TestPal1") &&
            e.Message.Contains("recommendationEligible=true but variant.kind=boss"));
    }

    [Fact]
    public void DuplicateRankFails()
    {
        using var dataDir = new TempDataDir("partner-skills.json", "partner-skills.duplicate-rank.json");

        var report = DataValidator.Validate(dataDir.Path, SchemaDir);

        Assert.False(report.IsValid);
        Assert.Contains(report.Errors, e => e.Message.Contains("duplicate rank"));
    }

    [Fact]
    public void InvalidTypeInNumericArrayFails()
    {
        using var dataDir = new TempDataDir("pals.json", "pals.invalid-numeric-type.json");

        var report = DataValidator.Validate(dataDir.Path, SchemaDir);

        Assert.False(report.IsValid);
        Assert.Contains(report.Errors, e => e.Message.Contains("pals.json") && e.Message.Contains("runSpeed"));
    }
}
