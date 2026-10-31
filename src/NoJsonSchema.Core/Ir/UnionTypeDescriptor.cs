namespace NoJsonSchema.Core.Ir;

/// <summary>An anyOf value represented by one of its ordered cases.</summary>
public sealed record UnionTypeDescriptor : TypeDescriptor
{
    public required IReadOnlyList<UnionCase> Cases { get; init; }
}

public sealed record UnionCase(string Name, TypeRef? Type);
