// ============================================================================
// HistoryExport.cs - 이력 화면용 기간 변환과 CSV 내보내기
//
// 파이프라인에서의 위치: 저장소에 쌓인 검사 결과(InspectionResult)를 사람이 보고(기간 조회) 파일로 꺼내는(CSV) 후단 기능이다.
// 저장소는 시각을 모두 UTC로 보관하므로 화면의 "로컬 날짜"를 UTC 범위로 바꾸는 변환이 필요하고,
// CSV 는 Excel 에서 열 때 보안 문제(수식 주입)가 생기지 않도록 만든다.
// ============================================================================
using System.Globalization;

namespace VisionQC.Core;

/// <summary>화면의 로컬 달력 날짜를 저장소의 UTC 범위로 바꾼다. 종료일 전체를 포함한다.</summary>
public static class HistoryPeriod
{
    /// <summary>
    /// 로컬 날짜 구간 [from, to]를 UTC 시각 구간으로 바꾼다. null 은 "제한 없음".
    /// 시작은 시작일 00:00:00, 종료는 종료일 23:59:59.9999999(하루의 마지막 틱)로 잡아 종료일 하루 전체를 포함한다.
    /// 시작일이 종료일보다 늦으면 ArgumentException.
    /// </summary>
    public static (DateTime? FromUtc, DateTime? ToUtc) ToUtc(DateTime? from, DateTime? to, TimeZoneInfo zone)
    {
        if (from.HasValue && to.HasValue && from.Value.Date > to.Value.Date)
            throw new ArgumentException("시작일은 종료일보다 늦을 수 없습니다.");
        // Unspecified 로 지정하는 이유: ConvertTimeToUtc 는 Kind 가 Local/Utc 이면 zone 인자와 충돌해 예외를 낼 수 있어,
        // "이 시간대의 벽시계 시각"으로 해석시키려고 종류를 비워 둔다.
        DateTime? start = from.HasValue ? TimeZoneInfo.ConvertTimeToUtc(DateTime.SpecifyKind(from.Value.Date, DateTimeKind.Unspecified), zone) : null;
        DateTime? end = to.HasValue ? TimeZoneInfo.ConvertTimeToUtc(DateTime.SpecifyKind(to.Value.Date.AddTicks(TimeSpan.TicksPerDay - 1), DateTimeKind.Unspecified), zone) : null;
        return (start, end);
    }
}

/// <summary>현재 화면에 조회된 원 판정을 RFC 4180 형식으로 내보낸다. 정정 판정과 혼동하지 않는다.</summary>
// 열 구성: 결과ID, 작업ID, 검사시각(UTC, ISO 8601 "O"), 원 판정, 불량 점수, 처리시간(ms), 이미지 경로, 오류 메시지.
// 사람이 정정한 판정(Review)은 포함하지 않고 모델의 "원 판정"만 내보낸다.
public static class HistoryCsv
{
    /// <summary>rows 를 CSV 로 writer 에 쓴다. 줄바꿈은 RFC 4180 규약대로 CRLF, 모든 값은 큰따옴표로 감싼다.</summary>
    public static void Write(TextWriter writer, IReadOnlyList<InspectionResult> rows)
    {
        writer.Write("result_id,job_id,inspected_at_utc,original_verdict,defect_score,elapsed_ms,image_path,error_message\r\n");
        foreach (var r in rows)
        {
            // 숫자·날짜는 InvariantCulture 로 써서 PC 지역 설정(쉼표/마침표 소수점)과 상관없이 같은 형식이 되게 한다. 점수 "R"은 왕복 변환 가능한 정밀도.
            var cells = new[] { r.Id.ToString(CultureInfo.InvariantCulture), r.JobId.ToString(CultureInfo.InvariantCulture),
                r.InspectedAtUtc.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture), r.Verdict.ToString(),
                r.Score.ToString("R", CultureInfo.InvariantCulture), r.ElapsedMs.ToString(CultureInfo.InvariantCulture),
                SafeText(r.ImagePath), SafeText(r.ErrorMessage ?? "") };
            writer.Write(string.Join(",", cells.Select(Escape)));
            writer.Write("\r\n");
        }
    }

    // (수식 주입 방어) 값이 = + - @ 로 시작하면 Excel 이 수식으로 실행할 수 있다. 외부에서 온 파일명/오류 메시지가
    // 악의적으로 만들어졌을 수 있으므로 앞에 작은따옴표를 붙여 문자열로만 취급되게 한다. 탭/개행으로 시작하는 값도 같은 이유로 막는다.
    // Excel 등에서 외부 파일명/오류 메시지가 수식으로 실행되지 않도록 텍스트 필드만 보호한다.
    private static string SafeText(string value)
    {
        var trimmed = value.AsSpan().TrimStart();
        return trimmed.Length > 0 && "=+-@".Contains(trimmed[0]) || value.Length > 0 && "\t\r\n".Contains(value[0])
            ? "'" + value : value;
    }

    // CSV 이스케이프: 값 안의 큰따옴표(")는 두 개("")로 바꾸고 전체를 큰따옴표로 감싼다.
    private static string Escape(string value) => "\"" + value.Replace("\"", "\"\"") + "\"";
}
