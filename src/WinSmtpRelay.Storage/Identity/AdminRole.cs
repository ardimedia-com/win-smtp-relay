using Microsoft.AspNetCore.Identity;

namespace WinSmtpRelay.Storage.Identity;

public class AdminRole : IdentityRole<int>
{
    public AdminRole() { }

    public AdminRole(string roleName) : base(roleName) { }
}
