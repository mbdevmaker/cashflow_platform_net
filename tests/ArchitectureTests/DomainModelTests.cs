using System.Reflection;
using CashFlow.Domain;
using DailyConsolidation.Domain;

namespace ArchitectureTests;

/// <summary>Regras de modelagem DDD: estado só muda por métodos de domínio; objetos de valor são imutáveis.</summary>
public sealed class DomainModelTests
{
    private static readonly Type[] Entities = [typeof(CashAccount), typeof(CashTransaction), typeof(Merchant)];

    [Fact]
    public void Entidades_do_CashFlow_nao_tem_setters_publicos()
    {
        var violations = Entities
            .SelectMany(t => t.GetProperties(BindingFlags.Instance | BindingFlags.Public))
            .Where(p => p.SetMethod is { IsPublic: true })
            .Select(p => $"{p.DeclaringType!.Name}.{p.Name}")
            .ToList();

        Assert.True(violations.Count == 0, "Setters públicos: " + string.Join(", ", violations));
    }

    [Fact]
    public void Entidades_do_CashFlow_nao_tem_construtores_publicos_sem_regra()
    {
        // CashAccount e CashTransaction só nascem pelas fábricas (Open, Register, OpeningBalance, Reverse).
        Assert.Empty(typeof(CashAccount).GetConstructors(BindingFlags.Instance | BindingFlags.Public));
        Assert.Empty(typeof(CashTransaction).GetConstructors(BindingFlags.Instance | BindingFlags.Public));
    }

    [Fact]
    public void Entidades_e_servicos_de_dominio_sao_sealed_ou_estaticos()
    {
        var open = typeof(CashAccount).Assembly.GetTypes()
            .Concat(typeof(DailyBalance).Assembly.GetTypes())
            .Where(t => t is { IsClass: true, IsPublic: true, IsSealed: false, IsAbstract: false })
            .Select(t => t.FullName)
            .ToList();

        Assert.True(open.Count == 0, "Tipos de domínio não sealed: " + string.Join(", ", open));
    }

    [Fact]
    public void Objetos_de_valor_da_consolidacao_sao_imutaveis()
    {
        var mutable = new[] { typeof(DailyBalance), typeof(DayTotals), typeof(AccountWatermark), typeof(ConsistencyInfo) }
            .SelectMany(t => t.GetProperties(BindingFlags.Instance | BindingFlags.Public))
            .Where(p => p.SetMethod is { } set && !set.ReturnParameter.GetRequiredCustomModifiers()
                .Contains(typeof(System.Runtime.CompilerServices.IsExternalInit)))
            .Select(p => $"{p.DeclaringType!.Name}.{p.Name}")
            .ToList();

        Assert.True(mutable.Count == 0, "Propriedades mutáveis: " + string.Join(", ", mutable));
    }
}
