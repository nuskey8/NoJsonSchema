using NoJsonSchema.Core.Ir;

namespace NoJsonSchema.Core.Emit;

public static class UnionFormatterEmitter
{
    public static string Emit(UnionTypeDescriptor type, TypeGraph graph, GenerationOptions options)
    {
        var w = new SourceWriter();
        var ctx = new FormatterEmitter.EmitContext(graph);
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
                w.WriteLine("var token = tokenizer.PeekToken();");
                w.WriteLine("var original = tokenizer;");
                foreach (var branch in type.Cases)
                {
                    using (w.Block($"if ({UnionTokenCondition(branch.Type, graph)})"))
                    {
                        using (w.Block("try"))
                            w.WriteLine($"return Read{branch.Name}(ref tokenizer, options);");
                        using (w.Block("catch (NoJsonFormatException)"))
                            w.WriteLine("tokenizer = original;");
                    }
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
                    w.WriteLine($"var value = default(Holder{branch.Name});");
                    FormatterEmitter.EmitReadValueInto(w, UnionProperty(branch.Type), ctx);
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
                                    FormatterEmitter.EmitWriteCoreValue(w, UnionProperty(branch.Type), FormatterEmitter.Unwrap(branch.Type), "branchValue", ctx.IsValueTypeReference(branch.Type), ctx);
                            }
                            else FormatterEmitter.EmitWriteCoreValue(w, UnionProperty(branch.Type), branch.Type, "branchValue", false, ctx);
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

    static string UnionTokenCondition(TypeRef? type, TypeGraph graph)
    {
        var conditions = new HashSet<string>(StringComparer.Ordinal);
        var visited = new HashSet<string>(StringComparer.Ordinal);
        Visit(type);
        return string.Join(" || ", conditions.Select(condition => "(" + condition + ")"));

        void Visit(TypeRef? current)
        {
            switch (current)
            {
                case null: conditions.Add("token == (byte)'n'"); break;
                case TypeRef.Nullable nullable:
                    conditions.Add("token == (byte)'n'");
                    Visit(nullable.Inner);
                    break;
                case TypeRef.Array: conditions.Add("token == (byte)'['"); break;
                case TypeRef.Dictionary: conditions.Add("token == (byte)'{'"); break;
                case TypeRef.Named named when visited.Add(named.Name):
                    switch (graph.Types[named.Name])
                    {
                        case ObjectTypeDescriptor: conditions.Add("token == (byte)'{'"); break;
                        case EnumTypeDescriptor: conditions.Add("token == (byte)'\"'"); break;
                        case UnionTypeDescriptor union:
                            foreach (var branch in union.Cases) Visit(branch.Type);
                            break;
                        default: conditions.Add("true"); break;
                    }
                    break;
                case TypeRef.Named: break;
                case TypeRef.Primitive primitive:
                    conditions.Add(primitive.Kind switch
                    {
                        PrimitiveKind.Boolean => "token == (byte)'t' || token == (byte)'f'",
                        PrimitiveKind.SByte or PrimitiveKind.Byte or PrimitiveKind.Int16 or PrimitiveKind.UInt16
                            or PrimitiveKind.Int32 or PrimitiveKind.UInt32 or PrimitiveKind.Int64 or PrimitiveKind.UInt64
                            or PrimitiveKind.Single or PrimitiveKind.Double => "token == (byte)'-' || (token >= (byte)'0' && token <= (byte)'9')",
                        _ => "token == (byte)'\"'",
                    });
                    break;
                default: conditions.Add("true"); break;
            }
        }
    }
}
