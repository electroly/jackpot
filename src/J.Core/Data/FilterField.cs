﻿namespace J.Core.Data;

public enum FilterFieldType
{
    Filename,
    TagType,
}

public readonly record struct FilterField(FilterFieldType Type, TagTypeId? TagTypeId)
{
    public string GetDisplayName(TagType? tagType) =>
        Type switch
        {
            FilterFieldType.Filename => "Filename",
            FilterFieldType.TagType => tagType!.Value.SingularName,
            _ => throw new Exception($"Unexpected {nameof(Type)}."),
        };

    public bool IsOperatorApplicable(FilterOperator o) =>
        Type switch
        {
            FilterFieldType.Filename => o is FilterOperator.ContainsString or FilterOperator.DoesNotContainString,
            FilterFieldType.TagType => o
                is FilterOperator.IsNotTag
                    or FilterOperator.IsTag
                    or FilterOperator.IsTagged
                    or FilterOperator.IsNotTagged,
            _ => throw new Exception($"Unexpected {nameof(Type)}."),
        };
}
