using NATS.Client.Core;

namespace K4os.Orleans.Clustering.NATS;

public partial class NatsMembershipTable: IMembershipTable
{
    public async Task InitializeMembershipTable(bool tryInitTableVersion)
    {
        _store = await InitializeStore();
        
        if (tryInitTableVersion)        
        {
            await TryInitializeTableVersion();            
        }
    }
    
    public async Task DeleteMembershipTableEntries(string clusterId)
    {
        await RemoveStore(clusterId);
    }
    
    public async Task CleanupDefunctSiloEntries(DateTimeOffset beforeDate)
    {
        await UpdateRetry(() => SanitizeMembershipTable(beforeDate));
    }
    
    public Task<MembershipTableData> ReadRow(SiloAddress key)
    {
        throw new NotImplementedException();
    }
    
    public Task<MembershipTableData> ReadAll()
    {
        throw new NotImplementedException();
    }
    
    public Task<bool> InsertRow(MembershipEntry entry, TableVersion tableVersion)
    {
        throw new NotImplementedException();
    }
    
    public Task<bool> UpdateRow(MembershipEntry entry, string etag, TableVersion tableVersion)
    {
        throw new NotImplementedException();
    }
    
    public Task UpdateIAmAlive(MembershipEntry entry)
    {
        throw new NotImplementedException();
    }
}
