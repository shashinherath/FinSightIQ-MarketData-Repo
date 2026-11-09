using System.Data;
using System.Globalization;
using Dapper;

namespace FinSightIQ.MarketDataService.Infrastructure.Persistence;

/// <summary>
/// Maps PostgreSQL DATE values to <see cref="DateOnly"/>.
/// </summary>
public sealed class DateOnlyTypeHandler : SqlMapper.TypeHandler<DateOnly>
{
    public override void SetValue(IDbDataParameter parameter, DateOnly value)
    {
        parameter.DbType = DbType.Date;
        parameter.Value = value.ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc);
    }

    public override DateOnly Parse(object value)
    {
        return value switch
        {
            DateOnly dateOnly => dateOnly,
            DateTime dateTime => DateOnly.FromDateTime(dateTime),
            DateTimeOffset dateTimeOffset => DateOnly.FromDateTime(dateTimeOffset.UtcDateTime),
            string stringValue => DateOnly.Parse(stringValue, CultureInfo.InvariantCulture),
            _ => throw new DataException($"Cannot convert {value.GetType()} to DateOnly.")
        };
    }
}

/// <summary>
/// Maps PostgreSQL TIMESTAMPTZ values to UTC <see cref="DateTimeOffset"/>.
/// </summary>
public sealed class DateTimeOffsetTypeHandler : SqlMapper.TypeHandler<DateTimeOffset>
{
    public override void SetValue(IDbDataParameter parameter, DateTimeOffset value)
    {
        parameter.DbType = DbType.DateTimeOffset;
        parameter.Value = value.ToUniversalTime();
    }

    public override DateTimeOffset Parse(object value)
    {
        return value switch
        {
            DateTimeOffset dto => dto.ToUniversalTime(),
            DateTime dt when dt.Kind == DateTimeKind.Utc => new DateTimeOffset(dt, TimeSpan.Zero),
            DateTime dt when dt.Kind == DateTimeKind.Local => new DateTimeOffset(dt).ToUniversalTime(),
            DateTime dt => new DateTimeOffset(DateTime.SpecifyKind(dt, DateTimeKind.Utc), TimeSpan.Zero),
            string str => DateTimeOffset.Parse(str, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal).ToUniversalTime(),
            _ => throw new DataException($"Cannot convert {value.GetType()} to DateTimeOffset.")
        };
    }
}

/// <summary>
/// Configures global Dapper conventions and custom SQL type handlers.
/// </summary>
public static class DapperConfiguration
{
    private static bool _isConfigured;
    private static readonly object SyncLock = new();

    public static void Configure()
    {
        if (_isConfigured)
        {
            return;
        }

        lock (SyncLock)
        {
            if (_isConfigured)
            {
                return;
            }

            DefaultTypeMap.MatchNamesWithUnderscores = true;
            SqlMapper.AddTypeHandler(new DateOnlyTypeHandler());
            SqlMapper.AddTypeHandler(new DateTimeOffsetTypeHandler());

            _isConfigured = true;
        }
    }
}

