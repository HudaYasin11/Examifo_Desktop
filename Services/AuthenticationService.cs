namespace Examifo_Desktop.Services;

public class AuthenticationService
{
    public Task<bool> LoginAsync(string email, string password)
    {
        // Temporary mock login.
        // This will be replaced with the real Examifo API later.

        bool valid =
            email.Trim().Equals(
                "student@examifo.com",
                StringComparison.OrdinalIgnoreCase)
            && password == "123456";

        return Task.FromResult(valid);
    }
}