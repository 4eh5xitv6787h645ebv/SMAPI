using System;

namespace StardewModdingAPI.Framework.Health;

/// <summary>A narrow bounded queue which builds and writes reports from frozen source snapshots.</summary>
/// <remarks>Implementations allow at most one writing request, one pending request, and one exact failed request retained for retry.</remarks>
internal interface IModHealthExportQueue
{
    /// <summary>Queue a frozen export request.</summary>
    ModHealthExportQueueResult Enqueue(ModHealthExportRequest request);

    /// <summary>Retry the exact frozen request retained after the most recent failed export.</summary>
    ModHealthExportQueueResult Retry(Guid? requestId = null);

    /// <summary>Discard any failed request retained for retry.</summary>
    void DiscardRetryable(Guid? requestId = null);

    /// <summary>Get the latest status, optionally for one specific request.</summary>
    ModHealthExportStatus GetStatus(Guid? requestId = null);

    /// <summary>Get the latest prepared-report state, or one exact request without substituting another model.</summary>
    ModHealthPreparedReportSnapshot GetPreparedReport(Guid? requestId = null);
}
