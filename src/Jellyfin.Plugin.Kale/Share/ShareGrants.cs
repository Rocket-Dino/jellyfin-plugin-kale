using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;

namespace Jellyfin.Plugin.Kale.Share;

/// <summary>
/// One Kale Share invitation's sign-in (kale#331, ADR-020). The owner's app makes a random secret,
/// puts it in the invitation link and registers only its SHA-256 here, bound to the guest's
/// Jellyfin account. The guest's app redeems the secret once, through Kale Share, for a Jellyfin
/// session of its own: no password travels and the owner does not have to approve a code.
/// </summary>
public sealed class ShareGrant
{
    public Guid Id { get; set; }

    public Guid UserId { get; set; }

    /// <summary>Lowercase hex SHA-256 of the secret's raw bytes. The secret itself is never stored.</summary>
    public string SecretHash { get; set; } = string.Empty;

    /// <summary>Who the invitation is for, as the owner typed it. Shown back to the owner only.</summary>
    public string Label { get; set; } = string.Empty;

    public DateTime CreatedUtc { get; set; }

    public DateTime ExpiresUtc { get; set; }

    public DateTime? RedeemedUtc { get; set; }

    public string? RedeemedDeviceId { get; set; }

    public string? RedeemedDeviceName { get; set; }

    /// <summary>
    /// The invitation's keys for the guest's other devices, sealed by the owner's app with a key derived
    /// from the secret (AES-GCM). Opaque here: the server cannot read them. Handed back only on redemption,
    /// so the link itself carries one key however many devices it admits.
    /// </summary>
    public string? Spares { get; set; }

    /// <summary>
    /// Set on a guest's own grant (<see cref="ShareGrants.CreateSelf"/>): the device that made it, for
    /// another of the same person's devices. Null on an invitation the owner made.
    /// </summary>
    public string? FromDevice { get; set; }
}

public enum SelfGrantOutcome
{
    Created,
    Invalid,
    TooMany,
}

public enum RedeemOutcome
{
    SignIn,
    Unknown,
    Expired,
    Used,
}

/// <summary>
/// The grants and their rules, apart from Jellyfin so they can be tested on their own. Persistence is
/// the plugin's configuration (<see cref="Configuration.PluginConfiguration.ShareGrants"/>), through
/// the two delegates.
/// </summary>
public sealed class ShareGrants
{
    /// <summary>Longest invitation the plugin accepts. The app offers one day to one week.</summary>
    public static readonly TimeSpan MaxLifetime = TimeSpan.FromDays(30);

    /// <summary>
    /// The device that redeemed a grant may redeem it again for this long: a phone that lost the
    /// answer (the connection dropped, the app was killed) must not burn the invitation.
    /// </summary>
    public static readonly TimeSpan RetryWindow = TimeSpan.FromMinutes(15);

    /// <summary>Largest sealed spares blob accepted (five keys sealed are well under 1 KB).</summary>
    public const int MaxSparesLength = 4096;

    /// <summary>
    /// A guest's own grant lives one day: the app's hand-off window (KaleShareAddDevice.approvalWindow).
    /// The server decides it; the caller cannot ask for longer.
    /// </summary>
    public static readonly TimeSpan SelfLifetime = TimeSpan.FromHours(24);

    /// <summary>Open own grants one person may hold at once: the per-invitation device cap (ADR-020 §3).</summary>
    public const int MaxOpenSelfGrants = 5;

    /// <summary>How long a finished grant stays listed for the owner before it is dropped.</summary>
    public static readonly TimeSpan KeepFinished = TimeSpan.FromDays(30);

    private readonly Func<List<ShareGrant>> _load;
    private readonly Action<List<ShareGrant>> _save;
    private readonly Func<DateTime> _now;
    private readonly object _gate = new();

    public ShareGrants(Func<List<ShareGrant>> load, Action<List<ShareGrant>> save, Func<DateTime> now)
    {
        _load = load;
        _save = save;
        _now = now;
    }

    /// <summary>Lowercase hex SHA-256 of a secret given as hex; null when it is not hex of 16 to 64 bytes.</summary>
    public static string? HashOf(string secretHex)
    {
        if (secretHex.Length is < 32 or > 128 || secretHex.Length % 2 != 0)
        {
            return null;
        }

        byte[] raw;
        try
        {
            raw = Convert.FromHexString(secretHex);
        }
        catch (FormatException)
        {
            return null;
        }

        return Convert.ToHexString(SHA256.HashData(raw)).ToLowerInvariant();
    }

    public static bool IsHash(string value) =>
        value.Length == 64 && value.All(c => c is (>= '0' and <= '9') or (>= 'a' and <= 'f'));

    /// <returns>The new grant, or null when the hash or the expiry is not acceptable.</returns>
    public ShareGrant? Create(Guid userId, string secretHash, string label, DateTime expiresUtc)
    {
        var now = _now();
        var hash = secretHash.Trim().ToLowerInvariant();
        if (userId == Guid.Empty || !IsHash(hash) || expiresUtc <= now || expiresUtc > now + MaxLifetime)
        {
            return null;
        }

        lock (_gate)
        {
            var grants = Pruned(_load(), now);
            if (grants.Any(g => g.SecretHash == hash))
            {
                return null;
            }

            var grant = new ShareGrant
            {
                Id = Guid.NewGuid(),
                UserId = userId,
                SecretHash = hash,
                Label = label.Trim(),
                CreatedUtc = now,
                ExpiresUtc = expiresUtc,
            };
            grants.Add(grant);
            _save(grants);
            return grant;
        }
    }

    /// <summary>
    /// A signed-in person's one-time sign-in for another of THEIR OWN devices (Add Another Device):
    /// always for <paramref name="userId"/> (the caller, never a body field), one day, single use, and
    /// at most <see cref="MaxOpenSelfGrants"/> open at once. Labelled like the owner's newest invitation
    /// for the same account, so the owner's app files it under the same guest; else
    /// <paramref name="fallbackLabel"/> (the account's name).
    /// </summary>
    public (SelfGrantOutcome Outcome, ShareGrant? Grant) CreateSelf(Guid userId, string secretHash, string fromDevice, string fallbackLabel)
    {
        var now = _now();
        var hash = secretHash.Trim().ToLowerInvariant();
        var device = fromDevice.Trim();
        if (userId == Guid.Empty || !IsHash(hash) || device.Length == 0)
        {
            return (SelfGrantOutcome.Invalid, null);
        }

        lock (_gate)
        {
            var grants = Pruned(_load(), now);
            if (grants.Any(g => g.SecretHash == hash))
            {
                return (SelfGrantOutcome.Invalid, null);
            }

            var open = grants.Count(g => g.UserId == userId && g.FromDevice is not null && g.RedeemedUtc is null && g.ExpiresUtc > now);
            if (open >= MaxOpenSelfGrants)
            {
                return (SelfGrantOutcome.TooMany, null);
            }

            var label = grants.Where(g => g.UserId == userId && g.FromDevice is null)
                .OrderByDescending(g => g.CreatedUtc)
                .Select(g => g.Label)
                .FirstOrDefault(l => !string.IsNullOrWhiteSpace(l)) ?? fallbackLabel.Trim();
            var grant = new ShareGrant
            {
                Id = Guid.NewGuid(),
                UserId = userId,
                SecretHash = hash,
                Label = label,
                CreatedUtc = now,
                ExpiresUtc = now + SelfLifetime,
                FromDevice = device.Length > 100 ? device[..100] : device,
            };
            grants.Add(grant);
            _save(grants);
            return (SelfGrantOutcome.Created, grant);
        }
    }

    /// <summary>One person's own grants: what their devices made, never the owner's invitations.</summary>
    public IReadOnlyList<ShareGrant> ListOwn(Guid userId) =>
        List().Where(g => g.UserId == userId && g.FromDevice is not null).ToList();

    /// <summary>Withdraw one of this person's own grants. Another person's, or an owner's invitation, is not theirs.</summary>
    public bool WithdrawOwn(Guid userId, Guid id)
    {
        lock (_gate)
        {
            var grants = _load();
            var removed = grants.RemoveAll(g => g.Id == id && g.UserId == userId && g.FromDevice is not null) > 0;
            if (removed)
            {
                _save(grants);
            }

            return removed;
        }
    }

    public IReadOnlyList<ShareGrant> List()
    {
        lock (_gate)
        {
            var all = _load();
            var grants = Pruned(all, _now());
            if (grants.Count != all.Count)
            {
                _save(grants);
            }

            return grants;
        }
    }

    /// <summary>Attach the sealed spares to a grant not yet redeemed.</summary>
    public bool SetSpares(Guid id, string spares)
    {
        if (spares.Length is 0 or > MaxSparesLength)
        {
            return false;
        }

        lock (_gate)
        {
            var grants = _load();
            var grant = grants.FirstOrDefault(g => g.Id == id);
            if (grant is null || grant.RedeemedUtc is not null)
            {
                return false;
            }

            grant.Spares = spares;
            _save(grants);
            return true;
        }
    }

    public bool Withdraw(Guid id)
    {
        lock (_gate)
        {
            var grants = _load();
            var removed = grants.RemoveAll(g => g.Id == id) > 0;
            if (removed)
            {
                _save(grants);
            }

            return removed;
        }
    }

    /// <summary>
    /// Use the grant this secret opens, for this device. Single use, except that the same device may
    /// repeat a redemption within <see cref="RetryWindow"/>. <c>Fresh</c> is true only when this call
    /// used the grant; only then may a refused sign-in <see cref="Unredeem"/> it.
    /// </summary>
    public (RedeemOutcome Outcome, ShareGrant? Grant, bool Fresh) Redeem(string secretHex, string deviceId, string deviceName)
    {
        var hash = HashOf(secretHex);
        if (hash is null || string.IsNullOrEmpty(deviceId))
        {
            return (RedeemOutcome.Unknown, null, false);
        }

        var now = _now();
        lock (_gate)
        {
            var grants = _load();
            // Compare every stored hash in fixed time; never stop at the first match.
            var wanted = Convert.FromHexString(hash);
            ShareGrant? grant = null;
            foreach (var g in grants)
            {
                if (CryptographicOperations.FixedTimeEquals(Convert.FromHexString(g.SecretHash), wanted))
                {
                    grant = g;
                }
            }

            if (grant is null)
            {
                return (RedeemOutcome.Unknown, null, false);
            }

            if (grant.RedeemedUtc is { } at)
            {
                return grant.RedeemedDeviceId == deviceId && now - at <= RetryWindow
                    ? (RedeemOutcome.SignIn, grant, false)
                    : (RedeemOutcome.Used, grant, false);
            }

            if (now >= grant.ExpiresUtc)
            {
                return (RedeemOutcome.Expired, grant, false);
            }

            grant.RedeemedUtc = now;
            grant.RedeemedDeviceId = deviceId;
            grant.RedeemedDeviceName = deviceName;
            _save(grants);
            return (RedeemOutcome.SignIn, grant, true);
        }
    }

    /// <summary>
    /// Put a redemption back when Jellyfin then refused the sign-in (account disabled, device not
    /// allowed): the invitation was not used, so it stays usable once the owner fixes the account.
    /// </summary>
    public void Unredeem(Guid id)
    {
        lock (_gate)
        {
            var grants = _load();
            var grant = grants.FirstOrDefault(g => g.Id == id);
            if (grant is null || grant.RedeemedUtc is null)
            {
                return;
            }

            grant.RedeemedUtc = null;
            grant.RedeemedDeviceId = null;
            grant.RedeemedDeviceName = null;
            _save(grants);
        }
    }

    private static List<ShareGrant> Pruned(List<ShareGrant> grants, DateTime now) =>
        grants.Where(g => (g.RedeemedUtc ?? g.ExpiresUtc) + KeepFinished > now).ToList();
}
