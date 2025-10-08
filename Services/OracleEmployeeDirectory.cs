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
            const string sql = @"
                SELECT EMP_ID, NAME_ENG
                FROM BADEA_ADDONS.EMPLOYEES
                WHERE UPPER(USERID) = UPPER(:p_userid)";

            var conn = _db.Database.GetDbConnection();
            if (conn.State != ConnectionState.Open)
                await conn.OpenAsync(ct);

            await using var cmd = conn.CreateCommand();
            cmd.CommandText = sql;

            var p = cmd.CreateParameter();
            p.ParameterName = "p_userid";
            p.Value = userId ?? (object)DBNull.Value;
            cmd.Parameters.Add(p);

            await using var reader = await cmd.ExecuteReaderAsync(ct);
            if (await reader.ReadAsync(ct))
            {
                var empId = reader.GetString(0);
                var name = reader.GetString(1);
                return (empId, name);
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
