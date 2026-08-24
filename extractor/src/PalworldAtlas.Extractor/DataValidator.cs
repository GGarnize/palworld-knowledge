using System.Text.Json;
using Json.Schema;

namespace PalworldAtlas.Extractor;

public enum ValidationSeverity { Error, Warning }

public sealed record ValidationIssue(ValidationSeverity Severity, string Message)
{
    public override string ToString() =>
        $"{(Severity == ValidationSeverity.Error ? "ERROR" : "WARNING")} {Message}";
}

public sealed class ValidationReport
{
    private readonly List<ValidationIssue> _issues = [];

    public IReadOnlyList<ValidationIssue> Issues => _issues;
    public IEnumerable<ValidationIssue> Errors => _issues.Where(issue => issue.Severity == ValidationSeverity.Error);
    public IEnumerable<ValidationIssue> Warnings => _issues.Where(issue => issue.Severity == ValidationSeverity.Warning);
    public bool IsValid => !Errors.Any();

    public void Error(string message) => _issues.Add(new ValidationIssue(ValidationSeverity.Error, message));
    public void Warn(string message) => _issues.Add(new ValidationIssue(ValidationSeverity.Warning, message));
}

/// <summary>
/// Deterministic validation for the canonical datasets in data/*.json: JSON-Schema
/// structural checks plus semantic/cross-dataset integrity checks that a schema
/// alone cannot express (uniqueness, cross-file references, derived-field
/// consistency). Deliberately separated from "content changed" concerns - a Pal's
/// runSpeed changing between game patches is never an error here.
/// </summary>
public static class DataValidator
{
    /// <summary>
    /// Shared schema-evaluation options, reused by build-metadata's own
    /// self-validation of the manifest it assembles so "format" (e.g.
    /// date-time) is actually enforced everywhere instead of being a
    /// decorative annotation, and so the two callers can't silently drift.
    /// </summary>
    public static readonly EvaluationOptions SchemaEvaluationOptions = new()
    {
        OutputFormat = OutputFormat.List,
        RequireFormatValidation = true
    };

    public static ValidationReport Validate(string dataDir, string schemaDir)
    {
        var report = new ValidationReport();

        var pals = LoadDataset(dataDir, "pals.json", schemaDir, "pal.schema.json", JsonValueKind.Array, report);
        var passives = LoadDataset(dataDir, "passive-skills.json", schemaDir, "passive-skill.schema.json", JsonValueKind.Array, report);
        var partners = LoadDataset(dataDir, "partner-skills.json", schemaDir, "partner-skill.schema.json", JsonValueKind.Array, report);
        var settings = LoadDataset(dataDir, "game-settings.json", schemaDir, "game-settings.schema.json", JsonValueKind.Object, report);

        var passiveIds = passives is { } p ? CollectIds(p, "id") : null;
        var palIds = pals is { } pl ? CollectIds(pl, "id") : null;
        var partnerPalIds = partners is { } pt ? CollectIds(pt, "palId") : null;

        if (pals is { } palsRoot) ValidatePals(palsRoot, partnerPalIds, passiveIds, report);
        if (passives is { } passivesRoot) ValidatePassives(passivesRoot, report);
        if (partners is { } partnersRoot) ValidatePartnerSkills(partnersRoot, palIds, passiveIds, report);
        if (settings is { } settingsRoot) ValidateGameSettings(settingsRoot, report);

        return report;
    }

    // Loading + schema validation -------------------------------------------

    private static JsonElement? LoadDataset(
        string dataDir,
        string fileName,
        string schemaDir,
        string schemaFileName,
        JsonValueKind expectedKind,
        ValidationReport report)
    {
        var dataPath = Path.Combine(dataDir, fileName);
        var schemaPath = Path.Combine(schemaDir, schemaFileName);

        if (!File.Exists(dataPath))
        {
            report.Error($"{fileName}: file not found at '{dataPath}'.");
            return null;
        }

        if (!File.Exists(schemaPath))
        {
            report.Error($"{fileName}: schema file not found at '{schemaPath}'.");
            return null;
        }

        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(File.ReadAllText(dataPath));
        }
        catch (JsonException exception)
        {
            report.Error($"{fileName}: invalid JSON - {exception.Message}");
            return null;
        }

        foreach (var error in ValidateAgainstSchema(document.RootElement, schemaPath, fileName))
            report.Error(error);

        if (document.RootElement.ValueKind != expectedKind)
        {
            report.Error(
                $"{fileName}: expected top-level {expectedKind.ToString().ToLowerInvariant()}, found {document.RootElement.ValueKind.ToString().ToLowerInvariant()}.");
            return null;
        }

        return document.RootElement;
    }

    /// <summary>
    /// Runs a JSON-Schema evaluation and returns formatted error strings (empty when
    /// valid). Shared so build-metadata's self-check of the manifest it assembles
    /// uses the exact same evaluation path/options as the four dataset validations.
    /// </summary>
    public static IReadOnlyList<string> ValidateAgainstSchema(JsonElement element, string schemaPath, string label)
    {
        JsonSchema schema;
        try
        {
            schema = JsonSchema.FromText(File.ReadAllText(schemaPath));
        }
        catch (Exception exception)
        {
            return [$"{label}: could not load schema '{schemaPath}' - {exception.Message}"];
        }

        var results = schema.Evaluate(element, SchemaEvaluationOptions);
        if (results.IsValid) return [];

        var errors = new List<string>();
        foreach (var detail in results.Details)
        {
            if (detail.IsValid || !detail.HasErrors) continue;

            foreach (var (keyword, message) in detail.Errors!)
                errors.Add($"{label} at '{detail.InstanceLocation}': {message} [{keyword}]");
        }

        return errors;
    }

    private static HashSet<string> CollectIds(JsonElement array, string propertyName)
    {
        var ids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (array.ValueKind != JsonValueKind.Array) return ids;

        foreach (var item in array.EnumerateArray())
        {
            if (item.ValueKind == JsonValueKind.Object &&
                item.TryGetProperty(propertyName, out var idProperty) &&
                idProperty.ValueKind == JsonValueKind.String)
            {
                var id = idProperty.GetString();
                if (!string.IsNullOrEmpty(id)) ids.Add(id);
            }
        }

        return ids;
    }

    internal static string? GetString(JsonElement element, string propertyName) =>
        element.ValueKind == JsonValueKind.Object &&
        element.TryGetProperty(propertyName, out var value) &&
        value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    internal static bool GetBool(JsonElement element, string propertyName, bool fallback = false) =>
        element.ValueKind == JsonValueKind.Object &&
        element.TryGetProperty(propertyName, out var value) &&
        (value.ValueKind == JsonValueKind.True || value.ValueKind == JsonValueKind.False)
            ? value.GetBoolean()
            : fallback;

    internal static bool TryGetObject(JsonElement element, string propertyName, out JsonElement result)
    {
        if (element.ValueKind == JsonValueKind.Object &&
            element.TryGetProperty(propertyName, out result) &&
            result.ValueKind == JsonValueKind.Object)
            return true;

        result = default;
        return false;
    }

    internal static bool TryGetArray(JsonElement element, string propertyName, out JsonElement result)
    {
        if (element.ValueKind == JsonValueKind.Object &&
            element.TryGetProperty(propertyName, out result) &&
            result.ValueKind == JsonValueKind.Array)
            return true;

        result = default;
        return false;
    }

    // pals.json ---------------------------------------------------------------

    private static void ValidatePals(
        JsonElement pals,
        HashSet<string>? partnerPalIds,
        HashSet<string>? passiveIds,
        ValidationReport report)
    {
        var seenIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var pal in pals.EnumerateArray())
        {
            var id = GetString(pal, "id");
            if (string.IsNullOrEmpty(id)) continue; // already a schema error

            if (!seenIds.Add(id))
                report.Error($"pals.json id={id}: duplicate id");

            var hasVariant = TryGetObject(pal, "variant", out var variant);
            var kind = hasVariant ? GetString(variant, "kind") : null;
            var eligible = hasVariant && GetBool(variant, "recommendationEligible");

            if (eligible && !string.Equals(kind, "normal", StringComparison.Ordinal))
                report.Error($"pals.json id={id}: recommendationEligible=true but variant.kind={kind}");

            var partnerSkillId = GetString(pal, "partnerSkillId");
            if (partnerSkillId is not null && partnerPalIds is not null && !partnerPalIds.Contains(partnerSkillId))
                report.Error($"pals.json id={id} partnerSkillId={partnerSkillId}: reference not found in partner-skills.json");

            if (TryGetArray(pal, "naturalPassives", out var naturalPassives) && passiveIds is not null)
            {
                foreach (var entry in naturalPassives.EnumerateArray())
                {
                    var passiveId = entry.ValueKind == JsonValueKind.String ? entry.GetString() : null;
                    if (passiveId is not null && !passiveIds.Contains(passiveId))
                        report.Error($"pals.json id={id} naturalPassive={passiveId}: reference not found in passive-skills.json");
                }
            }

            // Warnings (content-shape, non-blocking) ---------------------------

            if (string.Equals(kind, "unknown", StringComparison.Ordinal))
                report.Warn($"pals.json id={id}: variant.kind=unknown");

            var name = TryGetObject(pal, "name", out var nameElement) ? nameElement : default;
            if (GetString(name, "ptBR") is null)
                report.Warn($"pals.json id={id}: missing PT-BR name");
            if (GetString(name, "enUS") is null)
                report.Warn($"pals.json id={id}: missing EN name");

            if (string.Equals(kind, "normal", StringComparison.Ordinal) && eligible && partnerSkillId is null)
                report.Warn($"pals.json id={id}: normal and recommendation-eligible but has no partnerSkillId");
        }
    }

    // passive-skills.json -------------------------------------------------------

    private static void ValidatePassives(JsonElement passives, ValidationReport report)
    {
        var seenIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var sawNonDisplayable = false;

        foreach (var passive in passives.EnumerateArray())
        {
            var id = GetString(passive, "id");
            if (string.IsNullOrEmpty(id)) continue; // already a schema error

            if (!seenIds.Add(id))
                report.Error($"passive-skills.json id={id}: duplicate id");

            if (string.Equals(GetString(passive, "category"), "SortNotDisplayable", StringComparison.Ordinal))
                sawNonDisplayable = true;
        }

        // Sanity net for "do not filter internal/non-displayable skills" - not a
        // hardcoded row count, just a check that the category isn't entirely absent.
        if (seenIds.Count > 0 && !sawNonDisplayable)
            report.Warn("passive-skills.json: no SortNotDisplayable rows found - dataset may have been pre-filtered");
    }

    // partner-skills.json -----------------------------------------------------

    private static void ValidatePartnerSkills(
        JsonElement partners,
        HashSet<string>? palIds,
        HashSet<string>? passiveIds,
        ValidationReport report)
    {
        var seenPalIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var row in partners.EnumerateArray())
        {
            var palId = GetString(row, "palId");
            if (string.IsNullOrEmpty(palId)) continue; // already a schema error

            if (!seenPalIds.Add(palId))
                report.Error($"partner-skills.json palId={palId}: duplicate palId");

            if (palIds is not null && !palIds.Contains(palId))
                report.Error($"partner-skills.json palId={palId}: reference not found in pals.json");

            var derivedBaseCampRelevant = false;
            var seenRanks = new HashSet<int>();

            if (TryGetArray(row, "ranks", out var ranks))
            {
                foreach (var rank in ranks.EnumerateArray())
                {
                    var rankNumber = rank.TryGetProperty("rank", out var rankValue) &&
                        rankValue.ValueKind == JsonValueKind.Number
                        ? rankValue.GetInt32()
                        : (int?)null;

                    if (rankNumber is { } number && !seenRanks.Add(number))
                        report.Error($"partner-skills.json palId={palId} rank={number}: duplicate rank");

                    if (!TryGetArray(rank, "passiveSkills", out var passiveSkills)) continue;

                    foreach (var skill in passiveSkills.EnumerateArray())
                    {
                        var skillId = GetString(skill, "id");
                        var unresolved = GetBool(skill, "unresolved");

                        var hasActivation = TryGetObject(skill, "activation", out var activation);
                        if (hasActivation && (GetBool(activation, "baseCamp") || GetBool(activation, "worker")))
                            derivedBaseCampRelevant = true;

                        if (string.IsNullOrEmpty(skillId)) continue;

                        if (passiveIds is null) continue;

                        var exists = passiveIds.Contains(skillId);

                        if (unresolved)
                        {
                            if (exists)
                                report.Error(
                                    $"partner-skills.json palId={palId} rank={rankNumber} passiveSkillId={skillId}: unresolved=true but reference exists in passive-skills.json");
                        }
                        else if (!exists)
                        {
                            report.Error(
                                $"partner-skills.json palId={palId} rank={rankNumber} passiveSkillId={skillId}: reference not found in passive-skills.json");
                        }
                    }
                }
            }

            var baseCampRelevant = GetBool(row, "baseCampRelevant");
            if (baseCampRelevant != derivedBaseCampRelevant)
                report.Error(
                    $"partner-skills.json palId={palId}: baseCampRelevant={baseCampRelevant.ToString().ToLowerInvariant()} but value derived from activation.baseCamp/worker is {derivedBaseCampRelevant.ToString().ToLowerInvariant()}");

            // Warning: active-skill arrays with an unusual length relative to the
            // row's own rank count / to each other (not necessarily wrong - some
            // skills genuinely omit a rank's value - but worth a human's attention).
            if (TryGetObject(row, "activeSkill", out var activeSkill))
            {
                var rankCount = seenRanks.Count;
                var mainLength = ArrayLength(activeSkill, "mainValueByRank");
                var coolLength = ArrayLength(activeSkill, "overwriteCoolTimeByRank");
                var effectLength = ArrayLength(activeSkill, "overwriteEffectTimeByRank");

                var unusual =
                    (mainLength > 0 && rankCount > 0 && mainLength != rankCount) ||
                    (coolLength > 0 && mainLength > 0 && coolLength != mainLength) ||
                    (effectLength > 0 && mainLength > 0 && effectLength != mainLength);

                if (unusual)
                    report.Warn(
                        $"partner-skills.json palId={palId}: unusual active-skill array lengths (ranks={rankCount}, mainValueByRank={mainLength}, overwriteCoolTimeByRank={coolLength}, overwriteEffectTimeByRank={effectLength})");
            }
        }
    }

    private static int ArrayLength(JsonElement element, string propertyName) =>
        TryGetArray(element, propertyName, out var array) ? array.GetArrayLength() : 0;

    // game-settings.json --------------------------------------------------------

    private static void ValidateGameSettings(JsonElement settings, ValidationReport report)
    {
        var hasTransport = TryGetObject(settings, "transport", out _);
        var applied = TryGetObject(settings, "runtimeEvidence", out var runtimeEvidence) &&
            GetBool(runtimeEvidence, "applied");

        if (applied != hasTransport)
            report.Error(
                $"game-settings.json: runtimeEvidence.applied={applied.ToString().ToLowerInvariant()} but transport is {(hasTransport ? "present" : "absent")}");

        if (!hasTransport)
            report.Warn("game-settings.json: no runtime evidence supplied - 'transport' is absent (optional)");
    }
}
