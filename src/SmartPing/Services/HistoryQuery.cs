using System.Diagnostics.CodeAnalysis;

namespace SmartPing.Services;

public sealed record HistoryQuery(double Hours, int RequestedBucketSeconds)
{
    public const int DefaultBucketSeconds = 15;
    public const int MaximumChartPoints = 3600;
    public double Minutes => Hours * 60;

    public static bool TryCreate(double? hours, int? minutes, int? bucketSeconds,
        [NotNullWhen(true)] out HistoryQuery? query, out string? error)
    {
        query = null;
        error = null;
        if (hours is not null && minutes is not null)
            error = "请只指定分钟或小时统计周期，不能同时设置。";
        else if (minutes is < 1 or > 60)
            error = "自定义统计周期必须为 1–60 分钟的整数。";
        else if (hours is { } value && !double.IsFinite(value))
            error = "统计时间范围无效。";
        else if (bucketSeconds is < 1 or > 3600)
            error = "统计间隔必须为 1–3600 秒的整数。";
        if (error is not null) return false;

        var windowHours = minutes is { } minuteWindow ? minuteWindow / 60.0 : Math.Clamp(hours ?? 1, 1.0 / 60, 168);
        query = new(windowHours, bucketSeconds ?? DefaultBucketSeconds);
        return true;
    }

    public static int ResolveBucketSeconds(double hours, int? requestedSeconds, int maxPoints = MaximumChartPoints)
    {
        if (!double.IsFinite(hours) || hours <= 0 || hours > 168)
            throw new ArgumentOutOfRangeException(nameof(hours));
        if (requestedSeconds is < 1 or > 3600)
            throw new ArgumentOutOfRangeException(nameof(requestedSeconds));
        if (maxPoints is < 1 or > MaximumChartPoints)
            throw new ArgumentOutOfRangeException(nameof(maxPoints));
        return Math.Max(requestedSeconds ?? DefaultBucketSeconds, (int)Math.Ceiling(hours * 3600 / maxPoints));
    }
}
