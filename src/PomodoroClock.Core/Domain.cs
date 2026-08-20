using System.Collections.ObjectModel;

namespace PomodoroClock.Core;

public enum TimerPhase { Focus, ShortBreak, LongBreak }
public enum TimerStatus { Ready, Running, AwaitingSettlement }
public enum WorkItemStatus { InProgress, Completed }
public enum WorkItemPriority { Low = 0, Normal = 1, High = 2 }

public sealed record TimerSettings(int FocusMinutes = 25, int ShortBreakMinutes = 5, int LongBreakMinutes = 15, int RoundsBeforeLongBreak = 4)
{
    public void Validate()
    {
        if (FocusMinutes is < 1 or > 120) throw new ArgumentOutOfRangeException(nameof(FocusMinutes));
        if (ShortBreakMinutes is < 1 or > 60) throw new ArgumentOutOfRangeException(nameof(ShortBreakMinutes));
        if (LongBreakMinutes is < 1 or > 120) throw new ArgumentOutOfRangeException(nameof(LongBreakMinutes));
        if (RoundsBeforeLongBreak is < 1 or > 12) throw new ArgumentOutOfRangeException(nameof(RoundsBeforeLongBreak));
    }

    public TimeSpan Duration(TimerPhase phase) => TimeSpan.FromMinutes(phase switch
    {
        TimerPhase.Focus => FocusMinutes,
        TimerPhase.ShortBreak => ShortBreakMinutes,
        TimerPhase.LongBreak => LongBreakMinutes,
        _ => FocusMinutes
    });
}

public sealed record TimerSnapshot(TimerPhase Phase, TimerStatus Status, DateTimeOffset? StartedAt, DateTimeOffset? TargetEnd, TimeSpan Remaining, int CompletedRounds, bool IsLongBreakDue)
{
    public static TimerSnapshot Initial(int rounds = 0) => new(TimerPhase.Focus, TimerStatus.Ready, null, null, TimeSpan.Zero, rounds, false);
}

public interface IClock { DateTimeOffset UtcNow { get; } }
public sealed class SystemClock : IClock { public DateTimeOffset UtcNow => DateTimeOffset.UtcNow; }

public sealed class PomodoroTimer
{
    private readonly IClock _clock;
    private TimerSettings _settings;
    private TimerSnapshot _snapshot;
    public event EventHandler<TimerSnapshot>? Changed;
    public TimerSnapshot Snapshot => _snapshot;

    public PomodoroTimer(IClock? clock = null, TimerSettings? settings = null, int completedRounds = 0)
    {
        _clock = clock ?? new SystemClock();
        _settings = settings ?? new TimerSettings();
        _settings.Validate();
        _snapshot = TimerSnapshot.Initial(completedRounds) with { Remaining = _settings.Duration(TimerPhase.Focus) };
    }

    public void UpdateSettings(TimerSettings settings) { settings.Validate(); _settings = settings; }

    public bool Start()
    {
        if (_snapshot.Status != TimerStatus.Ready) return false;
        var now = _clock.UtcNow;
        var due = _snapshot.Phase == TimerPhase.LongBreak || _snapshot.Phase == TimerPhase.ShortBreak;
        _snapshot = _snapshot with { Status = TimerStatus.Running, StartedAt = now, TargetEnd = now + _settings.Duration(_snapshot.Phase), Remaining = _settings.Duration(_snapshot.Phase), IsLongBreakDue = due };
        Changed?.Invoke(this, _snapshot); return true;
    }

    public TimerSnapshot Tick()
    {
        if (_snapshot.Status != TimerStatus.Running || _snapshot.TargetEnd is null) return _snapshot;
        var remaining = _snapshot.TargetEnd.Value - _clock.UtcNow;
        if (remaining > TimeSpan.Zero) { _snapshot = _snapshot with { Remaining = remaining }; Changed?.Invoke(this, _snapshot); return _snapshot; }
        _snapshot = _snapshot with { Status = _snapshot.Phase == TimerPhase.Focus ? TimerStatus.AwaitingSettlement : TimerStatus.Ready, Remaining = TimeSpan.Zero };
        Changed?.Invoke(this, _snapshot); return _snapshot;
    }

    public bool Cancel() { if (_snapshot.Status != TimerStatus.Running) return false; _snapshot = _snapshot with { Status = TimerStatus.Ready, StartedAt = null, TargetEnd = null, Remaining = TimeSpan.Zero }; Changed?.Invoke(this, _snapshot); return true; }
    public bool SkipBreak() { if (_snapshot.Status != TimerStatus.Ready || _snapshot.Phase == TimerPhase.Focus) return false; SetFocus(); return true; }
    public void SetFocus() => SetPhase(TimerPhase.Focus);
    public void SetShortBreak() => SetPhase(TimerPhase.ShortBreak);
    public void SetLongBreak() => SetPhase(TimerPhase.LongBreak);
    public void CompleteFocusAndSetNextPhase(bool longBreak)
    {
        if (_snapshot.Status != TimerStatus.AwaitingSettlement || _snapshot.Phase != TimerPhase.Focus) throw new InvalidOperationException("当前没有待结算的专注轮次。");
        var rounds = _snapshot.CompletedRounds + 1;
        _snapshot = _snapshot with { CompletedRounds = rounds, Phase = longBreak ? TimerPhase.LongBreak : TimerPhase.ShortBreak, Status = TimerStatus.Ready, StartedAt = null, TargetEnd = null, Remaining = TimeSpan.Zero, IsLongBreakDue = longBreak };
        Changed?.Invoke(this, _snapshot);
    }
    public void FinishBreak() { if (_snapshot.Status != TimerStatus.Ready || (_snapshot.Phase != TimerPhase.ShortBreak && _snapshot.Phase != TimerPhase.LongBreak)) return; SetFocus(); }

    private void SetPhase(TimerPhase phase) { _snapshot = _snapshot with { Phase = phase, Status = TimerStatus.Ready, StartedAt = null, TargetEnd = null, Remaining = TimeSpan.Zero, IsLongBreakDue = phase == TimerPhase.LongBreak }; Changed?.Invoke(this, _snapshot); }
}

public sealed record WorkItem(Guid Id, string Title, string Notes, WorkItemStatus Status, WorkItemPriority Priority, DateTimeOffset CreatedUtc, DateTimeOffset? CompletedUtc, int ParticipationCount, bool IsArchived = false);
public sealed record FocusSession(Guid Id, DateTimeOffset StartedUtc, DateTimeOffset EndedUtc, int DurationSeconds);
public sealed record SessionLink(Guid SessionId, Guid WorkItemId, string TitleSnapshot);
public sealed record DailySummary(DateOnly Date, int Pomodoros, int FocusMinutes, int CompletedItems);
public sealed record SessionDetails(FocusSession Session, IReadOnlyList<string> WorkItems);
public sealed record StatisticsSummary(DateOnly From, DateOnly To, int Pomodoros, int FocusMinutes, int CompletedItems, IReadOnlyList<DailySummary> Days, IReadOnlyList<SessionDetails> Sessions);

public interface IWorkItemRepository
{
    Task<IReadOnlyList<WorkItem>> GetAsync(bool includeArchived = false, CancellationToken cancellationToken = default);
    Task<WorkItem> SaveAsync(WorkItem item, CancellationToken cancellationToken = default);
    Task SetCompletedAsync(Guid id, bool completed, CancellationToken cancellationToken = default);
    Task ArchiveAsync(Guid id, CancellationToken cancellationToken = default);
}

public interface IFocusSessionRepository
{
    Task SaveSettlementAsync(FocusSession session, IReadOnlyCollection<Guid> workItemIds, string? newTitle, int completedRounds, bool longBreakDue, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<SessionDetails>> GetSessionsAsync(DateOnly from, DateOnly to, CancellationToken cancellationToken = default);
}

public interface IStatisticsService
{
    Task<StatisticsSummary> GetAsync(DateOnly from, DateOnly to, CancellationToken cancellationToken = default);
}

public interface IAppSettingsRepository
{
    Task<TimerSettings> GetTimerSettingsAsync(CancellationToken cancellationToken = default);
    Task SaveTimerSettingsAsync(TimerSettings settings, CancellationToken cancellationToken = default);
    Task<int> GetCompletedRoundsAsync(CancellationToken cancellationToken = default);
}
