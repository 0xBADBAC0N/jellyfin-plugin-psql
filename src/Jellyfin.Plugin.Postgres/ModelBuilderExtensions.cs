using System;
using Jellyfin.Plugin.Postgres.ValueConverters;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;

namespace Jellyfin.Plugin.Postgres;

/// <summary>
/// ModelBuilder helpers shared across provider configuration.
/// </summary>
public static class ModelBuilderExtensions
{
    public static ModelBuilder UseValueConverterForType<T>(this ModelBuilder modelBuilder, ValueConverter converter)
    {
        var type = typeof(T);
        foreach (var entityType in modelBuilder.Model.GetEntityTypes())
        {
            foreach (var property in entityType.GetProperties())
            {
                if (property.ClrType == type)
                {
                    property.SetValueConverter(converter);
                }
            }
        }

        return modelBuilder;
    }

    public static void SetDefaultDateTimeKind(this ModelBuilder modelBuilder, DateTimeKind kind)
    {
        var converter = new DateTimeKindValueConverter(kind);
        modelBuilder.UseValueConverterForType<DateTime>(converter);
        modelBuilder.UseValueConverterForType<DateTime?>(converter);
    }
}
