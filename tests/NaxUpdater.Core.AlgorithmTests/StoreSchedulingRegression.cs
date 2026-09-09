using NaxUpdater.Core.Models;
using NaxUpdater.Core.Services;
using Windows.ApplicationModel.Store.Preview.InstallControl;

internal static class StoreSchedulingRegression
{
    public static async Task RunAsync(Action<bool, string> assert)
    {
        var provider=new Probe();
        var apps=Enumerable.Range(0,40).Select(i=>new InstalledApplication("msix:Fixture"+i,"Fixture"+i,"Fixture","1.0","1.0","fixture",null,"fixture",null,null,
            InstallScope.CurrentUser,ManagementMode.Msix,ConfidenceLevel.High,false,[],null,[])).ToArray();
        var checkedApps=await new UpdateCheckService([provider],providerTimeout:TimeSpan.FromMilliseconds(250),sourceCheckTimeout:TimeSpan.FromMilliseconds(200))
            .CheckAsync(new(DateTimeOffset.UtcNow,apps,[],[]));
        assert(provider.Maximum<=8 && provider.Maximum>1,"Store admission exceeded its concurrency limit or unnecessarily serialized every app.");
        assert(checkedApps.FailedCheckCount==0 && checkedApps.CheckedVersionCount==40,
            "Waiting for Store admission consumed the per-app execution timeout.");
        var reads=0;
        var client=new NativeStoreUpdateClient(()=>{Interlocked.Increment(ref reads);Thread.Sleep(30);return [new Item()];});
        var entries=await Task.WhenAll(Enumerable.Range(0,8).Select(_=>client.ReadQueueAsync("Fixture_family",CancellationToken.None)));
        assert(reads==1 && entries.All(e=>e?.State==AppInstallState.ReadyToDownload),"Concurrent Store checks did not share one queue snapshot.");
        await client.GetQueueItemAsync("Fixture_family",CancellationToken.None);
        assert(reads==2,"Execution preparation reused a cached queue membership snapshot.");
        using var gate=new ManualResetEventSlim();
        var blockedReads=0;
        var blocked=new NativeStoreUpdateClient(()=>{Interlocked.Increment(ref blockedReads);gate.Wait();return [];},TimeSpan.FromMilliseconds(40));
        try
        {
            for(var i=0;i<3;i++)
            {
                try{await blocked.ReadQueueAsync("Fixture_family",CancellationToken.None);assert(false,"An unresponsive queue was reported empty/current.");}
                catch(TimeoutException){assert(true,"Shared queue timeout reported.");}
            }
            assert(blockedReads==1,"An unresponsive queue caused repeated broker calls for each app.");
        }
        finally{gate.Set();}
        using var canceled=new CancellationTokenSource(); canceled.Cancel();
        try{await client.ReadQueueAsync("Fixture_family",canceled.Token);assert(false,"Canceled queue read continued.");}
        catch(OperationCanceledException){assert(reads==2,"Cancellation started another queue request.");}
        var architectureApp=apps[0] with {DisplayName="Microsoft Windows Desktop Runtime - 9.0.19 (x64)",PrimaryInstallPath=Environment.ProcessPath};
        assert(InstalledApplicationMetadata.Architecture(architectureApp)=="x64","Runtime architecture ignored the installed product's explicit architecture.");
    }
    private sealed class Item:INativeStoreUpdateItem
    {
        public string ProductId=>"Fixture.Product";
        public string PackageFamilyName=>"Fixture_family";
        public NativeStoreItemState Status()=>new(AppInstallState.ReadyToDownload);
    }
    private sealed class Probe:IUpdateProvider
    {
        private int active;
        public int Maximum;
        public string Id=>"fixture-store";
        public UpdateProviderDescriptor Descriptor {get;}=new(UpdateProviderAuthority.PlatformStore,100,"fixture");
        public bool CanHandle(InstalledApplication app)=>true;
        public async Task<UpdateCheckResult> CheckAsync(InstalledApplication app,CancellationToken token)
        {
            var count=Interlocked.Increment(ref active);
            lock(this){Maximum=Math.Max(Maximum,count);}
            try
            {
                await Task.Delay(80,token);
                return new(app.Identity,app.DisplayName,"1.0","1.0",UpdateStatus.Current,Id,Id,"neutral","fixture","x64","stable",null,null,null);
            }
            finally{Interlocked.Decrement(ref active);}
        }
    }
}
