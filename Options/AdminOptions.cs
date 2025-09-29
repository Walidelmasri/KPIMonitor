public sealed class AdminOptions
{
    public IReadOnlyCollection<string> AdminUsers { get; init; } = Array.Empty<string>();
    public IReadOnlyCollection<string> SuperAdminUsers { get; init; } = Array.Empty<string>();
}