using Microsoft.AspNetCore.Mvc.ApplicationModels;
using Microsoft.AspNetCore.Mvc.Authorization;

public sealed class ProtectCreateActionsConvention : IApplicationModelConvention
{
    private static readonly string[] Names =
        { "Create", "New", "Add", "Import", "Clone", "BulkCreate" };

    public void Apply(ApplicationModel app)
    {
        foreach (var c in app.Controllers)
        foreach (var a in c.Actions)
        {
            if (Names.Contains(a.ActionMethod.Name, StringComparer.OrdinalIgnoreCase))
            {
                // Require the "AdminOnly" policy on these actions
                a.Filters.Add(new AuthorizeFilter("AdminOnly"));
            }
        }
    }
}
