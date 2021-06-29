using System;
using System.Net.Http;

namespace Framework.Server
{
    public class HttpBlobSnapshotOptions
    {
        public string ContainerName { get; set; } = "http-snapshots";

        public Predicate<HttpRequestMessage> Filter { get; set; } = _ => true;

        public Func<HttpRequestMessage, HttpBlobSnapshotOptions, string> KeySelector { get; set; } = (request, options) =>
            request.RequestUri.Host + request.RequestUri.PathAndQuery;
    }
}
