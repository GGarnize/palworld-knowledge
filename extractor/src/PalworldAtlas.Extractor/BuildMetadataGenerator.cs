using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace PalworldAtlas.Extractor;

public sealed record DatasetInfo(string File, int? Rows, string Sha256);

/// <summary>
/// Outcome of <see cref="BuildMetadataGenerator.Generate"/>. <see cref="Success"/> is
/// false whenever validate-data found an error OR the assembled manifest itself fails
/// build.schema.json - in both cases <see cref="ManifestJson"/> is null and nothing
/// should be written.
/// </summary>
public sealed class BuildMetadataResult
{
    public required ValidationReport ValidationReport { get; init; }
    public IReadOnlyList<string> ComparisonWarnings { get; init; } = [];
    public IReadOnlyList<string> SchemaErrors { get; init; } = [];
    public string? ManifestJson { get; init; }

    public bool Success => ValidationReport.IsValid && SchemaErrors.Count == 0 && ManifestJson is not null;
}

/// <summary>
/// Assembles data/build.json: extractor identity, per-dataset row counts/SHA-256
/// hashes, the validate-data summary, and an optional comparison against a previous
/// build.json. Pure - never writes to disk itself (see <see cref="WriteIfValid"/>) so
/// it can be unit tested without touching the filesystem beyond reading fixtures.
/// </summary>
public static class BuildMetadataGenerator
{
    /// <summary>A row-count drop at or beyond this percentage (vs. the previous build) is a risk warning.</summary>
    public const double RowCountDropRiskThresholdPercent = 5.0;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    public static BuildMetadataResult Generate(
        string dataDir,
        string schemaDir,
        string buildId,
        string? gameVersion,
        string? previousBuildPath)
    {
        // Reuse DataValidator wholesale - build-metadata must never re-implement or
        // drift from what validate-data considers an error vs a warning.
        var report = DataValidator.Validate(dataDir, schemaDir);

        if (!report.IsValid)
            return new BuildMetadataResult { ValidationReport = report };

        var pals = DescribeDataset(dataDir, "pals.json", isArray: true);
        var passiveSkills = DescribeDataset(dataDir, "passive-skills.json", isArray: true);
        var partnerSkills = DescribeDataset(dataDir, "partner-skills.json", isArray: true);
        var gameSettings = DescribeDataset(dataDir, "game-settings.json", isArray: false);

        var currentDatasets = new Dictionary<string, DatasetInfo>(StringComparer.Ordinal)
        {
            ["pals"] = pals,
            ["passiveSkills"] = passiveSkills,
            ["partnerSkills"] = partnerSkills,
            ["gameSettings"] = gameSettings
        };

        var comparisonWarnings = new List<string>();
        object previousBuildComparison = new { available = false };

        if (!string.IsNullOrWhiteSpace(previousBuildPath))
        {
            // A path that doesn't exist is almost certainly an operator mistake
            // (typo, stale CI variable) - fail loudly instead of silently
            // producing a build.json with a comparison the caller expected.
            if (!File.Exists(previousBuildPath))
                throw new InvalidOperationException($"--previous-build file not found: '{previousBuildPath}'.");

            previousBuildComparison = BuildPreviousComparison(previousBuildPath, currentDatasets, comparisonWarnings);
        }

        var metadata = new
        {
            steamBuildId = buildId,
            gameVersion,
            generatedAtUtc = DateTime.UtcNow.ToString("o"),
            extractor = new { name = "PalworldAtlas.Extractor", version = GetExtractorVersion() },
            datasets = new
            {
                pals = new { file = pals.File, rows = pals.Rows, sha256 = pals.Sha256 },
                passiveSkills = new { file = passiveSkills.File, rows = passiveSkills.Rows, sha256 = passiveSkills.Sha256 },
                partnerSkills = new { file = partnerSkills.File, rows = partnerSkills.Rows, sha256 = partnerSkills.Sha256 },
                gameSettings = new { file = gameSettings.File, rows = gameSettings.Rows, sha256 = gameSettings.Sha256 }
            },
            validation = new { errors = report.Errors.Count(), warnings = report.Warnings.Count() },
            previousBuildComparison
        };

        var json = JsonSerializer.Serialize(metadata, JsonOptions);

        // The manifest we just assembled must itself satisfy build.schema.json -
        // this is a self-check, not decoration: a bug here (or a hand-edited,
        // broken schema file) must block the write exactly like a bad dataset does.
        using var manifestDocument = JsonDocument.Parse(json);
        var schemaErrors = DataValidator.ValidateAgainstSchema(
            manifestDocument.RootElement,
            Path.Combine(schemaDir, "build.schema.json"),
            "build.json");

        return new BuildMetadataResult
        {
            ValidationReport = report,
            ComparisonWarnings = comparisonWarnings,
            SchemaErrors = schemaErrors,
            ManifestJson = schemaErrors.Count == 0 ? json : null
        };
    }

    /// <summary>
    /// Atomic write: stages the manifest under a temp name in the same directory,
    /// then replaces the output - a reader never observes a partially-written or
    /// truncated build.json. Writes nothing and returns false when <see cref="BuildMetadataResult.Success"/> is false.
    /// </summary>
    public static bool WriteIfValid(BuildMetadataResult result, string output)
    {
        if (!result.Success) return false;

        var outputFullPath = Path.GetFullPath(output);
        Directory.CreateDirectory(Path.GetDirectoryName(outputFullPath)!);

        var tempPath = outputFullPath + $".tmp-{Guid.NewGuid():N}";
        File.WriteAllText(tempPath, result.ManifestJson);
        File.Move(tempPath, outputFullPath, overwrite: true);

        return true;
    }

    private static DatasetInfo DescribeDataset(string dataDir, string fileName, bool isArray)
    {
        var path = Path.Combine(dataDir, fileName);
        var bytes = File.ReadAllBytes(path);

        int? rows = null;
        if (isArray)
        {
            using var document = JsonDocument.Parse(bytes);
            rows = document.RootElement.ValueKind == JsonValueKind.Array
                ? document.RootElement.GetArrayLength()
                : 0;
        }

        return new DatasetInfo(fileName, rows, Convert.ToHexStringLower(SHA256.HashData(bytes)));
    }

    private static string GetExtractorVersion()
    {
        var assembly = typeof(BuildMetadataGenerator).Assembly;

        var informational = assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?
            .InformationalVersion;

        if (!string.IsNullOrWhiteSpace(informational))
            return informational;

        var version = assembly.GetName().Version;
        if (version is not null)
            return version.ToString();

        // Real fallback documented per task: assembly metadata is expected to
        // always be present for a built assembly, but this keeps the field
        // well-defined if that ever stops being true.
        return "dev";
    }

    private static object BuildPreviousComparison(
        string previousBuildPath,
        IReadOnlyDictionary<string, DatasetInfo> current,
        List<string> warnings)
    {
        JsonDocument previousDocument;
        try
        {
            previousDocument = JsonDocument.Parse(File.ReadAllText(previousBuildPath));
        }
        catch (JsonException exception)
        {
            warnings.Add($"--previous-build '{previousBuildPath}' is not valid JSON ({exception.Message}); comparison skipped.");
            return new { available = false, issue = "invalid JSON" };
        }

        var previousRoot = previousDocument.RootElement;

        if (!DataValidator.TryGetObject(previousRoot, "datasets", out var previousDatasets))
        {
            warnings.Add($"--previous-build '{previousBuildPath}' has no 'datasets' section; comparison skipped.");
            return new { available = false, issue = "missing 'datasets'" };
        }

        var previousBuildId = DataValidator.GetString(previousRoot, "steamBuildId");
        var previousGeneratedAtUtc = DataValidator.GetString(previousRoot, "generatedAtUtc");

        var datasetComparisons = new Dictionary<string, object>(StringComparer.Ordinal);
        var changedDatasets = new List<string>();
        var riskWarnings = new List<string>();

        foreach (var (key, info) in current)
        {
            if (!DataValidator.TryGetObject(previousDatasets, key, out var previousInfo))
            {
                riskWarnings.Add($"{key}: not present in previous build - cannot compare.");
                datasetComparisons[key] = new { available = false };
                continue;
            }

            var previousSha = DataValidator.GetString(previousInfo, "sha256");
            var previousRows = previousInfo.TryGetProperty("rows", out var rowsElement) &&
                rowsElement.ValueKind == JsonValueKind.Number
                ? rowsElement.GetInt32()
                : (int?)null;

            var hashChanged = !string.Equals(previousSha, info.Sha256, StringComparison.OrdinalIgnoreCase);
            if (hashChanged) changedDatasets.Add(key);

            int? delta = null;
            double? deltaPercent = null;

            if (previousRows is { } previousCount && info.Rows is { } currentCount)
            {
                delta = currentCount - previousCount;

                if (previousCount == 0)
                {
                    deltaPercent = currentCount == 0 ? 0 : null;
                }
                else
                {
                    deltaPercent = Math.Round(delta.Value / (double)previousCount * 100.0, 2);

                    if (currentCount == 0)
                        riskWarnings.Add($"{key}: dropped from {previousCount} rows to 0 (previously non-empty, now empty).");
                    else if (deltaPercent <= -RowCountDropRiskThresholdPercent)
                        riskWarnings.Add(
                            $"{key}: row count dropped {Math.Abs(deltaPercent.Value):0.0}% ({previousCount} -> {currentCount}).");
                }
            }

            datasetComparisons[key] = new
            {
                available = true,
                previousRows,
                currentRows = info.Rows,
                delta,
                deltaPercent,
                hashChanged
            };
        }

        // Surface risk warnings the same way as file-level comparison warnings -
        // they must not only live inside the written JSON.
        warnings.AddRange(riskWarnings);

        return new
        {
            available = true,
            previousBuildId,
            previousGeneratedAtUtc,
            datasets = datasetComparisons,
            changedDatasets,
            riskWarnings
        };
    }
}
