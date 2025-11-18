using System;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;

namespace Jellyfin.Plugin.Postgres.ValueConverters;

/// <summary>
/// Ensures DateTime values are stored and read using a fixed <see cref="DateTimeKind"/>.
/// </summary>
public sealed class DateTimeKindValueConverter : ValueConverter<DateTime, DateTime>
{
    public DateTimeKindValueConverter(DateTimeKind kind, ConverterMappingHints? mappingHints = null)
        : base(v => v.ToUniversalTime(), v => DateTime.SpecifyKind(v, kind), mappingHints)
    {
    }
}
