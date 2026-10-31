using NoJsonSchema.Core.Ir;

namespace NoJsonSchema.Core.Emit;

static class UnionTypeEmitter
{
    // CLI output follows the consumer's target; pinned older C# compilers can opt out.
    internal const string NativeCondition = "NET11_0_OR_GREATER && !NOJSONSCHEMA_LEGACY_UNIONS";

    internal static string Condition(GenerationOptions options) => options.UseUnion switch
    {
        true => "true", false => "false", _ => NativeCondition,
    };

    internal static bool IsValueType(TypeRef type, TypeGraph graph) => type switch
    {
        TypeRef.Nullable nullable => IsValueType(nullable.Inner, graph),
        TypeRef.Named named => graph.Types[named.Name] is EnumTypeDescriptor or UnionTypeDescriptor
            or ObjectTypeDescriptor { Style: TypeStyle.ReadonlyRecordStruct },
        _ => TypeExpression.IsValueType(type),
    };

    internal static string FormatterName(string name) => $"{name}.{name}Formatter";

    public static string Emit(UnionTypeDescriptor type, TypeGraph graph, GenerationOptions options)
    {
        var w = new SourceWriter();
        var cases = type.Cases.Select((branch, index) => (Type: branch.Type, Index: index + 1)).ToList();
        var values = cases.Where(c => c.Type is not null && IsValueType(c.Type, graph)).ToList();
        var hasReference = cases.Any(c => c.Type is not null && !IsValueType(c.Type, graph));
        var nullCase = cases.FirstOrDefault(c => c.Type is null).Index;
        TypeEmitter.WriteFileHeader(w);
        w.WriteLine($"namespace {options.Namespace};");
        TypeEmitter.EmitXmlDoc(w, type.Description);
        w.WriteLine($"#if {Condition(options)}");
        w.WriteLine("[global::System.Runtime.CompilerServices.Union]");
        w.WriteLine("#endif");
        w.WriteLine($"public readonly partial struct {type.Name}");
        w.WriteLine($"#if {Condition(options)}");
        w.WriteLine("    : global::System.Runtime.CompilerServices.IUnion");
        w.WriteLine("#endif");
        using (w.BraceBlock())
        {
            w.WriteLine("readonly int _caseIndex;");
            foreach (var branch in values)
                w.WriteLine($"readonly {TypeExpression.Render(branch.Type!)} _case{branch.Index};");
            if (hasReference) w.WriteLine("readonly object? _reference;");
            var parameters = new List<string> { "int caseIndex" };
            parameters.AddRange(values.Select(c => $"{TypeExpression.Render(c.Type!)} case{c.Index} = default"));
            if (hasReference) parameters.Add("object? reference = null");
            using (w.Block($"private {type.Name}({string.Join(", ", parameters)})"))
            {
                w.WriteLine("_caseIndex = caseIndex;");
                foreach (var branch in values) w.WriteLine($"_case{branch.Index} = case{branch.Index};");
                if (hasReference) w.WriteLine("_reference = reference;");
            }
            foreach (var branch in cases)
            {
                if (branch.Type is null)
                {
                    w.WriteLine($"static {type.Name} FromCase{branch.Index}() => new(caseIndex: {branch.Index});");
                    continue;
                }
                var rendered = TypeExpression.Render(branch.Type);
                var argument = IsValueType(branch.Type, graph) ? $"case{branch.Index}" : "reference";
                var payload = !IsValueType(branch.Type, graph) && branch.Type is not TypeRef.Nullable
                    ? "value ?? throw new global::System.ArgumentNullException(nameof(value))" : "value";
                w.WriteLine($"static {type.Name} FromCase{branch.Index}({rendered} value) => new(caseIndex: {branch.Index}, {argument}: {payload});");
            }
            using (w.Block("public object? Value => _caseIndex switch"))
            {
                foreach (var branch in cases)
                {
                    var stored = branch.Type is null ? "null"
                        : IsValueType(branch.Type, graph) ? $"(object?)_case{branch.Index}" : "_reference";
                    w.WriteLine($"{branch.Index} => {stored},");
                }
                w.WriteLine("_ => null,");
            }
            w.WriteLine(";");
            using (w.Block("public bool HasValue => _caseIndex switch"))
            {
                foreach (var branch in cases.Where(c => c.Type is not null))
                {
                    var nonNull = !IsValueType(branch.Type!, graph) ? "_reference is not null"
                        : branch.Type is TypeRef.Nullable ? $"_case{branch.Index}.HasValue" : "true";
                    w.WriteLine($"{branch.Index} => {nonNull},");
                }
                w.WriteLine("_ => false,");
            }
            w.WriteLine(";");

            foreach (var group in cases.Where(c => c.Type is not null)
                .GroupBy(c => TypeExpression.Render(c.Type is TypeRef.Nullable nullable ? nullable.Inner : c.Type!)))
            {
                var first = group.First();
                var nullableCase = group.FirstOrDefault(c => c.Type is TypeRef.Nullable).Index;
                var allowsNull = nullCase != 0 || nullableCase != 0;
                var parameterType = allowsNull ? group.Key + "?" : group.Key;
                var nullIndex = nullableCase == 0 ? nullCase : nullCase == 0 ? nullableCase : Math.Min(nullableCase, nullCase);
                var unwrap = IsValueType(first.Type!, graph) && allowsNull ? "value.Value" : "value";
                using (w.Block($"public {type.Name}({parameterType} value)"))
                {
                    if (allowsNull)
                        w.WriteLine($"this = value is null ? FromCase{nullIndex}({(nullIndex == nullableCase ? "default" : "")}) : FromCase{first.Index}({unwrap});");
                    else w.WriteLine($"this = FromCase{first.Index}(value);");
                }
                w.WriteLine($"public static implicit operator {type.Name}({parameterType} value) => new(value);");
                using (w.Block($"public bool TryGetValue(out {group.Key} value)"))
                {
                    if (!IsValueType(first.Type!, graph))
                    {
                        using (w.Block($"if (_reference is {group.Key} reference)"))
                        {
                            w.WriteLine("value = reference;");
                            w.WriteLine("return true;");
                        }
                    }
                    else
                    {
                        foreach (var branch in group)
                        {
                            var check = $"_caseIndex == {branch.Index}";
                            if (branch.Type is TypeRef.Nullable) check += $" && _case{branch.Index}.HasValue";
                            using (w.Block($"if ({check})"))
                            {
                                w.WriteLine($"value = _case{branch.Index}{(branch.Type is TypeRef.Nullable ? ".Value" : "")};");
                                w.WriteLine("return true;");
                            }
                        }
                    }
                    w.WriteLine("value = default!;");
                    w.WriteLine("return false;");
                }
            }
            if (cases.All(c => c.Type is null))
            {
                using (w.Block($"public {type.Name}(object? value)"))
                {
                    w.WriteLine("if (value is not null) throw new global::System.ArgumentException(\"Expected null\", nameof(value));");
                    w.WriteLine($"this = FromCase{nullCase}();");
                }
            }
        }
        return w.ToString();
    }
}
