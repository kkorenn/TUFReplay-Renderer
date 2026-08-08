#nullable enable
using System;

namespace TUFReplayRenderer.Infrastructure.Render
{
  public class RationalUtil
  {
    public static (int Numerator, int Denominator) Double2MaxPrecisionInt(double value, int maxDenominator = 100000000)
    {
      // 1. 예외 처리 및 부호 판별
      if (value == 0.0)
        return (0, 1);
      if (double.IsNaN(value) || double.IsInfinity(value))
        return (0, 1);

      var isNegative = value < 0;
      var sign = isNegative ? -1.0 : 1.0;
      var target = Math.Abs(value);
      // 2. 스턴-브로코 / 연속분수 경계값 초기화
      // 아래 두 행의 분수 (0/1, 1/0) 사이에서 시작하여 타겟에 가까워집니다.
      long m00 = 0,
        m01 = 1;
      long m10 = 1,
        m11 = 0;

      var x = target;

      // int 범위 오버플로우를 방지하면서 반복 처리
      while (true)
      {
        var ai = (long)Math.Floor(x);

        // 다음 단계의 분자, 분모 계산 (오버플로우 방지를 위해 long으로 임시 계산)
        var nextNumerator = ai * m10 + m00;
        var nextDenominator = ai * m11 + m01;

        // int 범위를 벗어나거나, 설정한 최대 분모를 초과하면 이전 단계의 최적값을 반환
        if (nextNumerator > int.MaxValue || nextDenominator > maxDenominator)
        {
          break;
        }

        // 행렬 업데이트
        m00 = m10;
        m01 = m11;
        m10 = nextNumerator;
        m11 = nextDenominator;

        // 오차가 거의 없으면 (정밀도 도달) 종료
        if (Math.Abs(x - ai) < 1e-15)
          break;

        // 다음 연속분수 항 준비
        x = 1.0 / (x - ai);
      }

      // 3. 최종 결정된 int 기반의 분자 분모
      var finalNumerator = (int)m10;
      var finalDenominator = (int)m11;

      // 퇴화 결과 방어: 분자/분모가 0이면 호출부(fps 계산 등)에서 0으로 나누기가 발생하므로
      // 반올림 정수 근사로 폴백합니다 (예: int.MaxValue 초과 입력 → (1, 0) 반환 방지).
      if (finalNumerator <= 0 || finalDenominator <= 0)
      {
        var approx = (int)Math.Min(Math.Max(Math.Round(target), 1), int.MaxValue);
        finalNumerator = approx;
        finalDenominator = 1;
      }

      // 부호 복원
      if (isNegative)
        finalNumerator = -finalNumerator;

      return (finalNumerator, finalDenominator);
    }
  }
}
