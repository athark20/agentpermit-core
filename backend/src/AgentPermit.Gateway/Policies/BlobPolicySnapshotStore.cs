using System.Text.Json;
using Azure;
using Azure.Identity;
using Azure.Storage.Blobs;
using Azure.Storage.Blobs.Models;

namespace AgentPermit.Gateway.Policies;

/// <summary>
/// Durable published-snapshot store backed by Azure Blob Storage (managed identity). Published
/// policy survives container restarts: each version is written to versions/{version}.json and a
/// current.json pointer. This is the production store; the file store is for local/dev.
/// </summary>
public sealed class BlobPolicySnapshotStore : IPolicySnapshotStore
{
    private readonly BlobContainerClient _container;
    private static readonly JsonSerializerOptions Indented = new() { WriteIndented = true };

    public BlobPolicySnapshotStore(string containerUri)
    {
        _container = new BlobContainerClient(new Uri(containerUri), new DefaultAzureCredential());
        try { _container.CreateIfNotExists(); } catch { /* container may already exist / no create rights */ }
    }

    public PolicySnapshot? LoadCurrent()
    {
        var blob = _container.GetBlobClient("current.json");
        try
        {
            if (!blob.Exists()) return null;
            var content = blob.DownloadContent().Value.Content.ToString();
            return JsonSerializer.Deserialize<PolicySnapshot>(content);
        }
        catch (RequestFailedException) { return null; }
    }

    public void SaveCurrent(PolicySnapshot snapshot)
    {
        var data = BinaryData.FromString(JsonSerializer.Serialize(snapshot, Indented));
        var safeVersion = string.Concat(snapshot.Version.Split(Path.GetInvalidFileNameChars()));
        _container.GetBlobClient($"versions/{safeVersion}.json").Upload(data, overwrite: true);
        _container.GetBlobClient("current.json").Upload(data, overwrite: true);
    }

    public IReadOnlyList<string> ListVersions()
    {
        var versions = new List<string>();
        try
        {
            foreach (var b in _container.GetBlobs(BlobTraits.None, BlobStates.None, "versions/", CancellationToken.None))
                versions.Add(Path.GetFileNameWithoutExtension(b.Name));
        }
        catch (RequestFailedException) { /* no container yet */ }
        return versions.OrderBy(v => v).ToList();
    }
}
