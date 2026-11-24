using Sentinel.Persistence.Common;
using Xunit;

namespace Sentinel.UnitTests;

public class IdempotencyHelperTests
{
    [Fact]
    public void ComputeFingerprint_IdenticalArgs_ReturnsSameHash()
    {
        var args1 = new { WorkspaceId = Guid.Parse("11111111-1111-1111-1111-111111111111"), Title = "Test Title", ExpectedVersion = 0 };
        var args2 = new { WorkspaceId = Guid.Parse("11111111-1111-1111-1111-111111111111"), Title = "Test Title", ExpectedVersion = 0 };

        var hash1 = IdempotencyHelper.ComputeFingerprint(args1);
        var hash2 = IdempotencyHelper.ComputeFingerprint(args2);

        Assert.Equal(hash1, hash2);
    }

    [Fact]
    public void ComputeFingerprint_DifferentArgs_ReturnsDifferentHash()
    {
        var args1 = new { Title = "Version 1" };
        var args2 = new { Title = "Version 2" };

        var hash1 = IdempotencyHelper.ComputeFingerprint(args1);
        var hash2 = IdempotencyHelper.ComputeFingerprint(args2);

        Assert.NotEqual(hash1, hash2);
    }
}
