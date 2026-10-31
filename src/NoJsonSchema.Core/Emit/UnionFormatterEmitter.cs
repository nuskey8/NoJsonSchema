using NoJsonSchema.Core.Ir;

namespace NoJsonSchema.Core.Emit;

public static partial class FormatterEmitter
{
    internal static string EmitUnion(UnionTypeDescriptor type, TypeGraph graph, GenerationOptions options)
    {
        var w = new SourceWriter();
        var ctx = new EmitContext(graph);
        TypeEmitter.WriteFileHeader(w);
        w.WriteLine("using System;");
        w.WriteLine($"namespace {options.Namespace};");
        using (w.Block($"public readonly partial struct {type.Name}"))
        using (w.Block($"internal static class {type.Name}Formatter"))
        {
            using (w.Block($"public static {type.Name} Deserialize(ReadOnlySpan<byte> utf8Json, NoJsonSerializerOptions options)"))
            {
                w.WriteLine("var tokenizer = new Utf8JsonTokenizer(utf8Json);");
                w.WriteLine("var value = ReadValue(ref tokenizer, options);");
                w.WriteLine("tokenizer.SkipWhitespace();");
                w.WriteLine("if (tokenizer.Position != utf8Json.Length) tokenizer.ThrowFormatException(\"Unexpected trailing JSON\");");
                w.WriteLine("return value;");
            }
            using (w.Block($"internal static {type.Name} ReadValue(ref Utf8JsonTokenizer tokenizer, NoJsonSerializerOptions options)"))
            {
                w.WriteLine("var original = tokenizer;");
                foreach (var branch in type.Cases)
                {
                    using (w.Block("try"))
                    {
                        w.WriteLine($"var result = Read{branch.Name}(ref tokenizer, options);");
                        w.WriteLine("return result;");
                    }
                    using (w.Block("catch (NoJsonFormatException)"))
                        w.WriteLine("tokenizer = original;");
                }
                w.WriteLine($"tokenizer.ThrowFormatException(\"No anyOf branch matched {type.Name}\");");
                w.WriteLine("return default!;");
            }
            for (var i = 0; i < type.Cases.Count; i++)
            {
                var branch = type.Cases[i];
                using (w.Block($"static {type.Name} Read{branch.Name}(ref Utf8JsonTokenizer tokenizer, NoJsonSerializerOptions options)"))
                {
                    if (branch.Type is null)
                    {
                        w.WriteLine("if (!tokenizer.TryReadNull()) tokenizer.ThrowFormatException(\"Expected null\");");
                        w.WriteLine($"return {type.Name}.FromCase{i + 1}();");
                        continue;
                    }
                    EmitUnionRequiredCheck(w, branch.Type, graph);
                    w.WriteLine($"var value = default(Holder{branch.Name});");
                    EmitReadValueInto(w, UnionProperty(branch.Type), ctx);
                    w.WriteLine($"return {type.Name}.FromCase{i + 1}(value.Value);");
                }
                if (branch.Type is not null)
                {
                    using (w.Block($"struct Holder{branch.Name}"))
                        w.WriteLine($"public {TypeExpression.Render(branch.Type)} Value;");
                }
            }
            using (w.Block($"public static void Serialize(global::System.Buffers.IBufferWriter<byte> writer, in {type.Name} value, NoJsonSerializerOptions options)"))
            {
                w.WriteLine("var w = new Utf8JsonBufferWriter(writer);");
                w.WriteLine("WriteValue(ref w, value, options);");
                w.WriteLine("w.Flush();");
            }
            using (w.Block($"internal static void WriteValue(ref Utf8JsonBufferWriter w, {type.Name} value, NoJsonSerializerOptions options)"))
            {
                w.WriteLine("switch (value._caseIndex)");
                using (w.BraceBlock())
                {
                    for (var i = 0; i < type.Cases.Count; i++)
                    {
                        var branch = type.Cases[i];
                        w.WriteLine($"case {i + 1}:");
                        using (w.BraceBlock())
                        {
                            if (branch.Type is not null)
                            {
                                var stored = UnionTypeEmitter.IsValueType(branch.Type, graph)
                                    ? $"value._case{i + 1}" : $"({TypeExpression.Render(branch.Type)})value._reference!";
                                w.WriteLine($"var branchValue = {stored};");
                            }
                            if (branch.Type is null) w.WriteLine("w.WriteNull();");
                            else if (branch.Type is TypeRef.Nullable)
                            {
                                using (w.Block("if (branchValue is null)")) w.WriteLine("w.WriteNull();");
                                w.WriteLine("else");
                                using (w.BraceBlock())
                                    EmitWriteCoreValue(w, UnionProperty(branch.Type), Unwrap(branch.Type), "branchValue", ctx.IsValueTypeReference(branch.Type), ctx);
                            }
                            else EmitWriteCoreValue(w, UnionProperty(branch.Type), branch.Type, "branchValue", false, ctx);
                            w.WriteLine("return;");
                        }
                    }
                    w.WriteLine("default: throw new InvalidOperationException(\"Uninitialized anyOf value\");");
                }
            }
        }
        return w.ToString();
    }

    static PropertyDescriptor UnionProperty(TypeRef type) => new()
    {
        Name = "Value", JsonName = "value", Type = type, IsRequired = true,
        IsNullable = type is TypeRef.Nullable,
    };

    static void EmitUnionRequiredCheck(SourceWriter w, TypeRef type, TypeGraph graph)
    {
        if (Unwrap(type) is not TypeRef.Named named
            || !graph.Types.TryGetValue(named.Name, out var descriptor)
            || descriptor is not ObjectTypeDescriptor obj) return;
        var required = FlattenProperties(obj, graph).Where(p => p.IsRequired).ToList();
        if (required.Count == 0) return;
        w.WriteLine("var probe = tokenizer;");
        using var nullableScope = type is TypeRef.Nullable ? w.Block("if (!probe.TryReadNull())") : null;
        w.WriteLine("probe.ReadStartObject();");
        for (var i = 0; i < required.Count; i++) w.WriteLine($"bool found{i} = false;");
        using (w.Block("while (probe.TryReadPropertyName(out var name))"))
        {
            for (var i = 0; i < required.Count; i++)
                w.WriteLine($"if (name.SequenceEqual({EncodeUtf8Literal(EscapeJsonString(required[i].JsonName))})) found{i} = true;");
            w.WriteLine("probe.SkipValue();");
        }
        w.WriteLine($"if ({string.Join(" || ", required.Select((_, i) => "!found" + i))}) tokenizer.ThrowFormatException(\"Missing required anyOf property\");");
    }
}
