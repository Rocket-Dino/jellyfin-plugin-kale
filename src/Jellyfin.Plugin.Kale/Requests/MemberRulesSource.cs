using System.Linq;
using Jellyfin.Database.Implementations.Entities;
using MediaBrowser.Common.Extensions;
using MediaBrowser.Controller;
using MediaBrowser.Controller.Configuration;
using MediaBrowser.Model.Globalization;

namespace Jellyfin.Plugin.Kale.Requests;

/// <summary>
/// Builds a member's <see cref="MemberRules"/> from the server's own records: their Kale discovery
/// limits in <c>DisplayPreferences</c> (<c>usersettings</c>, client <c>kale</c>, read the way
/// Jellyfin's <c>GET /DisplayPreferences/usersettings</c> reads them), their library cap
/// (<c>MaxParentalRatingScore</c>), the server's rating ladder and its <c>MetadataCountryCode</c>.
/// Nothing the device says is trusted for any of these.
/// </summary>
public class MemberRulesSource
{
    private readonly IDisplayPreferencesManager _preferences;
    private readonly ILocalizationManager _localization;
    private readonly IServerConfigurationManager _configuration;

    public MemberRulesSource(IDisplayPreferencesManager preferences, ILocalizationManager localization, IServerConfigurationManager configuration)
    {
        _preferences = preferences;
        _localization = localization;
        _configuration = configuration;
    }

    public MemberRules For(User user)
    {
        var custom = _preferences.ListCustomItemDisplayPreferences(user.Id, DiscoveryLimits.PreferencesId.GetMD5(), DiscoveryLimits.Client);
        var limits = DiscoveryLimits.FromCustomPrefs(custom);
        var ladder = new CertificationLadder(_localization.GetParentalRatings().Select(r => (r.Name, r.Value ?? r.RatingScore?.Score)));
        return new MemberRules(limits, user.MaxParentalRatingScore, ladder, _configuration.Configuration.MetadataCountryCode ?? string.Empty);
    }
}
