using System.Text.Json;
using System.Text.Json.Serialization;
using CUE4Parse.UE4.Assets.Objects;

namespace PalworldAtlas.Extractor;

internal static class Program
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) }
    };

    public static int Main(string[] args)
    {
        try
        {
            if (args.Length == 0 || args[0] is "-h" or "--help")
            {
                PrintUsage();
                return 0;
            }

            var command = args[0].ToLowerInvariant();
            var options = ParseOptions(args[1..]);

            // validate-data and build-metadata work purely off already-generated
            // data/*.json files and schemas/*.schema.json - they never touch the
            // game PAK, so they must not be forced through the --pak-dir
            // requirement every other command has.
            if (command == "validate-data")
                return ValidateData(
                    Required(options, "data-dir"),
                    Required(options, "schema-dir"));

            if (command == "build-metadata")
                return BuildMetadata(
                    Required(options, "data-dir"),
                    Required(options, "schema-dir"),
                    Required(options, "build-id"),
                    options.GetValueOrDefault("game-version"),
                    Required(options, "output"),
                    options.GetValueOrDefault("previous-build"));

            var pakDirectory = Required(options, "pak-dir");
            var buildId = options.GetValueOrDefault("build-id", "unknown");
            var mappings = options.GetValueOrDefault("mappings");

            return command switch
            {
                "probe" => Probe(
                    pakDirectory,
                    mappings,
                    buildId,
                    Required(options, "output")),

                "publish" => Publish(
                    pakDirectory,
                    mappings,
                    buildId,
                    Required(options, "output"),
                    options.GetValueOrDefault("game-version"),
                    options.GetValueOrDefault("previous-manifest")),

                "inventory" => Inventory(
                    pakDirectory,
                    mappings,
                    Required(options, "contains")),

                "dump-table" => DumpTable(
                    pakDirectory,
                    mappings,
                    Required(options, "table"),
                    Required(options, "output")),

                "probe-table" => ProbeTable(
                    pakDirectory,
                    mappings,
                    Required(options, "table")),

                "inspect-row" => InspectRow(
                    pakDirectory,
                    mappings,
                    Required(options, "table"),
                    Required(options, "row")),
                
                "inspect-row-deep" => InspectRowDeep(
                    pakDirectory,
                    mappings,
                    Required(options, "table"),
                    Required(options, "row")),
                
                "normalize-passives" => NormalizePassives(
                    pakDirectory,
                    mappings,
                    Required(options, "output")),

                "normalize-pals" => NormalizePals(
                    pakDirectory,
                    mappings,
                    Required(options, "output")),

                "normalize-partner-skills" => NormalizePartnerSkills(
                    pakDirectory,
                    mappings,
                    Required(options, "output")),

                "inspect-asset" => InspectAsset(
                    pakDirectory,
                    mappings,
                    Required(options, "asset"),
                    options.GetValueOrDefault("contains")),

                "normalize-game-settings" => NormalizeGameSettings(
                    pakDirectory,
                    mappings,
                    Required(options, "output"),
                    options.GetValueOrDefault("runtime-evidence")),

                "inspect-mapping" => InspectMapping(
                    pakDirectory,
                    mappings,
                    Required(options, "contains")),
                    
                _ => throw new ArgumentException($"Unknown command '{command}'"),
            };
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine($"{exception.GetType().Name}: {exception.Message}");
            return 1;
        }
    }

    private static int ValidateData(string dataDir, string schemaDir)
    {
        var report = DataValidator.Validate(dataDir, schemaDir);

        foreach (var issue in report.Issues)
            Console.WriteLine(issue.ToString());

        Console.WriteLine(
            $"validate-data: {report.Errors.Count()} error(s), {report.Warnings.Count()} warning(s).");

        return report.IsValid ? 0 : 1;
    }

    private static int BuildMetadata(
        string dataDir,
        string schemaDir,
        string buildId,
        string? gameVersion,
        string output,
        string? previousBuildPath)
    {
        var result = BuildMetadataGenerator.Generate(dataDir, schemaDir, buildId, gameVersion, previousBuildPath);

        foreach (var issue in result.ValidationReport.Issues)
            Console.WriteLine(issue.ToString());

        if (!result.ValidationReport.IsValid)
        {
            Console.Error.WriteLine(
                $"build-metadata: {result.ValidationReport.Errors.Count()} error(s) from validate-data - refusing to generate '{output}'.");
            return 1;
        }

        if (result.SchemaErrors.Count > 0)
        {
            // The manifest we just assembled failed to satisfy build.schema.json -
            // this is a self-check, not decoration: a bug here must block the
            // write exactly like a bad dataset blocks it above.
            foreach (var error in result.SchemaErrors)
                Console.Error.WriteLine($"ERROR {error}");

            Console.Error.WriteLine(
                $"build-metadata: assembled manifest fails build.schema.json - refusing to write '{output}'.");
            return 1;
        }

        BuildMetadataGenerator.WriteIfValid(result, output);

        foreach (var warning in result.ComparisonWarnings)
            Console.WriteLine($"WARNING build-metadata: {warning}");

        Console.WriteLine(
            $"build-metadata: wrote '{Path.GetFullPath(output)}' (errors=0, warnings={result.ValidationReport.Warnings.Count()}).");

        return 0;
    }

    private static int Probe(string pakDirectory, string? mappings, string buildId, string output)
    {
        var report = ProbeRunner.Run(pakDirectory, mappings, buildId);
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(output))!);
        File.WriteAllText(output, JsonSerializer.Serialize(report, JsonOptions));
        Console.WriteLine($"Probe complete: {report.Tables.Count(table => table.Parsed)}/{report.Tables.Count} tables parsed");
        return report.ProductionGatePassed ? 0 : 2;
    }

    private static int Publish(string pakDirectory, string? mappings, string buildId, string output, string? gameVersion, string? previousManifest)
    {
        if (buildId == "unknown") throw new ArgumentException("--build-id is required for publish");
        using var workspace = new PakWorkspace(pakDirectory, mappings);
        new AtlasPublisher(workspace).Publish(output, buildId, gameVersion, previousManifest);
        Console.WriteLine($"Published validated build {buildId} to {Path.GetFullPath(output)}");
        return 0;
    }
    private static int Inventory(
        string pakDirectory,
        string? mappings,
        string contains)
    {
        using var workspace = new PakWorkspace(pakDirectory, mappings);

        var files = workspace.FindFiles(contains);

        Console.WriteLine($"Found {files.Count} file(s) containing '{contains}':");

        foreach (var file in files)
            Console.WriteLine(file);

        return 0;
    }
    private static int InspectRow(
        string pakDirectory,
        string? mappings,
        string tablePath,
        string rowId)
    {
        using var workspace = new PakWorkspace(pakDirectory, mappings);

        var table = workspace.LoadArbitraryTable(tablePath);

        if (table is null)
            throw new InvalidOperationException(
                $"Could not load table '{tablePath}'.");

        var found = table.RowMap
            .Where(entry =>
                entry.Key.Text.Equals(
                    rowId,
                    StringComparison.OrdinalIgnoreCase))
            .Select(entry => new
            {
                Found = true,
                Entry = entry
            })
            .FirstOrDefault();

        if (found is null)
            throw new InvalidOperationException(
                $"Row '{rowId}' not found.");

        var match = found.Entry;

        Console.WriteLine($"Table: {tablePath}");
        Console.WriteLine($"Row:   {match.Key.Text}");
        Console.WriteLine();

        var reader = new RowReader(match.Value);

        foreach (var field in reader.InspectFields())
            Console.WriteLine(
                $"{field.Name} [{field.Type}] = {field.Value}");

        return 0;
    }
    private static int InspectRowDeep(
        string pakDirectory,
        string? mappings,
        string tablePath,
        string rowId)
    {
        using var workspace = new PakWorkspace(pakDirectory, mappings);

        var table = workspace.LoadArbitraryTable(tablePath);

        if (table is null)
            throw new InvalidOperationException(
                $"Could not load table '{tablePath}'.");

        var match = table.RowMap
            .FirstOrDefault(entry =>
                entry.Key.Text.Equals(
                    rowId,
                    StringComparison.OrdinalIgnoreCase));

        if (match.Value is null)
            throw new InvalidOperationException(
                $"Row '{rowId}' not found.");

        Console.WriteLine($"Table: {tablePath}");
        Console.WriteLine($"Row:   {match.Key.Text}");
        Console.WriteLine();

        var reader = new RowReader(match.Value);

        foreach (var field in reader.FieldNames)
        {
            Console.WriteLine(
                $"{field} = {DescribeValue(reader.RawValue(field))}");
        }

        return 0;
    }
    private static int DumpTable(
        string pakDirectory,
        string? mappings,
        string tablePath,
        string output)
    {
        using var workspace = new PakWorkspace(pakDirectory, mappings);

        var table = workspace.LoadArbitraryTable(tablePath);

        if (table is null)
            throw new InvalidOperationException($"Could not load table '{tablePath}'.");

        var rows = table.RowMap
            .Select(entry =>
            {
                var reader = new RowReader(entry.Value);

                var fields = reader.FieldNames.ToDictionary(
                    field => field,
                    field => DescribeValue(reader.RawValue(field)),
                    StringComparer.OrdinalIgnoreCase);

                return new
                {
                    id = entry.Key.Text,
                    fields
                };
            })
            .ToArray();

        Directory.CreateDirectory(
            Path.GetDirectoryName(Path.GetFullPath(output))!);

        File.WriteAllText(
            output,
            JsonSerializer.Serialize(rows, JsonOptions));

        Console.WriteLine(
            $"Dumped {rows.Length} row(s) from '{tablePath}' to '{Path.GetFullPath(output)}'");

        return 0;
    }
    private static int ProbeTable(
        string pakDirectory,
        string? mappings,
        string tablePath)
    {
        using var workspace = new PakWorkspace(pakDirectory, mappings);

        var table = workspace.LoadArbitraryTable(tablePath);

        if (table is null)
            throw new InvalidOperationException($"Could not load table '{tablePath}'.");

        var fields = table.RowMap.Values
            .Take(50)
            .SelectMany(row => new RowReader(row).FieldNames)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Order(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        Console.WriteLine($"Table: {tablePath}");
        Console.WriteLine($"Rows: {table.RowMap.Count}");
        Console.WriteLine($"Fields: {fields.Length}");

        foreach (var field in fields)
            Console.WriteLine(field);

        return 0;
    }
    private static int NormalizePassives(
        string pakDirectory,
        string? mappings,
        string output)
    {
        using var workspace = new PakWorkspace(pakDirectory, mappings);

        const string passiveTablePath =
            "Pal/Content/Pal/DataTable/PassiveSkill/DT_PassiveSkill_Main";

        const string ptNamePath =
            "Pal/Content/L10N/pt-BR/Pal/DataTable/Text/DT_SkillNameText_Common";

        const string enNamePath =
            "Pal/Content/L10N/en/Pal/DataTable/Text/DT_SkillNameText_Common";

        const string ptDescPath =
            "Pal/Content/L10N/pt-BR/Pal/DataTable/Text/DT_SkillDescText_Common";

        const string enDescPath =
            "Pal/Content/L10N/en/Pal/DataTable/Text/DT_SkillDescText_Common";

        var passiveTable = workspace.LoadArbitraryTable(passiveTablePath)
            ?? throw new InvalidOperationException(
                $"Could not load '{passiveTablePath}'.");

        var ptNameTable = workspace.LoadArbitraryTable(ptNamePath)
            ?? throw new InvalidOperationException(
                $"Could not load '{ptNamePath}'.");

        var enNameTable = workspace.LoadArbitraryTable(enNamePath)
            ?? throw new InvalidOperationException(
                $"Could not load '{enNamePath}'.");

        var ptDescTable = workspace.LoadArbitraryTable(ptDescPath)
            ?? throw new InvalidOperationException(
                $"Could not load '{ptDescPath}'.");

        var enDescTable = workspace.LoadArbitraryTable(enDescPath)
            ?? throw new InvalidOperationException(
                $"Could not load '{enDescPath}'.");

        static Dictionary<string, string> ReadTextTable(
            CUE4Parse.UE4.Assets.Exports.Engine.UDataTable table)
        {
            return table.RowMap.ToDictionary(
                entry => entry.Key.Text,
                entry => new RowReader(entry.Value)
                    .String("", "TextData"),
                StringComparer.OrdinalIgnoreCase);
        }

        var ptNames = ReadTextTable(ptNameTable);
        var enNames = ReadTextTable(enNameTable);
        var ptDescriptions = ReadTextTable(ptDescTable);
        var enDescriptions = ReadTextTable(enDescTable);

        static string StripEnumPrefix(string value)
        {
            var separator = value.LastIndexOf("::", StringComparison.Ordinal);
            return separator >= 0
                ? value[(separator + 2)..]
                : value;
        }

        var normalized = passiveTable.RowMap
            .Select(entry =>
            {
                var id = entry.Key.Text;
                var reader = new RowReader(entry.Value);

                var overrideName = reader.String(
                    "None",
                    "OverrideNameTextID");

                var overrideDesc = reader.String(
                    "None",
                    "OverrideDescMsgID");

                var defaultLocalizationId = $"PASSIVE_{id}";

                var nameId =
                    !string.Equals(
                        overrideName,
                        "None",
                        StringComparison.OrdinalIgnoreCase)
                        ? overrideName
                        : defaultLocalizationId;

                var descriptionId =
                    !string.Equals(
                        overrideDesc,
                        "None",
                        StringComparison.OrdinalIgnoreCase)
                        ? overrideDesc
                        : defaultLocalizationId;

                var effects = new List<object>();

                for (var index = 1; index <= 4; index++)
                {
                    var typeRaw = reader.String(
                        "EPalPassiveSkillEffectType::no",
                        $"EffectType{index}");

                    var type = StripEnumPrefix(typeRaw);

                    if (string.Equals(
                            type,
                            "no",
                            StringComparison.OrdinalIgnoreCase))
                        continue;

                    var targetRaw = reader.String(
                        "EPalPassiveSkillEffectTargetType::None",
                        $"TargetType{index}");

                    effects.Add(new
                    {
                        type,
                        value = reader.Number(
                            0,
                            $"EffectValue{index}"),
                        target = StripEnumPrefix(targetRaw)
                    });
                }

                var category = StripEnumPrefix(
                    reader.String(
                        "EPalPassiveCategory::None",
                        "Category"));

                var element = StripEnumPrefix(
                    reader.String(
                        "EPalElementType::None",
                        "TargetElementType"));

                ptNames.TryGetValue(nameId, out var ptName);
                enNames.TryGetValue(nameId, out var enName);

                ptDescriptions.TryGetValue(
                    descriptionId,
                    out var ptDescription);

                enDescriptions.TryGetValue(
                    descriptionId,
                    out var enDescription);

                return new
                {
                    id,

                    localization = new
                    {
                        nameId,
                        descriptionId
                    },

                    name = new
                    {
                        ptBR = string.IsNullOrWhiteSpace(ptName)
                            ? null
                            : ptName,

                        enUS = string.IsNullOrWhiteSpace(enName)
                            ? null
                            : enName
                    },

                    description = new
                    {
                        ptBR = string.IsNullOrWhiteSpace(ptDescription)
                            ? null
                            : ptDescription,

                        enUS = string.IsNullOrWhiteSpace(enDescription)
                            ? null
                            : enDescription
                    },

                    category,

                    rank = reader.Int(0, "Rank"),

                    lotteryWeight =
                        reader.Int(0, "LotteryWeight"),

                    targetElement =
                        string.Equals(
                            element,
                            "None",
                            StringComparison.OrdinalIgnoreCase)
                            ? null
                            : element,

                    availability = new
                    {
                        pal =
                            reader.Bool(false, "AddPal"),

                        rarePal =
                            reader.Bool(false, "AddRarePal"),

                        mutationPal =
                            reader.Bool(false, "AddMutationPal"),

                        worldTreePal =
                            reader.Bool(false, "AddWorldTreePal"),

                        accessory =
                            reader.Bool(false, "AddAccessory"),

                        armor =
                            reader.Bool(false, "AddArmor"),

                        meleeWeapon =
                            reader.Bool(false, "AddMeleeWeapon"),

                        shotWeapon =
                            reader.Bool(false, "AddShotWeapon")
                    },

                    activation = new
                    {
                        always =
                            reader.Bool(false, "InvokeAlways"),

                        worker =
                            reader.Bool(false, "InvokeWorker"),

                        baseCamp =
                            reader.Bool(false, "InvokeInBaseCamp"),

                        otomo =
                            reader.Bool(false, "InvokeInOtomo"),

                        activeOtomo =
                            reader.Bool(false, "InvokeActiveOtomo"),

                        reserve =
                            reader.Bool(false, "InvokeReserve"),

                        riding =
                            reader.Bool(false, "InvokeRiding")
                    },

                    stackablePartnerSkillBySameTribe =
                        reader.Bool(
                            false,
                            "IsStackablePartnerSkillBySameTribe"),

                    effects
                };
            })
            .ToArray();

        Directory.CreateDirectory(
            Path.GetDirectoryName(
                Path.GetFullPath(output))!);

        File.WriteAllText(
            output,
            JsonSerializer.Serialize(
                normalized,
                JsonOptions));

        var withPtName = normalized.Count(
            row => row.name.ptBR is not null);

        var withEnName = normalized.Count(
            row => row.name.enUS is not null);

        var displayable = normalized.Count(
            row => string.Equals(
                row.category,
                "SortDisplayable",
                StringComparison.OrdinalIgnoreCase));

        var addPal = normalized.Count(
            row => row.availability.pal);

        Console.WriteLine(
            $"Normalized {normalized.Length} passive rows.");

        Console.WriteLine(
            $"PT-BR names: {withPtName}");

        Console.WriteLine(
            $"EN names:    {withEnName}");

        Console.WriteLine(
            $"Displayable: {displayable}");

        Console.WriteLine(
            $"AddPal:      {addPal}");

        Console.WriteLine(
            $"Output:      {Path.GetFullPath(output)}");

        return 0;
    }
    private static int NormalizePals(
        string pakDirectory,
        string? mappings,
        string output)
    {
        using var workspace = new PakWorkspace(pakDirectory, mappings);

        const string palTablePath =
            "Pal/Content/Pal/DataTable/Character/DT_PalMonsterParameter";

        const string ptNamePath =
            "Pal/Content/L10N/pt-BR/Pal/DataTable/Text/DT_PalNameText_Common";

        const string enNamePath =
            "Pal/Content/L10N/en/Pal/DataTable/Text/DT_PalNameText_Common";

        const string partnerSkillTablePath =
            "Pal/Content/Pal/DataTable/PassiveSkill/DT_PartnerSkillParameter";

        var palTable = workspace.LoadArbitraryTable(palTablePath)
            ?? throw new InvalidOperationException(
                $"Could not load '{palTablePath}'.");

        var ptNameTable = workspace.LoadArbitraryTable(ptNamePath)
            ?? throw new InvalidOperationException(
                $"Could not load '{ptNamePath}'.");

        var enNameTable = workspace.LoadArbitraryTable(enNamePath)
            ?? throw new InvalidOperationException(
                $"Could not load '{enNamePath}'.");

        // Loaded only to check row existence for partnerSkillId - the full
        // partner-skill payload (ranks, effects, cool times, ...) already
        // lives in partner-skills.json and must not be duplicated here.
        var partnerSkillTable = workspace.LoadArbitraryTable(partnerSkillTablePath)
            ?? throw new InvalidOperationException(
                $"Could not load '{partnerSkillTablePath}'.");

        var partnerSkillIds = new HashSet<string>(
            partnerSkillTable.RowMap.Select(entry => entry.Key.Text),
            StringComparer.OrdinalIgnoreCase);

        static Dictionary<string, string> ReadTextTable(
            CUE4Parse.UE4.Assets.Exports.Engine.UDataTable table)
        {
            return table.RowMap.ToDictionary(
                entry => entry.Key.Text,
                entry => new RowReader(entry.Value)
                    .String("", "TextData", "Text", "Value"),
                StringComparer.OrdinalIgnoreCase);
        }

        static string StripEnumPrefix(string value)
        {
            var separator = value.LastIndexOf(
                "::",
                StringComparison.Ordinal);

            return separator >= 0
                ? value[(separator + 2)..]
                : value;
        }

        var ptNames = ReadTextTable(ptNameTable);
        var enNames = ReadTextTable(enNameTable);

        static string? CleanPassive(string value)
        {
            var result = StripEnumPrefix(value);

            if (string.IsNullOrWhiteSpace(result) ||
                result.Equals("None", StringComparison.OrdinalIgnoreCase))
                return null;

            return result;
        }

        var palIds = new HashSet<string>(
            palTable.RowMap.Select(entry => entry.Key.Text),
            StringComparer.OrdinalIgnoreCase);

        // Documented technical-variant heuristic ---------------------------
        // DT_PalMonsterParameter has no IsPal=false rows today, and most
        // non-recommendable content is already caught by IsBoss / IsTowerBoss
        // / IsRaidBoss. A handful of scripted/event-bound duplicates (a
        // quest-locked encounter, a "police" event spawn, a tower boss's own
        // otomo companion) are not flagged as boss content, so they fall
        // back to this narrow, evidence-based check: the row has no real
        // Paldex entry (ZukanIndex <= 0) AND stripping a known
        // scripted-context prefix/suffix yields a tribe id that already
        // exists as its own, independently classified row in this table.
        string[] technicalSuffixes = ["_Quest_Friend", "_Quest_Enemy", "_Quest", "_Otomo"];
        string[] technicalPrefixes = ["POLICE_"];

        bool IsDocumentedTechnicalVariant(string rowId, int zukanIndex)
        {
            if (zukanIndex > 0) return false;

            string? baseId = null;

            foreach (var suffix in technicalSuffixes)
            {
                if (rowId.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
                {
                    baseId = rowId[..^suffix.Length];
                    break;
                }
            }

            baseId ??= technicalPrefixes
                .Where(prefix => rowId.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                .Select(prefix => rowId[prefix.Length..])
                .FirstOrDefault();

            if (baseId is null) return false;

            if (palIds.Contains(baseId)) return true;

            // PREDATOR_-prefixed quest duplicates reference the plain tribe,
            // e.g. PREDATOR_FlowerRabbit_Quest -> FlowerRabbit.
            const string predatorPrefix = "PREDATOR_";
            return baseId.StartsWith(predatorPrefix, StringComparison.OrdinalIgnoreCase) &&
                palIds.Contains(baseId[predatorPrefix.Length..]);
        }

        static string DetermineVariantKind(
            bool isPal, bool isBoss, bool isTowerBoss, bool isRaidBoss, bool isTechnicalVariant)
        {
            if (isTowerBoss) return "towerBoss";
            if (isRaidBoss) return "raidBoss";
            if (isBoss) return "boss";
            if (!isPal) return "unknown";
            return isTechnicalVariant ? "technical" : "normal";
        }

        var normalized = palTable.RowMap
            .Select(entry =>
            {
                var id = entry.Key.Text;
                var reader = new RowReader(entry.Value);

                var tribe = StripEnumPrefix(
                    reader.String(id, "Tribe"));

                var isPal = reader.Bool(false, "IsPal");
                var isBoss = reader.Bool(false, "IsBoss");
                var isTowerBoss = reader.Bool(false, "IsTowerBoss");
                var isRaidBoss = reader.Bool(false, "IsRaidBoss");

                var zukanIndex = reader.Int(-1, "ZukanIndex", "PalDexNum");

                var isTechnicalVariant =
                    !isTowerBoss && !isRaidBoss && !isBoss &&
                    IsDocumentedTechnicalVariant(id, zukanIndex);

                var variantKind = DetermineVariantKind(
                    isPal, isBoss, isTowerBoss, isRaidBoss, isTechnicalVariant);

                var bpClass = reader.String(id, "BPClass");

                var zukanIndexSuffix = reader.String("", "ZukanIndexSuffix");

                var bestWorkSuitability = StripEnumPrefix(
                    reader.String("EPalWorkSuitability::None", "BestWorkSuitability"));

                var overrideNameId = reader.String(
                    "None",
                    "OverrideNameTextId",
                    "OverrideNameTextID");

                var localizationId =
                    !overrideNameId.Equals(
                        "None",
                        StringComparison.OrdinalIgnoreCase)
                        ? overrideNameId
                        : $"PAL_NAME_{id}";

                static string? ResolveName(
                    Dictionary<string, string> names,
                    string localizationId,
                    string id,
                    string tribe)
                {
                    string[] candidates =
                    [
                        localizationId,
                        localizationId.Replace(
                            "PAL_NAME_",
                            "",
                            StringComparison.OrdinalIgnoreCase),
                        $"PAL_NAME_{id}",
                        id,
                        $"PAL_NAME_{tribe}",
                        tribe
                    ];

                    foreach (var candidate in candidates)
                    {
                        if (names.TryGetValue(candidate, out var value) &&
                            !string.IsNullOrWhiteSpace(value))
                            return value;
                    }

                    return null;
                }

                var workSuitability =
                    new Dictionary<string, int>(
                        StringComparer.OrdinalIgnoreCase);

                foreach (var key in new[]
                {
                    "EmitFlame",
                    "Watering",
                    "Seeding",
                    "GenerateElectricity",
                    "Handcraft",
                    "Collection",
                    "Deforest",
                    "Mining",
                    "OilExtraction",
                    "ProductMedicine",
                    "Cool",
                    "Transport",
                    "MonsterFarm"
                })
                {
                    var value = reader.Int(
                        0,
                        $"WorkSuitability_{key}");

                    if (value > 0)
                        workSuitability[key] = value;
                }

                var naturalPassives = new[]
                {
                    reader.String("None", "PassiveSkill1"),
                    reader.String("None", "PassiveSkill2"),
                    reader.String("None", "PassiveSkill3"),
                    reader.String("None", "PassiveSkill4")
                }
                .Select(CleanPassive)
                .Where(value => value is not null)
                .Cast<string>()
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();

                return new
                {
                    id,

                    tribe,

                    localization = new
                    {
                        nameId = localizationId
                    },

                    name = new
                    {
                        ptBR = ResolveName(
                            ptNames,
                            localizationId,
                            id,
                            tribe),

                        enUS = ResolveName(
                            enNames,
                            localizationId,
                            id,
                            tribe)
                    },

                    paldexNumber = zukanIndex,

                    zukanIndexSuffix = string.IsNullOrWhiteSpace(zukanIndexSuffix)
                        ? null
                        : zukanIndexSuffix,

                    rarity = reader.Int(0, "Rarity"),

                    isPal,

                    bpClass,

                    variant = new
                    {
                        kind = variantKind,
                        isBoss,
                        isTowerBoss,
                        isRaidBoss,
                        isTechnicalVariant,
                        recommendationEligible = variantKind == "normal"
                    },

                    behavior = new
                    {
                        nocturnal = reader.Bool(
                            false,
                            "Nocturnal")
                    },

                    movement = new
                    {
                        slowWalkSpeed = reader.Number(
                            0,
                            "SlowWalkSpeed"),

                        walkSpeed = reader.Number(
                            0,
                            "WalkSpeed"),

                        runSpeed = reader.Number(
                            0,
                            "RunSpeed"),

                        transportSpeed = reader.Number(
                            0,
                            "TransportSpeed"),

                        rideSprintSpeed = reader.Number(
                            0,
                            "RideSprintSpeed"),

                        swimSpeed = reader.Number(
                            0,
                            "SwimSpeed"),

                        swimDashSpeed = reader.Number(
                            0,
                            "SwimDashSpeed")
                    },

                    bestWorkSuitability = bestWorkSuitability.Equals(
                        "None",
                        StringComparison.OrdinalIgnoreCase)
                        ? null
                        : bestWorkSuitability,

                    workSuitability,

                    naturalPassives,

                    partnerSkillId = partnerSkillIds.Contains(id) ? id : null,

                    source = new
                    {
                        table = "DT_PalMonsterParameter",
                        row = id
                    }
                };
            })
            .ToArray();

        Directory.CreateDirectory(
            Path.GetDirectoryName(
                Path.GetFullPath(output))!);

        File.WriteAllText(
            output,
            JsonSerializer.Serialize(
                normalized,
                JsonOptions));

        Console.WriteLine(
            $"Normalized {normalized.Length} Pal rows.");

        Console.WriteLine(
            $"PT-BR names: {normalized.Count(x => x.name.ptBR is not null)}");

        Console.WriteLine(
            $"EN names:    {normalized.Count(x => x.name.enUS is not null)}");

        Console.WriteLine("Variant kinds:");

        foreach (var kind in new[] { "normal", "boss", "towerBoss", "raidBoss", "technical", "unknown" })
            Console.WriteLine(
                $"  {kind}: {normalized.Count(x => x.variant.kind == kind)}");

        Console.WriteLine(
            $"Recommendation eligible: {normalized.Count(x => x.variant.recommendationEligible)}");

        Console.WriteLine(
            $"With partner skill: {normalized.Count(x => x.partnerSkillId is not null)}");

        Console.WriteLine(
            $"Output:      {Path.GetFullPath(output)}");

        return 0;
    }
    private static int NormalizePartnerSkills(
        string pakDirectory,
        string? mappings,
        string output)
    {
        using var workspace = new PakWorkspace(pakDirectory, mappings);

        const string partnerSkillTablePath =
            "Pal/Content/Pal/DataTable/PassiveSkill/DT_PartnerSkillParameter";

        const string passiveMainTablePath =
            "Pal/Content/Pal/DataTable/PassiveSkill/DT_PassiveSkill_Main";

        var partnerSkillTable = workspace.LoadArbitraryTable(partnerSkillTablePath)
            ?? throw new InvalidOperationException(
                $"Could not load '{partnerSkillTablePath}'.");

        var passiveMainTable = workspace.LoadArbitraryTable(passiveMainTablePath)
            ?? throw new InvalidOperationException(
                $"Could not load '{passiveMainTablePath}'.");

        var passiveMainRows = passiveMainTable.RowMap.ToDictionary(
            entry => entry.Key.Text,
            entry => new RowReader(entry.Value),
            StringComparer.OrdinalIgnoreCase);

        static string StripEnumPrefix(string value)
        {
            var separator = value.LastIndexOf("::", StringComparison.Ordinal);
            return separator >= 0 ? value[(separator + 2)..] : value;
        }

        static bool IsNone(string value) =>
            string.IsNullOrWhiteSpace(value) ||
            value.Equals("None", StringComparison.OrdinalIgnoreCase);

        static string ReadKey(FStructFallback? wrapper) =>
            wrapper is null ? "" : new RowReader(wrapper).String("", "Key");

        static List<object> ExtractEffects(RowReader passiveReader)
        {
            var effects = new List<object>();

            for (var index = 1; index <= 4; index++)
            {
                var typeRaw = passiveReader.String(
                    "EPalPassiveSkillEffectType::no",
                    $"EffectType{index}");

                var type = StripEnumPrefix(typeRaw);

                if (string.Equals(type, "no", StringComparison.OrdinalIgnoreCase))
                    continue;

                var targetRaw = passiveReader.String(
                    "EPalPassiveSkillEffectTargetType::None",
                    $"TargetType{index}");

                effects.Add(new
                {
                    type,
                    value = passiveReader.Number(0, $"EffectValue{index}"),
                    target = StripEnumPrefix(targetRaw)
                });
            }

            return effects;
        }

        object? ExtractSkillParameters(FStructFallback? parametersStruct)
        {
            if (parametersStruct is null)
                return null;

            var parameters = new RowReader(parametersStruct);

            var workType = StripEnumPrefix(
                parameters.String("EPalWorkType::None", "WorkType"));

            var targetElementType = StripEnumPrefix(
                parameters.String("EPalElementType::None", "TargetElementType"));

            var mapObjectIds = parameters.NameArray("MapObjectId");
            var itemIds = parameters.NameArray("ItemId");

            var palTribeIds = parameters.NameArray("PalTribeIds")
                .Select(StripEnumPrefix)
                .ToArray();

            var triggerStruct = parameters.Struct("TriggerParam");
            var triggerTribeIds = triggerStruct is null
                ? []
                : new RowReader(triggerStruct)
                    .NameArray("TargetTribeIds")
                    .Select(StripEnumPrefix)
                    .ToArray();

            object? trigger = triggerTribeIds.Length == 0
                ? null
                : new { targetTribeIds = triggerTribeIds };

            var itemParamStruct = parameters.Struct("ItemParam");
            object? item = null;

            if (itemParamStruct is not null)
            {
                var itemReader = new RowReader(itemParamStruct);

                var itemTypeA = StripEnumPrefix(
                    itemReader.String("EPalItemTypeA::None", "ItemTypeA"));

                var itemTypeB = StripEnumPrefix(
                    itemReader.String("EPalItemTypeB::None", "ItemTypeB"));

                var itemIdsList = itemReader.NameArray("ItemIds");
                var excludedItemIds = itemReader.NameArray("ExcludedItemIds");
                var containNoItemId = itemReader.Bool(false, "bIsContainNoItemId");

                var weaponType = StripEnumPrefix(
                    itemReader.String("EPalWeaponType::None", "WeaponType"));

                var weaponTypes = itemReader.NameArray("WeaponTypes")
                    .Select(StripEnumPrefix)
                    .ToArray();

                var meleeOnly = itemReader.Bool(false, "bMeleeOnly");

                var hasContent =
                    !IsNone(itemTypeA) ||
                    !IsNone(itemTypeB) ||
                    itemIdsList.Count > 0 ||
                    excludedItemIds.Count > 0 ||
                    containNoItemId ||
                    !IsNone(weaponType) ||
                    weaponTypes.Length > 0 ||
                    meleeOnly;

                if (hasContent)
                    item = new
                    {
                        itemTypeA = IsNone(itemTypeA) ? null : itemTypeA,
                        itemTypeB = IsNone(itemTypeB) ? null : itemTypeB,
                        itemIds = itemIdsList.Count > 0 ? itemIdsList : null,
                        excludedItemIds = excludedItemIds.Count > 0 ? excludedItemIds : null,
                        containNoItemId,
                        weaponType = IsNone(weaponType) ? null : weaponType,
                        weaponTypes = weaponTypes.Length > 0 ? weaponTypes : null,
                        meleeOnly
                    };
            }

            var regeneStruct = parameters.Struct("RegeneParam");
            int? regeneInterval = null;

            if (regeneStruct is not null)
            {
                var value = new RowReader(regeneStruct).Int(0, "Interval");
                if (value != 0) regeneInterval = value;
            }

            var otherOtomoStruct = parameters.Struct("OtherOtomoConditionParam");
            object? otherOtomoCondition = null;

            if (otherOtomoStruct is not null)
            {
                var otherOtomoReader = new RowReader(otherOtomoStruct);

                var otherOtomoElementType = StripEnumPrefix(
                    otherOtomoReader.String("EPalElementType::None", "TargetElementType"));

                var otherOtomoElementTypes = otherOtomoReader
                    .NameArray("TargetElementTypes")
                    .Select(StripEnumPrefix)
                    .ToArray();

                var otherOtomoPalTribeIds = otherOtomoReader
                    .NameArray("PalTribeIds")
                    .Select(StripEnumPrefix)
                    .ToArray();

                var hasContent =
                    !IsNone(otherOtomoElementType) ||
                    otherOtomoElementTypes.Length > 0 ||
                    otherOtomoPalTribeIds.Length > 0;

                if (hasContent)
                    otherOtomoCondition = new
                    {
                        targetElementType = IsNone(otherOtomoElementType) ? null : otherOtomoElementType,
                        targetElementTypes = otherOtomoElementTypes.Length > 0 ? otherOtomoElementTypes : null,
                        palTribeIds = otherOtomoPalTribeIds.Length > 0 ? otherOtomoPalTribeIds : null
                    };
            }

            return new
            {
                delayTime = parameters.Number(0, "DelayTime"),
                workType = IsNone(workType) ? null : workType,
                assignIgnoreCurrentWorkType =
                    parameters.Bool(false, "bAssignPassiveIgnoreCurrentWorkType"),
                mapObjectIds = mapObjectIds.Count > 0 ? mapObjectIds : null,
                itemIds = itemIds.Count > 0 ? itemIds : null,
                assignOthers = parameters.Bool(false, "AssignOthers"),
                targetElementType = IsNone(targetElementType) ? null : targetElementType,
                palTribeIds = palTribeIds.Length > 0 ? palTribeIds : null,
                notAssignSelf = parameters.Bool(false, "bNotAssignSelf"),
                floatValue1 = parameters.Number(0, "FloatValue1"),
                trigger,
                item,
                regeneInterval,
                otherOtomoCondition
            };
        }

        var unresolvedCount = 0;

        var normalized = partnerSkillTable.RowMap
            .Select(entry =>
            {
                var id = entry.Key.Text;
                var reader = new RowReader(entry.Value);

                var activeSkillStruct = reader.Struct("ActiveSkill");
                object? activeSkill = null;

                if (activeSkillStruct is not null)
                {
                    var activeReader = new RowReader(activeSkillStruct);

                    var skillName = activeReader.String("Unknown", "SkillName");
                    var wazaId = StripEnumPrefix(
                        activeReader.String("EPalWazaID::None", "WazaID"));

                    var mainValueByRank = activeReader.NumberArray(
                        "ActiveSkill_MainValueByRank");

                    var overwriteCoolTimeByRank = activeReader.NumberArray(
                        "ActiveSkill_OverWriteCoolTimeByRank");

                    var overwriteEffectTimeByRank = activeReader.NumberArray(
                        "ActiveSkill_OverWriteEffectTimeByRank");

                    var isEmpty =
                        skillName.Equals("Unknown", StringComparison.OrdinalIgnoreCase) &&
                        wazaId.Equals("None", StringComparison.OrdinalIgnoreCase) &&
                        mainValueByRank.Count == 0 &&
                        overwriteCoolTimeByRank.Count == 0 &&
                        overwriteEffectTimeByRank.Count == 0;

                    if (!isEmpty)
                        activeSkill = new
                        {
                            skillName,
                            wazaId,
                            flags = new
                            {
                                idleCostDecreaseEveryFrame = activeReader.Bool(
                                    false, "bIdlelCostDecreaseEveryFrame"),

                                isExecSkillContinuation = activeReader.Bool(
                                    false, "bIsExecSkillContinuation"),

                                isOneShotRideAction = activeReader.Bool(
                                    false, "bIsOneShotRideAction"),

                                isRidingActiveSkillNotWeapon = activeReader.Bool(
                                    false, "IsRidingActiveSkillNotWeapon"),

                                ridingActiveSkillNotWeaponCondition = StripEnumPrefix(
                                    activeReader.String(
                                        "EPalRidingActiveSkillNotWeaponCondition::None",
                                        "RidingActiveSkillNotWeaponCondition")),

                                isToggleRidingActiveSkillNotWeapon = activeReader.Bool(
                                    false, "bIsToggleRidingActiveSkillNotWeapon")
                            },
                            mainValueByRank,
                            overwriteCoolTimeByRank,
                            overwriteEffectTimeByRank
                        };
                }

                var restrictionItems = reader.StructArray("RestrictionItems")
                    .Select(ReadKey)
                    .Where(value => !string.IsNullOrWhiteSpace(value))
                    .ToArray();

                var textReferenceRaw = reader.StructArray("TextReferencePassiveSkills");

                string[][]? textReferencePassiveSkills = textReferenceRaw.Count == 0
                    ? null
                    : textReferenceRaw
                        .Select(entryStruct => new RowReader(entryStruct)
                            .StructArray("PassiveSkillIds")
                            .Select(ReadKey)
                            .Where(value => !string.IsNullOrWhiteSpace(value))
                            .ToArray())
                        .ToArray();

                var rankStructs = reader.StructArray("PassiveSkills");
                var ranks = new List<object>();
                var baseCampRelevant = false;

                for (var rankIndex = 0; rankIndex < rankStructs.Count; rankIndex++)
                {
                    var rankReader = new RowReader(rankStructs[rankIndex]);
                    var skillEntries = rankReader.StructArray("SkillAndParametersArray");

                    var passiveSkillsForRank = new List<object>();

                    foreach (var skillEntryStruct in skillEntries)
                    {
                        var skillEntryReader = new RowReader(skillEntryStruct);

                        var skillId = ReadKey(
                            skillEntryReader.Struct("SkillName"));

                        var parameters = ExtractSkillParameters(
                            skillEntryReader.Struct("Parameters"));

                        List<object> effects = [];
                        object? activation = null;
                        var isUnresolved = string.IsNullOrWhiteSpace(skillId);

                        if (!isUnresolved)
                        {
                            if (passiveMainRows.TryGetValue(skillId, out var mainReader))
                            {
                                effects = ExtractEffects(mainReader);

                                var worker = mainReader.Bool(false, "InvokeWorker");
                                var baseCamp = mainReader.Bool(false, "InvokeInBaseCamp");

                                if (worker || baseCamp)
                                    baseCampRelevant = true;

                                activation = new
                                {
                                    always = mainReader.Bool(false, "InvokeAlways"),
                                    worker,
                                    baseCamp,
                                    otomo = mainReader.Bool(false, "InvokeInOtomo"),
                                    activeOtomo = mainReader.Bool(false, "InvokeActiveOtomo"),
                                    reserve = mainReader.Bool(false, "InvokeReserve"),
                                    riding = mainReader.Bool(false, "InvokeRiding")
                                };
                            }
                            else
                            {
                                isUnresolved = true;
                            }
                        }

                        if (isUnresolved)
                            unresolvedCount++;

                        passiveSkillsForRank.Add(new
                        {
                            id = skillId,
                            effects,
                            activation,
                            parameters,
                            unresolved = isUnresolved ? true : (bool?)null
                        });
                    }

                    ranks.Add(new
                    {
                        rank = rankIndex + 1,
                        passiveSkills = passiveSkillsForRank
                    });
                }

                return new
                {
                    palId = id,
                    restrictionItems,
                    activeSkill,
                    ranks,
                    baseCampRelevant,
                    textReferencePassiveSkills,

                    source = new
                    {
                        table = "DT_PartnerSkillParameter",
                        row = id
                    }
                };
            })
            .ToArray();

        Directory.CreateDirectory(
            Path.GetDirectoryName(Path.GetFullPath(output))!);

        File.WriteAllText(
            output,
            JsonSerializer.Serialize(normalized, JsonOptions));

        var rowsWithRanks = normalized.Count(row => row.ranks.Count > 0);
        var rowsWithActiveSkills = normalized.Count(row => row.activeSkill is not null);
        var baseCampRelevantCount = normalized.Count(row => row.baseCampRelevant);

        Console.WriteLine(
            $"Normalized {normalized.Length} partner-skill rows.");

        Console.WriteLine(
            $"Rows with ranks: {rowsWithRanks}");

        Console.WriteLine(
            $"Rows with active skills: {rowsWithActiveSkills}");

        Console.WriteLine(
            $"Base-camp relevant: {baseCampRelevantCount}");

        Console.WriteLine(
            $"Unresolved passive refs: {unresolvedCount}");

        Console.WriteLine(
            $"Output: {Path.GetFullPath(output)}");

        return 0;
    }
    private static int NormalizeGameSettings(
        string pakDirectory,
        string? mappings,
        string output,
        string? runtimeEvidencePath)
    {
        using var workspace = new PakWorkspace(pakDirectory, mappings);

        const string gameSettingAssetPath =
            "Pal/Content/Pal/Blueprint/System/BP_PalGameSetting";

        const string gameSettingCdoExportName =
            "Default__BP_PalGameSetting_C";

        var package = workspace.LoadPackage(gameSettingAssetPath);

        var cdoExport = package.GetExports()
            .FirstOrDefault(export => string.Equals(
                export.Name,
                gameSettingCdoExportName,
                StringComparison.OrdinalIgnoreCase))
            ?? throw new InvalidOperationException(
                $"Could not find export '{gameSettingCdoExportName}' in '{gameSettingAssetPath}'.");

        var reader = new RowReader(cdoExport.Properties);

        static string StripEnumPrefix(string value)
        {
            var separator = value.LastIndexOf("::", StringComparison.Ordinal);
            return separator >= 0 ? value[(separator + 2)..] : value;
        }

        static string LowerCamel(string value) =>
            value.Length == 0
                ? value
                : char.ToLowerInvariant(value[0]) + value[1..];

        // WorkSuitability -----------------------------------------------

        var maxRank = reader.Int(0, "WorkSuitabilityMaxRank");

        var transportAbsorbRange = reader.NumberArray(
            "TransportItemAbsorbRangeByWorkSuitabilityRank");

        var craftSpeedByType = new Dictionary<string, IReadOnlyList<double>>(
            StringComparer.OrdinalIgnoreCase);

        foreach (var (key, value) in reader.StructMap("WorkSuitabilityDefineDataMap"))
        {
            var type = StripEnumPrefix(key);
            var defineReader = new RowReader(value);
            craftSpeedByType[type] = defineReader.NumberArray("CraftSpeeds");
        }

        // Collection/Deforest/Mining carry their own top-level struct
        // (with extra per-rank gameplay data we intentionally skip) instead
        // of an entry in WorkSuitabilityDefineDataMap.
        foreach (var (type, propertyName) in new[]
        {
            ("Collection", "WorkSuitabilityDefineData_Collection"),
            ("Deforest", "WorkSuitabilityDefineData_Deforest"),
            ("Mining", "WorkSuitabilityDefineData_Mining")
        })
        {
            var defineStruct = reader.Struct(propertyName);
            var commonStruct = defineStruct is null
                ? null
                : new RowReader(defineStruct).Struct("CommonDefineData");

            if (commonStruct is null)
            {
                Console.Error.WriteLine(
                    $"Warning: could not read CraftSpeeds for '{type}' from '{propertyName}.CommonDefineData'.");
                continue;
            }

            craftSpeedByType[type] = new RowReader(commonStruct).NumberArray("CraftSpeeds");
        }

        var workSuitability = new
        {
            sourceKind = "pak-blueprint",
            source = gameSettingAssetPath,
            maxRank,
            craftSpeedByType,
            transportItemAbsorbRangeByRank = transportAbsorbRange
        };

        // WorkHard --------------------------------------------------------

        var workHardModes = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);

        foreach (var (key, value) in reader.StructMap("BaseCampPassiveEffectWorkHardInfoMap"))
        {
            var mode = LowerCamel(StripEnumPrefix(key));
            var modeReader = new RowReader(value);

            workHardModes[mode] = new
            {
                workSpeedRate = modeReader.Number(0, "WorkSpeedRate"),
                moveSpeedRate = modeReader.Number(0, "MoveSpeedRate"),
                affectSanityRate = modeReader.Number(0, "AffectSanityRate"),
                decreaseFullStomachRate = modeReader.Number(0, "DecreaseFullStomachRate")
            };
        }

        var workHard = new
        {
            sourceKind = "pak-blueprint",
            source = gameSettingAssetPath,
            sourceProperty = "BaseCampPassiveEffectWorkHardInfoMap",
            modes = workHardModes
        };

        // Transport (runtime evidence only; nothing here comes from the PAK) --

        object? transport = null;

        if (!string.IsNullOrWhiteSpace(runtimeEvidencePath))
        {
            if (!File.Exists(runtimeEvidencePath))
                throw new InvalidOperationException(
                    $"--runtime-evidence file not found: '{runtimeEvidencePath}'.");

            using var evidenceDoc = JsonDocument.Parse(
                File.ReadAllText(runtimeEvidencePath));

            var root = evidenceDoc.RootElement;

            static JsonElement? TryGet(JsonElement parent, string property) =>
                parent.ValueKind == JsonValueKind.Object &&
                parent.TryGetProperty(property, out var value)
                    ? value
                    : null;

            static string? GetString(JsonElement? element, string property) =>
                element is { } value &&
                value.ValueKind == JsonValueKind.Object &&
                value.TryGetProperty(property, out var result) &&
                result.ValueKind == JsonValueKind.String
                    ? result.GetString()
                    : null;

            static double? GetNumber(JsonElement? element, string property) =>
                element is { } value &&
                value.ValueKind == JsonValueKind.Object &&
                value.TryGetProperty(property, out var result) &&
                result.ValueKind == JsonValueKind.Number
                    ? result.GetDouble()
                    : null;

            static int? GetInt(JsonElement? element, string property) =>
                element is { } value &&
                value.ValueKind == JsonValueKind.Object &&
                value.TryGetProperty(property, out var result) &&
                result.ValueKind == JsonValueKind.Number
                    ? result.GetInt32()
                    : null;

            var palGameSetting = TryGet(root, "palGameSetting");
            var workTransportingSpeedRate = palGameSetting is { } pgs
                ? TryGet(pgs, "workTransportingSpeedRate")
                : null;

            var palDebugSetting = TryGet(root, "palDebugSetting");

            var baseSpeedType = palDebugSetting is { } pds
                ? TryGet(pds, "confirmTransportItemBaseSpeedType")
                : null;

            var speedMultiple = palDebugSetting is { } pds2
                ? TryGet(pds2, "confirmTransportItemSpeedMultipleRate")
                : null;

            if (workTransportingSpeedRate is null)
                Console.Error.WriteLine(
                    "Warning: runtime evidence is missing 'palGameSetting.workTransportingSpeedRate'; transport.globalSpeedRate omitted.");

            if (baseSpeedType is null)
                Console.Error.WriteLine(
                    "Warning: runtime evidence is missing 'palDebugSetting.confirmTransportItemBaseSpeedType'; transport.baseSpeedTypeEvidence omitted.");

            transport = new
            {
                globalSpeedRate = GetNumber(workTransportingSpeedRate, "value"),
                globalSpeedRateSourceKind = GetString(workTransportingSpeedRate, "sourceKind"),
                globalSpeedRateSource = GetString(workTransportingSpeedRate, "source"),

                baseSpeedTypeEvidence = baseSpeedType is null
                    ? null
                    : new
                    {
                        value = GetString(baseSpeedType, "value"),
                        enumValue = GetInt(baseSpeedType, "enumValue"),
                        sourceKind = GetString(baseSpeedType, "sourceKind"),
                        source = GetString(baseSpeedType, "source"),
                        confidence = GetString(baseSpeedType, "confidence"),
                        note = GetString(baseSpeedType, "note")
                    },

                debugConfirmationSpeedMultipleRate = GetNumber(speedMultiple, "value"),
                debugConfirmationSpeedMultipleRateSourceKind = GetString(speedMultiple, "sourceKind")
            };
        }
        else
        {
            Console.Error.WriteLine(
                "Warning: no --runtime-evidence file supplied; 'transport' will be omitted (WorkTransportingSpeedRate is not serialized in the PAK/Blueprint).");
        }

        var normalized = new
        {
            workSuitability,
            workHard,
            transport,
            runtimeEvidence = new
            {
                applied = transport is not null,
                file = runtimeEvidencePath
            }
        };

        Directory.CreateDirectory(
            Path.GetDirectoryName(Path.GetFullPath(output))!);

        File.WriteAllText(
            output,
            JsonSerializer.Serialize(normalized, JsonOptions));

        Console.WriteLine(
            $"Normalized game settings from '{gameSettingAssetPath}'.");

        Console.WriteLine(
            $"WorkSuitabilityMaxRank: {maxRank}");

        Console.WriteLine(
            $"CraftSpeed types resolved: {craftSpeedByType.Count}");

        Console.WriteLine(
            $"WorkHard modes resolved: {workHardModes.Count}");

        Console.WriteLine(
            $"Transport section: {(transport is not null ? "included (runtime evidence)" : "omitted (no --runtime-evidence)")}");

        Console.WriteLine(
            $"Output: {Path.GetFullPath(output)}");

        return 0;
    }

    private static int InspectAsset(
        string pakDirectory,
        string? mappings,
        string assetPath,
        string? contains)
    {
        using var workspace = new PakWorkspace(pakDirectory, mappings);

        var package = workspace.LoadPackage(assetPath);

        bool Matches(string? value)
        {
            if (string.IsNullOrWhiteSpace(contains))
                return true;

            return value?.Contains(
                contains,
                StringComparison.OrdinalIgnoreCase) == true;
        }

        Console.WriteLine($"Package: {package.Name}");
        Console.WriteLine($"Exports: {package.ExportMapLength}");
        Console.WriteLine($"Names:   {package.NameMap.Length}");
        Console.WriteLine();

        Console.WriteLine("=== NAMEMAP ===");

        foreach (var entry in package.NameMap)
        {
            var name = entry.Name;

            if (Matches(name))
                Console.WriteLine(name);
        }

        Console.WriteLine();
        Console.WriteLine("=== EXPORTS / PROPERTIES ===");

        foreach (var export in package.GetExports())
        {
            var exportMatches =
                Matches(export.Name) ||
                Matches(export.ExportType);

            var matchingProperties = export.Properties
                .Where(property =>
                {
                    if (string.IsNullOrWhiteSpace(contains))
                        return true;

                    var propertyName = property.Name.Text;

                    string? value = null;

                    try
                    {
                        value = DescribeValue(
                            property.Tag?.GetValue<object>());
                    }
                    catch
                    {
                        // só inspeção
                    }

                    return Matches(propertyName) ||
                        Matches(property.TagData.Type) ||
                        Matches(value);
                })
                .ToArray();

            if (!exportMatches &&
                matchingProperties.Length == 0)
                continue;

            Console.WriteLine(
                $"{export.Name} [{export.ExportType}]");

            Console.WriteLine(
                $"  Class: {export.Class?.GetPathName() ?? "None"}");

            Console.WriteLine(
                $"  Super: {export.Super?.GetPathName() ?? "None"}");

            Console.WriteLine(
                $"  Outer: {export.Outer?.GetPathName() ?? "None"}");

            foreach (var property in matchingProperties)
            {
                var type =
                    property.TagData.Type ?? "unknown";

                string value;

                try
                {
                    value = DescribeValue(
                        property.Tag?.GetValue<object>());
                }
                catch
                {
                    value = "<unreadable>";
                }

                Console.WriteLine(
                    $"  {property.Name.Text} [{type}] = {value}");
            }
        }

        return 0;
    }
    private static int InspectMapping(
        string pakDirectory,
        string? mappings,
        string contains)
    {
        using var workspace = new PakWorkspace(
            pakDirectory,
            mappings);

        var typeMappings = workspace.Mappings
            ?? throw new InvalidOperationException(
                "Mappings are not available.");

        Console.WriteLine("=== TYPES ===");

        foreach (var pair in typeMappings.Types
            .Where(pair =>
                pair.Key.Contains(
                    contains,
                    StringComparison.OrdinalIgnoreCase) ||
                pair.Value.Properties.Values.Any(property =>
                    property.Name.Contains(
                        contains,
                        StringComparison.OrdinalIgnoreCase)))
            .OrderBy(pair => pair.Key))
        {
            var type = pair.Value;

            Console.WriteLine();
            Console.WriteLine($"Type:  {type.Name}");
            Console.WriteLine($"Super: {type.SuperType ?? "None"}");
            Console.WriteLine(
                $"Properties: {type.PropertyCount}");

            foreach (var property in type.Properties
                .OrderBy(pair => pair.Key))
            {
                var info = property.Value;
                var mappingType = info.MappingType;

                Console.WriteLine(
                    $"  [{property.Key}] {info.Name} " +
                    $"type={mappingType.Type} " +
                    $"struct={mappingType.StructType ?? "-"} " +
                    $"enum={mappingType.EnumName ?? "-"}");
            }
        }

        Console.WriteLine();
        Console.WriteLine("=== ENUMS ===");

        foreach (var pair in typeMappings.Enums
            .Where(pair =>
                pair.Key.Contains(
                    contains,
                    StringComparison.OrdinalIgnoreCase))
            .OrderBy(pair => pair.Key))
        {
            Console.WriteLine();
            Console.WriteLine($"Enum: {pair.Key}");

            foreach (var value in pair.Value.OrderBy(x => x.Key))
                Console.WriteLine(
                    $"  {value.Key} = {value.Value}");
        }

        return 0;
    }
    private static string DescribeValue(object? value, int depth = 0)
    {
        if (value is null)
            return "null";

        if (depth >= 5)
            return value.ToString() ?? value.GetType().Name;
        if (depth >= 8)
            return value.ToString() ?? value.GetType().Name;
        if (depth >= 10)
            return value.ToString() ?? value.GetType().Name;
            
        if (value is CUE4Parse.UE4.Assets.Objects.FScriptStruct scriptStruct)
        {
            return DescribeValue(
                scriptStruct.StructType,
                depth + 1);
        }

        if (value is CUE4Parse.UE4.Assets.Objects.FStructFallback structValue)
        {
            var parts = structValue.Properties.Select(property =>
            {
                object? nested = null;

                if (string.Equals(
                        property.TagData.Type,
                        "StructProperty",
                        StringComparison.OrdinalIgnoreCase))
                {
                    try
                    {
                        nested = property.Tag?
                            .GetValue<
                                CUE4Parse.UE4.Assets.Objects.FStructFallback>();
                    }
                    catch
                    {
                        // tenta leitura genérica abaixo
                    }
                }

                if (nested is null)
                {
                    try
                    {
                        nested = property.Tag?.GetValue<object>();
                    }
                    catch
                    {
                        nested = "<unreadable>";
                    }
                }

                return
                    $"{property.Name.Text}=" +
                    $"{DescribeValue(nested, depth + 1)}";
            });

            return "{ " + string.Join(", ", parts) + " }";
        }

        if (value is CUE4Parse.UE4.Assets.Objects.UScriptArray array)
        {
            var items = array.Properties
                .Take(20)
                .Select(item =>
                {
                    object? nested = null;

                    // StructProperty dentro de arrays nem sempre é
                    // corretamente desempacotado por GetValue<object>().
                    try
                    {
                        var fallback =
                            item.GetValue<
                                CUE4Parse.UE4.Assets.Objects.FStructFallback>();

                        if (fallback is not null)
                            nested = fallback;
                    }
                    catch
                    {
                        // não é FStructFallback; tenta genericamente abaixo
                    }

                    if (nested is null)
                    {
                        try
                        {
                            nested = item.GetValue<object>();
                        }
                        catch
                        {
                            nested = "<unreadable>";
                        }
                    }

                    return DescribeValue(
                        nested,
                        depth + 1);
                });

            return "[ " + string.Join(", ", items) + " ]";
        }
        if (value is CUE4Parse.UE4.Assets.Objects.UScriptMap map)
        {
            var items = map.Properties
                .Take(50)
                .Select(pair =>
                {
                    object? keyValue;
                    object? mappedValue;

                    try
                    {
                        keyValue = pair.Key.GetValue<object>();
                    }
                    catch
                    {
                        keyValue = "<unreadable-key>";
                    }

                    try
                    {
                        mappedValue = pair.Value?.GetValue<object>();
                    }
                    catch
                    {
                        mappedValue = "<unreadable-value>";
                    }

                    return
                        $"{DescribeValue(keyValue, depth + 1)} => " +
                        $"{DescribeValue(mappedValue, depth + 1)}";
                });

            return "{ " + string.Join(", ", items) + " }";
        }
        return value.ToString() ?? value.GetType().Name;
    }
    private static Dictionary<string, string> ParseOptions(string[] args)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        for (var index = 0; index < args.Length; index++)
        {
            if (!args[index].StartsWith("--", StringComparison.Ordinal))
                throw new ArgumentException($"Unexpected argument '{args[index]}'");
            var key = args[index][2..];
            if (index + 1 >= args.Length || args[index + 1].StartsWith("--", StringComparison.Ordinal))
                throw new ArgumentException($"Missing value for --{key}");
            result[key] = args[++index];
        }
        return result;
    }

    private static string Required(IReadOnlyDictionary<string, string> options, string key) =>
        options.TryGetValue(key, out var value) && !string.IsNullOrWhiteSpace(value)
            ? value
            : throw new ArgumentException($"--{key} is required");

    private static void PrintUsage() => Console.WriteLine("""
        Palworld Atlas data extractor

        probe     --pak-dir PATH --output FILE [--build-id ID] [--mappings FILE]

        publish   --pak-dir PATH --output DIR --build-id ID [--mappings FILE]
                [--game-version VERSION] [--previous-manifest FILE]

        inventory --pak-dir PATH --contains TEXT [--mappings FILE]

        dump-table --pak-dir PATH --table PACKAGE_PATH --output FILE [--mappings FILE]

        probe-table --pak-dir PATH --table PACKAGE_PATH [--mappings FILE]

        inspect-row --pak-dir PATH --table PACKAGE_PATH --row ROW_ID [--mappings FILE]

        inspect-row-deep --pak-dir PATH --table PACKAGE_PATH --row ROW_ID [--mappings FILE]

        normalize-passives --pak-dir PATH --output FILE [--mappings FILE]

        normalize-pals --pak-dir PATH --output FILE [--mappings FILE]

        normalize-partner-skills --pak-dir PATH --output FILE [--mappings FILE]

        normalize-game-settings --pak-dir PATH --output FILE [--mappings FILE]
                [--runtime-evidence FILE]

        inspect-asset --pak-dir PATH --asset PACKAGE_PATH [--contains TEXT] [--mappings FILE]

        inspect-mapping --pak-dir PATH --contains TEXT [--mappings FILE]

        validate-data --data-dir PATH --schema-dir PATH

        build-metadata --data-dir PATH --schema-dir PATH --build-id ID --output FILE
                [--game-version VERSION] [--previous-build FILE]

        """);
}
