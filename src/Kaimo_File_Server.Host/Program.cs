using Kaimo_File_Server.Infrastructure;
using Kaimo_File_Server.Infrastructure.Configuration;
using Kaimo_File_Server.Infrastructure.Logging;
using Kaimo_File_Server.Search;
using Kaimo_File_Server.Smb;
using Microsoft.Extensions.DependencyInjection;

// The SMB library processes every non-READ/WRITE command (CREATE, SET_INFO, CLOSE, ...)
// strictly sequentially per connection, and its handle-teardown path disposes open handles
// through the synchronous IDisposable.Dispose. In our bridge that crosses the async
// IFileService via SmbSync.Run (Task.Run(...).GetResult()), which parks one thread-pool
// thread while a second runs the work. A burst — deleting many files, or a client
// disconnect that disposes many still-open DELETE_ON_CLOSE handles — can therefore drain
// the pool and stall for seconds, because the pool only grows ~1-2 threads/second past its
// minimum. Raise the floor (default = CPU core count) so those bursts have threads at once.
{
    int desiredMin = Math.Max(Environment.ProcessorCount * 4, 64);
    ThreadPool.GetMinThreads(out int workerMin, out int completionMin);
    if (workerMin < desiredMin)
        ThreadPool.SetMinThreads(desiredMin, completionMin);
}

var builder = Host.CreateApplicationBuilder(args);

// -- Infrastructure (DB + Repositories + AuthenticationLookup) --
builder.Services.AddInfrastructure(builder.Configuration);

// -- Global, live-reloadable log level (shared with the Web UI via the DB) --
builder.AddDynamicLogLevel();

// -- Elastic Search --
//    Must be registered BEFORE AddCoreServices so the real search service wins
//    over the NoOp fallback (AddCoreServices uses TryAddSingleton). Without this
//    the Host runs on NoOpSearchService and files written via SMB are never
//    indexed -> search never returns hits for SMB-served files.
builder.Services.AddElasticSearch(builder.Configuration);

// -- Core Services (FileService, AclService, StorageEngine) --
var storagePath = builder.Configuration.GetValue<string>("Storage:RootPath") ?? "/data/storage";
builder.Services.AddCoreServices(storagePath);

// -- Config store (desired-state flags for data services, shared with the Web UI) --
builder.Services.AddMemoryCache();
builder.Services.AddScoped<IConfigRepository, ConfigRepository>();

// -- SMB Transport (registered as an IManagedDataService) --
builder.Services.AddSmb(builder.Configuration);

// -- Reconciler: drives Start/Stop of all managed data services from config flags --
builder.Services.AddHostedService<Kaimo_File_Server.Host.DataServiceReconciler>();


var host = builder.Build();
await host.InitializeDatabaseAsync();

// Ensure the index exists with the correct (n-gram) mapping before the SMB
// server starts writing. Otherwise the first indexed document would let ES
// auto-create the index with a default mapping, breaking partial-word search.
var search = host.Services.GetRequiredService<ISearchService>();
await search.InitializeAsync();

host.Run();
