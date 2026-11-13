namespace VisionQC.Core;

/// <summary>
/// 무조작 시간 추적. 시계를 주입받아 시간 없이 테스트한다.
/// 검사·감시가 돌고 있는 동안(busy)에는 만료시키지 않는다 — 무인 연속 검사가 로그아웃으로 끊기지 않게 하기 위한 선택이며,
/// 그 대신 감시 중에는 로그인된 세션이 열려 있다는 점이 위험으로 남는다(문서에 명시).
/// </summary>
public sealed class IdleMonitor
{
    public const int DefaultMinutes = 15, MinMinutes = 1, MaxMinutes = 480;

    private readonly Func<DateTime> _now;
    private DateTime _last;

    public TimeSpan Timeout { get; }

    public IdleMonitor(TimeSpan timeout, Func<DateTime>? now = null)
    {
        if (timeout <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(timeout), "제한 시간은 0보다 커야 합니다.");
        Timeout = timeout;
        _now = now ?? (() => DateTime.UtcNow);
        _last = _now();
    }

    /// <summary>사용자가 조작했다(마우스·키보드).</summary>
    public void Touch() => _last = _now();

    public TimeSpan Idle => _now() - _last;

    /// <summary>busy(검사·감시 중)이면 만료되지 않는다. 한 번 busy가 끝나면 그 시점부터 다시 센다.</summary>
    public bool IsExpired(bool busy)
    {
        if (busy) { _last = _now(); return false; }
        return Idle >= Timeout;
    }

    public static bool IsValidMinutes(int minutes) => minutes >= MinMinutes && minutes <= MaxMinutes;
}
