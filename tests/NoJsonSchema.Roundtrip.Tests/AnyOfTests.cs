using System.Reflection;
using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using NoJsonSchema.Core;
using NoJsonSchema.Core.Schema;
using Xunit;

namespace NoJsonSchema.Roundtrip.Tests;

public class AnyOfTests
{
    const string Schema = """
        {"$defs": {
          "Choice": {"anyOf": [{"type":"integer"}, {"type":"string"}, {"type":"null"}, {"type":"number"}]},
          "Cat": {"type":"object", "properties":{"meow":{"type":"boolean"}}, "required":["meow"]},
          "Dog": {"type":"object", "properties":{"bark":{"type":"boolean"}}, "required":["bark"]},
          "Pet": {"anyOf":[{"$ref":"#/$defs/Cat"}, {"$ref":"#/$defs/Dog"}]},
          "Envelope": {"type":"object", "properties":{
            "choice":{"$ref":"#/$defs/Choice"},
            "inline":{"anyOf":[{"type":"string"},{"type":"boolean"}]},
            "choices":{"type":"array","items":{"$ref":"#/$defs/Choice"}},
            "map":{"type":"object","additionalProperties":{"$ref":"#/$defs/Choice"}}
          }}
        }}
        """;

    static Assembly Compile(string schema = Schema, GenerationOptions? options = null)
    {
        var result = new GeneratorPipeline().Generate(
            schema,
            options ?? new GenerationOptions { Namespace = "AnyOf", UseUnion = false }
        );
        var parse = new CSharpParseOptions(
            LanguageVersion.Latest,
            preprocessorSymbols:
            [
                "NET5_0_OR_GREATER",
                "NET6_0_OR_GREATER",
                "NET7_0_OR_GREATER",
                "NET8_0_OR_GREATER",
            ]
        );
        var trees = result.Files.Select(f => CSharpSyntaxTree.ParseText(f.SourceText, parse));
        var refs = AppDomain
            .CurrentDomain.GetAssemblies()
            .Where(a => !a.IsDynamic && !string.IsNullOrEmpty(a.Location))
            .Select(a => MetadataReference.CreateFromFile(a.Location));
        var compilation = CSharpCompilation.Create(
            "AnyOf_" + Guid.NewGuid().ToString("N"),
            trees,
            refs,
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary)
        );
        using var stream = new MemoryStream();
        var emit = compilation.Emit(stream);
        Assert.True(
            emit.Success,
            string.Join("\n", emit.Diagnostics.Where(d => d.Severity == DiagnosticSeverity.Error))
        );
        return Assembly.Load(stream.ToArray());
    }

    static object Read(Assembly assembly, string name, string json)
    {
        var type = assembly.GetType("AnyOf." + name)!;
        var method = assembly
            .GetType("AnyOf.AnyOfSerializer")!
            .GetMethods()
            .Single(m =>
                m.Name == "Deserialize" && m.GetParameters()[0].ParameterType == typeof(byte[])
            );
        return method.MakeGenericMethod(type).Invoke(null, [Encoding.UTF8.GetBytes(json), null])!;
    }

    static string Write(Assembly assembly, string name, object value) =>
        Encoding.UTF8.GetString(
            (byte[])
                assembly
                    .GetType("AnyOf.AnyOfSerializer")!
                    .GetMethod("SerializeToUtf8Bytes")!
                    .MakeGenericMethod(assembly.GetType("AnyOf." + name)!)
                    .Invoke(null, [value, null])!
        );

    [Fact]
    public void Storage_UsesTypedValueFieldsAndOneReferenceSlot()
    {
        var assembly = Compile();
        var choice = assembly.GetType("AnyOf.Choice")!;
        var fields = choice.GetFields(BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.Single(fields, f => !f.FieldType.IsValueType);
        Assert.Contains(fields, f => f.FieldType == typeof(long));
        Assert.Contains(fields, f => f.FieldType == typeof(double));
        var pet = assembly.GetType("AnyOf.Pet")!;
        Assert.Single(pet.GetFields(BindingFlags.Instance | BindingFlags.NonPublic), f => !f.FieldType.IsValueType);
        Assert.Null(assembly.GetType("AnyOf.ChoiceCase1"));

        var value = Read(assembly, "Choice", "42");
        object?[] arguments = [0L];
        Assert.Equal(true, choice.GetMethod("TryGetValue", [typeof(long).MakeByRefType()])!.Invoke(value, arguments));
        Assert.Equal(42L, arguments[0]);
        Assert.Equal(true, choice.GetProperty("HasValue")!.GetValue(value));
        Assert.DoesNotContain(choice.GetMembers(BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly),
            member => member.Name.Contains("Case", StringComparison.Ordinal));
        Assert.Null(assembly.GetType("AnyOf.ChoiceFormatter"));
        var empty = Activator.CreateInstance(choice)!;
        Assert.Equal(false, choice.GetProperty("HasValue")!.GetValue(empty));
        Assert.Throws<TargetInvocationException>(() => Write(assembly, "Choice", empty));
    }

    [Fact]
    public void DuplicateClrTypes_ExposeOneTypedConstructor()
    {
        const string schema = """{"$defs":{"Choice":{"anyOf":[{"type":"integer"},{"type":"integer"}]}}}""";
        var assembly = Compile(schema);
        var choice = assembly.GetType("AnyOf.Choice")!;
        Assert.Single(choice.GetConstructors());
        var value = Activator.CreateInstance(choice, [42L])!;
        Assert.Equal(42L, choice.GetProperty("Value")!.GetValue(value));
        Assert.Equal("42", Write(assembly, "Choice", value));
        Assert.Equal("42", Write(assembly, "Choice", Read(assembly, "Choice", "42")));
    }

    [Fact]
    public void ValueProperty_PreservesPayloadTypeForNumericOnlyChoices()
    {
        const string schema = """{"$defs":{"Choice":{"anyOf":[{"type":"integer"},{"type":"number"}]}}}""";
        var assembly = Compile(schema);
        var integer = Read(assembly, "Choice", "42");
        Assert.IsType<long>(integer.GetType().GetProperty("Value")!.GetValue(integer));
        var number = Read(assembly, "Choice", "1.5");
        Assert.IsType<double>(number.GetType().GetProperty("Value")!.GetValue(number));
    }

    [Fact]
    public void ValueObjectAndEnumCases_StayInTypedFields()
    {
        const string schema = """
            {"$defs":{
              "Point":{"type":"object","properties":{"x":{"type":"integer"}},"required":["x"]},
              "Color":{"type":"string","enum":["red","blue"]},
              "Choice":{"anyOf":[{"$ref":"#/$defs/Point"},{"$ref":"#/$defs/Color"}]}
            }}
            """;
        var assembly = Compile(schema, new GenerationOptions
        {
            Namespace = "AnyOf", UseUnion = false, ValueObjectTypes = ["Point"],
        });
        var fields = assembly.GetType("AnyOf.Choice")!.GetFields(BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.All(fields, f => Assert.True(f.FieldType.IsValueType));
        Assert.Equal("{\"x\":42}", Write(assembly, "Choice", Read(assembly, "Choice", "{\"x\":42}")));
        Assert.Equal("\"blue\"", Write(assembly, "Choice", Read(assembly, "Choice", "\"blue\"")));
    }

    [Fact]
    public void StructContainer_RoundTripsOptionalUnionProperties()
    {
        var assembly = Compile(Schema, new GenerationOptions
        {
            Namespace = "AnyOf", UseUnion = false, ValueObjectTypes = ["Envelope"],
        });
        const string json = "{\"choice\":42,\"inline\":true,\"choices\":[42],\"map\":{\"a\":42}}";
        Assert.Equal(json, Write(assembly, "Envelope", Read(assembly, "Envelope", json)));
    }

    [Theory]
    [InlineData("42", typeof(long))]
    [InlineData("\"hello\"", typeof(string))]
    [InlineData("null", null)]
    [InlineData("1.5", typeof(double))]
    public void Scalars_ChooseFirstMatchingBranch_AndRoundTrip(string json, Type? payloadType)
    {
        var assembly = Compile();
        var value = Read(assembly, "Choice", json);
        Assert.True(value.GetType().IsValueType);
        Assert.Equal(payloadType, value.GetType().GetProperty("Value")!.GetValue(value)?.GetType());
        Assert.Equal(json, Write(assembly, "Choice", value));
    }

    [Fact]
    public void Objects_CheckRequiredProperties_AndRestoreTokenizerAfterFailure()
    {
        var assembly = Compile();
        var value = Read(assembly, "Pet", "{\"bark\":true}");
        Assert.Equal("AnyOf.Dog", value.GetType().GetProperty("Value")!.GetValue(value)!.GetType().FullName);
        Assert.Equal("{\"bark\":true}", Write(assembly, "Pet", value));
        var error = Assert.Throws<TargetInvocationException>(() => Read(assembly, "Pet", "{}"));
        Assert.Equal("NoJsonFormatException", error.InnerException!.GetType().Name);
    }

    [Fact]
    public void Inline_ArrayAndDictionary_UseUnionFormatters()
    {
        var assembly = Compile();
        const string json =
            "{\"choice\":42,\"inline\":true,\"choices\":[42,\"hello\",null],\"map\":{\"key\":\"value\"}}";
        Assert.Equal(json, Write(assembly, "Envelope", Read(assembly, "Envelope", json)));
    }

    [Theory]
    [InlineData("true")]
    [InlineData("[]")]
    [InlineData("42 false")]
    public void NoMatchOrTrailingInput_Throws(string json)
    {
        var error = Assert.Throws<TargetInvocationException>(() => Read(Compile(), "Choice", json));
        Assert.Equal("NoJsonFormatException", error.InnerException!.GetType().Name);
    }

    [Theory]
    [InlineData(
        """{"$defs":{"Choice":{"anyOf":[{"type":["integer","null"]},{"type":"string"}]}}}""",
        "null"
    )]
    [InlineData("""{"$defs":{"Choice":{"anyOf":[{"type":"null"}]}}}""", "null")]
    [InlineData("""{"$defs":{"Choice":{"anyOf":[{"type":"integer"},{"type":["integer","null"]}]}}}""", "null")]
    [InlineData("""{"$defs":{"Choice":{"anyOf":[{"type":"integer","format":"int32"},{"type":"null"}]}}}""", "42")]

    [InlineData(
        """{"$defs":{"Choice":{"anyOf":[{"type":["object","null"],"properties":{"x":{"type":"string"}},"required":["x"]},{"type":"string"}]}}}""",
        "null"
    )]
    [InlineData(
        """{"$defs":{"Choice":{"anyOf":[{"type":"array","items":{"type":"integer"}},{"type":"string"}]}}}""",
        "[1,2]"
    )]
    [InlineData(
        """{"$defs":{"Choice":{"anyOf":[{"type":"object","additionalProperties":{"type":"integer"}},{"type":"string"}]}}}""",
        "{\"a\":1}"
    )]
    public void NullableAndCollectionCases_RoundTrip(string schema, string json)
    {
        var assembly = Compile(schema);
        Assert.Equal(json, Write(assembly, "Choice", Read(assembly, "Choice", json)));
    }

    [Fact]
    public void NonConsumingRecursiveAlternatives_AreRejected()
    {
        Assert.Throws<SchemaLoadException>(() =>
            new GeneratorPipeline().Generate(
                """{"$defs":{"Choice":{"anyOf":[{"$ref":"#/$defs/Choice"},{"type":"string"}]}}}""",
                new GenerationOptions()
            )
        );
    }

    [Fact]
    public void ObjectRecursion_ConsumesInputAndRoundTrips()
    {
        const string schema = """
            {"$defs":{
              "Choice":{"anyOf":[{"type":"integer"},{"$ref":"#/$defs/Node"}]},
              "Node":{"type":"object","properties":{"child":{"$ref":"#/$defs/Choice"}},"required":["child"]}
            }}
            """;
        var assembly = Compile(schema);
        const string json = "{\"child\":{\"child\":42}}";
        Assert.Equal(json, Write(assembly, "Choice", Read(assembly, "Choice", json)));
    }

    [Fact]
    public void IncludeFilter_KeepsBranchDependencies()
    {
        var result = new GeneratorPipeline().Generate(
            Schema,
            new GenerationOptions { IncludedTypes = ["Pet"] }
        );
        Assert.Contains(result.Files, f => f.FileName == "Cat.g.cs");
        Assert.Contains(result.Files, f => f.FileName == "Dog.g.cs");
        Assert.DoesNotContain(result.Files, f => f.FileName == "Choice.g.cs");
    }

    [Theory]
    [InlineData("""{"$defs":{"Choice":{"anyOf":[{}, {"type":"string"}]}}}""")]
    [InlineData(
        """{"$defs":{"Choice":{"anyOf":[{"type":"array","items":{"type":"array","items":{"type":"integer"}}},{"type":"string"}]}}}"""
    )]
    [InlineData(
        """{"$defs":{"Choice":{"anyOf":[{"type":"array","items":{"type":["integer","null"]}},{"type":"string"}]}}}"""
    )]
    public void UnsupportedPayloads_AreRejectedInsteadOfLosingData(string schema)
    {
        Assert.Throws<SchemaLoadException>(() =>
            new GeneratorPipeline().Generate(schema, new GenerationOptions())
        );
    }

    [Fact]
    public void SiblingConstraints_AreRejectedInsteadOfIgnored()
    {
        Assert.Throws<SchemaLoadException>(() =>
            new GeneratorPipeline().Generate(
                """{"type":"string","anyOf":[{"type":"string"}]}""",
                new GenerationOptions()
            )
        );
    }
}
