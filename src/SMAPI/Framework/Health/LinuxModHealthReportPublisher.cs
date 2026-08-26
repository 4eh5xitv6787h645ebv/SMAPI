using System;
using System.Threading;

namespace StardewModdingAPI.Framework.Health;

/// <summary>Opens the private Linux report directory for each attempt so a startup permission failure remains retryable.</summary>
internal sealed class LinuxModHealthReportPublisher : IModHealthReportPublisher
{
    private readonly string OutputDirectory;
    private readonly string RelativeDirectory;

    public LinuxModHealthReportPublisher(string outputDirectory, string relativeDirectory = "ErrorLogs/HealthReports")
    {
        this.OutputDirectory = outputDirectory ?? throw new ArgumentNullException(nameof(outputDirectory));
        this.RelativeDirectory = relativeDirectory;
    }

    /// <inheritdoc />
    public ModHealthPublishedReport Publish(ModHealthExportRequest request, ModHealthReportPayload payload, CancellationToken cancellationToken)
    {
        using ModHealthReportPublisher publisher = new(new LinuxModHealthReportFileSystem(this.OutputDirectory, this.RelativeDirectory));
        return publisher.Publish(request, payload, cancellationToken);
    }
}
