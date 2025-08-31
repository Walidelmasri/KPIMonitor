namespace KPIMonitor.Services.Auth
{
    public interface IAdAuthenticator
    {
        /// Returns normalized DOMAIN\sam on success; null on failure.
        Task<string?> ValidateAsync(string username, string password);
    }
}
