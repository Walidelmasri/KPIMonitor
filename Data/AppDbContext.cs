using Microsoft.EntityFrameworkCore;
using KPIMonitor.Models;

namespace KPIMonitor.Data
{
    public class AppDbContext : DbContext
    {
        public AppDbContext(DbContextOptions<AppDbContext> options) : base(options) { }

        public DbSet<DimPillar> DimPillars { get; set; }
        public DbSet<DimObjective> DimObjectives { get; set; }
        public DbSet<DimKpi> DimKpis { get; set; }
        public DbSet<DimPeriod> DimPeriods { get; set; }
        public DbSet<KpiYearPlan> KpiYearPlans { get; set; }
        public DbSet<KpiFact> KpiFacts { get; set; }
        public DbSet<KpiFiveYearTarget> KpiFiveYearTargets { get; set; }
        public DbSet<KpiAction> KpiActions { get; set; } = default!;
        public DbSet<KpiActionDeadlineHistory> KpiActionDeadlineHistories { get; set; } = default!;
        public DbSet<AuditLog> AuditLogs { get; set; } = default!;
        public DbSet<KpiFactChange> KpiFactChanges { get; set; } = null!;
        public DbSet<KPIMonitor.Models.KpiFactChangeBatch> KpiFactChangeBatches { get; set; } = null!;
        public DbSet<RedBoardOrder> RedBoardOrders { get; set; } = default!;

        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            modelBuilder.Entity<DimPillar>(e =>
            {
                e.ToTable("DIMPILLAR");
                e.HasKey(p => p.PillarId);
                e.Property(p => p.PillarId)
                  .HasColumnName("PILLARID")
                  .ValueGeneratedOnAdd();

                e.Property(p => p.PillarCode).HasColumnName("PILLARCODE").HasMaxLength(10);
                e.Property(p => p.PillarName).HasColumnName("PILLARNAME").HasMaxLength(200);
                e.Property(p => p.CreatedBy).HasColumnName("CREATEDBY").HasMaxLength(50);
                e.Property(p => p.CreatedDate).HasColumnName("CREATEDDATE");
                e.Property(p => p.LastChangedBy).HasColumnName("LASTCHANGEDBY").HasMaxLength(50);
                e.Property(p => p.IsActive).HasColumnName("ISACTIVE");

                // Optional: reflect DB default for CreatedDate (won’t auto-populate back unless reloaded)
                // e.Property(p => p.CreatedDate).HasDefaultValueSql("SYSTIMESTAMP");
            });
            modelBuilder.Entity<DimObjective>(e =>
            {
                e.ToTable("DIMOBJECTIVE");
                e.HasKey(x => x.ObjectiveId);

                e.Property(x => x.ObjectiveId).HasColumnName("OBJECTIVEID").ValueGeneratedOnAdd();
                e.Property(x => x.PillarId).HasColumnName("PILLARID");
                e.Property(x => x.ObjectiveCode).HasColumnName("OBJECTIVECODE").HasMaxLength(50);
                e.Property(x => x.ObjectiveName).HasColumnName("OBJECTIVENAME").HasMaxLength(200);
                e.Property(x => x.CreatedBy).HasColumnName("CREATEDBY").HasMaxLength(150);
                e.Property(x => x.CreatedDate).HasColumnName("CREATEDDATE");
                e.Property(x => x.LastChangedBy).HasColumnName("LASTCHANGEDBY").HasMaxLength(150);
                e.Property(x => x.IsActive).HasColumnName("ISACTIVE");

                // FK (Objective.PillarId -> Pillar.PillarId)
                e.HasOne(x => x.Pillar)
                 .WithMany()                   // no collection needed now
                 .HasForeignKey(x => x.PillarId)
                 .OnDelete(DeleteBehavior.Restrict); // keep objectives if pillar in use (adjust if you want cascade)
            });
            modelBuilder.Entity<DimKpi>(e =>
            {
                e.ToTable("DIMKPI");
                e.HasKey(k => k.KpiId);

                e.Property(k => k.KpiId).HasColumnName("KPIID").ValueGeneratedOnAdd();
                e.Property(k => k.ObjectiveId).HasColumnName("OBJECTIVEID");
                e.Property(k => k.PillarId).HasColumnName("PILLARID");
                e.Property(k => k.KpiCode).HasColumnName("KPICODE").HasMaxLength(50);
                e.Property(k => k.KpiName).HasColumnName("KPINAME").HasMaxLength(200);
                e.Property(k => k.CreatedBy).HasColumnName("CREATEDBY").HasMaxLength(150);
                e.Property(k => k.CreatedDate).HasColumnName("CREATEDDATE");
                e.Property(k => k.LastChangedBy).HasColumnName("LASTCHANGEDBY").HasMaxLength(150);
                e.Property(k => k.IsActive).HasColumnName("ISACTIVE");

                e.HasOne(k => k.Pillar)
                 .WithMany()
                 .HasForeignKey(k => k.PillarId)
                 .OnDelete(DeleteBehavior.Restrict);

                e.HasOne(k => k.Objective)
                 .WithMany()
                 .HasForeignKey(k => k.ObjectiveId)
                 .OnDelete(DeleteBehavior.Restrict);
            });
            modelBuilder.Entity<DimPeriod>(e =>
            {
                e.ToTable("DIMPERIOD");
                e.HasKey(x => x.PeriodId);
                e.Property(x => x.PeriodId).HasColumnName("PERIODID").ValueGeneratedOnAdd();
                e.Property(x => x.Year).HasColumnName("YEAR");
                e.Property(x => x.QuarterNum).HasColumnName("QUARTERNUM");
                e.Property(x => x.MonthNum).HasColumnName("MONTHNUM");
                e.Property(x => x.StartDate).HasColumnName("STARTDATE");
                e.Property(x => x.EndDate).HasColumnName("ENDDATE");
                e.Property(x => x.CreatedBy).HasColumnName("CREATEDBY").HasMaxLength(150);
                e.Property(x => x.CreatedDate).HasColumnName("CREATEDDATE");
                e.Property(x => x.LastChangedBy).HasColumnName("LASTCHANGEDBY").HasMaxLength(150);
                e.Property(x => x.IsActive).HasColumnName("ISACTIVE");

                e.HasIndex(x => new { x.Year, x.QuarterNum, x.MonthNum })
                    .IsUnique()
                    .HasDatabaseName("UQ_DIMPERIOD");
            });

            modelBuilder.Entity<KpiYearPlan>(e =>
            {
                e.ToTable("KPIYEARPLAN");
                e.HasKey(x => x.KpiYearPlanId);

                e.Property(x => x.KpiYearPlanId).HasColumnName("KPIYEARPLANID").ValueGeneratedOnAdd();
                e.Property(x => x.KpiId).HasColumnName("KPIID");
                e.Property(x => x.PeriodId).HasColumnName("PERIODID");
                e.Property(x => x.Frequency).HasColumnName("FREQUENCY").HasMaxLength(30);
                e.Property(x => x.Unit).HasColumnName("UNIT").HasMaxLength(2);
                e.Property(x => x.AnnualTarget).HasColumnName("ANNUALTARGET");
                e.Property(x => x.AnnualBudget).HasColumnName("ANNUALBUDGET");
                e.Property(x => x.TargetDirection).HasColumnName("TARGET_DIRECTION");
                e.Property(x => x.Priority).HasColumnName("PRIORITY");
                e.Property(x => x.Owner).HasColumnName("OWNER").HasMaxLength(50);
                e.Property(x => x.Editor).HasColumnName("EDITOR").HasMaxLength(50);
                e.Property(x => x.CreatedBy).HasColumnName("CREATEDBY").HasMaxLength(50);
                e.Property(x => x.CreatedDate).HasColumnName("CREATEDDATE");
                e.Property(x => x.LastChangedBy).HasColumnName("LASTCHANGEDBY").HasMaxLength(50);
                e.Property(x => x.IsActive).HasColumnName("ISACTIVE");
                e.Property(p => p.OwnerEmpId)
                     .HasColumnName("OWNEREMPID")
                     .HasMaxLength(5);

                e.Property(p => p.EditorEmpId)
                 .HasColumnName("EDITOREMPID")
                 .HasMaxLength(5);
                e.HasOne(x => x.Kpi)
                 .WithMany()
                 .HasForeignKey(x => x.KpiId)
                 .OnDelete(DeleteBehavior.Restrict);

                e.HasOne(x => x.Period)
                 .WithMany()
                 .HasForeignKey(x => x.PeriodId)
                 .OnDelete(DeleteBehavior.Restrict);

                // one plan per KPI per year period (matches your DB unique constraint)
                e.HasIndex(x => new { x.KpiId, x.PeriodId })
                 .IsUnique()
                 .HasDatabaseName("UQ_KpiYearPlan_Kpi_Period");
            });
            modelBuilder.Entity<KpiFact>(e =>
            {
                e.ToTable("KPIFACTS");
                e.HasKey(x => x.KpiFactId);

                e.Property(x => x.KpiFactId).HasColumnName("KPIFACTID").ValueGeneratedOnAdd();
                e.Property(x => x.KpiId).HasColumnName("KPIID");
                e.Property(x => x.PeriodId).HasColumnName("PERIODID");
                e.Property(x => x.KpiYearPlanId).HasColumnName("KPIYEARPLANID");
                e.Property(x => x.ActualValue).HasColumnName("ACTUALVALUE");
                e.Property(x => x.TargetValue).HasColumnName("TARGETVALUE");
                e.Property(x => x.ForecastValue).HasColumnName("FORECASTVALUE");
                e.Property(x => x.Budget).HasColumnName("BUDGET");
                e.Property(x => x.StatusCode).HasColumnName("STATUSCODE").HasMaxLength(50);
                e.Property(x => x.CreatedBy).HasColumnName("CREATEDBY").HasMaxLength(50);
                e.Property(x => x.CreatedDate).HasColumnName("CREATEDDATE");
                e.Property(x => x.LastChangedBy).HasColumnName("LASTCHANGEDBY").HasMaxLength(50);
                e.Property(x => x.IsActive).HasColumnName("ISACTIVE");

                e.HasOne(x => x.Kpi)
                 .WithMany()
                 .HasForeignKey(x => x.KpiId)
                 .OnDelete(DeleteBehavior.Restrict);

                e.HasOne(x => x.Period)
                 .WithMany()
                 .HasForeignKey(x => x.PeriodId)
                 .OnDelete(DeleteBehavior.Restrict);

                e.HasOne(x => x.KpiYearPlan)
                 .WithMany()
                 .HasForeignKey(x => x.KpiYearPlanId)
                 .OnDelete(DeleteBehavior.Restrict);

                e.HasIndex(x => new { x.KpiYearPlanId, x.PeriodId })
                 .IsUnique()
                 .HasDatabaseName("UQ_KpiFacts_Plan_Period");
            });
            modelBuilder.Entity<KpiFiveYearTarget>(e =>
            {
                e.ToTable("KPIFIVEYEARTARGET");
                e.HasKey(x => x.KpiFiveYearTargetId);

                e.Property(x => x.KpiFiveYearTargetId).HasColumnName("KPIFIVEYEARTARGETID").ValueGeneratedOnAdd();
                e.Property(x => x.KpiId).HasColumnName("KPIID");
                e.Property(x => x.BaseYear).HasColumnName("BASEYEAR");
                e.Property(x => x.Period1).HasColumnName("PERIOD1");
                e.Property(x => x.Period2).HasColumnName("PERIOD2");
                e.Property(x => x.Period3).HasColumnName("PERIOD3");
                e.Property(x => x.Period4).HasColumnName("PERIOD4");
                e.Property(x => x.Period5).HasColumnName("PERIOD5");
                e.Property(x => x.Period1Value).HasColumnName("PERIOD1VALUE");
                e.Property(x => x.Period2Value).HasColumnName("PERIOD2VALUE");
                e.Property(x => x.Period3Value).HasColumnName("PERIOD3VALUE");
                e.Property(x => x.Period4Value).HasColumnName("PERIOD4VALUE");
                e.Property(x => x.Period5Value).HasColumnName("PERIOD5VALUE");
                e.Property(x => x.CreatedBy).HasColumnName("CREATEDBY").HasMaxLength(50);
                e.Property(x => x.CreatedDate).HasColumnName("CREATEDDATE");
                e.Property(x => x.LastChangedBy).HasColumnName("LASTCHANGEDBY").HasMaxLength(50);
                e.Property(x => x.LastChangedDate).HasColumnName("LASTCHANGEDDATE");
                e.Property(x => x.IsActive).HasColumnName("ISACTIVE");
                e.HasOne(x => x.Kpi)
                 .WithMany()                         // (no collection on DimKpi needed)
                 .HasForeignKey(x => x.KpiId)
                 .OnDelete(DeleteBehavior.Restrict);
            });
            // -------------------- KpiAction --------------------
            modelBuilder.Entity<KpiAction>(e =>
            {
                e.ToTable("KPIACTION");

                e.HasKey(x => x.ActionId);
                e.Property(x => x.ActionId)
                 .HasColumnName("ACTIONID")
                 .ValueGeneratedOnAdd();   // <-- add this
                e.Property(x => x.KpiId).HasColumnName("KPIID").IsRequired(false);
                e.Property(x => x.Owner).HasColumnName("OWNER").HasMaxLength(100).IsRequired();
                e.Property(x => x.AssignedAt).HasColumnName("ASSIGNEDAT");
                e.Property(x => x.Description).HasColumnName("DESCRIPTION").HasMaxLength(400).IsRequired();
                e.Property(x => x.DueDate).HasColumnName("DUEDATE");
                e.Property(x => x.StatusCode).HasColumnName("STATUSCODE").HasMaxLength(20).IsRequired();
                e.Property(x => x.ExtensionCount).HasColumnName("EXTENSIONCOUNT");
                e.Property(x => x.CreatedBy).HasColumnName("CREATEDBY").HasMaxLength(100);
                e.Property(x => x.CreatedDate).HasColumnName("CREATEDDATE");
                e.Property(x => x.LastChangedBy).HasColumnName("LASTCHANGEDBY").HasMaxLength(100);
                e.Property(x => x.LastChangedDate).HasColumnName("LASTCHANGEDDATE");
                e.Property(a => a.IsGeneral)
                .HasColumnName("ISGENERAL")
                .HasColumnType("NUMBER(1)")
                .HasDefaultValue(false);
                e.HasOne(x => x.Kpi)
                 .WithMany()                       // (no back-collection on DimKpi; keeps your existing models intact)
                 .HasForeignKey(x => x.KpiId)
                 .OnDelete(DeleteBehavior.Restrict)
                 .HasConstraintName("FK_KPI_ACTION_KPI");
            });

            // --------- KpiActionDeadlineHistory ---------
            modelBuilder.Entity<KpiActionDeadlineHistory>(e =>
            {
                e.ToTable("KPIACTIONDEADLINEHISTORY");

                e.HasKey(x => x.Id);
                e.Property(x => x.Id)
                 .HasColumnName("ID")
                 .ValueGeneratedOnAdd();   // <-- add this
                e.Property(x => x.ActionId).HasColumnName("ACTIONID").IsRequired();

                e.Property(x => x.OldDueDate).HasColumnName("OLDDUEDATE");
                e.Property(x => x.NewDueDate).HasColumnName("NEWDUEDATE").IsRequired();

                e.Property(x => x.ChangedAt).HasColumnName("CHANGEDAT").IsRequired();
                e.Property(x => x.ChangedBy).HasColumnName("CHANGEDBY").HasMaxLength(100);
                e.Property(x => x.Reason).HasColumnName("REASON").HasMaxLength(400);

                e.HasOne(x => x.Action)
                 .WithMany()                       // you can add a collection later if you want
                 .HasForeignKey(x => x.ActionId)
                 .OnDelete(DeleteBehavior.Cascade)
                 .HasConstraintName("FK_KPI_ACT_HIST_ACTION");
            });
            modelBuilder.Entity<AuditLog>(e =>
            {
                e.ToTable("AUDITLOG");
                e.HasKey(x => x.AuditId);
                e.Property(x => x.AuditId).HasColumnName("AUDITID").ValueGeneratedOnAdd();
                e.Property(x => x.TableName).HasColumnName("TABLENAME").HasMaxLength(128).IsRequired();
                e.Property(x => x.KeyJson).HasColumnName("KEYJSON").IsRequired();              // CLOB/NVARCHAR2 under the hood
                e.Property(x => x.Action).HasColumnName("ACTION").HasMaxLength(20).IsRequired();
                e.Property(x => x.ChangedBy).HasColumnName("CHANGEDBY").HasMaxLength(150);
                e.Property(x => x.ChangedAtUtc).HasColumnName("CHANGEDATUTC");
                e.Property(x => x.ColumnChangesJson).HasColumnName("COLUMNCHANGESJSON").IsRequired();
            });


            modelBuilder.Entity<KpiFactChange>(e =>
            {
                // You already ended up querying KPIFACTCHANGES, so keep that:
                e.ToTable("KPIFACTCHANGES");
                e.HasKey(x => x.KpiFactChangeId);

                // Map every property to the real UPPERCASE columns
                e.Property(x => x.KpiFactChangeId).HasColumnName("KPIFACTCHANGEID").ValueGeneratedOnAdd(); ;
                e.Property(x => x.KpiFactId).HasColumnName("KPIFACTID");
                e.Property(x => x.ProposedActualValue).HasColumnName("PROPOSEDACTUALVALUE");
                e.Property(x => x.ProposedTargetValue).HasColumnName("PROPOSEDTARGETVALUE");
                e.Property(x => x.ProposedForecastValue).HasColumnName("PROPOSEDFORECASTVALUE");
                e.Property(x => x.ProposedStatusCode).HasColumnName("PROPOSEDSTATUSCODE");
                e.Property(x => x.SubmittedBy).HasColumnName("SUBMITTEDBY");
                e.Property(x => x.SubmittedAt).HasColumnName("SUBMITTEDAT");
                e.Property(x => x.ApprovalStatus).HasColumnName("APPROVALSTATUS");
                e.Property(x => x.ReviewedBy).HasColumnName("REVIEWEDBY");
                e.Property(x => x.ReviewedAt).HasColumnName("REVIEWEDAT");
                e.Property(x => x.BatchId).HasColumnName("BATCHID");
                e.Property(x => x.RejectReason).HasColumnName("REJECTREASON");

                // FK → KpiFacts (leave this if KpiFacts already works in your app)
                e.HasOne(x => x.KpiFact)
                 .WithMany(f => f.Changes)
                 .HasForeignKey(x => x.KpiFactId);
            });
            // ---- KpiFactChange (fix BATCHID mapping) ----
            modelBuilder.Entity<KpiFactChange>(e =>
            {
                e.ToTable("KPIFACTCHANGES");
                e.HasKey(x => x.KpiFactChangeId);

                e.Property(x => x.KpiFactChangeId).HasColumnName("KPIFACTCHANGEID").ValueGeneratedOnAdd();
                e.Property(x => x.KpiFactId).HasColumnName("KPIFACTID");
                e.Property(x => x.ProposedActualValue).HasColumnName("PROPOSEDACTUALVALUE");
                e.Property(x => x.ProposedTargetValue).HasColumnName("PROPOSEDTARGETVALUE");
                e.Property(x => x.ProposedForecastValue).HasColumnName("PROPOSEDFORECASTVALUE");
                e.Property(x => x.ProposedStatusCode).HasColumnName("PROPOSEDSTATUSCODE");
                e.Property(x => x.SubmittedBy).HasColumnName("SUBMITTEDBY");
                e.Property(x => x.SubmittedAt).HasColumnName("SUBMITTEDAT");
                e.Property(x => x.ApprovalStatus).HasColumnName("APPROVALSTATUS");
                e.Property(x => x.ReviewedBy).HasColumnName("REVIEWEDBY");
                e.Property(x => x.ReviewedAt).HasColumnName("REVIEWEDAT");
                e.Property(x => x.RejectReason).HasColumnName("REJECTREASON");
                e.Property(x => x.BatchId).HasColumnName("BATCHID"); // <-- was "BatchId" (wrong case)

                e.HasOne(x => x.KpiFact)
                 .WithMany(f => f.Changes)
                 .HasForeignKey(x => x.KpiFactId)
                 .OnDelete(DeleteBehavior.Restrict);
            });

            // ---- KpiFactChangeBatch (map EVERY column to UPPERCASE) ----
            modelBuilder.Entity<KpiFactChangeBatch>(e =>
            {
                e.ToTable("KPIFACTCHANGEBATCHES");
                e.HasKey(x => x.BatchId);

                e.Property(x => x.BatchId).HasColumnName("BATCHID").ValueGeneratedOnAdd();
                e.Property(x => x.KpiId).HasColumnName("KPIID");
                e.Property(x => x.KpiYearPlanId).HasColumnName("KPIYEARPLANID");
                e.Property(x => x.Year).HasColumnName("YEAR");                  // <-- this fixes ORA-00904
                e.Property(x => x.Frequency).HasColumnName("FREQUENCY");
                e.Property(x => x.PeriodMin).HasColumnName("PERIODMIN");
                e.Property(x => x.PeriodMax).HasColumnName("PERIODMAX");

                e.Property(x => x.RowCount).HasColumnName("ROWCOUNT");
                e.Property(x => x.SkippedCount).HasColumnName("SKIPPEDCOUNT");

                e.Property(x => x.SubmittedBy).HasColumnName("SUBMITTEDBY");
                e.Property(x => x.SubmittedAt).HasColumnName("SUBMITTEDAT").HasColumnType("TIMESTAMP(0)");

                e.Property(x => x.ApprovalStatus).HasColumnName("APPROVALSTATUS");
                e.Property(x => x.ReviewedBy).HasColumnName("REVIEWEDBY");
                e.Property(x => x.ReviewedAt).HasColumnName("REVIEWEDAT").HasColumnType("TIMESTAMP(0)");
                e.Property(x => x.RejectReason).HasColumnName("REJECTREASON");
            });
            modelBuilder.Entity<RedBoardOrder>(e =>
            {
                e.ToTable("REDBOARDORDER");

                e.HasKey(x => x.Id);

                e.Property(x => x.Id)
                    .HasColumnName("ID");

                e.Property(x => x.KpiId)
                    .HasColumnName("KPIID");

                e.Property(x => x.SortOrder)
                    .HasColumnName("SORTORDER");
                e.Property(x => x.IsHidden)
                .HasColumnName("IS_HIDDEN");
            });

        }


        public override async Task<int> SaveChangesAsync(CancellationToken cancellationToken = default)
        {
            // collect audit rows BEFORE saving
            var now = DateTime.UtcNow;
            // var user = string.IsNullOrWhiteSpace(CurrentUserName) ? "system" : CurrentUserName;

            var audits = new List<AuditLog>();

            // entries we want to log
            var entries = ChangeTracker.Entries()
                .Where(e => e.Entity is not AuditLog &&
                            e.State != EntityState.Detached &&
                            e.State != EntityState.Unchanged)
                .ToList();

            foreach (var e in entries)
            {
                var table = e.Metadata.GetTableName() ?? e.Metadata.ClrType.Name.ToUpperInvariant();

                // primary key(s)
                var keyProps = e.Properties.Where(p => p.Metadata.IsPrimaryKey());
                var keyDict = new Dictionary<string, object?>();
                foreach (var kp in keyProps)
                {
                    object? val = e.State == EntityState.Added ? kp.CurrentValue
                                : e.State == EntityState.Deleted ? kp.OriginalValue
                                : kp.CurrentValue ?? kp.OriginalValue;
                    keyDict[kp.Metadata.Name] = val;
                }

                // action + changed columns
                string action = e.State switch
                {
                    EntityState.Added => "Added",
                    EntityState.Deleted => "Deleted",
                    EntityState.Modified => "Modified",
                    _ => "Unknown"
                };

                var changes = new List<object>();

                if (e.State == EntityState.Added)
                {
                    foreach (var p in e.Properties)
                    {
                        changes.Add(new { Column = p.Metadata.Name, Old = (string?)null, New = p.CurrentValue });
                    }
                }
                else if (e.State == EntityState.Deleted)
                {
                    foreach (var p in e.Properties)
                    {
                        changes.Add(new { Column = p.Metadata.Name, Old = p.OriginalValue, New = (string?)null });
                    }
                }
                else if (e.State == EntityState.Modified)
                {
                    foreach (var p in e.Properties.Where(p => p.IsModified))
                    {
                        // only record when value actually changed
                        var oldVal = p.OriginalValue;
                        var newVal = p.CurrentValue;
                        if (!Equals(oldVal, newVal))
                        {
                            changes.Add(new { Column = p.Metadata.Name, Old = oldVal, New = newVal });
                        }
                    }
                    // if nothing actually changed (rare), skip logging
                    if (changes.Count == 0) continue;
                }

                var audit = new AuditLog
                {
                    TableName = table,
                    KeyJson = System.Text.Json.JsonSerializer.Serialize(keyDict),
                    Action = action,
                    ChangedBy = "system", //need to change this in the future to actual currentuser fetched by ad
                    ChangedAtUtc = now,
                    ColumnChangesJson = System.Text.Json.JsonSerializer.Serialize(changes)
                };
                audits.Add(audit);
            }

            // save data first
            var result = await base.SaveChangesAsync(cancellationToken);

            // then save audits (if any)
            if (audits.Count > 0)
            {
                AuditLogs.AddRange(audits);
                await base.SaveChangesAsync(cancellationToken);
            }

            return result;
        }
    }
    // Simple entity for manual RedBoard ordering
    public class RedBoardOrder
    {
        public int Id { get; set; }
        public decimal KpiId { get; set; }   // same type as DimKpi.KpiId
        public int SortOrder { get; set; }
            public bool IsHidden { get; set; }  // maps to IS_HIDDEN

    }

}
