using Vouch.Domain.Entities;
using Vouch.Domain.Services;
using Xunit;

namespace Vouch.UnitTests.DomainServices;

public class TrustScoreCalculatorTests
{
    [Fact]
    public void CalculateVouchWeight_NormalVoucher_ReturnsBaseWeight()
    {
        var voucher = new User
        {
            Email = "voucher@sab.ac.lk",
            PasswordHash = "hash",
            FullName = "John Doe",
            Faculty = "Computing",
            Department = "Software Engineering",
            CreatedAt = DateTimeOffset.UtcNow.AddDays(-30),
            ActiveVouchesReceivedCount = 2
        };

        var weight = TrustScoreCalculator.CalculateVouchWeight(voucher, mutualVouchersBetweenCircles: 0, DateTimeOffset.UtcNow);

        Assert.Equal(1.0, weight);
    }

    [Fact]
    public void CalculateVouchWeight_HighTrustVoucher_ReturnsBasePlusBonus()
    {
        var voucher = new User
        {
            Email = "veteran@sab.ac.lk",
            PasswordHash = "hash",
            FullName = "Veteran Peer",
            Faculty = "Computing",
            Department = "Software Engineering",
            CreatedAt = DateTimeOffset.UtcNow.AddDays(-30),
            ActiveVouchesReceivedCount = 5 // >= 5 threshold
        };

        var weight = TrustScoreCalculator.CalculateVouchWeight(voucher, mutualVouchersBetweenCircles: 0, DateTimeOffset.UtcNow);

        // 1.0 base + 0.2 bonus = 1.2
        Assert.Equal(1.2, weight);
    }

    [Fact]
    public void CalculateVouchWeight_VoucherRegisteredUnder14Days_ReturnsZeroWeight()
    {
        var voucher = new User
        {
            Email = "newbie@sab.ac.lk",
            PasswordHash = "hash",
            FullName = "New Peer",
            Faculty = "Computing",
            Department = "Software Engineering",
            CreatedAt = DateTimeOffset.UtcNow.AddDays(-10), // Under 14 days
            ActiveVouchesReceivedCount = 6
        };

        var weight = TrustScoreCalculator.CalculateVouchWeight(voucher, mutualVouchersBetweenCircles: 0, DateTimeOffset.UtcNow);

        // REQ-9: Vouches from users registered within past 14 days carry zero Trust Score weight
        Assert.Equal(0.0, weight);
    }

    [Fact]
    public void CalculateVouchWeight_CliqueDetected_Applies50PercentDampening()
    {
        var voucher = new User
        {
            Email = "clique@sab.ac.lk",
            PasswordHash = "hash",
            FullName = "Clique Friend",
            Faculty = "Computing",
            Department = "Software Engineering",
            CreatedAt = DateTimeOffset.UtcNow.AddDays(-40),
            ActiveVouchesReceivedCount = 6 // 1.2 base + bonus
        };

        // 3 mutual vouchers detected
        var weight = TrustScoreCalculator.CalculateVouchWeight(voucher, mutualVouchersBetweenCircles: 3, DateTimeOffset.UtcNow);

        // (1.0 + 0.2) * 0.5 = 0.6
        Assert.Equal(0.6, weight);
    }

    [Fact]
    public void ApplyScoreCap_Exceeds20_ReturnsMaxCap()
    {
        var cappedScore = TrustScoreCalculator.ApplyScoreCap(24.5);
        Assert.Equal(20.0, cappedScore);
    }
}
