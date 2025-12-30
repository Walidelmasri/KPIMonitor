namespace KPIMonitor.ViewModels
{
    /// <summary>DTO for the Assign Owner & Editor modal.</summary>
    public sealed class OwnerEditorEditVm
    {
        public decimal PlanId { get; init; }

        // Selected EMP_IDs (VARCHAR2(5))
        public string? OwnerEmpId { get; set; }
        public string? EditorEmpId { get; set; }

        // For context (what the plan currently shows)
        public string? CurrentOwnerName { get; init; }
        public string? CurrentEditorName { get; init; }
        public string? Editor2EmpId { get; set; } // optional
        public string? CurrentEditor2Name { get; init; }

        // Dropdown options
        public IReadOnlyList<EmployeePickDto> Employees { get; init; } = Array.Empty<EmployeePickDto>();
    }

    /// <summary>One employee option for the dropdown.</summary>
    public sealed class EmployeePickDto
    {
        public string EmpId { get; init; } = "";   // BADEA_ADDONS.EMPLOYEES.EMP_ID
        public string Label { get; init; } = "";   // "NAME_ENG (EMP_ID)"
        public string? UserId { get; init; }       // optional for future
    }
}
