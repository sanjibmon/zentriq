using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using Zentriq.Infrastructure.Backup;
using Zentriq.Infrastructure.Database;
using Zentriq.Infrastructure.Secrets;

namespace Zentriq.Tests;

public sealed class BackupCryptoTests
{
    private static readonly string InstallKey = Convert.ToBase64String(Enumerable.Range(1, 32).Select(i => (byte)i).ToArray());
    private static readonly string OtherKey = Convert.ToBase64String(Enumerable.Range(100, 32).Select(i => (byte)i).ToArray());

    private static async Task<byte[]> EncryptAsync(byte[] plain, BackupPasswordKey? pw)
    {
        var (header, dataKey) = BackupCrypto.NewHeader("1.1.1", "manual", InstallKey, pw, "SRV1", Guid.NewGuid().ToString(), "database, configuration");
        using var input = new MemoryStream(plain);
        using var output = new MemoryStream();
        await BackupCrypto.EncryptAsync(input, output, dataKey, header, default);
        return output.ToArray();
    }

    private static async Task<byte[]> DecryptAsync(byte[] file, BackupUnlock unlock)
    {
        using var input = new MemoryStream(file);
        using var output = new MemoryStream();
        await BackupCrypto.DecryptAsync(input, output, h => BackupCrypto.Unlock(h, unlock), default);
        return output.ToArray();
    }

    [Fact]
    public async Task Opens_with_the_installation_key_or_the_password_only()
    {
        var plain = RandomNumberGenerator.GetBytes(3 * BackupCrypto.ChunkSize + 1234);
        var pw = BackupCrypto.DerivePasswordKey("correct horse battery", iterations: 10_000);
        var file = await EncryptAsync(plain, pw);

        Assert.Equal(plain, await DecryptAsync(file, new BackupUnlock([InstallKey])));
        Assert.Equal(plain, await DecryptAsync(file, new BackupUnlock(Password: "correct horse battery")));
        Assert.Equal(plain, await DecryptAsync(file, new BackupUnlock([OtherKey], "correct horse battery")));
        var wrong = await Assert.ThrowsAsync<BackupKeyException>(() => DecryptAsync(file, new BackupUnlock(Password: "wrong horse battery")));
        Assert.Contains("password is wrong", wrong.Message);
        var none = await Assert.ThrowsAsync<BackupKeyException>(() => DecryptAsync(file, new BackupUnlock([OtherKey])));
        Assert.Contains("Enter the backup password", none.Message);

        // The header is readable without a key and says how the backup can be opened; it holds no key material in clear.
        var header = BackupCrypto.ReadHeader(new MemoryStream(file));
        Assert.Equal(2, header.Format);
        Assert.True(header.HasPassword);
        Assert.Equal(BackupCrypto.Fingerprint(InstallKey), header.KeyFingerprint);
        Assert.Equal("database, configuration", header.Contents);
        var headerText = Encoding.UTF8.GetString(file, 0, Array.IndexOf(file, (byte)'\n', 6));
        Assert.DoesNotContain(InstallKey, headerText);
        Assert.DoesNotContain(Convert.ToBase64String(pw.Key), headerText);
    }

    [Fact]
    public async Task Without_a_password_only_the_installation_key_opens_it()
    {
        var plain = Encoding.UTF8.GetBytes("zentriq backup");
        var file = await EncryptAsync(plain, null);
        Assert.False(BackupCrypto.ReadHeader(new MemoryStream(file)).HasPassword);
        Assert.Equal(plain, await DecryptAsync(file, new BackupUnlock([OtherKey, InstallKey])));
        var ex = await Assert.ThrowsAsync<BackupKeyException>(() => DecryptAsync(file, new BackupUnlock(Password: "anything at all")));
        Assert.Contains("has no password", ex.Message);
    }

    [Fact]
    public async Task Tampering_is_detected()
    {
        var file = await EncryptAsync(RandomNumberGenerator.GetBytes(5000), null);
        // A changed header (associated data of every chunk).
        var h = (byte[])file.Clone();
        var at = Encoding.UTF8.GetString(h).IndexOf("\"manual\"", StringComparison.Ordinal);
        h[at + 1] = (byte)'M';
        await Assert.ThrowsAsync<InvalidDataException>(() => DecryptAsync(h, new BackupUnlock([InstallKey])));
        // A changed byte of the encrypted content.
        var c = (byte[])file.Clone();
        c[^10] ^= 0x55;
        await Assert.ThrowsAsync<InvalidDataException>(() => DecryptAsync(c, new BackupUnlock([InstallKey])));
        // Truncation.
        await Assert.ThrowsAsync<InvalidDataException>(() => DecryptAsync(file[..^20], new BackupUnlock([InstallKey])));
    }

    [Fact]
    public async Task Format_1_backups_of_earlier_builds_still_open()
    {
        var header = new BackupHeader(1, "1.0.0", DateTimeOffset.UtcNow, "nightly", BackupCrypto.Fingerprint(InstallKey), Convert.ToBase64String(RandomNumberGenerator.GetBytes(4)), "OLD");
        var plain = Encoding.UTF8.GetBytes("old style");
        using var output = new MemoryStream();
        await BackupCrypto.EncryptAsync(new MemoryStream(plain), output, Convert.FromBase64String(InstallKey), header, default);
        Assert.Equal(plain, await DecryptAsync(output.ToArray(), new BackupUnlock([InstallKey])));
        var ex = await Assert.ThrowsAsync<BackupKeyException>(() => DecryptAsync(output.ToArray(), new BackupUnlock([OtherKey], "a password here")));
        Assert.Contains("recovery key", ex.Message);
        Assert.Null(BackupCrypto.ReadHeader(new MemoryStream(output.ToArray())).Keys);
    }

    [Fact]
    public void Password_rules_and_recovery_key_text()
    {
        Assert.NotNull(BackupCrypto.CheckPassword("short"));
        Assert.Null(BackupCrypto.CheckPassword("long enough pass"));
        Assert.Equal(InstallKey, BackupCrypto.ParseRecoveryKey($"Zentriq backup recovery key\r\nKey fingerprint: X\r\nKey: {InstallKey}\r\n"));
        Assert.Equal(InstallKey, BackupCrypto.ParseRecoveryKey(InstallKey));
        Assert.Null(BackupCrypto.ParseRecoveryKey("not a key"));
        Assert.Throws<InvalidDataException>(() => BackupCrypto.ReadHeader(new MemoryStream(Encoding.UTF8.GetBytes("PK\u0003\u0004 not a backup"))));
    }
}

public sealed class BackupScheduleTests
{
    private static readonly TimeZoneInfo Utc = TimeZoneInfo.Utc;

    [Fact]
    public void Daily_weekly_and_monthly_slots()
    {
        var daily = new BackupSchedule { Frequency = "daily", Time = "01:30" };
        var now = new DateTimeOffset(2026, 10, 25, 9, 0, 0, TimeSpan.Zero);   // a Sunday
        Assert.Equal(new DateTimeOffset(2026, 10, 25, 1, 30, 0, TimeSpan.Zero), daily.LastSlot(now, Utc));
        Assert.Equal(new DateTimeOffset(2026, 10, 26, 1, 30, 0, TimeSpan.Zero), daily.NextRun(now, Utc));
        Assert.Equal("Daily at 01:30", daily.Describe());

        var weekly = new BackupSchedule { Frequency = "weekly", Time = "22:00", Weekdays = [1, 3, 5] };
        Assert.Equal(new DateTimeOffset(2026, 10, 23, 22, 0, 0, TimeSpan.Zero), weekly.LastSlot(now, Utc));     // Friday
        Assert.Equal(new DateTimeOffset(2026, 10, 26, 22, 0, 0, TimeSpan.Zero), weekly.NextRun(now, Utc));     // Monday
        Assert.Equal("Every Monday, Wednesday, Friday at 22:00", weekly.Describe());

        var monthly = new BackupSchedule { Frequency = "monthly", Time = "03:00", Day = 28 };
        Assert.Equal(new DateTimeOffset(2026, 9, 28, 3, 0, 0, TimeSpan.Zero), monthly.LastSlot(now, Utc));
        Assert.Equal(new DateTimeOffset(2026, 10, 28, 3, 0, 0, TimeSpan.Zero), monthly.NextRun(now, Utc));

        Assert.Null(new BackupSchedule { Enabled = false }.NextRun(now, Utc));
        Assert.NotNull(new BackupSchedule { Time = "25:00" }.Validate());
        Assert.NotNull(new BackupSchedule { Frequency = "weekly", Weekdays = [] }.Validate());
        Assert.NotNull(new BackupSchedule { Frequency = "monthly", Day = 31 }.Validate());
    }

    [Fact]
    public void Due_once_per_slot_never_right_after_a_change_and_not_for_old_slots()
    {
        var s = new BackupSchedule { Frequency = "daily", Time = "01:30" };
        var slot = new DateTimeOffset(2026, 10, 25, 1, 30, 0, TimeSpan.Zero);
        Assert.False(s.IsDue(slot.AddMinutes(-1), slot.AddDays(-1).AddMinutes(2), null, Utc));       // before today's slot
        Assert.True(s.IsDue(slot.AddMinutes(3), slot.AddDays(-1).AddMinutes(2), null, Utc));          // yesterday's ran
        Assert.False(s.IsDue(slot.AddMinutes(8), slot.AddMinutes(3), null, Utc));                     // already ran for this slot
        Assert.False(s.IsDue(slot.AddHours(3), null, slot.AddHours(2), Utc));                         // schedule saved after the slot
        Assert.True(s.IsDue(slot.AddHours(3), null, slot.AddHours(-2), Utc));                         // saved before: catch up
        Assert.True(s.IsDue(slot.AddHours(20), slot.AddDays(-3), null, Utc));                         // server was off at 01:30
        Assert.False(new BackupSchedule { Enabled = false }.IsDue(slot.AddMinutes(3), null, null, Utc));
    }
}

public sealed class BackupNameTests
{
    [Fact]
    public void Names_parse_and_retention_keeps_the_newest_of_this_server()
    {
        var t0 = new DateTime(2026, 10, 1, 1, 30, 0, DateTimeKind.Utc);
        Assert.Equal("zentriq-srv-01-20261001-013000-scheduled.zbak", BackupNames.Make("SRV-01", t0, "scheduled"));
        Assert.Equal(("srv-01", t0, "scheduled"), BackupNames.Parse("zentriq-srv-01-20261001-013000-scheduled.zbak")!.Value);
        Assert.Equal((null, t0, "nightly"), BackupNames.Parse("zentriq-20261001-013000-nightly.zbak")!.Value);   // earlier builds
        Assert.Null(BackupNames.Parse("zentriq-write-test-1.tmp"));

        var names = new List<string>();
        for (var i = 0; i < 6; i++) names.Add(BackupNames.Make("SRV", t0.AddDays(i), i % 2 == 0 ? "scheduled" : "manual"));
        names.Add("zentriq-20260901-013000-nightly.zbak");                        // older build, same server
        for (var i = 0; i < 7; i++) names.Add(BackupNames.Make("SRV", t0.AddHours(i), "pre-upgrade"));
        names.Add(BackupNames.Make("OTHER", t0.AddDays(-30), "scheduled"));       // another server sharing the destination
        names.Add(BackupNames.Make("SRV", t0.AddDays(-60), "before-migration"));  // made by hand: never removed

        var delete = BackupNames.ToDelete(names, "srv", 3);
        Assert.Equal(4 + 2, delete.Count);                                         // 7 rotated → 3, 7 pre-upgrade → 5
        Assert.Contains("zentriq-20260901-013000-nightly.zbak", delete);
        Assert.DoesNotContain(names.Single(n => n.Contains("-other-")), delete);
        Assert.DoesNotContain(names.Single(n => n.Contains("before-migration")), delete);
        Assert.DoesNotContain(BackupNames.Make("SRV", t0.AddDays(5), "manual"), delete);
        Assert.Equal(2, BackupNames.ToDelete(names, "srv", 0).Count);              // keep all rotated; safety still pruned
    }
}

public sealed class BackupTargetTests
{
    private sealed class Fake(Func<HttpRequestMessage, byte[]?, HttpResponseMessage> respond) : HttpMessageHandler
    {
        public List<(HttpMethod Method, string Url, Dictionary<string, string> Headers, byte[]? Body)> Calls { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var body = request.Content is null ? null : await request.Content.ReadAsByteArrayAsync(ct);
            var headers = request.Headers.Concat(request.Content?.Headers ?? Enumerable.Empty<KeyValuePair<string, IEnumerable<string>>>())
                .ToDictionary(h => h.Key.ToLowerInvariant(), h => string.Join(",", h.Value));
            Calls.Add((request.Method, request.RequestUri!.AbsoluteUri, headers, body));
            return respond(request, body);
        }
    }

    private static HttpResponseMessage Ok(string body = "", HttpStatusCode code = HttpStatusCode.OK) => new(code) { Content = new StringContent(body) };

    private static string TempFile(int size, out byte[] data)
    {
        data = RandomNumberGenerator.GetBytes(size);
        var path = Path.Combine(Path.GetTempPath(), $"zq-target-{Guid.NewGuid():N}.zbak");
        File.WriteAllBytes(path, data);
        return path;
    }

    [Fact]
    public async Task Folder_target_writes_lists_reads_and_deletes()
    {
        var dir = Path.Combine(Path.GetTempPath(), "zq-dest-" + Guid.NewGuid().ToString("N"), "nested");
        var file = TempFile(4096, out var data);
        try
        {
            using var t = new FolderTarget(dir);
            var test = await BackupTargets.TestAsync(t, default);
            Assert.Contains("0 backup(s)", test.Message);
            await t.UploadAsync(file, "zentriq-srv-20261001-013000-manual.zbak", default);
            var list = await t.ListAsync(default);
            Assert.Equal(4096, Assert.Single(list).Size);
            Assert.False(File.Exists(Path.Combine(dir, "zentriq-srv-20261001-013000-manual.zbak.partial")));
            var back = Path.Combine(Path.GetTempPath(), $"zq-back-{Guid.NewGuid():N}");
            await t.DownloadAsync(list[0].Name, back, default);
            Assert.Equal(data, File.ReadAllBytes(back));
            File.Delete(back);
            await t.DeleteAsync(list[0].Name, default);
            Assert.Empty(await t.ListAsync(default));
        }
        finally { File.Delete(file); Directory.Delete(Path.GetDirectoryName(dir)!, true); }
    }

    [Fact]
    public async Task Azure_uploads_blocks_and_signs_with_the_account_key()
    {
        var accountKey = Convert.ToBase64String(RandomNumberGenerator.GetBytes(64));
        var fake = new Fake((req, _) => req.Method == HttpMethod.Get
            ? Ok("""<?xml version="1.0" encoding="utf-8"?><EnumerationResults><Blobs><Blob><Name>zq/zentriq-srv-20261001-013000-manual.zbak</Name><Properties><Last-Modified>Thu, 01 Oct 2026 01:31:00 GMT</Last-Modified><Content-Length>2500</Content-Length></Properties></Blob><Blob><Name>zq/other.txt</Name><Properties /></Blob></Blobs><NextMarker /></EnumerationResults>""")
            : Ok("", HttpStatusCode.Created));
        var s = new BackupTargetSettings { User = "zentriqstore", Bucket = "backups", Path = "zq" };
        using var t = new AzureBlobTarget(s, accountKey, new HttpClient(fake)) { Chunk = 1000 };
        var file = TempFile(2500, out var data);
        try { await t.UploadAsync(file, "zentriq-srv-20261001-013000-manual.zbak", default); }
        finally { File.Delete(file); }

        Assert.Equal(4, fake.Calls.Count);                                    // 3 blocks + block list
        Assert.All(fake.Calls.Take(3), c => Assert.Contains("comp=block&blockid=", c.Url));
        Assert.Equal(data, fake.Calls.Take(3).SelectMany(c => c.Body!).ToArray());
        var list = Encoding.UTF8.GetString(fake.Calls[3].Body!);
        Assert.Contains("comp=blocklist", fake.Calls[3].Url);
        Assert.Equal(3, list.Split("<Latest>").Length - 1);
        Assert.StartsWith("https://zentriqstore.blob.core.windows.net/backups/zq/zentriq-srv-20261001-013000-manual.zbak?", fake.Calls[0].Url);

        // The Shared Key signature of a request, recomputed from the documented string-to-sign.
        var c0 = fake.Calls[0];
        var blockId = Uri.UnescapeDataString(c0.Url.Split("blockid=")[1]);
        var toSign = $"PUT\n\n\n1000\n\n\n\n\n\n\n\n\nx-ms-date:{c0.Headers["x-ms-date"]}\nx-ms-version:{AzureBlobTarget.Version}\n" +
                     $"/zentriqstore/backups/zq/zentriq-srv-20261001-013000-manual.zbak\nblockid:{blockId}\ncomp:block";
        var expected = Convert.ToBase64String(HMACSHA256.HashData(Convert.FromBase64String(accountKey), Encoding.UTF8.GetBytes(toSign)));
        Assert.Equal($"SharedKey zentriqstore:{expected}", c0.Headers["authorization"]);

        var files = await t.ListAsync(default);
        Assert.Equal(2500, Assert.Single(files).Size);
        Assert.Contains("restype=container&comp=list&prefix=zq%2Fzentriq-", fake.Calls[^1].Url);

        // A SAS token goes in the query string instead of a signature.
        var sasFake = new Fake((_, _) => Ok("", HttpStatusCode.Accepted));
        using var sas = new AzureBlobTarget(new BackupTargetSettings { User = "zentriqstore", Bucket = "backups", AuthMode = "sas" }, "?sv=2022-11-02&sig=abc", new HttpClient(sasFake));
        await sas.DeleteAsync("zentriq-x.zbak", default);
        Assert.EndsWith("/backups/zentriq-x.zbak?sv=2022-11-02&sig=abc", sasFake.Calls[0].Url);
        Assert.False(sasFake.Calls[0].Headers.ContainsKey("authorization"));
    }

    [Fact]
    public async Task S3_single_and_multipart_uploads()
    {
        var fake = new Fake((req, _) =>
        {
            var u = req.RequestUri!.AbsoluteUri;
            if (req.Method == HttpMethod.Post && u.EndsWith("?uploads=")) return Ok("<InitiateMultipartUploadResult><UploadId>UP-1</UploadId></InitiateMultipartUploadResult>");
            if (req.Method == HttpMethod.Put && u.Contains("partNumber="))
            {
                var r = Ok();
                r.Headers.ETag = new System.Net.Http.Headers.EntityTagHeaderValue($"\"etag-{u.Split("partNumber=")[1].Split('&')[0]}\"");
                return r;
            }
            if (req.Method == HttpMethod.Get) return Ok("<ListBucketResult xmlns=\"http://s3.amazonaws.com/doc/2006-03-01/\"><IsTruncated>false</IsTruncated><Contents><Key>zq/zentriq-srv-20261001-013000-manual.zbak</Key><Size>25</Size><LastModified>2026-10-01T01:31:00.000Z</LastModified></Contents></ListBucketResult>");
            return Ok("<CompleteMultipartUploadResult/>");
        });
        var s = new BackupTargetSettings { Bucket = "zq-backups", Region = "ap-south-1", User = "AKIDEXAMPLE", Path = "zq" };
        var clock = () => new DateTime(2026, 10, 25, 10, 0, 0, DateTimeKind.Utc);
        using (var t = new S3Target(s, "wJalrXUtnFEMI/K7MDENG+bPxRfiCYEXAMPLEKEY", new HttpClient(fake), clock) { MultipartThreshold = 10, MinPart = 10 })
        {
            var small = TempFile(8, out _);
            var big = TempFile(25, out var data);
            try
            {
                await t.UploadAsync(small, "zentriq-small.zbak", default);
                await t.UploadAsync(big, "zentriq-big.zbak", default);
            }
            finally { File.Delete(small); File.Delete(big); }
            Assert.Equal("https://zq-backups.s3.ap-south-1.amazonaws.com/zq/zentriq-small.zbak", fake.Calls[0].Url);
            Assert.Equal("UNSIGNED-PAYLOAD", fake.Calls[0].Headers["x-amz-content-sha256"]);
            Assert.StartsWith("AWS4-HMAC-SHA256 Credential=AKIDEXAMPLE/20261025/ap-south-1/s3/aws4_request, SignedHeaders=host;x-amz-content-sha256;x-amz-date, Signature=", fake.Calls[0].Headers["authorization"]);
            Assert.EndsWith("?uploads=", fake.Calls[1].Url);
            var parts = fake.Calls.Where(c => c.Url.Contains("partNumber=")).ToList();
            Assert.Equal(3, parts.Count);
            Assert.Equal(data, parts.SelectMany(p => p.Body!).ToArray());
            var complete = Encoding.UTF8.GetString(fake.Calls[^1].Body!);
            Assert.Contains("<PartNumber>3</PartNumber><ETag>&quot;etag-3&quot;</ETag>", complete);
            Assert.EndsWith("?uploadId=UP-1", fake.Calls[^1].Url);
            var list = await t.ListAsync(default);
            Assert.Equal(25, Assert.Single(list).Size);
            Assert.Contains("list-type=2&prefix=zq%2Fzentriq-", fake.Calls[^1].Url);
        }
        // S3-compatible service with path-style URLs.
        var pf = new Fake((_, _) => Ok());
        using var p = new S3Target(new BackupTargetSettings { Bucket = "zq", User = "minio", Endpoint = "https://minio.local:9000", PathStyle = true }, "secret-key", new HttpClient(pf));
        await p.DeleteAsync("zentriq-x.zbak", default);
        Assert.Equal("https://minio.local:9000/zq/zentriq-x.zbak", pf.Calls[0].Url);
        Assert.Contains("/us-east-1/s3/aws4_request", pf.Calls[0].Headers["authorization"]);
    }

    [Fact]
    public async Task S3_errors_are_explained()
    {
        var fake = new Fake((_, _) => new HttpResponseMessage(HttpStatusCode.Forbidden) { Content = new StringContent("<Error><Code>SignatureDoesNotMatch</Code><Message>x</Message></Error>") });
        using var t = new S3Target(new BackupTargetSettings { Bucket = "zq-backups", Region = "eu-west-1", User = "AKID" }, "bad", new HttpClient(fake));
        var ex = await Assert.ThrowsAsync<BackupHttpException>(() => t.ListAsync(default));
        Assert.Contains("secret access key is wrong", ex.Message);
    }

    [Fact]
    public async Task Gcs_resumable_upload_in_pieces()
    {
        using var rsa = RSA.Create(2048);
        var keyJson = JsonSerializer.Serialize(new Dictionary<string, string>
        {
            ["type"] = "service_account", ["client_email"] = "backup@zq.iam.gserviceaccount.com", ["private_key"] = rsa.ExportPkcs8PrivateKeyPem(), ["project_id"] = "zq",
        });
        var fake = new Fake((req, _) =>
        {
            var u = req.RequestUri!.AbsoluteUri;
            if (u.StartsWith("https://token.test")) return Ok("""{"access_token":"tok","expires_in":3600}""");
            if (u.Contains("uploadType=resumable"))
            {
                var r = Ok();
                r.Headers.Location = new Uri("https://storage.test/upload/session-1");
                return r;
            }
            if (u.StartsWith("https://storage.test/upload/session-1"))
            {
                var range = req.Content!.Headers.ContentRange!;
                return range.To + 1 == range.Length ? Ok("{}") : new HttpResponseMessage((HttpStatusCode)308);
            }
            return Ok("""{"items":[{"name":"zq/zentriq-srv-20261001-013000-manual.zbak","size":"700","updated":"2026-10-01T01:31:00Z"}]}""");
        });
        using var t = new GcsTarget(new BackupTargetSettings { Bucket = "zq-backups", Path = "zq" }, keyJson, new HttpClient(fake), "https://storage.test", "https://token.test/token") { Chunk = 256 };
        var file = TempFile(700, out var data);
        try { await t.UploadAsync(file, "zentriq-srv-20261001-013000-manual.zbak", default); }
        finally { File.Delete(file); }
        Assert.Contains("name=zq%2Fzentriq-srv-20261001-013000-manual.zbak", fake.Calls[1].Url);
        Assert.Equal("Bearer tok", fake.Calls[1].Headers["authorization"]);
        var pieces = fake.Calls.Where(c => c.Url.Contains("session-1")).ToList();
        Assert.Equal(3, pieces.Count);
        Assert.Equal("bytes 512-699/700", pieces[2].Headers["content-range"]);
        Assert.Equal(data, pieces.SelectMany(p => p.Body!).ToArray());
        Assert.Equal(700, Assert.Single(await t.ListAsync(default)).Size);
    }

    [Fact]
    public void Destinations_are_validated()
    {
        static string? Field(BackupDestination d, bool stored = false) => BackupTargets.Validate(d, stored)?.Field;
        Assert.Equal("path", Field(new BackupDestination { Name = "NAS", Kind = BackupKinds.Smb, Settings = new() { Path = "nas/share" } }));
        Assert.Equal("password", Field(new BackupDestination { Name = "NAS", Kind = BackupKinds.Smb, Settings = new() { Path = @"\\nas\backups\zentriq", User = @"CORP\svc-backup" } }));
        Assert.Null(Field(new BackupDestination { Name = "NAS", Kind = BackupKinds.Smb, Settings = new() { Path = @"\\nas\backups\zentriq", User = @"CORP\svc-backup" } }, stored: true));
        Assert.Null(Field(new BackupDestination { Name = "NAS", Kind = BackupKinds.Smb, Settings = new() { Path = @"\\nas\backups" } }));      // computer account
        Assert.Equal("host", Field(new BackupDestination { Name = "FTP", Kind = BackupKinds.Ftp, Settings = new() { Host = "ftp://x" } }));
        Assert.Equal("user", Field(new BackupDestination { Name = "Az", Kind = BackupKinds.Azure, Settings = new() { User = "Bad_Name", Bucket = "backups" } }));
        Assert.Equal("region", Field(new BackupDestination { Name = "S3", Kind = BackupKinds.S3, Settings = new() { Bucket = "zq-backups", User = "AKID" }, Secret = new() { Password = "x" } }));
        Assert.Null(Field(new BackupDestination { Name = "Wasabi", Kind = BackupKinds.S3, Settings = new() { Bucket = "zq-backups", User = "AKID", Endpoint = "https://s3.wasabisys.com" }, Secret = new() { Password = "x" } }));
        Assert.Equal("privateKey", Field(new BackupDestination { Name = "GCS", Kind = BackupKinds.Gcs, Settings = new() { Bucket = "zq-backups" }, Secret = new() { PrivateKey = "{}" } }));
        Assert.Equal("keep", Field(new BackupDestination { Name = "x", Kind = BackupKinds.Folder, Keep = -1, Settings = new() { Path = Path.GetTempPath() } }));
        Assert.Null(Field(new BackupDestination { Name = "Disk", Kind = BackupKinds.Folder, Settings = new() { Path = Path.GetTempPath() } }));
    }
}

public sealed class DataProtectionKeyBackupTests
{
    /// <summary>The console's MFA secrets stay readable after the key ring is moved through a backup (Windows: DPAPI).</summary>
    [Fact]
    public void Key_ring_survives_a_backup_and_restore()
    {
        if (!OperatingSystem.IsWindows()) return;
        var a = Path.Combine(Path.GetTempPath(), "zq-dp-a-" + Guid.NewGuid().ToString("N"));
        var b = Path.Combine(Path.GetTempPath(), "zq-dp-b-" + Guid.NewGuid().ToString("N"));
        try
        {
            var first = DataProtectionProvider.Create(new DirectoryInfo(a), o => { o.SetApplicationName("Zentriq"); o.ProtectKeysWithDpapi(protectToLocalMachine: true); });
            var protectedSecret = first.CreateProtector("mfa").Protect("JBSWY3DPEHPK3PXP");
            Assert.Contains("DpapiXmlDecryptor", File.ReadAllText(Directory.GetFiles(a, "*.xml").Single()));

            var (files, portable, kept) = DataProtectionKeyBackup.Export(a);
            Assert.Equal(1, portable);
            Assert.Equal(0, kept);
            Assert.Contains("zentriqPortable", files[0].Xml);
            Assert.Equal(1, DataProtectionKeyBackup.Import(b, files));
            Assert.DoesNotContain("zentriqPortable", File.ReadAllText(Path.Combine(b, files[0].Name)));
            Assert.Equal(0, DataProtectionKeyBackup.Import(b, files));                       // already there

            var second = DataProtectionProvider.Create(new DirectoryInfo(b), o => { o.SetApplicationName("Zentriq"); o.ProtectKeysWithDpapi(protectToLocalMachine: true); });
            Assert.Equal("JBSWY3DPEHPK3PXP", second.CreateProtector("mfa").Unprotect(protectedSecret));
        }
        finally
        {
            foreach (var d in new[] { a, b }) if (Directory.Exists(d)) Directory.Delete(d, true);
        }
    }
}

public sealed class BackupStoreDatabaseTests : IAsyncLifetime
{
    private readonly TestDatabase _db = new();
    public Task InitializeAsync() => _db.InitializeAsync();
    public Task DisposeAsync() => _db.DisposeAsync();

    [DbFact]
    public async Task Settings_destinations_and_history()
    {
        await new SqlMigrator(NullLogger<SqlMigrator>.Instance).MigrateAsync(_db.OwnerConnection, _db.App, "1.0.0-test");
        await using var ds = NpgsqlDataSource.Create(_db.AppConnection);
        var secrets = new AppSecrets { CredentialKey = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)) };
        var store = new BackupStore(ds, new CredentialProtector(() => secrets));

        // Defaults carried over from the nightly backup task of earlier builds.
        var s = await store.LoadSettingsAsync(default);
        Assert.Equal("01:30", s.Schedule.Time);
        Assert.Equal(14, s.KeepLocal);
        Assert.NotNull(s.ScheduleChangedAt);
        Assert.Null(store.PasswordKey(s));

        var pw = BackupCrypto.DerivePasswordKey("a long backup pass", iterations: 10_000);
        s.PasswordSalt = pw.Salt; s.PasswordIterations = pw.Iterations; s.PasswordKey = store.ProtectPasswordKey(pw.Key);
        await store.SaveSettingsAsync(s, null, default);
        var again = await store.LoadSettingsAsync(default);
        Assert.Equal(pw.Key, store.PasswordKey(again)!.Key);
        await using (var c = ds.CreateCommand("SELECT value::text FROM zentriq.system_settings WHERE key = 'backup.settings'"))
            Assert.DoesNotContain(Convert.ToBase64String(pw.Key), (string)(await c.ExecuteScalarAsync())!);

        var d = new BackupDestination { Id = Guid.NewGuid(), Name = "NAS", Kind = BackupKinds.Smb, Keep = 10, Settings = new() { Path = @"\\nas\zq", User = "svc" }, Secret = new() { Password = "share-pass-1" } };
        await store.SaveDestinationAsync(d, true, true, null, default);
        await using (var c = ds.CreateCommand("SELECT secret FROM zentriq.backup_destinations"))
            Assert.DoesNotContain("share-pass-1", (string)(await c.ExecuteScalarAsync())!);
        Assert.Equal("share-pass-1", (await store.DestinationAsync(d.Id, true, default))!.Destination.Secret.Password);
        var noSecret = Assert.Single(await store.DestinationsAsync(false, default));
        Assert.True(noSecret.HasSecret);
        Assert.Null(noSecret.Destination.Secret.Password);
        d.Name = "NAS 2"; d.Secret = new();
        await store.SaveDestinationAsync(d, false, false, null, default);                 // keeps the stored secret
        Assert.Equal("share-pass-1", (await store.DestinationAsync(d.Id, true, default))!.Destination.Secret.Password);
        await store.SetStatusAsync(d.Id, true, "Copied", "SHA256:abc", default);
        var row = (await store.DestinationAsync(d.Id, false, default))!;
        Assert.Equal("ok", row.Status.Status);
        Assert.Equal("SHA256:abc", row.Destination.Settings.HostKey);

        Assert.Null(await store.LastScheduledStartAsync(default));
        var run = await store.StartRunAsync("schedule", "scheduled", "zentriq-srv-20261025-013000-scheduled.zbak", null, true, "1.1.1", default);
        await store.FinishRunAsync(run, "partial", 1234, "ab", [new DestinationResult(d.Id, "NAS 2", "smb", "failed", "access denied")], new BackupContents { Schema = 27 }, "NAS 2: access denied", default);
        var last = await store.LastScheduledStartAsync(default);
        Assert.NotNull(last);
        Assert.True(DateTimeOffset.UtcNow - last!.Value < TimeSpan.FromMinutes(5));

        // Unlock data handed from the console to the maintenance task.
        var u = store.UnprotectUnlock(store.ProtectUnlock("pw-for-restore", null, new byte[32]));
        Assert.Equal("pw-for-restore", u.Password);
        Assert.Equal(32, u.DataKey!.Length);
        await store.SetKeyFingerprintAsync("ABCDEF0123456789", default);
        Assert.Equal("ABCDEF0123456789", await store.KeyFingerprintAsync(default));
    }
}
