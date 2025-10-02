namespace KPIMonitor.Services.Auth
{
    public interface IAdAuthenticator
    {
        /// Returns normalized DOMAIN\sam on success; null on failure.
        Task<string?> ValidateAsync(string username, string password);
        /// <summary>
        /// Returns true if the user is a member of the allowed group (direct or nested).
        /// username can be SAM or UPN; password is required if binding as the user.
        /// </summary>
        Task<bool> IsMemberOfAllowedGroupAsync(string username, string password, CancellationToken ct = default);
    
    }
}
