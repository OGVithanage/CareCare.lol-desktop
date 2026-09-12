using CareCare;

var normalized = DomainPolicy.Normalize(["https://Example.com/a", "http://example.com:8080", "bücher.de", "https://example.com."]);
if (!normalized.SequenceEqual(new[] { "example.com", "xn--bcher-kva.de" })) throw new Exception("Normalization or deduplication failed.");
foreach (var invalid in new[] { "", "localhost", "127.0.0.1", "[::1]", "*.example.com", "https://user:pass@example.com", "file:///test", "-bad.example", "a_b.example", new string('a', 64) + ".com" })
{
    try { DomainPolicy.Normalize([invalid]); }
    catch (ArgumentException) { continue; }
    throw new Exception($"Accepted invalid input: {invalid}");
}
try { DomainPolicy.Normalize(Enumerable.Repeat("example.com", 501).ToArray()); throw new Exception("Limit was not enforced."); }
catch (ArgumentException) { }
if (DomainPolicy.Normalize([]).Length != 0) throw new Exception("Empty list rejected.");
Console.WriteLine("Domain policy checks passed.");

foreach (var scheme in new[] { "", "http://", "https://" })
foreach (var host in new[] { "google.com", "www.google.com", "WWW.Google.COM." })
{
    var expanded = DomainPolicy.Expand(DomainPolicy.Normalize([scheme + host]));
    if (!expanded.SequenceEqual(new[] { "google.com", "www.google.com" }))
        throw new Exception($"Main-site aliases failed: {scheme}{host}");
}
if (!DomainPolicy.Expand(DomainPolicy.Normalize(["mail.google.com"]))
    .SequenceEqual(new[] { "mail.google.com", "www.mail.google.com" }))
    throw new Exception("An explicit subdomain must not allow its parent or siblings.");
var fullList = DomainPolicy.Normalize(Enumerable.Range(0, 500).Select(i => $"site{i}.example.com").ToArray());
if (DomainPolicy.Expand(fullList).Length != 1000 || DomainPolicy.Normalize(fullList).Length != 500)
    throw new Exception("Alias expansion broke the user-rule limit or restart normalization.");
Console.WriteLine("Domain alias checks passed.");
