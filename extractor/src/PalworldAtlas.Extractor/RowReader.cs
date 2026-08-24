using CUE4Parse.UE4.Assets.Objects;
using CUE4Parse.UE4.Objects.Core.Math;
using CUE4Parse.UE4.Objects.Core.i18N;
using CUE4Parse.UE4.Objects.UObject;

namespace PalworldAtlas.Extractor;

internal sealed class RowReader
{
    private readonly Dictionary<string, FPropertyTag> _properties;

    public RowReader(FStructFallback row) : this(row.Properties)
    {
    }

    public RowReader(IEnumerable<FPropertyTag> properties)
    {
        _properties = properties
            .GroupBy(property => property.Name.Text, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.First(), StringComparer.OrdinalIgnoreCase);
    }

    public IReadOnlyList<string> FieldNames => _properties.Keys.Order(StringComparer.OrdinalIgnoreCase).ToArray();

    private FPropertyTag? Find(params string[] names)
    {
        foreach (var name in names)
            if (_properties.TryGetValue(name, out var property)) return property;
        return null;
    }

    public string String(string fallback, params string[] names)
    {
        var property = Find(names);
        if (property is null) return fallback;
        try
        {
            return property.TagData.Type switch
            {
                "NameProperty" or "EnumProperty" => property.Tag.GetValue<FName>().Text,
                "TextProperty" => property.Tag.GetValue<FText>().Text,
                _ => property.Tag.GetValue<string>() ?? fallback,
            };
        }
        catch
        {
            try { return Convert.ToString(property.Tag.GetValue<object>()) ?? fallback; }
            catch { return fallback; }
        }
    }

    public int Int(int fallback, params string[] names)
    {
        var property = Find(names);
        if (property is null) return fallback;
        foreach (var type in new[] { typeof(int), typeof(short), typeof(byte), typeof(long), typeof(float), typeof(double) })
        {
            try { return Convert.ToInt32(property.Tag.GetValue(type)); }
            catch { }
        }
        return fallback;
    }

    public double Number(double fallback, params string[] names)
    {
        foreach (var name in names)
        {
            var value = Value(name);

            if (value is null)
                continue;

            try
            {
                return Convert.ToDouble(
                    value,
                    System.Globalization.CultureInfo.InvariantCulture);
            }
            catch
            {
                // tenta o próximo alias
            }
        }

        return fallback;
    }

    public double NumberAt(int index, double fallback, params string[] names)
    {
        var property = Find(names);
        if (property is null || index < 0) return fallback;
        try
        {
            var array = property.Tag.GetValue<UScriptArray>();
            if (index >= array.Properties.Count) return fallback;
            var item = array.Properties[index];

            // item.GetValue(Type) (non-generic) silently returns null when the
            // requested Type doesn't match the element's own declared type
            // (e.g. asking a FloatProperty element for typeof(double)), and
            // Convert.ToDouble(null) returns 0 instead of throwing - so a
            // type-guessing loop over GetValue(Type) would silently zero out
            // the element. GetValue<object>() returns the element's actual
            // boxed value regardless of its declared type, so convert that.
            try
            {
                return Convert.ToDouble(
                    item.GetValue<object>(),
                    System.Globalization.CultureInfo.InvariantCulture);
            }
            catch { }

            foreach (var type in new[] { typeof(double), typeof(float), typeof(int), typeof(long), typeof(short), typeof(byte) })
            {
                try { return Convert.ToDouble(item.GetValue(type)); }
                catch { }
            }
        }
        catch { }
        return fallback;
    }

    public bool Bool(bool fallback, params string[] names)
    {
        var property = Find(names);
        if (property is null) return fallback;
        try { return property.Tag.GetValue<bool>(); }
        catch { return fallback; }
    }
    public IReadOnlyList<(string Name, string Type, string Value)> InspectFields()
    {
        var result = new List<(string Name, string Type, string Value)>();

        foreach (var pair in _properties.OrderBy(pair => pair.Key, StringComparer.OrdinalIgnoreCase))
        {
            var property = pair.Value;
            var type = property.TagData.Type ?? "unknown";
            var value = "";

            try
            {
                var raw = property.Tag.GetValue<object>();
                value = raw?.ToString() ?? "null";
            }
            catch
            {
                value = "<unreadable>";
            }

            result.Add((pair.Key, type, value));
        }

        return result;
    }
    public object? RawValue(string name)
    {
        var property = Find(name);

        if (property is null)
            return null;

        try
        {
            return property.Tag.GetValue<object>();
        }
        catch
        {
            return null;
        }
    }
    public object? Value(string name)
    {
        var property = Find(name);
        if (property is null)
            return null;

        try
        {
            return property.TagData.Type switch
            {
                "BoolProperty" => property.Tag.GetValue<bool>(),
                "IntProperty" => property.Tag.GetValue<int>(),
                "Int64Property" => property.Tag.GetValue<long>(),
                "FloatProperty" => property.Tag.GetValue<float>(),
                "DoubleProperty" => property.Tag.GetValue<double>(),
                "NameProperty" => property.Tag.GetValue<FName>().Text,
                "EnumProperty" => property.Tag.GetValue<FName>().Text,
                "TextProperty" => property.Tag.GetValue<FText>().Text,
                "StrProperty" => property.Tag.GetValue<string>(),
                _ => Convert.ToString(property.Tag.GetValue<object>())
            };
        }
        catch
        {
            return null;
        }
    }

    public FStructFallback? Struct(params string[] names)
    {
        var property = Find(names);
        if (property is null) return null;
        try { return property.Tag.GetValue<FStructFallback>(); }
        catch { return null; }
    }

    public IReadOnlyList<FStructFallback> StructArray(params string[] names)
    {
        var property = Find(names);
        if (property is null) return Array.Empty<FStructFallback>();
        try
        {
            var array = property.Tag.GetValue<UScriptArray>();
            var result = new List<FStructFallback>(array.Properties.Count);
            foreach (var item in array.Properties)
            {
                try { result.Add(item.GetValue<FStructFallback>()); }
                catch { }
            }
            return result;
        }
        catch { return Array.Empty<FStructFallback>(); }
    }

    public IReadOnlyList<(string Key, FStructFallback Value)> StructMap(params string[] names)
    {
        var property = Find(names);
        if (property is null) return Array.Empty<(string, FStructFallback)>();
        try
        {
            var map = property.Tag.GetValue<UScriptMap>();
            var result = new List<(string Key, FStructFallback Value)>(map.Properties.Count);
            foreach (var pair in map.Properties)
            {
                string key;
                try { key = pair.Key.GetValue<FName>().Text; }
                catch
                {
                    try { key = Convert.ToString(pair.Key.GetValue<object>()) ?? ""; }
                    catch { key = ""; }
                }

                if (string.IsNullOrWhiteSpace(key)) continue;

                try
                {
                    var value = pair.Value?.GetValue<FStructFallback>();
                    if (value is not null)
                        result.Add((key, value));
                }
                catch { }
            }
            return result;
        }
        catch { return Array.Empty<(string, FStructFallback)>(); }
    }

    public int ArrayLength(params string[] names)
    {
        var property = Find(names);
        if (property is null) return 0;
        try { return property.Tag.GetValue<UScriptArray>().Properties.Count; }
        catch { return 0; }
    }

    public IReadOnlyList<string> NameArray(params string[] names)
    {
        var property = Find(names);
        if (property is null) return Array.Empty<string>();
        try
        {
            var array = property.Tag.GetValue<UScriptArray>();
            var result = new List<string>(array.Properties.Count);
            foreach (var item in array.Properties)
            {
                try { result.Add(item.GetValue<FName>().Text); continue; }
                catch { }
                try { result.Add(Convert.ToString(item.GetValue<object>()) ?? ""); }
                catch { }
            }
            return result;
        }
        catch { return Array.Empty<string>(); }
    }

    public IReadOnlyList<double> NumberArray(params string[] names)
    {
        var property = Find(names);
        if (property is null) return Array.Empty<double>();
        try
        {
            var array = property.Tag.GetValue<UScriptArray>();
            var result = new List<double>(array.Properties.Count);
            foreach (var item in array.Properties)
            {
                // item.GetValue(Type) (non-generic) silently returns null when the
                // requested Type doesn't match the element's own declared type
                // (e.g. asking a FloatProperty element for typeof(double)), and
                // Convert.ToDouble(null) returns 0 instead of throwing - so a
                // type-guessing loop over GetValue(Type) would silently zero out
                // every element. GetValue<object>() returns the element's actual
                // boxed value regardless of its declared type, so convert that.
                try
                {
                    result.Add(Convert.ToDouble(
                        item.GetValue<object>(),
                        System.Globalization.CultureInfo.InvariantCulture));
                    continue;
                }
                catch { }

                foreach (var type in new[] { typeof(double), typeof(float), typeof(int), typeof(long), typeof(short), typeof(byte) })
                {
                    try { result.Add(Convert.ToDouble(item.GetValue(type))); break; }
                    catch { }
                }
            }
            return result;
        }
        catch { return Array.Empty<double>(); }
    }

    public (double X, double Y)? Coordinates(params string[] names)
    {
        foreach (var name in names)
        {
            var property = Find(name);
            if (property is null) continue;
            try
            {
                var vector = property.Tag.GetValue<FVector>();
                return (vector.X, vector.Y);
            }
            catch { }
            try
            {
                var transform = property.Tag.GetValue<FTransform>();
                return (transform.Translation.X, transform.Translation.Y);
            }
            catch { }
            try
            {
                var nested = property.Tag.GetValue<FStructFallback>();
                var nestedReader = new RowReader(nested);
                var x = nestedReader.Number(double.NaN, "X", "x");
                var y = nestedReader.Number(double.NaN, "Y", "y");
                if (!double.IsNaN(x) && !double.IsNaN(y)) return (x, y);
            }
            catch { }
        }

        var directX = Number(double.NaN, "WorldX", "LocationX", "PosX", "X");
        var directY = Number(double.NaN, "WorldY", "LocationY", "PosY", "Y");
        return double.IsNaN(directX) || double.IsNaN(directY) ? null : (directX, directY);
    }
}
