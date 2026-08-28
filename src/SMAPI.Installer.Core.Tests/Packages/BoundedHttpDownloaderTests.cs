using System.Net;
using System.Net.Sockets;
using System.Text;
using FluentAssertions;
using NUnit.Framework;
using StardewModdingAPI.Installer.Core.Packages;

namespace StardewModdingAPI.Installer.Core.Tests.Packages;

[TestFixture]
public sealed class BoundedHttpDownloaderTests
{
    private string TempRoot = null!;

    [SetUp]
    public void SetUp()
    {
        this.TempRoot = Path.Combine(Path.GetTempPath(), $"smapi-download-tests-{Guid.NewGuid():N}");
        Directory.CreateDirectory(this.TempRoot);
    }

    [TearDown]
    public void TearDown()
    {
        if (Directory.Exists(this.TempRoot))
            Directory.Delete(this.TempRoot, recursive: true);
    }

    [Test]
    public async Task DownloadAsync_ReviewedRedirectAndBoundedBody_AtomicallyCompletes()
    {
        byte[] content = Encoding.UTF8.GetBytes("synthetic package bytes");
        await using LoopbackHttpServer server = new(async (request, index, stream, cancellationToken) =>
        {
            if (index == 1)
                await LoopbackHttpServer.WriteResponseAsync(stream, "302 Found", "", $"Location: {serverUri(request, "/asset")}\r\n", cancellationToken);
            else
                await LoopbackHttpServer.WriteResponseAsync(stream, "200 OK", content, cancellationToken: cancellationToken);

            static Uri serverUri(HttpRequest request, string path)
            {
                return new Uri($"http://{request.Host}{path}");
            }
        });
        using BoundedHttpDownloader downloader = new(new LoopbackDownloadPolicy());
        string destination = Path.Combine(this.TempRoot, "package.zip");
        File.WriteAllText(destination + ".part", "stale partial download");
        CaptureProgress progress = new();

        DownloadResult result = await downloader.DownloadAsync(
            new Uri(server.BaseUri, "/start"),
            destination,
            new DownloadLimits(1024, TimeSpan.FromSeconds(5), 2),
            progress
        );

        File.ReadAllBytes(destination).Should().Equal(content);
        File.Exists(destination + ".part").Should().BeFalse();
        result.BytesReceived.Should().Be(content.Length);
        result.FinalUri.AbsolutePath.Should().Be("/asset");
        progress.Values.Should().ContainSingle().Which.BytesReceived.Should().Be(content.Length);
    }

    [Test]
    public async Task DownloadAsync_DeclaredBodyTooLarge_RejectsAndDeletesPart()
    {
        await using LoopbackHttpServer server = new(async (_, _, stream, cancellationToken) =>
        {
            await LoopbackHttpServer.WriteRawAsync(
                stream,
                "HTTP/1.1 200 OK\r\nContent-Length: 1000\r\nConnection: close\r\n\r\n",
                cancellationToken
            );
        });
        using BoundedHttpDownloader downloader = new(new LoopbackDownloadPolicy());
        string destination = Path.Combine(this.TempRoot, "package.zip");
        File.WriteAllText(destination, "previous verified package");

        Func<Task> action = () => downloader.DownloadAsync(
            server.BaseUri,
            destination,
            new DownloadLimits(10, TimeSpan.FromSeconds(5), 0)
        );

        await action.Should().ThrowAsync<PackageSecurityException>().WithMessage("*size limit*");
        File.ReadAllText(destination).Should().Be("previous verified package");
        File.Exists(destination + ".part").Should().BeFalse();
    }

    [Test]
    public async Task DownloadAsync_ChunkedBodyExceedsLimit_RejectsAndDeletesPart()
    {
        await using LoopbackHttpServer server = new(async (_, _, stream, cancellationToken) =>
        {
            await LoopbackHttpServer.WriteRawAsync(
                stream,
                "HTTP/1.1 200 OK\r\nTransfer-Encoding: chunked\r\nConnection: close\r\n\r\n"
                + "8\r\n12345678\r\n8\r\nabcdefgh\r\n0\r\n\r\n",
                cancellationToken
            );
        });
        using BoundedHttpDownloader downloader = new(new LoopbackDownloadPolicy());
        string destination = Path.Combine(this.TempRoot, "package.zip");

        Func<Task> action = () => downloader.DownloadAsync(
            server.BaseUri,
            destination,
            new DownloadLimits(10, TimeSpan.FromSeconds(5), 0)
        );

        await action.Should().ThrowAsync<PackageSecurityException>().WithMessage("*while streaming*");
        File.Exists(destination + ".part").Should().BeFalse();
    }

    [Test]
    public async Task DownloadAsync_ExternalCancellation_PropagatesAndDeletesPart()
    {
        await using LoopbackHttpServer server = new(async (_, _, stream, cancellationToken) =>
        {
            await LoopbackHttpServer.WriteRawAsync(
                stream,
                "HTTP/1.1 200 OK\r\nContent-Length: 100\r\nConnection: close\r\n\r\npartial",
                cancellationToken
            );
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
        });
        using BoundedHttpDownloader downloader = new(new LoopbackDownloadPolicy());
        string destination = Path.Combine(this.TempRoot, "package.zip");
        using CancellationTokenSource cancellationSource = new();

        Task<DownloadResult> download = downloader.DownloadAsync(
            server.BaseUri,
            destination,
            new DownloadLimits(1024, TimeSpan.FromSeconds(10), 0),
            cancellationToken: cancellationSource.Token
        );
        await server.RequestReceived.Task.WaitAsync(TimeSpan.FromSeconds(2));
        cancellationSource.Cancel();

        await download.Invoking(async task => await task).Should().ThrowAsync<OperationCanceledException>();
        File.Exists(destination).Should().BeFalse();
        File.Exists(destination + ".part").Should().BeFalse();
    }

    [Test]
    public async Task DownloadAsync_TotalTimeout_ReportsCredentialSafeFailureAndDeletesPart()
    {
        await using LoopbackHttpServer server = new(async (_, _, stream, cancellationToken) =>
        {
            await LoopbackHttpServer.WriteRawAsync(
                stream,
                "HTTP/1.1 200 OK\r\nContent-Length: 100\r\nConnection: close\r\n\r\n",
                cancellationToken
            );
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
        });
        using BoundedHttpDownloader downloader = new(new LoopbackDownloadPolicy());
        string destination = Path.Combine(this.TempRoot, "package.zip");

        Func<Task> action = () => downloader.DownloadAsync(
            new Uri(server.BaseUri, "/asset?token=super-secret"),
            destination,
            new DownloadLimits(1024, TimeSpan.FromMilliseconds(150), 0)
        );

        PackageSecurityException exception = (await action.Should().ThrowAsync<PackageSecurityException>()).Which;
        exception.Message.Should().Contain("timed out");
        exception.Message.Should().NotContain("super-secret");
        File.Exists(destination + ".part").Should().BeFalse();
    }

    [Test]
    public async Task DownloadAsync_HttpFailure_DoesNotEchoQueryCredentials()
    {
        await using LoopbackHttpServer server = new(async (_, _, stream, cancellationToken) =>
        {
            await LoopbackHttpServer.WriteResponseAsync(stream, "404 Not Found", "not found", cancellationToken: cancellationToken);
        });
        using BoundedHttpDownloader downloader = new(new LoopbackDownloadPolicy());

        Func<Task> action = () => downloader.DownloadAsync(
            new Uri(server.BaseUri, "/asset?token=super-secret"),
            Path.Combine(this.TempRoot, "package.zip"),
            new DownloadLimits(1024, TimeSpan.FromSeconds(5), 0)
        );

        PackageSecurityException exception = (await action.Should().ThrowAsync<PackageSecurityException>()).Which;
        exception.Message.Should().Contain("HTTP 404");
        exception.Message.Should().NotContain("super-secret");
    }

    private sealed class LoopbackDownloadPolicy : IDownloadUriPolicy
    {
        public void AssertAllowed(Uri uri, bool isInitial)
        {
            if (
                !uri.IsAbsoluteUri
                || uri.Scheme != Uri.UriSchemeHttp
                || uri.Host != IPAddress.Loopback.ToString()
                || !string.IsNullOrEmpty(uri.UserInfo)
            )
            {
                throw new PackageSecurityException("Test URI rejected.");
            }
        }
    }

    private sealed class CaptureProgress : IProgress<DownloadProgress>
    {
        public List<DownloadProgress> Values { get; } = [];

        public void Report(DownloadProgress value)
        {
            this.Values.Add(value);
        }
    }

    private sealed record HttpRequest(string Host, string Target);

    private sealed class LoopbackHttpServer : IAsyncDisposable
    {
        private readonly CancellationTokenSource CancellationSource = new();
        private readonly TcpListener Listener;
        private readonly Func<HttpRequest, int, NetworkStream, CancellationToken, Task> Responder;
        private readonly Task RunTask;

        public Uri BaseUri { get; }
        public TaskCompletionSource RequestReceived { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public LoopbackHttpServer(Func<HttpRequest, int, NetworkStream, CancellationToken, Task> responder)
        {
            this.Responder = responder;
            this.Listener = new TcpListener(IPAddress.Loopback, 0);
            this.Listener.Start();
            int port = ((IPEndPoint)this.Listener.LocalEndpoint).Port;
            this.BaseUri = new Uri($"http://{IPAddress.Loopback}:{port}/");
            this.RunTask = this.RunAsync();
        }

        public async ValueTask DisposeAsync()
        {
            this.CancellationSource.Cancel();
            this.Listener.Stop();
            try
            {
                await this.RunTask.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
            }
            catch (SocketException)
            {
            }
            this.CancellationSource.Dispose();
        }

        public static Task WriteResponseAsync(
            NetworkStream stream,
            string status,
            string body,
            string extraHeaders = "",
            CancellationToken cancellationToken = default
        )
        {
            return LoopbackHttpServer.WriteResponseAsync(
                stream,
                status,
                Encoding.UTF8.GetBytes(body),
                extraHeaders,
                cancellationToken
            );
        }

        public static async Task WriteResponseAsync(
            NetworkStream stream,
            string status,
            byte[] body,
            string extraHeaders = "",
            CancellationToken cancellationToken = default
        )
        {
            await LoopbackHttpServer.WriteRawAsync(
                stream,
                $"HTTP/1.1 {status}\r\nContent-Length: {body.Length}\r\n{extraHeaders}Connection: close\r\n\r\n",
                cancellationToken
            );
            await stream.WriteAsync(body, cancellationToken).ConfigureAwait(false);
            await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
        }

        public static async Task WriteRawAsync(NetworkStream stream, string raw, CancellationToken cancellationToken)
        {
            byte[] bytes = Encoding.ASCII.GetBytes(raw);
            await stream.WriteAsync(bytes, cancellationToken).ConfigureAwait(false);
            await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
        }

        private async Task RunAsync()
        {
            int requestIndex = 0;
            while (!this.CancellationSource.IsCancellationRequested)
            {
                using TcpClient client = await this.Listener.AcceptTcpClientAsync(this.CancellationSource.Token).ConfigureAwait(false);
                using NetworkStream stream = client.GetStream();
                HttpRequest request = await this.ReadRequestAsync(stream, this.CancellationSource.Token).ConfigureAwait(false);
                requestIndex++;
                this.RequestReceived.TrySetResult();
                await this.Responder(request, requestIndex, stream, this.CancellationSource.Token).ConfigureAwait(false);
            }
        }

        private async Task<HttpRequest> ReadRequestAsync(NetworkStream stream, CancellationToken cancellationToken)
        {
            byte[] buffer = new byte[8192];
            int count = 0;
            while (count < buffer.Length)
            {
                int read = await stream.ReadAsync(buffer.AsMemory(count, buffer.Length - count), cancellationToken).ConfigureAwait(false);
                if (read == 0)
                    break;
                count += read;
                if (Encoding.ASCII.GetString(buffer, 0, count).Contains("\r\n\r\n", StringComparison.Ordinal))
                    break;
            }

            string header = Encoding.ASCII.GetString(buffer, 0, count);
            string[] lines = header.Split("\r\n", StringSplitOptions.None);
            string target = lines[0].Split(' ', StringSplitOptions.RemoveEmptyEntries)[1];
            string host = lines.First(line => line.StartsWith("Host:", StringComparison.OrdinalIgnoreCase))[5..].Trim();
            return new HttpRequest(host, target);
        }
    }
}
