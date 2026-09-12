using System.Text.Json;

namespace CareCare;

internal static class PolicyState
{
    internal static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    internal static async Task Save(string path, Allowlist policy, CancellationToken token)
    {
        var temporary = path + ".tmp";
        await using (var stream = new FileStream(temporary, FileMode.Create, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough))
        {
            await JsonSerializer.SerializeAsync(stream, policy, Json, token);
            stream.Flush(true);
        }
        File.Move(temporary, path, true);
    }

    internal static async Task<Allowlist> Load(string path, CancellationToken token)
    {
        if (!File.Exists(path)) return new(2, []);
        var text = await File.ReadAllTextAsync(path, token);
        var policy = DomainPolicy.Parse(text, allowLegacy: true);
        // Atomic replacement only after the complete collection has passed validation.
        await Save(path, policy, token);
        return policy;
    }
}
