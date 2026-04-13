using System.Data;
using System.Globalization;
using KPIMonitor.Data;
using KPIMonitor.ViewModels;
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
                SELECT EMP_ID, NAME_ENG, NAME_ARABIC, USERID
                FROM   BADEA_ADDONS.EMPLOYEES
                WHERE  EMP_ID IS NOT NULL
                ORDER  BY NAME_ENG";

            await using var rdr = await cmd.ExecuteReaderAsync(ct);
            while (await rdr.ReadAsync(ct))
            {
                string empId = rdr.GetString(0);
                string nameEng = rdr.IsDBNull(1) ? "-" : rdr.GetString(1);
                string nameAr = rdr.IsDBNull(2) ? nameEng : rdr.GetString(2);
                string? userId = rdr.IsDBNull(3) ? null : rdr.GetString(3);

                var isArabic = CultureInfo.CurrentUICulture.Name
                    .StartsWith("ar", StringComparison.OrdinalIgnoreCase);

                var displayName = isArabic ? nameAr : nameEng;

                list.Add(new EmployeePickDto
                {
                    EmpId = empId,
                    NameEng = nameEng,
                    NameAr = nameAr,
                    Label = $"{displayName} ({empId})",
                    UserId = userId
                });
            }

            return list;
        }

        public async Task<(string EmpId, string NameEng, string? NameAr)?> TryGetByEmpIdAsync(string empId, CancellationToken ct = default)
        {
            if (string.IsNullOrWhiteSpace(empId)) return null;

            var conn = _db.Database.GetDbConnection();
            if (conn.State != ConnectionState.Open)
                await conn.OpenAsync(ct);

            await using var cmd = conn.CreateCommand();
            cmd.CommandText = @"
                SELECT EMP_ID, NAME_ENG, NAME_ARABIC
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
                string nEng = rdr.IsDBNull(1) ? "-" : rdr.GetString(1);
                string? nAr = rdr.IsDBNull(2) ? null : rdr.GetString(2);
                return (e, nEng, nAr);
            }

            return null;
        }

        public async Task<(string EmpId, string NameEng, string? NameAr)?> TryGetByUserIdAsync(string userId, CancellationToken ct = default)
        {
            if (string.IsNullOrWhiteSpace(userId)) return null;

            static string NormalizeSam(string raw)
            {
                var s = raw.Trim();

                var bs = s.LastIndexOf('\\');
                if (bs >= 0 && bs < s.Length - 1)
                    s = s[(bs + 1)..];

                var at = s.IndexOf('@');
                if (at > 0)
                    s = s[..at];

                return s.Trim();
            }

            var sam = NormalizeSam(userId).ToUpperInvariant();
            if (string.IsNullOrWhiteSpace(sam)) return null;

            var conn = _db.Database.GetDbConnection();
            if (conn.State != ConnectionState.Open)
                await conn.OpenAsync(ct);

            // 1) Exact USERID match
            await using (var cmd = conn.CreateCommand())
            {
                cmd.CommandText = @"
                    SELECT EMP_ID, NAME_ENG, NAME_ARABIC
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
                    var nameEng = r.IsDBNull(1) ? "" : r.GetString(1);
                    string? nameAr = r.IsDBNull(2) ? null : r.GetString(2);
                    return (emp, nameEng, nameAr);
                }
            }

            // 2) USERID contains sam
            await using (var cmd = conn.CreateCommand())
            {
                cmd.CommandText = @"
                    SELECT EMP_ID, NAME_ENG, NAME_ARABIC
                    FROM   BADEA_ADDONS.EMPLOYEES
                    WHERE  UPPER(USERID) LIKE :p_like
                       OR  UPPER(USERID) LIKE :p_domLike
                       OR  UPPER(USERID) LIKE :p_mailLike
                    ORDER BY LENGTH(USERID) ASC";

                var p1 = cmd.CreateParameter();
                p1.ParameterName = "p_like";
                p1.Value = $"%{sam}%";

                var p2 = cmd.CreateParameter();
                p2.ParameterName = "p_domLike";
                p2.Value = $"%\\{sam}";

                var p3 = cmd.CreateParameter();
                p3.ParameterName = "p_mailLike";
                p3.Value = $"{sam}@%";

                cmd.Parameters.Add(p1);
                cmd.Parameters.Add(p2);
                cmd.Parameters.Add(p3);

                await using var r = await cmd.ExecuteReaderAsync(ct);
                if (await r.ReadAsync(ct))
                {
                    var emp = r.GetString(0);
                    var nameEng = r.IsDBNull(1) ? "" : r.GetString(1);
                    string? nameAr = r.IsDBNull(2) ? null : r.GetString(2);
                    return (emp, nameEng, nameAr);
                }
            }

            // 3) EMAIL contains sam
            await using (var cmd = conn.CreateCommand())
            {
                cmd.CommandText = @"
                    SELECT EMP_ID, NAME_ENG, NAME_ARABIC
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
                    var nameEng = r.IsDBNull(1) ? "" : r.GetString(1);
                    string? nameAr = r.IsDBNull(2) ? null : r.GetString(2);
                    return (emp, nameEng, nameAr);
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