namespace DailyConsolidation.Domain;

/// <summary>Projeção do saldo de um dia (por comerciante, conta e moeda). ClosingBalance = Opening + Credits - Debits.</summary>
public sealed record DailyBalance(
    DateOnly BusinessDate,
    decimal OpeningBalance,
    decimal TotalCredits,
    decimal TotalDebits,
    decimal ClosingBalance)
{
    public static DailyBalance Empty(DateOnly date, decimal opening) => new(date, opening, 0m, 0m, opening);
}

/// <summary>Totais agregados de movimentos de um dia (créditos inclui OPENING_BALANCE).</summary>
public sealed record DayTotals(DateOnly BusinessDate, decimal Credits, decimal Debits);

public static class MovementTypes
{
    public const string Credit = "CREDIT";
    public const string Debit = "DEBIT";
    public const string OpeningBalance = "OPENING_BALANCE";

    /// <summary>Abertura é entrada de saldo auditável (regra 12) e soma em créditos.</summary>
    public static bool IsInflow(string type) => type is Credit or OpeningBalance;
}

/// <summary>Cálculo determinístico e independente de ordem de chegada dos eventos (SPEC §11).</summary>
public static class DailyBalanceCalculator
{
    /// <summary>
    /// Recalcula os dias com movimento a partir de uma data, propagando o saldo final como abertura do dia seguinte.
    /// </summary>
    /// <param name="previousClosing">Saldo final do último dia anterior ao primeiro dia recalculado (0 se não houver).</param>
    /// <param name="days">Totais por dia (qualquer ordem).</param>
    public static IReadOnlyList<DailyBalance> Recalculate(decimal previousClosing, IEnumerable<DayTotals> days)
    {
        var result = new List<DailyBalance>();
        var running = previousClosing;
        foreach (var day in days.OrderBy(d => d.BusinessDate))
        {
            var closing = running + day.Credits - day.Debits;
            result.Add(new DailyBalance(day.BusinessDate, running, day.Credits, day.Debits, closing));
            running = closing;
        }
        return result;
    }

    /// <summary>
    /// Preenche todos os dias de [from, to]: dias sem movimento têm abertura = fechamento = saldo anterior (regra 14).
    /// </summary>
    public static IReadOnlyList<DailyBalance> FillPeriod(DateOnly from, DateOnly to, decimal closingBeforeFrom, IEnumerable<DailyBalance> materialized)
    {
        if (to < from) throw new ArgumentException("to deve ser >= from.", nameof(to));

        var byDate = materialized.Where(b => b.BusinessDate >= from && b.BusinessDate <= to)
            .ToDictionary(b => b.BusinessDate);
        var result = new List<DailyBalance>(to.DayNumber - from.DayNumber + 1);
        var running = closingBeforeFrom;

        for (var d = from; d <= to; d = d.AddDays(1))
        {
            if (byDate.TryGetValue(d, out var b))
            {
                result.Add(b);
                running = b.ClosingBalance;
            }
            else
            {
                result.Add(DailyBalance.Empty(d, running));
            }
        }
        return result;
    }
}
