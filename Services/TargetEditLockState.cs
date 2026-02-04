using Oracle.ManagedDataAccess.Client;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace KPIMonitor.Services
{
    // Persistent global flag stored in Oracle (survives app restarts)
    public sealed class TargetEditLockState
    {
        private const string FlagKey = "TARGET_EDIT_UNLOCKED";
        private readonly string _connStr;
        private readonly ILogger<TargetEditLockState> _log;

        // cached value to avoid DB reads on every request
        private volatile bool _cachedUnlocked = false;
        private DateTime _cachedAtUtc = DateTime.MinValue;
        private readonly object _gate = new();

        // cache refresh interval (safe + lightweight)
        private static readonly TimeSpan CacheTtl = TimeSpan.FromSeconds(15);

        public TargetEditLockState(IConfiguration cfg, ILogger<TargetEditLockState> log)
        {
            _connStr = cfg.GetConnectionString("DefaultConnection") ?? throw new InvalidOperationException("Missing DefaultConnection");
            _log = log;
        }

        public bool IsUnlocked
        {
            get
            {
                // If cache is stale, refresh synchronously (ASP.NET Core has no sync context deadlock risk)
                if (DateTime.UtcNow - _cachedAtUtc > CacheTtl)
                {
                    try { RefreshFromDb(); }
                    catch (Exception ex) { _log.LogWarning(ex, "Failed to refresh TARGET_EDIT_UNLOCKED flag; using cached value."); }
                }
                return _cachedUnlocked;
            }
        }

        public async Task<bool> ToggleAsync(string updatedBy, CancellationToken ct)
        {
            // toggle in DB, then update cache
            var newValue = await ToggleDbAsync(updatedBy, ct);
            lock (_gate)
            {
                _cachedUnlocked = newValue;
                _cachedAtUtc = DateTime.UtcNow;
            }
            return newValue;
        }

        public async Task WarmUpAsync(CancellationToken ct)
        {
            try
            {
                var v = await ReadDbAsync(ct);
                lock (_gate)
                {
                    _cachedUnlocked = v;
                    _cachedAtUtc = DateTime.UtcNow;
                }
            }
            catch (Exception ex)
            {
                _log.LogWarning(ex, "WarmUp failed for TARGET_EDIT_UNLOCKED flag; defaulting to locked.");
                lock (_gate)
                {
                    _cachedUnlocked = false;
                    _cachedAtUtc = DateTime.UtcNow;
                }
            }
        }

        private void RefreshFromDb()
        {
            lock (_gate)
            {
                // double-check inside lock
                if (DateTime.UtcNow - _cachedAtUtc <= CacheTtl) return;

                using var con = new OracleConnection(_connStr);
                con.Open();

                using var cmd = con.CreateCommand();
                cmd.CommandText = @"
SELECT FLAG_VALUE
  FROM APP_FEATURE_FLAGS
 WHERE FLAG_KEY = :k";
                cmd.Parameters.Add(new OracleParameter("k", FlagKey));

                var obj = cmd.ExecuteScalar();
                var val = Convert.ToInt32(obj ?? 0);
                _cachedUnlocked = val == 1;
                _cachedAtUtc = DateTime.UtcNow;
            }
        }

        private async Task<bool> ReadDbAsync(CancellationToken ct)
        {
            await using var con = new OracleConnection(_connStr);
            await con.OpenAsync(ct);

            await using var cmd = con.CreateCommand();
            cmd.CommandText = @"
SELECT FLAG_VALUE
  FROM APP_FEATURE_FLAGS
 WHERE FLAG_KEY = :k";
            cmd.Parameters.Add(new OracleParameter("k", FlagKey));

            var obj = await cmd.ExecuteScalarAsync(ct);
            var val = Convert.ToInt32(obj ?? 0);
            return val == 1;
        }

        private async Task<bool> ToggleDbAsync(string updatedBy, CancellationToken ct)
        {
            await using var con = new OracleConnection(_connStr);
            await con.OpenAsync(ct);

            await using var tx = con.BeginTransaction(); // ✅ IMPORTANT (so we can COMMIT)

            // flip 0/1
            await using (var cmd = con.CreateCommand())
            {
                cmd.Transaction = tx; // ✅ bind command to transaction

                cmd.CommandText = @"
UPDATE APP_FEATURE_FLAGS
   SET FLAG_VALUE = CASE WHEN FLAG_VALUE = 1 THEN 0 ELSE 1 END,
       UPDATED_BY = :u,
       UPDATED_AT = SYSDATE
 WHERE FLAG_KEY = :k";

                cmd.Parameters.Add(new OracleParameter("u", string.IsNullOrWhiteSpace(updatedBy) ? "system" : updatedBy));
                cmd.Parameters.Add(new OracleParameter("k", FlagKey));

                var rows = await cmd.ExecuteNonQueryAsync(ct);
                if (rows != 1)
                    throw new InvalidOperationException("TARGET_EDIT_UNLOCKED row not found or not unique.");
            }

            await tx.CommitAsync(ct); // ✅ THIS is what makes it persistent

            // read back (new session state is committed already, but this is fine)
            return await ReadDbAsync(ct);
        }

    }
}
