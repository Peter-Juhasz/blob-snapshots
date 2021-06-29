using Azure;
using Azure.Storage.Blobs;
using Azure.Storage.Blobs.Models;
using Azure.Storage.Blobs.Specialized;

using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

using System;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Threading;
using System.Threading.Tasks;

namespace Framework.Server
{
    public class HttpBlobSnapshotDelegatingHandler : DelegatingHandler
    {
        public HttpBlobSnapshotDelegatingHandler(BlobServiceClient blobServiceClient, IOptions<HttpBlobSnapshotOptions> options, ILogger<HttpBlobSnapshotDelegatingHandler> logger)
        {
            BlobServiceClient = blobServiceClient;
            Logger = logger;
            Options = options.Value;
            ContainerClient = BlobServiceClient.GetBlobContainerClient(Options.ContainerName);
        }

        private BlobServiceClient BlobServiceClient { get; }
        private ILogger<HttpBlobSnapshotDelegatingHandler> Logger { get; }
        public HttpBlobSnapshotOptions Options { get; }
        private BlobContainerClient ContainerClient { get; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            // check whether this request is applicable
            if (!IsApplicableTo(request))
            {
                return await base.SendAsync(request, cancellationToken);
            }

            // find blob
            var key = Options.KeySelector(request, Options);
            var blob = ContainerClient.GetBlockBlobClient(key);

            try
            {
                // download
                var blobResponse = (await blob.DownloadAsync(cancellationToken)).Value;

                // create response
                var content = new StreamContent(blobResponse.Content);
                content.Headers.ContentType = MediaTypeHeaderValue.Parse(blobResponse.ContentType);
                content.Headers.ContentLength = blobResponse.ContentLength;
                var responseMessage = new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = content
                };
                return responseMessage;
            }
            catch (RequestFailedException ex) when (
                ex.ErrorCode == BlobErrorCode.ContainerNotFound ||
                ex.ErrorCode == BlobErrorCode.BlobNotFound ||
                ex.ErrorCode == BlobErrorCode.OperationTimedOut
            )
            { }
            catch (Exception ex)
            {
                Logger.LogWarning(ex, "Couldn't access HTTP blob snapshot storage.");
            }

            // execute request
            var response = await base.SendAsync(request, cancellationToken);

            // save
            if (IsApplicableTo(response))
            {
                // buffer content
                await response.Content.LoadIntoBufferAsync();

                // read
                var stream = await response.Content.ReadAsStreamAsync(cancellationToken);

                // upload
                var options = new BlobUploadOptions
                {
                    HttpHeaders = new BlobHttpHeaders
                    {
                        ContentType = response.Content.Headers.ContentType.ToString(),
                    },
                    AccessTier = AccessTier.Hot,
                };
                try
                {
                    await blob.UploadAsync(stream, options, cancellationToken);
                }
                // container may not exists on very first call
                catch (RequestFailedException ex) when (ex.ErrorCode == BlobErrorCode.ContainerNotFound)
                {
                    // create blob container
                    await ContainerClient.CreateIfNotExistsAsync(PublicAccessType.None, cancellationToken: cancellationToken);

                    // retry
                    stream.Seek(0L, SeekOrigin.Begin);
                    await blob.UploadAsync(stream, options, cancellationToken);
                }
                catch (Exception ex)
                {
                    Logger.LogWarning(ex, "Couldn't save HTTP snapshot to blob storage.");
                }

                // rewind stream
                stream.Seek(0L, SeekOrigin.Begin);
            }
            return response;
        }

        private bool IsApplicableTo(HttpRequestMessage request) =>
            request.Method == HttpMethod.Get &&
            request.Headers.Range == null &&
            Options.Filter(request)
        ;

        private static bool IsApplicableTo(HttpResponseMessage response) =>
            response.StatusCode is HttpStatusCode.OK &&
            response.Headers.CacheControl switch
            {
                CacheControlHeaderValue cacheControlHeader => !cacheControlHeader.NoCache,
                null => true,
            };
    }
}
