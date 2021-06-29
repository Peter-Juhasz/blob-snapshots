# HTTP Client Blob Snapshot

## Introduction
A few example use cases:
 - bring remote data closer to a local storage to reduce latency
 - recover from remote service failures and fallback to a cached snapshot of data
 - reduce usage of a remote service to avoid rate limiting
 - reduce billing cost of paid service (e.g.: resolving municipalities from postal codes)

## Solution

### Intercepting HTTP calls
You can intercept all HTTP calls using a [DelegatingHandler](https://docs.microsoft.com/en-us/dotnet/api/system.net.http.delegatinghandler):

```cs
protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
{
	return await base.SendAsync(request, cancellationToken);
}
```

In this method, you are free to do whatever you want to generate a [HttpResponseMessage](https://docs.microsoft.com/en-us/dotnet/api/system.net.http.httpresponsemessage) based on a [HttpRequestMessage](https://docs.microsoft.com/en-us/dotnet/api/system.net.http.httprequestmessage). You can also use this concept for testing, to mock remote services and have control over the responses returned in different test cases.

To use this `DelegatingHandler`, you have to register it to a specific `HttpClient`:

```cs
services.AddHttpClient(nameof(MyClient))
	.AddHttpMessageHandler<MyDelegatingHandler>()
```

You can chain as many handlers after each other as you like.

### Saving responses to Blob Storage
First, we need to determine which requests and responses to save. Let's use a simple approach for that:

```cs
static bool IsApplicable(HttpRequestMessage request) =>
    request.Method == HttpMethod.Get &&
    request.Headers.Range == null;

static bool IsApplicable(HttpResponseMessage response) =>
    response.StatusCode is HttpStatusCode.OK &&
    response.Headers.CacheControl switch
    {
        CacheControlHeaderValue cacheControlHeader => !cacheControlHeader.NoCache,
        null => true,
    };
```

Then implement saving:

```cs
// check whether this request is applicable
if (!IsApplicableTo(request))
{
    return await base.SendAsync(request, cancellationToken);
}

// execute request
var response = await base.SendAsync(request, cancellationToken);

// save
if (IsApplicable(response))
{
    // buffer content
    await response.Content.LoadIntoBufferAsync();

    // read
    var stream = await response.Content.ReadAsStreamAsync(cancellationToken);

    // upload
    var key = uri.Host + uri.PathAndQuery;
    var blob = ContainerClient.GetBlockBlobClient(key);
    var options = new BlobUploadOptions
    {
        HttpHeaders = new BlobHttpHeaders
        {
            ContentType = response.Content.Headers.ContentType?.ToString(),
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
```

### Load content
Now that we have saved responses on Blob Storage, we can try to serve responses from there:

```cs
// check whether this request is applicable
if (!IsApplicableTo(request))
{
    return await base.SendAsync(request, cancellationToken);
}

// find blob
var key = uri.Host + uri.PathAndQuery;
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
{
    // no snapshot found is expected
}
catch (Exception ex)
{
    Logger.LogWarning(ex, "Couldn't access HTTP blob snapshot storage.");
}
```


## Appendix

### Ignore specific calls
We used a simple static rule earlier to decided what requests are applicable to snapshot:
```cs
static bool IsApplicable(HttpRequestMessage request) =>
    request.Method == HttpMethod.Get &&
    request.Headers.Range == null;
```

We could add a user-defined filter to our configuration options:
```cs
public Func<HttpRequestMessage, bool> RequestFilter { get; set; }
```

So we could take this filter into account as well when determining whether a request is applicable to snapshot or not:
```cs
bool IsApplicable(HttpRequestMessage request) =>
    request.Method == HttpMethod.Get &&
    request.Headers.Range == null &&
    Options.RequestFilter(request);
```

And then we are able to define our specific filtering logic:
```cs
new HttpSnapshotOptions 
{
    RequestFilter = request => !request.RequestUri.AbsolutePath.StartsWith("/weather")
}
```

### Ignore specific parts of the URI
We used the following formula to generate blob names:
```cs
var key = uri.Host + uri.PathAndQuery;
```

But in some cases the path or query part of the URI may contain either values we would like to ignore:
 - a tenant ID `/tenants/123/api`
 - a pre-shared subscription key `/api?subscription-key=123`
 - an access token `/api?jwt=123`
 - current date or time `/api?time=2020-06-04`
 - an operation or correlation ID `/api?correlationId=123`
 - ...

So we could add a user-definable key selection / uniqueness function to the configuration:
```cs
public Func<HttpRequestMessage, string> KeySelector { get; set; }
```

And use this to determine the blob name for a request:
```cs
var key = Options.KeySelector(request);
```

### Time-based expiration
In some cases the remote service responses may be static/immutable and never change, while in most cases they may expire after some time.

But now that we have better control over cache keys, we could append time as well:
```cs
var expiration = TimeSpan.FromHours(1);
var currenTimeSlot = DateTimeOffset.Now.Floor(expiration);
var key = String.Join('/', request.RequestUri.Host, request.RequestUri.PathAndQuery, currentTimeSlot);
```

Even though we include time in cache keys, we should be able to delete expired contents. To do that, we can configure a [Lifecycle management rule](https://docs.microsoft.com/en-us/azure/storage/blobs/storage-lifecycle-management-concepts) in Azure Blob Storage, to automatically delete blobs older than X days. But if we have this rule in place already, we can even forget about appending time slots to cache keys, because Azure is going to delete old blobs either way. So when a blob is expired, it is not going to be there anymore, so our logic is going to get a fresh version from the remote service and store it as a new blob with the same name.

### Cache control
The remote service may send specific caching instructions for each response, where we can't have preset lifecycle management rules which are applied to all blobs in general.

As a first step, we should save them:
```cs
var options = new BlobUploadOptions
{
    HttpHeaders = new BlobHttpHeaders
    {
        ContentType = response.Content.Headers.ContentType?.ToString(),
        CacheControl = response.Content.Headers.CacheControl?.ToString(),
    },
    AccessTier = AccessTier.Hot,
};
```

In a next step, we could expire specific snapshots upon reading:
```cs
var blobResponse = await blob.DownloadAsync(cancellationToken);
if (CacheControlHeaderValue.TryParse(blobResponse.Details.CacheControl, out var cacheControl))
{
    if (DateTimeOffset.Now - blobResponse.Details.LastModified > cacheControl.MaxAge)
    {
        // invalidate cache
        await blob.DeleteIfExistsAsync(conditions: new BlobRequestConditions()
        { 
            IfMatch = blobResponse.Details.ETag // for race condition
        });

        // TODO: replay request
    }
}
```

### Tiering and performance
In general, the `Hot` tier is recommended for frequently access data, this is why we used that tier in the example.

But, if your use case is routed in performance, you can have a few extra options to speed up and get even lower latencies:
 - as this concept uses only Blobs, you can use a [Premium tier Blob Storage](https://azure.microsoft.com/en-us/blog/azure-premium-block-blob-storage-is-now-generally-available/) with SSDs, instead of a General Purpose account
 - if your service is deployed to multiple regions, you can have either a CDN in front of the Blob Storage to add another layer of cache, or use [Object Replication](https://docs.microsoft.com/en-us/azure/storage/blobs/object-replication-overview) to replicate blobs to multiple regions

### Use another storage mechanism
This implementation is specific to Azure Blob Storage, but the storage logic could be easily extracted from the main logic into something else:

```cs
public interface IHttpSnapshotStorage
{
    Task StoreAsync(string key, HttpResponseMessage response, CancellationToken cancellationToken);

    Task<HttpResponseMessage?> LoadAsync(string key, CancellationToken cancellationToken);
}
```
