using System.Buffers.Binary;
using System.Text;
using System.Text.Json;
using Npgsql;
using NpgsqlTypes;

namespace Zentriq.Infrastructure.Remote;

/// <summary>Metadata of a recorded remote session (the first block of the engine's .mcrec file) and its time span.</summary>
public sealed record RecordingInfo(string? MeshUser, string? UserName, string? NodeId, string? NodeName, int? Protocol, DateTimeOffset? Start, DateTimeOffset? End, bool Complete, long Size);

/// <summary>
/// Recorded remote sessions: the engine writes one .mcrec file per recorded session (blocks of a 16-byte header — type
/// 1 metadata / 2 data / 3 end, flags, size, time in milliseconds — followed by the data). Zentriq indexes the files,
/// links them to the session started from the console, plays them back in the console and deletes them after the
/// retention period.
/// </summary>
public static class RemoteRecordings
{
    public static string Kind(int? protocol) => protocol switch { 1 => "terminal", 2 => "desktop", 5 => "files", _ => "other" };

    /// <summary>The full path of a recording inside the recordings folder, or null when the name leaves the folder.</summary>
    public static string? PathOf(string dir, string relative)
    {
        if (string.IsNullOrWhiteSpace(relative)) return null;
        var root = Path.GetFullPath(dir).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
        var full = Path.GetFullPath(Path.Combine(root, relative.Replace('/', Path.DirectorySeparatorChar)));
        return full.StartsWith(root, StringComparison.OrdinalIgnoreCase) && full.EndsWith(".mcrec", StringComparison.OrdinalIgnoreCase) ? full : null;
    }

    /// <summary>Reads the metadata block and walks the block headers to find the end of the session.</summary>
    public static RecordingInfo? Read(Stream s)
    {
        var header = new byte[16];
        if (!Fill(s, header)) return null;
        var type = BinaryPrimitives.ReadInt16BigEndian(header.AsSpan(0, 2));
        var size = BinaryPrimitives.ReadInt32BigEndian(header.AsSpan(4, 4));
        if (type != 1 || size is <= 0 or > 1_000_000) return null;
        var start = Time(header);
        var meta = new byte[size];
        if (!Fill(s, meta)) return null;
        string? user = null, userName = null, node = null, nodeName = null; int? protocol = null;
        try
        {
            using var doc = JsonDocument.Parse(meta);
            var r = doc.RootElement;
            if (r.ValueKind == JsonValueKind.Object)
            {
                if (!(r.TryGetProperty("magic", out var magic) && magic.GetString() is "MeshCentralRelaySession")) return null;
                user = Str(r, "userid"); userName = Str(r, "username"); node = Str(r, "nodeid"); nodeName = Str(r, "nodename");
                if (r.TryGetProperty("protocol", out var p) && p.ValueKind == JsonValueKind.Number && p.TryGetInt32(out var pi)) protocol = pi;
            }
        }
        catch (JsonException) { return null; }
        DateTimeOffset? end = null;
        var complete = false;
        var last = start;
        while (Fill(s, header))
        {
            type = BinaryPrimitives.ReadInt16BigEndian(header.AsSpan(0, 2));
            size = BinaryPrimitives.ReadInt32BigEndian(header.AsSpan(4, 4));
            if (size < 0) break;
            last = Time(header) ?? last;
            if (type == 3) { complete = true; end = last; break; }
            if (s.CanSeek) { if (s.Position + size > s.Length) break; s.Seek(size, SeekOrigin.Current); }
            else if (!Fill(s, new byte[size])) break;
        }
        return new RecordingInfo(user, userName, node, nodeName, protocol, start, end ?? last, complete, s.CanSeek ? s.Length : 0);
    }

    private static DateTimeOffset? Time(byte[] h)
    {
        long ms = ((long)BinaryPrimitives.ReadUInt16BigEndian(h.AsSpan(10, 2)) << 32) | BinaryPrimitives.ReadUInt32BigEndian(h.AsSpan(12, 4));
        return ms > 946_684_800_000 && ms < 32_503_680_000_000 ? DateTimeOffset.FromUnixTimeMilliseconds(ms) : null;
    }

    private static bool Fill(Stream s, byte[] buf)
    {
        var read = 0;
        while (read < buf.Length)
        {
            var n = s.Read(buf, read, buf.Length - read);
            if (n == 0) return false;
            read += n;
        }
        return true;
    }

    private static string? Str(JsonElement e, string name) => e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    public sealed record IndexResult(int Added, int Deleted);

    /// <summary>
    /// Adds new finished recordings to the index (with the Zentriq technician, device and session) and deletes the
    /// recordings older than <paramref name="retentionDays"/>.
    /// </summary>
    public static async Task<IndexResult> IndexAsync(NpgsqlDataSource ds, string dir, int retentionDays, CancellationToken ct)
    {
        var added = 0; var deleted = 0;
        if (!Directory.Exists(dir)) return new IndexResult(0, 0);
        var known = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        await using (var cmd = ds.CreateCommand("SELECT file_name FROM zentriq.remote_recordings"))
        await using (var r = await cmd.ExecuteReaderAsync(ct))
            while (await r.ReadAsync(ct)) known.Add(r.GetString(0));
        var cutoff = DateTimeOffset.UtcNow.AddDays(-Math.Max(1, retentionDays));
        var root = Path.GetFullPath(dir);
        foreach (var path in Directory.EnumerateFiles(root, "*.mcrec", SearchOption.AllDirectories))
        {
            var rel = Path.GetRelativePath(root, path).Replace(Path.DirectorySeparatorChar, '/');
            if (known.Contains(rel)) continue;
            var fi = new FileInfo(path);
            if (fi.LastWriteTimeUtc < cutoff.UtcDateTime)
            {
                try { fi.Delete(); deleted++; } catch (IOException) { } catch (UnauthorizedAccessException) { }
                continue;
            }
            RecordingInfo? info;
            try
            {
                await using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                info = Read(fs);
            }
            catch (IOException) { continue; }
            catch (UnauthorizedAccessException) { continue; }
            if (info is null) continue;
            // Still being written: wait for the end block (or two quiet minutes after the engine stopped writing).
            if (!info.Complete && fi.LastWriteTimeUtc > DateTime.UtcNow.AddMinutes(-2)) continue;
            var start = info.Start ?? new DateTimeOffset(fi.CreationTimeUtc, TimeSpan.Zero);
            var userId = RemoteSupportService.UserOfMeshUser(info.MeshUser);
            await using var ins = ds.CreateCommand("""
                WITH u AS (SELECT id, display_name FROM zentriq.users WHERE id = @u),
                     n AS (SELECT asset_id, name FROM zentriq.remote_nodes WHERE node_id = @node),
                     s AS (SELECT id FROM zentriq.remote_sessions
                            WHERE user_id = @u AND node_id = @node AND created_at BETWEEN @start - interval '15 minutes' AND @start + interval '2 minutes'
                            ORDER BY created_at DESC LIMIT 1)
                INSERT INTO zentriq.remote_recordings (file_name, session_id, user_id, user_name, node_id, asset_id, device_name, protocol, started_at, ended_at, size_bytes)
                VALUES (@f, (SELECT id FROM s), (SELECT id FROM u), COALESCE((SELECT display_name FROM u), @un), @node, (SELECT asset_id FROM n),
                        COALESCE(@nn, (SELECT name FROM n)), @p, @start, @end, @size)
                ON CONFLICT (file_name) DO NOTHING
                RETURNING session_id
                """);
            ins.Parameters.AddWithValue("f", rel);
            ins.Parameters.Add(new NpgsqlParameter("u", NpgsqlDbType.Uuid) { Value = (object?)userId ?? DBNull.Value });
            var un = info.UserName;
            if (un is not null && un.EndsWith("-rec", StringComparison.Ordinal)) un = un[..^4];
            ins.Parameters.Add(new NpgsqlParameter("un", NpgsqlDbType.Text) { Value = (object?)un ?? DBNull.Value });
            ins.Parameters.Add(new NpgsqlParameter("node", NpgsqlDbType.Text) { Value = (object?)info.NodeId ?? DBNull.Value });
            ins.Parameters.Add(new NpgsqlParameter("nn", NpgsqlDbType.Text) { Value = (object?)info.NodeName ?? DBNull.Value });
            ins.Parameters.Add(new NpgsqlParameter("p", NpgsqlDbType.Integer) { Value = (object?)info.Protocol ?? DBNull.Value });
            ins.Parameters.AddWithValue("start", start);
            ins.Parameters.Add(new NpgsqlParameter("end", NpgsqlDbType.TimestampTz) { Value = (object?)info.End ?? DBNull.Value });
            ins.Parameters.AddWithValue("size", fi.Length);
            var sid = await ins.ExecuteScalarAsync(ct);
            added++;
            if (sid is long sessionId)
            {
                await using var mark = ds.CreateCommand("UPDATE zentriq.remote_sessions SET recorded = true WHERE id = @id");
                mark.Parameters.AddWithValue("id", sessionId);
                await mark.ExecuteNonQueryAsync(ct);
            }
        }

        // Retention.
        var old = new List<(long Id, string File)>();
        await using (var cmd = ds.CreateCommand("SELECT id, file_name FROM zentriq.remote_recordings WHERE deleted_at IS NULL AND COALESCE(started_at, indexed_at) < @c"))
        {
            cmd.Parameters.AddWithValue("c", cutoff);
            await using var r = await cmd.ExecuteReaderAsync(ct);
            while (await r.ReadAsync(ct)) old.Add((r.GetInt64(0), r.GetString(1)));
        }
        foreach (var (id, file) in old)
        {
            if (PathOf(root, file) is { } p && File.Exists(p))
                try { File.Delete(p); } catch (IOException) { continue; } catch (UnauthorizedAccessException) { continue; }
            await using var del = ds.CreateCommand("UPDATE zentriq.remote_recordings SET deleted_at = now() WHERE id = @id");
            del.Parameters.AddWithValue("id", id);
            await del.ExecuteNonQueryAsync(ct);
            deleted++;
        }
        return new IndexResult(added, deleted);
    }

    /// <summary>Writes a recording file in the engine's format (tests and the acceptance check).</summary>
    public static byte[] Build(object metadata, IEnumerable<(int Type, int Flags, DateTimeOffset At, byte[] Data)> blocks)
    {
        using var ms = new MemoryStream();
        void Block(int type, int flags, DateTimeOffset at, byte[] data)
        {
            var h = new byte[16];
            BinaryPrimitives.WriteInt16BigEndian(h.AsSpan(0, 2), (short)type);
            BinaryPrimitives.WriteInt16BigEndian(h.AsSpan(2, 2), (short)flags);
            BinaryPrimitives.WriteInt32BigEndian(h.AsSpan(4, 4), data.Length);
            var t = at.ToUnixTimeMilliseconds();
            BinaryPrimitives.WriteUInt16BigEndian(h.AsSpan(10, 2), (ushort)(t >> 32));
            BinaryPrimitives.WriteUInt32BigEndian(h.AsSpan(12, 4), (uint)(t & 0xFFFFFFFF));
            ms.Write(h);
            ms.Write(data);
        }
        var list = blocks.ToList();
        Block(1, 0, list.Count > 0 ? list[0].At : DateTimeOffset.UtcNow, Encoding.UTF8.GetBytes(JsonSerializer.Serialize(metadata)));
        foreach (var b in list) Block(b.Type, b.Flags, b.At, b.Data);
        return ms.ToArray();
    }
}
