// ============================================================================
// Idle.cs - 무조작 자동 로그아웃용 타이머 로직(보안 기능)
//
// 파이프라인과는 별개로 앱의 사용자 인증(Auth.cs)을 보조한다. 공용 검사 PC에서 로그인한 채 자리를 비워
// 다른 사람이 권한(예: 설정 변경)을 쓰는 일을 막으려는 목적이다. UI 가 Touch()/IsExpired()를 호출하기만 하면 되도록
// 시간 계산만 이 클래스에 두고, 시계를 주입(Func<DateTime>)받아 시간을 기다리지 않고 단위 테스트할 수 있게 했다.
// ============================================================================
namespace VisionQC.Core;

/// <summary>
/// 무조작 시간 추적. 시계를 주입받아 시간 없이 테스트한다.
/// 검사·감시가 돌고 있는 동안(busy)에는 만료시키지 않는다 — 무인 연속 검사가 로그아웃으로 끊기지 않게 하기 위한 선택이며,
/// 그 대신 감시 중에는 로그인된 세션이 열려 있다는 점이 위험으로 남는다(문서에 명시).
/// </summary>
public sealed class IdleMonitor
{
    // 기본 15분, 허용 범위 1~480분(8시간). 너무 짧으면 작업 방해, 너무 길면 보안 의미가 없어 범위를 제한한다.
    public const int DefaultMinutes = 15, MinMinutes = 1, MaxMinutes = 480;

    private readonly Func<DateTime> _now;
    // 마지막 조작 시각(UTC).
    private DateTime _last;

    /// <summary>이 시간 이상 조작이 없으면 만료로 본다.</summary>
    public TimeSpan Timeout { get; }

    /// <summary>timeout 은 0보다 커야 한다. now 를 생략하면 실제 UTC 시계를 쓴다(테스트에서는 가짜 시계 주입).</summary>
    public IdleMonitor(TimeSpan timeout, Func<DateTime>? now = null)
    {
        if (timeout <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(timeout), "제한 시간은 0보다 커야 합니다.");
        Timeout = timeout;
        _now = now ?? (() => DateTime.UtcNow);
        _last = _now();
    }

    /// <summary>사용자가 조작했다(마우스·키보드).</summary>
    public void Touch() => _last = _now();

    /// <summary>마지막 조작 이후 흐른 시간.</summary>
    public TimeSpan Idle => _now() - _last;

    /// <summary>busy(검사·감시 중)이면 만료되지 않는다. 한 번 busy가 끝나면 그 시점부터 다시 센다.</summary>
    public bool IsExpired(bool busy)
    {
        if (busy) { _last = _now(); return false; }
        return Idle >= Timeout;
    }

    /// <summary>설정값(분)이 허용 범위 안인지 검사한다(설정 파일·입력 검증용).</summary>
    public static bool IsValidMinutes(int minutes) => minutes >= MinMinutes && minutes <= MaxMinutes;
}
