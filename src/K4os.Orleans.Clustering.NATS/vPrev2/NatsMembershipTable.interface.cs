namespace K4os.Orleans.Clustering.NATS.vPrev2;

public partial class NatsMembershipTable: IMembershipTable
{
    public Task InitializeMembershipTable(bool tryInitTableVersion)
    {
        throw new NotImplementedException();
    }

    public Task DeleteMembershipTableEntries(string clusterId)
    {
        throw new NotImplementedException();
    }

    public Task CleanupDefunctSiloEntries(DateTimeOffset beforeDate)
    {
        throw new NotImplementedException();
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

    public async Task<bool> UpdateRow(MembershipEntry entry, string etag, TableVersion tableVersion)
    {
       
        await using (await AcquireTableLock())
        {
            var tableRevision = await CheckTableVersion(tableVersion);
            if (tableRevision is null) return false;

            var entryRevision = await CheckEntryVersion(entry, etag);
            if (entryRevision is null) return false;

            await UpdateTimestamp(entry);
            await UpdateEntry(entry, entryRevision);
            await UpdateTableVersion(tableVersion, tableRevision);
        }

        return true;
    }

    public Task UpdateIAmAlive(MembershipEntry entry) => 
        UpdateTimestamp(entry);
}
