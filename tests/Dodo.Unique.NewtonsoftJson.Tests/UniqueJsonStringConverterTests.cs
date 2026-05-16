using Newtonsoft.Json;
using TUnit.Core;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;

namespace Dodo.Unique.NewtonsoftJson.Tests;

public sealed class UniqueJsonStringConverterTests
{
    private static JsonSerializerSettings SettingsWithPool(UniqueStringPool pool)
        => new() { Converters = { new UniqueJsonStringConverter(pool) } };

    [Test]
    public async Task Deserialize_same_value_twice_returns_reference_equal_string()
    {
        var pool = new UniqueStringPool(TimeSpan.FromHours(1));
        var settings = SettingsWithPool(pool);

        var a = JsonConvert.DeserializeObject<string>("\"vegan\"", settings);
        var b = JsonConvert.DeserializeObject<string>("\"vegan\"", settings);

        await Assert.That(ReferenceEquals(a, b)).IsTrue();
    }

    [Test]
    public async Task Deserialize_null_returns_null()
    {
        var settings = SettingsWithPool(new UniqueStringPool(TimeSpan.FromHours(1)));

        await Assert.That(JsonConvert.DeserializeObject<string?>("null", settings)).IsNull();
    }

    [Test]
    public async Task Deserialize_inside_object_dedupes_repeated_field_values()
    {
        var pool = new UniqueStringPool(TimeSpan.FromHours(1));
        var settings = SettingsWithPool(pool);
        var json = "[{\"Name\":\"Pepperoni\"},{\"Name\":\"Pepperoni\"}]";

        var items = JsonConvert.DeserializeObject<Item[]>(json, settings)!;

        await Assert.That(ReferenceEquals(items[0].Name, items[1].Name)).IsTrue();
    }

    [Test]
    public async Task Write_passes_value_through_unchanged()
    {
        var settings = SettingsWithPool(new UniqueStringPool(TimeSpan.FromHours(1)));

        var json = JsonConvert.SerializeObject("hello", settings);

        await Assert.That(json).IsEqualTo("\"hello\"");
    }

    [Test]
    public async Task Write_null_emits_null()
    {
        var settings = SettingsWithPool(new UniqueStringPool(TimeSpan.FromHours(1)));

        var json = JsonConvert.SerializeObject((string?)null, settings);

        await Assert.That(json).IsEqualTo("null");
    }

    [Test]
    public async Task Constructor_null_pool_throws()
    {
        await Assert.That(() => new UniqueJsonStringConverter(null!)).Throws<ArgumentNullException>();
    }

    [Test]
    public async Task Deserialize_non_string_token_throws()
    {
        var settings = SettingsWithPool(new UniqueStringPool(TimeSpan.FromHours(1)));

        await Assert.That(() => JsonConvert.DeserializeObject<string>("123", settings))
            .Throws<JsonSerializationException>();
    }

    private sealed class Item
    {
        public string Name { get; set; } = string.Empty;
    }
}
