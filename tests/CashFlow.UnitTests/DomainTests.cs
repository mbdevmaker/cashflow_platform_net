using CashFlow.Domain;

namespace CashFlow.UnitTests;

public class DomainTests
{
    private static readonly Guid MerchantA = Guid.NewGuid();
    private static readonly Guid MerchantB = Guid.NewGuid();
    private static readonly DateTimeOffset Now = new(2026, 11, 17, 20, 0, 0, TimeSpan.Zero);

    private static CashAccount NewAccount(Guid? merchant = null) =>
        CashAccount.Open(Guid.NewGuid(), merchant ?? MerchantA, "Caixa principal", "BRL", "America/Sao_Paulo", Now);

    private static CashTransaction Register(CashAccount account, string type = "CREDIT", decimal amount = 500m, Guid? merchant = null, DateTimeOffset? at = null) =>
        CashTransaction.Register(Guid.NewGuid(), account, merchant ?? account.MerchantId, type, amount, "Venda",
            at ?? new DateTimeOffset(2026, 11, 17, 14, 30, 0, TimeSpan.FromHours(-3)), "key-1", "user", Now);

    [Fact]
    public void Credito_valido_e_registrado_com_data_operacional_e_sequencia()
    {
        var account = NewAccount();
        var tx = Register(account);

        Assert.Equal(CashTransactionType.Credit, tx.Type);
        Assert.Equal(500m, tx.Amount);
        Assert.Equal("BRL", tx.Currency);
        Assert.Equal(new DateOnly(2026, 11, 17), tx.BusinessDate);
        Assert.Equal(1, tx.AccountSequence);
        Assert.Equal(TimeSpan.Zero, tx.OccurredAt.Offset);
    }

    [Fact]
    public void Debito_valido_e_registrado()
    {
        var tx = Register(NewAccount(), "debit", 10.5m);
        Assert.Equal(CashTransactionType.Debit, tx.Type);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(0.00001)]
    public void Valor_zero_negativo_ou_com_mais_de_4_casas_e_rejeitado(decimal amount)
    {
        var ex = Assert.Throws<DomainException>(() => Register(NewAccount(), amount: amount));
        Assert.Equal(DomainErrorKind.Validation, ex.Kind);
    }

    [Fact]
    public void Tipo_invalido_e_rejeitado()
    {
        var ex = Assert.Throws<DomainException>(() => Register(NewAccount(), type: "OPENING_BALANCE"));
        Assert.Equal("type_invalid", ex.Code);
    }

    [Fact]
    public void Conta_de_outro_comerciante_e_rejeitada_como_nao_encontrada()
    {
        var accountOfA = NewAccount(MerchantA);
        var ex = Assert.Throws<DomainException>(() => Register(accountOfA, merchant: MerchantB));
        Assert.Equal(DomainErrorKind.NotFound, ex.Kind);
    }

    [Fact]
    public void Data_operacional_usa_fuso_da_conta_e_nao_UTC()
    {
        var account = NewAccount();
        // 23:30 em São Paulo (-03:00) = 02:30Z do dia seguinte; a data operacional deve ser a local.
        var tx = Register(account, at: new DateTimeOffset(2026, 11, 16, 23, 30, 0, TimeSpan.FromHours(-3)));
        Assert.Equal(new DateOnly(2026, 11, 16), tx.BusinessDate);

        // 01:00Z do dia 17 = 22:00 do dia 16 em São Paulo.
        var tx2 = Register(account, at: new DateTimeOffset(2026, 11, 17, 1, 0, 0, TimeSpan.Zero));
        Assert.Equal(new DateOnly(2026, 11, 16), tx2.BusinessDate);
    }

    [Fact]
    public void Estorno_integral_cria_lancamento_inverso_ligado_ao_original()
    {
        var account = NewAccount();
        var original = Register(account, "CREDIT", 120m);
        var reversal = original.Reverse(Guid.NewGuid(), account, "erro", Now, "rev-1", "user", Now);

        Assert.Equal(CashTransactionType.Debit, reversal.Type);
        Assert.Equal(original.Amount, reversal.Amount);
        Assert.Equal(original.Id, reversal.ReversesTransactionId);
        Assert.Equal(2, reversal.AccountSequence);
        Assert.Equal(CashTransactionType.Credit, original.Type); // original intacto
    }

    [Fact]
    public void Estorno_de_estorno_e_rejeitado()
    {
        var account = NewAccount();
        var reversal = Register(account).Reverse(Guid.NewGuid(), account, null, Now, "rev-1", null, Now);
        var ex = Assert.Throws<DomainException>(() => reversal.Reverse(Guid.NewGuid(), account, null, Now, "rev-2", null, Now));
        Assert.Equal("reversal_of_reversal", ex.Code);
    }

    [Fact]
    public void Saldo_de_abertura_e_lancamento_auditavel_estornavel_como_debito()
    {
        var account = NewAccount();
        var opening = CashTransaction.OpeningBalance(Guid.NewGuid(), account, 1000m, Now, null, Now);
        Assert.Equal(CashTransactionType.OpeningBalance, opening.Type);
        Assert.Equal(CashTransactionType.Debit, opening.Reverse(Guid.NewGuid(), account, null, Now, "r", null, Now).Type);
    }

    [Theory]
    [InlineData("Brasilia")]
    [InlineData("America/Nowhere")]
    [InlineData("")]
    public void Fuso_horario_invalido_e_rejeitado(string tz)
    {
        Assert.Throws<DomainException>(() => CashAccount.Open(Guid.NewGuid(), MerchantA, "Caixa", "BRL", tz, Now));
    }

    [Theory]
    [InlineData("USD")]
    [InlineData("R$")]
    public void Moeda_nao_suportada_e_rejeitada(string currency)
    {
        Assert.Throws<DomainException>(() => CashAccount.Open(Guid.NewGuid(), MerchantA, "Caixa", currency, "America/Sao_Paulo", Now));
    }

    [Fact]
    public void OccurredAt_no_futuro_e_rejeitado()
    {
        Assert.Throws<DomainException>(() => Register(NewAccount(), at: Now.AddHours(1)));
    }
}
