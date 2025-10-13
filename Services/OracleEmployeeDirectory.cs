using System.Data;
using Oracle.ManagedDataAccess.Client;
using KPIMonitor.ViewModels;
using KPIMonitor.Data;
using Microsoft.EntityFrameworkCore;

namespace KPIMonitor.Services
{
    public class OracleEmployeeDirectory : IEmployeeDirectory
    {
        private readonly AppDbContext _db;
        public OracleEmployeeDirectory(AppDbContext db) => _db = db;

        public async Task<IReadOnlyList<EmployeePickDto>> GetAllForPickAsync(CancellationToken ct = default)
        {
            var list = new List<EmployeePickDto>();
            var conn = _db.Database.GetDbConnection();

            if (conn.State != ConnectionState.Open)
                await conn.OpenAsync(ct);

            await using var cmd = conn.CreateCommand();
            cmd.CommandText = @"
                SELECT EMP_ID, NAME_ENG, USERID
                FROM   BADEA_ADDONS.EMPLOYEES
                WHERE  EMP_ID IS NOT NULL
                ORDER  BY NAME_ENG";
            await using var rdr = await cmd.ExecuteReaderAsync(ct);
            while (await rdr.ReadAsync(ct))
            {
                string empId = rdr.GetString(0);
                string nameEng = rdr.IsDBNull(1) ? "-" : rdr.GetString(1);
                string? userId = rdr.IsDBNull(2) ? null : rdr.GetString(2);

                list.Add(new EmployeePickDto
                {
                    EmpId = empId,
                    Label = $"{nameEng} ({empId})",
                    UserId = userId
                });
            }
            return list;
        }

        public async Task<(string EmpId, string NameEng)?> TryGetByEmpIdAsync(string empId, CancellationToken ct = default)
        {
            if (string.IsNullOrWhiteSpace(empId)) return null;

            var conn = _db.Database.GetDbConnection();
            if (conn.State != ConnectionState.Open)
                await conn.OpenAsync(ct);

            await using var cmd = conn.CreateCommand();
            cmd.CommandText = @"
                SELECT EMP_ID, NAME_ENG
                FROM   BADEA_ADDONS.EMPLOYEES
                WHERE  EMP_ID = :p_emp";
            var p = cmd.CreateParameter();
            p.ParameterName = "p_emp";
            p.Value = empId;
            cmd.Parameters.Add(p);

            await using var rdr = await cmd.ExecuteReaderAsync(ct);
            if (await rdr.ReadAsync(ct))
            {
                string e = rdr.GetString(0);
                string n = rdr.IsDBNull(1) ? "-" : rdr.GetString(1);
                return (e, n);
            }
            return null;
        }

        public async Task<(string EmpId, string NameEng)?> TryGetByUserIdAsync(string userId, CancellationToken ct = default)
        {
            if (string.IsNullOrWhiteSpace(userId)) return null;

            static string NormalizeSam(string raw)
            {
                var s = raw.Trim();
                var bs = s.LastIndexOf('\\');               // DOMAIN\user
                if (bs >= 0 && bs < s.Length - 1) s = s[(bs + 1)..];
                var at = s.IndexOf('@');                    // user@domain
                if (at > 0) s = s[..at];
                return s.Trim();
            }

            var sam = NormalizeSam(userId).ToUpperInvariant();
            if (string.IsNullOrWhiteSpace(sam)) return null;

            var conn = _db.Database.GetDbConnection();
            if (conn.State != System.Data.ConnectionState.Open)
                await conn.OpenAsync(ct);

            // 1) Fast path: exact match on USERID (handles cases where USERID is already "john.smith")
            await using (var cmd = conn.CreateCommand())
            {
                cmd.CommandText = @"
            SELECT EMP_ID, NAME_ENG
            FROM   BADEA_ADDONS.EMPLOYEES
            WHERE  UPPER(USERID) = :p_exact";
                var p = cmd.CreateParameter();
                p.ParameterName = "p_exact";
                p.Value = sam;
                cmd.Parameters.Add(p);

                await using var r = await cmd.ExecuteReaderAsync(ct);
                if (await r.ReadAsync(ct))
                {
                    var emp = r.GetString(0);
                    var name = r.IsDBNull(1) ? "" : r.GetString(1);
                    return (emp, name);
                }
            }

            // 2) Fallback: match where USERID contains the SAM (covers BADEA\sam and sam@badea.local)
            // Prefer endings like '\SAM' or '@' patterns, but also allow '%SAM%'
            await using (var cmd = conn.CreateCommand())
            {
                cmd.CommandText = @"
            SELECT EMP_ID, NAME_ENG
            FROM   BADEA_ADDONS.EMPLOYEES
            WHERE  UPPER(USERID) LIKE :p_like
               OR  UPPER(USERID) LIKE :p_domLike
               OR  UPPER(USERID) LIKE :p_mailLike
            ORDER BY LENGTH(USERID) ASC"; // shorter USERID often means cleaner/specific
                var p1 = cmd.CreateParameter(); p1.ParameterName = "p_like"; p1.Value = $"%{sam}%";
                var p2 = cmd.CreateParameter(); p2.ParameterName = "p_domLike"; p2.Value = $"%\\{sam}";      // DOMAIN\sam
                var p3 = cmd.CreateParameter(); p3.ParameterName = "p_mailLike"; p3.Value = $"{sam}@%";       // sam@domain
                cmd.Parameters.Add(p1); cmd.Parameters.Add(p2); cmd.Parameters.Add(p3);

                await using var r = await cmd.ExecuteReaderAsync(ct);
                if (await r.ReadAsync(ct))
                {
                    var emp = r.GetString(0);
                    var name = r.IsDBNull(1) ? "" : r.GetString(1);
                    return (emp, name);
                }
            }

            // 3) Last resort: EMAIL contains sam (some rows may have true mailbox there)
            await using (var cmd = conn.CreateCommand())
            {
                cmd.CommandText = @"
            SELECT EMP_ID, NAME_ENG
            FROM   BADEA_ADDONS.EMPLOYEES
            WHERE  UPPER(EMAIL) LIKE :p_mailAny";
                var p = cmd.CreateParameter();
                p.ParameterName = "p_mailAny";
                p.Value = $"%{sam}%";
                cmd.Parameters.Add(p);

                await using var r = await cmd.ExecuteReaderAsync(ct);
                if (await r.ReadAsync(ct))
                {
                    var emp = r.GetString(0);
                    var name = r.IsDBNull(1) ? "" : r.GetString(1);
                    return (emp, name);
                }
            }

            return null;
        }


        public async Task<string?> TryGetLoginByEmpIdAsync(string empId, CancellationToken ct = default)
        {
            if (string.IsNullOrWhiteSpace(empId)) return null;

            var conn = _db.Database.GetDbConnection();
            if (conn.State != ConnectionState.Open)
                await conn.OpenAsync(ct);

            await using var cmd = conn.CreateCommand();
            cmd.CommandText = @"
                SELECT USERID
                FROM   BADEA_ADDONS.EMPLOYEES
                WHERE  EMP_ID = :p_emp";
            var p = cmd.CreateParameter();
            p.ParameterName = "p_emp";
            p.Value = empId;
            cmd.Parameters.Add(p);

            var val = await cmd.ExecuteScalarAsync(ct);
            return val == null || val is DBNull ? null : Convert.ToString(val);
        }
    }
}
