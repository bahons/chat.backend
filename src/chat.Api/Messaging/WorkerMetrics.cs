using System.Diagnostics.Metrics;

namespace chat.Api.Messaging;

internal static class WorkerMetrics
{
    internal const string MeterName = "chat.Workers";
    private static readonly Meter Meter = new(MeterName, "1.0.0");
    internal static readonly Counter<long> RetentionDeleted = Meter.CreateCounter<long>("chat.retention.affected", "{row}");
    internal static readonly Histogram<double> RetentionDuration = Meter.CreateHistogram<double>("chat.retention.duration", "s");
    internal static readonly Histogram<int> Overdue = Meter.CreateHistogram<int>("chat.queue.overdue.capped", "{row}");

    internal static void RecordRetention(string queue, int count) =>
        RetentionDeleted.Add(count, new KeyValuePair<string, object?>("queue", queue));
}
