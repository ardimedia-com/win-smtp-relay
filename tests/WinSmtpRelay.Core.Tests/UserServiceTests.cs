using Microsoft.EntityFrameworkCore;
using WinSmtpRelay.Storage;

namespace WinSmtpRelay.Core.Tests;

[TestClass]
public class UserServiceTests
{
    private RelayDbContext _db = null!;
    private UserService _sut = null!;

    [TestInitialize]
    public void Setup()
    {
        var options = new DbContextOptionsBuilder<RelayDbContext>()
            .UseSqlite("Data Source=:memory:")
            .Options;

        _db = new RelayDbContext(options, new CurrentTenant());
        _db.Database.OpenConnection();
        _db.Database.EnsureCreated();
        _sut = new UserService(_db);
    }

    [TestCleanup]
    public void Cleanup()
    {
        _db.Database.CloseConnection();
        _db.Dispose();
    }

    [TestMethod]
    [TestCategory("Unit")]
    public async Task CreateUser_StoresBcryptHash()
    {
        await _sut.CreateUserAsync("alice", "P@ssw0rd!");

        var user = await _db.RelayUsers.FirstOrDefaultAsync(u => u.Username == "alice");
        Assert.IsNotNull(user);
        Assert.IsTrue(user.PasswordHash.StartsWith("$2"), "Password should be bcrypt hashed");
        Assert.AreNotEqual("P@ssw0rd!", user.PasswordHash);
    }

    [TestMethod]
    [TestCategory("Unit")]
    public async Task ValidateCredentials_CorrectPassword_ReturnsTrue()
    {
        await _sut.CreateUserAsync("bob", "secret123");

        var result = await _sut.ValidateCredentialsAsync("bob", "secret123");
        Assert.IsTrue(result);
    }

    [TestMethod]
    [TestCategory("Unit")]
    public async Task ValidateCredentials_WrongPassword_ReturnsFalse()
    {
        await _sut.CreateUserAsync("charlie", "correct");

        var result = await _sut.ValidateCredentialsAsync("charlie", "wrong");
        Assert.IsFalse(result);
    }

    [TestMethod]
    [TestCategory("Unit")]
    public async Task ValidateCredentials_NonExistentUser_ReturnsFalse()
    {
        var result = await _sut.ValidateCredentialsAsync("nobody", "password");
        Assert.IsFalse(result);
    }

    [TestMethod]
    [TestCategory("Unit")]
    public async Task ValidateCredentials_DisabledUser_ReturnsFalse()
    {
        await _sut.CreateUserAsync("disabled_user", "password");
        var user = await _db.RelayUsers.FirstAsync(u => u.Username == "disabled_user");
        user.IsEnabled = false;
        await _db.SaveChangesAsync();

        var result = await _sut.ValidateCredentialsAsync("disabled_user", "password");
        Assert.IsFalse(result);
    }

    [TestMethod]
    [TestCategory("Unit")]
    public async Task GetByUsername_ExistingUser_ReturnsUser()
    {
        await _sut.CreateUserAsync("dave", "password");

        var user = await _sut.GetByUsernameAsync("dave");
        Assert.IsNotNull(user);
        Assert.AreEqual("dave", user.Username);
    }

    [TestMethod]
    [TestCategory("Unit")]
    public async Task GetByUsername_NonExistent_ReturnsNull()
    {
        var user = await _sut.GetByUsernameAsync("nobody");
        Assert.IsNull(user);
    }

    [TestMethod]
    [TestCategory("Unit")]
    public async Task GetAllUsers_ReturnsAllCreatedUsers()
    {
        await _sut.CreateUserAsync("user1", "pass1");
        await _sut.CreateUserAsync("user2", "pass2");

        var users = await _sut.GetAllUsersAsync();
        Assert.AreEqual(2, users.Count);
    }

    [TestMethod]
    [TestCategory("Unit")]
    public async Task DeleteUser_RemovesUser()
    {
        await _sut.CreateUserAsync("to_delete", "password");
        var user = await _db.RelayUsers.FirstAsync(u => u.Username == "to_delete");

        await _sut.DeleteUserAsync(user.Id);
        _db.ChangeTracker.Clear();

        var deleted = await _db.RelayUsers.FirstOrDefaultAsync(u => u.Username == "to_delete");
        Assert.IsNull(deleted);
    }
}
