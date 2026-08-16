using IndieFi.Web.Data;
using IndieFi.Web.Modules.Accounts.Application;
using IndieFi.Web.Modules.Notifications.Domain;
using Microsoft.EntityFrameworkCore;

namespace IndieFi.Web.Modules.Notifications.Application;

public sealed record NotificationPreferenceSelection(
    NotificationKind Kind,
    bool WebEnabled,
    bool EmailEnabled);

public sealed record NotificationPreferenceView(
    NotificationKind Kind,
    bool WebEnabled,
    bool EmailEnabled,
    bool DefaultWebEnabled,
    bool DefaultEmailEnabled,
    bool IsMandatory,
    bool IsOverridden);

public enum NotificationPreferenceSaveOutcome
{
    Saved,
    InvalidSelection,
    AccountUnavailable,
}

public sealed class NotificationPreferenceService
{
    private static readonly NotificationKind[] SupportedKinds =
        Enum.GetValues<NotificationKind>();

    private readonly AppDbContext _db;
    private readonly TimeProvider _timeProvider;

    public NotificationPreferenceService(
        AppDbContext db,
        TimeProvider timeProvider)
    {
        _db = db;
        _timeProvider = timeProvider;
    }

    public async Task<IReadOnlyList<NotificationPreferenceView>> GetAsync(
        long userId,
        CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(userId);

        var overrides = await _db.NotificationPreferences
            .AsNoTracking()
            .Where(item => item.UserId == userId)
            .ToDictionaryAsync(item => item.Kind, cancellationToken);

        return SupportedKinds
            .Select(kind => CreateView(
                kind,
                overrides.GetValueOrDefault(kind)))
            .ToArray();
    }

    public async Task<NotificationPreferenceSaveOutcome> SaveAsync(
        long userId,
        IReadOnlyCollection<NotificationPreferenceSelection> selections,
        CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(userId);
        ArgumentNullException.ThrowIfNull(selections);

        if (!TryNormalizeSelections(selections, out var normalized))
        {
            return NotificationPreferenceSaveOutcome.InvalidSelection;
        }

        await using var transaction =
            await AccountMutationGuard.BeginTransactionIfNeededAsync(
                _db,
                cancellationToken);
        if (!await AccountMutationGuard.LockActiveAsync(
                _db,
                userId,
                cancellationToken))
        {
            await AccountMutationGuard.CommitIfOwnedAsync(
                transaction,
                cancellationToken);
            return NotificationPreferenceSaveOutcome.AccountUnavailable;
        }

        var existing = await _db.NotificationPreferences
            .Where(item => item.UserId == userId)
            .ToDictionaryAsync(item => item.Kind, cancellationToken);
        var updatedAt = _timeProvider.GetUtcNow();

        foreach (var selection in normalized)
        {
            if (!existing.TryGetValue(selection.Kind, out var preference))
            {
                preference = new NotificationPreference
                {
                    UserId = userId,
                    Kind = selection.Kind,
                };
                _db.NotificationPreferences.Add(preference);
            }

            preference.WebEnabled = selection.WebEnabled;
            preference.EmailEnabled = selection.EmailEnabled;
            preference.UpdatedAt = updatedAt;
        }

        await _db.SaveChangesAsync(cancellationToken);
        await AccountMutationGuard.CommitIfOwnedAsync(
            transaction,
            cancellationToken);
        return NotificationPreferenceSaveOutcome.Saved;
    }

    private static NotificationPreferenceView CreateView(
        NotificationKind kind,
        NotificationPreference? preference)
    {
        var defaults = NotificationPolicy.GetDefaults(kind);
        if (defaults.IsMandatory)
        {
            return new NotificationPreferenceView(
                kind,
                defaults.WebEnabled,
                defaults.EmailEnabled,
                defaults.WebEnabled,
                defaults.EmailEnabled,
                IsMandatory: true,
                IsOverridden: preference is not null);
        }

        return new NotificationPreferenceView(
            kind,
            preference?.WebEnabled ?? defaults.WebEnabled,
            preference?.EmailEnabled ?? defaults.EmailEnabled,
            defaults.WebEnabled,
            defaults.EmailEnabled,
            IsMandatory: false,
            IsOverridden: preference is not null);
    }

    private static bool TryNormalizeSelections(
        IReadOnlyCollection<NotificationPreferenceSelection> selections,
        out NotificationPreferenceSelection[] normalized)
    {
        normalized = [];
        if (selections.Count != SupportedKinds.Length)
        {
            return false;
        }

        var byKind = new Dictionary<NotificationKind, NotificationPreferenceSelection>();
        foreach (var selection in selections)
        {
            if (!Enum.IsDefined(selection.Kind)
                || !byKind.TryAdd(selection.Kind, selection))
            {
                return false;
            }
        }

        if (SupportedKinds.Any(kind => !byKind.ContainsKey(kind)))
        {
            return false;
        }

        normalized = SupportedKinds
            .Select(kind =>
            {
                var selection = byKind[kind];
                var defaults = NotificationPolicy.GetDefaults(kind);
                return defaults.IsMandatory
                    ? new NotificationPreferenceSelection(
                        kind,
                        defaults.WebEnabled,
                        defaults.EmailEnabled)
                    : selection;
            })
            .ToArray();
        return true;
    }
}
