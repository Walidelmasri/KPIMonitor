using Microsoft.EntityFrameworkCore;
using KPIMonitor.Models;

namespace KPIMonitor.Data
{
    public class AppDbContext : DbContext
    {
        public AppDbContext(DbContextOptions<AppDbContext> options) : base(options) { }

        public DbSet<DimPillar> DimPillars { get; set; }
        public DbSet<DimObjective> DimObjectives { get; set; } // <-- ADD THIS
        public DbSet<DimKpi> DimKpis { get; set; }
        public DbSet<DimPeriod> DimPeriods { get; set; }
        public DbSet<KpiYearPlan> KpiYearPlans { get; set; }
        public DbSet<KpiFact> KpiFacts { get; set; }
        public DbSet<KpiFiveYearTarget> KpiFiveYearTargets { get; set; }
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
                e.Property(x => x.Priority).HasColumnName("PRIORITY");
                e.Property(x => x.Owner).HasColumnName("OWNER").HasMaxLength(50);
                e.Property(x => x.Editor).HasColumnName("EDITOR").HasMaxLength(50);
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
        }
    }
}
